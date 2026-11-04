using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Studio5000.Client;
using Studio5000.Core;
using Xunit;
using Xunit.Abstractions;

namespace Studio5000.Tests;

public sealed class SdkJobHistoryTests(ITestOutputHelper output)
{
    private static string Job(int index, int payloadBytes = 0) => JsonSerializer.Serialize(new
    {
        id = "job-" + index, status = index == 0 ? "indeterminate" : "succeeded",
        createdAt = "2026-01-01T00:00:00+00:00", methods = new[] { "BuildAsync" },
        sourceSha256 = new string('a', 64), controllerTargetId = "fixture", controllerEffectsPossible = index == 0,
        steps = new[] { new { value = new { id = "nested", status = "nested", diagnostic = new string('x', payloadBytes) } } },
        events = new[] { new { message = "\u03bb\ud83d\ude80\"\\\n" } }, artifacts = Array.Empty<object>(),
        error = index == 0 ? "Inspect before retrying" : null
    });
    private static byte[] History(int count, int payloadBytes = 0) => Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Range(0, count).Select(i => Job(i, payloadBytes))) + "]");

    [Fact]
    public async Task Small_page_does_not_allocate_the_full_response_budget()
    {
        var bytes = History(6);
        using (var warm = new MemoryStream(bytes, false)) await SdkJobHistory.ReadAsync(warm);
        using var source = new MemoryStream(bytes, false);
        var thread = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var task = SdkJobHistory.ReadAsync(source);
        var synchronous = task.IsCompletedSuccessfully;
        var page = await task;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"Small-page allocated bytes: {allocated}; input bytes: {bytes.Length}");
        Assert.True(synchronous); Assert.Equal(thread, Environment.CurrentManagedThreadId);
        Assert.Equal(5, page.Items.Length); Assert.True(page.HasMore);
        Assert.InRange(allocated, 1, 256 * 1024);
    }

    [Theory]
    [InlineData(65537)] [InlineData(196613)]
    public async Task Large_split_token_can_finish_at_the_exact_cap_and_one_byte_less_still_refuses(int payloadBytes)
    {
        var bytes = History(1, payloadBytes);
        using var source = new ObservedStream(bytes, 997);
        var page = await SdkJobHistory.ReadAsync(source, maximumResponseBytes: bytes.Length);
        Assert.Equal("job-0", Assert.Single(page.Items).Id);
        Assert.Equal(bytes.Length, page.ResponseBytesRead); Assert.False(page.HasMore);
        using var shortBudget = new ObservedStream(bytes, 997);
        var error = await Assert.ThrowsAsync<ServiceErrorException>(() => SdkJobHistory.ReadAsync(shortBudget, maximumResponseBytes: bytes.Length - 1));
        Assert.Equal("sdk_job_history_limit", error.Error.Code);
        Assert.Equal(bytes.Length - 1, shortBudget.BytesRead);
    }

    [Fact]
    public async Task Malformed_large_token_keeps_the_same_typed_error_after_buffer_growth()
    {
        using var source = new ObservedStream(Encoding.UTF8.GetBytes("[{\"ignored\":\"" + new string('x', 128 * 1024)));
        var error = await Assert.ThrowsAsync<ServiceErrorException>(() => SdkJobHistory.ReadAsync(source));
        Assert.Equal("sdk_job_history_invalid", error.Error.Code);
        Assert.True(source.BytesRead > 65536);
    }

    [Fact]
    public async Task Cancellation_during_buffer_growth_stops_before_another_read()
    {
        using var cancel = new CancellationTokenSource();
        using var source = new ObservedStream(History(1, 256 * 1024), afterRead: count => { if (count > 128 * 1024) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SdkJobHistory.ReadAsync(source, ct: cancel.Token));
        Assert.InRange(source.BytesRead, 128 * 1024 + 1, 192 * 1024);
        Assert.True(source.BytesRead < source.Length);
    }

    [Fact]
    public async Task Oversized_legacy_history_yields_a_small_summary_page_without_reading_the_full_body()
    {
        using var source = new ObservedStream(History(100, 128 * 1024));
        Assert.True(source.Length > SdkJobHistory.MaximumBytes);
        var page = await SdkJobHistory.ReadAsync(source);
        Assert.Equal(5, page.Items.Length); Assert.Equal("job-0", page.Items[0].Id);
        Assert.Equal("indeterminate", page.Items[0].Status); Assert.True(page.Items[0].ControllerEffectsPossible);
        Assert.True(page.Items[0].HasError); Assert.False(page.Items[1].HasError);
        Assert.Equal(["BuildAsync"], page.Items[0].Methods); Assert.True(page.HasMore); Assert.Equal(5, page.NextOffset);
        Assert.Equal(100, page.SourceWindowLimit); Assert.True(page.SourceMayHaveMore); Assert.True(page.SummariesOnly);
        Assert.Equal(source.BytesRead, page.ResponseBytesRead); Assert.InRange(source.BytesRead, 1, 1024 * 1024);
        Assert.True(source.MaximumRead <= 65536);
        Assert.DoesNotContain("diagnostic", JsonSerializer.Serialize(page, ProjectService.Json));
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(page).Length, 1, 8192);
    }

    [Theory]
    [InlineData(1)] [InlineData(7)] [InlineData(65536)]
    public async Task Split_tokens_and_UTF8_preserve_metadata_and_offset(int chunk)
    {
        var json = Encoding.UTF8.GetString(History(4)).Replace("fixture", "\u03bb\ud83d\ude80\\\"");
        using var source = new ObservedStream(Encoding.UTF8.GetBytes(json), chunk);
        var page = await SdkJobHistory.ReadAsync(source, 1, 2);
        Assert.Equal(["job-1", "job-2"], page.Items.Select(j => j.Id));
        Assert.Equal("\u03bb\ud83d\ude80\"", page.Items[0].ControllerTargetId);
        Assert.False(page.Items[0].ControllerEffectsPossible); Assert.Equal(3, page.NextOffset);
    }

    [Theory]
    [InlineData(0, 0, 5)] [InlineData(4, 3, 20)] [InlineData(4, 50, 5)] [InlineData(100, 99, 20)]
    public async Task End_of_window_is_explicit_and_does_not_claim_all_history_is_known(int count, int offset, int limit)
    {
        using var source = new ObservedStream(History(count));
        var page = await SdkJobHistory.ReadAsync(source, offset, limit);
        Assert.False(page.HasMore); Assert.Null(page.NextOffset);
        Assert.Equal(Math.Max(0, count - offset), page.Items.Length);
        Assert.Equal(count == 100, page.SourceMayHaveMore);
    }

    [Fact]
    public async Task Missing_effect_or_error_fields_remain_unknown_instead_of_becoming_false()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("[{\"id\":\"old\",\"status\":\"indeterminate\",\"createdAt\":\"2026-01-01T00:00:00Z\",\"methods\":[]}]"));
        var job = Assert.Single((await SdkJobHistory.ReadAsync(source)).Items);
        Assert.Null(job.ControllerEffectsPossible); Assert.Null(job.HasError);
    }

    [Fact]
    public async Task Large_first_job_or_deep_offset_stops_at_the_same_byte_budget_with_a_typed_error()
    {
        foreach (var (body, offset) in new[] { (History(1, 2 * 1024 * 1024), 0), (History(30, 128 * 1024), 20) })
        {
            using var source = new ObservedStream(body);
            var error = await Assert.ThrowsAsync<ServiceErrorException>(() => SdkJobHistory.ReadAsync(source, offset, 5, 1024 * 1024));
            Assert.Equal("sdk_job_history_limit", error.Error.Code); Assert.Equal(413, error.Error.HttpStatus);
            Assert.Equal(1024 * 1024, source.BytesRead);
        }
    }

    [Theory]
    [InlineData(-1, 5)] [InlineData(100, 5)] [InlineData(0, 0)] [InlineData(0, 21)]
    public async Task Invalid_page_is_refused_before_any_stream_read(int offset, int limit)
    {
        using var source = new ObservedStream(History(1));
        var error = await Assert.ThrowsAsync<ServiceErrorException>(() => SdkJobHistory.ReadAsync(source, offset, limit));
        Assert.Equal("sdk_job_history_page_invalid", error.Error.Code); Assert.Equal(0, source.BytesRead);
    }

    [Theory]
    [InlineData("")] [InlineData("{}")] [InlineData("[null]")] [InlineData("[{}]")]
    [InlineData("[{")] [InlineData("[] false")]
    [InlineData("[{\"id\":\"x\",\"id\":\"y\"}]")]
    public async Task Malformed_or_incomplete_prefix_is_reported_without_exposing_raw_diagnostics(string json)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var error = await Assert.ThrowsAsync<ServiceErrorException>(() => SdkJobHistory.ReadAsync(source));
        Assert.Equal("sdk_job_history_invalid", error.Error.Code);
    }

    [Fact]
    public async Task Cancellation_propagates_and_the_response_budget_cannot_be_raised()
    {
        using var source = new ObservedStream(History(1)); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SdkJobHistory.ReadAsync(source, ct: cancel.Token));
        Assert.Equal(0, source.BytesRead);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SdkJobHistory.ReadAsync(source, maximumResponseBytes: SdkJobHistory.MaximumBytes + 1));
    }

    [Fact]
    public async Task A_complete_array_at_the_exact_byte_limit_succeeds_and_invalid_client_pages_never_bootstrap()
    {
        var bytes = History(1); using var source = new ObservedStream(bytes);
        var page = await SdkJobHistory.ReadAsync(source, maximumResponseBytes: bytes.Length);
        Assert.Single(page.Items); Assert.Equal(bytes.Length, page.ResponseBytesRead);
        var ready = 0;
        using var client = new ServiceClient("unused", ensureReady: _ => { ready++; throw new InvalidOperationException("Unexpected bootstrap"); });
        var error = await Assert.ThrowsAsync<ServiceErrorException>(() => client.ListSdkJobsAsync(-1));
        Assert.Equal("sdk_job_history_page_invalid", error.Error.Code); Assert.Equal(0, ready);
    }

    [Theory]
    [InlineData("\"controllerEffectsPossible\":true", "\"controllerEffectsPossible\":\"unknown\"")]
    [InlineData("\"methods\":[\"BuildAsync\"]", "\"methods\":[{}]")]
    [InlineData("\"status\":\"indeterminate\"", "\"status\":false")]
    public async Task Wrong_summary_field_types_are_not_reported_as_safe_defaults(string before, string after)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("[" + Job(0).Replace(before, after) + "]"));
        var error = await Assert.ThrowsAsync<ServiceErrorException>(() => SdkJobHistory.ReadAsync(source));
        Assert.Equal("sdk_job_history_invalid", error.Error.Code);
    }

    [Fact]
    public async Task HTTP_path_uses_legacy_endpoint_without_buffering_oversized_content_and_exact_job_read_is_unchanged()
    {
        var body = History(100, 128 * 1024);
        await using var fixture = new HttpFixture(body, Encoding.UTF8.GetBytes(Job(0, 1024)));
        using var client = new ServiceClient(fixture.Root, maximumResponseBytes: SdkJobHistory.MaximumBytes);
        var page = await client.ListSdkJobsAsync(); Assert.Equal(5, page.Items.Length);
        var detail = await client.GetAsync<JsonElement>("/api/sdk/jobs/job-0");
        Assert.Equal(1024, detail.GetProperty("steps")[0].GetProperty("value").GetProperty("diagnostic").GetString()!.Length);
        await fixture.Completed;
        Assert.Equal(["/api/sdk/jobs", "/api/sdk/jobs/job-0"], fixture.Paths);
    }

    [Fact]
    public async Task Client_limit_errors_use_the_existing_MCP_error_translator_and_do_not_dispatch_a_retry()
    {
        await using var fixture = new HttpFixture(History(1, 2 * 1024 * 1024));
        ServiceError? translated = null;
        using var client = new ServiceClient(fixture.Root, error => { translated = error.Error; return new InvalidOperationException(error.Error.Code); }, maximumResponseBytes: 1024 * 1024);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListSdkJobsAsync());
        Assert.Equal("sdk_job_history_limit", failure.Message); Assert.Equal("sdk_job_history_limit", translated?.Code);
        await fixture.Completed; Assert.Single(fixture.Paths);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HTTP_failures_keep_bounded_domain_envelopes_and_do_not_expose_oversized_bodies(bool oversized)
    {
        var text = oversized ? new string('s', 32768) : "{\"code\":\"fixture_failure\",\"message\":\"Useful bounded message\"}";
        await using var fixture = new HttpFixture(HttpStatusCode.BadRequest, Encoding.UTF8.GetBytes(text));
        using var client = new ServiceClient(fixture.Root);
        var error = await Assert.ThrowsAsync<ServiceErrorException>(() => client.ListSdkJobsAsync());
        Assert.Equal(oversized ? "service_http_error" : "fixture_failure", error.Error.Code);
        Assert.Equal(400, error.Error.HttpStatus); Assert.DoesNotContain("ssss", error.Message);
        await fixture.Completed; Assert.Single(fixture.Paths);
    }

    private sealed class ObservedStream(byte[] bytes, int chunk = 65536, Action<int>? afterRead = null) : MemoryStream(bytes, false)
    {
        public int BytesRead { get; private set; }
        public int MaximumRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumRead = Math.Max(MaximumRead, buffer.Length);
            var read = await base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
            BytesRead += read; afterRead?.Invoke(BytesRead); return read;
        }
    }

    // Synthetic loopback HTTP only; no installed service, SDK, journals or registry.
    private sealed class HttpFixture : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        private readonly HttpStatusCode status;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rockwell-history-fixture-" + Guid.NewGuid().ToString("N"));
        public List<string> Paths { get; } = [];
        public Task Completed { get; }
        public HttpFixture(params byte[][] responses) : this(HttpStatusCode.OK, responses) { }
        public HttpFixture(HttpStatusCode status, params byte[][] responses)
        {
            this.status = status;
            Directory.CreateDirectory(Path.Combine(Root, ".local"));
            var token = Path.Combine(Root, "token"); File.WriteAllText(token, "fixture-token"); listener.Start();
            File.WriteAllText(Path.Combine(Root, ".local", "connection.json"), JsonSerializer.Serialize(new
            { url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port, tokenFile = token }));
            Completed = Serve(responses);
        }
        private async Task Serve(byte[][] responses)
        {
            foreach (var body in responses)
            {
                using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                Paths.Add((await reader.ReadLineAsync(stop.Token))!.Split(' ')[1]);
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                try
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(int)status} {status}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                    for (var i = 0; i < body.Length; i += 65536)
                        await stream.WriteAsync(body.AsMemory(i, Math.Min(65536, body.Length - i)), stop.Token);
                }
                catch (IOException) { } // The bounded client deliberately closes an unfinished read-only body.
            }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop();
            try { await Completed; } catch (OperationCanceledException) { } catch (SocketException) { }
            stop.Dispose(); Directory.Delete(Root, true);
        }
    }
}
