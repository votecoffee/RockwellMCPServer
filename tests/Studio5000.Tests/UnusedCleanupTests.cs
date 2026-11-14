using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Studio5000.Extensibility;
using Studio5000.Extensions.Rockwell;
using Xunit;

namespace Studio5000.Tests;

public sealed class UnusedCleanupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellCleanup", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public UnusedCleanupTests()
    {
        Directory.CreateDirectory(root);
        service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
    }
    private static XElement Tag(string name, string type, string? access = "None", string tagType = "Base") => new("Tag", new XAttribute("Name", name),
        new XAttribute("TagType", tagType), new XAttribute("DataType", type), access is null ? null : new XAttribute("ExternalAccess", access));
    private static XElement Routine(string code) => new("Routine", new XAttribute("Name", "Main"), new XAttribute("Type", "RLL"),
        new XElement("RLLContent", new XElement("Rung", new XAttribute("Number", "0"), new XElement("Text", new XCData(code)))));
    private static XDocument Fixture()
    {
        var leaf = new XElement("DataType", new XAttribute("Name", "Leaf"), new XElement("Members", new XElement("Member", new XAttribute("Name", "Value"), new XAttribute("DataType", "DINT"))));
        var outer = new XElement("DataType", new XAttribute("Name", "Outer"), new XElement("Members", new XElement("Member", new XAttribute("Name", "Nested"), new XAttribute("DataType", "Leaf"))));
        XElement Aoi(string name, string code) => new("AddOnInstructionDefinition", new XAttribute("Name", name), new XElement("Parameters"),
            new XElement("LocalTags", new XElement("LocalTag", new XAttribute("Name", "Tmp"), new XAttribute("DataType", "Leaf")),
                new XElement("LocalTag", new XAttribute("Name", "Instance"), new XAttribute("DataType", "Helper"))), new XElement("Routines", Routine(code)));
        var kept = Tag("Kept", "DINT");
        kept.Add(new XElement("Description", new XCData("CRLF\r\n$N version-marker")),
            new XElement("Data", new XAttribute("Format", "L5K"), new XCData("[42]")),
            new XElement("Data", new XAttribute("Format", "Decorated"), new XElement("DataValue", new XAttribute("DataType", "DINT"), new XAttribute("Value", "42"))),
            new XElement("Data", new XAttribute("Format", "Native"), new XCData("opaque $N\r\nbytes")));
        return new(new XElement("RSLogix5000Content", new XAttribute("TargetType", "Controller"), new XAttribute("ContainsContext", "false"), new XAttribute("SoftwareRevision", "35.00"),
            new XElement("Controller", new XAttribute("Name", "Synthetic"), new XElement("DataTypes", leaf, outer),
                new XElement("AddOnInstructionDefinitions", Aoi("Helper", "NOP();"), Aoi("Unused", "Helper(Instance);")),
                new XElement("Tags", kept, Tag("UnusedInstance", "Unused"), Tag("UnusedArray", "Outer")),
                new XElement("Modules", new XElement("Module", new XAttribute("Name", "Ethernet"), new XAttribute("Address", "192.0.2.1"))),
                new XElement("Programs", new XElement("Program", new XAttribute("Name", "Example"), new XAttribute("MainRoutineName", "Main"),
                    new XElement("Tags"), new XElement("Routines", Routine("MOV(Kept,Kept);")))))));
    }
    private async Task<ProjectInfo> Register(XDocument doc)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".L5X");
        await File.WriteAllTextAsync(path, doc.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false));
        return await service.RegisterAsync(path);
    }
    private static UnusedCleanupSelection[] AllCandidates => [new("tag", "UnusedInstance"), new("tag", "UnusedArray"), new("aoi", "Unused"), new("aoi", "Helper"), new("udt", "Outer"), new("udt", "Leaf")];
    private static UnusedCleanupRequest Request(ProjectInfo p, UnusedCleanupSelection[]? candidates = null) => new(p.Id, p.CurrentSha256, p.Revision, candidates ?? AllCandidates);
    private static UnusedCleanupApplyRequest Apply(UnusedCleanupPreview p) => new(p.Request, p.ReviewSha256, p.PreviewReceipt!.Id, p.PreviewReceipt.Sha256);

    [Fact]
    public async Task Closed_tag_type_and_AOI_removals_preserve_every_other_byte_and_replay_the_retained_result()
    {
        var doc = Fixture(); var p = await Register(doc); var original = File.ReadAllBytes(p.SourcePath);
        var preview = await service.PreviewUnusedCleanupAsync(Request(p));
        Assert.True(preview.CanApply); Assert.False(preview.Partial); Assert.Empty(preview.Retentions); Assert.Equal(6, preview.Removals.Length);
        Assert.Contains(preview.Dependencies, e => e.From.EndsWith("/Unused") && e.To.EndsWith("/Helper"));
        Assert.True(preview.Preservation.UnselectedBytesPreserved);
        var result = await service.ApplyUnusedCleanupAsync(Apply(preview));
        var actual = File.ReadAllText(service.Download(result.Candidate.Id).Path);
        var expected = Encoding.UTF8.GetString(original);
        foreach (var removal in preview.Removals)
        {
            var element = doc.Descendants().Single(e => (string?)e.Attribute("Name") == removal.Selection.Name && e.Name.LocalName == (removal.Selection.Kind switch { "tag" => "Tag", "udt" => "DataType", _ => "AddOnInstructionDefinition" }));
            expected = expected.Replace(element.ToString(SaveOptions.DisableFormatting), "", StringComparison.Ordinal);
        }
        Assert.Equal(expected, actual); Assert.Equal(preview.ResultSha256, result.Candidate.Sha256);
        Assert.Equal(original, File.ReadAllBytes(p.SourcePath)); Assert.Equal(p, service.Get(p.Id)); Assert.Empty(service.SdkJobList());
        Assert.Equal(result, await service.ApplyUnusedCleanupAsync(Apply(preview)));
        var status = await service.UnusedCleanupStatusAsync(preview.PreviewReceipt!.Id, preview.PreviewReceipt.Sha256);
        Assert.Equal("applied", status.Status); Assert.Equal(result, status.Result);
    }
    [Fact]
    public async Task Explicit_HMI_retention_keeps_transitive_types_and_AOI_dependencies()
    {
        var p = await Register(Fixture());
        var request = Request(p) with { Retain = [new(new("tag", "UnusedInstance"), "hmi", "Reviewed display binding")] };
        var preview = await service.PreviewUnusedCleanupAsync(request);
        Assert.Contains(preview.Retentions, r => r.Declaration.Selection.Name == "UnusedInstance" && r.Reason.StartsWith("hmi:"));
        Assert.Contains(preview.Retentions, r => r.Declaration.Selection.Name == "Unused" && r.RequiredBy!.EndsWith("UnusedInstance"));
        Assert.Contains(preview.Retentions, r => r.Declaration.Selection.Name == "Helper");
        Assert.Contains(preview.Retentions, r => r.Declaration.Selection.Name == "Leaf");
        Assert.Equal(new[] { "UnusedArray", "Outer" }, preview.Removals.Select(r => r.Selection.Name));
    }
    [Theory]
    [InlineData("Read/Write", "Base", "external-access")]
    [InlineData(null, "Base", "external-access")]
    [InlineData("None", "Produced", "produced-consumed")]
    [InlineData("None", "Consumed", "produced-consumed")]
    public async Task External_and_produced_tags_are_retained_with_their_type_closure(string? access, string tagType, string reason)
    {
        var doc = Fixture(); var tag = doc.Descendants("Tag").Single(t => (string?)t.Attribute("Name") == "UnusedArray");
        tag.Attribute("ExternalAccess")?.Remove(); if (access is not null) tag.SetAttributeValue("ExternalAccess", access);
        tag.SetAttributeValue("TagType", tagType);
        var p = await Register(doc); var report = await service.PreviewUnusedCleanupAsync(Request(p));
        Assert.Contains(report.Retentions, r => r.Declaration.Selection.Name == "UnusedArray" && r.Reason.StartsWith(reason));
        Assert.Contains(report.Retentions, r => r.Declaration.Selection.Name == "Outer");
        Assert.Contains(report.Retentions, r => r.Declaration.Selection.Name == "Leaf");
    }
    [Fact]
    public async Task Retained_alias_chains_preserve_the_selected_base_declarations()
    {
        var doc = Fixture(); var tags = doc.Root!.Element("Controller")!.Element("Tags")!;
        doc.Descendants("Program").Single().Descendants("Text").Single().Value = "NOP();";
        var first = Tag("FirstAlias", "DINT", tagType: "Alias"); first.SetAttributeValue("AliasFor", "Kept");
        var second = Tag("SecondAlias", "DINT", "Read/Write", "Alias"); second.SetAttributeValue("AliasFor", "FirstAlias");
        tags.Add(first, second);
        var p = await Register(doc); var preview = await service.PreviewUnusedCleanupAsync(Request(p, [new("tag", "FirstAlias"), new("tag", "Kept")]));
        Assert.False(preview.CanApply); Assert.Equal(2, preview.Retentions.Length);
        Assert.Contains(preview.Retentions, r => r.Declaration.Selection.Name == "Kept" && r.RequiredBy!.EndsWith("FirstAlias"));
        Assert.Equal("unused_cleanup_blocked", (await Assert.ThrowsAsync<OperationException>(() => service.ApplyUnusedCleanupAsync(Apply(preview)))).Code);
    }
    [Fact]
    public async Task Program_shadowing_and_AOI_locals_do_not_capture_controller_declarations()
    {
        var doc = Fixture(); var controller = doc.Root!.Element("Controller")!;
        controller.Element("Tags")!.Add(Tag("Shadow", "DINT"));
        var program = controller.Element("Programs")!.Element("Program")!;
        program.Element("Tags")!.Add(Tag("Shadow", "DINT"));
        program.Element("Routines")!.Add(new XElement("Routine", new XAttribute("Name", "ST"), new XAttribute("Type", "ST"), new XElement("STContent", new XElement("Line", "Shadow:=1;"))));
        var aoi = controller.Element("AddOnInstructionDefinitions")!.Elements().First();
        aoi.Element("LocalTags")!.Add(new XElement("LocalTag", new XAttribute("Name", "Shadow"), new XAttribute("DataType", "DINT")));
        aoi.Descendants("Text").Single().Value = "MOV(Shadow,Shadow);";
        var p = await Register(doc); var report = await service.PreviewUnusedCleanupAsync(Request(p, [new("tag", "Shadow"), new("tag", "Shadow", "Example")]));
        Assert.Null(Assert.Single(report.Removals).Selection.ProgramName);
        Assert.Equal("Example", Assert.Single(report.Retentions).Declaration.Selection.ProgramName);
    }
    [Fact]
    public async Task Retained_MESSAGE_native_configuration_keeps_the_local_element_and_its_nested_types()
    {
        var doc = Fixture(); var message = Tag("ReadMessage", "MESSAGE");
        message.Add(new XElement("Data", new XAttribute("Format", "Message"), new XElement("MessageParameters", new XAttribute("LocalElement", "UnusedArray[0]"))));
        doc.Root!.Element("Controller")!.Element("Tags")!.Add(message);
        var p = await Register(doc); var preview = await service.PreviewUnusedCleanupAsync(Request(p));
        Assert.Contains(preview.Retentions, r => r.Declaration.Selection.Name == "UnusedArray" && r.Reason == "native-configuration-mention");
        Assert.Contains(preview.Retentions, r => r.Declaration.Selection.Name == "Outer");
        Assert.Contains(preview.Retentions, r => r.Declaration.Selection.Name == "Leaf");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_preserves_original_BOM_encoding_and_CRLF_native_annotations(bool utf16)
    {
        var doc = Fixture(); var text = doc.ToString(SaveOptions.DisableFormatting);
        Encoding encoding = utf16 ? new UnicodeEncoding(false, true, true) : new UTF8Encoding(true, true);
        var path = Path.Combine(root, "encoded.L5X");
        var original = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray(); File.WriteAllBytes(path, original);
        var p = await service.RegisterAsync(path); var preview = await service.PreviewUnusedCleanupAsync(Request(p, [new("tag", "UnusedArray")]));
        var result = await service.ApplyUnusedCleanupAsync(Apply(preview));
        var removed = doc.Descendants("Tag").Single(t => (string?)t.Attribute("Name") == "UnusedArray").ToString(SaveOptions.DisableFormatting);
        var expected = encoding.GetPreamble().Concat(encoding.GetBytes(text.Replace(removed, "", StringComparison.Ordinal))).ToArray();
        Assert.Equal(expected, File.ReadAllBytes(service.Download(result.Candidate.Id).Path));
        Assert.Equal(original, File.ReadAllBytes(path));
    }
    [Theory]
    [InlineData("signed")]
    [InlineData("opaque")]
    [InlineData("fbd")]
    [InlineData("force")]
    public async Task Protected_and_unknown_content_never_becomes_proof_of_unused_declarations(string kind)
    {
        var doc = Fixture(); var controller = doc.Root!.Element("Controller")!;
        if (kind == "signed") controller.Element("AddOnInstructionDefinitions")!.Elements().First().SetAttributeValue("SignatureID", "test");
        if (kind == "opaque") controller.Add(new XElement("EncodedData", new XAttribute("EncodedType", "AddOnInstructionDefinition"), new XAttribute("Name", "Opaque"), "not-decoded"));
        if (kind == "fbd") controller.Element("Programs")!.Element("Program")!.Element("Routines")!.Add(new XElement("Routine", new XAttribute("Name", "Unknown"), new XAttribute("Type", "FBD")));
        if (kind == "force") controller.Element("Tags")!.Elements().Single(t => (string?)t.Attribute("Name") == "UnusedArray").Add(new XElement("ForceData", "unchanged"));
        var p = await Register(doc); var report = await service.PreviewUnusedCleanupAsync(Request(p));
        if (kind is "opaque" or "fbd") { Assert.True(report.Partial); Assert.Empty(report.Removals); }
        else Assert.Contains(report.Retentions, r => r.Reason == "safety-force-signed-or-protected");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parameters_only_protected_AOI_retains_unresolved_StringFamily_and_AOI_candidates(bool selectProtected)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "ProtectedCleanupDemo.L5X");
        var bytes = File.ReadAllBytes(fixture); var doc = XmlProjects.Parse(bytes);
        var protectedAoi = doc.Descendants("EncodedData").Single();
        Assert.NotNull(protectedAoi.Element("Parameters")); Assert.Null(protectedAoi.Element("LocalTags")); Assert.Null(protectedAoi.Element("Routines"));
        var names = new[] { "DemoString40", "DemoString64", "DemoString20", "DemoString32" };
        Assert.DoesNotContain(protectedAoi.Descendants().Attributes("DataType"), a => names.Contains(a.Value));
        Assert.All(doc.Descendants("DataType"), t => Assert.Equal("StringFamily", (string?)t.Attribute("Family")));
        var path = Path.Combine(root, "parameters-only.L5X"); File.WriteAllBytes(path, bytes);
        var p = await service.RegisterAsync(path);
        var candidates = names.Select(n => new UnusedCleanupSelection("udt", n)).Append(new("aoi", "SpareInstruction"));
        if (selectProtected) candidates = candidates.Append(new("aoi", "ProtectedAssembly"));
        var report = await service.PreviewUnusedCleanupAsync(Request(p, candidates.ToArray()));
        Assert.True(report.Partial); Assert.False(report.CanApply); Assert.Empty(report.Removals); Assert.Null(report.ResultSha256);
        Assert.Equal(selectProtected ? 6 : 5, report.Retentions.Length);
        Assert.All(names, name => Assert.Contains(report.Retentions, r => r.Declaration.Selection.Name == name));
        Assert.Contains(report.Retentions, r => r.Declaration.Selection.Name == "SpareInstruction");
        Assert.Contains(report.Diagnostics, d => d.StartsWith("protected-aoi-hidden-dependencies:", StringComparison.Ordinal));
        Assert.Contains(report.Dependencies, e => e.Kind == "protected-aoi-hidden-dependencies" && e.To.EndsWith("/DemoString40", StringComparison.Ordinal));
        if (selectProtected)
            Assert.Contains(report.Retentions, r => r.Declaration.Selection.Name == "DemoString40" && r.RequiredBy == "aoi:controller/ProtectedAssembly");
        else
            Assert.Contains(report.Retentions, r => r.Declaration.Selection.Name == "DemoString40" && r.RequiredBy == "retained-source");
        Assert.Equal("unused_cleanup_blocked", (await Assert.ThrowsAsync<OperationException>(() => service.ApplyUnusedCleanupAsync(Apply(report)))).Code);
        Assert.Equal(bytes, File.ReadAllBytes(path)); Assert.Equal(p, service.Get(p.Id)); Assert.Empty(service.SdkJobList());
    }
    [Fact]
    public async Task Preview_and_apply_bind_scope_revision_receipt_and_review_without_replaying_unknown_attempts()
    {
        var p = await Register(Fixture()); var request = Request(p); var preview = await service.PreviewUnusedCleanupAsync(request);
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewUnusedCleanupAsync(request with { ExpectedRevision = p.Revision + 1 }))).Code);
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.ApplyUnusedCleanupAsync(Apply(preview) with { ExpectedReviewSha256 = new string('0', 64) }))).Code);
        Assert.Equal("unused_cleanup_receipt", (await Assert.ThrowsAsync<OperationException>(() => service.ApplyUnusedCleanupAsync(Apply(preview) with { PreviewReceiptSha256 = new string('0', 64) }))).Code);
        var intent = Path.Combine(Path.GetDirectoryName(service.Download(preview.PreviewReceipt!.Id).Path)!, "unused-cleanup-apply.json");
        File.WriteAllText(intent, JsonSerializer.Serialize(new { status = "applying-indeterminate", reviewSha256 = preview.ReviewSha256, result = (object?)null }));
        Assert.Equal("unused_cleanup_indeterminate", (await Assert.ThrowsAsync<OperationException>(() => service.ApplyUnusedCleanupAsync(Apply(preview)))).Code);
        var partial = Fixture(); partial.Root!.SetAttributeValue("ContainsContext", "true"); var partialProject = await Register(partial);
        Assert.Equal("unused_cleanup_scope", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewUnusedCleanupAsync(Request(partialProject)))).Code);
    }
    [Fact]
    public async Task Full_decorated_controller_with_over_100000_unrelated_operands_supports_seven_exact_candidates()
    {
        var path = Path.Combine(root, "full.L5X"); FullControllerReviewTests.WriteController(path, true);
        var text = File.ReadAllText(path).Replace("<RSLogix5000Content ", "<RSLogix5000Content TargetType=\"Controller\" ContainsContext=\"false\" ", StringComparison.Ordinal)
            .Replace("XIC(Requested)OTE(Enabled);", string.Concat(Enumerable.Repeat("XIC(Requested)", 8)) + "OTE(Enabled);", StringComparison.Ordinal);
        var tags = string.Concat(Enumerable.Range(0, 7).Select(i => Tag("Unused" + i, "DINT").ToString(SaveOptions.DisableFormatting)));
        text = text.Replace("</Tags><Programs>", tags + "</Tags><Programs>", StringComparison.Ordinal); File.WriteAllText(path, text, new UTF8Encoding(false));
        var p = await service.RegisterAsync(path);
        var references = service.FindTagReferences(p.Id, new(p.CurrentSha256, "Unused0"));
        Assert.Empty(references.References); Assert.True(references.ScannedCandidates > 100000);
        var preview = await service.PreviewUnusedCleanupAsync(Request(p, Enumerable.Range(0, 7).Select(i => new UnusedCleanupSelection("tag", "Unused" + i)).ToArray()));
        Assert.True(preview.CanApply); Assert.Equal(7, preview.Removals.Length); Assert.Empty(preview.Retentions);
        Assert.Equal(p.SourceSha256, ProjectService.Hash(File.ReadAllBytes(path)));
    }
    [Fact]
    public void Operations_expose_the_shared_contract_without_an_AOI_structural_scope_bypass()
    {
        var operations = new LogixExtension().Describe();
        var operation = operations.Single(o => o.Name == "studio_unused_cleanup_preview");
        Assert.Equal("preview", operation.Effect);
        ExtensionSchema.Validate(operation.InputSchema, JsonSerializer.SerializeToElement(new { request = new UnusedCleanupRequest("id", "sha", 0, [new("aoi", "Unused")]) }, ProjectService.Json));
        Assert.Equal("apply", operations.Single(o => o.Name == "studio_unused_cleanup_apply").Effect);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
