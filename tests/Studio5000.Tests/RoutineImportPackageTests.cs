using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class RoutineImportPackageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-routine-package-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    private const string ExistingSecret = "9007199254740993";
    private const string NewLiteral = "9223372036854775807";
    private static string Tag(string name, string type, string value, string dimensions = "0") =>
        $"<Tag Name='{name}' TagType='Base' DataType='{type}' Dimensions='{dimensions}' Radix='Decimal' ExternalAccess='Read/Write'><Data Format='L5K'><![CDATA[{value}]]></Data></Tag>";
    private static string Source(bool edited) => $"""
        <RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="35.00" TargetType="Controller" TargetName="Fixture" ContainsContext="false">
          <Controller Name="Fixture" Use="Target" ProcessorType="fixture">
            <DataTypes><DataType Name="Unused"><Members><Member Name="Value" DataType="DINT"/></Members></DataType></DataTypes>
            <Tags>{Tag("ExistingGlobal", "LINT", ExistingSecret)}{(edited ? Tag("NewGlobal", "BOOL", "0") : "")}</Tags>
            <Programs><Program Name="App" MainRoutineName="Main" Use="Target"><Tags>{Tag("Keep", "DINT", edited ? "987654321" : "123456789")}
              {(edited ? Tag("NewFlag", "BOOL", "0") + Tag("NewResult", "LINT", NewLiteral) + Tag("NewArray", "DINT", "[[1,2,3],[4,5,6]]", "2,3") : "")}</Tags>
              <Routines><Routine Name="Main" Type="RLL"><Description><![CDATA[Δ routine]]></Description><RLLContent>
                <Rung Number="0" Type="N"><Comment><![CDATA[keep comment]]></Comment><Text><![CDATA[{(edited ? "XIC(NewFlag)ADD(Keep,1,NewResult);" : "NOP();")}]]></Text></Rung>
                <Rung Number="1" Type="N"><Text><![CDATA[NOP();]]></Text></Rung>
              </RLLContent></Routine><Routine Name="Other" Type="RLL"><RLLContent><Rung Number="0"><Text>NOP();</Text></Rung></RLLContent></Routine></Routines>
            </Program><Program Name="OtherProgram"><Tags>{Tag("NotSelected", "DINT", "7777777")}</Tags><Routines/></Program></Programs>
          </Controller>
        </RSLogix5000Content>
        """;
    public RoutineImportPackageTests()
    {
        Directory.CreateDirectory(root);
        service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], EnableControllerOperations = false,
            ControllerTargets = [new("fixture", @"AB_ETH-1\192.0.2.1\Backplane\0")] });
    }
    private async Task<ProjectInfo> Register(string name, string text)
    { var path = Path.Combine(root, name + ".l5x"); await File.WriteAllTextAsync(path, text, new UTF8Encoding(false)); return await service.RegisterAsync(path); }
    private async Task<RoutineImportPackageRequest> Request(string? sourceText = null, string? baselineText = null)
    {
        var source = await Register("edited-" + Guid.NewGuid().ToString("N"), sourceText ?? Source(true));
        var baseline = await Register("baseline-" + Guid.NewGuid().ToString("N"), baselineText ?? Source(false));
        return new(new(source.Id, source.CurrentSha256), new(baseline.Id, baseline.CurrentSha256), "App", "Main", [new("NewFlag"), new("NewResult")]);
    }

    [Fact]
    public async Task Package_excludes_all_tag_definitions_and_existing_values_and_preserves_the_full_routine()
    {
        var request = await Request(); var source = service.Get(request.Source.ProjectId); var baseline = service.Get(request.Baseline.ProjectId);
        var sourceBytes = File.ReadAllBytes(source.SourcePath); var baselineBytes = File.ReadAllBytes(baseline.SourcePath);
        var result = await service.PrepareRoutineImportPackageAsync(request);
        var xmlBytes = File.ReadAllBytes(service.Download(result.Routine.Id).Path); var xml = Encoding.UTF8.GetString(xmlBytes);
        var doc = XmlProjects.Parse(xmlBytes); var routine = Assert.Single(doc.Descendants("Routine"));
        Assert.Equal("Routine", (string?)doc.Root!.Attribute("TargetType")); Assert.Equal("Main", (string?)doc.Root.Attribute("TargetName"));
        Assert.Equal("Target", (string?)routine.Attribute("Use")); Assert.Equal(2, routine.Descendants("Rung").Count());
        Assert.Contains("XIC(NewFlag)ADD(Keep,1,NewResult);", xml); Assert.Contains("Δ routine", xml); Assert.Contains("keep comment", xml);
        Assert.Empty(doc.Descendants("Tag")); Assert.Empty(doc.Descendants("Tags")); Assert.Empty(doc.Descendants("Data")); Assert.Empty(doc.Descendants("DataTypes"));
        Assert.DoesNotContain(ExistingSecret, xml); Assert.DoesNotContain("987654321", xml); Assert.DoesNotContain(NewLiteral, xml);
        Assert.DoesNotContain("OtherProgram", xml); Assert.DoesNotContain("Name=\"Other\"", xml);
        Assert.Equal(0, result.Validation.TagDefinitions); Assert.Equal(0, result.Validation.SavedValueElements); Assert.True(result.Validation.WholeRoutineTarget);
        Assert.False(result.Validation.NativeImportAccepted); Assert.False(result.RequiredTags.NativeImportAccepted);
        Assert.Equal("Controller/Programs/Program[@Name='App']", result.ProgramXPath); Assert.Equal("Main", result.TargetName);
        Assert.Equal("finalize_during_import", result.RecommendedWorkflow); Assert.Equal("FinalizeEdits", result.ImportOption);
        Assert.Equal(["NewFlag", "NewResult"], result.RequiredTags.Tags.Select(t => t.Name));
        Assert.All(result.RequiredTags.Tags, tag => { Assert.Equal("program", tag.Scope); Assert.Equal("App", tag.ProgramName); Assert.Empty(tag.Dimensions); Assert.Equal("0", tag.SourceDimensions); });
        Assert.Equal("BOOL", result.RequiredTags.Tags[0].DataType); Assert.Equal("LINT", result.RequiredTags.Tags[1].DataType);
        var manifest = File.ReadAllText(service.Download(result.Prerequisites.Id).Path);
        Assert.Contains(NewLiteral, manifest); Assert.DoesNotContain(ExistingSecret, manifest); Assert.DoesNotContain("987654321", manifest); Assert.DoesNotContain("123456789", manifest);
        Assert.Contains("create_missing_only_never_write_existing", manifest);
        Assert.Equal(sourceBytes, File.ReadAllBytes(source.SourcePath)); Assert.Equal(baselineBytes, File.ReadAllBytes(baseline.SourcePath));
        Assert.Equal(source.CurrentSha256, service.Get(source.Id).CurrentSha256); Assert.Equal(baseline.CurrentSha256, service.Get(baseline.Id).CurrentSha256);
        Assert.Empty(service.SdkJobList());
        using var archive = ZipFile.OpenRead(service.Download(result.Package.Id).Path);
        Assert.Equal(["routine.l5x", "prerequisites.json", "README.txt"], archive.Entries.Select(e => e.FullName));
        using var reader = new StreamReader(archive.GetEntry("routine.l5x")!.Open()); Assert.Equal(xml, reader.ReadToEnd());
        Assert.Equal(result.Package.Sha256, (await service.PrepareRoutineImportPackageAsync(request)).Package.Sha256);
    }

    [Fact]
    public async Task Prerequisites_preserve_controller_scope_array_dimensions_and_all_exact_value_representations()
    {
        var text = Source(true).Replace(Tag("NewResult", "LINT", NewLiteral), Tag("NewResult", "LINT", NewLiteral).Replace("</Tag>", $"<Data Format='Decorated'><DataValue DataType='LINT' Radix='Decimal' Value='{NewLiteral}'/></Data></Tag>"));
        var request = await Request(text); request = request with { PrerequisiteTags = [new("NewGlobal", "controller"), new("NewArray"), new("NewResult")] };
        var result = await service.PrepareRoutineImportPackageAsync(request);
        var global = result.RequiredTags.Tags[0]; Assert.Equal("controller", global.Scope); Assert.Null(global.ProgramName);
        var array = result.RequiredTags.Tags[1]; Assert.Equal([2, 3], array.Dimensions); Assert.Equal("2,3", array.SourceDimensions); Assert.Contains("[[1,2,3],[4,5,6]]", Assert.Single(array.InitialData).Xml);
        var integer = result.RequiredTags.Tags[2]; Assert.Equal(["L5K", "Decorated"], integer.InitialData.Select(v => v.Format)); Assert.All(integer.InitialData, value => Assert.Contains(NewLiteral, value.Xml));
    }

    [Fact]
    public async Task Existing_tags_are_rejected_as_prerequisites_including_case_insensitive_matches()
    {
        var request = await Request(baselineText: Source(false).Replace("</Tags>\n", Tag("newflag", "BOOL", "1") + "</Tags>\n"));
        var error = await Assert.ThrowsAsync<OperationException>(() => service.PrepareRoutineImportPackageAsync(request));
        Assert.Equal("routine_import_existing_tag", error.Code); Assert.Empty(service.SdkJobList());
    }

    [Theory]
    [InlineData("scope")][InlineData("duplicate")][InlineData("missing")][InlineData("mode")][InlineData("stale")]
    public async Task Invalid_selections_modes_and_stale_snapshots_do_not_publish_artifacts(string kind)
    {
        var request = await Request();
        request = kind switch
        {
            "scope" => request with { PrerequisiteTags = [new("NewFlag", "other")] },
            "duplicate" => request with { PrerequisiteTags = [new("NewFlag"), new("newflag")] },
            "missing" => request with { PrerequisiteTags = [new("NotPresent")] },
            "mode" => request with { Mode = "include_all_tags" },
            _ => request with { Source = request.Source with { Sha256 = "stale" } }
        };
        await Assert.ThrowsAsync<OperationException>(() => service.PrepareRoutineImportPackageAsync(request));
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "state", "artifacts"))); Assert.Empty(service.SdkJobList());
    }

    [Theory]
    [InlineData("alias")][InlineData("no-data")][InlineData("dimensions")][InlineData("forced")][InlineData("opaque")][InlineData("safety")][InlineData("baseline-safety")][InlineData("foreign-controller")]
    public async Task Unsupported_or_ambiguous_inputs_are_not_silently_rewritten(string kind)
    {
        var text = Source(true);
        text = kind switch
        {
            "alias" => text.Replace("Name='NewFlag' TagType='Base'", "Name='NewFlag' TagType='Alias'"),
            "no-data" => text.Replace(Tag("NewFlag", "BOOL", "0"), "<Tag Name='NewFlag' TagType='Base' DataType='BOOL'/>"),
            "dimensions" => text.Replace(Tag("NewFlag", "BOOL", "0"), Tag("NewFlag", "BOOL", "0", "2,0")),
            "forced" => text.Replace(Tag("NewFlag", "BOOL", "0"), Tag("NewFlag", "BOOL", "0").Replace("</Tag>", "<ForceData>1</ForceData></Tag>")),
            "opaque" => text.Replace("<RLLContent>", "<EncodedData/><RLLContent>"),
            "safety" => text.Replace("Program Name=\"App\"", "Program Class=\"Safety\" Name=\"App\""),
            "baseline-safety" => text,
            _ => text.Replace("Controller Name=\"Fixture\"", "Controller Name=\"Other\"")
        };
        var request = await Request(text, kind == "baseline-safety" ? Source(false).Replace("Program Name=\"App\"", "Program Class=\"Safety\" Name=\"App\"") : null);
        await Assert.ThrowsAsync<OperationException>(() => service.PrepareRoutineImportPackageAsync(request));
        Assert.Empty(Directory.GetDirectories(Path.Combine(root, "state", "artifacts"))); Assert.Empty(service.SdkJobList());
    }

    [Theory]
    [InlineData("<Tags><Tag Name='Hidden'/></Tags>")]
    [InlineData("<x:Tag xmlns:x='urn:hidden' Name='Hidden'/>")]
    [InlineData("<Data Format='L5K'>123</Data>")]
    [InlineData("<DefaultData>123</DefaultData>")]
    [InlineData("<ForceData>123</ForceData>")]
    [InlineData("<DataValue Value='123'/>")]
    [InlineData("<Array DataType='DINT'><Element Index='[0]' Value='123'/></Array>")]
    [InlineData("<Structure DataType='Fixture'><DataValueMember Name='Value' Value='123'/></Structure>")]
    public async Task Tag_free_validator_rejects_definitions_and_saved_values_even_when_nested_or_namespaced(string payload)
    {
        var result = await service.PrepareRoutineImportPackageAsync(await Request());
        var doc = XmlProjects.Parse(File.ReadAllBytes(service.Download(result.Routine.Id).Path));
        doc.Descendants("Routine").Single().Add(XElement.Parse(payload));
        Assert.Equal("routine_import_payload", Assert.Throws<OperationException>(() => RoutineImportPackaging.ValidateTagFree(doc, "App", "Main")).Code);
    }

    [Fact]
    public async Task Tag_free_online_plan_checks_payload_without_connecting_and_uses_full_routine_Finalize_target()
    {
        var result = await service.PrepareRoutineImportPackageAsync(await Request());
        var acd = Path.Combine(root, "matching.acd"); File.WriteAllText(acd, "opaque ACD fixture; never opened natively");
        var request = new OnlineEditPlanRequest("fixture", "App", "Main", new(null, result.Routine.Sha256, result.Routine.Id),
            ProjectService.Hash(File.ReadAllBytes(acd)), SourcePath: acd, Workflow: "finalize_during_import", ImportKind: "routine", RoutinePackagingMode: "tag_definition_free");
        var plan = await service.PrepareOnlineEditAsync(request);
        Assert.NotNull(plan.RoutinePackageValidation); Assert.False(plan.RoutinePackageValidation.NativeImportAccepted);
        Assert.Equal("FinalizeEdits", plan.SdkImportOption); Assert.False(plan.TestEditsObserved); Assert.False(plan.FinalizationObserved);
        var call = plan.SdkRequest.Calls.Single(c => c.Method == "PartialImportWithTargetFromXmlFileAsync");
        Assert.Equal("Controller/Programs/Program[@Name='App']", call.Arguments["xPath"].GetString()); Assert.Equal("Main", call.Arguments["targetName"].GetString());
        Assert.Contains(plan.StudioSteps, step => step.Contains("separately created missing tag", StringComparison.Ordinal));
        var original = File.ReadAllBytes(service.Download(result.Routine.Id).Path); var injected = Path.Combine(root, "bad.l5x");
        var doc = XmlProjects.Parse(original); doc.Root!.Element("Controller")!.Add(new XElement("Tags", new XElement("Tag", new XAttribute("Name", "Wrong")))); doc.Save(injected);
        var error = await Assert.ThrowsAsync<OperationException>(() => service.PrepareOnlineEditAsync(request with { ImportFile = new(injected, ProjectService.Hash(File.ReadAllBytes(injected))) }));
        Assert.Equal("routine_import_payload", error.Code);
        Assert.Equal("routine_import_mode", (await Assert.ThrowsAsync<OperationException>(() => service.PrepareOnlineEditAsync(request with { ImportKind = "rungs" }))).Code);
        Assert.Empty(service.SdkJobList());
    }

    [Fact]
    public async Task Readable_ST_and_empty_prerequisite_lists_are_supported_without_importing_context_tags()
    {
        var doc = XDocument.Parse(Source(true)); var routine = doc.Descendants("Routine").First(); routine.SetAttributeValue("Type", "ST");
        routine.Element("RLLContent")!.ReplaceWith(new XElement("STContent", new XElement("Line", new XAttribute("Number", "0"), new XCData("NewResult := Keep + 1;"))));
        var request = await Request(doc.ToString()); var result = await service.PrepareRoutineImportPackageAsync(request with { PrerequisiteTags = [] });
        Assert.Empty(result.RequiredTags.Tags); Assert.Contains("NewResult := Keep + 1;", File.ReadAllText(service.Download(result.Routine.Id).Path));
        Assert.Empty(XmlProjects.Parse(File.ReadAllBytes(service.Download(result.Routine.Id).Path)).Descendants("Tag"));
    }

    [Fact]
    public async Task Program_only_bundles_selected_missing_program_helpers_and_keeps_controller_creation_separate()
    {
        var request = await Request(); request = request with { Mode = "program_only", PrerequisiteTags = [new("NewFlag"), new("NewResult"), new("NewArray"), new("NewGlobal", "controller")] };
        var result = await service.PrepareRoutineImportPackageAsync(request);
        var bytes = File.ReadAllBytes(service.Download(result.Routine.Id).Path); var doc = XmlProjects.Parse(bytes); var xml = Encoding.UTF8.GetString(bytes);
        Assert.Null(doc.Root!.Element("Controller")!.Element("Tags"));
        var program = Assert.Single(doc.Descendants("Program"));
        Assert.Equal(["NewFlag", "NewResult", "NewArray"], program.Element("Tags")!.Elements("Tag").Select(t => (string)t.Attribute("Name")!));
        Assert.All(program.Element("Tags")!.Elements("Tag"), tag => Assert.Null(tag.Attribute("Use")));
        Assert.Contains(NewLiteral, xml); Assert.DoesNotContain(ExistingSecret, xml); Assert.DoesNotContain("987654321", xml); Assert.DoesNotContain("NewGlobal", xml);
        Assert.Equal(3, result.Validation.ProgramTagDefinitions); Assert.Equal(0, result.Validation.ControllerTagDefinitions); Assert.Equal(0, result.Validation.ExistingTagValueElements);
        Assert.Equal("program_only", result.Validation.Mode); Assert.False(result.Validation.NativeImportAccepted);
        Assert.Equal(3, result.RequiredTags.BundledProgramDependencies.Length);
        var controller = Assert.Single(result.RequiredTags.ControllerCreateBeforeImport); Assert.Equal("NewGlobal", controller.Name); Assert.Null(controller.ProgramName);
        Assert.Equal(controller, Assert.Single(result.RequiredTags.CreateBeforeImport));
    }

    [Theory]
    [InlineData("existing-program", "routine_import_existing_tag")]
    [InlineData("controller", "routine_import_payload")]
    [InlineData("nested-tag", "routine_import_payload")]
    [InlineData("routine-data", "routine_import_payload")]
    public async Task Program_only_validator_rejects_existing_values_controller_definitions_and_scope_escape(string kind, string code)
    {
        var request = (await Request()) with { Mode = "program_only" }; var result = await service.PrepareRoutineImportPackageAsync(request);
        var doc = XmlProjects.Parse(File.ReadAllBytes(service.Download(result.Routine.Id).Path));
        var program = doc.Descendants("Program").Single(); var controller = doc.Root!.Element("Controller")!;
        if (kind == "existing-program") program.Element("Tags")!.Add(XElement.Parse(Tag("Keep", "DINT", "123456789")));
        if (kind == "controller") controller.Add(new XElement("Tags", XElement.Parse(Tag("Sneak", "REAL", "0.1"))));
        if (kind == "nested-tag") program.Element("Tags")!.Elements("Tag").First().Element("Data")!.Add(XElement.Parse(Tag("Sneak", "BOOL", "0")));
        if (kind == "routine-data") doc.Descendants("Routine").Single().Element("RLLContent")!.Add(new XElement("Data", "123"));
        Assert.Equal(code, Assert.Throws<OperationException>(() => RoutineImportPackaging.ValidateProgramOnly(doc, "App", "Main", XDocument.Parse(Source(false)))).Code);
    }

    [Fact]
    public async Task Program_only_planning_requires_a_pinned_baseline_and_keeps_native_acceptance_false()
    {
        var packageRequest = (await Request()) with { Mode = "program_only" }; var result = await service.PrepareRoutineImportPackageAsync(packageRequest);
        var acd = Path.Combine(root, "source.acd"); File.WriteAllText(acd, "opaque saved ACD fixture");
        var request = new OnlineEditPlanRequest("fixture", "App", "Main", new(null, result.Routine.Sha256, result.Routine.Id),
            ProjectService.Hash(File.ReadAllBytes(acd)), SourcePath: acd, Workflow: "finalize_during_import", ImportKind: "routine", RoutinePackagingMode: "program_only");
        Assert.Equal("routine_import_baseline", (await Assert.ThrowsAsync<OperationException>(() => service.PrepareOnlineEditAsync(request))).Code);
        var plan = await service.PrepareOnlineEditAsync(request with { RoutineTagBaseline = packageRequest.Baseline });
        Assert.Equal(2, plan.RoutinePackageValidation!.ProgramTagDefinitions); Assert.False(plan.RoutinePackageValidation.NativeImportAccepted);
        Assert.Contains(plan.StudioSteps, step => step.Contains("not a verified remedy", StringComparison.Ordinal)); Assert.Empty(service.SdkJobList());
        var baseline = XDocument.Parse(Source(false)); baseline.Descendants("Program").First().Element("Tags")!.Add(XElement.Parse(Tag("NewFlag", "BOOL", "1")));
        var nowExists = await Register("now-exists", baseline.ToString());
        Assert.Equal("routine_import_existing_tag", (await Assert.ThrowsAsync<OperationException>(() => service.PrepareOnlineEditAsync(request with { RoutineTagBaseline = new(nowExists.Id, nowExists.CurrentSha256) }))).Code);
    }

    [Fact]
    public async Task Three_generated_REAL_dependencies_are_package_checks_and_the_tag_free_fallback_still_excludes_every_definition()
    {
        // Generated syntax fixture only: it does not reproduce or establish acceptance of a user's crashed native file.
        var doc = XDocument.Parse(Source(true)); var tags = doc.Descendants("Program").First().Element("Tags")!;
        tags.Add(XElement.Parse(Tag("HelperP", "REAL", "1.25")), XElement.Parse(Tag("HelperI", "REAL", "-2.50e-3")), XElement.Parse(Tag("HelperD", "REAL", "3.0")));
        var request = await Request(doc.ToString()); request = request with { PrerequisiteTags = [new("HelperP"), new("HelperI"), new("HelperD"), new("NewGlobal", "controller")] };
        var programOnly = await service.PrepareRoutineImportPackageAsync(request with { Mode = "program_only" });
        Assert.Equal(3, programOnly.RequiredTags.BundledProgramDependencies.Length); Assert.All(programOnly.RequiredTags.BundledProgramDependencies, t => Assert.Equal("REAL", t.DataType));
        Assert.False(programOnly.Validation.NativeImportAccepted);
        var fallback = await service.PrepareRoutineImportPackageAsync(request);
        var bytes = File.ReadAllBytes(service.Download(fallback.Routine.Id).Path); var xml = XmlProjects.Parse(bytes);
        Assert.Empty(xml.Descendants("Tag")); Assert.Empty(xml.Descendants("Tags")); Assert.Empty(xml.Descendants("Data"));
        Assert.Equal(4, fallback.RequiredTags.CreateBeforeImport.Length); Assert.Empty(fallback.RequiredTags.BundledProgramDependencies);
        Assert.Contains("-2.50e-3", fallback.RequiredTags.Tags.Single(t => t.Name == "HelperI").InitialData.Single().Xml);
        Assert.False(fallback.Validation.NativeImportAccepted); Assert.Empty(service.SdkJobList());
    }

    [Fact]
    public async Task Raw_initial_data_without_a_Format_attribute_is_retained_exactly_in_the_separate_manifest()
    {
        var request = await Request(Source(true).Replace(Tag("NewFlag", "BOOL", "0"), "<Tag Name='NewFlag' TagType='Base' DataType='BOOL'><Data>00 00 00 00</Data></Tag>"));
        var result = await service.PrepareRoutineImportPackageAsync(request);
        var data = Assert.Single(result.RequiredTags.Tags.Single(t => t.Name == "NewFlag").InitialData);
        Assert.Equal("Raw", data.Format); Assert.Equal("<Data>00 00 00 00</Data>", data.Xml);
        Assert.Empty(XmlProjects.Parse(File.ReadAllBytes(service.Download(result.Routine.Id).Path)).Descendants("Data"));
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
