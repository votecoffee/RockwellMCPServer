using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class AoiDependencyWorkflowTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellAoiDependencies", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public AoiDependencyWorkflowTests() { Directory.CreateDirectory(root); service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [root] }); }
    private const string Inner = "<DataType Name='Inner'><Members><Member Name='Value' DataType='DINT' Dimension='0' Radix='Decimal'/></Members></DataType>";
    private const string Outer = "<DataType Name='Outer'><Members><Member Name='Record' DataType='Inner' Dimension='0' Radix='NullType'/></Members></DataType>";
    private static string Aoi(string name, string locals = "", string code = "NOP();") => "<AddOnInstructionDefinition Name='" + name + "' Class='Standard'><Parameters><Parameter Name='Enable' DataType='BOOL' Usage='Input'/></Parameters><LocalTags>" + locals + "</LocalTags><Routines><Routine Name='Logic' Type='RLL'><RLLContent><Rung Number='0' Type='N'><Text><![CDATA[" + code + "]]></Text></Rung></RLLContent></Routine></Routines></AddOnInstructionDefinition>";
    private static readonly string Nested = Aoi("Nested", "<LocalTag Name='State' DataType='Outer'/>", "MOV(0,State.Record.Value);");
    private static readonly string Link = Aoi("Link", "<LocalTag Name='N' DataType='Nested'/><LocalTag Name='H' DataType='Helper'/>", "Helper(H)Nested(N);");
    private const string Main = "/RSLogix5000Content/Controller/Programs/Program[@Name='P']/Routines/Routine[@Name='Main']/RLLContent/Rung/Text";
    private static string Xml(bool additions = false, string code = "EthernetDevice(Dev,192,0,2,44)MOV(Keep,Keep);", string helper = "NOP();") =>
        "<?xml version='1.0' encoding='utf-8'?>\r\n<RSLogix5000Content SoftwareRevision='35.00' TargetType='Controller' TargetName='Synthetic' ContainsContext='false'><Controller Name='Synthetic' SafetyNetworkNumber='11223344'>" +
        "<DataTypes><DataType Name='Existing'><Members><Member Name='Value' DataType='DINT' Dimension='0' Radix='Decimal'/></Members></DataType>" + (additions ? Inner + Outer : "") + "</DataTypes>" +
        "<Modules><Module Name='Remote' CatalogNumber='Synthetic'><Ports><Port Address='198.51.100.44'/></Ports><SafetyNetwork>123456</SafetyNetwork><ConfigData ConfigSize='8'><Data Format='L5K'><![CDATA[[42,99]]]></Data></ConfigData><InputData>77</InputData></Module></Modules>" +
        "<AddOnInstructionDefinitions>" + Aoi("Helper", code: helper) + Aoi("EthernetDevice") + "<EncodedData EncodedType='AddOnInstructionDefinition' Name='ProtectedLibrary'><Parameters/><Payload>opaque μ\r\nretained</Payload></EncodedData>" + (additions ? Nested + Link : "") + "</AddOnInstructionDefinitions>" +
        "<Tags><Tag Name='Keep' TagType='Base' DataType='DINT'><Description><![CDATA[keep μ\r\nall state]]></Description><Data Format='L5K'><![CDATA[41]]></Data><Data Format='Decorated'><DataValue DataType='DINT' Value='41'/></Data><Data Format='Native'>opaque saved bytes</Data></Tag><Tag Name='MessageCfg' TagType='Base' DataType='MESSAGE'><Data Format='Message'><MessageParameters Path='1,0,2,198.51.100.55'/></Data></Tag></Tags>" +
        "<Programs><Program Name='P' Class='Standard' MainRoutineName='Main'><Tags><Tag Name='Dev' TagType='Base' DataType='EthernetDevice'><Data Format='L5K'><![CDATA[[1,2,3]]]></Data></Tag></Tags><Routines><Routine Name='Main' Type='RLL'><RLLContent><Rung Number='0' Type='N'><Text><![CDATA[" + code + "]]></Text></Rung></RLLContent></Routine></Routines></Program></Programs><Tasks><Task Name='MainTask'><ScheduledPrograms><ScheduledProgram Name='P'/></ScheduledPrograms></Task></Tasks><SafetyInfo Signature='trusted'><SignatureValue>nonzero</SignatureValue></SafetyInfo></Controller></RSLogix5000Content>";
    private async Task<ProjectInfo> Register(string xml, bool utf16 = false)
    { var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); var encoding = utf16 ? Encoding.Unicode : new UTF8Encoding(true); if (utf16) xml = xml.Replace("encoding='utf-8'", "encoding='utf-16'"); File.WriteAllBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes(xml)).ToArray()); return await service.RegisterAsync(path); }
    private static MergeSnapshot Snap(ProjectInfo p) => new(p.Id, p.CurrentSha256);
    private static AoiDependencyPlanRequest Selection(ProjectInfo source, ProjectInfo fresh) => new(Snap(source), Snap(fresh), ["Link"], ["Link", "Nested"], ["Outer", "Inner"]);
    private StructuralEditRequest Batch(ProjectInfo fresh, AoiDependencyPlan plan) => new(fresh.Id, fresh.CurrentSha256, fresh.Revision,
        new(null, plan.PlanReceipt!.Sha256, plan.PlanReceipt.Id), new([], [], plan.DataTypeScope, AcknowledgeIncompleteBindings: true, ReferenceAdditions: plan.ReferenceAdditions), plan.Edits);
    private static StructuralApplyRequest Apply(StructuralEditPreview p) => new(p.Request, p.ReviewSha256, p.PreviewReceipt!.Id, p.PreviewReceipt.Sha256);
    private async Task<StructuralEditResult> Change(ProjectInfo p, string value, bool safety = false)
    {
        var context = await service.StructuralContextAsync(new(p.Id, p.CurrentSha256, p.Revision, Main));
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(path, "{\"reason\":\"synthetic reviewed scope\"}");
        var request = new StructuralEditRequest(p.Id, p.CurrentSha256, p.Revision, new(path, ProjectService.Hash(File.ReadAllBytes(path))),
            new([new("P", "Main")], [], [], SafetyHandling: safety ? "review-only" : "block", AcknowledgeIncompleteBindings: true), [new("set_text", Main, context.Targets[0].ElementSha256, value)]);
        return await service.ApplyStructuralEditsAsync(Apply(await service.PreviewStructuralEditsAsync(request)));
    }
    private static PreservationStageReceipt Receipt(StructuralEditResult result) => new(result.Receipt.Id, result.Receipt.Sha256);
    private static AoiAddressPin Pin() => new("P", "Main", "EthernetDevice", "Dev", [1, 2, 3, 4]);

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Ordered_source_pinned_AOI_and_UDT_additions_preserve_every_original_fresh_byte(bool utf16)
    {
        var source = await Register(Xml(true).Replace("198.51.100.44", "203.0.113.99").Replace("CDATA[41]", "CDATA[99]").Replace("Value='41'", "Value='99'")); var fresh = await Register(Xml(), utf16);
        var original = File.ReadAllBytes(fresh.SourcePath);
        var plan = await service.PlanAoiDependenciesAsync(Selection(source, fresh));
        Assert.True(plan.CanPlan, JsonSerializer.Serialize(plan.Findings));
        Assert.Equal(new[] { "Helper", "Inner", "Outer", "Nested", "Link" }, plan.Definitions.Select(d => d.Name));
        Assert.Equal("use-existing", plan.Definitions[0].Operation);
        var preview = await service.PreviewStructuralEditsAsync(Batch(fresh, plan)); Assert.True(preview.CanApply, JsonSerializer.Serialize(preview.Findings));
        var applied = await service.ApplyStructuralEditsAsync(Apply(preview));
        var bytes = File.ReadAllBytes(service.Download(applied.Candidate.Id).Path); var doc = XDocument.Parse((utf16 ? Encoding.Unicode : Encoding.UTF8).GetString(bytes).TrimStart('\ufeff'), LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo); var lexical = new XmlSourceDocument(bytes, doc);
        var patches = plan.Definitions.Where(d => d.Operation == "add-reviewed").Select(d => doc.Descendants(d.Kind == "aoi" ? "AddOnInstructionDefinition" : "DataType").Single(n => (string?)n.Attribute("Name") == d.Name)).Select(n => { var span = lexical.Locate(n); return new XmlSourceDocument.Patch(span.Start, span.End, ""); }).ToArray();
        Assert.Equal(original, lexical.Apply(patches));
        var chain = await service.ReviewPreservationChainAsync(new(Snap(fresh), Snap(applied.Project), [Receipt(applied)], [Pin()]));
        Assert.True(chain.CanAttest, JsonSerializer.Serialize(chain.Findings)); Assert.Equal(3, chain.ExistingTagsPreserved); Assert.Equal(1, chain.AddressPinsPreserved);
        Assert.Equal(original, File.ReadAllBytes(fresh.SourcePath));
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(service.Download(chain.Receipt!.Id).Path));
        Assert.Equal(fresh.CurrentSha256, receipt.RootElement.GetProperty("original_fresh_sha256").GetString()); Assert.Equal(applied.Candidate.Sha256, receipt.RootElement.GetProperty("candidate_sha256").GetString());
    }

    [Theory]
    [InlineData("unapproved")][InlineData("incompatible")][InlineData("collision")][InlineData("safety")][InlineData("encoded")][InlineData("unknown-call")][InlineData("cycle")][InlineData("st")]
    public async Task Missing_approval_incompatible_or_unsupported_dependencies_are_blocked(string kind)
    {
        var sourceXml = Xml(true);
        if (kind == "safety") sourceXml = sourceXml.Replace("Name='Link' Class='Standard'", "Name='Link' Class='Safety'");
        if (kind == "encoded") sourceXml = sourceXml.Replace("Name='Link' Class='Standard'", "Name='Link' Class='Standard' SignatureID='trusted'");
        if (kind == "unknown-call") sourceXml = sourceXml.Replace("Helper(H)Nested(N);", "MissingAoi(H);");
        if (kind == "cycle") sourceXml = sourceXml.Replace("MOV(0,State.Record.Value);", "Link(Recursive);");
        if (kind == "st") sourceXml = sourceXml.Replace("Type='RLL'", "Type='ST'").Replace("<RLLContent><Rung Number='0' Type='N'><Text><![CDATA[", "<STContent><Line Number='0'><![CDATA[").Replace("]]></Text></Rung></RLLContent>", "]]></Line></STContent>");
        var freshXml = kind == "collision" ? Xml().Replace("<DataTypes>", "<DataTypes><DataType Name='Link'><Members><Member Name='X' DataType='DINT'/></Members></DataType>") : Xml(helper: kind == "incompatible" ? "AFI();" : "NOP();");
        var source = await Register(sourceXml); var fresh = await Register(freshXml); var request = Selection(source, fresh);
        if (kind == "unapproved") request = request with { AoiAdditions = ["Link"] };
        var plan = await service.PlanAoiDependenciesAsync(request);
        Assert.False(plan.CanPlan); Assert.Empty(plan.Edits); Assert.Contains(plan.Findings, f => f.Status == "blocked");
    }

    [Fact]
    public async Task Structural_reference_rechecks_existing_AOI_contracts_even_without_the_planner()
    {
        var source = await Register(Xml(true)); var fresh = await Register(Xml()); var plan = await service.PlanAoiDependenciesAsync(Selection(source, fresh));
        var changedFresh = await Register(Xml(helper: "AFI();")); var request = Batch(changedFresh, plan);
        // Fingerprint destination containers independently: this is a newly reviewed request, not a stale hash test.
        var edits = new List<StructuralEdit>(); foreach (var edit in request.Edits) { var context = await service.StructuralContextAsync(new(changedFresh.Id, changedFresh.CurrentSha256, 0, edit.XPath)); edits.Add(edit with { ExpectedElementSha256 = context.Targets[0].ElementSha256 }); }
        var preview = await service.PreviewStructuralEditsAsync(request with { Edits = edits.ToArray() });
        Assert.False(preview.CanApply); Assert.Contains(preview.Findings, f => f.Check == "reference-aoi-contract-mismatch");
    }

    [Fact]
    public async Task Multiple_structural_stages_compose_back_to_original_fresh_and_reordering_is_refused()
    {
        var source = await Register(Xml(true)); var fresh = await Register(Xml()); var plan = await service.PlanAoiDependenciesAsync(Selection(source, fresh));
        var one = await service.ApplyStructuralEditsAsync(Apply(await service.PreviewStructuralEditsAsync(Batch(fresh, plan))));
        var two = await Change(one.Project, "EthernetDevice(Dev,192,0,2,44)ADD(Keep,1,Keep);");
        var request = new PreservationChainRequest(Snap(fresh), Snap(two.Project), [Receipt(one), Receipt(two)], [Pin()]);
        Assert.True((await service.ReviewPreservationChainAsync(request)).CanAttest);
        Assert.False((await service.ReviewPreservationChainAsync(request with { Stages = [Receipt(two), Receipt(one)] })).CanAttest);
        Assert.False((await service.ReviewPreservationChainAsync(request with { Stages = [Receipt(one)] })).CanAttest);
    }

    [Fact]
    public async Task Fresh_numeric_device_operands_are_preserved_while_reviewed_logic_changes_remain()
    {
        var fresh = await Register(Xml()); var logic = await Register(Xml(code: "EthernetDevice(Dev, 203, 0, 113, 55)ADD(Keep,1,Keep);"));
        var plan = await service.PlanAoiAddressesAsync(new(Snap(fresh), Snap(logic), [Pin()]));
        var edit = Assert.Single(plan.LogicEdits); Assert.Equal("EthernetDevice(Dev, 192, 0, 2, 44)ADD(Keep,1,Keep);", edit.Value);
        var good = await Change(fresh, edit.Value!); Assert.True((await service.ReviewPreservationChainAsync(new(Snap(fresh), Snap(good.Project), [Receipt(good)], [Pin()]))).CanAttest);
        var bad = await Change(fresh, "EthernetDevice(Dev,203,0,113,55)ADD(Keep,1,Keep);");
        var review = await service.ReviewPreservationChainAsync(new(Snap(fresh), Snap(bad.Project), [Receipt(bad)], [Pin()]));
        Assert.False(review.CanAttest); Assert.Contains(review.Findings, f => f.Check == "original-fresh-address");
    }

    [Theory] [InlineData("duplicate")][InlineData("dynamic")][InlineData("range")][InlineData("indices")]
    public async Task Address_mapping_never_guesses_ambiguous_or_unsupported_operands(string kind)
    {
        var code = kind switch { "duplicate" => "EthernetDevice(Dev,192,0,2,44)EthernetDevice(Dev,192,0,2,44);", "dynamic" => "EthernetDevice(Dev,Keep,0,2,44);", "range" => "EthernetDevice(Dev,999,0,2,44);", _ => "EthernetDevice(Dev,192,0,2,44);" };
        var p = await Register(Xml(code: code)); var pin = kind == "indices" ? Pin() with { ArgumentIndices = [1, 1, 2, 3] } : Pin();
        await Assert.ThrowsAsync<OperationException>(() => service.PlanAoiAddressesAsync(new(Snap(p), Snap(p), [pin])));
    }

    [Fact]
    public async Task Safety_overlay_requires_separate_explicit_composition_authorization()
    {
        var fresh = await Register(Xml().Replace("Name='P' Class='Standard'", "Name='P' Class='Safety'"));
        var changed = await Change(fresh, "NOP();", safety: true);
        var request = new PreservationChainRequest(Snap(fresh), Snap(changed.Project), [Receipt(changed)]);
        var blocked = await service.ReviewPreservationChainAsync(request); Assert.False(blocked.CanAttest); Assert.Contains(blocked.Findings, f => f.Check == "safety-overlay-authorization");
        var reviewed = await service.ReviewPreservationChainAsync(request with { SafetyOverlayReason = "Synthetic separately authorized review-only overlay; native acceptance pending" });
        Assert.True(reviewed.CanAttest); Assert.True(Assert.Single(reviewed.Stages).ReviewedSafetyOverlay);
    }

    [Fact]
    public async Task Source_snapshot_and_composed_receipt_hashes_are_rechecked()
    {
        var source = await Register(Xml(true)); var fresh = await Register(Xml()); var plan = await service.PlanAoiDependenciesAsync(Selection(source, fresh));
        var reference = plan.ReferenceAdditions[0].Source;
        File.AppendAllText(service.Download(reference.ArtifactId!).Path, " ");
        Assert.Equal("artifact_tampered", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewStructuralEditsAsync(Batch(fresh, plan)))).Code);
        var changed = await Change(fresh, "NOP();");
        await Assert.ThrowsAsync<OperationException>(() => service.ReviewPreservationChainAsync(new(Snap(fresh), Snap(changed.Project), [new(changed.Receipt.Id, new string('0', 64))])));
    }

    [Fact]
    public async Task Indeterminate_stage_is_never_replayed_or_attested()
    {
        var p = await Register(Xml()); var changed = await Change(p, "NOP();");
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(service.Download(changed.Receipt.Id).Path));
        var previewId = receipt.RootElement.GetProperty("previewReceipt").GetProperty("id").GetString()!;
        var path = Path.Combine(root, "state", "artifacts", previewId, "structural-apply.json");
        var data = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))!; data["status"] = "applying-indeterminate"; File.WriteAllText(path, data.ToJsonString());
        var before = File.ReadAllBytes(path);
        Assert.Equal("preservation_chain_incomplete", (await Assert.ThrowsAsync<OperationException>(() => service.ReviewPreservationChainAsync(new(Snap(p), Snap(changed.Project), [Receipt(changed)])))).Code);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_separately_reviewed_tag_replacement_does_not_claim_original_fresh_preservation()
    {
        var p = await Register(Xml()); var tagPath = "/RSLogix5000Content/Controller/Tags/Tag[@Name='Keep']";
        var context = await service.StructuralContextAsync(new(p.Id, p.CurrentSha256, 0, tagPath));
        var tag = XDocument.Parse(Xml()).Descendants("Tag").Single(t => (string?)t.Attribute("Name") == "Keep");
        tag.Elements("Data").Remove(); tag.Add(new XElement("Data", new XAttribute("Format", "L5K"), "0"));
        var plan = Path.Combine(root, "reviewed-tag-replacement.json"); File.WriteAllText(plan, "{\"review\":\"synthetic replacement only\"}");
        var request = new StructuralEditRequest(p.Id, p.CurrentSha256, 0, new(plan, ProjectService.Hash(File.ReadAllBytes(plan))),
            new([], [new("Keep")], [], AcknowledgeIncompleteBindings: true, ValueDispositions: [new(new("Keep"), "replace-reviewed", "Synthetic separately reviewed tag replacement")]),
            [new("replace_element", tagPath, context.Targets[0].ElementSha256, tag.ToString(SaveOptions.DisableFormatting))]);
        var preview = await service.PreviewStructuralEditsAsync(request); Assert.True(preview.CanApply, JsonSerializer.Serialize(preview.Findings));
        var changed = await service.ApplyStructuralEditsAsync(Apply(preview));
        var report = await service.ReviewPreservationChainAsync(new(Snap(p), Snap(changed.Project), [Receipt(changed)]));
        Assert.False(report.CanAttest); Assert.Contains(report.Findings, f => f.Check == "original-fresh-tag");
    }

    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
