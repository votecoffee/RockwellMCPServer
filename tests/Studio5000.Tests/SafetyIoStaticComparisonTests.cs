using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class SafetyIoStaticComparisonTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellStaticSafetyIo", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public SafetyIoStaticComparisonTests() { Directory.CreateDirectory(root); service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [root] }); }
    private static (string Catalog, string Series, int Major, string Code, string Module, int Inputs, int Tests, int Outputs) Model(string id) => id switch
    {
        "1734-IB8S/B" => ("1734-IB8S", "B", 2, "15", "1734_IB8S", 8, 4, 0),
        "1732ES-IB16/A" => ("1732ES-IB16", "A", 1, "30", "1732ES_IB16", 16, 16, 0),
        _ => ("1732ES-IB8XOBV4", "A", 1, "9", "1732ES_IB8XOB8", 8, 8, 8)
    };
    private SafetyIoStaticProfileRequest Profile(string id)
    {
        var m = Model(id); var fields = new List<(string Name, string Type)> { ("CfgSize", "DINT"), ("CfgIDNum", "DINT"), ("ConfigCRC", "DINT"), ("ConfigTime", "DINT"), ("ConfigDate", "INT"), ("ConfigPad", "INT") };
        for (var i = 0; i < m.Tests; i++) fields.Add(($"Test{i:00}Mode", "SINT"));
        if (m.Outputs > 0) { fields.Add(("OutputErrorLatchTime", "INT")); for (var i = 0; i < 8; i++) fields.Add(($"Pt{i:00}OutputMode", "SINT")); for (var i = 0; i < 8; i += 2) fields.Add(($"Pt{i:00}_{i + 1:00}OutputMode", "SINT")); }
        fields.Add(("InputErrorLatchTime", "INT"));
        for (var i = 0; i < m.Inputs; i++) fields.AddRange(new[] { ($"Pt{i:00}OffOnDelay", "INT"), ($"Pt{i:00}OnOffDelay", "INT"), ($"Pt{i:00}Mode", "SINT"), ($"Pt{i:00}TestSource", "SINT") });
        for (var i = 0; i < m.Inputs; i += 2) fields.AddRange(new[] { ($"Pt{i:00}_{i + 1:00}Mode", "SINT"), ($"Pad{i / 2}", "SINT"), ($"Pt{i:00}_{i + 1:00}ModeDiscrepancyTime", "INT") });
        var members = new XElement("Members", fields.Select(f => new XElement("Member", new XAttribute("Name", f.Name), new XAttribute("DataType", f.Type))));
        var enums = new Dictionary<string, string[]> { ["InputMode"] = ["Not Used", "Safety Pulse Test", "Safety", "Standard"], ["InputPtType"] = ["Single", "Equivalent", "Complementary"], ["TestMode"] = ["Not Used", "Standard", "Pulse Test", "Power Supply"], ["TestMode_w_MutingLamp"] = ["Not Used", "Standard", "Pulse Test", "Power Supply", "Muting Lamp"], ["OutputMode"] = ["Not Used", "Safety", "Safety Pulse Test"], ["OutputPtType"] = ["Single", "Dual"] };
        var sourceEnum = id == "1734-IB8S/B" ? "TestSource" : "TestSource" + m.Tests;
        enums[sourceEnum] = new[] { "None" }.Concat(Enumerable.Range(0, m.Tests).Select(i => "T" + i)).ToArray();
        void Bit(string name, string target, int width, string enumeration) => members.Add(new XElement("Member", new XAttribute("Name", name), new XAttribute("DataType", "BITFIELD"), new XAttribute("Target", target), new XAttribute("LowBit", "0"), new XAttribute("HiBit", width - 1), new XAttribute("Enum", enumeration)));
        for (var i = 0; i < m.Tests; i++) Bit($"Test{i:00}_Mode", $"Test{i:00}Mode", 3, i % (m.Tests == 4 ? 2 : 4) == (m.Tests == 4 ? 1 : 3) ? "TestMode_w_MutingLamp" : "TestMode");
        for (var i = 0; i < m.Inputs; i++) { Bit($"Pt{i:00}_Mode", $"Pt{i:00}Mode", 2, "InputMode"); Bit($"Pt{i:00}_TestSource", $"Pt{i:00}TestSource", m.Inputs == 16 ? 5 : 4, sourceEnum); }
        for (var i = 0; i < m.Inputs; i += 2) Bit($"Pt{i:00}_{i + 1:00}_Mode", $"Pt{i:00}_{i + 1:00}Mode", 2, "InputPtType");
        for (var i = 0; i < m.Outputs; i++) Bit($"Pt{i:00}_OutputMode", $"Pt{i:00}OutputMode", 2, "OutputMode");
        for (var i = 0; i < m.Outputs; i += 2) Bit($"Pt{i:00}_{i + 1:00}_OutputMode", $"Pt{i:00}_{i + 1:00}OutputMode", 2, "OutputPtType");
        var identity = new XElement("ModuleTypeInstall", new XElement("ModuleTypes", new XElement("ModuleType", new XElement("Identity", new XElement("CatalogNumber", m.Catalog), new XElement("VendorID", 1), new XElement("ProductType", 35), new XElement("ProductName", new XAttribute("ProductCode", m.Code)), new XElement("Variant", new XAttribute("MajorRev", m.Major), new XElement("Series", m.Series))))));
        var parameters = new XElement("AOPModuleTypes", new XElement("ModuleType", new XAttribute("VendorID", 1), new XAttribute("ProductType", 35), new XAttribute("ProductCode", m.Code), new XElement("Variant", new XAttribute("MajorRev", m.Major), new XAttribute("ModuleID", m.Module))), new XElement("DataType", new XAttribute("Name", "AB:" + m.Module + ":C:0"), members));
        foreach (var (name, labels) in enums)
        {
            parameters.Add(new XElement("Enum", new XAttribute("ID", name), new XAttribute("StringsID", name + "Labels"), labels.Select((_, i) => new XElement("Value", new XAttribute("StringID", i.ToString(CultureInfo.InvariantCulture)), i))));
            parameters.Add(new XElement("Strings", new XAttribute("ID", name + "Labels"), labels.Select((label, i) => new XElement("String", new XAttribute("ID", i.ToString(CultureInfo.InvariantCulture)), new XElement("Text", new XAttribute("LCID", "1033"), label)))));
        }
        SdkInputFile Save(string name, XElement xml) { var path = Path.Combine(root, Guid.NewGuid().ToString("N") + name + ".xml"); File.WriteAllText(path, xml.ToString()); return new(path, ProjectService.Hash(File.ReadAllBytes(path))); }
        return new(id, new(Save("identity", identity), Save("parameters", parameters)));
    }
    private static byte[] Payload(string id)
    {
        var size = id == "1734-IB8S/B" ? 90 : id == "1732ES-IB16/A" ? 166 : 108;
        var bytes = new byte[(size + 3) / 4 * 4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)size - 4); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 864);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0xAABBCCDD); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 0x12345678); bytes[16] = 0x34; bytes[17] = 0x56; bytes[18] = 0x89; bytes[19] = 0xAB;
        bytes[20] = 2; bytes[21] = 1;
        var mode = id == "1734-IB8S/B" ? 30 : id == "1732ES-IB16/A" ? 42 : 48;
        bytes[mode] = 2; bytes[mode + 1] = 1;
        if (id == "1734-IB8S/B") { bytes[54] = 1; bytes[55] = 1; }
        if (id == "1732ES-IB16/A") { bytes[132] = 3; bytes[133] = 16; }
        if (id == "1732ES-IB8XOBV4/A") { bytes[30] = 2; bytes[38] = 1; }
        if (size < bytes.Length) { bytes[size] = 0xA5; bytes[size + 1] = 0x5A; }
        return bytes;
    }
    private static string Vector(byte[] bytes, string encoding, int physical, bool hexadecimal = false)
    {
        var values = encoding == "flat-bytes" ? bytes.Take(physical).Select(b => unchecked((sbyte)b).ToString(CultureInfo.InvariantCulture)).ToArray() :
            Enumerable.Range(0, bytes.Length / 4).Select(i => hexadecimal ? "16#" + BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4)).ToString("X8", CultureInfo.InvariantCulture) : BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4, 4)).ToString(CultureInfo.InvariantCulture)).ToArray();
        return "[\r\n  " + string.Join(",\r\n  ", values) + "\r\n]";
    }
    private async Task<SafetyIoStaticBrowseRequest> Register(string id, SafetyIoStaticProfileRequest profile, byte[] payload, string encoding = "packed-dint32-le", string? identityOverride = null, string size = "82", string ip = "192.0.2.10", bool hex = false, Action<XElement>? identityEdit = null)
    {
        var m = Model(id); var info = service.InspectSafetyIoStaticProfile(profile);
        var xml = "<RSLogix5000Content SoftwareRevision='35.00' TargetType='Controller' ContainsContext='false'><Controller Name='Synthetic'><Modules><Module Name='SafetyInput' CatalogNumber='" + id + "' Vendor='1' ProductType='35' ProductCode='" + m.Code + "' Major='" + m.Major + "' Minor='1' " + (identityOverride ?? "") + "><Ports><Port Address='" + ip + "'/></Ports><SafetyNetwork><Number>retained-native-SNN</Number></SafetyNetwork><Communications><ConfigData ConfigSize='" + size + "' DataType='AB:" + m.Module + ":C:0'><Data Format='L5K'><![CDATA[" + Vector(payload, encoding, info.ConfigurationBytes, hex) + "]]></Data></ConfigData></Communications></Module></Modules></Controller></RSLogix5000Content>";
        if (identityEdit is not null) { var document = XDocument.Parse(xml); identityEdit(document.Descendants("Module").Single()); xml = document.ToString(SaveOptions.DisableFormatting); }
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); File.WriteAllBytes(path, new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(xml)).ToArray()); var p = await service.RegisterAsync(path);
        return new(p.Id, p.CurrentSha256, p.Revision, "SafetyInput", profile, info.Sha256, encoding);
    }
    private static SafetyIoStaticValue Value(SafetyIoStaticSnapshot snapshot, string name) => snapshot.Values.Single(v => v.Name == name);

    [Theory] [InlineData("1734-IB8S/B",90,30)] [InlineData("1732ES-IB16/A",166,42)] [InlineData("1732ES-IB8XOBV4/A",108,48)]
    public async Task Explicit_native_packed_vectors_decode_reviewed_offsets_and_preserve_all_saved_bytes(string id, int length, int modeOffset)
    {
        var profile = Profile(id); var payload = Payload(id); var request = await Register(id, profile, payload);
        var source = service.Get(request.ProjectId); var before = File.ReadAllBytes(source.SourcePath);
        var decoded = await service.BrowseSafetyIoStaticAsync(request);
        Assert.Equal(length, decoded.Profile.ConfigurationBytes); Assert.Equal(payload.Length, decoded.PayloadBytes); Assert.Equal(payload.Length / 4, decoded.NativeTokenCount);
        Assert.Equal((uint)2, Value(decoded, "Pt00_Mode").Value); Assert.Equal(modeOffset, Value(decoded, "Pt00_Mode").Offset);
        Assert.Equal(0xAABBCCDDu, Value(decoded, "ConfigCRC").Value); Assert.Equal("82", decoded.NativeConfigSize);
        Assert.Equal(ProjectService.Hash(payload), decoded.PayloadSha256); Assert.Contains(decoded.Diagnostics, d => d.Contains("not-validated", StringComparison.Ordinal));
        if (id == "1734-IB8S/B") { Assert.Equal(86u, Value(decoded, "CfgSize").Value); Assert.Equal(1u, Value(decoded, "Pt04_Mode").Value); Assert.Equal(1u, Value(decoded, "Pt04_TestSource").Value); }
        if (id == "1732ES-IB16/A") Assert.Equal(16u, Value(decoded, "Pt15_TestSource").Value);
        if (id == "1732ES-IB8XOBV4/A") { Assert.Equal("AB:1732ES_IB8XOB8:C:0", decoded.Profile.ConfigDataType); Assert.Equal(2u, Value(decoded, "Pt00_OutputMode").Value); Assert.Equal(1u, Value(decoded, "Pt00_01_OutputMode").Value); }
        Assert.Equal(before, File.ReadAllBytes(source.SourcePath));
        var flat = await Register(id, profile, payload, "flat-bytes"); var hexadecimal = await Register(id, profile, payload, hex: true);
        Assert.True((await service.CompareSafetyIoStaticAsync(new(request, flat))).ParametersEqual); Assert.True((await service.CompareSafetyIoStaticAsync(new(request, hexadecimal))).ParametersEqual);
    }

    [Theory] [InlineData("1734-IB8S/B")] [InlineData("1732ES-IB16/A")] [InlineData("1732ES-IB8XOBV4/A")]
    public async Task Signature_envelope_padding_reserved_bits_and_IP_context_are_distinct_from_channel_parameters(string id)
    {
        var profile = Profile(id); var payload = Payload(id); var a = await Register(id, profile, payload); var changed = payload.ToArray();
        changed[0] ^= 1; changed[8] ^= 0x55; changed[12] ^= 0x11; changed[16] ^= 0x22; changed[18] ^= 0x33; changed[20] |= 0x80;
        var physical = service.InspectSafetyIoStaticProfile(profile).ConfigurationBytes; if (physical < changed.Length) changed[physical] ^= 0x77;
        var b = await Register(id, profile, changed, size: "unreviewed-native-envelope", ip: "198.51.100.99");
        var comparison = await service.CompareSafetyIoStaticAsync(new(a, b));
        Assert.True(comparison.ParametersEqual); Assert.Equal(0, comparison.ChangedParameters); Assert.False(comparison.ModuleContextEqual); Assert.False(comparison.NativeConfigSizeEqual);
        Assert.Contains(comparison.NonParameterDifferences, d => d.Category == "signature"); Assert.Contains(comparison.NonParameterDifferences, d => d.Category == "envelope"); Assert.Contains(comparison.NonParameterDifferences, d => d.Category == "reserved");
        if (physical < changed.Length) Assert.False(comparison.TerminalPaddingEqual);
        Assert.NotNull(comparison.Receipt); Assert.Equal("not-performed", System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(service.Download(comparison.Receipt!.Id).Path)).RootElement.GetProperty("nativeAcceptance").GetString());
    }

    [Theory] [InlineData("1734-IB8S/B",30)] [InlineData("1732ES-IB16/A",42)] [InlineData("1732ES-IB8XOBV4/A",48)]
    public async Task Parameter_differences_are_paged_against_the_same_immutable_comparison(string id, int modeOffset)
    {
        var profile = Profile(id); var payload = Payload(id); var a = await Register(id, profile, payload); payload[modeOffset] = 3; payload[modeOffset - 4] = 7;
        var b = await Register(id, profile, payload); var one = await service.CompareSafetyIoStaticAsync(new(a, b, Limit: 1)); var two = await service.CompareSafetyIoStaticAsync(new(a, b, Offset: 1, Limit: 1));
        Assert.False(one.ParametersEqual); Assert.Equal(2, one.ChangedParameters); Assert.Equal(1, one.NextOffset); Assert.Single(two.Rows); Assert.Equal(one.ComparisonSha256, two.ComparisonSha256); Assert.NotEqual(one.Rows[0].Name, two.Rows[0].Name);
    }

    [Theory] [InlineData("Series='A'")] [InlineData("Series='C'")]
    public async Task Conflicting_suffix_series_is_refused(string series)
    { var profile = Profile("1734-IB8S/B"); var request = await Register("1734-IB8S/B", profile, Payload("1734-IB8S/B"), identityOverride: series); await Assert.ThrowsAsync<OperationException>(() => service.BrowseSafetyIoStaticAsync(request)); }

    [Fact]
    public async Task Unknown_enums_remain_explicit_and_do_not_imply_semantic_acceptance()
    { var id = "1734-IB8S/B"; var profile = Profile(id); var payload = Payload(id); payload[31] = 15; var a = await Register(id, profile, payload); var report = await service.CompareSafetyIoStaticAsync(new(a, a)); Assert.True(report.ParametersEqual); Assert.True(report.HasUnknownParameterValues); Assert.False(Value(report.Before, "Pt00_TestSource").Known); }

    [Fact]
    public async Task Stale_profile_source_and_wrong_encoding_are_refused()
    {
        var id = "1734-IB8S/B"; var profile = Profile(id); var a = await Register(id, profile, Payload(id));
        await Assert.ThrowsAsync<OperationException>(() => service.BrowseSafetyIoStaticAsync(a with { ExpectedSha256 = new string('0',64) }));
        await Assert.ThrowsAsync<OperationException>(() => service.BrowseSafetyIoStaticAsync(a with { ExpectedProfileSha256 = new string('0',64) }));
        await Assert.ThrowsAsync<OperationException>(() => service.BrowseSafetyIoStaticAsync(a with { Encoding = "flat-bytes" }));
        File.AppendAllText(profile.Files.Parameters.Path!, " "); await Assert.ThrowsAsync<OperationException>(() => service.BrowseSafetyIoStaticAsync(a));
    }
    [Theory] [InlineData("Vendor","2")] [InlineData("ProductType","7")] [InlineData("ProductCode","999")] [InlineData("Major","99")] [InlineData("CatalogNumber","1734-IB8S/A")] [InlineData("CatalogNumber","1734-IB8S/B/extra")]
    public async Task Product_identity_checks_are_not_loosened_to_accept_a_catalog_suffix(string attribute, string value)
    { var id = "1734-IB8S/B"; var request = await Register(id,Profile(id),Payload(id),identityEdit: m => m.SetAttributeValue(attribute,value)); await Assert.ThrowsAsync<OperationException>(() => service.BrowseSafetyIoStaticAsync(request)); }

    [Fact]
    public async Task Base_catalog_with_matching_explicit_series_is_supported_without_rewriting()
    { var id = "1732ES-IB16/A"; var request = await Register(id,Profile(id),Payload(id),identityOverride:"Series='A'",identityEdit: m => m.SetAttributeValue("CatalogNumber","1732ES-IB16")); var snapshot=await service.BrowseSafetyIoStaticAsync(request); Assert.Equal("1732ES-IB16",snapshot.CatalogNumber); }

    [Theory] [InlineData("[1,2,]")] [InlineData("[[1]]")]
    public void Malformed_vectors_are_refused(string vector) => Assert.Throws<OperationException>(() => SafetyIoStaticDecoding.Payload(vector,"packed-dint32-le",90,out _));

    [Theory] [InlineData("4294967296")] [InlineData("-2147483649")] [InlineData("16#FFFFFFFFFFFFFFFF")]
    public void Out_of_range_words_are_refused_even_when_vector_length_matches(string literal)
    { var vector="["+string.Join(",",new[]{literal}.Concat(Enumerable.Repeat("0",22)))+"]"; Assert.Throws<OperationException>(() => SafetyIoStaticDecoding.Payload(vector,"packed-dint32-le",90,out _)); }

    [Fact]
    public void Unsigned_DWORD_maximum_is_preserved_without_sign_extension()
    { var vector="[16#FFFFFFFF,"+string.Join(",",Enumerable.Repeat("0",22))+"]"; var bytes=SafetyIoStaticDecoding.Payload(vector,"packed-dint32-le",90,out _); Assert.Equal(uint.MaxValue,BinaryPrimitives.ReadUInt32LittleEndian(bytes)); }

    [Fact]
    public async Task Cross_family_comparison_and_alternate_representations_are_not_auto_aligned()
    {
        var a=await Register("1734-IB8S/B",Profile("1734-IB8S/B"),Payload("1734-IB8S/B")); var b=await Register("1732ES-IB16/A",Profile("1732ES-IB16/A"),Payload("1732ES-IB16/A"));
        Assert.Equal("safety_io_static_profile_mismatch",(await Assert.ThrowsAsync<OperationException>(() => service.CompareSafetyIoStaticAsync(new(a,b)))).Code);
        var duplicate=await Register("1734-IB8S/B",Profile("1734-IB8S/B"),Payload("1734-IB8S/B"),identityEdit:m => m.Descendants("ConfigData").Single().Add(new XElement("Data",new XAttribute("Format","Decorated"))));
        await Assert.ThrowsAsync<OperationException>(() => service.BrowseSafetyIoStaticAsync(duplicate));
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root,true); }
}
