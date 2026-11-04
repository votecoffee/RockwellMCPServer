using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class TypeDependencyTests
{
    private static string Definition(string name, string memberType) => $"<DataType Name='{name}'><Description><![CDATA[Δ {name}]]></Description><Members><Member Name='Value' DataType='{memberType}'/></Members></DataType>\r\n";
    private static byte[] Snapshot(bool modified) => Encoding.UTF8.GetBytes($"""
        <RSLogix5000Content SchemaRevision='1.0' SoftwareRevision='35.00' TargetType='Controller' ContainsContext='false'><Controller Name='Fixture'>
        <DataTypes>{Definition("Leaf", "DINT")}{Definition("Legacy", "Leaf")}{(modified ? Definition("NewType", "Legacy") + Definition("Unused", "REAL") : "")}</DataTypes>
        <Tags><Tag Name='GlobalBase' TagType='Base' DataType='Legacy'><Data Format='L5K'><![CDATA[[[987654321]]]]></Data></Tag><Tag Name='GlobalAlias' TagType='Alias' AliasFor='GlobalBase.Value'/></Tags>
        <Programs><Program Name='App' MainRoutineName='Main'><Tags><Tag Name='Existing' TagType='Base' DataType='Legacy'><Data Format='L5K'><![CDATA[[[41]]]]></Data></Tag>
        <Tag Name='LocalAlias' TagType='Alias' AliasFor='GlobalAlias'/>{(modified ? "<Tag Name='NewLocal' TagType='Base' DataType='NewType'><Data Format='L5K'><![CDATA[[[[0]]]]]]></Data></Tag>" : "")}</Tags>
        <Routines><Routine Name='Main' Type='RLL'><RLLContent><Rung Number='0'><Text>NOP();</Text></Rung></RLLContent></Routine></Routines></Program></Programs>
        <Tasks><Task Name='MainTask'><ScheduledPrograms><ScheduledProgram Name='App'/></ScheduledPrograms></Task></Tasks></Controller></RSLogix5000Content>
        """);
    private static TypeDependencyPackageRequest Request(byte[] source, byte[] baseline) => new(new("source", ProjectService.Hash(source)), new("baseline", ProjectService.Hash(baseline)), "App");
    private static byte[] Bytes(XDocument doc) => Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
    private static string Read(ZipArchive zip, string path) { using var reader = new StreamReader(zip.GetEntry(path)!.Open()); return reader.ReadToEnd(); }

    [Fact]
    public void Source_closure_includes_legacy_nested_types_and_alias_targets_in_dependency_order()
    {
        var a = Snapshot(true); var b = Snapshot(false); var package = LogixTypeDependencies.Build(a, b, Request(a, b));
        Assert.Equal(["Leaf", "Legacy", "NewType"], package.Closure.Definitions.Select(d => d.Name));
        Assert.Equal(["DINT"], package.Closure.Definitions[0].References);
        Assert.Equal(["Leaf"], package.Closure.Definitions[1].References);
        Assert.Equal("use_existing_never_overwrite", package.Closure.Definitions[0].DestinationOperation);
        Assert.Equal("create_new_after_native_review", package.Closure.Definitions[2].DestinationOperation);
        Assert.Contains(package.Closure.TagReferences, t => t.Name == "GlobalAlias" && t.AliasFor == "GlobalBase.Value");
        Assert.Contains(package.Closure.TagReferences, t => t.Name == "GlobalBase" && t.DataType == "Legacy");
        Assert.True(package.Closure.CompleteForDeclaredSourceGraph); Assert.False(package.Closure.NativePreconfigurationAccepted);
        Assert.DoesNotContain(package.Closure.Definitions, d => d.Name == "Unused");
        var xml = XmlProjects.Parse(package.TypesXml!);
        Assert.Equal("Reference", (string?)xml.Descendants("DataType").First().Attribute("Use"));
        Assert.Equal("Target", (string?)xml.Descendants("DataType").Last().Attribute("Use"));
        Assert.DoesNotContain(xml.Descendants(), e => e.Name.LocalName is "Tag" or "Tags" or "Data" or "Program" or "Task" or "Module" or "Routine");
        Assert.DoesNotContain("987654321", Encoding.UTF8.GetString(package.TypesXml!));
        using var zip = new ZipArchive(new MemoryStream(package.Zip));
        foreach (var definition in package.Closure.Definitions)
        {
            var original = Read(zip, "source-definitions/" + definition.Name + ".xml");
            Assert.Contains(original, Encoding.UTF8.GetString(a));
            Assert.Equal(definition.SourceRawSha256, ProjectService.Hash(Encoding.UTF8.GetBytes(original)));
        }
    }
    [Fact]
    public void Whole_program_default_carries_complete_legacy_context_and_keeps_new_definition_authorization_explicit()
    {
        var a = Snapshot(true); var b = Snapshot(false); var request = new ProgramImportPackageRequest(new("source", ProjectService.Hash(a)), new("baseline", ProjectService.Hash(b)), "App", "MainTask", NewDataTypes: ["NewType"]);
        var package = ProgramImportPackaging.Build(a, b, request);
        Assert.Equal(["Leaf", "Legacy", "NewType"], XmlProjects.Parse(package.ProgramXml).Descendants("DataType").Select(e => (string?)e.Attribute("Name")));
        Assert.True(package.Manifest.TypeClosure!.CompleteForDeclaredSourceGraph); Assert.False(package.Manifest.NativePreconfigurationAccepted);
        Assert.Equal(["NewType"], package.Manifest.NewDataTypes);
        using var zip = new ZipArchive(new MemoryStream(package.Zip));
        Assert.Contains(Read(zip, "source-type-definitions/Legacy.xml"), Encoding.UTF8.GetString(a));
        Assert.Equal("program_import_dependency_missing", Assert.Throws<OperationException>(() => ProgramImportPackaging.Build(a, b, request with { NewDataTypes = [] })).Code);
        var legacy = ProgramImportPackaging.Build(a, b, request with { DependencyMode = LogixTypeDependencies.SelectedNewOnly });
        Assert.Null(legacy.Manifest.TypeClosure); Assert.False(legacy.Manifest.NativePreconfigurationAccepted);
        Assert.Single(XmlProjects.Parse(legacy.ProgramXml).Descendants("DataType"));
    }
    [Fact]
    public void Destination_type_presence_does_not_hide_a_missing_source_definition()
    {
        var doc = XmlProjects.Parse(Snapshot(true)); doc.Descendants("DataType").Single(t => (string?)t.Attribute("Name") == "Leaf").Remove();
        var a = Bytes(doc); var b = Snapshot(false);
        Assert.Equal("type_dependency_source_missing", Assert.Throws<OperationException>(() => LogixTypeDependencies.Build(a, b, Request(a, b))).Code);
    }
    [Theory]
    [InlineData("type_cycle", "type_dependency_cycle")]
    [InlineData("alias_cycle", "type_dependency_cycle")]
    [InlineData("layout_conflict", "type_dependency_destination_conflict")]
    [InlineData("unknown_type", "type_dependency_source_missing")]
    [InlineData("fbd", "type_dependency_unsupported_reference")]
    [InlineData("aoi", "type_dependency_unsupported_reference")]
    public void An_incomplete_or_unsupported_graph_is_refused_instead_of_being_called_complete(string change, string expected)
    {
        var doc = XmlProjects.Parse(Snapshot(true));
        if (change == "type_cycle") doc.Descendants("DataType").First().Descendants("Member").Single().SetAttributeValue("DataType", "Legacy");
        if (change == "alias_cycle") doc.Descendants("Tag").Single(t => (string?)t.Attribute("Name") == "GlobalAlias").SetAttributeValue("AliasFor", "Program:App.LocalAlias");
        if (change == "layout_conflict") doc.Descendants("DataType").First().Descendants("Member").Single().SetAttributeValue("Name", "Different");
        if (change == "unknown_type") doc.Descendants("Tag").First().SetAttributeValue("DataType", "UnknownSourceType");
        if (change == "fbd") doc.Descendants("Routine").Single().SetAttributeValue("Type", "FBD");
        if (change == "aoi")
        {
            doc.Root!.Element("Controller")!.Add(new XElement("AddOnInstructionDefinitions", new XElement("AddOnInstructionDefinition", new XAttribute("Name", "AoiType"))));
            doc.Descendants("Tag").First().SetAttributeValue("DataType", "AoiType");
        }
        var a = Bytes(doc); var b = Snapshot(false);
        Assert.Equal(expected, Assert.Throws<OperationException>(() => LogixTypeDependencies.Build(a, b, Request(a, b))).Code);
    }
    [Fact]
    public void Existing_only_closure_needs_no_new_type_import_and_builtin_bit_members_are_not_fake_definitions()
    {
        var a = Snapshot(false); var package = LogixTypeDependencies.Build(a, a, Request(a, a));
        Assert.Null(package.TypesXml); Assert.Equal(2, package.Closure.Definitions.Length);
        var doc = XmlProjects.Parse(a); doc.Descendants("DataType").First().Descendants("Member").Single().SetAttributeValue("DataType", "BIT");
        var b = Bytes(doc); var bit = LogixTypeDependencies.Build(b, b, Request(b, b));
        Assert.Contains("BIT", bit.Closure.BuiltInTypes); Assert.DoesNotContain(bit.Closure.Definitions, d => d.Name == "BIT");
    }
    [Fact]
    public void Direct_referenced_controller_type_closure_is_collected_without_tag_data_delivery()
    {
        var doc = XmlProjects.Parse(Snapshot(false)); doc.Descendants("Tag").Where(t => (string?)t.Attribute("Name") is "Existing" or "LocalAlias").Remove();
        doc.Descendants("Text").Single().Value = "XIC(GlobalBase.Value)NOP();";
        var a = Bytes(doc); var package = LogixTypeDependencies.Build(a, a, Request(a, a));
        Assert.Equal(["Leaf", "Legacy"], package.Closure.Definitions.Select(d => d.Name));
        Assert.Contains(package.Closure.TagReferences, r => r.Scope == "controller" && r.Name == "GlobalBase");
    }
}
