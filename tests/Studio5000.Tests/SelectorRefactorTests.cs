using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class SelectorRefactorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellSelectorPreview", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    private const string Routine = "/RSLogix5000Content/Controller/Programs/Program[@Name='Example']/Routines/Routine[@Name='Main']";
    private const string Selector = "[XIC(DecisionA)MOV(10,Chosen)XIC(WaitForCont),XIC(DecisionB)MOV(20,Chosen)XIC(WaitForCont),XIC(Retry)MOV(30,Chosen)]OTE(Setup);";
    public SelectorRefactorTests()
    {
        Directory.CreateDirectory(root);
        service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
    }
    private static XElement Tag(string name, string type, params object[] extra) => new("Tag", new XAttribute("Name", name), new XAttribute("DataType", type), new XAttribute("TagType", "Base"), extra);
    private async Task<(ProjectInfo Project, string Path)> Register(string text = Selector, Action<XElement>? modify = null)
    {
        var tags = new XElement("Tags", new[] { "DecisionA", "DecisionB", "Retry", "WaitForCont", "Setup" }.Select(n => Tag(n, "BOOL")),
            Tag("Chosen", "DINT"), Tag("Other", "DINT"), Tag("Flags", "BOOL", new XAttribute("Dimensions", "4")),
            Tag("ChosenAlias", "DINT", new XAttribute("AliasFor", "Chosen")), Tag("RecordAlias", "RouteRecord", new XAttribute("AliasFor", "SharedRecord")));
        for (var i = 0; i < 8; i++) { tags.Add(Tag("Gate" + i, "BOOL")); tags.Add(Tag("Fallback" + i, "BOOL")); }
        var program = new XElement("Program", new XAttribute("Name", "Example"), new XAttribute("MainRoutineName", "Main"), tags,
            new XElement("Routines", new XElement("Routine", new XAttribute("Name", "Main"), new XAttribute("Type", "RLL"),
            new XElement("RLLContent", new XElement("Rung", new XAttribute("Number", "5"), new XElement("Text", new XCData(text)))))));
        modify?.Invoke(program);
        var doc = new XDocument(new XElement("RSLogix5000Content", new XElement("Controller", new XAttribute("Name", "Fixture"),
            new XElement("Tags", Tag("SharedRecord", "RouteRecord")),
            new XElement("DataTypes", new XElement("DataType", new XAttribute("Name", "RouteRecord"), new XElement("Members",
                new XElement("Member", new XAttribute("Name", "Value"), new XAttribute("DataType", "DINT")),
                new XElement("Member", new XAttribute("Name", "Ready"), new XAttribute("DataType", "BOOL"))))), new XElement("Programs", program))));
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); await File.WriteAllTextAsync(path, doc.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false));
        return (await service.RegisterAsync(path), path);
    }
    private SelectorRefactorReport Preview(ProjectInfo p, string prefix = "Route") => service.PreviewSelectorRefactor(p.Id, new(p.CurrentSha256, Routine, 0, prefix));

    // Independent tiny interpreter for the fixture's contacts, parallel paths and immediate writes.
    // It models stable Boolean inputs only; it is not a native/scan acceptance test.
    private static (int Chosen, int Setup, int[] Writes) Execute(IEnumerable<string> rungs, Dictionary<string, int> inputs)
    {
        var values = new Dictionary<string, int>(inputs, StringComparer.Ordinal); var writes = new List<int>();
        for (var i = 1; i <= 32; i++) values["Route_" + i.ToString("D2")] = 1; // Stale true helper bits must be overwritten.
        int Value(string value) => int.TryParse(value, out var literal) ? literal : values.GetValueOrDefault(value);
        bool Run(List<LadderPreview.Part> sequence, bool power)
        {
            foreach (var part in sequence)
            {
                if (part.Legs is { } legs)
                {
                    var joined = false; foreach (var leg in legs) joined |= Run(leg, power); power = joined;
                }
                else switch (part.Instruction.ToUpperInvariant())
                {
                    case "XIC": power &= Value(part.Operands[0]) != 0; break;
                    case "XIO": power &= Value(part.Operands[0]) == 0; break;
                    case "MOV": if (power) { var value = Value(part.Operands[0]); values[part.Operands[1]] = value; writes.Add(value); } break;
                    case "OTE": values[part.Operands[0]] = power ? 1 : 0; break;
                    case "NOP": break;
                    default: throw new InvalidOperationException("Fixture interpreter does not implement " + part.Instruction);
                }
            }
            return power;
        }
        foreach (var text in rungs) Run(LadderPreview.ParseLogic(text), true);
        return (values.GetValueOrDefault("Chosen"), values.GetValueOrDefault("Setup"), writes.ToArray());
    }
    [Fact]
    public async Task All_decision_combinations_retain_writes_priority_and_downstream_continuation_gates()
    {
        var (project, path) = await Register(); var original = await File.ReadAllBytesAsync(path); var preview = Preview(project);
        Assert.Equal(7, preview.Rungs.Length); Assert.Equal(3, preview.ProposedTags.Length);
        Assert.All(preview.ProposedTags, t => { Assert.Equal("Example", t.ProgramName); Assert.Equal("BOOL", t.DataType); });
        for (var bits = 0; bits < 16; bits++)
        {
            var input = new Dictionary<string, int> { ["DecisionA"] = bits & 1, ["DecisionB"] = (bits >> 1) & 1, ["Retry"] = (bits >> 2) & 1, ["WaitForCont"] = (bits >> 3) & 1, ["Chosen"] = -1 };
            var expected = Execute([Selector], input); var actual = Execute(preview.Rungs.Select(r => r.Text), input);
            Assert.Equal(expected.Chosen, actual.Chosen); Assert.Equal(expected.Setup, actual.Setup); Assert.Equal(expected.Writes, actual.Writes);
        }
        var falseWait = Execute(preview.Rungs.Select(r => r.Text), new() { ["DecisionA"] = 1, ["WaitForCont"] = 0, ["Chosen"] = -1 });
        Assert.Equal(10, falseWait.Chosen); Assert.Equal(0, falseWait.Setup); Assert.Equal(new[] { 10 }, falseWait.Writes);
        var all = Execute(preview.Rungs.Select(r => r.Text), new() { ["DecisionA"] = 1, ["DecisionB"] = 1, ["Retry"] = 1, ["WaitForCont"] = 1 });
        Assert.Equal(new[] { 10, 20, 30 }, all.Writes); Assert.Equal(30, all.Chosen);
        for (var i = 0; i < preview.Writes.Length; i++)
        {
            Assert.Equal("route-summary", preview.Rungs[i * 2].Kind); Assert.Equal("original-mov", preview.Rungs[i * 2 + 1].Kind);
            Assert.EndsWith("OTE(" + preview.Writes[i].RouteTag + ");", preview.Rungs[i * 2].Text);
            Assert.Contains("MOV(" + preview.Writes[i].Source + "," + preview.Writes[i].Destination + ")", preview.Rungs[i * 2 + 1].Text);
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(path)); Assert.Equal(0, service.Get(project.Id).Revision); Assert.Empty(service.SdkJobList());
        Assert.Equal(preview.ReviewSha256, Preview(project).ReviewSha256);
        Assert.DoesNotContain("apply", JsonSerializer.Serialize(preview, ProjectService.Json).ToLowerInvariant());
    }
    [Theory]
    [InlineData(7)][InlineData(8)]
    public async Task Deep_wrapped_selectors_preview_at_most_one_branch_level_without_dropping_writes(int depth)
    {
        var body = Selector[..Selector.LastIndexOf("OTE", StringComparison.Ordinal)];
        for (var i = 0; i < depth - 1; i++) body = "[XIC(Gate" + i + ")" + body + ",XIC(Fallback" + i + ")MOV(" + (40 + i) + ",Chosen)]";
        var text = body + "OTE(Setup);"; var (project, _) = await Register(text); var preview = Preview(project);
        Assert.Equal(depth, preview.OriginalBranchDepth); Assert.All(preview.Rungs, r => Assert.InRange(r.MaximumBranchDepth, 0, 1));
        Assert.Equal(depth + 2, preview.Writes.Length);
        foreach (var choice in new[] { 0, 1 })
        {
            var inputs = new Dictionary<string, int> { ["DecisionA"] = 1, ["DecisionB"] = 1, ["Retry"] = 1, ["WaitForCont"] = choice };
            for (var i = 0; i < 8; i++) { inputs["Gate" + i] = 1; inputs["Fallback" + i] = choice; }
            var expected = Execute([text], inputs); var actual = Execute(preview.Rungs.Select(r => r.Text), inputs);
            Assert.Equal(expected.Writes, actual.Writes); Assert.Equal(expected.Chosen, actual.Chosen); Assert.Equal(expected.Setup, actual.Setup);
        }
    }
    [Fact]
    public async Task Fixed_array_predicates_and_repeated_continuation_contacts_remain_in_the_proposal()
    {
        var text = "XIC(Flags[1])MOV(10,Chosen)XIC(WaitForCont)XIC(WaitForCont)OTE(Setup);";
        var (p, _) = await Register(text); var preview = Preview(p);
        Assert.Contains("XIC(Flags[1])", preview.Rungs[0].Text);
        Assert.Contains("XIC(WaitForCont)XIC(WaitForCont)", preview.Rungs[^1].Text);
        Assert.Equal(0, preview.OriginalBranchDepth);
        var expected = Execute([text], new() { ["Flags[1]"] = 1, ["WaitForCont"] = 0 });
        var actual = Execute(preview.Rungs.Select(r => r.Text), new() { ["Flags[1]"] = 1, ["WaitForCont"] = 0 });
        Assert.Equal(expected.Chosen, actual.Chosen); Assert.Equal(0, actual.Setup);
    }
    [Fact]
    public async Task Aliases_to_controller_UDT_members_resolve_and_shared_roots_block_guard_dependencies()
    {
        var (good, _) = await Register("XIC(DecisionA)MOV(Other,RecordAlias.Value)OTE(Setup);");
        var preview = Preview(good); Assert.Equal("RecordAlias.Value", Assert.Single(preview.Writes).Destination);
        var (dependent, _) = await Register("XIC(RecordAlias.Ready)MOV(10,RecordAlias.Value)OTE(Setup);");
        Assert.Contains("read by a predicate", Assert.Throws<OperationException>(() => Preview(dependent)).Message);
    }
    [Fact]
    public async Task Later_MOV_sources_observe_earlier_writes_in_the_same_order()
    {
        const string text = "XIC(DecisionA)MOV(10,Chosen)MOV(Chosen,Other)MOV(Other,Chosen)XIC(WaitForCont)OTE(Setup);";
        var (p, _) = await Register(text); var preview = Preview(p);
        foreach (var enabled in new[] { 0, 1 })
        {
            var inputs = new Dictionary<string, int> { ["DecisionA"] = enabled, ["WaitForCont"] = 0, ["Chosen"] = -1, ["Other"] = 99 };
            var expected = Execute([text], inputs); var actual = Execute(preview.Rungs.Select(r => r.Text), inputs);
            Assert.Equal(expected.Writes, actual.Writes); Assert.Equal(expected.Chosen, actual.Chosen); Assert.Equal(0, actual.Setup);
        }
        Assert.Equal(new[] { "10", "Chosen", "Other" }, preview.Writes.Select(w => w.Source));
    }
    [Fact]
    public async Task Safety_and_active_edit_states_remain_outside_preview_scope()
    {
        var (safety, _) = await Register(modify: p => p.SetAttributeValue("Class", "Safety"));
        Assert.Contains("Safety", Assert.Throws<OperationException>(() => Preview(safety)).Message);
        var (edits, _) = await Register(modify: p => p.Descendants("Rung").Single().SetAttributeValue("OnlineEditType", "Pending"));
        Assert.Contains("online-edit", Assert.Throws<OperationException>(() => Preview(edits)).Message);
    }
    [Theory]
    [InlineData("EQU(ChosenAlias,1)MOV(10,Chosen)OTE(Setup);", "read by a predicate")]
    [InlineData("XIC(DecisionA)JSR(Main,0)MOV(10,Chosen)OTE(Setup);", "Calls, jumps")]
    [InlineData("XIC(DecisionA)JMP(Label)MOV(10,Chosen)OTE(Setup);", "Calls, jumps")]
    [InlineData("XIC(DecisionA)ONS(Other)MOV(10,Chosen)OTE(Setup);", "Calls, jumps")]
    [InlineData("XIC(Flags[Other])MOV(10,Chosen)OTE(Setup);", "Dynamic")]
    [InlineData("XIC(Missing)MOV(10,Chosen)OTE(Setup);", "Unresolved")]
    [InlineData("[XIC(DecisionA)MOV(10,Chosen),XIC(DecisionB)]OTE(Setup);", "bypasses")]
    public async Task Unsupported_or_dependency_sensitive_forms_do_not_return_partial_proposals(string text, string detail)
    {
        var (p, _) = await Register(text); var ex = Assert.Throws<OperationException>(() => Preview(p));
        Assert.Equal("selector_refactor_unsupported", ex.Code); Assert.Contains(detail, ex.Message); Assert.Empty(service.SdkJobList());
    }
    [Fact]
    public async Task Source_hash_collisions_types_constants_and_expansion_limits_are_guarded()
    {
        var (p, _) = await Register();
        Assert.Equal("stale_revision", Assert.Throws<OperationException>(() => service.PreviewSelectorRefactor(p.Id, new("stale", Routine, 0, "Route"))).Code);
        var (collision, _) = await Register(modify: p => p.Element("Tags")!.Add(Tag("Route_01", "BOOL")));
        Assert.Contains("already exists", Assert.Throws<OperationException>(() => Preview(collision)).Message);
        var (wrongType, _) = await Register(modify: p => p.Element("Tags")!.Elements("Tag").Single(t => (string?)t.Attribute("Name") == "Chosen").SetAttributeValue("DataType", "REAL"));
        Assert.Contains("DINT", Assert.Throws<OperationException>(() => Preview(wrongType)).Message);
        var (constant, _) = await Register(modify: p => p.Element("Tags")!.Elements("Tag").Single(t => (string?)t.Attribute("Name") == "Chosen").SetAttributeValue("Constant", "true"));
        Assert.Contains("writable", Assert.Throws<OperationException>(() => Preview(constant)).Message);
        var many = string.Concat(Enumerable.Repeat("[XIC(DecisionA),XIC(DecisionB)]", 9)) + "MOV(10,Chosen)OTE(Setup);";
        var (expanded, _) = await Register(many); Assert.Contains("budget", Assert.Throws<OperationException>(() => Preview(expanded)).Message);
        Assert.Empty(service.SdkJobList());
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
