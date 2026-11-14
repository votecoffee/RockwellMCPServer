using System.Text.Json;

namespace Studio5000.Core;

public sealed partial class ProjectService
{
    private (ProjectInfo Project, byte[] Bytes, SafetyIoProfiles Profile) SafetyIoSource(SafetyIoBrowseRequest request)
    {
        if (request is null || !StructuralEditing.Hash(request.ExpectedProfileSha256)) throw new OperationException("safety_io_policy", "Supply the exact saved project revision/hash, module and pinned profile hash.");
        var input = StructuralSource(request.ProjectId, request.ExpectedSha256, request.ExpectedRevision);
        var profile = ReadSafetyIoProfile(request.Profile);
        if (!profile.Info.Sha256.Equals(request.ExpectedProfileSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "The AOP profile binding changed; discover/decode again.");
        return (input.Project, input.Bytes, profile);
    }
    public async Task<SafetyIoConfiguration> BrowseSafetyIoAsync(SafetyIoBrowseRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new OperationException("safety_io_policy", "Bind the exact saved source before decoding.");
        using (await EnterWorkspaceAsync(request.ProjectId, ct))
        {
            var input = SafetyIoSource(request);
            return SafetyIoConfigurationEditing.Decode(input.Project, ModuleConnectionEditing.Parse(input.Bytes), input.Profile, request).Info;
        }
    }
    private (SafetyIoPreview Preview, byte[] Bytes) BuildSafetyIo(SafetyIoEditRequest request)
    {
        if (request?.Source is null || !StructuralEditing.Hash(request.ExpectedConfigurationSha256)) throw new OperationException("safety_io_policy", "Bind the exact decoded source/configuration hash.");
        var input = SafetyIoSource(request.Source);
        return SafetyIoConfigurationEditing.Build(input.Project, input.Bytes, input.Profile, request);
    }
    public async Task<SafetyIoPreview> PreviewSafetyIoAsync(SafetyIoEditRequest request, CancellationToken ct = default)
    {
        if (request?.Source is null) throw new OperationException("safety_io_policy", "Bind the decoded source before preview.");
        using (await EnterWorkspaceAsync(request.Source.ProjectId, ct))
        {
            var preview = BuildSafetyIo(request).Preview;
            var receipt = StructuralArtifact(request.Source.ProjectId, "json", JsonSerializer.SerializeToUtf8Bytes(new SafetyIoPreviewDocument("safety_io_preview", 1, preview), Json));
            Audit("safety_io_preview", Get(request.Source.ProjectId), request.Source.ExpectedSha256, preview.ResultSha256, receipt.Id);
            return preview with { PreviewReceipt = receipt };
        }
    }
    private (Artifact Artifact, SafetyIoPreview Preview) SafetyIoReceipt(string id, string sha256)
    {
        if (!StructuralEditing.Hash(sha256)) throw new OperationException("safety_io_receipt", "Pin the exact safety I/O preview receipt hash.");
        var stored = Download(id); var artifact = stored.Artifact;
        if (artifact.Format != "json" || artifact.Bytes > 8 * 1024 * 1024 || !artifact.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("safety_io_receipt", "The preview receipt hash/format differs.");
        var doc = JsonSerializer.Deserialize<SafetyIoPreviewDocument>(File.ReadAllBytes(stored.Path), Json);
        if (doc?.Kind != "safety_io_preview" || doc.SchemaVersion != 1 || doc.Preview?.Before?.Source?.Id != artifact.ProjectId) throw new OperationException("safety_io_receipt", "Select a retained safety I/O preview receipt.");
        return (artifact, doc.Preview);
    }
    public async Task<SafetyIoStatus> SafetyIoStatusAsync(string previewReceiptId, string expectedSha256, CancellationToken ct = default)
    {
        var retained = SafetyIoReceipt(previewReceiptId, expectedSha256);
        using (await EnterWorkspaceAsync(retained.Preview.Before.Source.Id, ct))
        {
            var path = Path.Combine(ArtifactDir(previewReceiptId), "safety-io-apply.json"); var state = File.Exists(path) ? Read<SafetyIoApplyState>(path) : null;
            if (state is not null && state.ReviewSha256 != retained.Preview.ReviewSha256 || state?.Result is { } stored && stored.ReviewSha256 != retained.Preview.ReviewSha256) throw new OperationException("state_tampered", "Safety I/O apply state differs from the preview.");
            if (state?.Result is { } result) { _ = Download(result.Candidate.Id); _ = Download(result.Receipt.Id); }
            return new(retained.Artifact, retained.Preview with { PreviewReceipt = retained.Artifact }, state?.Status ?? "previewed", state?.Result);
        }
    }
    public async Task<SafetyIoResult> ApplySafetyIoAsync(SafetyIoApplyRequest request, CancellationToken ct = default)
    {
        var retained = SafetyIoReceipt(request.PreviewReceiptId, request.PreviewReceiptSha256);
        using (await EnterWorkspaceAsync(retained.Preview.Before.Source.Id, ct))
        {
            if (!retained.Preview.ReviewSha256.Equals(request.ExpectedReviewSha256, StringComparison.OrdinalIgnoreCase) || Hash(JsonSerializer.SerializeToUtf8Bytes(retained.Preview.Request, Json)) != Hash(JsonSerializer.SerializeToUtf8Bytes(request.Preview, Json))) throw new OperationException("stale_revision", "Apply requires the complete unchanged preview request and its exact review/receipt hashes.");
            var path = Path.Combine(ArtifactDir(request.PreviewReceiptId), "safety-io-apply.json");
            if (File.Exists(path))
            {
                var previous = Read<SafetyIoApplyState>(path);
                if (previous.ReviewSha256 != retained.Preview.ReviewSha256 || previous.Result is { } stored && stored.ReviewSha256 != retained.Preview.ReviewSha256) throw new OperationException("state_tampered", "Safety I/O apply state differs from the preview.");
                if (previous.Status == "applied" && previous.Result is { } result) { _ = Download(result.Candidate.Id); _ = Download(result.Receipt.Id); return result; }
                throw new OperationException("safety_io_indeterminate", "The previous apply has no recorded completion. Inspect retained status/artifacts; it is not replayed automatically.");
            }
            var built = BuildSafetyIo(request.Preview);
            if (built.Preview.ReviewSha256 != retained.Preview.ReviewSha256) throw new OperationException("stale_revision", "Source, AOP profiles, edits or findings changed; preview again.");
            if (!built.Preview.CanApply) throw new OperationException("safety_io_blocked", "Resolve the explicit review-only and unvalidated-signature policy findings before emitting a review candidate.");
            Save(path, new SafetyIoApplyState("applying-indeterminate", built.Preview.ReviewSha256, null));
            var project = PublishDiagnosticProject(built.Bytes, "REVIEW ONLY: Safety I/O point configuration", "safety-io:" + retained.Preview.Before.Source.Id);
            var candidate = StructuralArtifact(project.Id, "l5x", built.Bytes);
            var receipt = StructuralArtifact(project.Id, "json", JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, kind = "safety_io_apply", producer = WorkspaceIdentity.Name, toolVersion = WorkspaceIdentity.Version,
                createdAt = DateTimeOffset.UtcNow, previewReceipt = retained.Artifact, preview = built.Preview, project, candidate, nativeAcceptance = "not-performed", signatureStatus = "unvalidated-after-parameter-change", nativeAopRoundTripRequired = true }, Json));
            var applied = new SafetyIoResult(project, candidate, receipt, built.Preview.ReviewSha256);
            Save(path, new SafetyIoApplyState("applied", built.Preview.ReviewSha256, applied)); Audit("safety_io_apply", project, built.Preview.Before.Source.Sha256, candidate.Sha256, receipt.Id);
            return applied;
        }
    }
}
