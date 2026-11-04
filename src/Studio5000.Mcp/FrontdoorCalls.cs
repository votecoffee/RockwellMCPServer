using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Studio5000.Core;

namespace Studio5000.Mcp;

internal sealed class FrontdoorCalls(string root, McpFrontdoorPolicy policy,
    Func<CancellationToken, IDisposable>? acquireForwardedCall = null) : IDisposable
{
    private readonly SemaphoreSlim concurrent = new(2, 2);
    private readonly Func<CancellationToken, IDisposable> acquireForward = acquireForwardedCall ?? (ct => McpClientLease.AcquireForwardedCall(root, policy, ct));
    private static readonly HashSet<string> Metadata = ["studio_capabilities", "rockwell_broker_capabilities",
        "rockwell_read_workflow", "rockwell_read_workflow_reference", "studio_extensions_catalog", "studio_extensions_describe"];
    internal McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => async (context, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (!concurrent.Wait(0)) return Refused("mcp_frontdoor_busy", "This transport's bounded request window is occupied.");
            IDisposable? forward = null;
            try
            {
                try
                {
                    policy.CheckCurrentProcess();
                    if (!Metadata.Contains(context.Params?.Name ?? "")) forward = acquireForward(ct);
                }
                catch (OperationException error) { return Refused(error.Code, error.Message); }
                catch (Exception error) when (error is IOException or InvalidOperationException)
                { return Refused("mcp_forwarding_capacity", "The bounded forwarding window is unavailable; this request was not dispatched."); }
                return await next(context, ct);
            }
            finally
            {
                try { forward?.Dispose(); }
                finally { concurrent.Release(); }
            }
        };
    private static CallToolResult Refused(string code, string message)
    {
        var value = new { code, message, dispatched = false, automaticRetry = false };
        return new() { IsError = true, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value) }],
            StructuredContent = JsonSerializer.SerializeToElement(value) };
    }
    public void Dispose() => concurrent.Dispose();
}
