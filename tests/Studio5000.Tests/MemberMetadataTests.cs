using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class MemberMetadataTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellMemberMetadata", Guid.NewGuid().ToString("N"));
    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ComplexMemberMetadata.synthetic.l5x"));
    private static XElement Member(XDocument doc, string name) => doc.Descendants("DataType").Single(t => (string?)t.Attribute("Name") == "Outer").Elements("Members").Elements("Member").Single(m => (string?)m.Attribute("Name") == name);
    private static byte[] Bytes(XDocument doc) => Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
    private static MergeSnapshot Snapshot(ProjectInfo p) => new(p.Id, p.CurrentSha256);
    private async Task<ProjectInfo> Register(ProjectService service, byte[] bytes)
    { Directory.CreateDirectory(root); var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); await File.WriteAllBytesAsync(path, bytes); return await service.RegisterAsync(path); }

    [Fact]
    public void Synthetic_native_convention_fixture_keeps_atomic_styles_and_bit_host_metadata()
    {
        var bytes = Fixture(); var report = LogixMemberMetadata.Inspect(XmlProjects.Parse(bytes));
        Assert.Equal(12, report.Compared); Assert.Empty(report.Findings); Assert.False(report.NativeImportAccepted);
        Assert.Equal(bytes, Fixture()); Assert.Contains("separate from XSD", report.Boundary);
    }
    [Theory]
    [InlineData("Label")][InlineData("CustomLabel")][InlineData("Nested")][InlineData("History")]
    [InlineData("Delay")][InlineData("Total")][InlineData("Sequence")]
    public void Explicit_numeric_radix_on_scalar_or_array_complex_member_is_a_source_bound_failure(string name)
    {
        var doc = XmlProjects.Parse(Fixture()); Member(doc, name).SetAttributeValue("Radix", "Decimal"); var before = Bytes(doc);
        var finding = Assert.Single(LogixMemberMetadata.Inspect(doc).Findings);
        Assert.Equal("fail", finding.Status); Assert.Equal("NullType", finding.ExpectedRadix); Assert.Equal("Decimal", finding.ActualRadix);
        Assert.EndsWith("/@Radix", finding.Location); Assert.Equal(64, finding.Hash.Length); Assert.Contains(name, finding.Detail);
        Assert.Equal(before, Bytes(doc));
    }
    [Fact]
    public void Missing_radix_fails_for_known_complex_types_and_unknown_types_remain_unsupported()
    {
        var doc = XmlProjects.Parse(Fixture()); Member(doc, "Label").Attribute("Radix")!.Remove();
        Assert.Equal("fail", Assert.Single(LogixMemberMetadata.Inspect(doc).Findings).Status);
        Member(doc, "Label").SetAttributeValue("DataType", "UnknownModuleType");
        var finding = Assert.Single(LogixMemberMetadata.Inspect(doc).Findings); Assert.Equal("unsupported", finding.Status); Assert.Null(finding.ExpectedRadix);
    }
    [Fact]
    public void Xsd_attribute_enumeration_does_not_prove_complex_member_metadata_compatibility()
    {
        // Generated schema models the missing datatype/radix cross-field constraint;
        // this is not the vendor v35 schema and is not native import acceptance.
        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="Member"><xs:complexType>
            <xs:attribute name="Name" type="xs:string" use="required"/><xs:attribute name="DataType" type="xs:string" use="required"/>
            <xs:attribute name="Dimension" type="xs:nonNegativeInteger" use="required"/><xs:attribute name="Hidden" type="xs:boolean" use="required"/>
            <xs:attribute name="Radix" use="required"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="Decimal"/><xs:enumeration value="NullType"/></xs:restriction></xs:simpleType></xs:attribute>
            </xs:complexType></xs:element></xs:schema>
            """;
        var schemas = new XmlSchemaSet { XmlResolver = null }; using var reader = XmlReader.Create(new StringReader(xsd), new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        schemas.Add(null, reader); schemas.Compile();
        var doc = XmlProjects.Parse(Fixture()); Member(doc, "Label").SetAttributeValue("Radix", "Decimal");
        var member = new XElement(Member(doc, "Label")); var errors = new List<string>();
        member.Validate(schemas.GlobalElements[new XmlQualifiedName("Member")]!, schemas, (_, e) => errors.Add(e.Message));
        Assert.Empty(errors); Assert.Equal("fail", Assert.Single(LogixMemberMetadata.Inspect(doc).Findings).Status);
    }
    [Theory]
    [InlineData("Decimal")][InlineData(null)]
    public void Ordinary_authoring_rejects_invalid_new_complex_members_without_silently_normalizing(string? radix)
    {
        var bytes = Fixture(); var type = new XElement("DataType", new XAttribute("Name", "NewType"), new XElement("Members", new XElement("Member", new XAttribute("Name", "Label"), new XAttribute("DataType", "STRING"), radix is null ? null : new XAttribute("Radix", radix))));
        var edit = new Edit("add_element", "/RSLogix5000Content/Controller/DataTypes", Value: type.ToString(SaveOptions.DisableFormatting));
        Assert.Equal("udt_member_metadata", Assert.Throws<OperationException>(() => XmlProjects.Apply(bytes, [edit])).Code);
        type.Descendants("Member").Single().SetAttributeValue("Radix", "NullType");
        Assert.Empty(LogixMemberMetadata.Inspect(XmlProjects.Parse(XmlProjects.Apply(bytes, [edit with { Value = type.ToString(SaveOptions.DisableFormatting) }]))).Findings);
        Assert.Equal(bytes, Fixture());
    }
    [Fact]
    public void Unrelated_comment_edits_preserve_inherited_metadata_for_explicit_diagnostic_review()
    {
        var doc = XmlProjects.Parse(Fixture()); Member(doc, "Label").SetAttributeValue("Radix", "Decimal");
        var changed = XmlProjects.Apply(Bytes(doc), [new("set_text", "/RSLogix5000Content/Controller/DataTypes/DataType[@Name='Outer']/Description", Value: "Reviewed comment")]);
        Assert.Equal("Decimal", (string?)Member(XmlProjects.Parse(changed), "Label").Attribute("Radix"));
        Assert.Equal("fail", Assert.Single(LogixMemberMetadata.Inspect(XmlProjects.Parse(changed)).Findings).Status);
    }
    [Fact]
    public async Task Validation_exposes_nonwaivable_new_inherited_and_resolved_metadata_with_immutable_receipts()
    {
        using var service = new ProjectService(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], EnableControllerOperations = false });
        var doc = XmlProjects.Parse(Fixture()); Member(doc, "Label").SetAttributeValue("Radix", "Decimal"); var bad = await Register(service, Bytes(doc));
        var request = new LogixValidationRequest("offline-integrity", Snapshot(bad), Baseline: Snapshot(bad));
        var report = await service.PreviewLogixValidationAsync(request); Assert.Equal("fail", report.Status);
        var finding = Assert.Single(report.Findings, f => f.Check == LogixMemberMetadata.CheckName); Assert.Equal("inherited", finding.Classification);
        var receipt = await service.RecordLogixValidationAsync(new(request, report.ReviewSha256)); Assert.Equal("fail", service.LogixValidationReceipt(receipt.Artifact.Id, receipt.Artifact.Sha256).Report.Status);
        var native = new LogixValidationRequest("native-readback", Snapshot(bad), Reference: Snapshot(bad), ExpectedDifferences: [new(LogixMemberMetadata.CheckName, finding.Location, null, finding.ActualSha256, "Cannot waive this check")]);
        Assert.Equal("validation_policy", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewLogixValidationAsync(native))).Code);
        var good = await Register(service, Fixture()); var resolved = await service.PreviewLogixValidationAsync(new("offline-integrity", Snapshot(good), Baseline: Snapshot(bad)));
        Assert.Equal("pass", resolved.Status); Assert.Contains(resolved.Findings, f => f.Check == LogixMemberMetadata.CheckName && f.Classification == "resolved");
        var fresh = await service.PreviewLogixValidationAsync(request with { Baseline = null }); Assert.Contains(fresh.Findings, f => f.Check == LogixMemberMetadata.CheckName && f.Classification == "new");
        Assert.Equal(0, service.Get(bad.Id).Revision); Assert.Empty(service.SdkJobList());
    }
    [Fact]
    public async Task Structural_authoring_and_type_packaging_share_the_guard_without_starting_native_jobs()
    {
        using var service = new ProjectService(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], EnableControllerOperations = false });
        var good = await Register(service, Fixture()); var planPath = Path.Combine(root, "review.json"); await File.WriteAllTextAsync(planPath, "{\"purpose\":\"synthetic metadata fixture\"}");
        const string xpath = "/RSLogix5000Content/Controller/DataTypes"; var context = await service.StructuralContextAsync(new(good.Id, good.CurrentSha256, good.Revision, xpath));
        const string fragment = "<DataType Name='NewType'><Members><Member Name='Label' DataType='STRING' Radix='Decimal'/></Members></DataType>";
        var preview = await service.PreviewStructuralEditsAsync(new(good.Id, good.CurrentSha256, good.Revision, new(planPath, ProjectService.Hash(await File.ReadAllBytesAsync(planPath))),
            new([], [], ["NewType"], AcknowledgeIncompleteBindings: true), [new("add_element", xpath, context.Targets.Single().ElementSha256, fragment)]));
        Assert.False(preview.CanApply); Assert.Contains(preview.Findings, f => f.Check == LogixMemberMetadata.CheckName && f.Status == "blocked");
        var doc = XmlProjects.Parse(Fixture()); Member(doc, "Label").SetAttributeValue("Radix", "Decimal"); var bad = await Register(service, Bytes(doc));
        var error = await Assert.ThrowsAsync<OperationException>(() => service.PrepareTypeDependencyPackageAsync(new(Snapshot(bad), Snapshot(bad), DataTypes: ["Outer"])));
        Assert.Equal("type_dependency_member_metadata", error.Code); Assert.Empty(service.SdkJobList());
    }
    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RockwellMemberMetadata")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped its root.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
