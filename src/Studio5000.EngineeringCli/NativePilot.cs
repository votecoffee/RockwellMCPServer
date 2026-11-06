using System.Security.Cryptography;
using System.Text.Json;
using Studio5000.Core;
using Studio5000.Core.Resources;

namespace Rockwell.Engineering;

internal static class NativePilot
{
    private static string Configuration => Path.Combine(AppContext.BaseDirectory, "native-pilot.json");
    private static string Pending => Path.Combine(EngineeringCli.StateRoot, "native-pending.json");

    internal static CliProblem? CheckAdmission()
    {
        if (File.Exists(Pending))
            return new("native_cleanup_required", "A prior Rockwell native dispatch needs review. Inspect the retained native-pending.json and linked job.");
        return File.Exists(Configuration) ? null :
            new("native_disabled", "The local native pilot is disabled. This package runs saved L5X reads and SDK catalog discovery.");
    }

    internal static object Run(string job, string requestPath, bool typedReadExport)
    {
        if (CheckAdmission() is { } blocked) throw new OperationException(blocked.Code, blocked.Message);
        var config = JsonSerializer.Deserialize<PilotConfiguration>(L5xSummary.ReadBytes(Configuration, 16 * 1024), EngineeringCli.StrictJson)
            ?? throw new OperationException("native_pilot_invalid", "Missing native pilot configuration.");
        if (!Path.IsPathFullyQualified(config.WorkspaceRoot) || !Directory.Exists(config.WorkspaceRoot))
            throw new OperationException("native_pilot_invalid", "Use an existing absolute native pilot workspace.");
        var root = Path.GetFullPath(config.WorkspaceRoot);
        ProjectService.RejectLinks(root);
        if (File.Exists(Path.Combine(root, ".local", "maintenance-hold.json")))
            throw new OperationException("maintenance_hold", "The native pilot workspace is under maintenance hold.");
        var options = NativeExecutionPolicy.CreateLocalOptions(root);
        var bridge = options.NativeBridgePath!;
        ProjectService.RejectLinks(bridge);
        using (var stream = File.OpenRead(bridge))
        {
            var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (digest != config.BridgeSha256) throw new OperationException("native_bridge_changed", "The pilot bridge differs from the reviewed SHA-256.");
        }

        var bytes = L5xSummary.ReadBytes(requestPath, 2 * 1024 * 1024);
        var request = typedReadExport ? SdkReadExport.ToSdkRequest(bytes) :
            JsonSerializer.Deserialize<SdkRunRequest>(bytes, EngineeringCli.StrictJson)
                ?? throw new OperationException("sdk_request_invalid", "Supply a JSON SDK run request.");
        if (request.ProjectId is not null || request.SourceArtifactId is not null || request.ControllerTargetId is not null ||
            request.SourcePath is { } source && !Path.IsPathFullyQualified(source) ||
            request.InputFiles?.Values.Any(file => file is null || file.ArtifactId is not null || file.Path is null ||
                !Path.IsPathFullyQualified(file.Path)) == true)
            throw new OperationException("sdk_file_scope", "Use explicit local source/import paths and hashes. Service IDs and controller targets are outside the pilot.");
        SdkCatalog.ValidateCalls(request.Calls);
        NativeExecutionPolicy.ValidateCalls(NativeExecutionPolicy.LocalProject, request.Calls);

        using var resources = new ResourceSupervisor(job, Path.Combine(job, "native-registry"), 8, 16);
        using var projects = new ProjectService(options);
        ProjectService.RejectLinks(Pending);
        EngineeringCli.WriteNew(Pending, new { jobId = Path.GetFileName(job), job, requestPath, pilot = root,
            dispatchPreparedUtc = DateTimeOffset.UtcNow });
        var sdkJob = projects.StartSdkJobAsync(request).GetAwaiter().GetResult();
        while (sdkJob.Status is "queued" or "running")
        {
            Thread.Sleep(100);
            sdkJob = projects.SdkJob(sdkJob.Id);
        }
        EngineeringCli.WriteNew(Path.Combine(job, "sdk-result-reference.json"), new { sdkJob.Id, sdkJob.Status,
            record = Path.Combine(options.StateDirectory, "sdk-jobs", sdkJob.Id, "job.json") });
        if (sdkJob.Status != "succeeded")
            throw new OperationException("native_job_review_required", $"SDK job {sdkJob.Id} reported {sdkJob.Status}. Review its retained receipt before another native dispatch.");
        var artifacts = sdkJob.Artifacts.Select(artifact => new { artifact, path = projects.Download(artifact.Id).Path }).ToArray();
        return new { input = L5xSummary.Fingerprint(requestPath, bytes), sdkJob, artifacts };
    }

    internal static void Confirm(string id)
    {
        using var marker = JsonDocument.Parse(L5xSummary.ReadBytes(Pending, 16 * 1024));
        if (marker.RootElement.GetProperty("jobId").GetString() != id)
            throw new OperationException("native_cleanup_required", "The retained native marker belongs to a different job.");
        EngineeringCli.WriteNew(Path.Combine(EngineeringCli.StateRoot, "jobs", id, "native-close-verified.json"),
            new { jobId = id, confirmedUtc = DateTimeOffset.UtcNow });
        File.Delete(Pending);
    }

    private sealed record PilotConfiguration(string WorkspaceRoot, string BridgeSha256);
}
