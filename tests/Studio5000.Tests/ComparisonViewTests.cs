using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Studio5000.Extensibility;
using Studio5000.Extensions.Rockwell;
using Xunit;

namespace Studio5000.Tests;

public sealed class ComparisonViewTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellComparisonViews", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public ComparisonViewTests() { Directory.CreateDirectory(root); service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [root] }); }
    private static XDocument Fixture() => XDocument.Parse("""
        <RSLogix5000Content TargetType="Controller" ContainsContext="false" SoftwareRevision="35.00"><Controller Name="Synthetic">
        <Tags><Tag Name="Saved" DataType="DINT"><Description>Keep tag comment</Description><Data Format="L5K">42</Data></Tag></Tags>
        <DataTypes><DataType Name="Record" Class="User"><Members><Member Name="Value" DataType="DINT"/></Members></DataType></DataTypes>
        <Modules><Module Name="Network" Address="192.0.2.1"/></Modules><Tasks><Task Name="MainTask" Rate="10"/></Tasks>
        <AddOnInstructionDefinitions><EncodedData Name="Protected" EncodedType="AddOnInstructionDefinition"><Payload><![CDATA[original opaque bytes]]></Payload></EncodedData></AddOnInstructionDefinitions>
        <Programs><Program Name="Main"><Tags/><Routines>
        <Routine Name="Ladder" Type="RLL"><Description><![CDATA[Routine notes]]></Description><RLLContent>
        <Rung Number="0"><Comment><![CDATA[First line
        Second line]]></Comment><Text><![CDATA[XIC(Enable)OTE(Output);]]></Text></Rung>
        <Rung Number="1"><Comment><LocalizedComment Lang="en-US"><![CDATA[Literal notes]]></LocalizedComment></Comment><Text><![CDATA[MSG('A  B');]]></Text></Rung>
        </RLLContent></Routine>
        <Routine Name="Structured" Type="ST"><Description><![CDATA[ST notes]]></Description><STContent><Line Number="0"><![CDATA[Value := 'A  B'; // retained inline code comment]]></Line></STContent></Routine>
        <Routine Name="Unknown" Type="FBD"><Description>Unknown format notes</Description><FBDContent/></Routine>
        </Routines></Program></Programs></Controller></RSLogix5000Content>
        """);
    private async Task<ProjectInfo> Project(Action<XDocument>? edit = null)
    {
        var doc = Fixture(); edit?.Invoke(doc); var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x");
        File.WriteAllText(path, doc.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false)); return await service.RegisterAsync(path);
    }
    private static ProjectCompareRequest Request(ProjectInfo a, ProjectInfo b, string? view = null) => new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, RoutineView: view);
    private LadderCompareRequest Ladder(ProjectInfo a, ProjectInfo b, string? view = null, bool drawings = true)
    {
        var routine = service.CompareRoutines(Request(a, b, view)).Routines.Single(r => r.Name == "Ladder");
        return new(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, routine.LeftXPath, routine.RightXPath, Limit: 20, RoutineView: view, IncludeDrawings: drawings);
    }
    private static void BlankLines(XDocument doc)
    {
        foreach (var e in doc.Descendants().Where(e => e.Name.LocalName is "Description" or "Comment" or "LocalizedComment" &&
            e.Ancestors("Routine").Any(r => (string?)r.Attribute("Type") is "RLL" or "ST") && !e.HasElements).ToArray())
            e.Value = "\r\n\t\r\n" + e.Value.Replace("\n", "\n\n", StringComparison.Ordinal) + "\r\n";
    }

    [Fact]
    public async Task Annotation_blank_lines_are_an_explicit_view_and_exact_results_remain_available()
    {
        var a = await Project(); var b = await Project(BlankLines);
        var exact = service.CompareProjects(Request(a, b)); Assert.True(exact.TotalChanges > 0); Assert.Null(exact.View);
        Assert.Equal(JsonSerializer.Serialize(exact, ProjectService.Json), JsonSerializer.Serialize(service.CompareProjects(Request(a, b, "exact")), ProjectService.Json));
        foreach (var mode in new[] { "annotation-blank-lines", "instructions" })
        {
            var report = service.CompareProjects(Request(a, b, mode)); Assert.Equal(0, report.TotalChanges); Assert.Equal(mode, report.View!.Mode);
            var rows = service.CompareRoutines(Request(a, b, mode)); var routine = rows.Routines.Single(r => r.Name == "Ladder");
            Assert.Equal("unchanged", routine.Change); Assert.Equal("modified", routine.ExactChange);
            var ladder = service.CompareLadder(Ladder(a, b, mode, false)); Assert.All(ladder.Rungs, r => Assert.Equal("unchanged", r.Change));
            Assert.All(ladder.Rungs, r => Assert.Equal("modified", r.ExactChange)); Assert.NotEqual(ladder.Rungs[0].Left!.Comment, ladder.Rungs[0].Right!.Comment);
            Assert.Empty(service.CompareLadder(Ladder(a, b, mode, false) with { ChangesOnly = true }).Rungs);
            Assert.Equal(a.CurrentSha256, report.Left.CurrentSha256); Assert.Equal(b.CurrentSha256, report.Right.CurrentSha256);
        }
        Assert.Equal(a.SourceSha256, ProjectService.Hash(File.ReadAllBytes(a.SourcePath))); Assert.Equal(b.SourceSha256, ProjectService.Hash(File.ReadAllBytes(b.SourcePath)));
    }

    [Fact]
    public async Task Instructions_view_excludes_documentation_but_preserves_literals_inline_comments_and_instruction_whitespace()
    {
        var a = await Project(); var comment = await Project(d => d.Descendants("Comment").First().Value = "Different meaningful documentation");
        Assert.True(service.CompareProjects(Request(a, comment, "annotation-blank-lines")).TotalChanges > 0);
        Assert.Equal(0, service.CompareProjects(Request(a, comment, "instructions")).TotalChanges);
        foreach (var changedText in new[] { "MSG('A B');", "MSG('A$'B');", "MSG ('A  B');" })
        {
            var b = await Project(d => d.Descendants("Text").Last().Value = changedText);
            Assert.True(service.CompareProjects(Request(a, b, "instructions")).TotalChanges > 0);
        }
        var st = await Project(d => d.Descendants("Line").Single().Value = "Value := 'A  B'; // changed inline code comment");
        Assert.Equal("modified", service.CompareRoutines(Request(a, st, "instructions")).Routines.Single(r => r.Name == "Structured").Change);
    }

    [Fact]
    public async Task Values_layout_configuration_unsupported_XML_and_ciphertext_stay_exact_in_every_view()
    {
        var a = await Project(); var b = await Project(d =>
        {
            d.Descendants("Data").Single().Value = "43"; d.Descendants("Member").Single().SetAttributeValue("DataType", "REAL");
            d.Descendants("Module").Single().SetAttributeValue("Address", "192.0.2.2"); d.Descendants("Task").Single().SetAttributeValue("Rate", "20");
            d.Descendants("Payload").Single().Value = "different opaque bytes";
            d.Descendants("Routine").Single(r => (string?)r.Attribute("Type") == "FBD").Element("Description")!.Value += "\n\n";
        });
        var exact = service.CompareProjects(Request(a, b));
        foreach (var mode in new[] { "instructions", "annotation-blank-lines" })
        {
            var view = service.CompareProjects(Request(a, b, mode)); Assert.Equal(exact.TotalChanges, view.TotalChanges);
            Assert.Equal(exact.Differences.Select(d => (d.Kind, d.LeftSha256, d.RightSha256)), view.Differences.Select(d => (d.Kind, d.LeftSha256, d.RightSha256)));
        }
        var opaque = await Project(d => d.Descendants("Routine").First().Add(new XElement("EncodedData", "opaque")));
        var opaqueChanged = await Project(d => { d.Descendants("Routine").First().Add(new XElement("EncodedData", "opaque")); d.Descendants("Comment").First().Value += "\n\n"; });
        Assert.True(service.CompareProjects(Request(opaque, opaqueChanged, "instructions")).TotalChanges > 0);
        var unknown = await Project(d => { d.Descendants("Routine").First().Add(new XElement("Unknown", new XElement("Comment", "retain"))); d.Descendants("Tag").Single().Element("Description")!.Value = "Changed tag annotation"; });
        Assert.True(service.CompareProjects(Request(a, unknown, "instructions")).TotalChanges >= 2);
    }

    [Fact]
    public async Task Text_only_skips_parsing_and_geometry_without_changing_the_page_or_comparison()
    {
        var a = await Project(); var b = await Project(d => d.Descendants("Text").First().Value = "XIC(");
        var drawn = service.CompareLadder(Ladder(a, b)); var text = service.CompareLadder(Ladder(a, b, drawings: false));
        Assert.Equal(drawn.Total, text.Total); Assert.Equal(drawn.Rungs.Select(r => (r.Position, r.Change)), text.Rungs.Select(r => (r.Position, r.Change)));
        Assert.Equal("invalid", drawn.Rungs[0].Right!.Status);
        Assert.All(text.Rungs.SelectMany(r => new[] { r.Left, r.Right }).OfType<LadderRungPreview>(), r => { Assert.Null(r.Diagram); Assert.Equal("text-only", r.Status); Assert.Empty(r.Diagnostics); });
        Assert.Contains("not performed", text.Coverage, StringComparison.Ordinal);
        var huge = await Project(d => d.Descendants("Text").First().Value = string.Concat(Enumerable.Repeat("NOP()", 300)) + ";");
        Assert.Equal("unsupported", service.CompareLadder(Ladder(huge, huge)).Rungs[0].Left!.Status);
        Assert.Equal(1501, service.CompareLadder(Ladder(huge, huge, drawings: false)).Rungs[0].Left!.RawText.Length);
        var one = service.PreviewLadder(b.Id, new(b.CurrentSha256, Ladder(a, b).RightRoutineXPath!, IncludeDrawings: false)); Assert.Equal("text-only", one.Rungs[0].Status);
    }

    [Fact]
    public async Task Projection_alignment_paging_locate_and_saved_bookmarks_share_the_same_filter()
    {
        var a = await Project(); var b = await Project(d => { BlankLines(d); d.Descendants("Rung").First().AddBeforeSelf(new XElement("Rung", new XAttribute("Number", "0"), new XElement("Text", "NOP();"))); });
        var request = Request(a, b, "instructions"); var all = service.CompareLadder(Ladder(a, b, "instructions", false));
        Assert.Equal(new[] { "added", "unchanged", "unchanged" }, all.Rungs.Select(r => r.Change));
        var page = service.CompareLadder(Ladder(a, b, "instructions", false) with { Offset = 1, Limit = 1 }); Assert.Equal(0, page.Rungs.Single().Left!.Index); Assert.Equal(1, page.Rungs.Single().Right!.Index);
        var diff = Assert.Single(service.CompareProjects(request).Differences);
        var located = service.LocateComparedLadder(new(request, diff.LeftXPath, diff.RightXPath, true, false)); Assert.Equal(1, located.Ladder.Total); Assert.Equal("added", located.Ladder.Rungs.Single().Change);
        var c = Ladder(a, b, "instructions", false);
        var session = await service.SaveComparisonSessionAsync(new("Projected review", request, new(c.LeftRoutineXPath, c.RightRoutineXPath, 0, true)));
        Assert.Equal("instructions", (await service.GetComparisonSessionAsync(session.Id)).Session.Comparison.RoutineView);
        Assert.True((await service.GetComparisonSessionAsync(session.Id)).CanResume);
    }

    [Fact]
    public async Task Immutable_review_exports_retain_projection_and_original_source_hashes()
    {
        var a = await Project(); var b = await Project(BlankLines);
        var saved = await service.ExportComparisonReviewAsync(new(Request(a, b, "instructions"), "json", true));
        var readback = JsonSerializer.Deserialize<ComparisonReviewDocument>(File.ReadAllBytes(service.Download(saved.Artifact.Id).Path), ProjectService.Json)!;
        Assert.Equal("instructions", saved.Review.View!.Mode); Assert.Equal("instructions", readback.View!.Mode); Assert.Empty(readback.Changes);
        Assert.Equal(a.CurrentSha256, readback.Left.Sha256); Assert.Equal(b.CurrentSha256, readback.Right.Sha256);
        var html = await service.ExportComparisonReviewAsync(new(Request(a, b, "annotation-blank-lines")));
        Assert.Contains("annotation-blank-lines", File.ReadAllText(service.Download(html.Artifact.Id).Path));
    }

    [Fact]
    public async Task Full_controller_filtered_projection_keeps_values_and_exact_hashes()
    {
        var p = Path.Combine(root, "large.l5x"); FullControllerReviewTests.WriteController(p, false); var q = Path.Combine(root, "large-readback.l5x");
        var original = File.ReadAllText(p); var text = original.Replace("<Comment>Fixture rung ", "<Comment>\n\nFixture rung ", StringComparison.Ordinal).Replace("</Comment>", "\n</Comment>", StringComparison.Ordinal);
        Assert.NotEqual(original, text); File.WriteAllText(q, text, new UTF8Encoding(false));
        var a = await service.RegisterAsync(p); var b = await service.RegisterAsync(q);
        var scope = Request(a, b, "instructions") with { ProgramName = "P003_Mode", RoutineName = "Main" };
        Assert.Equal(0, service.CompareProjects(scope).TotalChanges); Assert.True(service.CompareProjects(scope with { RoutineView = null }).TotalChanges > 0);
        Assert.Equal("unchanged", Assert.Single(service.CompareRoutines(scope).Routines).Change);
    }

    [Fact]
    public void Published_bounds_and_enums_match_runtime_and_reject_invalid_schema_definitions()
    {
        var catalog = new LogixExtension().Describe();
        foreach (var (name, max) in new[] { ("studio_compare_ladder", 20), ("studio_ladder_preview", 20), ("studio_compare_projects", 200), ("studio_compare_routines", 200) })
        {
            var schema = catalog.Single(o => o.Name == name).InputSchema;
            var limit = schema.GetProperty("properties").GetProperty("request").GetProperty("properties").GetProperty("limit");
            Assert.Equal(1, limit.GetProperty("minimum").GetInt32()); Assert.Equal(max, limit.GetProperty("maximum").GetInt32());
            foreach (var value in new[] { 0, 1, max, max + 1 })
            {
                var number = JsonSerializer.SerializeToElement(value);
                if (value < 1 || value > max) Assert.Throws<ArgumentException>(() => ExtensionSchema.Validate(limit, number));
                else ExtensionSchema.Validate(limit, number);
            }
            ExtensionSchema.CheckDefinition(schema);
        }
        foreach (var invalid in new[] { "{\"minimum\":2,\"maximum\":1}", "{\"minimum\":\"oops\"}", "{\"unsupported\":true}" })
            using (var schema = JsonDocument.Parse(invalid)) Assert.Throws<ArgumentException>(() => ExtensionSchema.CheckDefinition(schema.RootElement));
        Assert.Throws<OperationException>(() => RoutineComparisonViews.Info("invented"));
        var projected = catalog.Single(o => o.Name == "studio_compare_projects").InputSchema;
        Assert.Throws<ArgumentException>(() => ExtensionSchema.Validate(projected,
            JsonSerializer.SerializeToElement(new { request = new ProjectCompareRequest("a", "sha", "b", "sha", RoutineView: "invented") }, ProjectService.Json)));
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
