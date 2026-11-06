using System.Text;
using System.Text.Json;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class BoundL5kExportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellBoundL5k", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public BoundL5kExportTests()
    {
        Directory.CreateDirectory(root);
        service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [root] });
    }
    private static string L5x(int value = 7, int major = 35) =>
        $"<RSLogix5000Content SoftwareRevision=\"{major}.00\" TargetType=\"Controller\" TargetName=\"Synthetic\" ContainsContext=\"false\"><Controller Name=\"Synthetic\" MajorRev=\"{major}\">" +
        "<Tags><Tag Name=\"Counter\" TagType=\"Base\" DataType=\"DINT\"><Description><![CDATA[<Alarm[10]> First line \r\nSecond $$L line <@ver 1.400>]]></Description>" +
        $"<Data Format=\"L5K\"><![CDATA[{value}]]></Data></Tag></Tags></Controller></RSLogix5000Content>";
    private const string L5k = "IE_VER := 2.26;\r\nCONTROLLER Synthetic (Major := 35)\r\nTAG\r\nCounter : DINT (Description := \"<Alarm[10]> First line $NSecond $$$$L line <@ver 1.400>\") := 7;\r\nEND_TAG\r\nEND_CONTROLLER\r\n";

    private (SdkJob Job, BoundL5kReviewRequest Request) Retain(string? sourceXml = null, string? nativeXml = null, string l5k = L5k, bool acd = false)
    {
        var format = acd ? "acd" : "l5x";
        var sourcePath = Path.Combine(root, Guid.NewGuid().ToString("N") + "." + format);
        var source = Encoding.UTF8.GetBytes(acd ? "synthetic ACD preimage, never passed to a vendor runtime" : sourceXml ?? L5x());
        File.WriteAllBytes(sourcePath, source);
        var id = Guid.NewGuid().ToString("N");
        Artifact Artifact(string extension, byte[] bytes)
        {
            var artifact = new Artifact(Guid.NewGuid().ToString("N"), id, extension, ProjectService.Hash(bytes), bytes.Length, "Synthetic job fixture");
            var dir = Path.Combine(root, "state", "artifacts", artifact.Id);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "project." + extension), bytes);
            File.WriteAllBytes(Path.Combine(dir, "artifact.json"), JsonSerializer.SerializeToUtf8Bytes(artifact, ProjectService.Json));
            return artifact;
        }
        var readback = Artifact("l5x", Encoding.UTF8.GetBytes(nativeXml ?? L5x()));
        var native = Artifact("l5k", Encoding.UTF8.GetBytes(l5k));
        var work = Path.Combine(root, "state", "sdk-jobs", id, "work");
        Directory.CreateDirectory(work);
        File.WriteAllBytes(Path.Combine(work, "input." + format), source);
        var methods = BoundL5kExport.Methods.ToArray();
        var job = new SdkJob(id, "succeeded", DateTimeOffset.UnixEpoch, methods, ProjectService.Hash(source), null, false,
            methods.Select((m, i) => new SdkStepResult(i, m, "succeeded", null)).ToArray(), [], [readback, native],
            BoundL5kSource: new(sourcePath, format, 35), SdkAssembly: SdkCatalog.Surface.Assembly);
        SaveJob(job);
        return (job, new(id, acd ? readback.Sha256 : job.SourceSha256!));
    }

    private void SaveJob(SdkJob job) => File.WriteAllBytes(Path.Combine(root, "state", "sdk-jobs", job.Id, "job.json"),
        JsonSerializer.SerializeToUtf8Bytes(job, ProjectService.Json));

    [Fact]
    public async Task Plan_checks_source_revision_and_emits_only_private_offline_SDK_calls()
    {
        var input = Retain();
        var source = input.Job.BoundL5kSource!;
        var plan = await service.PlanBoundL5kAsync(new(source.Path, input.Job.SourceSha256!, 35));
        Assert.Equal(new[] { "OpenLogixProjectAsync", "SaveAsAsync", "SaveAsAsync" }, plan.SdkRequest.Calls.Select(c => c.Method));
        Assert.True(plan.SdkRequest.Calls[1].Arguments["detailedL5x"].GetBoolean());
        Assert.Equal("native-export.l5k", plan.SdkRequest.Calls[2].Arguments["savePath"].GetString());
        Assert.Equal("bound_l5k_revision", (await Assert.ThrowsAsync<OperationException>(() => service.PlanBoundL5kAsync(new(source.Path, plan.SourceSha256, 36)))).Code);
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.PlanBoundL5kAsync(new(source.Path, new string('0', 64), 35)))).Code);
        Assert.Equal("bound_l5k_policy", (await Assert.ThrowsAsync<OperationException>(() => service.StartSdkJobAsync(plan.SdkRequest with { ControllerTargetId = "denied" }))).Code);
        Assert.Equal("bound_l5k_policy", (await Assert.ThrowsAsync<OperationException>(() => service.StartSdkJobAsync(plan.SdkRequest with { Calls = [new("SaveAsync", [])] }))).Code);
        Assert.Equal("native_unavailable", (await Assert.ThrowsAsync<OperationException>(() => service.StartSdkJobAsync(plan.SdkRequest))).Code);
        var acd = Retain(acd: true).Job;
        var acdPlan = await service.PlanBoundL5kAsync(new(acd.BoundL5kSource!.Path, acd.SourceSha256!, 35));
        Assert.Equal("acd", acdPlan.Source.Format);
    }

    [Fact]
    public async Task Bridge_requires_the_private_remapped_pair_and_offline_profile()
    {
        var job = Retain().Job;
        var plan = await service.PlanBoundL5kAsync(new(job.BoundL5kSource!.Path, job.SourceSha256!, 35));
        var work = Path.Combine(root, "work");
        var source = Path.Combine(work, "input.l5x");
        var calls = plan.SdkRequest.Calls;
        calls[0].Arguments["projectFilePath"] = JsonSerializer.SerializeToElement(source);
        foreach (var call in calls.Skip(1))
            call.Arguments["savePath"] = JsonSerializer.SerializeToElement(Path.Combine(work, call.Arguments["savePath"].GetString()!));
        var request = new SdkBridgeRequest(work, source, null, calls, NativeExecutionPolicy.LocalProject, true);
        BoundL5kExport.ValidateWorkerRequest(request);
        Assert.Throws<OperationException>(() => BoundL5kExport.ValidateWorkerRequest(request with { CommunicationsPath = "never" }));
        Assert.Throws<OperationException>(() => BoundL5kExport.ValidateWorkerRequest(request with { ExecutionProfile = NativeExecutionPolicy.Standard }));
        calls[2].Arguments["savePath"] = JsonSerializer.SerializeToElement(Path.Combine(root, "unreviewed.l5k"));
        Assert.Throws<OperationException>(() => BoundL5kExport.ValidateWorkerRequest(request));
    }

    [Fact]
    public async Task Approved_pair_publishes_the_GM_pointers_and_exact_native_bytes()
    {
        var original = L5x().Replace("TargetType=", "ExportDate=\"prior\" TargetType=");
        var native = L5x().Replace("TargetType=", "ExportDate=\"later\" TargetType=");
        var (job, request) = Retain(original, native);
        var preview = await service.PreviewBoundL5kAsync(request);
        Assert.True(preview.CanBind, string.Join(", ", preview.Issues.Select(i => i.Detail)));
        Assert.True(preview.OriginalL5xEquivalent);
        Assert.NotEqual(preview.SourceSha256, preview.NativeL5x.Sha256);
        var record = await service.RecordBoundL5kAsync(new(request, preview.ReviewSha256));
        var binding = Assert.IsType<GmL5kDerivedInput>(record.DerivedInput);
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(binding.Binding.Path));
        Assert.Equal(job.SourceSha256, receipt.RootElement.GetProperty(binding.Binding.SourcePointer[1..]).GetString());
        Assert.Equal(binding.Sha256, receipt.RootElement.GetProperty(binding.Binding.OutputPointer[1..]).GetString());
        Assert.Equal(binding.Binding.Sha256, ProjectService.Hash(File.ReadAllBytes(binding.Binding.Path)));
        var exported = await service.SaveArtifactAsync(record.NativeL5k.Id, Path.Combine(root, "approved.l5k"));
        Assert.Equal(Encoding.UTF8.GetBytes(L5k), File.ReadAllBytes(exported.Path));
        Assert.Equal(Encoding.UTF8.GetBytes(original), File.ReadAllBytes(job.BoundL5kSource!.Path));
        await Assert.ThrowsAsync<OperationException>(() => service.RecordBoundL5kAsync(new(request, new string('0', 64))));
    }

    [Fact]
    public async Task Material_drift_withholds_original_binding_but_allows_review_of_the_exact_new_L5X()
    {
        var (job, request) = Retain(nativeXml: L5x(8), l5k: L5k.Replace(":= 7", ":= 8"));
        var preview = await service.PreviewBoundL5kAsync(request);
        Assert.False(preview.CanBind);
        Assert.False(preview.OriginalL5xEquivalent);
        Assert.Contains(preview.Differences, d => d.Kind == "Tag");
        var evidence = await service.RecordBoundL5kAsync(new(request, preview.ReviewSha256));
        Assert.Null(evidence.DerivedInput);
        using var document = JsonDocument.Parse(File.ReadAllBytes(service.Download(evidence.Receipt.Id).Path));
        Assert.False(document.RootElement.TryGetProperty("selected_l5x_sha256", out _));
        var native = await service.PreviewBoundL5kAsync(request with { SelectedL5xSha256 = job.Artifacts[0].Sha256 });
        Assert.True(native.CanBind);
        Assert.False(native.OriginalL5xEquivalent);
        Assert.Equal("native-readback-l5x", native.Selection);
    }

    [Theory]
    [InlineData("$NSecond", "$LSecond", "bound_l5k_message_escape")]
    [InlineData("Second", "Changed", "bound_l5k_message_difference")]
    [InlineData("1.400", "1.500", "bound_l5k_version_markers")]
    [InlineData("\r\n", "\n", "bound_l5k_line_endings")]
    public async Task Annotation_and_format_drift_is_reported_without_rewriting(string from, string to, string code)
    {
        var modified = L5k.Replace(from, to);
        var (_, request) = Retain(l5k: modified);
        var review = await service.PreviewBoundL5kAsync(request);
        Assert.False(review.CanBind);
        Assert.Contains(review.Issues, i => i.Code == code);
        Assert.Equal(Encoding.UTF8.GetBytes(modified), File.ReadAllBytes(service.Download(review.NativeL5k.Id).Path));
    }

    [Fact]
    public async Task Acd_binding_targets_native_L5X_and_rejects_unproven_or_changed_outputs()
    {
        var (job, request) = Retain(acd: true);
        var preview = await service.PreviewBoundL5kAsync(request);
        Assert.True(preview.CanBind);
        Assert.Null(preview.OriginalL5xEquivalent);
        Assert.NotEqual(preview.SourceSha256, request.SelectedL5xSha256);
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() =>
            service.PreviewBoundL5kAsync(request with { SelectedL5xSha256 = job.SourceSha256! }))).Code);
        SaveJob(job with { Status = "indeterminate" });
        Assert.Equal("bound_l5k_provenance", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewBoundL5kAsync(request))).Code);
        SaveJob(job with { ControllerEffectsPossible = true });
        Assert.Equal("bound_l5k_provenance", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewBoundL5kAsync(request))).Code);
        SaveJob(job);
        File.AppendAllText(service.Download(preview.NativeL5k.Id).Path, "changed");
        Assert.Equal("artifact_tampered", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewBoundL5kAsync(request))).Code);
    }

    public void Dispose() { service.Dispose(); Directory.Delete(root, recursive: true); }
}
