using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;

namespace Postfach.Mime;

public sealed record AuthVerdict(string Spf, string Dkim, string Dmarc);

/// <summary>Phishing and identity hints derived from headers.</summary>
public static partial class MessageAnalysis
{
    /// <summary>Parses SPF/DKIM/DMARC results from the topmost Authentication-Results header(s).</summary>
    public static AuthVerdict ParseAuthenticationResults(HeaderList headers)
    {
        string spf = "none", dkim = "none", dmarc = "none";
        // The topmost header was added by the receiving server; later ones may be forged by the sender.
        foreach (var header in headers.Where(h => h.Field.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase)).Take(1))
        {
            var value = header.Value;
            spf = Find(value, "spf") ?? spf;
            dkim = Find(value, "dkim") ?? dkim;
            dmarc = Find(value, "dmarc") ?? dmarc;
        }

        return new AuthVerdict(spf, dkim, dmarc);
    }

    private static string? Find(string value, string method)
    {
        var match = Regex.Match(value, $@"(?:^|[\s;]){method}\s*=\s*([a-z]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// True when the display name claims a domain (or address) that differs from the sender's actual domain,
    /// e.g. <c>"paypal.com Support" &lt;x@evil.example&gt;</c>.
    /// </summary>
    public static bool SenderMismatch(MailboxAddress? from)
    {
        if (from is null || string.IsNullOrWhiteSpace(from.Name))
        {
            return false;
        }

        var actual = BaseDomain(from.Domain);
        foreach (Match match in DomainInName().Matches(from.Name))
        {
            var claimed = BaseDomain(match.Groups["domain"].Value);
            if (!string.Equals(claimed, actual, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BaseDomain(string domain)
    {
        var labels = domain.Trim().TrimEnd('.').ToLowerInvariant().Split('.');
        if (labels.Length <= 2)
        {
            return string.Join('.', labels);
        }

        // Keep three labels for common second-level registries (co.uk, com.au, ...).
        var sld = labels[^2];
        var take = sld is "co" or "com" or "org" or "net" or "ac" or "gov" && labels[^1].Length == 2 ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }

    /// <summary>
    /// Stable thread key from the conversation's root Message-ID (first References entry, else In-Reply-To, else itself).
    /// </summary>
    public static string ThreadKey(string? messageId, IEnumerable<string>? references, string? inReplyTo)
    {
        var root = references?.FirstOrDefault() ?? inReplyTo ?? messageId;
        if (string.IsNullOrEmpty(root))
        {
            return "t_" + Guid.NewGuid().ToString("N")[..12];
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(root.Trim('<', '>').ToLowerInvariant()));
        return "t_" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    [GeneratedRegex(@"(?<![\w.-])(?:[\w.+-]+@)?(?<domain>(?:[a-z0-9-]+\.)+[a-z]{2,})(?![\w-])", RegexOptions.IgnoreCase)]
    private static partial Regex DomainInName();
}
