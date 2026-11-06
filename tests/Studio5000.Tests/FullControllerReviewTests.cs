using System.Text;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class FullControllerReviewTests
{
    [Fact]
    public async Task FullDecoratedExportSupportsFilteredComparisonAndLocalTrace()
    {
        var root = Path.Combine(Path.GetTempPath(), "rockwell-full-controller-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = new ProjectService(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
            var leftFile = Path.Combine(root, "baseline.L5X");
            var rightFile = Path.Combine(root, "reviewed.L5X");
            WriteController(leftFile, false);
            WriteController(rightFile, true);
            var left = await service.RegisterAsync(leftFile);
            var right = await service.RegisterAsync(rightFile);
            var request = new ProjectCompareRequest(left.Id, left.CurrentSha256, right.Id, right.CurrentSha256, "P003_Mode", Limit: 1);
            var errorCompare = Record.Exception(() =>
            {
                var report = service.CompareProjects(request);
                Assert.Equal(2, report.FilteredChanges);
                Assert.Single(report.Differences);
                var next = service.CompareProjects(request with { Offset = 1 });
                Assert.NotEqual(report.Differences[0].Path, Assert.Single(next.Differences).Path);
                var scope = request with { Query = null, ProgramName = "P003_Mode", RoutineName = "Main" };
                Assert.Equal(2, service.CompareProjects(scope).TotalChanges);
                var routine = Assert.Single(service.CompareRoutines(scope).Routines);
                Assert.Equal(2, service.CompareLadder(new(left.Id, left.CurrentSha256, right.Id, right.CurrentSha256,
                    routine.LeftXPath, routine.RightXPath, ChangesOnly: true)).Total);
            });
            var errorTrace = Record.Exception(() =>
            {
                var trace = service.TraceLogic(right.Id, new(right.CurrentSha256, "Requested", "P003_Mode", Depth: 1));
                Assert.Equal("Program:P003_Mode.Requested", trace.Root);
                Assert.Contains(trace.Dependencies, d => d.From == trace.Root && d.To == "Program:P003_Mode.Enabled");
                Assert.Contains(trace.Usages, u => u.Symbol == trace.Root && u.Instruction == "XIC" && u.Role == "read");
            });
            Assert.True(errorCompare is null && errorTrace is null, $"Compare: {errorCompare?.Message}\nTrace: {errorTrace?.Message}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static void WriteController(string path, bool modified)
    {
        using var output = new StreamWriter(path, false, new UTF8Encoding(false));
        output.Write("<RSLogix5000Content SoftwareRevision=\"35.00\"><Controller Name=\"SyntheticController\"><DataTypes><DataType Name=\"RecordType\"><Members>");
        for (var i = 0; i < 32; i++)
            output.Write($"<Member Name=\"Member{i}\" DataType=\"DINT\" Dimension=\"0\" Radix=\"Decimal\"/>");
        output.Write("</Members></DataType></DataTypes><Tags>");
        // 24 decorated structure arrays with 576 populated elements each.
        for (var tag = 0; tag < 24; tag++)
        {
            output.Write($"<Tag Name=\"Batch_{tag}\" TagType=\"Base\" DataType=\"RecordType\" Dimensions=\"576\"><Data Format=\"Decorated\"><Array DataType=\"RecordType\" Dimensions=\"576\">");
            for (var row = 0; row < 576; row++)
            {
                output.Write($"<Element Index=\"[{row}]\"><Structure DataType=\"RecordType\">");
                for (var member = 0; member < 32; member++)
                    output.Write($"<DataValueMember Name=\"Member{member}\" DataType=\"DINT\" Radix=\"Decimal\" Value=\"{row + member}\"/>");
                output.Write("</Structure></Element>");
            }
            output.Write("</Array></Data></Tag>");
        }
        output.Write("</Tags><Programs>");
        for (var program = 0; program < 50; program++)
        {
            var name = program == 3 ? "P003_Mode" : $"P{program:D3}_Work";
            output.Write($"<Program Name=\"{name}\" MainRoutineName=\"Main\"><Tags>");
            output.Write("<Tag Name=\"Requested\" TagType=\"Base\" DataType=\"BOOL\"/><Tag Name=\"Enabled\" TagType=\"Base\" DataType=\"BOOL\"/></Tags><Routines>");
            for (var routine = 0; routine < 5; routine++)
            {
                output.Write($"<Routine Name=\"{(routine == 0 ? "Main" : "Work_" + routine)}\" Type=\"RLL\"><RLLContent>");
                for (var rung = 0; rung < 58; rung++)
                {
                    var code = program == 3 && routine == 0 && rung == 4
                        ? (modified ? "XIC(Requested)OTE(Enabled);" : "XIO(Requested)OTE(Enabled);")
                        : program == 3 && routine == 0 && rung == 32 && modified ? "XIO(Enabled);"
                        : program == 3 ? "NOP();" : "XIC(Requested)OTE(Enabled);";
                    var comment = program == 3 && routine == 0 ? $"<Comment>Fixture rung {rung}</Comment>" : "";
                    output.Write($"<Rung Number=\"{rung}\" Type=\"N\">{comment}<Text><![CDATA[{code}]]></Text></Rung>");
                }
                output.Write("</RLLContent></Routine>");
            }
            output.Write("</Routines></Program>");
        }
        output.Write("</Programs></Controller></RSLogix5000Content>");
    }
}
