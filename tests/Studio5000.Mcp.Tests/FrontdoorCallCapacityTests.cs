using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Studio5000.Core;
using Studio5000.Mcp;
using Xunit;

namespace Studio5000.Mcp.Tests;

public sealed class FrontdoorCallCapacityTests
{
    private static McpFrontdoorPolicy Policy => new(MaximumProcessPrivateBytes: 536870912,
        PerClientCommitReservationBytes: 1073741824, MaximumTransportCommitReservationBytes: 8589934592);
    private sealed class Lease(bool fail) : IDisposable
    {
        public bool Closed;
        public void Dispose() { Closed = true; if (fail) throw new IOException("Synthetic forwarding lease close failure"); }
    }
    private static RequestContext<CallToolRequestParams> Request(McpServer server) => new(server,
        new JsonRpcRequest { Id = new RequestId(1), Method = "tools/call" },
        new CallToolRequestParams { Name = "studio_extensions_read" });

    [Theory]
    [InlineData("success")]
    [InlineData("cancelled")]
    [InlineData("timeout")]
    [InlineData("operation-error")]
    public async Task ForwardingCleanupFailureReleasesLocalPermitForTheNextUsefulRead(string outcome)
    {
        var leases = new List<Lease>(); var calls = 0;
        using var admission = new FrontdoorCalls("unused-synthetic-root", Policy, _ =>
        { var lease = new Lease(leases.Count < 2); leases.Add(lease); return lease; });
        await using var server = McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), new McpServerOptions());
        var handler = admission.Wrap((_, _) =>
        {
            calls++;
            if (calls <= 2)
            {
                if (outcome == "cancelled") throw new OperationCanceledException("Synthetic request cancellation");
                if (outcome == "timeout") throw new TimeoutException("Synthetic request timeout");
                if (outcome == "operation-error") throw new IOException("Synthetic operation error");
            }
            return ValueTask.FromResult(new CallToolResult { IsError = false, Content = [new TextContentBlock { Text = "fixture read returned" }] });
        });
        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<IOException>(() => handler(Request(server), default).AsTask());
            Assert.Equal("Synthetic forwarding lease close failure", error.Message);
        }
        var next = await handler(Request(server), default);
        Assert.False(next.IsError); Assert.Equal(3, calls);
        Assert.Equal("fixture read returned", Assert.IsType<TextContentBlock>(Assert.Single(next.Content)).Text);
        Assert.Equal(3, leases.Count); Assert.All(leases, lease => Assert.True(lease.Closed));
    }

    [Fact]
    public async Task FailedForwardingAdmissionReleasesLocalPermitWithoutDispatch()
    {
        var attempts = 0; var calls = 0; var lease = new Lease(false);
        using var admission = new FrontdoorCalls("unused-synthetic-root", Policy, _ =>
        { if (++attempts <= 2) throw new IOException("Synthetic admission failure"); return lease; });
        await using var server = McpServer.Create(new StreamServerTransport(Stream.Null, Stream.Null), new McpServerOptions());
        var handler = admission.Wrap((_, _) =>
        { calls++; return ValueTask.FromResult(new CallToolResult { IsError = false, Content = [new TextContentBlock { Text = "fixture read returned" }] }); });
        for (var i = 0; i < 2; i++) Assert.True((await handler(Request(server), default)).IsError);
        Assert.Equal(0, calls);
        Assert.False((await handler(Request(server), default)).IsError);
        Assert.Equal(1, calls); Assert.Equal(3, attempts); Assert.True(lease.Closed);
    }
}
