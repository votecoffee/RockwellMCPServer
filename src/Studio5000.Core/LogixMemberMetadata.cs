using System.Text;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record LogixMemberMetadataFinding(string Location, string Status, string Detail, string Hash,
    string DataType, string? ActualRadix, string? ExpectedRadix);
public sealed record LogixMemberMetadataReport(int Compared, LogixMemberMetadataFinding[] Findings, string Boundary,
    bool NativeImportAccepted = false);

public static class LogixMemberMetadata
{
    public const string CheckName = "complex-udt-member-radix";
    public const string Boundary = "Static source-bound member metadata convention, separate from XSD and native acceptance. Declared UDTs and the listed built-in structures require Radix=NullType. Unknown types remain unsupported. No source rewrite, native import execution or failure-cause diagnosis.";
    private static readonly HashSet<string> Atomic = new(StringComparer.OrdinalIgnoreCase)
        { "BIT", "BOOL", "SINT", "INT", "DINT", "LINT", "REAL", "LREAL", "USINT", "UINT", "UDINT", "ULINT", "DT", "LDT", "TIME", "LTIME", "TIME32", "ITIME", "DATE", "TOD", "LTOD" };
    private static readonly HashSet<string> Structures = new(StringComparer.OrdinalIgnoreCase)
        { "STRING", "TIMER", "COUNTER", "CONTROL", "MESSAGE" };
    public static object Profile() => new { check = CheckName, complexRadix = "NullType", declaredCustomTypes = true,
        builtInStructures = Structures.Order().ToArray(), atomicTypes = Atomic.Order().ToArray(), unknownTypes = "unsupported",
        generatorPolicy = "Reject newly authored or changed datatype/radix metadata; never normalize source definitions silently.", boundary = Boundary };

    private static IEnumerable<XElement> Members(XDocument doc) => doc.Root!.Element("Controller")!.Elements("DataTypes").Elements("DataType").Elements("Members").Elements("Member");
    private static string Identity(XElement member) => ((string?)member.Parent!.Parent!.Attribute("Name") ?? "") + "/" + ((string?)member.Attribute("Name") ?? "");
    private static string MemberHash(XElement member) => ProjectService.Hash(Encoding.UTF8.GetBytes(ProjectComparison.Normalize(member, true).ToString(SaveOptions.DisableFormatting)));
    public static LogixMemberMetadataReport Inspect(XDocument document, string[]? dataTypeNames = null)
    {
        XmlProjects.Validate(document);
        var types = document.Root!.Element("Controller")!.Elements("DataTypes").Elements("DataType")
            .Select(t => (string?)t.Attribute("Name") ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var findings = new List<LogixMemberMetadataFinding>(); var compared = 0;
        var selected = dataTypeNames?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var member in Members(document).Where(m => selected is null || selected.Contains((string?)m.Parent!.Parent!.Attribute("Name") ?? "")))
        {
            compared++; var type = (string?)member.Attribute("DataType") ?? ""; var radix = (string?)member.Attribute("Radix");
            var complex = types.Contains(type) || Structures.Contains(type);
            if (!complex && Atomic.Contains(type)) continue; // Atomic display styles are outside this check.
            if (complex && radix == "NullType") continue;
            if (findings.Count == 10000) throw new OperationException("validation_limit", "Member metadata exceeds 10000 findings; no partial report.");
            findings.Add(new(XmlProjects.PathOf(member) + "/@Radix", complex ? "fail" : "unsupported",
                complex ? "Complex member " + Identity(member) + " (" + type + ") requires Radix=NullType under the native-export convention; observed " + (radix ?? "<missing>") + ". Source is unchanged; XSD validity cannot establish this convention or native acceptance."
                    : "Member " + Identity(member) + " has an absent or unknown datatype (" + type + "); complex/atomic radix compatibility is not inferred.",
                MemberHash(member), type, radix, complex ? "NullType" : null));
        }
        return new(compared, findings.ToArray(), Boundary);
    }
    // Only new members or explicitly changed datatype/radix pairs are authored here.
    // An unrelated text edit does not rewrite or newly authorize inherited metadata.
    internal static LogixMemberMetadataFinding[] AuthoredFindings(XDocument before, XDocument after)
    {
        var old = Members(before).ToDictionary(Identity, StringComparer.OrdinalIgnoreCase);
        var authored = Members(after).Where(m => !old.TryGetValue(Identity(m), out var prior) ||
            (string?)m.Attribute("DataType") != (string?)prior.Attribute("DataType") ||
            (string?)m.Attribute("Radix") != (string?)prior.Attribute("Radix"))
            .Select(m => XmlProjects.PathOf(m) + "/@Radix").ToHashSet(StringComparer.Ordinal);
        return Inspect(after).Findings.Where(f => authored.Contains(f.Location)).ToArray();
    }
    internal static void GuardAuthored(XDocument before, XDocument after)
    {
        var first = AuthoredFindings(before, after).FirstOrDefault();
        if (first is not null) throw new OperationException("udt_member_metadata", first.Location + ": " + first.Detail);
    }
}
