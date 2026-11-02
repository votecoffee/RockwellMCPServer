using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class AoiArgumentLinkageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellAoiLinkage", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public AoiArgumentLinkageTests()
    {
        Directory.CreateDirectory(root); service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
    }
    private static XElement Tag(string name, string type, string? alias = null) => new("Tag", new XAttribute("Name", name), new XAttribute("DataType", type),
        new XAttribute("TagType", alias is null ? "Base" : "Alias"), alias is null ? null : new XAttribute("AliasFor", alias));
    private static XElement Parameter(string name, string type, string usage, bool required = true, bool visible = true) => new("Parameter",
        new XAttribute("Name", name), new XAttribute("DataType", type), new XAttribute("Usage", usage), new XAttribute("Required", required ? "true" : "false"), new XAttribute("Visible", visible ? "true" : "false"));
    private static XElement Routine(params string[] texts) => new("Routines", new XElement("Routine", new XAttribute("Name", "Main"), new XAttribute("Type", "RLL"),
        new XElement("RLLContent", texts.Select((text, i) => new XElement("Rung", new XAttribute("Number", i), new XElement("Text", new XCData(text)))))));
    private static XDocument Project(string text, params XElement[] parameters) => new(new XElement("RSLogix5000Content", new XElement("Controller", new XAttribute("Name", "Fixture"),
        new XElement("DataTypes", new XElement("DataType", new XAttribute("Name", "RecordType"), new XElement("Members",
            new XElement("Member", new XAttribute("Name", "Count"), new XAttribute("DataType", "DINT")),
            new XElement("Member", new XAttribute("Name", "Text"), new XAttribute("DataType", "STRING"))))),
        new XElement("Tags", Tag("SavedRecord", "RecordType"), Tag("SavedCount", "DINT"), Tag("GlobalOnly", "DINT")),
        new XElement("AddOnInstructionDefinitions", new XElement("AddOnInstructionDefinition", new XAttribute("Name", "Probe"), new XElement("Parameters",
            Parameter("EnableIn", "BOOL", "Input"), Parameter("EnableOut", "BOOL", "Output"), parameters))),
        new XElement("Programs", new XElement("Program", new XAttribute("Name", "Example"), new XAttribute("MainRoutineName", "Main"),
            new XElement("Tags", Tag("Instance", "Probe"), Tag("Count", "DINT", "SavedCount"), Tag("Record", "RecordType", "SavedRecord"),
                Tag("Text", "STRING"), Tag("Other", "DINT"), Tag("Out", "BOOL"), Tag("Texts", "STRING")), Routine(text))))));

    private async Task<(ProjectInfo Info, string Path)> Register(XDocument doc)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); await File.WriteAllTextAsync(path, doc.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false));
        return (await service.RegisterAsync(path), path);
    }
    private static MergeSnapshot Snapshot(ProjectInfo p) => new(p.Id, p.CurrentSha256);
    private Task<LogixValidationReport> Preview(ProjectInfo p, ProjectInfo? baseline = null) => service.PreviewLogixValidationAsync(new("offline-integrity", Snapshot(p), Baseline: baseline is null ? null : Snapshot(baseline)));
    private static LogixValidationFinding[] Linkage(LogixValidationReport r) => r.Findings.Where(f => f.Check == AoiArgumentLinkage.CheckName && f.Classification != "resolved").ToArray();
    private static XElement ProgramTags(XDocument d) => d.Descendants("Program").First().Element("Tags")!;

    [Fact]
    public async Task Eight_calls_with_eleven_missing_local_alias_arguments_report_88_errors_then_resolve_without_code_changes()
    {
        var parameters = Enumerable.Range(1, 11).Select(i => Parameter("Question" + i, "STRING", "InOut")).ToArray();
        var call = "Probe(Instance," + string.Join(",", Enumerable.Range(1, 11).Select(i => "SharedQuestions.Text" + i)) + ");";
        var doc = Project(call, parameters); var controller = doc.Root!.Element("Controller")!;
        controller.Element("DataTypes")!.Add(new XElement("DataType", new XAttribute("Name", "QuestionRecord"), new XElement("Members",
            Enumerable.Range(1, 11).Select(i => new XElement("Member", new XAttribute("Name", "Text" + i), new XAttribute("DataType", "STRING"))))));
        controller.Element("Tags")!.Add(Tag("CellQuestions", "QuestionRecord"));
        var first = doc.Descendants("Program").Single(); first.Element("Routines")!.ReplaceWith(Routine(Enumerable.Repeat(call, 4).ToArray()));
        var second = new XElement(first); second.SetAttributeValue("Name", "Second"); controller.Element("Programs")!.Add(second);
        var (baseline, source) = await Register(doc); var sourceBytes = await File.ReadAllBytesAsync(source);
        var broken = await Preview(baseline); Assert.Equal("fail", broken.Status); Assert.Equal(88, Linkage(broken).Length);
        Assert.All(Linkage(broken), f => { Assert.Equal("fail", f.Status); Assert.Contains("declaration_missing", f.Detail); Assert.NotNull(f.ActualSha256); });
        var inherited = await Preview(baseline, baseline); Assert.Equal("fail", inherited.Status); Assert.All(Linkage(inherited), f => Assert.Equal("inherited", f.Classification));
        var logic = doc.Descendants("Text").Select(t => t.Value).ToArray();
        foreach (var p in doc.Descendants("Program")) p.Element("Tags")!.Add(Tag("SharedQuestions", "QuestionRecord", "CellQuestions"));
        Assert.Equal(logic, doc.Descendants("Text").Select(t => t.Value));
        var (fixedProject, _) = await Register(doc); var repaired = await Preview(fixedProject, baseline);
        Assert.Equal("pass", repaired.Status); Assert.Empty(Linkage(repaired));
        Assert.Equal(88, repaired.Findings.Count(f => f.Check == AoiArgumentLinkage.CheckName && f.Classification == "resolved"));
        Assert.Equal(96, repaired.Checks.Single(c => c.Name == AoiArgumentLinkage.CheckName).Compared);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source)); Assert.Equal(0, service.Get(baseline.Id).Revision); Assert.Empty(service.SdkJobList());
    }
    [Theory]
    [InlineData("Probe(Instance,Count,Record.Text);", "pass", null)]
    [InlineData("Probe(Instance,Record.Count,Text);", "pass", null)]
    [InlineData("Probe(Instance,Missing,Text);", "fail", "declaration_missing")]
    [InlineData("Probe(Instance,Record.Missing,Text);", "fail", "declared_member_missing")]
    [InlineData("Probe(Instance,Count,Other);", "fail", "declared type DINT")]
    [InlineData("Probe(Instance,Count,'literal');", "fail", "declared tag reference")]
    [InlineData("Probe(Instance,Count,Texts[1]);", "pass", null)]
    [InlineData("Probe(Instance,Count,Texts[Other]);", "unsupported", "dynamic_index_unsupported")]
    [InlineData("Probe(Instance,Count,Texts[9]);", "fail", "array_index_out_of_range")]
    [InlineData("Probe(Instance,Count+1,Text);", "unsupported", "expression_or_address_unsupported")]
    public async Task Input_and_InOut_arguments_resolve_aliases_members_types_and_fixed_arrays(string call, string status, string? detail)
    {
        var doc = Project(call, Parameter("CountInput", "DINT", "Input"), Parameter("Payload", "STRING", "InOut"));
        ProgramTags(doc).Elements("Tag").Single(t => (string?)t.Attribute("Name") == "Texts").SetAttributeValue("Dimensions", "4");
        var (p, _) = await Register(doc); var report = await Preview(p); Assert.Equal(status, report.Status);
        if (detail is null) Assert.Empty(Linkage(report)); else Assert.Contains(Linkage(report), f => f.Detail.Contains(detail, StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("Probe(Instance,Count,Text);", "pass")]
    [InlineData("Probe(Instance,Other,Count,Text,Out);", "pass")]
    [InlineData("Probe(Instance,Count);", "fail")]
    public async Task Optional_visible_hidden_and_interspersed_parameters_preserve_supported_call_layouts(string call, string status)
    {
        var doc = Project(call, Parameter("OptionalEarly", "DINT", "Input", false), Parameter("CountInput", "DINT", "Input"),
            Parameter("HiddenOptional", "BOOL", "Input", false, false), Parameter("Payload", "STRING", "InOut", false, false), Parameter("OptionalOut", "BOOL", "Output", false));
        var (p, _) = await Register(doc); Assert.Equal(status, (await Preview(p)).Status);
    }
    [Fact]
    public async Task Program_shadowing_dangling_and_cyclic_aliases_are_checked_in_the_caller_scope()
    {
        var doc = Project("Probe(Instance,Count);", Parameter("CountInput", "DINT", "Input"));
        var count = ProgramTags(doc).Elements("Tag").Single(t => (string?)t.Attribute("Name") == "Count"); count.SetAttributeValue("AliasFor", "Absent");
        var (dangling, _) = await Register(doc); Assert.Contains(Linkage(await Preview(dangling)), f => f.Status == "fail" && f.Detail.Contains("declaration_missing"));
        count.SetAttributeValue("AliasFor", "Other"); var other = ProgramTags(doc).Elements("Tag").Single(t => (string?)t.Attribute("Name") == "Other"); other.SetAttributeValue("AliasFor", "Count");
        var (cycle, _) = await Register(doc); Assert.Contains(Linkage(await Preview(cycle)), f => f.Detail.Contains("alias_cycle"));
        other.Attribute("AliasFor")!.Remove(); other.SetAttributeValue("DataType", "STRING");
        var (shadow, _) = await Register(doc); Assert.Contains(Linkage(await Preview(shadow)), f => f.Status == "fail" && f.Detail.Contains("declared type STRING"));
    }
    [Fact]
    public async Task InOut_UDT_identity_and_constant_reference_contracts_are_exact()
    {
        var parameter = Parameter("RecordInput", "RecordType", "InOut"); var doc = Project("Probe(Instance,Record);", parameter);
        var (valid, _) = await Register(doc); Assert.Equal("pass", (await Preview(valid)).Status);
        var saved = doc.Root!.Element("Controller")!.Element("Tags")!.Elements("Tag").Single(t => (string?)t.Attribute("Name") == "SavedRecord");
        saved.SetAttributeValue("DataType", "OtherRecord"); var (wrong, _) = await Register(doc); Assert.Equal("fail", (await Preview(wrong)).Status);
        saved.SetAttributeValue("DataType", "RecordType"); saved.SetAttributeValue("Constant", "true");
        var (constant, _) = await Register(doc); Assert.Contains(Linkage(await Preview(constant)), f => f.Detail.Contains("constant"));
        doc.Descendants("Parameter").Single(p => (string?)p.Attribute("Name") == "RecordInput").SetAttributeValue("Constant", "true");
        var (readOnly, _) = await Register(doc); Assert.Equal("pass", (await Preview(readOnly)).Status);
    }
    [Fact]
    public async Task AOI_body_callers_use_own_parameters_and_locals_without_controller_fallback()
    {
        var doc = Project("Probe(Instance,Count,Text);", Parameter("CountInput", "DINT", "Input"), Parameter("Payload", "STRING", "InOut"));
        var outer = new XElement("AddOnInstructionDefinition", new XAttribute("Name", "Outer"), new XElement("Parameters", Parameter("LocalText", "STRING", "InOut")),
            new XElement("LocalTags", new XElement("LocalTag", new XAttribute("Name", "Inner"), new XAttribute("DataType", "Probe")), new XElement("LocalTag", new XAttribute("Name", "LocalCount"), new XAttribute("DataType", "DINT"))),
            Routine("Probe(Inner,LocalCount,LocalText);"));
        doc.Root!.Element("Controller")!.Element("AddOnInstructionDefinitions")!.Add(outer);
        var (valid, _) = await Register(doc); Assert.Equal("pass", (await Preview(valid)).Status);
        outer.Descendants("Text").Single().Value = "Probe(Inner,GlobalOnly,LocalText);";
        var (invalid, _) = await Register(doc); var report = await Preview(invalid); Assert.Equal("fail", report.Status);
        Assert.Contains(Linkage(report), f => f.Detail.Contains("caller_declaration_missing"));
    }
    [Fact]
    public async Task Inherited_linkage_failures_cannot_be_waived_by_native_difference_rules()
    {
        var (p, _) = await Register(Project("Probe(Instance,Missing);", Parameter("CountInput", "DINT", "Input")));
        var request = new LogixValidationRequest("native-readback", Snapshot(p), Reference: Snapshot(p)); var report = await service.PreviewLogixValidationAsync(request);
        Assert.Equal("fail", report.Status); var finding = Assert.Single(Linkage(report)); Assert.Equal("inherited", finding.Classification);
        var ex = await Assert.ThrowsAsync<OperationException>(() => service.PreviewLogixValidationAsync(request with
        { ExpectedDifferences = [new(finding.Check, finding.Location, null, finding.ActualSha256, "Cannot waive linkage")] }));
        Assert.Equal("validation_policy", ex.Code);
        var profiles = JsonSerializer.Serialize(service.LogixValidationProfiles(), ProjectService.Json);
        Assert.Equal(4, RegexCount(profiles, AoiArgumentLinkage.CheckName)); Assert.Empty(service.SdkJobList());
    }
    [Theory]
    [InlineData("Probe(Instance,1);", "DINT", "Input", "pass")]
    [InlineData("Probe(Instance,2147483648);", "DINT", "Input", "unsupported")]
    [InlineData("Probe(Instance,Count);", "REAL", "Input", "unsupported")]
    [InlineData("Probe(Instance,1);", "DINT", "Output", "fail")]
    [InlineData("Probe(Instance,Text);", "STRING", "Input", "unsupported")]
    public async Task Literal_conversions_and_unsupported_parameter_metadata_do_not_produce_false_binding_passes(string call, string type, string usage, string status)
    {
        var (p, _) = await Register(Project(call, Parameter("Value", type, usage))); Assert.Equal(status, (await Preview(p)).Status);
    }
    [Fact]
    public async Task Instance_types_and_unavailable_structure_metadata_remain_explicit()
    {
        var doc = Project("Probe(Instance,Text);", Parameter("Value", "STRING", "InOut"));
        ProgramTags(doc).Elements("Tag").Single(t => (string?)t.Attribute("Name") == "Instance").SetAttributeValue("DataType", "DINT");
        var (wrongInstance, _) = await Register(doc); Assert.Contains(Linkage(await Preview(wrongInstance)), f => f.Status == "fail" && f.Detail.Contains("instance"));
        var unknown = Project("Probe(Instance,Record);", Parameter("Value", "UnlistedType", "InOut"));
        unknown.Root!.Element("Controller")!.Element("Tags")!.Elements("Tag").Single(t => (string?)t.Attribute("Name") == "SavedRecord").SetAttributeValue("DataType", "UnlistedType");
        var (unlisted, _) = await Register(unknown); Assert.Contains(Linkage(await Preview(unlisted)), f => f.Status == "unsupported" && f.Detail.Contains("metadata"));
    }
    private static int RegexCount(string text, string value) => System.Text.RegularExpressions.Regex.Matches(text, System.Text.RegularExpressions.Regex.Escape(value)).Count;
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
