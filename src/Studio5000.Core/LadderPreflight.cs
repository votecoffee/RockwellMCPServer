namespace Studio5000.Core;

public sealed record LadderPreflightFinding(string Check, string Status, string Detail, string Path);
public sealed record LadderPreflightReport(string NativeProfile, int MaximumAllowedBranchDepth, int? MaximumBranchDepth,
    LadderPreflightFinding[] Findings);

/// <summary>Read-only syntactic preflight, separate from native compilation and scan behavior.</summary>
public static class LadderPreflight
{
    public const string DepthCheck = "rll-native-branch-depth";
    public const string DuplicateCheck = "rll-duplicate-series-predicates";
    public const string NativeProfile = "logix5000-pm008j";
    public const int MaximumBranchDepth = 6;
    public const string ManualUrl = "https://literature.rockwellautomation.com/idc/groups/literature/documents/pm/1756-pm008_-en-p.pdf";
    public static object Profile() => new { id = NativeProfile, maximumNestedBranches = MaximumBranchDepth,
        publication = "1756-PM008J-EN-P, July 2022, page 12", source = ManualUrl,
        counting = "Simultaneous control branches outside instruction parentheses; excludes operand array brackets. Parallel legs do not increase nesting.",
        boundary = "Documented structural limit only. No native compiler, scan, motion or runtime acceptance." };

    public static int ValidateNativeDepth(string text)
    {
        var depth = LadderBranchGrammar.MeasureDepth(text);
        if (depth > MaximumBranchDepth) throw new OperationException("ladder_native_branch_depth", DepthDetail(depth));
        return depth;
    }
    private static string DepthDetail(int depth) => $"Nested control branch depth {depth} exceeds {MaximumBranchDepth} for {NativeProfile} (1756-PM008J-EN-P, page 12).";

    internal static bool Predicate(LadderPreview.Part part) => part.Legs is null && (part.Instruction.ToUpperInvariant(), part.Operands.Length) is
        ("XIC" or "XIO", 1) or ("EQU" or "NEQ" or "GRT" or "GEQ" or "LES" or "LEQ", 2) or ("LIM" or "MEQ", 3);

    public static LadderPreflightReport Inspect(string text)
    {
        var findings = new List<LadderPreflightFinding>(); int? depth = null;
        try
        {
            depth = LadderBranchGrammar.MeasureDepth(text);
            if (depth > MaximumBranchDepth) findings.Add(new(DepthCheck, "fail", DepthDetail(depth.Value), "rung"));
        }
        catch (OperationException ex)
        {
            findings.Add(new(DepthCheck, "unsupported", "Depth unavailable: " + ex.Message, "rung"));
        }
        void Walk(List<LadderPreview.Part> sequence, string path)
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < sequence.Count; i++)
            {
                var part = sequence[i]; var here = path + "/part[" + (i + 1) + "]";
                if (part.Legs is { } legs)
                {
                    seen.Clear();
                    for (var leg = 0; leg < legs.Count; leg++) Walk(legs[leg], here + "/leg[" + (leg + 1) + "]");
                }
                else if (Predicate(part))
                {
                    // Exact operand spelling preserves string literals; do not infer aliases or equivalence.
                    var key = part.Instruction.ToUpperInvariant() + "(" + string.Join(",", part.Operands) + ")";
                    if (seen.TryGetValue(key, out var earlier)) findings.Add(new(DuplicateCheck, "warning",
                        $"Identical predicate {key} repeats on an uninterrupted series path after {earlier}. Review only; no simplification or scan equivalence is asserted.", here));
                    else seen.Add(key, here);
                }
                else seen.Clear(); // Outputs, calls, stateful and unknown instructions interrupt the path.
            }
        }
        try { Walk(LadderPreview.ParseLogic(text), "rung"); }
        catch (OperationException ex) { findings.Add(new(DuplicateCheck, "unsupported", "Predicate scan unavailable: " + ex.Message, "rung")); }
        return new(NativeProfile, MaximumBranchDepth, depth, findings.ToArray());
    }
}
