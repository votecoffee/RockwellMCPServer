using System.Text.Json;
using Rockwell.Engineering;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class EngineeringCliDeadlineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellCliDeadline", Guid.NewGuid().ToString("N"));
    public EngineeringCliDeadlineTests() => Directory.CreateDirectory(root);
    private string Request(string contents)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void Native_workspace_gets_the_longer_budget_and_pins_the_exact_authorization()
    {
        var path = Request("{\"workspace\":\"synthetic\",\"allowNative\":true,\"operation\":\"studio_sdk_capabilities\"}");
        var request = EngineeringCli.Parse(["run", "--request", path]);
        Assert.Equal(300, request.TimeoutSeconds);
        Assert.Equal(270, WorkspaceRun.NativeWorkerTimeout(request.TimeoutSeconds));
        var pinned = request.PinnedRequest!;
        File.WriteAllText(path, "{\"allowNative\":false}");
        Assert.True(WorkspaceRun.AllowsNative(pinned));
        Assert.NotEqual(pinned, File.ReadAllBytes(path));
        Assert.Equal(pinned, EngineeringCli.ReadPinnedRequest(new StringReader(Convert.ToBase64String(pinned))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("incomplete!")]
    public void Owned_request_transport_rejects_empty_or_corrupt_input(string input) =>
        Assert.Equal("invalid_workspace_request", Assert.Throws<OperationException>(() =>
            EngineeringCli.ReadPinnedRequest(new StringReader(input))).Code);

    [Fact]
    public void Owned_request_transport_is_bounded_before_decoding() =>
        Assert.Equal("invalid_workspace_request", Assert.Throws<OperationException>(() =>
            EngineeringCli.ReadPinnedRequest(new StringReader(new string('A', 3 * 1024 * 1024)))).Code);

    [Fact]
    public void Saved_file_defaults_and_explicit_native_deadlines_are_distinct()
    {
        var read = Request("{\"operation\":\"studio_sdk_capabilities\"}");
        Assert.Equal(30, EngineeringCli.Parse(["run", "--request", read]).TimeoutSeconds);
        Assert.Equal(12, EngineeringCli.Parse(["run", "--request", read, "--timeout-seconds", "12"]).TimeoutSeconds);
        var native = Request("{\"allowNative\":true,\"operation\":\"studio_sdk_capabilities\"}");
        var explicitDeadline = EngineeringCli.Parse(["run", "--request", native, "--timeout-seconds", "90"]);
        Assert.Equal(60, WorkspaceRun.NativeWorkerTimeout(explicitDeadline.TimeoutSeconds));
        Assert.Equal("native_deadline_too_short", Assert.Throws<OperationException>(() =>
            EngineeringCli.Parse(["run", "--request", native, "--timeout-seconds", "30"])).Code);
        Assert.Equal("invalid_arguments", Assert.Throws<OperationException>(() =>
            EngineeringCli.Parse(["run", "--request", native, "--timeout-seconds", "301"])).Code);
        Assert.Equal(300, EngineeringCli.Parse(["sdk-read-export", "--request", read]).TimeoutSeconds);
    }

    [Fact]
    public void Deadline_selection_cannot_hide_duplicate_or_mistyped_native_authorization()
    {
        Assert.Equal("invalid_json", Assert.Throws<OperationException>(() => EngineeringCli.Parse(["run", "--request",
            Request("{\"allowNative\":false,\"AllowNative\":true}")])).Code);
        Assert.Throws<JsonException>(() => EngineeringCli.Parse(["run", "--request", Request("{\"allowNative\":\"true\"}")]));
    }

    private static SdkJob Job(string status) => new(new string('b', 32), status, DateTimeOffset.UnixEpoch,
        ["OpenLogixProjectAsync", "SaveAsAsync"], new string('a', 64), null, false, [], [], []);

    [Theory]
    [InlineData("succeeded")]
    [InlineData("indeterminate")]
    [InlineData("failed")]
    public async Task Synthetic_SDK_wait_retains_identity_and_terminal_evidence_without_redispatch(string terminal)
    {
        var count = 0;
        var queued = Job("queued");
        var result = await SdkJobWait.FinishAsync(queued, root, Path.Combine(root, "state"), id =>
        {
            Assert.Equal(queued.Id, id);
            return ++count < 3 ? queued with { Status = "running" } : queued with { Status = terminal };
        }, CancellationToken.None);
        Assert.Equal(terminal, result.Status);
        Assert.Equal(3, count);
        var reference = Assert.Single(SdkJobWait.References(root));
        using var document = JsonDocument.Parse(File.ReadAllBytes(reference));
        Assert.Equal(queued.Id, document.RootElement.GetProperty("id").GetString());
        Assert.Equal(queued.SourceSha256, document.RootElement.GetProperty("sourceSha256").GetString());
        Assert.Equal(Path.Combine(root, "state", "sdk-jobs", queued.Id, "job.json"), document.RootElement.GetProperty("record").GetString());
        Assert.True(File.Exists(Path.Combine(root, "sdk-" + queued.Id + "-terminal.json")));
    }

    [Fact]
    public async Task Interrupted_wait_keeps_the_original_job_reference_without_claiming_native_completion()
    {
        var queued = Job("queued");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SdkJobWait.FinishAsync(queued, root,
            Path.Combine(root, "state"), _ => throw new Exception("Cancelled observation must not dispatch or read a replacement"), cancelled.Token));
        var reference = Assert.Single(SdkJobWait.References(root));
        Assert.False(File.Exists(Path.Combine(root, "sdk-" + queued.Id + "-terminal.json")));
        var before = File.ReadAllBytes(reference);
        Assert.Single(SdkJobWait.References(root));
        Assert.Equal(before, File.ReadAllBytes(reference));
    }

    public void Dispose() => Directory.Delete(root, true);
}
