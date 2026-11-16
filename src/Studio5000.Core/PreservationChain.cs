using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Studio5000.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreservationStageReceipt(string ArtifactId, string Sha256);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreservationChainRequest(MergeSnapshot OriginalFresh, MergeSnapshot Candidate,
    PreservationStageReceipt[] Stages, AoiAddressPin[]? AddressPins = null, string? SafetyOverlayReason = null);
public sealed record PreservationChainStage(string SourceProjectId, string SourceSha256, string CandidateProjectId,
    string CandidateSha256, Artifact ApplyReceipt, string ReviewSha256, bool ReviewedSafetyOverlay);
public sealed record PreservationChainReport(ComparisonSnapshot OriginalFresh, ComparisonSnapshot Candidate, bool CanAttest,
    PreservationChainStage[] Stages, int ExistingTagsPreserved, int ProtectedSubtreesPreserved, int AddressPinsPreserved,
    StructuralFinding[] Findings, string ReviewSha256, string Boundary, Artifact? Receipt = null);

public sealed partial class ProjectService
{
    public async Task<PreservationChainReport> ReviewPreservationChainAsync(PreservationChainRequest request, CancellationToken ct = default)
    {
        const string boundary = "Composed saved-file preservation evidence from original fresh export through completed reviewed structural stages. Rebuilds each retained patch and verifies exact links/outputs; all original tag records and hardware/task/force/safety/library subtrees remain byte-exact. Explicit standard RLL AOI numeric address pins are optional and never inferred. Separately authorized safety routines remain REVIEW ONLY and unvalidated; no native/controller acceptance or automatic retry.";
        if (request.Stages is null || request.Stages.Length is < 1 or > 8 || request.Stages.Any(s => s is null || !StructuralEditing.Hash(s.Sha256)) ||
            request.Stages.Select(s => s.ArtifactId).Distinct(StringComparer.Ordinal).Count() != request.Stages.Length || request.SafetyOverlayReason?.Length > 2000)
            throw new OperationException("preservation_chain_policy", "Use 1..8 unique completed structural apply receipt IDs/hashes and an optional bounded explicit safety-overlay reason.");
        if (request.AddressPins is { Length: > 0 }) AoiAddressPreservation.Validate(request.AddressPins);
        await gate.WaitAsync(ct);
        try
        {
            var original = PreservationSource(request.OriginalFresh); var candidate = PreservationSource(request.Candidate);
            var findings = new List<StructuralFinding>(); var stages = new List<PreservationChainStage>();
            var previousId = original.Project.Id; var previousSha = original.Project.CurrentSha256; long bytesReviewed = original.Bytes.Length + candidate.Bytes.Length;
            void Block(string check, string location, string detail)
            {
                if (findings.Count >= 2000) throw new OperationException("preservation_chain_limit", "More than 2000 preservation findings; no partial attestation is emitted.");
                findings.Add(new("blocked", check, location, detail));
            }
            foreach (var selected in request.Stages)
            {
                ct.ThrowIfCancellationRequested();
                var stored = Download(selected.ArtifactId);
                if (stored.Artifact.Format != "json" || stored.Artifact.Bytes > 16 * 1024 * 1024 || !stored.Artifact.Sha256.Equals(selected.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new OperationException("preservation_chain_receipt", "A stage receipt differs from its exact reviewed artifact hash/format.");
                using var document = JsonDocument.Parse(File.ReadAllBytes(stored.Path)); var root = document.RootElement;
                if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "structural_apply" ||
                    !root.TryGetProperty("previewReceipt", out var reference)) throw new OperationException("preservation_chain_receipt", "Only completed retained structural apply receipts can form this chain.");
                var previewArtifact = reference.Deserialize<Artifact>(Json) ?? throw new OperationException("preservation_chain_receipt", "Missing retained preview identity.");
                var retained = StructuralReceipt(previewArtifact.Id, previewArtifact.Sha256);
                var path = Path.Combine(ArtifactDir(previewArtifact.Id), "structural-apply.json");
                if (!File.Exists(path)) throw new OperationException("preservation_chain_incomplete", "A stage has no completed durable apply state; it is never replayed.");
                var state = Read<StructuralApplyState>(path);
                if (state.Status != "applied" || state.Result is not { } applied || applied.Receipt != stored.Artifact ||
                    state.ReviewSha256 != retained.Preview.ReviewSha256 || applied.ReviewSha256 != retained.Preview.ReviewSha256)
                    throw new OperationException("preservation_chain_incomplete", "A stage is incomplete or does not match its retained completed apply; inspect status without replay.");
                _ = Download(applied.Candidate.Id);
                if (retained.Preview.Source.Id != previousId || retained.Preview.Source.Sha256 != previousSha)
                    Block("chain-link", selected.ArtifactId, "Stage source does not exactly match the preceding project/hash, starting with the original fresh export.");
                bytesReviewed += new FileInfo(RevisionFile(retained.Preview.Source.Id, retained.Preview.Source.Revision)).Length;
                if (bytesReviewed > 256L * 1024 * 1024) throw new OperationException("preservation_chain_limit", "Composed review exceeds 256 MiB input bytes; split into independently reviewed chains.");
                try
                {
                    var rebuilt = BuildStructuralEdits(retained.Preview.Request);
                    if (!rebuilt.Preview.CanApply || rebuilt.Preview.ResultSha256 != applied.Candidate.Sha256 || Hash(rebuilt.Bytes) != applied.Candidate.Sha256)
                        Block("stage-readback", selected.ArtifactId, "Current independent patch rebuild does not prove this completed candidate; retain the evidence and review differences.");
                }
                catch (OperationException ex) { Block("stage-readback", selected.ArtifactId, ex.Code + ": " + ex.Message); }
                var safety = retained.Preview.Changes.Any(c => c.Safety);
                if (safety && (string.IsNullOrWhiteSpace(request.SafetyOverlayReason) || retained.Preview.Request.Scope.SafetyHandling != "review-only"))
                    Block("safety-overlay-authorization", selected.ArtifactId, "Safety overlays require a separate explicit reason and a retained review-only safety scope. General merging does not enable them.");
                stages.Add(new(retained.Preview.Source.Id, retained.Preview.Source.Sha256, applied.Project.Id, applied.Candidate.Sha256,
                    stored.Artifact, retained.Preview.ReviewSha256, safety));
                previousId = applied.Project.Id; previousSha = applied.Candidate.Sha256;
            }
            if (previousId != candidate.Project.Id || previousSha != candidate.Project.CurrentSha256)
                Block("chain-result", candidate.Project.Id, "The selected final project/hash is not the final completed stage output.");
            var beforeDoc = ModuleConnectionEditing.Parse(original.Bytes); var afterDoc = ModuleConnectionEditing.Parse(candidate.Bytes);
            foreach (var doc in new[] { beforeDoc, afterDoc })
                if ((string?)doc.Root!.Attribute("TargetType") != "Controller" || (string?)doc.Root.Attribute("ContainsContext") != "false")
                    throw new OperationException("preservation_chain_source", "Composed original-fresh preservation requires full saved controller exports with ContainsContext=false.");
            var before = new XmlSourceDocument(original.Bytes, beforeDoc); var after = new XmlSourceDocument(candidate.Bytes, afterDoc);
            var beforeTags = LogixPreservation.Tags(beforeDoc); var afterTags = LogixPreservation.Tags(afterDoc); var tagCount = 0; var protectedCount = 0; var addressCount = 0;
            foreach (var (name, tag) in beforeTags)
                if (!afterTags.TryGetValue(name, out var observed) || before.Fingerprint(tag) != after.Fingerprint(observed))
                    Block("original-fresh-tag", name, "An existing original-fresh tag record changed or disappeared, including saved L5K/Decorated/Message/native data and annotations.");
                else tagCount++;
            var protectedNodes = beforeDoc.Descendants().Where(e => e.Name.LocalName is "Modules" or "Tasks" or "SafetyInfo" or "ForceData" or "AddOnInstructionDefinition" or "EncodedData")
                .Where(e => !e.Ancestors().Any(a => a.Name.LocalName is "Modules" or "Tasks" or "SafetyInfo" or "AddOnInstructionDefinition" or "EncodedData")).ToArray();
            foreach (var node in protectedNodes)
            {
                var location = XmlProjects.PathOf(node);
                try { if (before.Fingerprint(node) == after.Fingerprint(XmlProjects.Select(afterDoc, location))) protectedCount++; else Block("original-fresh-protected", location, "An original hardware/task/force/safety/library subtree changed bytes."); }
                catch (OperationException) { Block("original-fresh-protected", location, "An original protected subtree is missing or ambiguous."); }
            }
            foreach (var pin in request.AddressPins ?? [])
            {
                try
                {
                    var a = AoiAddressPreservation.Locate(beforeDoc, pin); var b = AoiAddressPreservation.Locate(afterDoc, pin);
                    if (a.Octets.SequenceEqual(b.Octets, StringComparer.Ordinal)) addressCount++;
                    else Block("original-fresh-address", pin.InstanceOperand, "Selected AOI address operands differ from the exact original fresh decimal literals.");
                }
                catch (OperationException ex) { Block("original-fresh-address", pin.InstanceOperand, ex.Code + ": " + ex.Message); }
            }
            findings.Add(new("review", "native-acceptance", "project", "Saved-file preservation only. Native compilation/readback and safety acceptance were not performed."));
            ComparisonSnapshot Snapshot(ProjectInfo p) => new(p.Id, p.Name, p.Revision, p.CurrentSha256);
            var report = new PreservationChainReport(Snapshot(original.Project), Snapshot(candidate.Project), findings.All(f => f.Status != "blocked"), stages.ToArray(), tagCount, protectedCount, addressCount, findings.ToArray(), "", boundary);
            report = report with { ReviewSha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(new { request, report }, Json)) };
            var receipt = StructuralArtifact(candidate.Project.Id, "json", JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, kind = "composed_fresh_preservation", producer = WorkspaceIdentity.Name, toolVersion = WorkspaceIdentity.Version,
                original_fresh_sha256 = original.Project.CurrentSha256, candidate_sha256 = candidate.Project.CurrentSha256, request, report, nativeAcceptance = "not-performed" }, Json));
            return report with { Receipt = receipt };
        }
        finally { gate.Release(); }
    }
}
