using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Studio5000.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProtectedDependencyExportPlanRequest(MergeSnapshot Source, string[] AoiNames);
public sealed record ProtectedDependencyExportPlan(ComparisonSnapshot Source, Artifact Snapshot, SdkRunRequest SdkRequest, string Boundary);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProtectedDependencyExport(string AoiName, string JobId, string ArtifactId, string ArtifactSha256);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProtectedDependencyPreviewRequest(MergeSnapshot Source, ProtectedDependencyExport[] Exports);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProtectedDependencyRecordRequest(ProtectedDependencyPreviewRequest Preview, string ExpectedReviewSha256);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProtectedDependencyEvidenceRef(string ArtifactId, string Sha256);
public sealed record ProtectedDependencyDefinition(string Kind, string Name, string SourceContractSha256, string NativeDefinitionSha256);
public sealed record ProtectedDependencyRoot(string AoiName, string SourceElementSha256, string NativeExportElementSha256,
    string SourceRelationship, string NativeInputSha256, string? NativeReadbackSha256, string JobId, string JobEvidenceSha256,
    string? SdkAssembly, Artifact NativeExport, ProtectedDependencyDefinition[] Dependencies);
public sealed record ProtectedDependencyPreview(ComparisonSnapshot Source, bool CanIssue, ProtectedDependencyRoot[] Roots,
    StructuralFinding[] Findings, string ReviewSha256, string Boundary);
public sealed record ProtectedDependencyEvidence(Artifact Receipt, Artifact SourceSnapshot, ProtectedDependencyPreview Review);
internal sealed record ProtectedDependencyDocument(int SchemaVersion, string Kind, string Producer, string ToolVersion,
    ProtectedDependencyPreviewRequest Request, ProtectedDependencyPreview Review, Artifact SourceSnapshot);
internal sealed record ProtectedDependencyIssued(string Sha256, string ReviewSha256);
internal sealed record ResolvedProtectedDependency(string AoiName, string SourceElementSha256, ProtectedDependencyDefinition[] Dependencies, string ReceiptSha256);

internal static class ProtectedDependencyPolicy
{
    internal const string Boundary = "Reviewed native dependency context for explicitly bound protected AOIs. Requires a completed offline SDK partial-export job, exact retained input L5X or same-job full ACD readback, Dependencies/ForceProtectedEncoding context and matching source type/library contracts. Export ciphertext may be re-encoded and is never compared as identity. Reuse binds original source opaque bytes and all manifested contracts. Library closure only: hidden tag/external/runtime bindings remain unproved. No native dispatch, decryption, general override or safety/deployment acceptance.";
    internal static XElement Controller(XDocument doc) => doc.Root?.Element("Controller") ?? throw Error("A named Controller is required.");
    internal static Dictionary<string, XElement> Types(XDocument doc) => Index(Controller(doc).Elements("DataTypes").Elements("DataType"));
    internal static Dictionary<string, XElement> Aois(XDocument doc) => Index(Controller(doc).Elements("AddOnInstructionDefinitions").Elements().Where(e => e.Name == "AddOnInstructionDefinition" || (string?)e.Attribute("EncodedType") == "AddOnInstructionDefinition"));
    private static Dictionary<string, XElement> Index(IEnumerable<XElement> nodes) => LogixPreservation.Index(nodes, n => (string?)n.Attribute("Name") ?? throw Error("A dependency definition has no name."));
    internal static bool Protected(XElement node) => node.Name == "EncodedData" || node.Descendants("EncodedData").Any();
    internal static string Contract(XElement node)
    { var copy = new XElement(node); copy.Attribute("Use")?.Remove(); return node.Name == "DataType" ? StructuralEditing.LayoutFingerprint(copy) : LogixPreservation.Fingerprint(copy)!; }
    internal static void FullSource(XDocument doc)
    {
        if ((string?)doc.Root?.Attribute("TargetType") != "Controller" || (string?)doc.Root?.Attribute("ContainsContext") != "false" || string.IsNullOrWhiteSpace((string?)doc.Root.Attribute("SoftwareRevision")))
            throw Error("Bind an exact full saved controller L5X with software revision and ContainsContext=false.");
    }
    internal static OperationException Error(string detail) => new("protected_dependency_evidence", detail);
}

public sealed partial class ProjectService
{
    public async Task<ProtectedDependencyExportPlan> PlanProtectedDependencyExportAsync(ProtectedDependencyExportPlanRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var input = PreservationSource(request.Source); var doc = ModuleConnectionEditing.Parse(input.Bytes); ProtectedDependencyPolicy.FullSource(doc); var aois = ProtectedDependencyPolicy.Aois(doc);
            if (request.AoiNames is null || request.AoiNames.Length is < 1 or > 16 || request.AoiNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.AoiNames.Length) throw ProtectedDependencyPolicy.Error("Select 1..16 unique exact protected AOI names.");
            var calls = new List<SdkCall> { new("OpenLogixProjectAsync", []) };
            foreach (var name in request.AoiNames)
            {
                WatchValidation.Identifier(name);
                if (!aois.TryGetValue(name, out var aoi) || !ProtectedDependencyPolicy.Protected(aoi)) throw ProtectedDependencyPolicy.Error("Select an existing opaque AOI; no plaintext source is exported by this plan.");
                calls.Add(new("PartialExportToXmlFileAsync", new() { ["xPath"] = JsonSerializer.SerializeToElement("Controller/AddOnInstructionDefinitions/AddOnInstructionDefinition[@Name='" + name + "']"),
                    ["filePath"] = JsonSerializer.SerializeToElement("dependency-" + (calls.Count - 1).ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + ".l5x") }));
            }
            var snapshot = StructuralArtifact(input.Project.Id, "l5x", input.Bytes);
            var sdk = new SdkRunRequest(calls.ToArray(), ExpectedSha256: snapshot.Sha256, SourceArtifactId: snapshot.Id);
            SdkCatalog.ValidateCalls(sdk.Calls); NativeExecutionPolicy.ValidateCalls(NativeExecutionPolicy.LocalProject, sdk.Calls);
            return new(new(input.Project.Id, input.Project.Name, input.Project.Revision, input.Project.CurrentSha256), snapshot, sdk, ProtectedDependencyPolicy.Boundary);
        }
        finally { gate.Release(); }
    }

    private async Task<(ProtectedDependencyPreview Review, byte[] Source)> BuildProtectedDependencyEvidenceAsync(ProtectedDependencyPreviewRequest request, CancellationToken ct,
        (ProjectInfo Project, byte[] Bytes)? frozenSource = null)
    {
        if (request.Exports is null || request.Exports.Length is < 1 or > 16 || request.Exports.Any(e => e is null || !StructuralEditing.Hash(e.ArtifactSha256)) || request.Exports.Select(e => e.AoiName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Exports.Length)
            throw ProtectedDependencyPolicy.Error("Select 1..16 unique protected AOIs and retained SDK job/export artifact IDs/hashes.");
        var source = frozenSource ?? PreservationSource(request.Source); var doc = ModuleConnectionEditing.Parse(source.Bytes); ProtectedDependencyPolicy.FullSource(doc);
        var lexical = new XmlSourceDocument(source.Bytes, doc); var aois = ProtectedDependencyPolicy.Aois(doc); var types = ProtectedDependencyPolicy.Types(doc);
        var roots = new List<ProtectedDependencyRoot>(); var findings = new List<StructuralFinding>(); long total = 0;
        var jobCache = new Dictionary<string, (SdkJob Job, string Hash, string Relationship, string InputHash, string? Readback)>();
        void Block(string name, string detail)
        {
            if (findings.Count >= 200) throw ProtectedDependencyPolicy.Error("Native context review exceeds 200 findings; no partial proof is issued.");
            findings.Add(new("blocked", "native-dependency-evidence", name, detail));
        }
        foreach (var export in request.Exports)
        {
            ct.ThrowIfCancellationRequested(); WatchValidation.Identifier(export.AoiName);
            if (!aois.TryGetValue(export.AoiName, out var original) || !ProtectedDependencyPolicy.Protected(original)) { Block(export.AoiName, "The selected source has no exact opaque AOI definition."); continue; }
            var jobId = Id(export.JobId);
            if (!jobCache.TryGetValue(jobId, out var cached))
            {
                var jobPath = SdkJobPath(jobId); RejectLinks(jobPath);
                if (new FileInfo(jobPath).Length > 2 * 1024 * 1024) throw ProtectedDependencyPolicy.Error("Native job metadata exceeds 2 MiB.");
                var job = SdkJob(jobId); var jobHash = Hash(JsonSerializer.SerializeToUtf8Bytes(job, Json));
                if (job.Id != jobId || job.Status != "succeeded" || job.Error is not null || job.ControllerEffectsPossible || job.ControllerTargetId is not null || !StructuralEditing.Hash(job.SourceSha256) || job.Methods.Length < 2 || job.Methods[0] != "OpenLogixProjectAsync" ||
                    job.Methods.Count(m => m == "OpenLogixProjectAsync") != 1 || !job.Methods.Contains("PartialExportToXmlFileAsync") || job.Methods.Any(m => m is not ("OpenLogixProjectAsync" or "PartialExportToXmlFileAsync" or "SaveAsAsync")) ||
                    job.Steps.Length != job.Methods.Length || job.Steps.Where((s, i) => s.Index != i || s.Method != job.Methods[i] || s.Status != "succeeded").Any() || job.Events.Any(e => e.Kind == "error") || job.SdkAssembly is not null && job.SdkAssembly != SdkCatalog.Surface.Assembly)
                { Block(export.AoiName, "Require a completed closed offline open/partial-export/save-only job, without errors, controller effects or ambiguous SDK identity."); continue; }
                var work = Path.Combine(SdkDirectory, jobId, "work"); var l5x = Path.Combine(work, "input.l5x"); var acd = Path.Combine(work, "input.acd");
                var format = File.Exists(l5x) && !File.Exists(acd) ? "l5x" : File.Exists(acd) && !File.Exists(l5x) ? "acd" : "unknown";
                if (format == "unknown") { Block(export.AoiName, "One retained exact native input snapshot is required."); continue; }
                var input = format == "l5x" ? l5x : acd; RejectLinks(input);
                if (new FileInfo(input).Length is < 1 or > 500L * 1024 * 1024 || await SdkFileHashAsync(input, ct) != job.SourceSha256) { Block(export.AoiName, "Native input no longer matches its recorded exact saved source hash."); continue; }
                string relationship; string? readback = null;
                if (format == "l5x")
                {
                    if (job.SourceSha256 != source.Project.CurrentSha256) { Block(export.AoiName, "The selected L5X is not the exact SDK input. Metadata or ciphertext similarity is insufficient."); continue; }
                    relationship = "exact-job-input-l5x";
                }
                else
                {
                    var outputs = job.Artifacts.Where(a => a.Format == "l5x" && a.Sha256 == source.Project.CurrentSha256).ToArray();
                    if (outputs.Length != 1 || Download(outputs[0].Id).Artifact != outputs[0]) { Block(export.AoiName, "ACD input requires selecting its exact same-job full native L5X readback artifact, not an unrelated saved L5X."); continue; }
                    relationship = "same-job-acd-native-l5x-readback"; readback = outputs[0].Sha256;
                }
                if (job.SdkAssembly is null) { Block(export.AoiName, "Legacy job did not record its SDK assembly. Retain its evidence, but use a separately authorized source-bound job with recorded SDK identity before issuing a receipt."); continue; }
                cached = (job, jobHash, relationship, job.SourceSha256!, readback); jobCache.Add(jobId, cached);
            }
            var artifact = cached.Job.Artifacts.SingleOrDefault(a => a.Id == export.ArtifactId);
            if (artifact is null || artifact.Format != "l5x" || !artifact.Sha256.Equals(export.ArtifactSha256, StringComparison.OrdinalIgnoreCase) || artifact.ProjectId != (cached.Job.ResultProject?.Id ?? cached.Job.Id)) { Block(export.AoiName, "Select the exact native L5X output recorded by this SDK job."); continue; }
            var stored = Download(artifact.Id);
            if (stored.Artifact != artifact || artifact.Bytes > 64 * 1024 * 1024 || (total += artifact.Bytes) > 128 * 1024 * 1024) throw ProtectedDependencyPolicy.Error("Native context hashes/metadata changed or XML exceeds 64 MiB each / 128 MiB total.");
            var nativeBytes = File.ReadAllBytes(stored.Path); var native = ModuleConnectionEditing.Parse(nativeBytes); var nativeLexical = new XmlSourceDocument(nativeBytes, native);
            var flags = ((string?)native.Root!.Attribute("ExportOptions") ?? "").Split([' ', '\r', '\n', '\t', ','], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nativeAois = ProtectedDependencyPolicy.Aois(native); var nativeTypes = ProtectedDependencyPolicy.Types(native);
            if (nativeAois.Count + nativeTypes.Count > 2048) throw ProtectedDependencyPolicy.Error("Native dependency manifest exceeds 2048 definitions per root.");
            if ((string?)native.Root.Attribute("TargetType") != "AddOnInstructionDefinition" || (string?)native.Root.Attribute("TargetName") != export.AoiName || (string?)native.Root.Attribute("ContainsContext") != "true" ||
                !flags.Contains("Dependencies") || !flags.Contains("ForceProtectedEncoding") || ProtectedDependencyPolicy.Controller(native).Elements("DataTypes").Count() != 1 ||
                ((string?)native.Root.Attribute("SoftwareRevision"))?.Split('.')[0] != ((string?)doc.Root!.Attribute("SoftwareRevision"))?.Split('.')[0] ||
                !nativeAois.TryGetValue(export.AoiName, out var encoded) || !ProtectedDependencyPolicy.Protected(encoded))
            { Block(export.AoiName, "Require a matching major-revision AOI-target export with Dependencies, ForceProtectedEncoding, explicit context/types and opaque target payload."); continue; }
            var dependencies = new List<ProtectedDependencyDefinition>(); var start = findings.Count;
            foreach (var (name, definition) in nativeTypes)
            {
                if (!types.TryGetValue(name, out var originalType) || ProtectedDependencyPolicy.Contract(originalType) != ProtectedDependencyPolicy.Contract(definition)) Block(name, "Native dependent type is absent or its layout differs from the bound source; native normalization is not silently accepted.");
                else dependencies.Add(new("udt", name, ProtectedDependencyPolicy.Contract(originalType), nativeLexical.Fingerprint(definition)));
            }
            foreach (var (name, definition) in nativeAois)
            {
                if (!aois.TryGetValue(name, out var originalAoi)) { Block(name, "Native dependent AOI is missing from the bound source."); continue; }
                if (ProtectedDependencyPolicy.Protected(definition) != ProtectedDependencyPolicy.Protected(originalAoi) ||
                    !ProtectedDependencyPolicy.Protected(definition) && ProtectedDependencyPolicy.Contract(originalAoi) != ProtectedDependencyPolicy.Contract(definition))
                    Block(name, "Readable dependent AOI contract differs from the bound source.");
                else dependencies.Add(new("aoi", name, ProtectedDependencyPolicy.Protected(originalAoi) ? lexical.Fingerprint(originalAoi) : ProtectedDependencyPolicy.Contract(originalAoi), nativeLexical.Fingerprint(definition)));
            }
            foreach (var type in ProtectedDependencyPolicy.Controller(native).DescendantsAndSelf().Attributes("DataType").Select(a => a.Value).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!ProgramImportPackaging.IsBuiltInType(type) && !nativeTypes.ContainsKey(type) && !nativeAois.ContainsKey(type)) Block(type, "Visible native dependency context is incomplete; a concrete dependent definition is missing.");
            foreach (var code in native.Descendants().Where(e => e.Name.LocalName is "Text" or "Line"))
            {
                if (code.Value.Length > 1024 * 1024) throw ProtectedDependencyPolicy.Error("Native readable source exceeds 1 MiB per unit.");
                var masked = new string(TagReferences.MaskSource(code.Value, _ => Block(export.AoiName, "Readable native source could not be lexically assessed.")));
                foreach (System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(masked,
                    @"(?<![A-Za-z0-9_])([A-Za-z_][A-Za-z0-9_]*)\s*\(", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)))
                    if (aois.ContainsKey(call.Groups[1].Value) && !nativeAois.ContainsKey(call.Groups[1].Value)) Block(call.Groups[1].Value, "Readable native AOI calls a source AOI omitted from its dependency context.");
            }
            if (dependencies.Count > 2048 || roots.Sum(r => r.Dependencies.Length) + dependencies.Count > 8192) throw ProtectedDependencyPolicy.Error("Native dependency manifest exceeds 2048 definitions per root / 8192 total.");
            if (findings.Skip(start).Any(f => f.Status == "blocked")) continue;
            roots.Add(new(export.AoiName, lexical.Fingerprint(original), nativeLexical.Fingerprint(encoded), cached.Relationship, cached.InputHash, cached.Readback, jobId, cached.Hash, cached.Job.SdkAssembly, artifact, dependencies.ToArray()));
        }
        var report = new ProtectedDependencyPreview(new(source.Project.Id, source.Project.Name, source.Project.Revision, source.Project.CurrentSha256), roots.Count == request.Exports.Length && findings.All(f => f.Status != "blocked"), roots.ToArray(), findings.ToArray(), "", ProtectedDependencyPolicy.Boundary);
        report = report with { ReviewSha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(new { request, report }, Json)) };
        if (JsonSerializer.SerializeToUtf8Bytes(report, Json).Length > 8 * 1024 * 1024) throw ProtectedDependencyPolicy.Error("Complete native dependency evidence exceeds 8 MiB; no partial proof is issued.");
        return (report, source.Bytes);
    }

    public async Task<ProtectedDependencyPreview> PreviewProtectedDependenciesAsync(ProtectedDependencyPreviewRequest request, CancellationToken ct = default)
    { await gate.WaitAsync(ct); try { return (await BuildProtectedDependencyEvidenceAsync(request, ct)).Review; } finally { gate.Release(); } }
    public async Task<ProtectedDependencyEvidence> RecordProtectedDependenciesAsync(ProtectedDependencyRecordRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var built = await BuildProtectedDependencyEvidenceAsync(request.Preview, ct);
            if (!built.Review.ReviewSha256.Equals(request.ExpectedReviewSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Native source/export/job evidence changed; review again.");
            if (!built.Review.CanIssue) throw ProtectedDependencyPolicy.Error("Resolve all source/context findings before issuing a dependency receipt.");
            var source = StructuralArtifact(built.Review.Source.Id, "l5x", built.Source);
            var document = new ProtectedDependencyDocument(1, "protected_dependency_evidence", WorkspaceIdentity.Name, WorkspaceIdentity.Version, request.Preview, built.Review, source);
            var receipt = StructuralArtifact(built.Review.Source.Id, "json", JsonSerializer.SerializeToUtf8Bytes(document, Json));
            Save(Path.Combine(ArtifactDir(receipt.Id), "protected-evidence-issued.json"), new ProtectedDependencyIssued(receipt.Sha256, built.Review.ReviewSha256));
            return new(receipt, source, built.Review);
        }
        finally { gate.Release(); }
    }

    private async Task<ResolvedProtectedDependency[]> ResolveProtectedDependenciesAsync(ProtectedDependencyEvidenceRef[] references,
        byte[] bytes, CancellationToken ct)
    {
        if (references.Length is < 1 or > 16 || references.Any(r => r is null || !StructuralEditing.Hash(r.Sha256)) ||
            references.Select(r => r.ArtifactId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != references.Length)
            throw ProtectedDependencyPolicy.Error("Select 1..16 distinct issued receipt artifact IDs and exact hashes.");
        var doc = ModuleConnectionEditing.Parse(bytes); ProtectedDependencyPolicy.FullSource(doc);
        var lexical = new XmlSourceDocument(bytes, doc); var aois = ProtectedDependencyPolicy.Aois(doc); var types = ProtectedDependencyPolicy.Types(doc);
        var resolved = new Dictionary<string, ResolvedProtectedDependency>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var reference in references)
        {
            ct.ThrowIfCancellationRequested();
            var retained = Download(reference.ArtifactId);
            if (retained.Artifact.Format != "json" || retained.Artifact.Bytes > 8 * 1024 * 1024 || (total += retained.Artifact.Bytes) > 16 * 1024 * 1024 ||
                !retained.Artifact.Sha256.Equals(reference.Sha256, StringComparison.OrdinalIgnoreCase)) throw ProtectedDependencyPolicy.Error("Dependency receipt format/hash/size differs.");
            var issuedPath = Path.Combine(ArtifactDir(reference.ArtifactId), "protected-evidence-issued.json");
            if (!File.Exists(issuedPath)) throw ProtectedDependencyPolicy.Error("Receipt has no retained issuance record.");
            var issued = Read<ProtectedDependencyIssued>(issuedPath);
            var document = JsonSerializer.Deserialize<ProtectedDependencyDocument>(File.ReadAllBytes(retained.Path), Json);
            if (document is null || document.Kind != "protected_dependency_evidence" || document.SchemaVersion != 1 || document.Producer != WorkspaceIdentity.Name ||
                issued.Sha256 != retained.Artifact.Sha256 || issued.ReviewSha256 != document.Review.ReviewSha256 || !document.Review.CanIssue)
                throw ProtectedDependencyPolicy.Error("Select an issued passing native dependency receipt.");
            var frozen = Download(document.SourceSnapshot.Id);
            if (frozen.Artifact != document.SourceSnapshot || frozen.Artifact.ProjectId != document.Review.Source.Id ||
                document.Request.Source.ProjectId != document.Review.Source.Id || document.Request.Source.Sha256 != document.Review.Source.Sha256 ||
                frozen.Artifact.Sha256 != document.Review.Source.Sha256 || frozen.Artifact.Format != "l5x" || frozen.Artifact.Bytes > 64 * 1024 * 1024)
                throw ProtectedDependencyPolicy.Error("Frozen original source snapshot differs from the issued receipt.");
            var identity = new ProjectInfo(document.Review.Source.Id, document.Review.Source.Name, frozen.Path,
                frozen.Artifact.Sha256, frozen.Artifact.Sha256, document.Review.Source.Revision, DateTimeOffset.UnixEpoch);
            // Revalidate the original source/job/artifacts, independently of later edits to the managed project.
            var rebuilt = await BuildProtectedDependencyEvidenceAsync(document.Request, ct, (identity, File.ReadAllBytes(frozen.Path)));
            if (!rebuilt.Review.CanIssue || rebuilt.Review.ReviewSha256 != document.Review.ReviewSha256)
                throw ProtectedDependencyPolicy.Error("Retained native job, source or context evidence changed after review.");
            foreach (var root in rebuilt.Review.Roots)
            {
                if (!aois.TryGetValue(root.AoiName, out var target) || !ProtectedDependencyPolicy.Protected(target) || lexical.Fingerprint(target) != root.SourceElementSha256)
                    throw ProtectedDependencyPolicy.Error("Current original protected AOI bytes differ from the source-bound receipt; exported ciphertext is not an identity substitute.");
                foreach (var dependency in root.Dependencies)
                {
                    var index = dependency.Kind == "udt" ? types : aois;
                    if (!index.TryGetValue(dependency.Name, out var definition) ||
                        (ProtectedDependencyPolicy.Protected(definition) ? lexical.Fingerprint(definition) : ProtectedDependencyPolicy.Contract(definition)) != dependency.SourceContractSha256)
                        throw ProtectedDependencyPolicy.Error("A manifested source type/AOI contract is missing or changed: " + dependency.Name);
                }
                if (!resolved.TryAdd(root.AoiName, new(root.AoiName, root.SourceElementSha256, root.Dependencies, reference.Sha256)))
                    throw ProtectedDependencyPolicy.Error("Duplicate/conflicting evidence for one protected root is not accepted.");
            }
        }
        return resolved.Values.ToArray();
    }
}
