using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record BoundL5kPlanRequest(string SourcePath, string ExpectedSha256, int MajorRevision);
public sealed record BoundL5kSource(string Path, string Format, int MajorRevision);
public sealed record BoundL5kPlan(BoundL5kSource Source, string SourceSha256, SdkRunRequest SdkRequest, string Boundary);
public sealed record BoundL5kReviewRequest(string JobId, string SelectedL5xSha256);
public sealed record BoundL5kIssue(string Code, string Detail);
public sealed record BoundL5kReview(BoundL5kReviewRequest Request, BoundL5kSource Source, string SourceSha256,
    string SdkAssembly, DateTimeOffset JobCreatedAt, string JobEvidenceSha256, Artifact NativeL5x, Artifact NativeL5k, string Selection,
    bool? OriginalL5xEquivalent, ProjectDifference[] Differences, BoundL5kIssue[] Issues, bool CanBind,
    string ReviewSha256, string Boundary);
public sealed record BoundL5kRecordRequest(BoundL5kReviewRequest Preview, string ExpectedReviewSha256);
public sealed record BoundL5kReceipt(int SchemaVersion, string Kind, BoundL5kReview Review,
    [property: JsonPropertyName("selected_l5x_sha256"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SelectedL5xSha256,
    [property: JsonPropertyName("derived_l5k_sha256")] string DerivedL5kSha256);
public sealed record GmL5kBinding(string Path, string Sha256,
    [property: JsonPropertyName("source_pointer")] string SourcePointer,
    [property: JsonPropertyName("output_pointer")] string OutputPointer);
public sealed record GmL5kDerivedInput(string Path, string Sha256, GmL5kBinding Binding);
public sealed record BoundL5kRecorded(Artifact Receipt, Artifact NativeL5x, Artifact NativeL5k,
    [property: JsonPropertyName("derived_input")] GmL5kDerivedInput? DerivedInput);

public static class BoundL5kExport
{
    internal const int MaximumBytes = 64 * 1024 * 1024;
    internal const string L5xOutput = "native-readback.l5x", L5kOutput = "native-export.l5k";
    internal const string Boundary = "Offline native SaveAs pair from one private SDK snapshot. Source, native L5X and native L5K hashes are distinct evidence. The binding covers the explicitly selected L5X and preserves native L5K bytes. GM validates utility-specific parser requirements separately.";
    internal static readonly string[] Methods = ["OpenLogixProjectAsync", "SaveAsAsync", "SaveAsAsync"];

    internal static SdkRunRequest Plan(BoundL5kPlanRequest plan) => new([
        new(Methods[0], []),
        new(Methods[1], new() { ["savePath"] = JsonSerializer.SerializeToElement(L5xOutput), ["detailedL5x"] = JsonSerializer.SerializeToElement(true) }),
        new(Methods[2], new() { ["savePath"] = JsonSerializer.SerializeToElement(L5kOutput) })
    ], ExpectedSha256: plan.ExpectedSha256, SourcePath: plan.SourcePath, BoundL5kMajorRevision: plan.MajorRevision);

    internal static BoundL5kSource? ValidateRequest(SdkRunRequest request)
    {
        if (request.BoundL5kMajorRevision is not { } major) return null;
        if (request.SourcePath is not { } path || !Path.IsPathFullyQualified(path) ||
            request.ProjectId is not null || request.SourceArtifactId is not null || request.ControllerTargetId is not null ||
            request.InputFiles is { Count: > 0 } || major is < 1 or > 99)
            throw Policy("Use one absolute saved ACD or L5X path, its exact SHA-256 and the reviewed Logix major revision. Controller targets and import files are outside this profile.");
        RequireSha(request.ExpectedSha256);
        path = Path.GetFullPath(path);
        var format = Path.GetExtension(path).ToLowerInvariant();
        if (format is not (".acd" or ".l5x")) throw Policy("Use a saved ACD or a full saved controller L5X.");
        ValidateCalls(request.Calls, null, L5xOutput, L5kOutput);
        return new(path, format[1..], major);
    }

    // The bridge receives only private, remapped paths and enforces the same exact operation shape.
    public static void ValidateWorkerRequest(SdkBridgeRequest request)
    {
        if (request.ExecutionProfile != NativeExecutionPolicy.LocalProject || request.CommunicationsPath is not null ||
            request.SourcePath is not { } source || !Path.IsPathFullyQualified(request.WorkDirectory) ||
            !(source == Path.Combine(request.WorkDirectory, "input.acd") || source == Path.Combine(request.WorkDirectory, "input.l5x")))
            throw Policy("The export bridge requires an offline private job snapshot.");
        ValidateCalls(request.Calls, source, Path.Combine(request.WorkDirectory, L5xOutput), Path.Combine(request.WorkDirectory, L5kOutput));
    }

    private static void ValidateCalls(SdkCall[] calls, string? source, string l5x, string l5k)
    {
        bool SaveMatches(SdkCall call, string expected, bool detailed) =>
            call.Arguments.TryGetValue("savePath", out var path) && path.ValueKind == JsonValueKind.String && path.GetString() == expected &&
            call.Arguments.Keys.All(k => k is "savePath" or "force" or "detailedL5x") &&
            (!call.Arguments.TryGetValue("force", out var force) || force.ValueKind == JsonValueKind.False) &&
            (call.Arguments.TryGetValue("detailedL5x", out var flag) ? flag.ValueKind == (detailed ? JsonValueKind.True : JsonValueKind.False) : !detailed);
        if (calls is null || !calls.Select(c => c.Method).SequenceEqual(Methods) ||
            (source is null ? calls[0].Arguments.Count != 0
                : calls[0].Arguments.Count != 1 || !calls[0].Arguments.TryGetValue("projectFilePath", out var input) ||
                  input.ValueKind != JsonValueKind.String || input.GetString() != source) ||
            !SaveMatches(calls[1], l5x, true) || !SaveMatches(calls[2], l5k, false))
            throw Policy("Use the exact offline calls from studio_bound_l5k_plan.");
    }

    internal static XDocument ReadFullController(string path, int major) => ReadFullController(ReadBytes(path), major);

    internal static XDocument ReadFullController(byte[] bytes, int major)
    {
        var doc = XmlProjects.ParseForAnalysis(bytes);
        var root = doc.Root!;
        var controllers = root.Elements("Controller").Take(2).ToArray();
        var controller = controllers.Length == 1 ? controllers[0] : null;
        var name = (string?)controller?.Attribute("Name");
        if ((string?)root.Attribute("TargetType") != "Controller" || (string?)root.Attribute("ContainsContext") == "true" ||
            string.IsNullOrWhiteSpace(name) || (root.Attribute("TargetName") is { } target && target.Value != name))
            throw Policy("Use a full named controller L5X export, with no partial target/context.");
        var wanted = major.ToString(CultureInfo.InvariantCulture);
        if (((string?)root.Attribute("SoftwareRevision"))?.Split('.')[0] != wanted ||
            (controller!.Attribute("MajorRev") is { } actual && actual.Value != wanted))
            throw new OperationException("bound_l5k_revision", "The full L5X software/controller revision differs from the reviewed Logix major revision.");
        return doc;
    }

    internal static byte[] ReadBytes(string path)
    {
        ProjectService.RejectLinks(path);
        using var stream = File.OpenRead(path);
        if (stream.Length is < 1 or > MaximumBytes)
            throw new OperationException("bound_l5k_limit", "Review supports nonempty L5X/L5K files up to 64 MiB.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    internal static void RequireSha(string? hash)
    {
        if (hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit)) throw Policy("Supply an exact 64-hex SHA-256.");
    }

    internal static OperationException Policy(string message) => new("bound_l5k_policy", message);
}

public sealed partial class ProjectService
{
    public async Task<BoundL5kPlan> PlanBoundL5kAsync(BoundL5kPlanRequest request, CancellationToken ct = default)
    {
        var sdk = BoundL5kExport.Plan(request);
        var source = BoundL5kExport.ValidateRequest(sdk)!;
        var path = AllowedSource(source.Path);
        if (new FileInfo(path).Length is < 1 or > 500L * 1024 * 1024)
            throw new OperationException("file_too_large", "Use a nonempty SDK input up to 500 MiB.");
        string hash;
        if (source.Format == "l5x")
        {
            var bytes = BoundL5kExport.ReadBytes(path);
            hash = Hash(bytes);
            BoundL5kExport.ReadFullController(bytes, source.MajorRevision);
        }
        else hash = await SdkFileHashAsync(path, ct);
        if (!hash.Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new OperationException("stale_revision", "The saved source differs from the reviewed SHA-256.");
        return new(source, hash, sdk with { SourcePath = path, ExpectedSha256 = hash }, BoundL5kExport.Boundary);
    }

    public async Task<BoundL5kReview> PreviewBoundL5kAsync(BoundL5kReviewRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return (await BuildBoundL5kReviewAsync(request, ct)).Review; }
        finally { gate.Release(); }
    }

    public async Task<BoundL5kRecorded> RecordBoundL5kAsync(BoundL5kRecordRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var (review, l5kPath) = await BuildBoundL5kReviewAsync(request.Preview, ct);
            if (!review.ReviewSha256.Equals(request.ExpectedReviewSha256, StringComparison.OrdinalIgnoreCase))
                throw new OperationException("stale_revision", "The job, selected input or preservation findings changed. Review the current result.");
            var manifest = new BoundL5kReceipt(1, review.CanBind ? "bound_native_l5k" : "unbound_native_l5k_evidence", review,
                review.CanBind ? review.Request.SelectedL5xSha256 : null, review.NativeL5k.Sha256);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
            var id = Guid.NewGuid().ToString("N");
            var dir = ArtifactDir(id);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "project.json");
            AtomicBytes(path, bytes);
            var receipt = new Artifact(id, review.Request.JobId, "json", Hash(bytes), bytes.Length, BoundL5kExport.Boundary);
            Save(Path.Combine(dir, "artifact.json"), receipt);
            var derived = review.CanBind ? new GmL5kDerivedInput(l5kPath, review.NativeL5k.Sha256,
                new(path, receipt.Sha256, "/selected_l5x_sha256", "/derived_l5k_sha256")) : null;
            return new(receipt, review.NativeL5x, review.NativeL5k, derived);
        }
        finally { gate.Release(); }
    }

    private async Task<(BoundL5kReview Review, string L5kPath)> BuildBoundL5kReviewAsync(BoundL5kReviewRequest request, CancellationToken ct)
    {
        BoundL5kExport.RequireSha(request.SelectedL5xSha256);
        request = request with { JobId = Id(request.JobId), SelectedL5xSha256 = request.SelectedL5xSha256.ToLowerInvariant() };
        var job = SdkJob(request.JobId);
        if (job.Id != request.JobId || job.Status != "succeeded" || job.BoundL5kSource is not { } source ||
            source.Format is not ("acd" or "l5x") || job.ControllerEffectsPossible || job.ControllerTargetId is not null ||
            job.SourceSha256 is null || job.SdkAssembly != SdkCatalog.Surface.Assembly ||
            !job.Methods.SequenceEqual(BoundL5kExport.Methods) || job.Steps.Length != BoundL5kExport.Methods.Length ||
            job.Steps.Where((step, index) => step.Index != index || step.Method != BoundL5kExport.Methods[index] || step.Status != "succeeded").Any() ||
            job.Artifacts.Length != 2 || job.Artifacts.Count(a => a.Format == "l5x") != 1 || job.Artifacts.Count(a => a.Format == "l5k") != 1)
            throw new OperationException("bound_l5k_provenance", "Select a completed, closed bound L5K job with the expected offline SDK identity and both retained artifacts.");
        var input = Path.Combine(SdkDirectory, job.Id, "work", "input." + source.Format);
        RejectLinks(input);
        if (new FileInfo(input).Length is < 1 or > 500L * 1024 * 1024)
            throw new OperationException("state_tampered", "The private SDK source snapshot has an invalid length.");
        var originalBytes = source.Format == "l5x" ? BoundL5kExport.ReadBytes(input) : null;
        var inputHash = originalBytes is null ? await SdkFileHashAsync(input, ct) : Hash(originalBytes);
        if (inputHash != job.SourceSha256) throw new OperationException("state_tampered", "The retained private SDK input differs from its job hash.");
        (Artifact Artifact, string Path, byte[] Bytes) Output(string format)
        {
            var recorded = job.Artifacts.Single(a => a.Format == format);
            if (recorded.ProjectId != job.Id) throw new OperationException("bound_l5k_provenance", "An output belongs to a different job.");
            var dir = ArtifactDir(recorded.Id);
            var actual = Read<Artifact>(Path.Combine(dir, "artifact.json"));
            var path = Path.Combine(dir, "project." + format);
            var bytes = BoundL5kExport.ReadBytes(path);
            if (recorded != actual || actual.Bytes != bytes.Length || Hash(bytes) != recorded.Sha256)
                throw new OperationException("artifact_tampered", "A native output no longer matches its job record.");
            return (recorded, path, bytes);
        }
        var l5x = Output("l5x"); var l5k = Output("l5k");
        var selection = source.Format == "l5x" && request.SelectedL5xSha256 == inputHash ? "saved-input-l5x"
            : request.SelectedL5xSha256 == l5x.Artifact.Sha256 ? "native-readback-l5x"
            : throw new OperationException("stale_revision", "Select the exact saved L5X input hash or the exact native readback L5X hash. An ACD hash cannot be the GM source pointer.");
        var issues = new List<BoundL5kIssue>();
        var reportedSdkError = job.Events.Any(e => e.Kind == "error");
        if (reportedSdkError) issues.Add(new("bound_l5k_sdk_diagnostic", "The completed SDK job reported an error event. Inspect its retained diagnostics before using either output."));
        bool? equivalent = null;
        ProjectDifference[] differences = [];
        XDocument? native = null, original = null;
        try { native = BoundL5kExport.ReadFullController(l5x.Bytes, source.MajorRevision); }
        catch (OperationException ex) { issues.Add(new(ex.Code, ex.Message)); }
        catch (System.Xml.XmlException) { issues.Add(new("bound_l5k_xml", "The native L5X is not supported well-formed XML.")); }
        if (native is not null && originalBytes is not null)
        {
            original = BoundL5kExport.ReadFullController(originalBytes, source.MajorRevision);
            equivalent = XNode.DeepEquals(ProjectComparison.Normalize(original.Root!, true), ProjectComparison.Normalize(native.Root!, true));
            if (equivalent == false)
            {
                issues.Add(new("bound_l5k_readback_difference", "The SDK input and native readback differ beyond XML formatting and export timestamps. The original input cannot be bound. A review may instead select the exact native L5X as a new source."));
                var before = new ProjectInfo(job.Id, "SDK input L5X", source.Path, inputHash, inputHash, 0, job.CreatedAt);
                var after = new ProjectInfo(job.Id, "Native L5X readback", l5x.Path, l5x.Artifact.Sha256, l5x.Artifact.Sha256, 0, job.CreatedAt);
                try { differences = ProjectComparison.Compare(before, original, after, native, new(job.Id, inputHash, job.Id, l5x.Artifact.Sha256, Limit: 12, IncludeRungNumbers: true)).Differences; }
                catch (OperationException ex) { issues.Add(new(ex.Code, "Component difference detail is unavailable. Both exact hashes and native artifacts are retained.")); }
            }
        }
        var selected = selection == "saved-input-l5x" ? original : native;
        var annotations = selected is null ? [] : BoundL5kAnnotations.Check(selected, l5k.Bytes);
        var canBind = !reportedSdkError && native is not null && annotations.Length == 0 && (selection == "native-readback-l5x" || equivalent == true);
        issues.AddRange(annotations);
        var review = new BoundL5kReview(request, source, inputHash, job.SdkAssembly, job.CreatedAt, Hash(JsonSerializer.SerializeToUtf8Bytes(job, Json)), l5x.Artifact, l5k.Artifact, selection,
            equivalent, differences, issues.ToArray(), canBind, "", BoundL5kExport.Boundary);
        return (review with { ReviewSha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(review, Json)) }, l5k.Path);
    }
}
