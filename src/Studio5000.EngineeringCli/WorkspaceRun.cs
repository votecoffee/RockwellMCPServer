using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Studio5000.Core;
using Studio5000.Core.Resources;

namespace Rockwell.Engineering;

internal static class WorkspaceRun
{
    private const int MaximumRequestBytes = 2 * 1024 * 1024;
    private const int MaximumInlineStepBytes = 96 * 1024;
    private static readonly JsonElement EmptyArguments = JsonSerializer.SerializeToElement(new { });

    private sealed record Request(string Workspace = "default", bool AllowNative = false, string? ControllerTargetId = null,
        string? Operation = null, JsonElement Arguments = default, Step[]? Steps = null);
    private sealed record Step(string Operation, JsonElement Arguments = default, int WaitMilliseconds = 0);
    private sealed record Settings(bool EnableControllerOperations = false, bool EnableSdkServiceRecovery = false, ControllerTarget[]? ControllerTargets = null,
        string[]? AllowedRoots = null, string[]? OutputRoots = null, string? FactoryTalkSchemaDirectory = null);
    private sealed record StepResult(int Index, string Operation, string Receipt, JsonElement? Value);
    private sealed record Hold(string JobId, string Workspace, string? ControllerTargetId);

    private static string SdkBridge => Path.Combine(AppContext.BaseDirectory, "bridges", "sdk", "Studio5000.NativeBridge.exe");
    private static string CipBridge => Path.Combine(AppContext.BaseDirectory, "bridges", "cip", "Studio5000.CipBridge.exe");
    private static string PilotHold => Path.Combine(EngineeringCli.StateRoot, "native-pending.json");
    private static string SdkServiceDirectory => Path.Combine(EngineeringCli.StateRoot, "sdk-service-control");

    public static object InstalledBridges() => new { sdk = File.Exists(SdkBridge), cip = File.Exists(CipBridge) };

    public static object Help() => new
    {
        command = "run --request <absolute JSON file> [--timeout-seconds 1..300]",
        single = new { workspace = "default", allowNative = false, operation = "studio_register_project", arguments = new { request = new { path = @"C:\Projects\Example.L5X" } } },
        batch = new { workspace = "default", allowNative = true, controllerTargetId = "<configured target for online actions>",
            steps = new[] { new { operation = "studio_watch_start", arguments = new { watchId = "<saved watch ID>" }, waitMilliseconds = 5000 } } },
        limits = new { maximumSteps = 24, maximumWaitPerStepMilliseconds = 120000, maximumTotalWaitMilliseconds = 240000, maximumRequestBytes = MaximumRequestBytes },
        deadlines = new { savedFileDefaultSeconds = 30, nativeDefaultSeconds = 300, nativeMinimumSeconds = 60,
            maximumSeconds = 300, nativeCleanupReserveSeconds = 30, backgroundSdkJobs = false },
        results = "SDK calls wait for their terminal result within this command. Retain SDK job references on timeout; no automatic retry. Every completed step has a durable receipt. Large values are available through the receipt path. Steps run in order and stop on an error."
    };

    internal static bool AllowsNative(byte[] bytes) => ReadJson<Request>(bytes).AllowNative;

    internal static int NativeWorkerTimeout(int commandSeconds)
    {
        if (commandSeconds is < 60 or > 300)
            throw new OperationException("native_deadline_too_short", "A native-enabled run requires --timeout-seconds 60..300 (default 300), including 30 seconds reserved for cleanup. No native job was started.");
        return commandSeconds - 30;
    }

    public static async Task<object> ExecuteAsync(string job, string requestPath, int timeoutSeconds)
    {
        var bytes = L5xSummary.ReadBytes(requestPath, MaximumRequestBytes);
        var request = ReadJson<Request>(bytes);
        var steps = Validate(request);
        var workspace = Path.Combine(EngineeringCli.StateRoot, "workspaces", request.Workspace);
        ProjectService.RejectLinks(workspace);
        Directory.CreateDirectory(workspace);
        var options = Options(workspace, request);
        if (request.AllowNative) options.NativeTimeoutSeconds = NativeWorkerTimeout(timeoutSeconds);
        var input = L5xSummary.Fingerprint(requestPath, bytes);
        EngineeringCli.WriteNew(Path.Combine(job, "invocation.json"), new { input, request.Workspace, request.AllowNative,
            request.ControllerTargetId, timeoutSeconds, options.NativeTimeoutSeconds, operations = steps.Select(s => s.Operation).ToArray() });

        var receipts = Path.Combine(job, "steps");
        Directory.CreateDirectory(receipts);
        var results = new List<StepResult>();
        var holds = Array.Empty<string>();
        var releaseHolds = false;
        var supervisor = request.AllowNative ? new ResourceSupervisor(job, Path.Combine(job, "native-registry"), 8, 16) : null;
        try
        {
            using var service = new ProjectService(options, sdkServiceMaintenanceHeld: () =>
                File.Exists(Path.Combine(EngineeringCli.StateRoot, "maintenance-hold.json")) ||
                File.Exists(Path.Combine(workspace, "maintenance-hold.json")),
                sdkServiceReceiptDirectory: SdkServiceDirectory, reportWorkspaceBusy: true);
            using var transport = new LocalOperations.Transport(service, job, request.AllowNative, request.Steps is not null, options.StateDirectory);
            if (request.AllowNative) holds = AcquireHolds(workspace, request, options, job);
            try
            {
                for (var index = 0; index < steps.Length; index++)
                {
                    var step = steps[index];
                    var number = index + 1;
                    var stem = Path.Combine(receipts, number.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
                    EngineeringCli.WriteNew(stem + "-intent.json", new { index = number, step.Operation });
                    var value = await LocalOperations.InvokeAsync(step.Operation, Arguments(step), transport, CancellationToken.None);
                    var receipt = stem + ".json";
                    EngineeringCli.WriteNew(receipt, new { index = number, step.Operation, value });
                    results.Add(new(number, step.Operation, receipt, new FileInfo(receipt).Length <= MaximumInlineStepBytes ? value : null));
                    if (step.WaitMilliseconds > 0) await Task.Delay(step.WaitMilliseconds);
                }
            }
            finally { await transport.StopOwnedWatchesAsync(); }
            if (transport.NativeOutcomeUncertain)
                throw new OperationException("native_job_review_required", "Inspect the native and watch receipts before another native dispatch.");
            releaseHolds = true;
        }
        catch
        {
            // An input or prerequisite failure before any native admission needs no reconciliation.
            releaseHolds = holds.Length > 0 && supervisor is not null && !supervisor.Ledger.Records().Any();
            throw;
        }
        finally
        {
            supervisor?.Dispose();
            if (releaseHolds && holds.Length > 0) EngineeringCli.WriteNew(Path.Combine(job, "native-closed.json"), holds);
        }
        return new { input, request.Workspace, steps = results };
    }

    private static Step[] Validate(Request request)
    {
        if (request.Workspace is null || request.Workspace.Length is < 1 or > 64 ||
            request.Workspace.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new OperationException("invalid_workspace", "Use a workspace name of 1 to 64 ASCII letters, digits, underscores or hyphens.");
        if (request.ControllerTargetId is not null && (!request.AllowNative || string.IsNullOrWhiteSpace(request.ControllerTargetId)))
            throw new OperationException("target_not_enabled", "A controller target requires explicit native execution for this command.");
        if ((request.Operation is null) == (request.Steps is null) || request.Steps is not null && request.Arguments.ValueKind != JsonValueKind.Undefined)
            throw new OperationException("invalid_workspace_request", "Supply either one operation with arguments or a steps array.");
        var steps = request.Steps ?? [new(request.Operation!, request.Arguments)];
        if (steps.Length is < 1 or > 24 || steps.Any(s => s is null || string.IsNullOrWhiteSpace(s.Operation) ||
            s.WaitMilliseconds is < 0 or > 120000) || steps.Sum(s => s.WaitMilliseconds) > 240000)
            throw new OperationException("invalid_workspace_batch", "Use 1 to 24 steps with at most 120 seconds of wait per step and 240 seconds total.");
        foreach (var step in steps) LocalOperations.Validate(step.Operation, Arguments(step));
        return steps;
    }

    private static JsonElement Arguments(Step step) => step.Arguments.ValueKind == JsonValueKind.Undefined ? EmptyArguments : step.Arguments;

    private static ServiceOptions Options(string workspace, Request request)
    {
        var config = Path.Combine(workspace, "settings.json");
        ProjectService.RejectLinks(config);
        var settings = File.Exists(config) ? ReadJson<Settings>(L5xSummary.ReadBytes(config, 64 * 1024)) : new Settings();
        var targets = settings.ControllerTargets ?? [];
        if (targets.Any(t => t is null || string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.CommunicationsPath)) ||
            targets.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != targets.Length)
            throw new OperationException("invalid_workspace_settings", "Configure unique target IDs and nonempty communications paths.");
        var target = request.ControllerTargetId is null ? null : targets.SingleOrDefault(t => t.Id == request.ControllerTargetId);
        if (request.ControllerTargetId is not null && (!settings.EnableControllerOperations || target is null))
            throw new OperationException("target_not_enabled", "The exact controller target must be configured and enabled in this workspace.");
        var options = new ServiceOptions
        {
            StateDirectory = Path.Combine(workspace, "state"),
            AllowedRoots = Roots(settings.AllowedRoots),
            OutputRoots = Roots(settings.OutputRoots),
            AllowAnySourcePath = settings.AllowedRoots is null,
            AllowAnyOutputPath = settings.OutputRoots is null,
            NativeBridgePath = request.AllowNative && File.Exists(SdkBridge) ? SdkBridge : null,
            CipBridgePath = request.AllowNative && File.Exists(CipBridge) ? CipBridge : null,
            EnableControllerOperations = target is not null,
            ControllerTargets = target is null ? targets : [target],
            EnableNativeGuiLaunch = false,
            EnableSdkServiceRecovery = settings.EnableSdkServiceRecovery,
            AutoStartSdkService = false
        };
        if (options.NativeBridgePath is not null) ProjectService.RejectLinks(options.NativeBridgePath);
        if (options.CipBridgePath is not null) ProjectService.RejectLinks(options.CipBridgePath);
        if (settings.FactoryTalkSchemaDirectory is { } schema) options.FactoryTalkSchemaDirectory = Roots([schema])[0];
        return options;
    }

    private static string[] Roots(string[]? roots)
    {
        if (roots is null) return [];
        if (roots.Length == 0 || roots.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            throw new OperationException("invalid_workspace_settings", "Configured roots must be nonempty arrays of absolute paths.");
        return roots.Select(Path.GetFullPath).ToArray();
    }

    private static T ReadJson<T>(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        RejectDuplicateProperties(document.RootElement);
        return document.RootElement.Deserialize<T>(EngineeringCli.StrictJson)
            ?? throw new OperationException("invalid_workspace_request", "Supply a JSON object.");
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
        if (value.ValueKind != JsonValueKind.Object) return;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new OperationException("invalid_json", $"Duplicate field '{property.Name}'.");
            RejectDuplicateProperties(property.Value);
        }
    }

    public static FileStream AcquireSdkServiceLease()
    {
        var path = Path.Combine(SdkServiceDirectory, "start.lock");
        ProjectService.RejectLinks(path);
        Directory.CreateDirectory(SdkServiceDirectory);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        { throw new OperationException("sdk_service_request_active", "Another command owns the local SDK service control request. Preserve its receipt."); }
    }

    private static string[] AcquireHolds(string workspace, Request request, ServiceOptions options, string job)
    {
        if (File.Exists(Path.Combine(EngineeringCli.StateRoot, "maintenance-hold.json")) ||
            File.Exists(Path.Combine(workspace, "maintenance-hold.json")))
            throw new OperationException("maintenance_hold", "An operator maintenance hold blocks native dispatch.");
        // The legacy pilot can target arbitrary reviewed copies. Keep its unknown outcomes visible to this workflow.
        if (File.Exists(PilotHold))
            throw new OperationException("native_cleanup_required", $"Review the retained native pilot marker at {PilotHold}.");
        var marker = new Hold(Path.GetFileName(job), request.Workspace, request.ControllerTargetId);
        var paths = new List<string> { Path.Combine(workspace, "native-pending.json") };
        if (request.ControllerTargetId is not null)
        {
            var target = options.ControllerTargets.Single();
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.CommunicationsPath.Trim().ToUpperInvariant())));
            paths.Add(Path.Combine(EngineeringCli.StateRoot, "target-holds", key + ".json"));
        }
        var acquired = new List<string>();
        try
        {
            foreach (var path in paths)
            {
                ProjectService.RejectLinks(path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                try { EngineeringCli.WriteNew(path, marker); }
                catch (IOException) when (File.Exists(path))
                { throw new OperationException("native_cleanup_required", $"An active or uncertain native operation owns {path}. Inspect its job receipt."); }
                acquired.Add(path);
            }
            EngineeringCli.WriteNew(Path.Combine(job, "native-holds.json"), paths);
            return paths.ToArray();
        }
        catch
        {
            foreach (var path in acquired) Release(path, marker.JobId);
            throw;
        }
    }

    public static void Confirm(string job, string id)
    {
        var closed = Path.Combine(job, "native-closed.json");
        if (!File.Exists(closed)) return;
        var paths = JsonSerializer.Deserialize<string[]>(L5xSummary.ReadBytes(closed, 16 * 1024), EngineeringCli.Json)!;
        var acquired = JsonSerializer.Deserialize<string[]>(L5xSummary.ReadBytes(Path.Combine(job, "native-holds.json"), 16 * 1024), EngineeringCli.Json)!;
        if (!paths.SequenceEqual(acquired, StringComparer.OrdinalIgnoreCase))
            throw new OperationException("native_cleanup_required", "The native close receipt does not match this job's acquired markers.");
        foreach (var path in paths) Release(path, id);
    }

    private static void Release(string path, string id)
    {
        var hold = JsonSerializer.Deserialize<Hold>(L5xSummary.ReadBytes(path, 16 * 1024), EngineeringCli.StrictJson);
        if (hold?.JobId != id)
            throw new OperationException("native_cleanup_required", "The retained native marker belongs to a different job.");
        File.Delete(path);
    }
}
