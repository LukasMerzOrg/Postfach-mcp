using System.Text;
using System.Text.RegularExpressions;

namespace Postfach.Mime;

/// <summary>Quote collapsing, signature and history trimming for plain text bodies.</summary>
public static partial class TextTrimmer
{
    /// <summary>Keeps the first <paramref name="keep"/> lines of each quoted block and collapses the rest.</summary>
    public static string CollapseQuotes(string text, int keep = 3)
    {
        var lines = SplitLines(text);
        var sb = new StringBuilder(text.Length);
        var run = 0;
        var collapsed = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith('>'))
            {
                run++;
                if (run > keep)
                {
                    collapsed++;
                    continue;
                }
            }
            else
            {
                FlushCollapsed(sb, ref collapsed);
                run = 0;
            }

            sb.Append(line).Append('\n');
        }

        FlushCollapsed(sb, ref collapsed);
        return sb.ToString().TrimEnd();
    }

    private static void FlushCollapsed(StringBuilder sb, ref int collapsed)
    {
        if (collapsed > 0)
        {
            sb.Append($"> [… {collapsed} quoted lines collapsed]\n");
            collapsed = 0;
        }
    }

    /// <summary>
    /// Removes the signature (RFC 3676 "-- " delimiter) and quoted history ("On … wrote:", Outlook headers,
    /// "&gt;" blocks) so that a thread view only shows what each message added.
    /// </summary>
    public static string TrimHistoryAndSignature(string text)
    {
        var lines = SplitLines(text);
        var kept = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            if (line == "-- " || line == "--" || IsHistoryMarker(line))
            {
                break;
            }

            if (line.StartsWith('>'))
            {
                continue;
            }

            kept.Add(line);
        }

        // Drop trailing "On ... wrote:" lines that were split across two lines.
        while (kept.Count > 0 && (string.IsNullOrWhiteSpace(kept[^1]) || WroteLine().IsMatch(kept[^1])))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return string.Join('\n', kept).Trim();
    }

    private static bool IsHistoryMarker(string line)
    {
        var l = line.Trim();
        return WroteLine().IsMatch(l)
            || l.StartsWith("-----Original Message-----", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("-----Ursprüngliche Nachricht-----", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("---------- Forwarded message", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("---------- Weitergeleitete Nachricht", StringComparison.OrdinalIgnoreCase)
            || OutlookHeader().IsMatch(l);
    }

    public static string Truncate(string text, int maxChars, out bool truncated)
    {
        truncated = text.Length > maxChars;
        if (!truncated)
        {
            return text;
        }

        var cut = text[..maxChars];
        var space = cut.LastIndexOfAny([' ', '\n']);
        if (space > maxChars * 0.8)
        {
            cut = cut[..space];
        }

        return cut.TrimEnd() + " …";
    }

    /// <summary>Single-line preview of a body: whitespace collapsed, quotes removed, at most <paramref name="max"/> chars.</summary>
    public static string Snippet(string? text, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var withoutQuotes = string.Join(' ', SplitLines(text).Where(l => !l.StartsWith('>')));
        var collapsed = Whitespace().Replace(withoutQuotes, " ").Trim();
        if (collapsed.Length <= max)
        {
            return collapsed;
        }

        return collapsed[..(max - 1)].TrimEnd() + "…";
    }

    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

    [GeneratedRegex(@"^(On|Am|Le|El|Il)\b.{0,200}\b(wrote|schrieb|a écrit|escribió|ha scritto)( .{0,40})?:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex WroteLine();

    [GeneratedRegex(@"^(From|Von|De):\s.+(Sent|Gesendet|Envoyé|Date|Datum):", RegexOptions.IgnoreCase)]
    private static partial Regex OutlookHeader();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
