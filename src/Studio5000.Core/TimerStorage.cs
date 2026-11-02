using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

internal sealed record TimerStorageAddress(string Root, string Identity, string Scope, string Type, string Field,
    string[] Aliases, string? Issue = null, bool PotentialTimer = false, bool Constant = false);

// Canonical declaration/member identity within one saved snapshot, never a controller memory address.
internal sealed class TimerStorage(XDocument document)
{
    private readonly LogicContext context = new(document);
    private readonly ILookup<string, XElement> types = document.Root!.Element("Controller")!.Elements("DataTypes").Elements("DataType")
        .ToLookup(t => (string?)t.Attribute("Name") ?? "", StringComparer.OrdinalIgnoreCase);
    private static bool Same(string? a, string? b) => LogicContext.Same(a, b);
    private bool MayContainTimer(string type, HashSet<string>? seen = null)
    {
        if (Same(type, "TIMER") || Same(type, "TIMER_T")) return true;
        seen ??= new(StringComparer.OrdinalIgnoreCase);
        if (seen.Count >= 32) return true; // Unknown deep graphs may contain timer storage.
        if (!seen.Add(type)) return false;
        return types[type].SelectMany(t => t.Elements("Members").Elements("Member")).Any(m => MayContainTimer((string?)m.Attribute("DataType") ?? "", seen));
    }
    internal TimerStorageAddress? Resolve(string operand, XElement owner, HashSet<string>? active = null)
    {
        XElement? declaration; string member; string root; string scope; string[] aliases = [];
        if (owner.Name == "AddOnInstructionDefinition")
        {
            var match = Regex.Match(operand.Trim(), @"\A(?<root>[A-Za-z_][A-Za-z0-9_]*)(?<member>(?:\.[A-Za-z_0-9]+|\[[^\]]+\])*)\z", RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            var name = match.Groups["root"].Value;
            var candidates = owner.Elements("LocalTags").Elements("LocalTag").Concat(owner.Elements("Parameters").Elements("Parameter"))
                .Where(t => Same((string?)t.Attribute("Name"), name)).ToArray();
            root = "AOI-definition:" + (string?)owner.Attribute("Name") + "." + name;
            if (candidates.Length != 1) return new(root, root, "aoi-binding-unknown", "", "", [], "AOI declaration missing or ambiguous", true);
            declaration = candidates[0]; scope = declaration.Name == "LocalTag" ? "aoi-definition-local-template" : "aoi-parameter-binding";
            root = "AOI-definition:" + (string?)owner.Attribute("Name") + "." + declaration.Name.LocalName + ":" + (string?)declaration.Attribute("Name");
            member = match.Groups["member"].Value;
            if (declaration.Attribute("AliasFor") is { } alias)
            {
                active ??= new(StringComparer.OrdinalIgnoreCase);
                if (active.Count >= 32 || !active.Add(root)) return new(root, root, scope, "", "", [root], "Alias cycle or limit", true);
                var resolved = Resolve(alias.Value + member, owner, active);
                return resolved is null ? new(root, root, scope, "", "", [root], "Unsupported AOI alias", true) : resolved with { Aliases = new[] { root }.Concat(resolved.Aliases).ToArray() };
            }
        }
        else
        {
            var program = (string?)owner.Attribute("Name"); var address = context.Resolve(operand, program);
            if (address is null) return null;
            root = address.Symbol; member = address.Member; aliases = address.Aliases; scope = address.Program is null ? "controller" : "program";
            if (address.Issue is not null) return new(root, root, scope, "", "", aliases, address.Issue, aliases.Length > 0 || Regex.IsMatch(member, @"\.(EN|DN|TT|PRE|ACC)(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            declaration = context.Tag(address.Tag, address.Program);
            if (declaration is null) return new(root, root, scope, "", "", aliases, "Declaration missing");
            foreach (var identity in aliases.Append(address.Symbol))
            {
                var split = identity.LastIndexOf('.'); var name = identity[(split + 1)..];
                var parents = identity.StartsWith("Controller.", StringComparison.OrdinalIgnoreCase) ? new[] { context.Controller } :
                    context.Controller.Elements("Programs").Elements("Program").Where(p => Same((string?)p.Attribute("Name"), identity[8..split])).ToArray();
                var parent = parents.Length == 1 ? parents[0] : null;
                if (parent?.Elements("Tags").Elements("Tag").Count(t => Same((string?)t.Attribute("Name"), name)) != 1)
                    return new(root, root, scope, "", "", aliases, "Ambiguous alias/base declaration", true);
            }
            var declaredProgram = declaration.Ancestors("Program").FirstOrDefault();
            root = (declaredProgram is null ? "Controller" : "Program:" + (string?)declaredProgram.Attribute("Name")) + "." + (string?)declaration.Attribute("Name");
        }
        var type = (string?)declaration.Attribute("DataType") ?? ""; var path = ""; var potential = MayContainTimer(type);
        var constant = Same((string?)declaration.Attribute("Constant"), "true");
        TimerStorageAddress Result(string? issue = null, string field = "") => new(root, root + path, scope, type, field, aliases, issue, potential, constant);
        int[] dimensions;
        try { dimensions = LogixStructures.Dimensions((string?)declaration.Attribute("Dimensions") ?? (string?)declaration.Attribute("Dimension")); }
        catch (OperationException) { return Result("Unsupported array dimensions"); }
        var position = 0;
        while (true)
        {
            if (Same(type, "TIMER") && dimensions.Length == 0)
            {
                if (position == member.Length) return Result();
                var field = member[position..]; var match = Regex.Match(field, @"\A\.(EN|DN|TT|PRE|ACC)(?:\.(\d+))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success) return Result("Unknown TIMER member", field);
                var canonical = match.Groups[1].Value.ToUpperInvariant();
                if (match.Groups[2].Success)
                {
                    if (canonical is not ("PRE" or "ACC") || !int.TryParse(match.Groups[2].Value, out var bit) || bit >= 32) return Result("Unsupported TIMER member bit", field);
                    canonical += "." + bit.ToString(CultureInfo.InvariantCulture);
                }
                return Result(field: "." + canonical);
            }
            if (Same(type, "TIMER_T")) return Result("TIMER_T is outside the supported TIMER profile");
            if (position == member.Length) return Result(dimensions.Length > 0 && potential ? "A fixed TIMER array element is required" : null);
            if (member[position] == '[')
            {
                var end = member.IndexOf(']', position); if (end < 0) return Result("Unclosed array index");
                var values = member[(position + 1)..end].Split(',');
                if (values.Length != dimensions.Length || dimensions.Length == 0) return Result("Array rank mismatch");
                var indices = new int[values.Length];
                for (var i = 0; i < indices.Length; i++)
                    if (!int.TryParse(values[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out indices[i]) || indices[i] >= dimensions[i]) return Result("Dynamic or out-of-range array index");
                path += "[" + string.Join(",", indices) + "]"; dimensions = []; position = end + 1;
            }
            else if (member[position] == '.')
            {
                if (dimensions.Length != 0) return Result("A fixed array element is required before a member");
                var start = ++position; while (position < member.Length && (char.IsAsciiLetterOrDigit(member[position]) || member[position] == '_')) position++;
                var name = member[start..position]; var declarations = types[type].ToArray();
                if (declarations.Length != 1) return Result("Member type metadata is unavailable or ambiguous");
                var members = declarations[0].Elements("Members").Elements("Member").Where(m => Same((string?)m.Attribute("Name"), name)).ToArray();
                if (members.Length != 1 || Same((string?)members[0].Attribute("Hidden"), "true")) return Result("Member missing, hidden or ambiguous");
                path += "." + (string?)members[0].Attribute("Name"); type = (string?)members[0].Attribute("DataType") ?? "";
                try { dimensions = LogixStructures.Dimensions((string?)members[0].Attribute("Dimension")); }
                catch (OperationException) { return Result("Member array dimensions unsupported"); }
            }
            else return Result("Unsupported member address");
        }
    }
}
