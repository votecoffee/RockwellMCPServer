using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class LogicMemberTraceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellMemberTrace", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public LogicMemberTraceTests()
    {
        Directory.CreateDirectory(root);
        service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
    }

    private async Task<ProjectInfo> Project(string[]? additional = null, int noise = 0)
    {
        var members = new XElement("Members",
            new XElement("Member", new XAttribute("Name", "Packet"), new XAttribute("DataType", "DINT")),
            new XElement("Member", new XAttribute("Name", "PacketFlag"), new XAttribute("DataType", "BIT"), new XAttribute("Target", "Packet"), new XAttribute("BitNumber", "22")),
            new XElement("Member", new XAttribute("Name", "Words"), new XAttribute("DataType", "DINT"), new XAttribute("Dimension", "4")),
            Enumerable.Range(0, 64).Select(i => new XElement("Member", new XAttribute("Name", "Noise" + i), new XAttribute("DataType", "DINT"))));
        XElement Tag(string name, string type, string? dimensions = null) => new("Tag", new XAttribute("Name", name),
            new XAttribute("TagType", "Base"), new XAttribute("DataType", type), dimensions is null ? null : new XAttribute("Dimensions", dimensions));
        XElement Alias(string name, string target) => new("Tag", new XAttribute("Name", name), new XAttribute("TagType", "Alias"), new XAttribute("AliasFor", target));
        var tags = new XElement("Tags", Tag("O", "RobotOutput"), Tag("Source", "DINT"), Tag("SourceByte", "SINT"), Tag("Other", "DINT"),
            Tag("Consumed", "BOOL"), Tag("LengthValue", "DINT"), Tag("SourceWords", "DINT", "4"), Tag("SourceInts", "INT", "8"),
            Tag("Clone", "RobotOutput"), Alias("WordAlias", "O.Packet"), Alias("BitAlias", "WordAlias.22"));
        var code = new[]
        {
            "OTE(BitAlias);",
            "XIC(O.Packet.22)OTE(Consumed);",
            "BTD(Source,4,O.Packet,20,4);",
            "BTD(Source,0,O.Packet,0,8);",
            "COP(Source,O.Packet,1);",
            "MOV(Source,O.Noise0);",
            "OTE(O.Packet.21);",
            "XIC(O.Packet.23)OTE(Consumed);",
            "BTD(O.Packet,20,Other,4,4);",
            "COP(O.Packet,Other,1);",
            "XIC(WordAlias.22)OTE(Consumed);"
        }.Concat(additional ?? []).Concat(Enumerable.Range(0, noise).Select(i => $"MOV(Source,O.Noise{i % 64});"));
        var routine = new XElement("Routine", new XAttribute("Name", "Main"), new XAttribute("Type", "RLL"),
            new XElement("RLLContent", code.Select((text, index) => new XElement("Rung", new XAttribute("Number", index), new XElement("Text", new XCData(text))))));
        var doc = new XDocument(new XElement("RSLogix5000Content", new XAttribute("SoftwareRevision", "35.00"),
            new XElement("Controller", new XAttribute("Name", "SyntheticRobot"), new XElement("DataTypes",
                new XElement("DataType", new XAttribute("Name", "RobotOutput"), members)), tags,
                new XElement("Programs", new XElement("Program", new XAttribute("Name", "Example"), new XElement("Tags"), new XElement("Routines", routine))))));
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".L5X");
        await File.WriteAllTextAsync(path, doc.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false));
        return await service.RegisterAsync(path);
    }

    [Fact]
    public async Task Exact_bit_finds_alias_writers_and_readers_without_unrelated_UDT_members()
    {
        var project = await Project(noise: 8000);
        var request = new LogicTraceRequest(project.CurrentSha256, "O.Packet.22", Depth: 1, Role: "write");
        var result = service.TraceLogic(project.Id, request);
        Assert.Equal("Controller.O.Packet.22", result.Root);
        Assert.Equal(3, result.TotalUsages);
        Assert.Contains(result.Usages, u => u.Instruction == "BTD" && u.Region!.BitOffset == 20 && u.Region.BitLength == 4);
        Assert.DoesNotContain(result.Usages, u => u.Operand.Contains("Noise", StringComparison.Ordinal));
        Assert.Contains(result.Usages, u => u.Operand == "BitAlias" && u.AliasChain.Contains("Controller.WordAlias"));
        Assert.Contains("Controller.Source.6", result.Symbols);
        Assert.Contains("Controller.Source.22", result.Symbols);
        Assert.Contains("Controller.Other.6", result.Symbols);
        Assert.DoesNotContain(result.Symbols, s => s.Contains("Noise", StringComparison.Ordinal));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result).Length < 1024 * 1024);
        var read = service.TraceLogic(project.Id, request with { TagName = "O", MemberPath = "Packet.22", Role = "read" });
        Assert.Equal(4, read.TotalUsages);
        Assert.Contains(read.Usages, u => u.Instruction == "BTD" && u.Role == "read");
        var alias = service.TraceLogic(project.Id, request with { TagName = "BitAlias", Role = "all" });
        Assert.Equal(result.Root, alias.Root);
        Assert.Equal(7, alias.TotalUsages);
        Assert.Equal(project.SourceSha256, ProjectService.Hash(File.ReadAllBytes(project.SourcePath)));
        Assert.Empty(service.SdkJobList());
    }

    [Fact]
    public async Task Whole_base_tag_results_are_paged_with_stable_nonoverlapping_offsets()
    {
        var project = await Project(noise: 8000);
        var request = new LogicTraceRequest(project.CurrentSha256, "O", Depth: 0, Limit: 17, DependencyLimit: 7, Role: "write");
        var first = service.TraceLogic(project.Id, request);
        Assert.True(first.TotalUsages > 8000);
        Assert.Equal(17, first.Usages.Length);
        Assert.Equal(17, first.NextOffset);
        var next = service.TraceLogic(project.Id, request with { Offset = first.NextOffset!.Value });
        Assert.Equal(first.TotalUsages, next.TotalUsages);
        Assert.DoesNotContain(first.Usages.Select(u => u.SourceXPath), path => next.Usages.Any(u => u.SourceXPath == path));
        Assert.True(first.Limited);
        var all = service.TraceLogic(project.Id, request with { Offset = first.TotalUsages });
        Assert.Empty(all.Usages);
        Assert.Null(all.NextOffset);
    }

    [Theory]
    [InlineData("COP(SourceWords[0],O.Words[1],2);", "Controller.SourceWords[1].22")]
    [InlineData("CPS(SourceWords[0],O.Words[1],2);", "Controller.SourceWords[1].22")]
    [InlineData("COP(SourceInts[0],O.Words[1],2);", "Controller.SourceInts[3].6")]
    public async Task Copies_use_destination_element_width_and_the_selected_bit_offset(string code, string mapped)
    {
        var project = await Project([code]);
        var result = service.TraceLogic(project.Id, new(project.CurrentSha256, "O.Words[2].22", Depth: 1, Direction: "upstream", Role: "write"));
        var write = Assert.Single(result.Usages);
        Assert.Equal(64, write.Region!.BitLength);
        Assert.Contains(mapped, result.Symbols);
        Assert.DoesNotContain(result.Usages, u => u.Instruction == "BTD");
        Assert.Contains(result.Dependencies, d => d.Mapping == "bitwise");
    }

    [Theory]
    [InlineData("BTD(Source,0,O.Packet,20,LengthValue);", "btd_range_unknown")]
    [InlineData("COP(Clone,O,1);", "copy_byte_layout_or_length_unknown")]
    [InlineData("COP(SourceWords[0],O.Words[3],2);", "copy_byte_layout_or_length_unknown")]
    public async Task Unresolved_transfer_ranges_remain_visible_possible_writes(string code, string reason)
    {
        var project = await Project([code]);
        var result = service.TraceLogic(project.Id, new(project.CurrentSha256, "O.Packet.22", Depth: 0, Role: "write"));
        Assert.Contains(result.Usages, u => u.Region?.Issue == reason);
        Assert.Contains(result.Diagnostics, issue => issue.Contains(reason, StringComparison.Ordinal));
        Assert.True(result.Partial);
    }

    [Fact]
    public async Task Dependency_pages_and_role_filters_are_independent_and_hash_bound()
    {
        var project = await Project();
        var request = new LogicTraceRequest(project.CurrentSha256, "O.Packet.22", Depth: 1, Limit: 1, DependencyLimit: 1, Role: "write");
        var first = service.TraceLogic(project.Id, request);
        Assert.Single(first.Usages); Assert.Single(first.Dependencies);
        Assert.NotNull(first.NextOffset); Assert.NotNull(first.NextDependencyOffset);
        var next = service.TraceLogic(project.Id, request with { DependencyOffset = first.NextDependencyOffset!.Value });
        Assert.Equal(first.TotalDependencies, next.TotalDependencies);
        Assert.NotEqual(first.Dependencies[0], next.Dependencies[0]);
        Assert.Equal(JsonSerializer.Serialize(first.Usages[0]), JsonSerializer.Serialize(next.Usages[0]));
        Assert.Equal("stale_revision", Assert.Throws<OperationException>(() => service.TraceLogic(project.Id, request with { ExpectedSha256 = "stale" })).Code);
        Assert.Equal("logic_trace_query", Assert.Throws<OperationException>(() => service.TraceLogic(project.Id, request with { Limit = 201 })).Code);
        foreach (var operand in new[] { "O.Packet.32", "O.Missing.1", "O.Words[4].2", "O.Packet.2.Bad" })
            Assert.Equal("logic_trace_selector", Assert.Throws<OperationException>(() => service.TraceLogic(project.Id, request with { TagName = operand })).Code);
        Assert.Equal("logic_trace_query", Assert.Throws<OperationException>(() => service.TraceLogic(project.Id, request with { TagName = "O.Words[Index].1" })).Code);
    }

    [Fact]
    public async Task Declared_packed_BIT_target_and_same_word_transfers_keep_the_exact_bit_identity()
    {
        var project = await Project(["OTE(O.PacketFlag);", "BTD(O.Packet,0,O.Packet,22,1);"]);
        var result = service.TraceLogic(project.Id, new(project.CurrentSha256, "O.Packet.22", Depth: 1, Role: "write"));
        Assert.Contains(result.Usages, u => u.Operand == "O.PacketFlag" && u.Region?.BitOffset == 22 && u.Region.BitLength == 1);
        Assert.Contains("Controller.O.Packet.0", result.Symbols);
        Assert.Contains(result.Dependencies, e => e.From == e.To && e.Mapping == "bitwise");
        var flag = service.TraceLogic(project.Id, new(project.CurrentSha256, "O", MemberPath: "PacketFlag", Depth: 0, Role: "write"));
        Assert.Equal("Controller.O.Packet.22", flag.Root);
        Assert.Equal(result.TotalUsages, flag.TotalUsages);
    }

    [Fact]
    public async Task Unknown_effects_remain_in_reader_and_writer_filters()
    {
        var project = await Project(["CustomInstruction(O.Packet);"]);
        var result = service.TraceLogic(project.Id, new(project.CurrentSha256, "O.Packet.22", Depth: 0, Role: "write"));
        Assert.Contains(result.Usages, u => u.Instruction == "CustomInstruction" && u.Role == "unknown");
        Assert.True(result.Partial);
    }

    [Fact]
    public async Task Byte_limited_pages_resume_immediately_after_the_last_emitted_row()
    {
        var index = new string('I', 12000);
        var project = await Project(Enumerable.Repeat($"CustomInstruction(O.Words[{index}]);", 100).ToArray());
        var request = new LogicTraceRequest(project.CurrentSha256, "O", Depth: 0, Limit: 200, Role: "unknown");
        var first = service.TraceLogic(project.Id, request);
        Assert.Equal(100, first.TotalUsages);
        Assert.InRange(first.Usages.Length, 1, 99);
        Assert.Equal(first.Usages.Length, first.NextOffset);
        var next = service.TraceLogic(project.Id, request with { Offset = first.NextOffset!.Value });
        Assert.Equal(100, first.Usages.Length + next.Usages.Length);
        Assert.Null(next.NextOffset);
        Assert.DoesNotContain(first.Usages.Select(u => u.SourceXPath), path => next.Usages.Any(u => u.SourceXPath == path));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(first.Usages).Length < 1024 * 1024);
        Assert.True(first.Partial);
    }

    [Fact]
    public async Task BTD_zero_fill_and_destination_truncation_do_not_invent_source_bits()
    {
        var project = await Project(["BTD(SourceByte,0,O.Packet,0,32);"]);
        var request = new LogicTraceRequest(project.CurrentSha256, "O.Packet.22", Depth: 1, Direction: "upstream");
        var filled = service.TraceLogic(project.Id, request);
        Assert.Contains(filled.Usages, u => u.Instruction == "BTD" && u.Region?.BitLength == 32);
        Assert.DoesNotContain(filled.Symbols, s => s.StartsWith("Controller.SourceByte", StringComparison.Ordinal));
        var copied = service.TraceLogic(project.Id, request with { TagName = "O.Packet.6" });
        Assert.Contains("Controller.SourceByte.6", copied.Symbols);

        var truncated = await Project(["BTD(Source,0,SourceByte,4,32);"]);
        var source = service.TraceLogic(truncated.Id, new(truncated.CurrentSha256, "Source.22", Depth: 1, Direction: "downstream"));
        Assert.DoesNotContain(source.Symbols, s => s.StartsWith("Controller.SourceByte", StringComparison.Ordinal));
    }

    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
