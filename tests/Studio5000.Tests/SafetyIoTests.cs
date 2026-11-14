using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Studio5000.Extensibility;
using Studio5000.Extensions.Rockwell;
using Xunit;

namespace Studio5000.Tests;

public sealed class SafetyIoTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellSafetyIo", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    private readonly SafetyIoProfile profile;
    private readonly string parametersPath;
    public SafetyIoTests()
    {
        Directory.CreateDirectory(root); service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
        var identity = new XElement("ModuleTypeInstall", new XElement("ModuleTypes", new XElement("ModuleType", new XAttribute("Version", "4"), new XElement("Identity",
            new XElement("CatalogNumber", "1734-IB8S"), new XElement("VendorID", "1"), new XElement("ProductType", "35"), new XElement("ProductName", new XAttribute("ProductCode", "15"), "1734-IB8S"), new XElement("Variant", new XAttribute("MajorRev", "2"), new XElement("Series", "B"))))));
        var members = new XElement("Members");
        void Member(string name, string type) => members.Add(new XElement("Member", new XAttribute("Name", name), new XAttribute("DataType", type)));
        void Bits(string name, string target, int high, string enumeration) => members.Add(new XElement("Member", new XAttribute("Name", name), new XAttribute("DataType", "BITFIELD"), new XAttribute("Target", target), new XAttribute("LowBit", "0"), new XAttribute("HiBit", high), new XAttribute("Enum", enumeration)));
        foreach (var name in new[] { "CfgSize", "CfgIDNum", "ConfigCRC", "ConfigTime" }) Member(name, "DINT");
        Member("ConfigDate", "INT"); Member("ConfigPad", "INT");
        for (var i = 0; i < 4; i++) { Member($"Test{i:00}Mode", "SINT"); Bits($"Test{i:00}_Mode", $"Test{i:00}Mode", 2, i % 2 == 0 ? "TestMode" : "TestMode_w_MutingLamp"); }
        Member("InputErrorLatchTime", "INT");
        for (var i = 0; i < 8; i++)
        {
            Member($"Pt{i:00}OffOnDelay", "INT"); Member($"Pt{i:00}OnOffDelay", "INT"); Member($"Pt{i:00}Mode", "SINT"); Bits($"Pt{i:00}_Mode", $"Pt{i:00}Mode", 1, "InputMode");
            Member($"Pt{i:00}TestSource", "SINT"); Bits($"Pt{i:00}_TestSource", $"Pt{i:00}TestSource", 3, "TestSource");
        }
        for (var i = 0; i < 4; i++) { Member($"Pt{i * 2:00}_{i * 2 + 1:00}Mode", "SINT"); Bits($"Pt{i * 2:00}_{i * 2 + 1:00}_Mode", $"Pt{i * 2:00}_{i * 2 + 1:00}Mode", 1, "InputPtType"); Member("Pad" + i, "SINT"); Member($"Pt{i * 2:00}_{i * 2 + 1:00}ModeDiscrepancyTime", "INT"); }
        var parameters = new XElement("AOPModuleTypes", new XElement("ModuleType", new XAttribute("VendorID", "1"), new XAttribute("ProductType", "35"), new XAttribute("ProductCode", "15"), new XElement("Variant", new XAttribute("MajorRev", "2"), new XAttribute("ModuleID", "1734_IB8S"))), new XElement("DataType", new XAttribute("Name", "AB:1734_IB8S:C:0"), members));
        void Enum(string name, params string[] labels)
        {
            parameters.Add(new XElement("Enum", new XAttribute("ID", name), new XAttribute("StringsID", name + "Strings"), labels.Select((label, i) => new XElement("Value", new XAttribute("StringID", i + 1), i))),
                new XElement("Strings", new XAttribute("ID", name + "Strings"), labels.Select((label, i) => new XElement("String", new XAttribute("ID", i + 1), new XElement("Text", new XAttribute("LCID", "1033"), label)))));
        }
        Enum("InputMode", "Not Used", "Safety Pulse Test", "Safety", "Standard"); Enum("TestSource", "None", "0", "1", "2", "3");
        Enum("TestMode", "Not Used", "Standard", "Pulse Test", "Power Supply"); Enum("TestMode_w_MutingLamp", "Not Used", "Standard", "Pulse Test", "Power Supply", "Muting Lamp"); Enum("InputPtType", "Single", "Equivalent", "Complementary");
        var identityPath = Path.Combine(root, "identity.synthetic.xml"); parametersPath = Path.Combine(root, "parameters.synthetic.xml");
        File.WriteAllText(identityPath, identity.ToString()); File.WriteAllText(parametersPath, parameters.ToString());
        profile = service.InspectSafetyIoProfile(new(new(identityPath, ProjectService.Hash(File.ReadAllBytes(identityPath))), new(parametersPath, ProjectService.Hash(File.ReadAllBytes(parametersPath)))));
    }
    private static byte[] Payload()
    {
        var bytes = new byte[90]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 90); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 864);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0xAABBCCDD); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 0x12345678); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 0x3456);
        bytes[18] = 11; bytes[19] = 12; bytes[20] = 2; bytes[21] = 2; bytes[24] = 0xE8; bytes[25] = 3;
        for (var i = 0; i < 8; i++) { bytes[26 + i * 6] = (byte)(i + 1); bytes[28 + i * 6] = (byte)(i * 2 + 2); }
        bytes[54] = 1; bytes[55] = 1; bytes[60] = 1; bytes[61] = 2;
        for (var i = 0; i < 4; i++) { bytes[75 + i * 4] = (byte)(10 + i); bytes[76 + i * 4] = 10; }
        return bytes;
    }
    private static string Vector(byte[] bytes, bool hex = false) => "[\r\n " + string.Join(", ", bytes.Select(b => hex ? "16#" + b.ToString("X2") : unchecked((sbyte)b).ToString(System.Globalization.CultureInfo.InvariantCulture))) + "\r\n]";
    private static string Source(string vector, bool utf16 = false) => "<?xml version='1.0' encoding='" + (utf16 ? "utf-16" : "utf-8") + "'?>\r\n<!-- synthetic profile and module; not a native import fixture -->\r\n<RSLogix5000Content><Controller Name='Synthetic'><SafetyInfo Signature='retained'/><DataTypes/><AddOnInstructionDefinitions><EncodedData Name='Library'><![CDATA[keep $N\r\n]]></EncodedData></AddOnInstructionDefinitions><Modules><Module Name='SafetyIn' CatalogNumber='1734-IB8S' Series='B' Vendor='1' ProductType='35' ProductCode='15' Major='2' Minor='1' SafetyEnabled='true'><Ports><Port Id='1' Address='7'/></Ports><Communications SafetyNetwork='16#0001_0002_0003'><ConfigData ConfigSize='90'><Data Format='L5K'><![CDATA[" + vector + "]]></Data></ConfigData><Connections><Connection Name='Input' Type='SafetyInput' RPI='10000' NetworkDelayMultiplier='20' ReactionTimeLimit='40.1'><InputData><Data Format='L5K'><![CDATA[[42]]]></Data><ForceData><![CDATA[opaque forces]]></ForceData></InputData><OutputData><Data Format='L5K'><![CDATA[[7]]]></Data></OutputData></Connection></Connections></Communications></Module></Modules><Tags/><Programs/></Controller></RSLogix5000Content>\r\n";
    private async Task<ProjectInfo> Register(string text, bool utf16 = false)
    { var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".L5X"); File.WriteAllText(path, text, utf16 ? new UnicodeEncoding(false, true) : new UTF8Encoding(true)); return await service.RegisterAsync(path); }
    private SafetyIoBrowseRequest Browse(ProjectInfo p) => new(p.Id, p.CurrentSha256, p.Revision, "SafetyIn", profile.Files, profile.Sha256);
    private SafetyIoEditRequest Request(SafetyIoConfiguration c) => new(new(c.Source.Id, c.Source.Sha256, c.Source.Revision, c.ModuleName, profile.Files, profile.Sha256), c.ConfigurationSha256,
        [new(6, "NotUsed", "None", "Safety", "None"), new(7, "NotUsed", "None", "Safety", "None")], [new(2, "NotUsed", "Standard"), new(3, "NotUsed", "PowerSupply")], "review-only", "preserve-unvalidated");
    private static SafetyIoApplyRequest Apply(SafetyIoPreview p) => new(p.Request, p.ReviewSha256, p.PreviewReceipt!.Id, p.PreviewReceipt.Sha256);

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)]
    public async Task Requested_changes_preserve_every_other_byte_signature_and_E_stop_channel(bool utf16, bool hex)
    {
        var bytes = Payload(); var text = Source(Vector(bytes, hex), utf16); var p = await Register(text, utf16); var original = File.ReadAllBytes(p.SourcePath);
        var browse = await service.BrowseSafetyIoAsync(Browse(p)); Assert.Equal(55, profile.Fields.Length); Assert.Equal(90, profile.ConfigurationBytes);
        Assert.Equal("SafetyPulseTest", browse.Inputs[4].Mode); Assert.Equal("T0", browse.Inputs[4].TestSource); Assert.Equal("T1", browse.Inputs[5].TestSource);
        Assert.Equal("2864434397", browse.Signature.ConfigCRC); Assert.Equal("305419896", browse.Signature.ConfigTime); Assert.Equal("13398", browse.Signature.ConfigDate);
        var preview = await service.PreviewSafetyIoAsync(Request(browse)); Assert.True(preview.CanApply); Assert.True(preview.NativeAopRoundTripRequired); Assert.True(preview.UnselectedBytesPreserved);
        Assert.Equal(new[] { 22, 23, 66, 72 }, preview.Changes.Select(c => c.Offset).Order());
        Assert.Equal("unvalidated-after-parameter-change", preview.SignatureStatus); Assert.Equal(browse.Signature.ConfigCRC, preview.After.Signature.ConfigCRC);
        Assert.Equal("unvalidated-after-parameter-change", preview.After.Signature.Status);
        Assert.Equal(browse.Inputs[4], preview.After.Inputs[4]); Assert.Equal(browse.Inputs[5], preview.After.Inputs[5]);
        var result = await service.ApplySafetyIoAsync(Apply(preview)); Assert.True(result.ReviewOnly); Assert.Equal("not-performed", result.NativeAcceptance);
        bytes[22] = 1; bytes[23] = 3; bytes[66] = 2; bytes[72] = 2;
        var expected = text.Replace(Vector(Payload(), hex), Vector(bytes, hex), StringComparison.Ordinal); Encoding encoding = utf16 ? new UnicodeEncoding(false, true) : new UTF8Encoding(true);
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(expected)).ToArray(), File.ReadAllBytes(service.Download(result.Candidate.Id).Path));
        Assert.Equal(original, File.ReadAllBytes(p.SourcePath)); Assert.Equal(p, service.Get(p.Id)); Assert.Empty(service.SdkJobList());
        Assert.Equal(result, await service.ApplySafetyIoAsync(Apply(preview)));
        Assert.Equal("applied", (await service.SafetyIoStatusAsync(preview.PreviewReceipt!.Id, preview.PreviewReceipt.Sha256)).Status);
    }
    [Fact]
    public async Task Default_signature_policy_blocks_apply_and_zero_is_not_a_supported_policy()
    {
        var p = await Register(Source(Vector(Payload()))); var request = Request(await service.BrowseSafetyIoAsync(Browse(p)));
        var blocked = await service.PreviewSafetyIoAsync(request with { SafetyHandling = "block", SignatureHandling = "require-native" });
        Assert.False(blocked.CanApply); Assert.Null(blocked.ResultSha256);
        Assert.Equal("safety_io_blocked", (await Assert.ThrowsAsync<OperationException>(() => service.ApplySafetyIoAsync(Apply(blocked)))).Code);
        Assert.Equal("safety_io_policy", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewSafetyIoAsync(request with { SignatureHandling = "zero" }))).Code);
    }
    [Fact]
    public async Task Source_revision_profiles_configuration_before_values_and_receipts_are_pinned()
    {
        var p = await Register(Source(Vector(Payload()))); var browse = await service.BrowseSafetyIoAsync(Browse(p)); var request = Request(browse);
        foreach (var bad in new[] { request with { Source = request.Source with { ExpectedRevision = p.Revision + 1 } }, request with { Source = request.Source with { ExpectedSha256 = new string('0', 64) } },
            request with { Source = request.Source with { ExpectedProfileSha256 = new string('0', 64) } }, request with { ExpectedConfigurationSha256 = new string('0', 64) }, request with { Inputs = [new(6, "Standard", "None", "Safety", "None")] } })
            Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewSafetyIoAsync(bad))).Code);
        var preview = await service.PreviewSafetyIoAsync(request);
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.ApplySafetyIoAsync(Apply(preview) with { ExpectedReviewSha256 = new string('0', 64) }))).Code);
        Assert.Equal("safety_io_receipt", (await Assert.ThrowsAsync<OperationException>(() => service.ApplySafetyIoAsync(Apply(preview) with { PreviewReceiptSha256 = new string('0', 64) }))).Code);
        File.AppendAllText(parametersPath, "\n<!-- changed after review -->");
        Assert.Equal("stale_revision", (await Assert.ThrowsAsync<OperationException>(() => service.ApplySafetyIoAsync(Apply(preview)))).Code);
    }
    [Theory]
    [InlineData("coupled-output")] [InlineData("no-source")] [InlineData("wrong-output")] [InlineData("muting-on-T2")] [InlineData("pair-mode")] [InlineData("reserved-bits")]
    public async Task Unsupported_or_conflicting_point_changes_are_rejected(string scenario)
    {
        var payload = Payload(); if (scenario == "pair-mode") payload[86] = 1; if (scenario == "reserved-bits") payload[66] = 128;
        var p = await Register(Source(Vector(payload))); var request = Request(await service.BrowseSafetyIoAsync(Browse(p)));
        request = scenario switch
        {
            "coupled-output" => request with { Inputs = [], TestOutputs = [new(0, "PulseTest", "Standard")] },
            "no-source" => request with { Inputs = [new(6, "NotUsed", "None", "SafetyPulseTest", "None")], TestOutputs = [] },
            "wrong-output" => request with { Inputs = [new(6, "NotUsed", "None", "SafetyPulseTest", "T2")], TestOutputs = [new(2, "NotUsed", "Standard")] },
            "muting-on-T2" => request with { Inputs = [], TestOutputs = [new(2, "NotUsed", "MutingLamp")] }, _ => request
        };
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewSafetyIoAsync(request)); Assert.Single(service.Projects());
    }
    [Theory]
    [InlineData("short")] [InlineData("long")] [InlineData("nested")] [InlineData("overflow")] [InlineData("alternate")] [InlineData("series")] [InlineData("identity")]
    public async Task Unknown_raw_formats_and_other_module_identities_are_not_guessed(string scenario)
    {
        var vector = scenario switch { "short" => Vector(Payload()[..89]), "long" => Vector(Payload().Concat(new byte[] { 0 }).ToArray()), "nested" => "[" + Vector(Payload()) + "]", "overflow" => Vector(Payload()).Replace("90,", "256,", StringComparison.Ordinal), _ => Vector(Payload()) };
        var text = Source(vector);
        if (scenario == "alternate") text = text.Replace("</ConfigData>", "<Data Format='Decorated'><Structure/></Data></ConfigData>", StringComparison.Ordinal);
        if (scenario == "series") text = text.Replace("Series='B'", "Series='A'", StringComparison.Ordinal);
        if (scenario == "identity") text = text.Replace("ProductCode='15'", "ProductCode='16'", StringComparison.Ordinal);
        var p = await Register(text); Assert.Equal("safety_io_configuration", (await Assert.ThrowsAsync<OperationException>(() => service.BrowseSafetyIoAsync(Browse(p)))).Code);
    }
    [Fact]
    public void Profile_layout_drift_is_rejected_even_if_the_changed_file_is_rehashed()
    {
        var xml = XDocument.Load(parametersPath); xml.Descendants("Member").First(m => (string?)m.Attribute("Name") == "CfgSize").SetAttributeValue("DataType", "INT"); xml.Save(parametersPath);
        var changed = profile.Files with { Parameters = new(parametersPath, ProjectService.Hash(File.ReadAllBytes(parametersPath))) };
        Assert.Equal("safety_io_profile", Assert.Throws<OperationException>(() => service.InspectSafetyIoProfile(changed)).Code);
    }
    [Fact]
    public async Task Unknown_apply_intent_is_retained_and_not_replayed()
    {
        var p = await Register(Source(Vector(Payload()))); var preview = await service.PreviewSafetyIoAsync(Request(await service.BrowseSafetyIoAsync(Browse(p))));
        var path = Path.Combine(Path.GetDirectoryName(service.Download(preview.PreviewReceipt!.Id).Path)!, "safety-io-apply.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { status = "applying-indeterminate", reviewSha256 = preview.ReviewSha256, result = (object?)null }));
        Assert.Equal("safety_io_indeterminate", (await Assert.ThrowsAsync<OperationException>(() => service.ApplySafetyIoAsync(Apply(preview)))).Code);
        Assert.Single(service.Projects());
    }
    [Fact]
    public void Shared_operation_schemas_expose_saved_file_review_effects()
    {
        var catalog = new LogixExtension().Describe();
        Assert.Equal("read", catalog.Single(o => o.Name == "studio_safety_io_profiles").Effect);
        Assert.Equal("read", catalog.Single(o => o.Name == "studio_safety_io_browse").Effect);
        Assert.Equal("preview", catalog.Single(o => o.Name == "studio_safety_io_preview").Effect);
        Assert.Equal("apply", catalog.Single(o => o.Name == "studio_safety_io_apply").Effect);
        ExtensionSchema.CheckDefinition(catalog.Single(o => o.Name == "studio_safety_io_preview").InputSchema);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
