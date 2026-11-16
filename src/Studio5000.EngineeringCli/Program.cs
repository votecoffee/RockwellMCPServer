using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Studio5000.Core;
using Studio5000.Core.Resources;
using Rockwell.Engineering;

return EngineeringCli.Execute(args);

internal sealed record CliProblem(string Code, string Message);
internal sealed record WorkerReply(JsonElement? Result, CliProblem? Error);
internal sealed record CliRequest(string Operation, string Input, int TimeoutSeconds, bool CancelOnEof, byte[]? PinnedRequest = null);

internal static class EngineeringCli
{
    internal const string Version = "2.6.0";
    internal const string JobEnvironment = "ROCKWELL_ENGINEERING_OWNED_JOB";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };
    internal static readonly JsonSerializerOptions StrictJson = new(Json)
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    internal static string StateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RockwellEngineering");

    internal static int Execute(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args is ["--owned-worker", var operation, var input, var deadline] && int.TryParse(deadline, out var seconds) && seconds is >= 1 and <= 300)
            return RunWorker(operation, input, seconds);
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            Console.WriteLine("""
                Rockwell.EngineeringCli operations
                  l5x-summary --input <absolute L5X path> [--timeout-seconds 1..300]
                  l5x-inspect --request <absolute JSON path>
                  l5x-preview --request <absolute JSON path>
                  l5x-apply --request <absolute JSON path>
                  sdk-read-export-plan --request <absolute JSON path>
                  sdk-read-export --request <absolute JSON path> [--timeout-seconds 1..300]
                  sdk-offline --request <absolute JSON path> [--timeout-seconds 1..300]
                  sdk-catalog
                  operations
                  operation-help <name>
                  run --request <absolute JSON path> [--timeout-seconds 1..300]
                  --version

                Job commands emit one JSON result. Saved-file timeout defaults to 30 seconds;
                native-enabled run and pilot commands default to 300 seconds.
                Use operation-help run for the workspace request format and native opt-in.
                """);
            return 0;
        }
        if (args is ["--version"]) { Console.WriteLine(Version); return 0; }
        if (args is ["operations"])
        {
            Print(new { version = Version, operations = new[] {
                new { name = "l5x-summary", input = "--input", mode = "saved-file-read" },
                new { name = "l5x-inspect", input = "--request", mode = "saved-file-read" },
                new { name = "l5x-preview", input = "--request", mode = "offline-review" },
                new { name = "l5x-apply", input = "--request", mode = "new-offline-copy" },
                new { name = "sdk-read-export-plan", input = "--request", mode = "offline-review" },
                new { name = "sdk-read-export", input = "--request", mode = "native-pilot-required" },
                new { name = "sdk-offline", input = "--request", mode = "native-pilot-required" },
                new { name = "sdk-catalog", input = "", mode = "static-metadata" },
                new { name = "run", input = "--request", mode = "workspace-operation" } },
                workspaceOperations = LocalOperations.Catalog() });
            return 0;
        }
        if (args is ["sdk-catalog"])
        {
            Print(new { version = Version, catalogVersion = WorkspaceIdentity.Version, assembly = SdkCatalog.Surface.Assembly, methods = SdkCatalog.Methods,
                offlinePilotMethods = NativeExecutionPolicy.LocalMethods });
            return 0;
        }
        try
        {
            if (args is ["operation-help", var name])
            {
                Print(name == "run" ? WorkspaceRun.Help() : LocalOperations.Help(name));
                return 0;
            }
            var request = Parse(args);
            if (!OperatingSystem.IsWindowsVersionAtLeast(10))
                return Report(69, request.Operation, null, null, "not-started", null, new("unsupported_platform", "Use Windows 10 or newer."));
            return RunJob(request);
        }
        catch (OperationException ex)
        {
            return Report(2, args[0], null, null, "not-started", null, Problem(ex));
        }
        catch (Exception ex)
        {
            return Report(70, args[0], null, null, "unverified", null, Problem(ex));
        }
    }

    internal static CliRequest Parse(string[] args)
    {
        var operation = args[0];
        var inputOption = operation switch { "l5x-summary" => "--input",
            "l5x-inspect" or "l5x-preview" or "l5x-apply" or "sdk-offline" or "sdk-read-export" or "sdk-read-export-plan" or "run" => "--request",
            _ => throw new OperationException("unknown_operation", "Use operations or --help to list the supported commands.") };
        string? input = null;
        int? timeout = null;
        var cancelOnEof = false;
        var options = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var option = args[i];
            if (!options.Add(option)) throw new OperationException("invalid_arguments", $"Duplicate option {option}.");
            if (option == "--cancel-on-stdin-eof") { cancelOnEof = true; continue; }
            if (i + 1 >= args.Length) throw new OperationException("invalid_arguments", $"Missing value for {option}.");
            var value = args[++i];
            if (option == inputOption && Path.IsPathFullyQualified(value)) input = Path.GetFullPath(value);
            else if (option == "--timeout-seconds" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 1 and <= 300) timeout = seconds;
            else throw new OperationException("invalid_arguments", $"Invalid option or value for {option}.");
        }
        if (input is null) throw new OperationException("invalid_arguments", $"Supply {inputOption} with an absolute file path.");
        var pinned = operation == "run" ? L5xSummary.ReadBytes(input, 2 * 1024 * 1024) : null;
        var nativeWorkspace = pinned is not null && WorkspaceRun.AllowsNative(pinned);
        var resolved = timeout ?? (nativeWorkspace || operation is "sdk-offline" or "sdk-read-export" ? 300 : 30);
        if (nativeWorkspace) _ = WorkspaceRun.NativeWorkerTimeout(resolved);
        return new(operation, input, resolved, cancelOnEof, pinned);
    }

    private static int RunJob(CliRequest request)
    {
        var native = request.Operation is "sdk-offline" or "sdk-read-export";
        if (native)
        {
            var blocked = NativePilot.CheckAdmission();
            if (blocked is not null) return Report(69, request.Operation, null, null, "not-started", null, blocked);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
        using var interrupt = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, interrupt.Token);
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; interrupt.Cancel(); };
        Console.CancelKeyPress += cancel;
        if (request.CancelOnEof) _ = Task.Run(() =>
        {
            try { _ = Console.In.ReadToEnd(); interrupt.Cancel(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        });
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var job = Path.Combine(StateRoot, "jobs", id);
            ProjectService.RejectLinks(job);
            Directory.CreateDirectory(job);
            var receipt = Path.Combine(job, "result.json");
            WriteNew(Path.Combine(job, "intent.json"), new { jobId = id, request.Operation, request.Input,
                request.TimeoutSeconds, createdUtc = DateTimeOffset.UtcNow });
            if (request.PinnedRequest is { } pinned)
                WriteNew(Path.Combine(job, "request-fingerprint.json"), L5xSummary.Fingerprint(request.Input, pinned));
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("The apphost path is unavailable."))
            { UseShellExecute = false, WorkingDirectory = job };
            foreach (var argument in new[] { "--owned-worker", request.Operation, request.Input, request.TimeoutSeconds.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
            start.Environment[JobEnvironment] = job;
            var worker = OwnedCommand.Run(job, start, request.PinnedRequest is { } bytes ? Convert.ToBase64String(bytes) : null, stop.Token);
            WorkerReply? reply = null;
            if (worker.ExitCode is 0 or 1 && worker.Output.Length != 0)
                try { reply = JsonSerializer.Deserialize<WorkerReply>(worker.Output, Json); }
                catch (JsonException) { }
            var (code, problem) = worker.Cleanup == "uncertain" ? (70, new CliProblem("cleanup_unverified", "Owned job cleanup could not be verified. Inspect the retained receipt."))
                : worker.Failure is OperationCanceledException && timeout.IsCancellationRequested ? (124, new CliProblem("deadline_exceeded", "The job exceeded its deadline."))
                : worker.Failure is OperationCanceledException ? (130, new CliProblem("cancelled", "The job was cancelled."))
                : worker.Failure is not null ? (70, Problem(worker.Failure))
                : reply?.Error is { } error ? (1, error)
                : worker.ExitCode != 0 || reply?.Result is null ? (1, new CliProblem("worker_failed", "The owned worker exited without a valid result."))
                : (0, (CliProblem?)null);
            if (native && code == 0 && worker.Cleanup == "confirmed")
            {
                try { NativePilot.Confirm(id); }
                catch (Exception ex) { (code, problem) = (70, Problem(ex)); }
            }
            if (request.Operation == "run" && worker.Cleanup == "confirmed")
            {
                try { WorkspaceRun.Confirm(job, id); }
                catch (Exception ex) { (code, problem) = (70, Problem(ex)); }
            }
            var input = reply?.Result is { ValueKind: JsonValueKind.Object } payload && payload.TryGetProperty("input", out var fingerprint)
                ? (JsonElement?)fingerprint : null;
            var sdkReferences = SdkJobWait.References(job);
            var recovery = sdkReferences.Length == 0 ? null : new { sdkJobReferences = sdkReferences, automaticRetry = false,
                reviewRequired = code != 0, instruction = "Inspect the SDK job records, native lifecycle and pending markers before another native dispatch. Process cleanup alone does not establish native close." };
            WriteNew(receipt, new { jobId = id, request.Operation, sourcePath = request.Input, input, exitCode = code,
                error = problem, worker.Cleanup, worker.ResourceId, worker.Child, workerExitCode = worker.ExitCode,
                invocation = File.Exists(Path.Combine(job, "invocation.json")) ? Path.Combine(job, "invocation.json") : null,
                steps = Directory.Exists(Path.Combine(job, "steps")) ? Path.Combine(job, "steps") : null,
                recovery, finishedUtc = DateTimeOffset.UtcNow });
            return Report(code, request.Operation, id, receipt, worker.Cleanup, code == 0 ? reply?.Result : null, problem, recovery);
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static int RunWorker(string operation, string input, int timeoutSeconds)
    {
        try
        {
            var job = Environment.GetEnvironmentVariable(JobEnvironment);
            if (job is null || !Path.IsPathFullyQualified(job) ||
                !string.Equals(Path.GetDirectoryName(Path.GetFullPath(job)), Path.Combine(StateRoot, "jobs"), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(job, "intent.json")))
                throw new OperationException("worker_invocation_denied", "Invoke the public CLI operation.");
            var driver = new WindowsResourceProcessDriver();
            var identity = driver.CurrentIdentity();
            var ledger = new ResourceLedger(Path.Combine(job, "registry"), driver);
            if (!ledger.Records().Any(record => record.AppId == "rockwell-engineering-cli" && record.State == "active" &&
                record.Containment == "windows_job" && record.Process == identity && driver.State(record.Owner) == "alive"))
                throw new OperationException("worker_invocation_denied", "The worker has no exact active CLI owner.");
            var result = operation switch
            {
                "l5x-summary" => L5xSummary.Read(input),
                "l5x-inspect" => L5xWorkflow.Inspect(input),
                "l5x-preview" => L5xWorkflow.Preview(job, input),
                "l5x-apply" => L5xWorkflow.Apply(job, input),
                "sdk-read-export-plan" => SdkReadExport.Plan(input),
                "sdk-offline" or "sdk-read-export" => NativePilot.Run(job, input, operation == "sdk-read-export"),
                "run" => WorkspaceRun.ExecuteAsync(job, input, timeoutSeconds, ReadPinnedRequest(Console.In)).GetAwaiter().GetResult(),
                _ => throw new OperationException("unknown_operation", "Unsupported worker operation.")
            };
            Print(new WorkerReply(JsonSerializer.SerializeToElement(result, Json), null));
            return 0;
        }
        catch (Exception ex) { Print(new WorkerReply(null, Problem(ex))); return 1; }
    }

    internal static void WriteNew(string path, object value)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, value, Json);
        file.Flush(true);
    }

    internal static byte[] ReadPinnedRequest(TextReader reader)
    {
        const int maximumBytes = 2 * 1024 * 1024, maximumEncoded = (maximumBytes + 2) / 3 * 4;
        var encoded = new StringBuilder();
        var buffer = new char[4096];
        while (reader.Read(buffer, 0, buffer.Length) is var count && count != 0)
        {
            if (encoded.Length + count > maximumEncoded)
                throw new OperationException("invalid_workspace_request", "The owned workspace request exceeds its limit.");
            encoded.Append(buffer, 0, count);
        }
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded.ToString()); }
        catch (FormatException) { throw new OperationException("invalid_workspace_request", "The owned workspace request is incomplete."); }
        if (bytes.Length is < 1 or > maximumBytes)
            throw new OperationException("invalid_workspace_request", "The owned workspace request has an invalid length.");
        return bytes;
    }

    private static CliProblem Problem(Exception error) => new(error is OperationException op ? op.Code : error switch
    {
        JsonException => "invalid_json", System.Xml.XmlException => "invalid_xml",
        FileNotFoundException or DirectoryNotFoundException => "input_missing", UnauthorizedAccessException => "access_denied",
        IOException => "io_error", _ => "operation_failed"
    }, error.Message.Length > 1000 ? error.Message[..1000] : error.Message);

    private static int Report(int exitCode, string operation, string? jobId, string? receipt, string cleanup, JsonElement? result, CliProblem? error, object? recovery = null)
    {
        if (error is not null) Console.Error.WriteLine($"{error.Code}: {error.Message}");
        Print(new { schemaVersion = 1, version = Version, operation, jobId,
            status = exitCode == 0 ? "succeeded" : "error", exitCode, result, error, cleanup, receipt, recovery });
        return exitCode;
    }

    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));
}
