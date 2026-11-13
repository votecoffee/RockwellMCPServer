namespace Studio5000.Core;

/// <summary>Bounded terminal instruction classification, separate from native compilation.</summary>
public static class LadderOutputCoverage
{
    public const string CheckName = "rll-output-coverage";
    private static readonly HashSet<string> Inputs = new(StringComparer.OrdinalIgnoreCase)
    {
        "XIC", "XIO", "EQU", "NEQ", "GRT", "GEQ", "LES", "LEQ", "LIM", "MEQ", "CMP", "AFI", "ONS"
    };
    private static readonly HashSet<string> Outputs = new(StringComparer.OrdinalIgnoreCase)
    {
        "OTE", "OTL", "OTU", "MOV", "COP", "CPS", "FLL", "CLR", "CPT",
        "ADD", "SUB", "MUL", "DIV", "MOD", "AND", "OR", "XOR", "NOT",
        "TON", "TOF", "RTO", "CTU", "CTD", "RES", "NOP", "JSR"
    };

    public static object Profile() => new
    {
        check = CheckName,
        inputInstructions = Inputs.Order(StringComparer.Ordinal).ToArray(),
        outputInstructions = Outputs.Order(StringComparer.Ordinal).ToArray(),
        rule = "Each path reaching the right rail must end with an output. A shared downstream output covers all preceding branch legs; an output before a terminal input does not.",
        publication = "1756-PM008J-EN-P, July 2022, pages 14-15",
        source = LadderPreflight.ManualUrl,
        unknownInstructions = "Terminal instructions outside the listed built-ins, including AOIs, report unsupported.",
        boundary = "Static terminal placement only. Instruction operands, types, full syntax, AOI output classification and native compilation require separate validation."
    };

    public static LadderPreflightFinding[] Inspect(string text)
    {
        try { return Inspect(LadderPreview.ParseLogic(text)); }
        catch (OperationException ex) { return [Unavailable(ex.Message)]; }
    }

    internal static LadderPreflightFinding Unavailable(string message) =>
        new(CheckName, "unsupported", "Output coverage unavailable: " + message, "rung");

    internal static LadderPreflightFinding[] Inspect(List<LadderPreview.Part> sequence)
    {
        var findings = new List<LadderPreflightFinding>();
        void Terminal(List<LadderPreview.Part> parts, string path)
        {
            if (parts.Count == 0)
            {
                findings.Add(new(CheckName, "fail", "Missing output instruction: an empty terminal path reaches the right rail.", path));
                return;
            }
            var last = parts[^1];
            var location = path + "/part[" + parts.Count + "]";
            if (last.Legs is { } legs)
            {
                for (var leg = 0; leg < legs.Count; leg++) Terminal(legs[leg], location + "/leg[" + (leg + 1) + "]");
                return;
            }
            if (Inputs.Contains(last.Instruction))
                findings.Add(new(CheckName, "fail",
                    $"Missing output instruction: terminal input {last.Instruction} has no downstream output. Outputs before this terminal input do not satisfy the right-rail requirement.",
                    location));
            else if (!Outputs.Contains(last.Instruction))
                findings.Add(new(CheckName, "unsupported",
                    $"Terminal instruction {last.Instruction} has no declared input/output classification in this check. Verify its placement in Studio.",
                    location));
        }
        Terminal(sequence, "rung");
        return findings.ToArray();
    }
}
