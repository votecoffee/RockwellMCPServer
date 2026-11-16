using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

// Retained native records are synthetic. These tests never invoke a vendor engine.
public sealed class ProtectedDependencyEvidenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellProtectedEvidence", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public ProtectedDependencyEvidenceTests()
    {
        Directory.CreateDirectory(root);
        service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [root] });
    }
    private static XElement Aoi(string name) => XElement.Parse($"<AddOnInstructionDefinition Name='{name}' Class='Standard'><Parameters/><LocalTags/><Routines><Routine Name='Logic' Type='RLL'><RLLContent><Rung Number='0'><Text><![CDATA[NOP();]]></Text></Rung></RLLContent></Routine></Routines></AddOnInstructionDefinition>");
    private static XElement Type() => XElement.Parse("<DataType Name='HiddenString' Class='User' Family='StringFamily'><Members><Member Name='LEN' DataType='DINT'/><Member Name='DATA' DataType='SINT' Dimension='82'/></Members></DataType>");
    private static XElement Opaque(int index, bool native = false) => new("EncodedData", new XAttribute("Name", "Opaque" + index),
        new XAttribute("EncodedType", "AddOnInstructionDefinition"), new XAttribute("EncryptionInfo", "synthetic"),
        new XElement("Parameters"), new XElement("Payload", new XCData((native ? "new-export-ciphertext-" : "original-ciphertext-") + index)));
    private static XDocument Source() => new(new XElement("RSLogix5000Content", new XAttribute("SoftwareRevision", "35.00"), new XAttribute("TargetType", "Controller"), new XAttribute("ContainsContext", "false"),
        new XElement("Controller", new XAttribute("Name", "Synthetic"), new XElement("DataTypes", Type()),
            new XElement("AddOnInstructionDefinitions", Aoi("SpareA"), Aoi("SpareB"), Aoi("HiddenHelper"), Enumerable.Range(0, 3).Select(i => Opaque(i))),
            XElement.Parse("<Tags><Tag Name='Existing' TagType='Base' DataType='DINT' ExternalAccess='None'><Data Format='L5K'><![CDATA[42]]></Data><Data Format='Decorated'><DataValue DataType='DINT' Value='42'/></Data><Data Format='Native'><![CDATA[opaque $N\r\nkeep]]></Data><Description><![CDATA[<Alarm[1]>Keep $N<@ver 1.4>]]></Description></Tag></Tags>"),
            XElement.Parse("<Modules><Module Name='Ethernet' Address='192.0.2.1'/></Modules>"),
            XElement.Parse("<Programs><Program Name='Main'><Tags/><Routines><Routine Name='main' Type='RLL'><RLLContent><Rung Number='0'><Text>NOP();</Text></Rung></RLLContent></Routine></Routines></Program></Programs>"))));
    private static XDocument Native(int index) => new(new XElement("RSLogix5000Content", new XAttribute("SoftwareRevision", "35.00"), new XAttribute("TargetType", "AddOnInstructionDefinition"),
        new XAttribute("TargetName", "Opaque" + index), new XAttribute("ContainsContext", "true"), new XAttribute("ExportOptions", "Dependencies ForceProtectedEncoding"),
        new XElement("Controller", new XAttribute("Name", "Synthetic"), new XElement("DataTypes", Type()), new XElement("AddOnInstructionDefinitions", Opaque(index, true), Aoi("HiddenHelper")))));
    private async Task<ProjectInfo> Register(XDocument doc)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); File.WriteAllText(path, doc.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false));
        return await service.RegisterAsync(path);
    }
    private void SaveJob(SdkJob job) => File.WriteAllBytes(Path.Combine(root, "state", "sdk-jobs", job.Id, "job.json"), JsonSerializer.SerializeToUtf8Bytes(job, ProjectService.Json));
    private Artifact RetainArtifact(string owner, string format, byte[] bytes)
    {
        var artifact = new Artifact(Guid.NewGuid().ToString("N"), owner, format, ProjectService.Hash(bytes), bytes.Length, "Synthetic native evidence; vendor execution not performed");
        var directory = Path.Combine(root, "state", "artifacts", artifact.Id); Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "project." + format), bytes); File.WriteAllBytes(Path.Combine(directory, "artifact.json"), JsonSerializer.SerializeToUtf8Bytes(artifact, ProjectService.Json));
        return artifact;
    }
    private async Task<(ProjectInfo Source, SdkJob Job, ProtectedDependencyPreviewRequest Request)> Retain(bool acd = false, XDocument? source = null, Func<int, XDocument>? native = null)
    {
        var project = await Register(source ?? Source()); var bytes = File.ReadAllBytes(project.SourcePath);
        var id = Guid.NewGuid().ToString("N"); var work = Path.Combine(root, "state", "sdk-jobs", id, "work"); Directory.CreateDirectory(work);
        var input = acd ? Encoding.UTF8.GetBytes("Synthetic ACD never opened natively") : bytes; File.WriteAllBytes(Path.Combine(work, acd ? "input.acd" : "input.l5x"), input);
        var artifacts = Enumerable.Range(0, 3).Select(i => RetainArtifact(project.Id, "l5x", Encoding.UTF8.GetBytes((native?.Invoke(i) ?? Native(i)).ToString(SaveOptions.DisableFormatting)))).ToList();
        if (acd) artifacts.Add(RetainArtifact(project.Id, "l5x", bytes));
        var methods = new[] { "OpenLogixProjectAsync" }.Concat(Enumerable.Repeat("PartialExportToXmlFileAsync", 3)).Concat(acd ? ["SaveAsAsync"] : Array.Empty<string>()).ToArray();
        var job = new SdkJob(id, "succeeded", DateTimeOffset.UnixEpoch, methods, ProjectService.Hash(input), null, false,
            methods.Select((m, i) => new SdkStepResult(i, m, "succeeded", null)).ToArray(), [], artifacts.ToArray(), project, SdkAssembly: SdkCatalog.Surface.Assembly);
        SaveJob(job);
        return (project, job, new(new(project.Id, project.CurrentSha256), artifacts.Take(3).Select((a, i) => new ProtectedDependencyExport("Opaque" + i, id, a.Id, a.Sha256)).ToArray()));
    }
    private async Task<ProtectedDependencyEvidenceRef> Issue(ProtectedDependencyPreviewRequest request)
    {
        var review = await service.PreviewProtectedDependenciesAsync(request); Assert.True(review.CanIssue, string.Join(";", review.Findings.Select(f => f.Detail)));
        var issued = await service.RecordProtectedDependenciesAsync(new(request, review.ReviewSha256)); return new(issued.Receipt.Id, issued.Receipt.Sha256);
    }
    private static UnusedCleanupRequest Cleanup(ProjectInfo project, ProtectedDependencyEvidenceRef[]? evidence = null, params string[] candidates) =>
        new(project.Id, project.CurrentSha256, project.Revision, (candidates.Length == 0 ? ["SpareA", "SpareB", "HiddenHelper"] : candidates).Select(n => new UnusedCleanupSelection("aoi", n)).ToArray(), NativeDependencyEvidence: evidence);

    [Fact]
    public async Task Exact_native_context_allows_only_reviewed_readable_removals_and_preserves_all_other_bytes()
    {
        var fixture = await Retain(); var original = File.ReadAllBytes(fixture.Source.SourcePath);
        var defaultReview = await service.PreviewUnusedCleanupAsync(Cleanup(fixture.Source)); Assert.False(defaultReview.CanApply);
        var plan = await service.PlanProtectedDependencyExportAsync(new(fixture.Request.Source, ["Opaque0", "Opaque1", "Opaque2"]));
        Assert.Equal(fixture.Source.CurrentSha256, plan.Snapshot.Sha256); Assert.Equal(4, plan.SdkRequest.Calls.Length);
        Assert.Equal("Controller/AddOnInstructionDefinitions/AddOnInstructionDefinition[@Name='Opaque0']", plan.SdkRequest.Calls[1].Arguments["xPath"].GetString());
        Assert.Null(plan.SdkRequest.ControllerTargetId);
        var nativeReview = await service.PreviewProtectedDependenciesAsync(fixture.Request);
        Assert.All(nativeReview.Roots, r => { Assert.NotEqual(r.SourceElementSha256, r.NativeExportElementSha256); Assert.Equal("exact-job-input-l5x", r.SourceRelationship); Assert.Contains(r.Dependencies, d => d.Name == "HiddenString"); });
        var proof = await Issue(fixture.Request); var preview = await service.PreviewUnusedCleanupAsync(Cleanup(fixture.Source, [proof]));
        Assert.True(preview.CanApply); Assert.Equal(new[] { "SpareA", "SpareB" }, preview.Removals.Select(r => r.Selection.Name));
        Assert.Equal("HiddenHelper", Assert.Single(preview.Retentions).Declaration.Selection.Name);
        Assert.Equal(3, preview.Coverage!.ProtectedAoiRoots); Assert.Equal(3, preview.Coverage.NativeDependencyRootsCovered); Assert.Equal(0, preview.Coverage.ProtectedAoiRootsUncovered);
        Assert.True(preview.Partial); Assert.True(preview.Coverage.HiddenOrUnsupportedSourcePresent);
        var apply = new UnusedCleanupApplyRequest(preview.Request, preview.ReviewSha256, preview.PreviewReceipt!.Id, preview.PreviewReceipt.Sha256);
        var result = await service.ApplyUnusedCleanupAsync(apply); var output = File.ReadAllBytes(service.Download(result.Candidate.Id).Path);
        var expected = Encoding.UTF8.GetString(original).Replace(Aoi("SpareA").ToString(SaveOptions.DisableFormatting), "", StringComparison.Ordinal).Replace(Aoi("SpareB").ToString(SaveOptions.DisableFormatting), "", StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), output); Assert.Equal(original, File.ReadAllBytes(fixture.Source.SourcePath));
        Assert.Equal(result, await service.ApplyUnusedCleanupAsync(apply)); Assert.Equal("not-performed", result.NativeAcceptance);
    }

    [Fact]
    public async Task Missing_one_root_retains_every_candidate_and_mixed_declaration_scope_is_refused()
    {
        var f = await Retain(); var proof = await Issue(f.Request with { Exports = f.Request.Exports[..2] });
        var preview = await service.PreviewUnusedCleanupAsync(Cleanup(f.Source, [proof])); Assert.False(preview.CanApply); Assert.Equal(1, preview.Coverage!.ProtectedAoiRootsUncovered);
        var mixed = Cleanup(f.Source, [proof]) with { Candidates = [new("tag", "Existing")] };
        Assert.Equal("protected_dependency_evidence", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewUnusedCleanupAsync(mixed))).Code);
    }

    [Theory]
    [InlineData("flags")] [InlineData("context")] [InlineData("layout")] [InlineData("missing-type")] [InlineData("readable-normalized")] [InlineData("revision")] [InlineData("not-opaque")]
    public async Task Partial_or_changed_native_context_cannot_issue_proof(string failure)
    {
        var f = await Retain(native: i =>
        {
            var doc = Native(i);
            switch (failure)
            {
                case "flags": doc.Root!.SetAttributeValue("ExportOptions", "Dependencies"); break;
                case "context": doc.Descendants("DataTypes").Single().Remove(); break;
                case "layout": doc.Descendants("Member").First().SetAttributeValue("DataType", "REAL"); break;
                case "missing-type": doc.Descendants("DataTypes").Single().Add(XElement.Parse("<DataType Name='NotInSource'><Members/></DataType>")); break;
                case "readable-normalized": doc.Descendants("Text").Single().Value = "MOV(1,Unknown);"; break;
                case "revision": doc.Root!.SetAttributeValue("SoftwareRevision", "36.00"); break;
                case "not-opaque": doc.Descendants("EncodedData").Single().ReplaceWith(Aoi("Opaque" + i)); break;
            }
            return doc;
        });
        var review = await service.PreviewProtectedDependenciesAsync(f.Request); Assert.False(review.CanIssue); Assert.NotEmpty(review.Findings);
        await Assert.ThrowsAsync<OperationException>(() => service.RecordProtectedDependenciesAsync(new(f.Request, review.ReviewSha256)));
    }

    [Theory]
    [InlineData("indeterminate")] [InlineData("effects")] [InlineData("target")] [InlineData("sdk")] [InlineData("no-sdk")] [InlineData("step")] [InlineData("errors")] [InlineData("method")]
    public async Task Ambiguous_or_incompatible_native_job_never_authorizes_removal(string failure)
    {
        var f = await Retain(); var job = f.Job;
        job = failure switch
        {
            "indeterminate" => job with { Status = "indeterminate" }, "effects" => job with { ControllerEffectsPossible = true }, "target" => job with { ControllerTargetId = "not-offline" },
            "sdk" => job with { SdkAssembly = "Unreviewed version" }, "no-sdk" => job with { SdkAssembly = null }, "step" => job with { Steps = job.Steps[..^1] },
            "errors" => job with { Events = [new("error", "native diagnostic")] }, "method" => job with { Methods = ["OpenLogixProjectAsync", "DownloadAsync"] }, _ => job
        };
        SaveJob(job); Assert.False((await service.PreviewProtectedDependenciesAsync(f.Request)).CanIssue);
    }

    [Fact]
    public async Task Receipt_rechecks_native_evidence_at_apply_and_rejects_stale_review()
    {
        var f = await Retain(); var review = await service.PreviewProtectedDependenciesAsync(f.Request);
        await Assert.ThrowsAsync<OperationException>(() => service.RecordProtectedDependenciesAsync(new(f.Request, new string('0', 64))));
        var proof = await Issue(f.Request); var preview = await service.PreviewUnusedCleanupAsync(Cleanup(f.Source, [proof]));
        SaveJob(f.Job with { Error = "later evidence changed" });
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyUnusedCleanupAsync(new(preview.Request, preview.ReviewSha256, preview.PreviewReceipt!.Id, preview.PreviewReceipt.Sha256)));
        Assert.Equal(File.ReadAllBytes(f.Source.SourcePath), File.ReadAllBytes(Path.Combine(root, "state", "sdk-jobs", f.Job.Id, "work", "input.l5x")));
    }

    [Fact]
    public async Task Receipt_reuse_requires_original_opaque_bytes_and_all_manifested_contracts()
    {
        var f = await Retain(); var proof = await Issue(f.Request);
        var variant = Source(); variant.Descendants("Module").Single().SetAttributeValue("Address", "192.0.2.2");
        variant.Descendants("Tag").Single().Element("Description")!.Value = "Revised annotation outside protected library";
        var different = await Register(variant); Assert.True((await service.PreviewUnusedCleanupAsync(Cleanup(different, [proof]))).CanApply);
        File.Delete(f.Source.SourcePath); // Frozen source and private job input survive changes to the original managed/input route.
        Assert.True((await service.PreviewUnusedCleanupAsync(Cleanup(different, [proof]))).CanApply);
        foreach (var failure in new[] { "ciphertext", "type", "readable-aoi", "missing-type" })
        {
            var changed = Source();
            switch (failure)
            {
                case "ciphertext": changed.Descendants("Payload").First().Value = "changed original ciphertext"; break;
                case "type": changed.Descendants("Member").First().SetAttributeValue("DataType", "REAL"); break;
                case "readable-aoi": changed.Descendants("AddOnInstructionDefinition").Single(a => (string?)a.Attribute("Name") == "HiddenHelper").Descendants("Text").Single().Value = "MOV(1,Other);"; break;
                case "missing-type": changed.Descendants("DataType").Single().Remove(); break;
            }
            var p = await Register(changed); await Assert.ThrowsAsync<OperationException>(() => service.PreviewUnusedCleanupAsync(Cleanup(p, [proof])));
        }
    }

    [Theory]
    [InlineData("input")] [InlineData("export")] [InlineData("receipt")] [InlineData("snapshot")] [InlineData("reference-hash")]
    public async Task Issued_receipts_do_not_hide_tampering_of_any_retained_preimage(string failure)
    {
        var f = await Retain(); var proof = await Issue(f.Request);
        var path = failure switch
        {
            "input" => Path.Combine(root, "state", "sdk-jobs", f.Job.Id, "work", "input.l5x"),
            "export" => service.Download(f.Job.Artifacts[0].Id).Path,
            "receipt" => service.Download(proof.ArtifactId).Path,
            "snapshot" => service.Download(JsonDocument.Parse(File.ReadAllBytes(service.Download(proof.ArtifactId).Path)).RootElement.GetProperty("sourceSnapshot").GetProperty("id").GetString()!).Path,
            _ => null
        };
        if (path is not null) File.AppendAllText(path, "tampered"); else proof = proof with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewUnusedCleanupAsync(Cleanup(f.Source, [proof])));
    }

    [Fact]
    public async Task Visible_native_dependency_call_missing_from_context_is_not_accepted()
    {
        var source = Source(); source.Descendants("AddOnInstructionDefinition").Single(a => (string?)a.Attribute("Name") == "HiddenHelper").Descendants("Text").Single().Value = "SpareA(Instance);";
        var f = await Retain(source: source, native: i => { var native = Native(i); native.Descendants("Text").Single().Value = "SpareA(Instance);"; return native; });
        var preview = await service.PreviewProtectedDependenciesAsync(f.Request); Assert.False(preview.CanIssue);
        Assert.Contains(preview.Findings, f => f.Detail.Contains("omitted from its dependency context", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Acd_requires_its_same_job_full_readback_and_unrelated_L5X_is_not_adopted()
    {
        var f = await Retain(acd: true); var review = await service.PreviewProtectedDependenciesAsync(f.Request); Assert.True(review.CanIssue);
        Assert.All(review.Roots, r => { Assert.Equal("same-job-acd-native-l5x-readback", r.SourceRelationship); Assert.NotEqual(r.NativeInputSha256, r.NativeReadbackSha256); });
        var unrelated = Source(); unrelated.Descendants("Module").Single().SetAttributeValue("Address", "192.0.2.3"); var other = await Register(unrelated);
        Assert.False((await service.PreviewProtectedDependenciesAsync(f.Request with { Source = new(other.Id, other.CurrentSha256) })).CanIssue);
        var missing = f.Job with { Artifacts = f.Job.Artifacts[..3] }; SaveJob(missing);
        Assert.False((await service.PreviewProtectedDependenciesAsync(f.Request)).CanIssue);
    }

    [Fact]
    public async Task Full_decorated_controller_is_target_filtered_with_all_unselected_bytes_preserved()
    {
        var path = Path.Combine(root, "large.l5x"); FullControllerReviewTests.WriteController(path, false); var full = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        full.Root!.SetAttributeValue("TargetType", "Controller"); full.Root.SetAttributeValue("ContainsContext", "false"); full.Root.SetAttributeValue("SoftwareRevision", "35.00");
        var small = Source().Root!.Element("Controller")!; var controller = full.Root.Element("Controller")!;
        controller.Element("DataTypes")!.Add(Type()); controller.Add(new XElement(small.Element("AddOnInstructionDefinitions")!));
        var f = await Retain(source: full); var proof = await Issue(f.Request); var before = File.ReadAllBytes(f.Source.SourcePath);
        var preview = await service.PreviewUnusedCleanupAsync(Cleanup(f.Source, [proof], "SpareA", "SpareB")); Assert.Equal(2, preview.Removals.Length); Assert.True(preview.Coverage!.ReadableUnits >= 14500);
        var result = await service.ApplyUnusedCleanupAsync(new(preview.Request, preview.ReviewSha256, preview.PreviewReceipt!.Id, preview.PreviewReceipt.Sha256));
        var expected = Encoding.UTF8.GetString(before).Replace(Aoi("SpareA").ToString(SaveOptions.DisableFormatting), "", StringComparison.Ordinal).Replace(Aoi("SpareB").ToString(SaveOptions.DisableFormatting), "", StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(service.Download(result.Candidate.Id).Path));
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
