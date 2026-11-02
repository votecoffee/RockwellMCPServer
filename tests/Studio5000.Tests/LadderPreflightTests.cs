using System.Text;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class LadderPreflightTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellLadderPreflight", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public LadderPreflightTests()
    {
        Directory.CreateDirectory(root);
        service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
    }
    private static string Nested(int depth, bool keyword = false)
    {
        var text = "XIC(Rows[Index[0]].Ready)";
        for (var i = 0; i < depth; i++) text = keyword ? "BST " + text + " NXB XIC(Alternate) BND " : "[" + text + ",XIC(Alternate)]";
        return text + "OTE(Selected);";
    }
    [Theory]
    [InlineData(0, false)][InlineData(1, false)][InlineData(6, false)][InlineData(7, false)][InlineData(8, false)]
    [InlineData(6, true)][InlineData(7, true)][InlineData(8, true)]
    public void Depth_counts_control_branches_only_and_uses_the_documented_six_level_limit(int depth, bool keyword)
    {
        var text = Nested(depth, keyword); var report = LadderPreflight.Inspect(text);
        Assert.Equal(depth, report.MaximumBranchDepth); Assert.Equal(6, report.MaximumAllowedBranchDepth);
        Assert.Equal("logix5000-pm008j", report.NativeProfile);
        Assert.Equal(depth > 6 ? 1 : 0, report.Findings.Count(f => f.Check == LadderPreflight.DepthCheck && f.Status == "fail"));
        if (depth > 6) Assert.Equal("ladder_native_branch_depth", Assert.Throws<OperationException>(() => LadderPreflight.ValidateNativeDepth(text)).Code);
        else Assert.Equal(depth, LadderPreflight.ValidateNativeDepth(text));
    }
    [Fact]
    public void Many_parallel_legs_array_commas_and_quoted_branch_tokens_do_not_add_nesting()
    {
        var text = "[" + string.Join(",", Enumerable.Repeat("EQU(Table[1,2],'[BST,NXB,BND]')", 32)) + "]OTE(Out);";
        var report = LadderPreflight.Inspect(text); Assert.Equal(1, report.MaximumBranchDepth); Assert.Empty(report.Findings);
    }
    [Theory]
    [InlineData("XIC(WaitForCont)XIC(Ready)XIC(WaitForCont)OTE(Out);", 1)]
    [InlineData("XIC(WaitForCont)MOV(1,Value)XIC(WaitForCont)OTE(Out);", 0)]
    [InlineData("XIC(WaitForCont)[XIC(WaitForCont),XIC(Ready)]XIC(WaitForCont)OTE(Out);", 0)]
    [InlineData("[XIC(Ready)XIC(Ready),XIC(Ready)]OTE(Out);", 1)]
    [InlineData("EQU(Text,'a')EQU(Text,'A')OTE(Out);", 0)]
    [InlineData("XIC(A)JSR(Main,0)XIC(A)OTE(Out);", 0)]
    [InlineData("NOP( );", 0)]
    public void Exact_duplicate_predicates_warn_only_within_uninterrupted_series(string text, int warnings)
    {
        var report = LadderPreflight.Inspect(text);
        Assert.Equal(warnings, report.Findings.Count(f => f.Check == LadderPreflight.DuplicateCheck && f.Status == "warning"));
    }
    private async Task<ProjectInfo> Register(string text, bool aoi = false)
    {
        var routine = new XElement("Routines", new XElement("Routine", new XAttribute("Name", "Main"), new XAttribute("Type", "RLL"),
            new XElement("RLLContent", new XElement("Rung", new XAttribute("Number", "5"), new XElement("Text", new XCData(text))))));
        var owner = aoi ? new XElement("AddOnInstructionDefinitions", new XElement("AddOnInstructionDefinition", new XAttribute("Name", "Selector"), routine))
            : new XElement("Programs", new XElement("Program", new XAttribute("Name", "Example"), new XAttribute("MainRoutineName", "Main"), routine));
        var doc = new XDocument(new XElement("RSLogix5000Content", new XElement("Controller", new XAttribute("Name", "Fixture"), owner)));
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); await File.WriteAllTextAsync(path, doc.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false));
        return await service.RegisterAsync(path);
    }
    private static MergeSnapshot Snapshot(ProjectInfo p) => new(p.Id, p.CurrentSha256);
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Program_and_readable_AOI_depth_findings_are_hash_bound_and_block_when_inherited(bool aoi)
    {
        var baseline = await Register(Nested(8), aoi); var same = await Register(Nested(8), aoi);
        var report = await service.PreviewLogixValidationAsync(new("offline-integrity", Snapshot(same), Baseline: Snapshot(baseline)));
        Assert.Equal("fail", report.Status);
        var finding = Assert.Single(report.Findings, f => f.Check == LadderPreflight.DepthCheck);
        Assert.Equal("inherited", finding.Classification); Assert.NotNull(finding.ActualSha256); Assert.Contains("depth 8", finding.Detail);
        var repaired = await Register(Nested(1), aoi);
        var resolved = await service.PreviewLogixValidationAsync(new("offline-integrity", Snapshot(repaired), Baseline: Snapshot(baseline)));
        Assert.Equal("pass", resolved.Status); Assert.Contains(resolved.Findings, f => f.Check == LadderPreflight.DepthCheck && f.Classification == "resolved");
        Assert.Equal(0, service.Get(same.Id).Revision); Assert.Empty(service.SdkJobList());
    }
    [Fact]
    public async Task Warnings_are_visible_nonblocking_and_native_expected_differences_cannot_waive_depth()
    {
        var repeated = await Register("XIC(WaitForCont)XIC(WaitForCont)OTE(Out);");
        var report = await service.PreviewLogixValidationAsync(new("offline-integrity", Snapshot(repeated)));
        Assert.Equal("pass", report.Status); Assert.Equal(1, report.Checks.Single(c => c.Name == LadderPreflight.DuplicateCheck).Warnings);
        var deep = await Register(Nested(7)); var request = new LogixValidationRequest("native-readback", Snapshot(deep), Reference: Snapshot(deep));
        var bad = await service.PreviewLogixValidationAsync(request); var finding = Assert.Single(bad.Findings, f => f.Check == LadderPreflight.DepthCheck);
        var ex = await Assert.ThrowsAsync<OperationException>(() => service.PreviewLogixValidationAsync(request with
        { ExpectedDifferences = [new(finding.Check, finding.Location, null, finding.ActualSha256, "Cannot waive depth")] }));
        Assert.Equal("validation_policy", ex.Code); Assert.Empty(service.SdkJobList());
    }
    [Fact]
    public void Malformed_and_over_budget_text_remain_explicitly_unsupported()
    {
        Assert.All(LadderPreflight.Inspect("[XIC(A)]OTE(Out);").Findings, f => Assert.Equal("unsupported", f.Status));
        Assert.Null(LadderPreflight.Inspect(Nested(17)).MaximumBranchDepth);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
