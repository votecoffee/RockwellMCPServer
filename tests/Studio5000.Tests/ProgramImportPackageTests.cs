using System.Text;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class ProgramImportPackageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-program-package-" + Guid.NewGuid().ToString("N"));
    private static string Tag(string name, string type, string value) => $"<Tag Name='{name}' TagType='Base' DataType='{type}' Dimensions='0'><Data Format='L5K'><![CDATA[{value}]]></Data></Tag>";
    private static string Type(string name, string member = "Value") => $"<DataType Name='{name}'><Members><Member Name='{member}' DataType='DINT'/></Members></DataType>";
    private static byte[] Snapshot(bool changed)
    {
        var routines = string.Concat(Enumerable.Range(0, 29).Select(i => $"<Routine Name='R{i:00}' Type='RLL'><RLLContent><Rung Number='0' Type='N'><Comment><![CDATA[λ {i}]]></Comment><Text><![CDATA[{(changed && i < 17 ? "MOV(NewGlobal.Value,Keep);" : "NOP();")}]]></Text></Rung></RLLContent></Routine>\r\n"));
        return Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="utf-8"?>
            <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="35.00" TargetType="Controller" ContainsContext="false">
            <Controller Name="Fixture" Use="Target"><Modules><Module Name="Hardware"/></Modules>
            <DataTypes>{Type("Existing")}{(changed ? Type("NewType") + Type("UnusedType") : "")}</DataTypes>
            <AddOnInstructionDefinitions><AddOnInstructionDefinition Name="KeepAoi"/></AddOnInstructionDefinitions>
            <Tags>{Tag("ExistingGlobal", "DINT", changed ? "999" : "73")}{(changed ? Tag("NewGlobal", "NewType", "[123]") + Tag("Unselected", "DINT", "888") : "")}</Tags>
            <Programs><Program Name="App" MainRoutineName="R00" TestEdits="false"><Tags>{Tag("Keep", "DINT", changed ? "999" : "41")}{Tag("State", "Existing", "[1]")}{(changed ? Tag("NewLocal", "BOOL", "0") : "")}</Tags><Routines>{routines}</Routines></Program>
            <Program Name="Other"><Tags>{Tag("OtherValue", "DINT", "19")}</Tags><Routines/></Program></Programs>
            <Tasks><Task Name="MainTask" Type="CONTINUOUS"><ScheduledPrograms><ScheduledProgram Name="App"/><ScheduledProgram Name="Other"/></ScheduledPrograms></Task></Tasks><WallClockTime LocalTimeAdjustment="0"/>
            </Controller></RSLogix5000Content>
            """);
    }
    private static ProgramImportPackageRequest Request(byte[] source, byte[] baseline) =>
        new(new("source", ProjectService.Hash(source)), new("baseline", ProjectService.Hash(baseline)), "App", "MainTask",
            Enumerable.Range(0, 17).Select(i => $"R{i:00}").ToArray(), ["NewGlobal"], ["NewType"]);
    private static (ProgramImportContent Package, byte[] Source, byte[] Baseline) Build()
    { var a = Snapshot(true); var b = Snapshot(false); return (ProgramImportPackaging.Build(a, b, Request(a, b)), a, b); }
    private static byte[] Bytes(XDocument doc) => Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
    private static XElement Program(XDocument doc) => doc.Descendants("Program").Single(p => (string?)p.Attribute("Name") == "App");
    private static byte[] NativeStyleReadback(byte[] source, byte[] baseline)
    {
        var a = XmlProjects.Parse(source); var b = XmlProjects.Parse(baseline); var incoming = new XElement(Program(a));
        foreach (var tag in Program(b).Elements("Tags").Elements("Tag"))
        {
            var target = incoming.Elements("Tags").Elements("Tag").Single(t => (string?)t.Attribute("Name") == (string?)tag.Attribute("Name"));
            target.Elements("Data").Remove(); target.Add(tag.Elements("Data").Select(d => new XElement(d)));
        }
        Program(b).ReplaceWith(incoming);
        b.Root!.Element("Controller")!.Element("Tags")!.Add(new XElement(a.Root!.Element("Controller")!.Elements("Tags").Elements("Tag").Single(t => (string?)t.Attribute("Name") == "NewGlobal")));
        b.Root.Element("Controller")!.Element("DataTypes")!.Add(new XElement(a.Descendants("DataType").Single(t => (string?)t.Attribute("Name") == "NewType")));
        return Bytes(b);
    }

    [Fact]
    public void Seventeen_changed_routines_still_include_all_twenty_nine_and_all_local_tags_exactly()
    {
        var (package, source, _) = Build(); var doc = XmlProjects.Parse(package.ProgramXml); var original = XmlProjects.Parse(source);
        Assert.Equal("Program", (string?)doc.Root!.Attribute("TargetType")); Assert.Equal("Target", (string?)Program(doc).Attribute("Use"));
        Assert.Equal(17, package.Manifest.ChangedRoutineCount); Assert.Equal(29, package.Manifest.IncludedRoutineCount);
        Assert.Equal(3, package.Manifest.IncludedLocalTagCount); Assert.Equal(12, package.Manifest.Definitions.Count(d => d.Kind == "routine" && d.Disposition == "retained"));
        foreach (var routine in Program(original).Descendants("Routine"))
            Assert.Equal(routine.ToString(SaveOptions.DisableFormatting), Program(doc).Descendants("Routine").Single(r => (string?)r.Attribute("Name") == (string?)routine.Attribute("Name")).ToString(SaveOptions.DisableFormatting));
        foreach (var definition in package.Manifest.Definitions.Where(d => d.Kind == "routine"))
        {
            Assert.Equal(definition.RawSha256, ProjectService.Hash(Encoding.UTF8.GetBytes(RawRoutine(source, definition.Name))));
            Assert.Contains(RawRoutine(source, definition.Name), Encoding.UTF8.GetString(package.ProgramXml));
        }
        Assert.True(package.Manifest.PackagingOnly); Assert.False(package.Manifest.NativeImportAccepted);
        Assert.Equal("overwrite_program", package.Manifest.ImportOperation); Assert.False(package.Manifest.RungInsertion);
        Assert.False(package.Manifest.Schema.XsdApplied);
    }
    private static string RawRoutine(byte[] bytes, string name)
    {
        var text = Encoding.UTF8.GetString(bytes); var start = text.IndexOf("<Routine Name='" + name + "'", StringComparison.Ordinal);
        return text[start..(text.IndexOf("</Routine>", start, StringComparison.Ordinal) + "</Routine>".Length)];
    }
    [Theory]
    [InlineData("routine")] [InlineData("local_tag")]
    public void Overwrite_omissions_are_refused_before_artifact_creation(string kind)
    {
        var source = XmlProjects.Parse(Snapshot(true));
        if (kind == "routine") Program(source).Descendants("Routine").Last().Remove(); else Program(source).Elements("Tags").Elements("Tag").First().Remove();
        var bytes = Bytes(source); var baseline = Snapshot(false);
        Assert.Equal("program_import_omission", Assert.Throws<OperationException>(() => ProgramImportPackaging.Build(bytes, baseline, Request(bytes, baseline))).Code);
    }
    [Fact]
    public void Source_closure_includes_legacy_type_context_but_only_selected_new_controller_tags()
    {
        var (package, _, _) = Build(); var doc = XmlProjects.Parse(package.ProgramXml); var c = doc.Root!.Element("Controller")!;
        Assert.Equal(["DataTypes", "Tags", "Programs"], c.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(["Existing", "NewType"], c.Elements("DataTypes").Elements("DataType").Select(t => (string?)t.Attribute("Name")));
        Assert.All(c.Elements("DataTypes").Elements("DataType"), t => Assert.Equal("Reference", (string?)t.Attribute("Use")));
        Assert.True(package.Manifest.TypeClosure!.CompleteForDeclaredSourceGraph); Assert.False(package.Manifest.NativePreconfigurationAccepted);
        Assert.Equal("NewGlobal", (string?)Assert.Single(c.Elements("Tags").Elements("Tag")).Attribute("Name"));
        Assert.DoesNotContain("Hardware", Encoding.UTF8.GetString(package.ProgramXml));
        Assert.DoesNotContain("UnusedType", Encoding.UTF8.GetString(package.ProgramXml));
        Assert.DoesNotContain("ExistingGlobal", Encoding.UTF8.GetString(package.ProgramXml));
    }
    [Fact]
    public void Referenced_new_controller_tags_require_explicit_selection_while_existing_values_are_not_bundled()
    {
        var a = Snapshot(true); var b = Snapshot(false);
        Assert.Equal("program_import_dependency_missing", Assert.Throws<OperationException>(() => ProgramImportPackaging.Build(a, b, Request(a, b) with { NewControllerTags = [] })).Code);
        Assert.Equal("program_import_dependency_existing", Assert.Throws<OperationException>(() => ProgramImportPackaging.Build(a, b, Request(a, b) with { NewControllerTags = ["ExistingGlobal"] })).Code);
        var source = XmlProjects.Parse(a);
        foreach (var text in Program(source).Descendants("Text")) text.Value = text.Value.Replace("NewGlobal.Value", "ExistingGlobal", StringComparison.Ordinal);
        var bytes = Bytes(source); var package = ProgramImportPackaging.Build(bytes, b, Request(bytes, b) with { NewControllerTags = [], NewDataTypes = [] });
        Assert.Empty(XmlProjects.Parse(package.ProgramXml).Root!.Element("Controller")!.Elements("Tags"));
    }
    [Fact]
    public void Values_use_offline_checkbox_contract_and_exact_saved_readback_without_claiming_online_observation()
    {
        var (package, source, baseline) = Build(); var m = package.Manifest;
        Assert.True(m.Values.PreserveExistingOfflineValuesCheckboxRequired); Assert.False(m.Values.NativePreservationObserved);
        Assert.Contains("Existing online", m.Values.ExistingOnlineValues); Assert.Contains("initial values", m.Values.NewTagValues);
        var readback = NativeStyleReadback(source, baseline);
        var report = ProgramImportPackaging.Compare(m, source, baseline, readback);
        Assert.True(report.Matches); Assert.True(report.SavedValuesMatch); Assert.True(report.OtherControllerPayloadsMatch); Assert.True(report.TaskScheduleMatches);
        var changed = XmlProjects.Parse(readback); Program(changed).Elements("Tags").Elements("Tag").First().Element("Data")!.Value = "999";
        Assert.False(ProgramImportPackaging.Compare(m, source, baseline, Bytes(changed)).SavedValuesMatch);
        var online = ProgramImportPackaging.Compare(m, source, baseline, Bytes(changed), "online_declarations");
        Assert.True(online.Matches); Assert.Null(online.SavedValuesMatch); Assert.False(online.NativeImportAccepted);
        var newChanged = XmlProjects.Parse(readback); Program(newChanged).Elements("Tags").Elements("Tag").Last().Element("Data")!.Value = "1";
        Assert.False(ProgramImportPackaging.Compare(m, source, baseline, Bytes(newChanged)).SavedValuesMatch);
    }
    [Theory]
    [InlineData("rung_append", "routine_set_or_exact_content")]
    [InlineData("routine_missing", "routine_set_or_exact_content")]
    [InlineData("tag_missing", "local_tag_set_or_declarations")]
    [InlineData("schedule", "task_schedule")]
    [InlineData("hardware", "unrelated_controller_payload_or_existing_values")]
    public void Readback_detects_appending_instead_of_replacement_and_other_preservation_failures(string change, string expected)
    {
        var (package, source, baseline) = Build(); var readback = XmlProjects.Parse(NativeStyleReadback(source, baseline));
        if (change == "rung_append") Program(readback).Descendants("RLLContent").First().Add(new XElement("Rung", new XAttribute("Number", "1"), new XElement("Text", "NOP();")));
        if (change == "routine_missing") Program(readback).Descendants("Routine").Last().Remove();
        if (change == "tag_missing") Program(readback).Elements("Tags").Elements("Tag").Last().Remove();
        if (change == "schedule") readback.Descendants("ScheduledProgram").First().Remove();
        if (change == "hardware") readback.Descendants("Module").Single().SetAttributeValue("Name", "UnexpectedHardware");
        var report = ProgramImportPackaging.Compare(package.Manifest, source, baseline, Bytes(readback));
        Assert.False(report.Matches); Assert.Contains(expected, report.Differences);
    }
    [Theory]
    [InlineData("PendingEdits")] [InlineData("TestEdits")] [InlineData("Unknown")]
    public void Pending_testing_and_unknown_content_are_not_silently_selected_or_finalized(string variant)
    {
        var source = XmlProjects.Parse(Snapshot(true)); source.Descendants("RLLContent").First().SetAttributeValue("OnlineEditType", variant);
        var a = Bytes(source); var b = Snapshot(false);
        Assert.Equal("program_import_routine_state", Assert.Throws<OperationException>(() => ProgramImportPackaging.Build(a, b, Request(a, b))).Code);
    }
    [Fact]
    public void Normal_or_finalized_ST_and_original_content_are_preserved_without_rung_import_instructions()
    {
        var a = XmlProjects.Parse(Snapshot(true)); var b = XmlProjects.Parse(Snapshot(false));
        foreach (var doc in new[] { a, b })
        {
            var r = Program(doc).Descendants("Routine").Last(); r.SetAttributeValue("Type", "ST");
            r.Element("RLLContent")!.ReplaceWith(new XElement("STContent", new XElement("Line", new XAttribute("Number", "0"), new XElement("Text", new XCData("Keep := Keep;")))));
            r.Element("STContent")!.SetAttributeValue("OnlineEditType", "Original");
        }
        var source = Bytes(a); var baseline = Bytes(b); var package = ProgramImportPackaging.Build(source, baseline, Request(source, baseline));
        Assert.Single(XmlProjects.Parse(package.ProgramXml).Descendants("STContent"));
        Assert.Contains(package.Manifest.NativeSteps, s => s.Contains("Finalize All Edits In Program", StringComparison.Ordinal));
        Assert.False(package.Manifest.NativeImportAccepted);
    }
    [Fact]
    public void Existing_type_overwrite_is_separate_from_new_type_addition_and_missing_closure_is_reported()
    {
        var a = Snapshot(true); var b = Snapshot(false);
        var existing = Request(a, b) with { NewDataTypes = ["Existing"] };
        Assert.Equal("program_import_dependency_existing", Assert.Throws<OperationException>(() => ProgramImportPackaging.Build(a, b, existing)).Code);
        Assert.Equal("program_import_dependency_missing", Assert.Throws<OperationException>(() => ProgramImportPackaging.Build(a, b, Request(a, b) with { NewDataTypes = [] })).Code);
        var changed = XmlProjects.Parse(a); changed.Descendants("DataType").Single(t => (string?)t.Attribute("Name") == "Existing").Descendants("Member").First().SetAttributeValue("Name", "Different");
        var bytes = Bytes(changed);
        Assert.Equal("type_dependency_destination_conflict", Assert.Throws<OperationException>(() => ProgramImportPackaging.Build(bytes, b, Request(bytes, b))).Code);
        Assert.Contains(Build().Package.Manifest.NativeSteps, s => s.Contains("new UDT addition does not inherently require download", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Routine", "R00", false)] [InlineData("Program", "Other", false)] [InlineData("Program", "App", true)]
    public void Component_source_must_identify_the_exact_whole_program(string targetType, string targetName, bool allowed)
    {
        var doc = XmlProjects.Parse(Snapshot(true)); doc.Root!.SetAttributeValue("TargetType", targetType); doc.Root.SetAttributeValue("TargetName", targetName);
        var a = Bytes(doc); var b = Snapshot(false);
        if (allowed) Assert.Equal(29, ProgramImportPackaging.Build(a, b, Request(a, b)).Manifest.IncludedRoutineCount);
        else Assert.Equal("program_import_context", Assert.Throws<OperationException>(() => ProgramImportPackaging.Build(a, b, Request(a, b))).Code);
    }
    [Theory]
    [InlineData("local", "force")] [InlineData("controller", "force")] [InlineData("controller", "safety")]
    public void New_dependencies_do_not_introduce_force_or_safety_metadata(string scope, string kind)
    {
        var doc = XmlProjects.Parse(Snapshot(true));
        var tag = scope == "local" ? Program(doc).Elements("Tags").Elements("Tag").Last() : doc.Root!.Element("Controller")!.Elements("Tags").Elements("Tag").Single(t => (string?)t.Attribute("Name") == "NewGlobal");
        tag.SetAttributeValue(kind == "force" ? "ForceValue" : "Class", kind == "force" ? "1" : "Safety");
        var a = Bytes(doc); var b = Snapshot(false);
        Assert.Equal("program_import_dependency", Assert.Throws<OperationException>(() => ProgramImportPackaging.Build(a, b, Request(a, b))).Code);
    }

    // Generated strict full-controller schema, not an installed vendor V35 XSD.
    // Required Modules/Tasks/clock prove scope separation without weakening that schema.
    private static byte[] Schema() => Encoding.UTF8.GetBytes("""
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xs:complexType name="TagType"><xs:sequence><xs:element name="Data" type="xs:anyType" minOccurs="0" maxOccurs="unbounded"/></xs:sequence><xs:attribute name="Name" type="xs:string" use="required"/><xs:attribute name="TagType" type="xs:string"/><xs:attribute name="DataType" type="xs:string"/><xs:attribute name="Dimensions" type="xs:string"/></xs:complexType>
          <xs:complexType name="TagsType"><xs:sequence><xs:element name="Tag" type="TagType" minOccurs="0" maxOccurs="unbounded"/></xs:sequence></xs:complexType>
          <xs:complexType name="TypesType"><xs:sequence><xs:element name="DataType" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence><xs:element name="Members" type="xs:anyType"/></xs:sequence><xs:attribute name="Name" type="xs:string" use="required"/><xs:attribute name="Use" type="xs:string"/></xs:complexType></xs:element></xs:sequence></xs:complexType>
          <xs:complexType name="ProgramsType"><xs:sequence><xs:element name="Program" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence><xs:element name="Tags" type="TagsType"/><xs:element name="Routines" type="xs:anyType"/></xs:sequence><xs:attribute name="Name" type="xs:string" use="required"/><xs:attribute name="Use" type="xs:string"/><xs:attribute name="MainRoutineName" type="xs:string"/><xs:attribute name="TestEdits" type="xs:boolean"/></xs:complexType></xs:element></xs:sequence></xs:complexType>
          <xs:element name="RSLogix5000Content"><xs:complexType><xs:sequence><xs:element name="Controller"><xs:complexType><xs:sequence>
            <xs:element name="Modules" type="xs:anyType"/><xs:element name="DataTypes" type="TypesType"/><xs:element name="AddOnInstructionDefinitions" type="xs:anyType"/>
            <xs:element name="Tags" type="TagsType"/><xs:element name="Programs" type="ProgramsType"/><xs:element name="Tasks" type="xs:anyType"/><xs:element name="WallClockTime" type="xs:anyType"/>
          </xs:sequence><xs:attribute name="Name" type="xs:string"/><xs:attribute name="Use" type="xs:string"/></xs:complexType></xs:element></xs:sequence><xs:anyAttribute processContents="skip"/></xs:complexType></xs:element>
        </xs:schema>
        """);
    [Fact]
    public void Partial_definition_scope_uses_unchanged_compiled_schema_and_does_not_claim_full_wrapper_acceptance()
    {
        var (package, source, _) = Build(); var schema = Schema(); var hash = ProjectService.Hash(schema);
        Assert.True(LogixImportSchema.Validate(XmlProjects.Parse(source), schema, "full_document").Valid);
        var full = LogixImportSchema.Validate(XmlProjects.Parse(package.ProgramXml), schema, "full_document"); Assert.False(full.Valid);
        var partialDocument = XmlProjects.Parse(package.ProgramXml); var before = partialDocument.ToString(SaveOptions.DisableFormatting);
        var partial = LogixImportSchema.Validate(partialDocument, schema, "partial_definitions");
        Assert.True(partial.Valid); Assert.False(partial.FullDocumentValidated); Assert.False(partial.NativeImportAccepted);
        Assert.Equal(before, partialDocument.ToString(SaveOptions.DisableFormatting));
        Assert.Equal(["Program:App", "Tag:NewGlobal", "DataType:Existing", "DataType:NewType"], partial.Definitions);
        Assert.Equal(hash, ProjectService.Hash(schema));
        var bad = XmlProjects.Parse(package.ProgramXml); Program(bad).SetAttributeValue("InventedAttribute", "invalid");
        Assert.False(LogixImportSchema.Validate(bad, schema, "partial_definitions").Valid);
        Assert.Equal(hash, ProjectService.Hash(schema));
    }
    [Fact]
    public void Schema_external_loading_and_unreviewed_scope_are_refused()
    {
        var (package, _, _) = Build(); var document = XmlProjects.Parse(package.ProgramXml);
        var external = Encoding.UTF8.GetBytes("<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'><xs:include schemaLocation='https://example.invalid/schema.xsd'/></xs:schema>");
        Assert.Equal("program_import_schema_external", Assert.Throws<OperationException>(() => LogixImportSchema.Validate(document, external, "partial_definitions")).Code);
        Assert.Equal("program_import_schema_scope", Assert.Throws<OperationException>(() => LogixImportSchema.Validate(document, Schema(), "relaxed_wrapper")).Code);
        var a = Snapshot(true); var b = Snapshot(false); var schema = Schema();
        var request = Request(a, b) with { Schema = new("fixture.xsd", ProjectService.Hash(schema), "full_document") };
        Assert.Equal("program_import_schema", Assert.Throws<OperationException>(() => ProgramImportPackaging.Build(a, b, request, schema)).Code);
    }
    [Fact]
    public async Task Shared_service_stores_immutable_artifacts_and_reuses_exact_manifest_for_saved_readback()
    {
        Directory.CreateDirectory(root); using var service = new ProjectService(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], EnableControllerOperations = false });
        async Task<ProjectInfo> Register(string name, byte[] bytes) { var path = Path.Combine(root, name + ".l5x"); await File.WriteAllBytesAsync(path, bytes); return await service.RegisterAsync(path); }
        var a = Snapshot(true); var b = Snapshot(false); var p = await Register("source", a); var q = await Register("baseline", b);
        var schema = Schema(); var schemaPath = Path.Combine(root, "fixture.xsd"); await File.WriteAllBytesAsync(schemaPath, schema);
        var request = Request(a, b) with { Source = new(p.Id, p.CurrentSha256), Baseline = new(q.Id, q.CurrentSha256), Schema = new(schemaPath, ProjectService.Hash(schema)) };
        var package = await service.PrepareProgramImportPackageAsync(request);
        var dependencies = await service.PrepareTypeDependencyPackageAsync(new(request.Source, request.Baseline, "App", ["NewGlobal"]));
        Assert.NotNull(dependencies.Types); Assert.True(dependencies.Closure.CompleteForDeclaredSourceGraph);
        Assert.False(dependencies.Closure.NativePreconfigurationAccepted);
        Assert.Equal(dependencies.Types.Sha256, ProjectService.Hash(File.ReadAllBytes(service.Download(dependencies.Types.Id).Path)));
        Assert.Empty(service.SdkJobList());
        Assert.True(package.Plan.Schema.Valid); Assert.True(package.Plan.Schema.XsdApplied); Assert.False(package.Plan.Schema.FullDocumentValidated);
        var readback = await Register("readback", NativeStyleReadback(a, b));
        Assert.True(service.CheckProgramImportReadback(new(package.Manifest.Id, package.Manifest.Sha256, new(readback.Id, readback.CurrentSha256))).Matches);
        Assert.Equal(ProjectService.Hash(File.ReadAllBytes(service.Download(package.Program.Id).Path)), package.Program.Sha256);
        Assert.Equal(p, service.Get(p.Id)); Assert.Equal(q, service.Get(q.Id));
        Assert.Equal(a, File.ReadAllBytes(p.SourcePath)); Assert.Equal(b, File.ReadAllBytes(q.SourcePath));
        var failure = await Assert.ThrowsAsync<OperationException>(() => service.PrepareProgramImportPackageAsync(request with { Schema = new(schemaPath, new string('0', 64)) }));
        Assert.Equal("program_import_schema", failure.Code);
    }
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
