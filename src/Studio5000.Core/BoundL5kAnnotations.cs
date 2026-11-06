using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

// Cross-format annotation preservation only. GM independently owns the utility's
// BaseLogic routing, version threshold, native workbook and input acceptance.
internal static partial class BoundL5kAnnotations
{
    private const int MaximumEvidenceItems = 100_000;

    [GeneratedRegex(@"<\s*(?:Alarm|Prompt|SL)\s*\[", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Message();

    [GeneratedRegex(@"<\s*@?ver\s+[^<>\r\n]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionMarker();

    internal static BoundL5kIssue[] Check(XDocument selected, byte[] l5k)
    {
        var issues = new List<BoundL5kIssue>();
        try
        {
            var offset = l5k.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            var text = new UTF8Encoding(false, true).GetString(l5k.AsSpan(offset));
            if (text.Contains('\0') || !HasCrlfLines(text))
                issues.Add(new("bound_l5k_line_endings", "The retained L5K must have UTF-8 text, CRLF physical lines and a final CRLF. No line endings were rewritten."));

            var xmlText = selected.Descendants().Where(e => !e.HasElements && !e.IsEmpty).Select(e => e.Value)
                .Concat(selected.DescendantNodes().OfType<XComment>().Select(c => c.Value));
            var expectedMessages = new Inventory();
            var expectedVersions = new Inventory();
            foreach (var value in xmlText)
            {
                if (Message().IsMatch(value)) expectedMessages.Add(value.Replace("\r\n", "\n", StringComparison.Ordinal));
                AddMarkers(expectedVersions, value);
            }
            var actualMessages = new Inventory();
            var actualVersions = new Inventory();
            AddMarkers(actualVersions, text);
            foreach (var (value, nonCrlfBreak, unsupportedEscape) in QuotedValues(text))
            {
                if (!Message().IsMatch(value)) continue;
                actualMessages.Add(value);
                if (nonCrlfBreak || unsupportedEscape)
                    if (!issues.Any(i => i.Code == "bound_l5k_message_escape"))
                        issues.Add(new("bound_l5k_message_escape", "A quoted message annotation uses a line break other than $N, or an unsupported escape. The original L5K bytes are retained."));
            }
            if (!expectedMessages.Matches(actualMessages))
                issues.Add(new("bound_l5k_message_difference", $"Exact message annotation blocks differ between selected L5X and native L5K ({expectedMessages.Count} vs {actualMessages.Count}). Split or differently encoded blocks require separate review."));
            if (!expectedVersions.Matches(actualVersions))
                issues.Add(new("bound_l5k_version_markers", $"Exact version-marker text or occurrence counts differ ({expectedVersions.Count} vs {actualVersions.Count}). The originals were retained."));
        }
        catch (DecoderFallbackException)
        { issues.Add(new("bound_l5k_encoding", "The retained native L5K is not valid UTF-8.")); }
        catch (OperationException ex)
        { issues.Add(new(ex.Code, ex.Message)); }
        return issues.ToArray();
    }

    private static bool HasCrlfLines(string text)
    {
        if (!text.EndsWith("\r\n", StringComparison.Ordinal)) return false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') return false;
            if (text[i] == '\r' && (i + 1 == text.Length || text[++i] != '\n')) return false;
        }
        return true;
    }

    private static void AddMarkers(Inventory inventory, string text)
    {
        foreach (Match marker in VersionMarker().Matches(text)) inventory.Add(marker.Value);
    }

    private sealed class Inventory
    {
        private readonly Dictionary<string, int> values = new(StringComparer.Ordinal);
        public int Count { get; private set; }
        public void Add(string value)
        {
            if (++Count > MaximumEvidenceItems)
                throw new OperationException("bound_l5k_annotation_limit", "Annotation evidence exceeds 100000 values.");
            values[value] = values.GetValueOrDefault(value) + 1;
        }
        public bool Matches(Inventory other) => Count == other.Count && values.All(pair => other.values.GetValueOrDefault(pair.Key) == pair.Value);
    }

    private static IEnumerable<(string Value, bool NonCrlfBreak, bool UnsupportedEscape)> QuotedValues(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var start = i++;
            if (text.AsSpan(start).StartsWith("//", StringComparison.Ordinal))
            {
                var end = text.IndexOf('\n', i + 1);
                i = end < 0 ? text.Length : end + 1;
                continue;
            }
            if (text.AsSpan(start).StartsWith("(*", StringComparison.Ordinal) || text.AsSpan(start).StartsWith("/*", StringComparison.Ordinal))
            {
                var end = text.IndexOf(text[start] == '(' ? "*)" : "*/", i + 1, StringComparison.Ordinal);
                if (end < 0) throw LexicalError();
                i = end + 2;
                continue;
            }
            if (text[start] != '"') continue;
            var value = new StringBuilder();
            var nonCrlfBreak = false; var unsupported = false; var closed = false;
            while (i < text.Length)
            {
                var c = text[i++];
                if (c == '"') { closed = true; break; }
                if (c != '$')
                {
                    nonCrlfBreak |= c is '\r' or '\n';
                    value.Append(c);
                    continue;
                }
                if (i == text.Length) throw LexicalError();
                var escape = text[i++];
                switch (char.ToUpperInvariant(escape))
                {
                    case 'N': value.Append('\n'); break;
                    case 'L': value.Append('\n'); nonCrlfBreak = true; break;
                    case 'R': value.Append('\r'); nonCrlfBreak = true; break;
                    case 'T': value.Append('\t'); break;
                    case '$':
                    case '"': value.Append(escape); break;
                    default:
                        if (i < text.Length && byte.TryParse(text.AsSpan(i - 1, 2), NumberStyles.AllowHexSpecifier,
                            CultureInfo.InvariantCulture, out var code) && code < 128)
                        { i++; value.Append((char)code); nonCrlfBreak |= code is 10 or 13; }
                        else { unsupported = true; value.Append('$').Append(escape); }
                        break;
                }
            }
            if (!closed) throw LexicalError();
            yield return (value.ToString(), nonCrlfBreak, unsupported);
        }
    }

    private static OperationException LexicalError() =>
        new("bound_l5k_lexical", "Unterminated L5K quote or comment prevents annotation preservation review.");
}
