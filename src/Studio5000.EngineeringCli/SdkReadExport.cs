using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Studio5000.Core;

namespace Rockwell.Engineering;

internal static class SdkReadExport
{
    private static readonly HashSet<string> Types =
        ["BOOL", "SINT", "INT", "DINT", "LINT", "USINT", "UINT", "UDINT", "ULINT", "REAL", "LREAL", "STRING"];

    internal static object Plan(string requestPath)
    {
        var request = ToSdkRequest(L5xSummary.ReadBytes(requestPath, 64 * 1024));
        var path = request.SourcePath!;
        ProjectService.RejectLinks(path);
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length is <= 0 or > 500L * 1024 * 1024)
            throw new OperationException("invalid_input_size", "SDK input must be nonempty and no larger than 500 MiB.");
        var digest = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        if (!string.Equals(digest, request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new OperationException("stale_source", "The ACD fingerprint differs from the request.");
        return new { input = new { path, bytes = source.Length, sha256 = digest }, calls = request.Calls,
            profile = NativeExecutionPolicy.LocalProject, nativeExecuted = false };
    }

    internal static SdkRunRequest ToSdkRequest(byte[] bytes)
    {
        var request = JsonSerializer.Deserialize<Request>(bytes, EngineeringCli.StrictJson)
            ?? throw new OperationException("invalid_request", "Supply a typed SDK read/export request.");
        var source = L5xWorkflow.Absolute(request.SourcePath, "disposable ACD path");
        if (!Path.GetExtension(source).Equals(".acd", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(request.ExpectedSha256 ?? "", "^[0-9a-fA-F]{64}$"))
            throw new OperationException("invalid_sdk_source", "Use a saved ACD and its SHA-256.");
        if (!Identifier(request.Tag) || request.Program is not null && !Identifier(request.Program) || !Types.Contains(request.DataType))
            throw new OperationException("invalid_sdk_tag", "Supply a controller or program tag name and a supported scalar SDK data type.");
        var export = request.ExportName ?? "review.L5X";
        if (!Regex.IsMatch(export, "^[A-Za-z0-9_][A-Za-z0-9_.-]{0,80}\\.[Ll]5[Xx]$") ||
            export.StartsWith("final.", StringComparison.OrdinalIgnoreCase) || export.StartsWith("input.", StringComparison.OrdinalIgnoreCase) ||
            export.StartsWith("import-", StringComparison.OrdinalIgnoreCase))
            throw new OperationException("invalid_sdk_export", "Use a simple new L5X artifact name outside the reserved SDK names.");
        var scope = request.Program is null ? "Controller" : $"Controller/Programs/Program[@Name='{request.Program}']";
        var tag = $"{scope}/Tags/Tag[@Name='{request.Tag}']";
        SdkCall[] calls =
        [
            new("OpenLogixProjectAsync", []),
            new($"GetTagValue{request.DataType}Async", new() { ["tagPath"] = JsonSerializer.SerializeToElement(tag),
                ["mode"] = JsonSerializer.SerializeToElement("Offline") }),
            new("SaveAsAsync", new() { ["savePath"] = JsonSerializer.SerializeToElement(export),
                ["force"] = JsonSerializer.SerializeToElement(false), ["detailedL5x"] = JsonSerializer.SerializeToElement(true) })
        ];
        SdkCatalog.ValidateCalls(calls);
        NativeExecutionPolicy.ValidateCalls(NativeExecutionPolicy.LocalProject, calls);
        return new(calls, SourcePath: source, ExpectedSha256: request.ExpectedSha256);
    }

    private static bool Identifier(string? name) => Regex.IsMatch(name ?? "", "^[A-Za-z_][A-Za-z0-9_]{0,39}$");

    private sealed record Request(string SourcePath, string ExpectedSha256, string Tag, string DataType,
        string? Program = null, string? ExportName = null);
}
