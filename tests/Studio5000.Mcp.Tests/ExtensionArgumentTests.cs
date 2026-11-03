using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Studio5000.Client;
using Studio5000.Core;
using Studio5000.Extensibility;
using Studio5000.Mcp;
using Xunit;

namespace Studio5000.Mcp.Tests;

public sealed class ExtensionArgumentTests
{
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
    private static ExtensionCall Call(JsonElement arguments) => new("fixture", "echo", "generation", arguments);

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Object_and_serialized_object_preserve_JSON_values_and_identity(bool serialized)
    {
        const string raw = "{\"integer\":9007199254740993,\"text\":\"{} \\\" Ω\",\"nested\":{\"items\":[true,null,2]}}";
        using var doc = JsonDocument.Parse(raw);
        var input = Call(serialized ? J(raw) : doc.RootElement);
        var actual = ExtensionArgumentObjects.NormalizeCall(input);
        Assert.Equal(raw, actual.Arguments.GetRawText()); Assert.Equal(JsonValueKind.Object, actual.Arguments.ValueKind);
        Assert.Equal(input.ExtensionId, actual.ExtensionId); Assert.Equal(input.Operation, actual.Operation); Assert.Equal(input.Generation, actual.Generation);
        Assert.Equal(serialized ? JsonValueKind.String : JsonValueKind.Object, input.Arguments.ValueKind);
        doc.Dispose(); Assert.Equal(9007199254740993L, actual.Arguments.GetProperty("integer").GetInt64());
    }
    [Theory]
    [InlineData("null")][InlineData("[]")][InlineData("42")][InlineData("true")][InlineData("\"{}\"")]
    [InlineData("{")][InlineData("{\"a\":1,}")][InlineData("{} trailing")][InlineData("/* comment */ {}")]
    public void Invalid_serialized_values_are_rejected_without_recursive_unwrapping(string raw)
    {
        var error = Assert.Throws<McpServiceErrorException>(() => ExtensionArgumentObjects.NormalizeCall(Call(J(raw))));
        Assert.Equal("invalid_arguments", error.Error.Code); Assert.Equal(400, error.Error.HttpStatus); Assert.DoesNotContain(raw, error.Error.Message);
    }
    [Theory]
    [InlineData(null)][InlineData(1)][InlineData(true)]
    public void Direct_scalars_and_undefined_are_rejected(object? value)
    {
        Assert.Throws<McpServiceErrorException>(() => ExtensionArgumentObjects.NormalizeCall(Call(J(value))));
        Assert.Throws<McpServiceErrorException>(() => ExtensionArgumentObjects.NormalizeCall(Call(default)));
        Assert.Throws<McpServiceErrorException>(() => ExtensionArgumentObjects.NormalizeCall(null!));
    }
    [Fact]
    public void Direct_and_serialized_size_limits_and_nested_depth_stay_bounded()
    {
        var limit = ExtensionArgumentObjects.MaximumCharacters;
        using var atLimit = JsonDocument.Parse("{\"text\":\"" + new string('x', limit - 11) + "\"}");
        Assert.Equal(limit, atLimit.RootElement.GetRawText().Length);
        Assert.Equal(limit, ExtensionArgumentObjects.NormalizeCall(Call(atLimit.RootElement)).Arguments.GetRawText().Length);
        using var over = JsonDocument.Parse("{\"text\":\"" + new string('x', limit) + "\"}");
        Assert.Throws<McpServiceErrorException>(() => ExtensionArgumentObjects.NormalizeCall(Call(over.RootElement)));
        Assert.Throws<McpServiceErrorException>(() => ExtensionArgumentObjects.NormalizeCall(Call(J(atLimit.RootElement.GetRawText()))));
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 130)) + "{}" + new string('}', 130);
        Assert.Throws<McpServiceErrorException>(() => ExtensionArgumentObjects.NormalizeCall(Call(J(deep))));
    }

    [Theory]
    [InlineData("Read", "read")][InlineData("Preview", "preview")][InlineData("Apply", "apply")][InlineData("Control", "control")]
    public async Task SDK_generated_schema_and_binding_forward_serialized_objects_once_on_the_original_channel(string method, string effect)
    {
        await using var fixture = await BoundaryFixture.Create(effect);
        var tool = McpServerTool.Create(typeof(ExtensionTools).GetMethod(method)!, fixture.Tools);
        var schema = tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("call").GetProperty("properties");
        Assert.Equal(JsonValueKind.True, schema.GetProperty("arguments").ValueKind); // Unconstrained SDK schema; registered signatures project this to string.
        var result = await fixture.Invoke(tool, new { call = new { extensionId = "fixture", operation = "echo", generation = fixture.Generation, arguments = "{\"text\":\"fixture\"}" } });
        Assert.NotEqual(true, result.IsError); Assert.Equal(1, fixture.HttpCalls); Assert.Equal(1, fixture.Worker.Calls);
        Assert.Equal("/api/extensions/invoke/" + effect, fixture.LastPath);
        Assert.Equal(JsonValueKind.Object, fixture.LastCall!.Arguments.ValueKind);
    }
    [Fact]
    public async Task SDK_direct_object_compatibility_and_nested_StartJob_use_the_same_normalizer_without_job_dispatch()
    {
        await using var fixture = await BoundaryFixture.Create("read");
        var read = McpServerTool.Create(typeof(ExtensionTools).GetMethod("Read")!, fixture.Tools);
        Assert.NotEqual(true, (await fixture.Invoke(read, new { call = new { extensionId = "fixture", operation = "echo", generation = fixture.Generation, arguments = new { text = "direct" } } })).IsError);
        var job = McpServerTool.Create(typeof(ExtensionTools).GetMethod("StartJob")!, fixture.Tools);
        var nested = job.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("request").GetProperty("properties").GetProperty("call").GetProperty("properties");
        Assert.Equal(JsonValueKind.True, nested.GetProperty("arguments").ValueKind);
        Assert.NotEqual(true, (await fixture.Invoke(job, new { request = new { effect = "read", call = new { extensionId = "fixture", operation = "echo", generation = fixture.Generation, arguments = "{\"text\":\"nested\"}" } } })).IsError);
        Assert.Equal("/api/extensions/jobs", fixture.LastPath); Assert.Equal(JsonValueKind.Object, fixture.LastCall!.Arguments.ValueKind);
        Assert.Equal("read", fixture.LastJob!.Effect); Assert.Equal(1, fixture.Worker.Calls); // The job request was captured, never dispatched.
        Assert.Empty(fixture.Host.Jobs());
        Assert.NotEqual(true, (await fixture.Invoke(job, new { request = new { effect = "read", call = new { extensionId = "fixture", operation = "echo", generation = fixture.Generation, arguments = new { text = "nested-direct" } } } })).IsError);
        Assert.Equal("nested-direct", fixture.LastCall!.Arguments.GetProperty("text").GetString()); Assert.Equal(1, fixture.Worker.Calls); Assert.Empty(fixture.Host.Jobs());
    }
    [Fact]
    public void Nested_jobs_keep_effect_and_reject_null_call_before_forwarding()
    {
        var job = ExtensionArgumentObjects.NormalizeJob(new("control", Call(J("{}"))));
        Assert.Equal("control", job.Effect); Assert.Equal(JsonValueKind.Object, job.Call.Arguments.ValueKind);
        Assert.Throws<McpServiceErrorException>(() => ExtensionArgumentObjects.NormalizeJob(null!));
        Assert.Throws<McpServiceErrorException>(() => ExtensionArgumentObjects.NormalizeJob(new("read", null!)));
    }
    [Fact]
    public async Task Original_forwarding_boundary_reproduces_the_reported_string_failure_in_a_pure_fixture()
    {
        await using var fixture = await BoundaryFixture.Create("read");
        var original = McpServerTool.Create((ExtensionCall call, CancellationToken ct) => fixture.RawForward(call, ct));
        var response = await fixture.Invoke(original, new { call = new { extensionId = "fixture", operation = "echo", generation = fixture.Generation, arguments = "{\"text\":\"fixture\"}" } });
        Assert.True(response.IsError); Assert.Equal("invalid_arguments", fixture.LastError); Assert.Equal(0, fixture.Worker.Calls);
        Assert.Equal(JsonValueKind.String, fixture.LastCall!.Arguments.ValueKind); Assert.Equal(1, fixture.HttpCalls);
    }
    [Fact]
    public async Task Oversize_and_direct_array_SDK_inputs_stop_before_HTTP()
    {
        await using var fixture = await BoundaryFixture.Create("read");
        var tool = McpServerTool.Create(typeof(ExtensionTools).GetMethod("Read")!, fixture.Tools);
        foreach (var arguments in new[] { J(new[] { 1 }), J(new string('x', ExtensionArgumentObjects.MaximumCharacters + 1)) })
        {
            var response = await fixture.Invoke(tool, new { call = new { extensionId = "fixture", operation = "echo", generation = fixture.Generation, arguments } });
            Assert.True(response.IsError); Assert.Equal(0, fixture.HttpCalls);
        }
    }
    [Theory]
    [InlineData("{")][InlineData("[]")][InlineData("42")]
    public async Task Malformed_or_nonobject_SDK_arguments_make_no_HTTP_request(string arguments)
    {
        await using var fixture = await BoundaryFixture.Create("read");
        var read = McpServerTool.Create(typeof(ExtensionTools).GetMethod("Read")!, fixture.Tools);
        var result = await fixture.Invoke(read, new { call = new { extensionId = "fixture", operation = "echo", generation = fixture.Generation, arguments } });
        Assert.True(result.IsError); Assert.Equal(0, fixture.HttpCalls); Assert.Equal(0, fixture.Worker.Calls);
        var job = McpServerTool.Create(typeof(ExtensionTools).GetMethod("StartJob")!, fixture.Tools);
        Assert.True((await fixture.Invoke(job, new { request = new { effect = "read", call = new { extensionId = "fixture", operation = "echo", generation = fixture.Generation, arguments } } })).IsError);
        Assert.Equal(0, fixture.HttpCalls); Assert.Empty(fixture.Host.Jobs());
    }
    [Theory]
    [InlineData("read", "apply", false, "effect_mismatch")]
    [InlineData("read", "read", true, "stale_generation")]
    public async Task Service_channel_and_generation_checks_reject_without_dispatch_or_retry(string channel, string declared, bool stale, string code)
    {
        await using var fixture = await BoundaryFixture.Create(declared);
        var tool = McpServerTool.Create(typeof(ExtensionTools).GetMethod("Read")!, fixture.Tools);
        var result = await fixture.Invoke(tool, new { call = new { extensionId = "fixture", operation = "echo", generation = stale ? "stale" : fixture.Generation, arguments = "{\"text\":\"fixture\"}" } });
        Assert.True(result.IsError); Assert.Equal(1, fixture.HttpCalls); Assert.Equal(0, fixture.Worker.Calls);
        Assert.Equal(code, fixture.LastError); Assert.EndsWith(channel, fixture.LastPath);
    }
    [Fact]
    public async Task Direct_HTTP_string_and_wrong_operation_schema_still_fail_the_unchanged_service_contract()
    {
        await using var fixture = await BoundaryFixture.Create("read");
        Assert.Equal("invalid_arguments", (await Assert.ThrowsAsync<OperationException>(() => fixture.Host.InvokeAsync("read", new("fixture", "echo", fixture.Generation, J("{}"))))).Code);
        var read = McpServerTool.Create(typeof(ExtensionTools).GetMethod("Read")!, fixture.Tools);
        Assert.True((await fixture.Invoke(read, new { call = new { extensionId = "fixture", operation = "echo", generation = fixture.Generation, arguments = "{\"unexpected\":1}" } })).IsError);
        Assert.Equal("invalid_arguments", fixture.LastError); Assert.Equal(0, fixture.Worker.Calls); Assert.Equal(1, fixture.HttpCalls);
    }

    private sealed class EchoWorker(ExtensionOperation definition) : IExtensionWorker
    {
        public int Calls;
        public Task<ExtensionOperation[]> DescribeAsync(CancellationToken ct) => Task.FromResult(new[] { definition });
        public Task<JsonElement> InvokeAsync(string operation, JsonElement arguments, CancellationToken ct) { Calls++; return Task.FromResult(arguments.Clone()); }
        public void Dispose() { }
    }
    private sealed class BoundaryFixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellMcpArgumentFixture", Guid.NewGuid().ToString("N"));
        private readonly HttpListener listener = new();
        private Task? server;
        private ServiceClient? client;
        public ExtensionHost Host = null!;
        public EchoWorker Worker = null!;
        public ExtensionTools Tools = null!;
        public string Generation = "";
        public int HttpCalls;
        public string? LastPath, LastError;
        public ExtensionCall? LastCall;
        public ExtensionJobRequest? LastJob;
        public static async Task<BoundaryFixture> Create(string effect)
        {
            var f = new BoundaryFixture(); Directory.CreateDirectory(Path.Combine(f.root, ".local"));
            var options = new ServiceOptions { StateDirectory = Path.Combine(f.root, "state"), ExtensionsDirectory = Path.Combine(f.root, "packages") };
            f.Worker = new(new("echo", "Synthetic argument echo only", effect, J(new { type = "object", properties = new { text = new { type = "string" } }, required = new[] { "text" }, additionalProperties = false })));
            f.Host = new(options, f.root, (_, _) => f.Worker);
            var package = Path.Combine(options.ExtensionsDirectory, "fixture", "versions", "1"); Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "fixture.dll"), "synthetic bytes; never loaded or executed");
            File.WriteAllText(Path.Combine(package, "manifest.json"), JsonSerializer.Serialize(new ExtensionPackage("fixture", "1", 1, "fixture.dll", "Fixture", new() { ["fixture.dll"] = ProjectService.Hash(File.ReadAllBytes(Path.Combine(package, "fixture.dll"))) }), ExtensionProtocol.Json));
            f.Generation = (await f.Host.ReloadAsync(new("fixture", "1"))).Generation;
            using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
            var url = "http://127.0.0.1:" + port; f.listener.Prefixes.Add(url + "/"); f.listener.Start();
            var key = Path.Combine(f.root, ".local", "fixture.key"); File.WriteAllText(key, "synthetic-key");
            File.WriteAllText(Path.Combine(f.root, ".local", "connection.json"), JsonSerializer.Serialize(new { url, tokenFile = key }));
            f.client = new(f.root, error => new McpServiceErrorException(error.Error)); f.Tools = new(f.client); f.server = f.Serve(); return f;
        }
        private async Task Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }
                HttpCalls++; LastPath = context.Request.Url!.AbsolutePath;
                try
                {
                    using var reader = new StreamReader(context.Request.InputStream); var body = await reader.ReadToEndAsync(); object response;
                    if (LastPath == "/api/extensions/jobs")
                    {
                        LastJob = JsonSerializer.Deserialize<ExtensionJobRequest>(body, ExtensionProtocol.Json)!; LastCall = LastJob.Call;
                        response = new ExtensionJob("fixture-captured", "fixture", "echo", Generation, LastJob.Effect, "captured-no-dispatch", DateTimeOffset.UnixEpoch);
                    }
                    else
                    {
                        LastCall = JsonSerializer.Deserialize<ExtensionCall>(body, ExtensionProtocol.Json)!;
                        response = await Host.InvokeAsync(LastPath.Split('/')[^1], LastCall);
                    }
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(response, ProjectService.Json); await context.Response.OutputStream.WriteAsync(bytes);
                }
                catch (OperationException e)
                {
                    LastError = e.Code; context.Response.StatusCode = 400;
                    await context.Response.OutputStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { code = e.Code, message = e.Message }));
                }
                finally { context.Response.Close(); }
            }
        }
        public Task<JsonElement> RawForward(ExtensionCall call, CancellationToken ct) => client!.PostAsync<JsonElement>("/api/extensions/invoke/read", call, ct);
        public async Task<CallToolResult> Invoke(McpServerTool tool, object arguments)
        {
            await using var mcp = McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), new McpServerOptions());
            var properties = J(arguments).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            var request = new CallToolRequestParams { Name = tool.ProtocolTool.Name, Arguments = properties };
            return await ServiceErrors.Wrap((context, ct) => tool.InvokeAsync(context, ct))(new RequestContext<CallToolRequestParams>(mcp, new JsonRpcRequest { Id = new RequestId(1), Method = "tools/call" }, request), CancellationToken.None);
        }
        public async ValueTask DisposeAsync()
        {
            client?.Dispose(); listener.Close(); if (server is not null) await server; Host?.Dispose(); Directory.Delete(root, true);
        }
    }
}
