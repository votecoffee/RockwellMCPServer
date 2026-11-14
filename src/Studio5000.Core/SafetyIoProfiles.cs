using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace Studio5000.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SafetyIoProfileFiles(SdkInputFile Identity, SdkInputFile Parameters);
public sealed record SafetyIoField(string Name, string DataType, int Offset, int Bytes);
public sealed record SafetyIoEnumValue(int Value, string Key, string Label);
public sealed record SafetyIoEnum(string Id, SafetyIoEnumValue[] Values);
public sealed record SafetyIoProfile(string Id, string CatalogNumber, string Series, int Major, string ConfigDataType,
    int ConfigurationBytes, string Sha256, SafetyIoProfileFiles Files, SafetyIoField[] Fields, SafetyIoEnum[] Enums, string Provenance);
internal sealed record SafetyIoBitField(string Name, SafetyIoField Storage, int LowBit, int Width, SafetyIoEnum Enum)
{
    internal int Mask => ((1 << Width) - 1) << LowBit;
    internal int Value(byte[] data) => (data[Storage.Offset] & Mask) >> LowBit;
    internal string Key(byte[] data) => Enum.Values.SingleOrDefault(v => v.Value == Value(data))?.Key ?? "Unknown:" + Value(data);
}

internal sealed class SafetyIoProfiles
{
    internal const string IdentityFile = "1734_IB8S.xml", ParametersFile = "PS1734SafetyIO_Discrete.xml", DataType = "AB:1734_IB8S:C:0";
    internal static string InstalledDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Rockwell Software", "RSLogix 5000", "Module Profiles", "RA 1734S Discrete");
    internal SafetyIoProfile Info { get; }
    private readonly Dictionary<string, SafetyIoBitField> bits;
    private SafetyIoProfiles(SafetyIoProfile info, Dictionary<string, SafetyIoBitField> bits) { Info = info; this.bits = bits; }
    internal SafetyIoBitField Bit(string name) => bits[name];
    internal IEnumerable<SafetyIoBitField> Bits => bits.Values;
    internal SafetyIoField Field(string name) => Info.Fields.Single(f => f.Name == name);
    private static OperationException Error(string detail) => new("safety_io_profile", detail);
    private static XDocument Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 1024 * 1024) throw Error("Each AOP profile XML is limited to 1 MiB.");
        using var stream = new MemoryStream(bytes); using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var doc = XDocument.Load(reader); var count = 0;
        foreach (var e in doc.Descendants()) if (++count > 20000 || e.Ancestors().Take(33).Count() > 32 || e.Name.Namespace != XNamespace.None) throw Error("Unsupported AOP namespace or XML budget.");
        return doc;
    }
    private static (string Name, string Type)[] ExpectedFields()
    {
        var fields = new List<(string, string)> { ("CfgSize", "DINT"), ("CfgIDNum", "DINT"), ("ConfigCRC", "DINT"), ("ConfigTime", "DINT"), ("ConfigDate", "INT"), ("ConfigPad", "INT") };
        for (var i = 0; i < 4; i++) fields.Add(($"Test{i:00}Mode", "SINT"));
        fields.Add(("InputErrorLatchTime", "INT"));
        for (var i = 0; i < 8; i++) fields.AddRange(new[] { ($"Pt{i:00}OffOnDelay", "INT"), ($"Pt{i:00}OnOffDelay", "INT"), ($"Pt{i:00}Mode", "SINT"), ($"Pt{i:00}TestSource", "SINT") });
        for (var i = 0; i < 4; i++) fields.AddRange(new[] { ($"Pt{2 * i:00}_{2 * i + 1:00}Mode", "SINT"), ($"Pad{i}", "SINT"), ($"Pt{2 * i:00}_{2 * i + 1:00}ModeDiscrepancyTime", "INT") });
        return fields.ToArray();
    }
    internal static SafetyIoProfiles Load(byte[] identityBytes, byte[] parametersBytes, SafetyIoProfileFiles files, string provenance)
    {
        var identity = Parse(identityBytes); var parametersDocument = Parse(parametersBytes); var parameters = parametersDocument.Root!;
        var identities = identity.Descendants("Identity").Where(e => (string?)e.Element("CatalogNumber") == "1734-IB8S").ToArray();
        if (identity.Root?.Name != "ModuleTypeInstall" || parameters.Name != "AOPModuleTypes" || identities.Length != 1) throw Error("Select the 1734-IB8S identity and parameter XML profiles.");
        var id = identities[0];
        if ((string?)id.Element("VendorID") != "1" || (string?)id.Element("ProductType") != "35" || !id.Elements("ProductName").Any(e => (string?)e.Attribute("ProductCode") == "15") ||
            !id.Elements("Variant").Any(e => (string?)e.Attribute("MajorRev") == "2" && (string?)e.Element("Series") == "B")) throw Error("The identity does not establish 1734-IB8S series B / major 2.");
        if (!parameters.Elements("ModuleType").Any(e => (string?)e.Attribute("VendorID") == "1" && (string?)e.Attribute("ProductType") == "35" && (string?)e.Attribute("ProductCode") == "15" &&
            e.Elements("Variant").Any(v => (string?)v.Attribute("MajorRev") == "2" && (string?)v.Attribute("ModuleID") == "1734_IB8S"))) throw Error("The parameter profile does not bind the matching module identity.");
        var types = parameters.Elements("DataType").Where(e => (string?)e.Attribute("Name") == DataType && e.Element("Members") is not null).ToArray();
        if (types.Length != 1) throw Error("Select exactly one supported module configuration type definition.");
        var members = types[0].Element("Members")!.Elements("Member").ToArray();
        var physical = members.Where(m => (string?)m.Attribute("DataType") != "BITFIELD").ToArray();
        var expected = ExpectedFields();
        if (physical.Length != expected.Length || physical.Where((m, i) => (string?)m.Attribute("Name") != expected[i].Name || (string?)m.Attribute("DataType") != expected[i].Type || m.Attribute("Dimension") is not null || m.Attribute("Dimensions") is not null).Any())
            throw Error("The physical member order/types differ from the supported 90-byte adapter; offsets are not guessed.");
        var offset = 0; var fields = new List<SafetyIoField>();
        foreach (var m in physical)
        {
            var type = (string)m.Attribute("DataType")!; var size = type == "DINT" ? 4 : type == "INT" ? 2 : 1;
            fields.Add(new((string)m.Attribute("Name")!, type, offset, size)); offset += size;
        }
        if (offset != 90) throw Error("The installed configuration layout is not 90 bytes.");
        var enums = new Dictionary<string, SafetyIoEnum>();
        foreach (var name in new[] { "InputMode", "TestSource", "TestMode", "TestMode_w_MutingLamp", "InputPtType" })
        {
            var definitions = parameters.Elements("Enum").Where(e => (string?)e.Attribute("ID") == name).ToArray();
            if (definitions.Length != 1) throw Error("A required point enum is missing or ambiguous.");
            var e = definitions[0]; var strings = parameters.Elements("Strings").Where(s => (string?)s.Attribute("ID") == (string?)e.Attribute("StringsID")).ToArray();
            if (strings.Length != 1) throw Error("Enum labels are missing or ambiguous.");
            var values = new List<SafetyIoEnumValue>();
            foreach (var v in e.Elements("Value"))
            {
                if (!int.TryParse(v.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 0 or > 15) throw Error("Unsupported enum encoding.");
                var labels = strings[0].Elements("String").Where(s => (string?)s.Attribute("ID") == (string?)v.Attribute("StringID")).Elements("Text").Where(t => (string?)t.Attribute("LCID") == "1033").ToArray();
                if (labels.Length != 1) throw Error("English enum label is missing or ambiguous.");
                var label = labels[0].Value.Trim(); var key = label.Replace(" ", "", StringComparison.Ordinal);
                if (name == "TestSource" && key != "None") key = "T" + key;
                values.Add(new(number, key, label));
            }
            var allowed = name == "InputMode" ? new[] { "NotUsed", "SafetyPulseTest", "Safety", "Standard" } : name == "TestSource" ? new[] { "None", "T0", "T1", "T2", "T3" } :
                name == "InputPtType" ? new[] { "Single", "Equivalent", "Complementary" } : name == "TestMode" ? new[] { "NotUsed", "Standard", "PulseTest", "PowerSupply" } : new[] { "NotUsed", "Standard", "PulseTest", "PowerSupply", "MutingLamp" };
            if (values.Select(v => v.Value).Distinct().Count() != values.Count || values.Select(v => v.Key).Distinct().Count() != values.Count || !values.Select(v => v.Key).Order().SequenceEqual(allowed.Order())) throw Error("Unsupported or ambiguous point enum semantics.");
            enums.Add(name, new(name, values.ToArray()));
        }
        var bits = new Dictionary<string, SafetyIoBitField>();
        void Bind(string name, string storage, int width, string enumName)
        {
            var aliases = members.Where(m => (string?)m.Attribute("Name") == name).ToArray();
            if (aliases.Length != 1) throw Error("Point bit field is absent or ambiguous.");
            var m = aliases[0];
            if ((string?)m.Attribute("DataType") != "BITFIELD" || (string?)m.Attribute("Target") != storage || (string?)m.Attribute("LowBit") != "0" || (string?)m.Attribute("HiBit") != (width - 1).ToString(CultureInfo.InvariantCulture) || (string?)m.Attribute("Enum") != enumName) throw Error("Point bit-field layout differs from the supported adapter.");
            if (enums[enumName].Values.Any(v => v.Value >= 1 << width)) throw Error("An enum exceeds its declared bit width.");
            bits.Add(name, new(name, fields.Single(f => f.Name == storage), 0, width, enums[enumName]));
        }
        for (var i = 0; i < 8; i++) { Bind($"Pt{i:00}_Mode", $"Pt{i:00}Mode", 2, "InputMode"); Bind($"Pt{i:00}_TestSource", $"Pt{i:00}TestSource", 4, "TestSource"); }
        for (var i = 0; i < 4; i++) Bind($"Test{i:00}_Mode", $"Test{i:00}Mode", 3, i % 2 == 0 ? "TestMode" : "TestMode_w_MutingLamp");
        for (var i = 0; i < 4; i++) Bind($"Pt{2 * i:00}_{2 * i + 1:00}_Mode", $"Pt{2 * i:00}_{2 * i + 1:00}Mode", 2, "InputPtType");
        if (members.Length != fields.Count + bits.Count) throw Error("Unrecognized configuration fields require a new adapter review.");
        var hash = ProjectService.Hash(JsonSerializer.SerializeToUtf8Bytes(new { identity = ProjectService.Hash(identityBytes), parameters = ProjectService.Hash(parametersBytes), adapter = "ib8s-b-raw90-v1" }, ProjectService.Json));
        return new(new("1734-IB8S/B", "1734-IB8S", "B", 2, DataType, offset, hash, files, fields.ToArray(), enums.Values.ToArray(), provenance), bits);
    }
}

public sealed partial class ProjectService
{
    private SafetyIoProfiles ReadSafetyIoProfile(SafetyIoProfileFiles files)
    {
        byte[] ReadProfile(SdkInputFile input, string name)
        {
            if (input is null || !StructuralEditing.Hash(input.Sha256) || (input.Path is null) == (input.ArtifactId is null)) throw new OperationException("safety_io_profile", "Pin each profile XML path or artifact and its SHA-256.");
            var installed = Path.Combine(SafetyIoProfiles.InstalledDirectory, name);
            var path = input.Path is not null ? Path.GetFullPath(input.Path).Equals(installed, StringComparison.OrdinalIgnoreCase) ? installed : AllowedSource(input.Path) : Download(input.ArtifactId!).Path;
            RejectLinks(path);
            if (new FileInfo(path).Length is < 1 or > 1024 * 1024) throw new OperationException("safety_io_profile", "Each profile XML is limited to 1 MiB.");
            var bytes = File.ReadAllBytes(path); if (!Hash(bytes).Equals(input.Sha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "A pinned AOP profile changed.");
            return bytes;
        }
        if (files is null) throw new OperationException("safety_io_profile", "Supply both pinned AOP profile files.");
        var identity = ReadProfile(files.Identity, SafetyIoProfiles.IdentityFile); var parameters = ReadProfile(files.Parameters, SafetyIoProfiles.ParametersFile);
        var installedFiles = new[] { (files.Identity, SafetyIoProfiles.IdentityFile), (files.Parameters, SafetyIoProfiles.ParametersFile) }.All(p => p.Item1.Path is not null && Path.GetFullPath(p.Item1.Path).Equals(Path.Combine(SafetyIoProfiles.InstalledDirectory, p.Item2), StringComparison.OrdinalIgnoreCase));
        return SafetyIoProfiles.Load(identity, parameters, files, installedFiles ? "installed-AOP-files; native AOP execution not performed" : "caller-pinned-profile-files; installed provenance not attested");
    }
    public SafetyIoProfile[] SafetyIoInstalledProfiles()
    {
        SdkInputFile Pin(string name)
        {
            var path = Path.Combine(SafetyIoProfiles.InstalledDirectory, name); RejectLinks(path);
            if (!File.Exists(path)) throw new OperationException("safety_io_profile_missing", "Install the matching RA 1734S Discrete AOP to discover 1734-IB8S/B.");
            if (new FileInfo(path).Length is < 1 or > 1024 * 1024) throw new OperationException("safety_io_profile", "Each AOP profile XML is limited to 1 MiB.");
            return new(path, Hash(File.ReadAllBytes(path)));
        }
        return [ReadSafetyIoProfile(new(Pin(SafetyIoProfiles.IdentityFile), Pin(SafetyIoProfiles.ParametersFile))).Info];
    }
    public SafetyIoProfile InspectSafetyIoProfile(SafetyIoProfileFiles files) => ReadSafetyIoProfile(files).Info;
}
