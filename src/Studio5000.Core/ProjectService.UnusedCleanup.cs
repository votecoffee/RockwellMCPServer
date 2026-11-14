using System.Text.Json;

namespace Studio5000.Core;

public sealed partial class ProjectService
{
    private (UnusedCleanupPreview Preview, byte[] Bytes) BuildUnusedCleanup(UnusedCleanupRequest request, CancellationToken ct)
    {
        var input = StructuralSource(request.ProjectId, request.ExpectedSha256, request.ExpectedRevision);
        return UnusedCleanup.Build(input.Project, input.Bytes, request, ct);
    }
    public async Task<UnusedCleanupPreview> PreviewUnusedCleanupAsync(UnusedCleanupRequest request, CancellationToken ct = default)
    {
        using (await EnterWorkspaceAsync(request.ProjectId, ct))
        {
            var preview = BuildUnusedCleanup(request, ct).Preview;
            var receipt = StructuralArtifact(request.ProjectId, "json", JsonSerializer.SerializeToUtf8Bytes(new UnusedCleanupReceipt("unused_cleanup_preview", 1, preview), Json));
            Audit("unused_cleanup_preview", Get(request.ProjectId), preview.Source.Sha256, preview.ResultSha256, receipt.Id);
            return preview with { PreviewReceipt = receipt };
        }
    }
    private (Artifact Artifact, UnusedCleanupPreview Preview) CleanupReceipt(string id, string sha256)
    {
        if (!StructuralEditing.Hash(sha256)) throw new OperationException("unused_cleanup_receipt", "Supply the exact retained cleanup preview receipt hash.");
        var stored = Download(id); var artifact = stored.Artifact;
        if (artifact.Format != "json" || artifact.Bytes > 8 * 1024 * 1024 || !artifact.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new OperationException("unused_cleanup_receipt", "Retained cleanup preview hash/format differs.");
        var document = JsonSerializer.Deserialize<UnusedCleanupReceipt>(File.ReadAllBytes(stored.Path), Json);
        if (document?.Kind != "unused_cleanup_preview" || document.SchemaVersion != 1 || document.Preview?.Source?.Id != artifact.ProjectId)
            throw new OperationException("unused_cleanup_receipt", "Select a retained cleanup preview receipt.");
        return (artifact, document.Preview);
    }
    public async Task<UnusedCleanupStatus> UnusedCleanupStatusAsync(string previewReceiptId, string expectedSha256, CancellationToken ct = default)
    {
        var retained = CleanupReceipt(previewReceiptId, expectedSha256);
        using (await EnterWorkspaceAsync(retained.Preview.Source.Id, ct))
        {
            var path = Path.Combine(ArtifactDir(previewReceiptId), "unused-cleanup-apply.json");
            var state = File.Exists(path) ? Read<UnusedCleanupApplyState>(path) : null;
            if (state is not null && state.ReviewSha256 != retained.Preview.ReviewSha256 || state?.Result is { } stored && stored.ReviewSha256 != retained.Preview.ReviewSha256)
                throw new OperationException("state_tampered", "Cleanup apply state differs from its retained preview.");
            if (state?.Result is { } result) { _ = Download(result.Candidate.Id); _ = Download(result.Receipt.Id); }
            return new(retained.Artifact, retained.Preview with { PreviewReceipt = retained.Artifact }, state?.Status ?? "previewed", state?.Result);
        }
    }
    public async Task<UnusedCleanupResult> ApplyUnusedCleanupAsync(UnusedCleanupApplyRequest request, CancellationToken ct = default)
    {
        var retained = CleanupReceipt(request.PreviewReceiptId, request.PreviewReceiptSha256);
        using (await EnterWorkspaceAsync(retained.Preview.Source.Id, ct))
        {
            if (!retained.Preview.ReviewSha256.Equals(request.ExpectedReviewSha256, StringComparison.OrdinalIgnoreCase) ||
                Hash(JsonSerializer.SerializeToUtf8Bytes(retained.Preview.Request, Json)) != Hash(JsonSerializer.SerializeToUtf8Bytes(request.Preview, Json)))
                throw new OperationException("stale_revision", "Apply must match the exact retained cleanup preview request and review hash.");
            var path = Path.Combine(ArtifactDir(request.PreviewReceiptId), "unused-cleanup-apply.json");
            if (File.Exists(path))
            {
                var previous = Read<UnusedCleanupApplyState>(path);
                if (previous.ReviewSha256 != retained.Preview.ReviewSha256 || previous.Result is { } stored && stored.ReviewSha256 != retained.Preview.ReviewSha256)
                    throw new OperationException("state_tampered", "Cleanup apply state differs from its retained preview.");
                if (previous.Status == "applied" && previous.Result is { } result) { _ = Download(result.Candidate.Id); _ = Download(result.Receipt.Id); return result; }
                throw new OperationException("unused_cleanup_indeterminate", "A previous apply has no recorded completion. Inspect retained status/artifacts; it is not replayed automatically.");
            }
            var built = BuildUnusedCleanup(request.Preview, ct);
            if (built.Preview.ReviewSha256 != retained.Preview.ReviewSha256) throw new OperationException("stale_revision", "Cleanup sources, closure or preservation changed; preview again.");
            if (!built.Preview.CanApply) throw new OperationException("unused_cleanup_blocked", "There are no dependency-closed eligible removals in this review.");
            Save(path, new UnusedCleanupApplyState("applying-indeterminate", built.Preview.ReviewSha256, null));
            var project = PublishDiagnosticProject(built.Bytes, "REVIEW ONLY: Unused declaration cleanup", "unused-cleanup:" + retained.Preview.Source.Id);
            var candidate = StructuralArtifact(project.Id, "l5x", built.Bytes);
            var receipt = StructuralArtifact(project.Id, "json", JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, kind = "unused_cleanup_apply", producer = WorkspaceIdentity.Name,
                toolVersion = WorkspaceIdentity.Version, createdAt = DateTimeOffset.UtcNow, previewReceipt = retained.Artifact, preview = built.Preview, project, candidate, nativeAcceptance = "not-performed" }, Json));
            var applied = new UnusedCleanupResult(project, candidate, receipt, built.Preview.ReviewSha256);
            Save(path, new UnusedCleanupApplyState("applied", built.Preview.ReviewSha256, applied));
            Audit("unused_cleanup_apply", project, built.Preview.Source.Sha256, candidate.Sha256, receipt.Id);
            return applied;
        }
    }
}
