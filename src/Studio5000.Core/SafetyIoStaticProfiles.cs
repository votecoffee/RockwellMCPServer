using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record SafetyIoStaticBit(string Name, string Target, int LowBit, int Width, string EnumId);
public sealed record SafetyIoStaticProfile(string Id, string CatalogNumber, string Series, int Major, string ProductCode,
    string ConfigDataType, int ConfigurationBytes, string Sha256, SafetyIoProfileFiles Files, SafetyIoField[] Fields,
    SafetyIoStaticBit[] Bits, SafetyIoEnum[] Enums, string Provenance);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SafetyIoStaticProfileRequest(string ProfileId, SafetyIoProfileFiles Files);

internal sealed record SafetyIoStaticModel(string Id, string Catalog, string Series, int Major, string ProductCode,
    string Directory, string IdentityFile, string ParametersFile, string ModuleId, string DataType, int Inputs, int Tests, int Outputs)
{
    internal static readonly SafetyIoStaticModel[] All = [
        new("1734-IB8S/B", "1734-IB8S", "B", 2, "15", "RA 1734S Discrete", "1734_IB8S.xml", "PS1734SafetyIO_Discrete.xml", "1734_IB8S", "AB:1734_IB8S:C:0", 8, 4, 0),
        new("1732ES-IB16/A", "1732ES-IB16", "A", 1, "30", "RA 1732ES Discrete", "1732ES_IB16.xml", "PS1732ES_Discrete.xml", "1732ES_IB16", "AB:1732ES_IB16:C:0", 16, 16, 0),
        new("1732ES-IB8XOBV4/A", "1732ES-IB8XOBV4", "A", 1, "9", "RA 1732ES Discrete", "1732ES_IB8XOBV4.xml", "PS1732ES_Discrete.xml", "1732ES_IB8XOB8", "AB:1732ES_IB8XOB8:C:0", 8, 8, 8)
    ];
    internal string Installed(string filename) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Rockwell Software", "RSLogix 5000", "Module Profiles", Directory, filename);
    internal static SafetyIoStaticModel Find(string id) => All.SingleOrDefault(m => m.Id == id) ?? throw new OperationException("safety_io_static_profile", "Select an exact supported static profile ID.");
    internal (string Name, string Type)[] Physical()
    {
        var fields = new List<(string, string)> { ("CfgSize", "DINT"), ("CfgIDNum", "DINT"), ("ConfigCRC", "DINT"), ("ConfigTime", "DINT"), ("ConfigDate", "INT"), ("ConfigPad", "INT") };
        for (var i = 0; i < Tests; i++) fields.Add(($"Test{i:00}Mode", "SINT"));
        if (Outputs > 0)
        {
            fields.Add(("OutputErrorLatchTime", "INT"));
            for (var i = 0; i < Outputs; i++) fields.Add(($"Pt{i:00}OutputMode", "SINT"));
            for (var i = 0; i < Outputs; i += 2) fields.Add(($"Pt{i:00}_{i + 1:00}OutputMode", "SINT"));
        }
        fields.Add(("InputErrorLatchTime", "INT"));
        for (var i = 0; i < Inputs; i++) fields.AddRange(new[] { ($"Pt{i:00}OffOnDelay", "INT"), ($"Pt{i:00}OnOffDelay", "INT"), ($"Pt{i:00}Mode", "SINT"), ($"Pt{i:00}TestSource", "SINT") });
        for (var i = 0; i < Inputs; i += 2) fields.AddRange(new[] { ($"Pt{i:00}_{i + 1:00}Mode", "SINT"), ($"Pad{i / 2}", "SINT"), ($"Pt{i:00}_{i + 1:00}ModeDiscrepancyTime", "INT") });
        return fields.ToArray();
    }
    internal SafetyIoStaticBit[] BitDefinitions()
    {
        var bits = new List<SafetyIoStaticBit>();
        for (var i = 0; i < Tests; i++) bits.Add(new($"Test{i:00}_Mode", $"Test{i:00}Mode", 0, 3,
            i % (Catalog == "1734-IB8S" ? 2 : 4) == (Catalog == "1734-IB8S" ? 1 : 3) ? "TestMode_w_MutingLamp" : "TestMode"));
        for (var i = 0; i < Inputs; i++) { bits.Add(new($"Pt{i:00}_Mode", $"Pt{i:00}Mode", 0, 2, "InputMode")); bits.Add(new($"Pt{i:00}_TestSource", $"Pt{i:00}TestSource", 0, Inputs == 16 ? 5 : 4, Catalog == "1734-IB8S" ? "TestSource" : "TestSource" + Tests)); }
        for (var i = 0; i < Inputs; i += 2) bits.Add(new($"Pt{i:00}_{i + 1:00}_Mode", $"Pt{i:00}_{i + 1:00}Mode", 0, 2, "InputPtType"));
        for (var i = 0; i < Outputs; i++) bits.Add(new($"Pt{i:00}_OutputMode", $"Pt{i:00}OutputMode", 0, 2, "OutputMode"));
        for (var i = 0; i < Outputs; i += 2) bits.Add(new($"Pt{i:00}_{i + 1:00}_OutputMode", $"Pt{i:00}_{i + 1:00}OutputMode", 0, 2, "OutputPtType"));
        return bits.ToArray();
    }
}

internal static class SafetyIoStaticProfiles
{
    private static OperationException Error(string text) => new("safety_io_static_profile", text);
    private static XDocument Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 1024 * 1024) throw Error("Each pinned AOP XML is limited to 1 MiB.");
        using var stream = new MemoryStream(bytes); using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var doc = XDocument.Load(reader); var count = 0;
        foreach (var e in doc.Descendants()) if (++count > 20000 || e.Ancestors().Take(33).Count() > 32 || e.Name.Namespace != XNamespace.None) throw Error("AOP namespace or XML budget is unsupported.");
        return doc;
    }
    internal static SafetyIoStaticProfile Load(SafetyIoStaticModel model, SafetyIoProfileFiles files, byte[] identityBytes, byte[] parameterBytes, string provenance)
    {
        var identity = Parse(identityBytes); var parameters = Parse(parameterBytes);
        var identities = identity.Descendants("Identity").Where(e => (string?)e.Element("CatalogNumber") == model.Catalog).ToArray();
        if (identity.Root?.Name != "ModuleTypeInstall" || parameters.Root?.Name != "AOPModuleTypes" || identities.Length != 1) throw Error("The pinned identity/profile pair is absent or ambiguous.");
        var id = identities[0];
        if ((string?)id.Element("VendorID") != "1" || (string?)id.Element("ProductType") != "35" || !id.Elements("ProductName").Any(e => (string?)e.Attribute("ProductCode") == model.ProductCode) ||
            !id.Elements("Variant").Any(v => (string?)v.Attribute("MajorRev") == model.Major.ToString(CultureInfo.InvariantCulture) && (string?)v.Element("Series") == model.Series) ||
            !parameters.Root.Elements("ModuleType").Any(e => (string?)e.Attribute("VendorID") == "1" && (string?)e.Attribute("ProductType") == "35" && (string?)e.Attribute("ProductCode") == model.ProductCode && e.Elements("Variant").Any(v => (string?)v.Attribute("MajorRev") == model.Major.ToString(CultureInfo.InvariantCulture) && (string?)v.Attribute("ModuleID") == model.ModuleId)))
            throw Error("Vendor/product/series/major and shared module identity must exactly match the supported adapter.");
        var types = parameters.Root.Elements("DataType").Where(t => (string?)t.Attribute("Name") == model.DataType && t.Element("Members") is not null).ToArray();
        if (types.Length != 1) throw Error("The configuration layout is absent or ambiguous.");
        var members = types[0].Element("Members")!.Elements("Member").ToArray(); var physical = members.Where(m => (string?)m.Attribute("DataType") != "BITFIELD").ToArray(); var expected = model.Physical();
        if (physical.Length != expected.Length || physical.Where((m, i) => (string?)m.Attribute("Name") != expected[i].Name || (string?)m.Attribute("DataType") != expected[i].Type || m.Attribute("Dimension") is not null || m.Attribute("Dimensions") is not null).Any())
            throw Error("Physical member order/types differ from the supported layout; offsets are never guessed.");
        var offset = 0; var fields = physical.Select(m => { var size = (string?)m.Attribute("DataType") == "DINT" ? 4 : (string?)m.Attribute("DataType") == "INT" ? 2 : 1; var f = new SafetyIoField((string)m.Attribute("Name")!, (string)m.Attribute("DataType")!, offset, size); offset += size; return f; }).ToArray();
        var bits = model.BitDefinitions();
        foreach (var bit in bits)
        {
            var nodes = members.Where(m => (string?)m.Attribute("Name") == bit.Name).ToArray();
            if (nodes.Length != 1 || (string?)nodes[0].Attribute("DataType") != "BITFIELD" || (string?)nodes[0].Attribute("Target") != bit.Target || (string?)nodes[0].Attribute("LowBit") != bit.LowBit.ToString(CultureInfo.InvariantCulture) || (string?)nodes[0].Attribute("HiBit") != (bit.LowBit + bit.Width - 1).ToString(CultureInfo.InvariantCulture) || (string?)nodes[0].Attribute("Enum") != bit.EnumId)
                throw Error("A channel bit field differs from the supported adapter.");
        }
        if (members.Length != fields.Length + bits.Length) throw Error("Unrecognized physical or bit fields require a separate adapter review.");
        var enums = new List<SafetyIoEnum>();
        foreach (var enumId in bits.Select(b => b.EnumId).Distinct(StringComparer.Ordinal))
        {
            var nodes = parameters.Root.Elements("Enum").Where(e => (string?)e.Attribute("ID") == enumId).ToArray();
            if (nodes.Length != 1) throw Error("Enum definition is missing or ambiguous.");
            var strings = parameters.Root.Elements("Strings").Where(e => (string?)e.Attribute("ID") == (string?)nodes[0].Attribute("StringsID")).ToArray();
            if (strings.Length != 1) throw Error("Enum labels are missing or ambiguous.");
            var values = new List<SafetyIoEnumValue>();
            foreach (var value in nodes[0].Elements("Value"))
            {
                if (!int.TryParse(value.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 0 or > 31) throw Error("Enum code is outside the supported range.");
                var labels = strings[0].Elements("String").Where(e => (string?)e.Attribute("ID") == (string?)value.Attribute("StringID")).Elements("Text").Where(t => (string?)t.Attribute("LCID") == "1033").ToArray();
                if (labels.Length != 1) throw Error("English enum label is missing or ambiguous.");
                var label = labels[0].Value.Trim(); values.Add(new(number, label.Replace(" ", "", StringComparison.Ordinal), label));
            }
            if (values.Count is < 1 or > 32 || values.Select(v => v.Value).Distinct().Count() != values.Count || values.Select(v => v.Key).Distinct().Count() != values.Count || bits.Where(b => b.EnumId == enumId).Any(b => values.Any(v => v.Value >= 1 << b.Width)))
                throw Error("Enum values are ambiguous or exceed the bit-field width.");
            enums.Add(new(enumId, values.ToArray()));
        }
        var hash = ProjectService.Hash(JsonSerializer.SerializeToUtf8Bytes(new { model.Id, adapter = "static-aop-layout-v1", identity = ProjectService.Hash(identityBytes), parameters = ProjectService.Hash(parameterBytes) }, ProjectService.Json));
        return new(model.Id, model.Catalog, model.Series, model.Major, model.ProductCode, model.DataType, offset, hash, files, fields, bits, enums.ToArray(), provenance);
    }
}

public sealed partial class ProjectService
{
    public SafetyIoStaticProfile InspectSafetyIoStaticProfile(SafetyIoStaticProfileRequest request)
    {
        var model = SafetyIoStaticModel.Find(request.ProfileId);
        byte[] ReadFile(SdkInputFile input, string filename)
        {
            if (input is null || !StructuralEditing.Hash(input.Sha256) || (input.Path is null) == (input.ArtifactId is null)) throw new OperationException("safety_io_static_profile", "Pin each profile path or artifact and SHA-256.");
            var path = input.Path is not null ? Path.GetFullPath(input.Path).Equals(model.Installed(filename), StringComparison.OrdinalIgnoreCase) ? model.Installed(filename) : AllowedSource(input.Path) : Download(input.ArtifactId!).Path;
            RejectLinks(path); if (new FileInfo(path).Length is < 1 or > 1024 * 1024) throw new OperationException("safety_io_static_profile", "Each AOP profile file is limited to 1 MiB.");
            var bytes = File.ReadAllBytes(path); if (!Hash(bytes).Equals(input.Sha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "A pinned AOP file changed."); return bytes;
        }
        if (request.Files is null) throw new OperationException("safety_io_static_profile", "Pin both identity and parameter XML files.");
        var identity = ReadFile(request.Files.Identity, model.IdentityFile); var parameters = ReadFile(request.Files.Parameters, model.ParametersFile);
        var installed = new[] { (request.Files.Identity, model.IdentityFile), (request.Files.Parameters, model.ParametersFile) }.All(p => p.Item1.Path is not null && Path.GetFullPath(p.Item1.Path).Equals(model.Installed(p.Item2), StringComparison.OrdinalIgnoreCase));
        return SafetyIoStaticProfiles.Load(model, request.Files, identity, parameters, installed ? "installed-AOP-files; no native AOP execution" : "caller-pinned-files; installed provenance not attested");
    }
    public SafetyIoStaticProfile[] SafetyIoStaticInstalledProfiles()
    {
        var profiles = new List<SafetyIoStaticProfile>();
        foreach (var model in SafetyIoStaticModel.All)
        {
            if (!File.Exists(model.Installed(model.IdentityFile)) || !File.Exists(model.Installed(model.ParametersFile))) continue;
            SdkInputFile Pin(string name) { var path = model.Installed(name); RejectLinks(path); if (new FileInfo(path).Length is < 1 or > 1024 * 1024) throw new OperationException("safety_io_static_profile", "Each AOP file is limited to 1 MiB."); return new(path, Hash(File.ReadAllBytes(path))); }
            profiles.Add(InspectSafetyIoStaticProfile(new(model.Id, new(Pin(model.IdentityFile), Pin(model.ParametersFile)))));
        }
        return profiles.ToArray();
    }
}
