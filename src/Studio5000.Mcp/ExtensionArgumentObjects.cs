using System.Text.Json;
using Studio5000.Client;
using Studio5000.Extensibility;

namespace Studio5000.Mcp;

// Registered MCP signatures can project JsonElement as string. Decode only here;
// HTTP and extension-worker contracts continue to require an actual object.
internal static class ExtensionArgumentObjects
{
    internal const int MaximumCharacters = 1024 * 1024;
    private static McpServiceErrorException Invalid() => new(new ServiceError("invalid_arguments",
        "Extension arguments must be a JSON object or a serialized JSON-object string under 1 MiB.", 400, []));

    internal static ExtensionCall NormalizeCall(ExtensionCall call)
    {
        if (call is null || call.Arguments.ValueKind is not (JsonValueKind.Object or JsonValueKind.String)) throw Invalid();
        if (call.Arguments.GetRawText().Length > MaximumCharacters) throw Invalid();
        if (call.Arguments.ValueKind == JsonValueKind.Object) return call with { Arguments = call.Arguments.Clone() };
        var text = call.Arguments.GetString()!;
        if (text.Length > MaximumCharacters) throw Invalid();
        try
        {
            using var parsed = JsonDocument.Parse(text, new() { MaxDepth = ExtensionProtocol.Json.MaxDepth });
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            return call with { Arguments = parsed.RootElement.Clone() };
        }
        catch (JsonException) { throw Invalid(); }
    }

    internal static ExtensionJobRequest NormalizeJob(ExtensionJobRequest request) => request is null ? throw Invalid() :
        request with { Call = NormalizeCall(request.Call) };
}
