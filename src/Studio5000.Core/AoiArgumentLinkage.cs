using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

/// <summary>Bounded saved RLL AOI operand linkage, not a complete instruction compiler.</summary>
public static class AoiArgumentLinkage
{
    public const string CheckName = "direct-rll-aoi-argument-linkage-v1";
    internal sealed record Finding(string Location, string Status, string Detail, string Hash);
    private sealed record Symbol(string? Type, int[] Dimensions, bool Writable, string? Issue = null, bool Unsupported = false);
    internal static (int Compared, Dictionary<string, Finding> Findings) Scan(XDocument doc)
    {
        var context = new LogicContext(doc); var findings = new Dictionary<string, Finding>(StringComparer.Ordinal); var compared = 0;
        var definitions = context.Controller.Elements("AddOnInstructionDefinitions").Elements("AddOnInstructionDefinition")
            .ToLookup(d => (string?)d.Attribute("Name") ?? "", StringComparer.OrdinalIgnoreCase);
        var types = context.Controller.Elements("DataTypes").Elements("DataType").ToLookup(d => (string?)d.Attribute("Name") ?? "", StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in context.Controller.Elements("Programs").Elements("Program").Prepend(context.Controller))
        foreach (var group in scope.Elements("Tags").Elements("Tag").GroupBy(t => (string?)t.Attribute("Name") ?? "", StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            ambiguous.Add((scope.Name == "Controller" ? "Controller" : "Program:" + (string?)scope.Attribute("Name")) + "." + group.Key);
        static bool True(XElement e, string name) => string.Equals((string?)e.Attribute(name), "true", StringComparison.OrdinalIgnoreCase);
        static Symbol Problem(string issue, bool unsupported = false) => new(null, [], false, issue, unsupported);
        Symbol Resolve(string operand, string? program, XElement? owner, HashSet<string>? active = null)
        {
            XElement? declaration; string member;
            if (owner is not null)
            {
                var match = Regex.Match(operand, @"\A(?<root>[A-Za-z_][A-Za-z0-9_]*)(?<member>(?:\.[A-Za-z_0-9]+|\[[^\]]+\])*)\z", RegexOptions.CultureInvariant);
                if (!match.Success) return Problem("unsupported_AOI_scope_address", true);
                var name = match.Groups["root"].Value;
                var declarations = owner.Elements("Parameters").Elements("Parameter").Concat(owner.Elements("LocalTags").Elements("LocalTag"))
                    .Where(d => LogicContext.Same((string?)d.Attribute("Name"), name)).ToArray();
                if (declarations.Length == 0) return Problem("caller_declaration_missing");
                if (declarations.Length != 1) return Problem("ambiguous_caller_declaration", true);
                declaration = declarations[0]; member = match.Groups["member"].Value;
                if (declaration.Attribute("AliasFor") is { } alias)
                {
                    active ??= new(StringComparer.OrdinalIgnoreCase);
                    if (active.Count >= 32 || !active.Add(name)) return Problem("alias_cycle_or_limit");
                    return Resolve(alias.Value + member, program, owner, active);
                }
            }
            else
            {
                var address = context.Resolve(operand, program);
                if (address is null) return Problem("expression_or_address_unsupported", true);
                if (address.Issue is not null) return Problem(address.Issue, address.Issue == "alias_target_unsupported");
                if (address.Aliases.Append(address.Symbol).Any(ambiguous.Contains)) return Problem("ambiguous_caller_declaration", true);
                declaration = context.Tag(address.Tag, address.Program); member = address.Member;
            }
            if (declaration is null) return Problem("caller_declaration_missing");
            var type = (string?)declaration.Attribute("DataType");
            if (string.IsNullOrEmpty(type)) return Problem("type_metadata_missing", true);
            int[] dims;
            try { dims = LogixStructures.Dimensions((string?)declaration.Attribute("Dimensions") ?? (string?)declaration.Attribute("Dimension")); }
            catch (OperationException) { return Problem("array_metadata_unsupported", true); }
            var position = 0;
            while (position < member.Length)
            {
                if (member[position] == '[')
                {
                    var end = member.IndexOf(']', position); if (end < 0) return Problem("index_syntax_unsupported", true);
                    var indices = member[(position + 1)..end].Split(',');
                    if (indices.Length != dims.Length || dims.Length == 0) return Problem("array_rank_mismatch");
                    for (var i = 0; i < indices.Length; i++)
                    {
                        if (!int.TryParse(indices[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return Problem("dynamic_index_unsupported", true);
                        if (index >= dims[i]) return Problem("array_index_out_of_range");
                    }
                    dims = []; position = end + 1;
                }
                else if (member[position] == '.')
                {
                    var start = ++position; while (position < member.Length && (char.IsAsciiLetterOrDigit(member[position]) || member[position] == '_')) position++;
                    var name = member[start..position]; if (dims.Length != 0) return Problem("array_element_required");
                    var width = type.ToUpperInvariant() switch { "SINT" => 8, "INT" => 16, "DINT" => 32, "LINT" => 64, _ => 0 };
                    if (width > 0 && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var bit))
                    { if (bit >= width) return Problem("bit_index_out_of_range"); type = "BOOL"; continue; }
                    var declarations = types[type].ToArray();
                    if (declarations.Length != 1) return Problem("member_type_metadata_unavailable_or_ambiguous", true);
                    var members = declarations[0].Elements("Members").Elements("Member").Where(m => LogicContext.Same((string?)m.Attribute("Name"), name)).ToArray();
                    if (members.Length == 0) return Problem("declared_member_missing");
                    if (members.Length != 1 || True(members[0], "Hidden")) return Problem("member_metadata_ambiguous_or_hidden", true);
                    type = (string?)members[0].Attribute("DataType"); if (string.IsNullOrEmpty(type)) return Problem("member_type_metadata_missing", true);
                    if (type.Equals("BIT", StringComparison.OrdinalIgnoreCase)) type = "BOOL";
                    try { dims = LogixStructures.Dimensions((string?)members[0].Attribute("Dimension")); }
                    catch (OperationException) { return Problem("member_array_metadata_unsupported", true); }
                }
                else return Problem("member_syntax_unsupported", true);
            }
            return new(type, dims, !True(declaration, "Constant"));
        }
        static IEnumerable<LadderPreview.Part> Flatten(IEnumerable<LadderPreview.Part> parts) => parts.SelectMany(p => p.Legs is { } legs ? Flatten(legs.SelectMany(l => l)) : [p]);
        foreach (var routine in doc.Descendants("Routine").Where(r => (string?)r.Attribute("Type") == "RLL" && r.Parent?.Name == "Routines" && r.Parent.Parent?.Name.LocalName is "Program" or "AddOnInstructionDefinition"))
        {
            var owner = routine.Parent!.Parent!; var aoiScope = owner.Name == "AddOnInstructionDefinition" ? owner : null;
            var program = aoiScope is null ? (string?)owner.Attribute("Name") : null;
            foreach (var rung in routine.Elements("RLLContent").Elements("Rung"))
            {
                var text = (string?)rung.Element("Text") ?? ""; var path = XmlProjects.PathOf(rung); var hash = LogixPreservation.TextHash(text)!;
                void Add(string suffix, string status, string detail)
                {
                    if (findings.Count >= 10000) throw new OperationException("validation_limit", "More than 10000 AOI linkage findings; no partial report was produced.");
                    var location = path + "/" + suffix;
                    findings.Add(location + "/" + detail, new(location, status, detail, hash));
                }
                LadderPreview.Part[] parts;
                try { parts = Flatten(LadderPreview.ParseLogic(text)).ToArray(); }
                catch (OperationException ex) { Add("AOI-scan", "unsupported", "AOI call scan unavailable: " + ex.Message); continue; }
                for (var call = 0; call < parts.Length; call++)
                {
                    var part = parts[call]; var matches = definitions[part.Instruction].ToArray(); if (matches.Length == 0) continue;
                    var location = "instruction[" + (call + 1) + "]/" + part.Instruction;
                    void Check(string name, string operand, string expected, bool writable, bool inOut)
                    {
                        compared++;
                        if ((writable || inOut) && (double.TryParse(operand, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
                            operand.Equals("true", StringComparison.OrdinalIgnoreCase) || operand.Equals("false", StringComparison.OrdinalIgnoreCase) || operand.StartsWith("'", StringComparison.Ordinal) || operand.StartsWith("\"", StringComparison.Ordinal)))
                        { Add(location + "/" + name, "fail", name + ": a declared tag reference is required: " + operand); return; }
                        var symbol = Resolve(operand, program, aoiScope);
                        if (symbol.Issue is not null) { Add(location + "/" + name, symbol.Unsupported ? "unsupported" : "fail", $"{name}: {operand}: {symbol.Issue}; expected {expected}."); return; }
                        if (symbol.Dimensions.Length > 0) { Add(location + "/" + name, "unsupported", name + ": array parameter binding is outside this scalar linkage check."); return; }
                        if (writable && !symbol.Writable) Add(location + "/" + name, "fail", name + ": destination/reference is constant: " + operand);
                        else if (LogicContext.Same(symbol.Type, expected) && expected.ToUpperInvariant() is not ("BOOL" or "SINT" or "INT" or "DINT" or "LINT" or "REAL" or "LREAL" or "USINT" or "UINT" or "UDINT" or "ULINT" or "STRING") && types[expected].Count() != 1 && definitions[expected].Count() != 1)
                            Add(location + "/" + name, "unsupported", name + ": structure/instruction type metadata is missing or ambiguous: " + expected);
                        else if (!LogicContext.Same(symbol.Type, expected))
                        {
                            string[] numeric = ["SINT", "INT", "DINT", "LINT", "REAL", "LREAL", "USINT", "UINT", "UDINT", "ULINT"];
                            var conversion = !inOut && numeric.Contains(symbol.Type!, StringComparer.OrdinalIgnoreCase) && numeric.Contains(expected, StringComparer.OrdinalIgnoreCase);
                            Add(location + "/" + name, conversion ? "unsupported" : "fail", $"{name}: {operand}: declared type {symbol.Type}; expected {expected}{(conversion ? "; numeric conversion is not proven" : "")}.");
                        }
                    }
                    if (matches.Length != 1 || matches[0].Element("Parameters") is null) { Add(location, "unsupported", "AOI parameter metadata absent or ambiguous."); continue; }
                    var definition = matches[0];
                    if (part.Operands.Length == 0) { Add(location, "fail", "AOI instance operand is missing."); continue; }
                    Check("instance", part.Operands[0], (string)definition.Attribute("Name")!, true, true);
                    var parameters = definition.Elements("Parameters").Elements("Parameter").Where(p => !LogicContext.Same((string?)p.Attribute("Name"), "EnableIn") && !LogicContext.Same((string?)p.Attribute("Name"), "EnableOut")).ToArray();
                    if (parameters.Any(p => string.IsNullOrEmpty((string?)p.Attribute("Name")) || string.IsNullOrEmpty((string?)p.Attribute("DataType")) || (string?)p.Attribute("Usage") is not ("Input" or "Output" or "InOut")) ||
                        parameters.GroupBy(p => (string?)p.Attribute("Name"), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                    { Add(location, "unsupported", "AOI parameter names/types/usage are incomplete or ambiguous."); continue; }
                    var required = parameters.Where(p => True(p, "Required") || (string?)p.Attribute("Usage") == "InOut").ToArray();
                    var visible = parameters.Where(p => True(p, "Visible") || True(p, "Required") || (string?)p.Attribute("Usage") == "InOut").ToArray();
                    var layout = part.Operands.Length == visible.Length + 1 ? visible : part.Operands.Length == required.Length + 1 ? required : null;
                    if (layout is null) { Add(location, part.Operands.Length < required.Length + 1 ? "fail" : "unsupported", "Argument count does not match required-only or visible parameter layout; required arguments: " + required.Length + "."); continue; }
                    for (var i = 0; i < layout.Length; i++)
                    {
                        var parameter = layout[i]; var name = (string)parameter.Attribute("Name")!; var usage = (string)parameter.Attribute("Usage")!;
                        var expected = (string)parameter.Attribute("DataType")!; var operand = part.Operands[i + 1];
                        int[] parameterDimensions;
                        try { parameterDimensions = LogixStructures.Dimensions((string?)parameter.Attribute("Dimensions") ?? (string?)parameter.Attribute("Dimension")); }
                        catch (OperationException) { Add(location + "/" + name, "unsupported", "Parameter dimensions are unsupported."); continue; }
                        if (parameterDimensions.Length > 0 || expected.Equals("MODULE", StringComparison.OrdinalIgnoreCase))
                        { Add(location + "/" + name, "unsupported", "Array/MODULE parameter binding is outside this scalar linkage check."); continue; }
                        if (usage != "InOut" && expected.ToUpperInvariant() is not ("SINT" or "INT" or "DINT" or "REAL" or "BOOL"))
                        { Add(location + "/" + name, "unsupported", "Input/Output parameter type is outside the documented atomic profile: " + expected); continue; }
                        if (usage == "Input" && (double.TryParse(operand, NumberStyles.Float, CultureInfo.InvariantCulture, out _) || operand.Equals("true", StringComparison.OrdinalIgnoreCase) || operand.Equals("false", StringComparison.OrdinalIgnoreCase)))
                        {
                            compared++;
                            var boolean = operand.Equals("true", StringComparison.OrdinalIgnoreCase) || operand.Equals("false", StringComparison.OrdinalIgnoreCase);
                            var integer = long.TryParse(operand, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number);
                            var known = expected.ToUpperInvariant() switch {
                                "BOOL" => boolean || integer && number is 0 or 1,
                                "SINT" => integer && number is >= sbyte.MinValue and <= sbyte.MaxValue,
                                "INT" => integer && number is >= short.MinValue and <= short.MaxValue,
                                "DINT" => integer && number is >= int.MinValue and <= int.MaxValue,
                                "REAL" => !boolean && float.TryParse(operand, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) && float.IsFinite(real),
                                _ => false };
                            if (!known) Add(location + "/" + name, "unsupported", "Input literal range/type/conversion is outside the supported atomic Input profile.");
                            continue;
                        }
                        Check(name, operand, expected, usage == "Output" || usage == "InOut" && !True(parameter, "Constant"), usage == "InOut");
                    }
                }
            }
        }
        return (compared, findings);
    }
}
