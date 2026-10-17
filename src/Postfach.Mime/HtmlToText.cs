using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Postfach.Mime;

/// <summary>Result of converting HTML to clean text.</summary>
public sealed record HtmlTextResult(string Text, int HiddenTextRemoved);

/// <summary>
/// Converts untrusted HTML mail to plain text. Scripts, styles, images and tracking pixels are dropped,
/// hidden text (display:none, zero size, white-on-white, ...) is removed and counted, links are kept as
/// Markdown <c>[text](url)</c>, blockquotes become <c>&gt;</c> quotes.
/// </summary>
public static partial class HtmlToText
{
    private static readonly HashSet<string> DroppedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "head", "title", "meta", "link", "noscript", "template", "svg", "img", "picture",
        "video", "audio", "iframe", "object", "embed", "canvas", "form", "input", "button", "select", "textarea",
    };

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "main", "aside", "nav", "table", "tr", "ul", "ol",
        "h1", "h2", "h3", "h4", "h5", "h6", "pre", "hr", "center", "address", "dl", "dt", "dd", "figure", "figcaption",
        "tbody", "thead", "tfoot", "body", "html",
    };

    public static HtmlTextResult Convert(string html)
    {
        var parser = new HtmlParser();
        var document = parser.ParseDocument(html ?? string.Empty);
        var root = (INode?)document.Body ?? document.DocumentElement;
        var state = new State();
        if (root is not null)
        {
            Walk(root, state, quoteDepth: 0);
        }

        return new HtmlTextResult(Normalize(state.Builder.ToString()), state.Hidden);
    }

    private sealed class State
    {
        public StringBuilder Builder { get; } = new();
        public int Hidden { get; set; }
        public int ListDepth { get; set; }
    }

    private static void Walk(INode node, State state, int quoteDepth)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText text:
                    AppendText(state, text.Data, quoteDepth);
                    break;
                case IElement element:
                    VisitElement(element, state, quoteDepth);
                    break;
            }
        }
    }

    private static void VisitElement(IElement element, State state, int quoteDepth)
    {
        var name = element.LocalName;
        if (DroppedElements.Contains(name))
        {
            return;
        }

        if (IsHidden(element))
        {
            if (!string.IsNullOrWhiteSpace(element.TextContent))
            {
                state.Hidden++;
            }

            return;
        }

        switch (name)
        {
            case "br":
                NewLine(state, quoteDepth);
                return;
            case "a":
            {
                var href = element.GetAttribute("href")?.Trim();
                var inner = new State { ListDepth = state.ListDepth };
                Walk(element, inner, 0);
                state.Hidden += inner.Hidden;
                var label = CollapseSpaces(inner.Builder.ToString()).Trim();
                if (href is not null && IsSafeLink(href) && !string.Equals(label, href, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals("mailto:" + label, href, StringComparison.OrdinalIgnoreCase))
                {
                    AppendText(state, label.Length == 0 ? href : $"[{label}]({href})", quoteDepth);
                }
                else
                {
                    AppendText(state, label.Length == 0 ? href ?? string.Empty : label, quoteDepth);
                }

                return;
            }
            case "blockquote":
                BlockBreak(state, quoteDepth);
                Walk(element, state, quoteDepth + 1);
                BlockBreak(state, quoteDepth);
                return;
            case "li":
                NewLine(state, quoteDepth);
                state.Builder.Append(new string(' ', Math.Max(0, state.ListDepth - 1) * 2)).Append("- ");
                Walk(element, state, quoteDepth);
                return;
            case "ul":
            case "ol":
                state.ListDepth++;
                BlockBreak(state, quoteDepth);
                Walk(element, state, quoteDepth);
                state.ListDepth--;
                BlockBreak(state, quoteDepth);
                return;
            case "td":
            case "th":
                Walk(element, state, quoteDepth);
                state.Builder.Append(' ');
                return;
            case "hr":
                BlockBreak(state, quoteDepth);
                state.Builder.Append("---");
                BlockBreak(state, quoteDepth);
                return;
        }

        if (BlockElements.Contains(name))
        {
            var paragraph = name is "p" or "table" or "pre" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
            BlockBreak(state, quoteDepth, paragraph);
            Walk(element, state, quoteDepth);
            BlockBreak(state, quoteDepth, paragraph);
            return;
        }

        Walk(element, state, quoteDepth);
    }

    private static bool IsSafeLink(string href) =>
        href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Detects the common ways mail hides text from humans but not from machines.</summary>
    internal static bool IsHidden(IElement element)
    {
        if (element.HasAttribute("hidden"))
        {
            return true;
        }

        var style = element.GetAttribute("style");
        if (string.IsNullOrEmpty(style))
        {
            return false;
        }

        var declarations = ParseStyle(style);
        string? Get(string key) => declarations.TryGetValue(key, out var v) ? v : null;

        if (Get("display") == "none" || Get("visibility") is "hidden" or "collapse")
        {
            return true;
        }

        if (Get("opacity") is { } opacity && double.TryParse(opacity, System.Globalization.CultureInfo.InvariantCulture, out var o) && o <= 0.05)
        {
            return true;
        }

        if (IsZero(Get("font-size")) || IsZero(Get("line-height")) && IsZero(Get("max-height")))
        {
            return true;
        }

        if ((IsZero(Get("max-height")) || IsZero(Get("height"))) && Get("overflow") == "hidden")
        {
            return true;
        }

        if (IsZero(Get("width")) && IsZero(Get("height")))
        {
            return true;
        }

        var color = Get("color");
        if (color is not null && IsWhite(color))
        {
            var background = Get("background-color") ?? Get("background");
            if (background is null || IsWhite(background))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, string> ParseStyle(string style)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = part[..colon].Trim().ToLowerInvariant();
            var value = part[(colon + 1)..].Replace("!important", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();
            result[key] = value;
        }

        return result;
    }

    private static bool IsZero(string? value) =>
        value is not null && ZeroLength().IsMatch(value);

    private static bool IsWhite(string value)
    {
        var v = value.Replace(" ", string.Empty);
        return v is "#fff" or "#ffffff" or "white" or "rgb(255,255,255)" or "#fefefe" or "rgba(255,255,255,1)"
            || v.StartsWith("rgba(255,255,255,", StringComparison.Ordinal);
    }

    private static void AppendText(State state, string text, int quoteDepth)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var collapsed = CollapseSpaces(text);
        if (collapsed.Length == 0)
        {
            return;
        }

        var sb = state.Builder;
        if (quoteDepth > 0 && (sb.Length == 0 || sb[^1] == '\n'))
        {
            sb.Append(new string('>', quoteDepth)).Append(' ');
            collapsed = collapsed.TrimStart();
        }
        else if (sb.Length > 0 && (sb[^1] == ' ' || sb[^1] == '\n') && collapsed[0] == ' ')
        {
            collapsed = collapsed.TrimStart();
        }

        sb.Append(collapsed);
    }

    private static void NewLine(State state, int quoteDepth)
    {
        TrimTrailingSpaces(state.Builder);
        state.Builder.Append('\n');
    }

    private static void BlockBreak(State state, int quoteDepth, bool paragraph = false)
    {
        var sb = state.Builder;
        TrimTrailingSpaces(sb);
        if (sb.Length == 0)
        {
            return;
        }

        if (sb[^1] != '\n')
        {
            sb.Append('\n');
        }

        if (paragraph && (sb.Length < 2 || sb[^2] != '\n'))
        {
            if (quoteDepth > 0)
            {
                sb.Append(new string('>', quoteDepth));
            }

            sb.Append('\n');
        }
    }

    private static void TrimTrailingSpaces(StringBuilder sb)
    {
        while (sb.Length > 0 && (sb[^1] == ' ' || sb[^1] == '\t'))
        {
            sb.Length--;
        }
    }

    private static string CollapseSpaces(string text) => Whitespace().Replace(text.Replace(' ', ' '), " ");

    private static string Normalize(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd());
        var result = string.Join('\n', lines);
        result = ManyNewlines().Replace(result, "\n\n");
        return result.Trim();
    }

    [GeneratedRegex(@"[ \t\r\n\f]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines();

    [GeneratedRegex(@"^0+(\.0+)?(px|pt|em|rem|%)?$")]
    private static partial Regex ZeroLength();
}
