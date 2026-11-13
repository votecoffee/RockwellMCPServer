using System.Text;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class LadderOutputCoverageTests : IDisposable
{
    private const string Check = "rll-output-coverage";
    private const string Counterexample = "MOV(NextId,RecordId)ADD(NextId,1,NextId)[GRT(NextId,100000)MOV(1,NextId)OTL(ResetEvent),LEQ(NextId,100000)];";
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellOutputCoverage", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;

    public LadderOutputCoverageTests()
    {
        Directory.CreateDirectory(root);
        service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
    }

    private async Task<ProjectInfo> Register(string text, bool aoi = false)
    {
        var routine = new XElement("Routine", new XAttribute("Name", "Main"), new XAttribute("Type", "RLL"),
            new XElement("RLLContent", new XElement("Rung", new XAttribute("Number", "8"), new XElement("Text", new XCData(text)))));
        var owner = aoi
            ? new XElement("AddOnInstructionDefinitions", new XElement("AddOnInstructionDefinition", new XAttribute("Name", "Example"), new XElement("Routines", routine)))
            : new XElement("Programs", new XElement("Program", new XAttribute("Name", "Example"), new XAttribute("MainRoutineName", "Main"), new XElement("Routines", routine)));
        var document = new XDocument(new XElement("RSLogix5000Content", new XElement("Controller", new XAttribute("Name", "Synthetic"), owner)));
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".L5X");
        await File.WriteAllTextAsync(path, document.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false));
        return await service.RegisterAsync(path);
    }

    private static MergeSnapshot Snapshot(ProjectInfo project) => new(project.Id, project.CurrentSha256);

    [Fact]
    public async Task Native_counterexample_passes_grammar_but_fails_its_exact_terminal_alternative()
    {
        var project = await Register(Counterexample);
        var report = await service.PreviewLogixValidationAsync(new("offline-integrity", Snapshot(project)));
        Assert.Equal("pass", report.Checks.Single(c => c.Name == LadderBranchGrammar.CheckName).Status);
        Assert.Equal("pass", report.Checks.Single(c => c.Name == LadderPreflight.DepthCheck).Status);
        Assert.Equal("fail", report.Status);
        var missing = Assert.Single(report.Findings, f => f.Check == Check);
        Assert.Equal("fail", missing.Status);
        Assert.EndsWith("/rung/part[3]/leg[2]/part[1]", missing.Location);
        Assert.Contains("LEQ", missing.Detail);
        Assert.NotNull(missing.ActualSha256);
        Assert.Equal(ProjectService.Hash(File.ReadAllBytes(project.SourcePath)), project.SourceSha256);
        Assert.Empty(service.SdkJobList());
    }

    [Theory]
    [InlineData("MOV(1,N)[XIC(A)OTE(C),XIO(B)];", 1)]
    [InlineData("MOV(1,N)XIC(A);", 1)]
    [InlineData("[XIC(A),XIO(B)];", 2)]
    [InlineData("BST XIC(A)OTE(C) NXB XIO(B) BND;", 1)]
    [InlineData("[XIC(A)OTE(C),[XIC(B)MOV(1,N),LEQ(N,100)]];", 1)]
    [InlineData("XIC(A);", 1)]
    public void Known_terminal_inputs_fail_even_after_an_earlier_output(string text, int count)
    {
        var findings = LadderPreflight.Inspect(text).Findings.Where(f => f.Check == Check).ToArray();
        Assert.Equal(count, findings.Length);
        Assert.All(findings, f => Assert.Equal("fail", f.Status));
    }

    [Theory]
    [InlineData("[XIC(A),XIC(B)]OTE(C);")]
    [InlineData("[XIC(A),XIC(B)]MOV(1,N);")]
    [InlineData("BST XIC(A) NXB XIC(B) BND MOV(1,N);")]
    [InlineData("MOV(1,N)[XIC(A),XIC(B)]OTE(C);")]
    [InlineData("[XIC(A),[XIC(B),XIO(D)]]OTE(C);")]
    [InlineData("[XIC(A)OTE(C),XIO(B)MOV(1,N)];")]
    [InlineData("[XIC(A),XIC(B)]ADD(N,1,N);")]
    [InlineData("[XIC(A),XIO(B)]TON(T,100,0);")]
    [InlineData("EQU(Text,'[NXB,LEQ(A,B)]')MOV(Table[Index[0]],Value);")]
    [InlineData("xic(A)ote(B);")]
    [InlineData("NOP();")]
    [InlineData("XIC(A)JSR(Other,0);")]
    public void Common_or_independent_terminal_outputs_cover_the_input_branches(string text)
    {
        Assert.DoesNotContain(LadderPreflight.Inspect(text).Findings, f => f.Check == Check);
    }

    [Theory]
    [InlineData("CustomAOI(State);")]
    [InlineData("MOV(1,N)[XIC(A)OTE(C),CustomInstruction(State)];")]
    [InlineData("MOV(1,N)CustomInstruction(State);")]
    [InlineData("[XIC(A)]OTE(C);")]
    public void Unknown_terminal_instructions_and_unparseable_branches_are_unsupported(string text)
    {
        var findings = LadderPreflight.Inspect(text).Findings.Where(f => f.Check == Check).ToArray();
        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal("unsupported", f.Status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inherited_failures_still_block_and_repairs_are_reported_resolved(bool aoi)
    {
        var baseline = await Register(Counterexample, aoi);
        var current = await Register(Counterexample, aoi);
        var report = await service.PreviewLogixValidationAsync(new("offline-integrity", Snapshot(current), Baseline: Snapshot(baseline)));
        Assert.Equal("fail", report.Status);
        Assert.Contains(report.Findings, f => f.Check == Check && f.Classification == "inherited" && f.Status == "fail");
        var repaired = await Register("[XIC(A),XIC(B)]OTE(C);", aoi);
        var fixedReport = await service.PreviewLogixValidationAsync(new("offline-integrity", Snapshot(repaired), Baseline: Snapshot(baseline)));
        Assert.Equal("pass", fixedReport.Status);
        Assert.Contains(fixedReport.Findings, f => f.Check == Check && f.Classification == "resolved");
    }

    [Fact]
    public async Task Native_expected_differences_cannot_waive_a_missing_output()
    {
        var project = await Register(Counterexample);
        var request = new LogixValidationRequest("native-readback", Snapshot(project), Reference: Snapshot(project));
        var report = await service.PreviewLogixValidationAsync(request);
        var finding = Assert.Single(report.Findings, f => f.Check == Check);
        var ex = await Assert.ThrowsAsync<OperationException>(() => service.PreviewLogixValidationAsync(request with
        {
            ExpectedDifferences = [new(Check, finding.Location, null, finding.ActualSha256, "Cannot waive missing output")]
        }));
        Assert.Equal("validation_policy", ex.Code);
    }

    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
