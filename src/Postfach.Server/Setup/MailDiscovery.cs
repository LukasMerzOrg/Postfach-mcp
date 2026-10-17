using System.Globalization;
using System.Net.Sockets;
using System.Xml.Linq;
using DnsClient;
using Postfach.Imap;
using Postfach.Server.Configuration;

namespace Postfach.Server.Setup;

public sealed record DiscoveredSettings
{
    /// <summary>preset, autoconfig, ispdb, mx, srv or guess.</summary>
    public required string Source { get; init; }

    public string? PresetKey { get; init; }

    public string? ProviderName { get; init; }

    public required ServerEndpoint Imap { get; init; }

    public ServerEndpoint? Smtp { get; init; }

    public required string Username { get; init; }

    public bool RequiresOAuth { get; init; }

    public bool InsecureTls { get; init; }

    public string? Note { get; init; }
}

/// <summary>
/// Finds IMAP/SMTP settings for an email address the way mail clients do: built-in presets, the domain's Mozilla
/// autoconfig file, the Thunderbird ISPDB, the MX host's provider, RFC 6186 SRV records, and finally probed guesses.
/// </summary>
public sealed class MailDiscovery(HttpClient http, ILookupClient dns, ILogger<MailDiscovery> logger)
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(4);

    private static readonly Dictionary<string, string> DomainPresets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail.com"] = "gmail", ["googlemail.com"] = "gmail",
        ["outlook.com"] = "outlook", ["hotmail.com"] = "outlook", ["hotmail.ch"] = "outlook", ["hotmail.de"] = "outlook",
        ["live.com"] = "outlook", ["live.de"] = "outlook", ["msn.com"] = "outlook", ["outlook.de"] = "outlook",
        ["icloud.com"] = "icloud", ["me.com"] = "icloud", ["mac.com"] = "icloud",
        ["fastmail.com"] = "fastmail", ["fastmail.fm"] = "fastmail",
        ["yahoo.com"] = "yahoo", ["yahoo.de"] = "yahoo", ["yahoo.fr"] = "yahoo", ["ymail.com"] = "yahoo",
        ["aol.com"] = "aol",
        ["gmx.net"] = "gmx", ["gmx.de"] = "gmx", ["gmx.ch"] = "gmx", ["gmx.at"] = "gmx", ["gmx.com"] = "gmx",
        ["web.de"] = "webde",
        ["mailbox.org"] = "mailboxorg",
        ["posteo.de"] = "posteo", ["posteo.net"] = "posteo",
        ["ik.me"] = "infomaniak", ["ikmail.com"] = "infomaniak",
        ["bluewin.ch"] = "bluewin",
        ["proton.me"] = "protonbridge", ["protonmail.com"] = "protonbridge", ["protonmail.ch"] = "protonbridge", ["pm.me"] = "protonbridge",
    };

    // MX host suffix -> preset, for custom domains hosted at a big provider.
    private static readonly (string Suffix, string Preset)[] MxPresets =
    [
        (".google.com", "gmail"), (".googlemail.com", "gmail"),
        (".mail.protection.outlook.com", "m365"),
        (".mail.icloud.com", "icloud"),
        (".messagingengine.com", "fastmail"),
        (".infomaniak.ch", "infomaniak"),
        (".mailbox.org", "mailboxorg"),
        (".protonmail.ch", "protonbridge"),
        (".zoho.eu", "zoho"),
    ];

    public async Task<DiscoveredSettings> DiscoverAsync(string email, CancellationToken ct)
    {
        var at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1)
        {
            throw new ArgumentException("Enter a full email address.");
        }

        var domain = new IdnMapping().GetAscii(email[(at + 1)..].Trim().TrimEnd('.')).ToLowerInvariant();

        if (DomainPresets.TryGetValue(domain, out var direct))
        {
            return FromPreset(direct, email, "preset");
        }

        foreach (var url in new[]
                 {
                     $"https://autoconfig.{domain}/mail/config-v1.1.xml?emailaddress={Uri.EscapeDataString(email)}",
                     $"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml?emailaddress={Uri.EscapeDataString(email)}",
                 })
        {
            if (await FetchConfigAsync(url, email, "autoconfig", ct) is { } found)
            {
                return found;
            }
        }

        if (await FetchConfigAsync($"https://autoconfig.thunderbird.net/v1.1/{domain}", email, "ispdb", ct) is { } ispdb)
        {
            return ispdb;
        }

        var mx = await MxHostAsync(domain, ct);
        if (mx is not null)
        {
            foreach (var (suffix, preset) in MxPresets)
            {
                if (("." + mx).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return FromPreset(preset, email, "mx");
                }
            }

            var mxDomain = RegistrableDomain(mx);
            if (!string.Equals(mxDomain, domain, StringComparison.OrdinalIgnoreCase)
                && await FetchConfigAsync($"https://autoconfig.thunderbird.net/v1.1/{mxDomain}", email, "mx", ct) is { } viaMx)
            {
                return viaMx;
            }
        }

        if (await SrvAsync(domain, email, ct) is { } srv)
        {
            return srv;
        }

        return await GuessAsync(domain, email, mx, ct);
    }

    public static DiscoveredSettings FromPreset(string key, string email, string source)
    {
        var p = AccountConfigLoader.Presets[key];
        return new DiscoveredSettings
        {
            Source = source,
            PresetKey = key,
            ProviderName = p.Name,
            Imap = new ServerEndpoint(p.ImapHost, p.ImapPort, p.ImapTls),
            Smtp = new ServerEndpoint(p.SmtpHost, p.SmtpPort, p.SmtpTls),
            Username = email,
            RequiresOAuth = p.RequiresOAuth,
            InsecureTls = p.InsecureTls,
            Note = p.Note,
        };
    }

    private async Task<DiscoveredSettings?> FetchConfigAsync(string url, string email, string source, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(StepTimeout);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.Scheme != "https")
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new MemoryStream();
            await CopyLimitedAsync(stream, buffer, 256 * 1024, timeout.Token);
            buffer.Position = 0;
            return ParseMozillaConfig(XDocument.Load(buffer), email, source);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException or InvalidDataException)
        {
            logger.LogDebug("Autoconfig {Url} not usable: {Error}", url, ex.GetType().Name);
            return null;
        }
    }

    private static async Task CopyLimitedAsync(Stream source, Stream target, int limit, CancellationToken ct)
    {
        var buffer = new byte[8192];
        int read, total = 0;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new InvalidDataException("Autoconfig document too large.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    /// <summary>Parses a Thunderbird autoconfig document (config-v1.1.xml); prefers TLS over STARTTLS, never plain.</summary>
    public static DiscoveredSettings? ParseMozillaConfig(XDocument document, string email, string source)
    {
        var provider = document.Root?.Element("emailProvider");
        if (provider is null)
        {
            return null;
        }

        static int Rank(XElement server) => (string?)server.Element("socketType") switch
        {
            "SSL" => 0,
            "STARTTLS" => 1,
            _ => 9,
        };

        var imapServer = provider.Elements("incomingServer").Where(s => (string?)s.Attribute("type") == "imap" && Rank(s) < 9).MinBy(Rank);
        if (imapServer is null)
        {
            return null;
        }

        var smtpServer = provider.Elements("outgoingServer").Where(s => (string?)s.Attribute("type") == "smtp" && Rank(s) < 9).MinBy(Rank);
        var auth = imapServer.Elements("authentication").Select(a => a.Value.Trim()).ToList();
        var oauthOnly = auth.Contains("OAuth2") && !auth.Any(a => a.StartsWith("password", StringComparison.Ordinal));
        var displayName = (string?)provider.Element("displayName");

        return new DiscoveredSettings
        {
            Source = source,
            ProviderName = displayName,
            Imap = Endpoint(imapServer, email),
            Smtp = smtpServer is null ? null : Endpoint(smtpServer, email),
            Username = Expand((string?)imapServer.Element("username") ?? "%EMAILADDRESS%", email),
            RequiresOAuth = oauthOnly,
            Note = oauthOnly ? "This provider only accepts OAuth sign-in." : null,
        };
    }

    private static ServerEndpoint Endpoint(XElement server, string email) => new(
        Expand(((string?)server.Element("hostname") ?? string.Empty).Trim(), email),
        int.TryParse((string?)server.Element("port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : 993,
        (string?)server.Element("socketType") == "SSL" ? TlsMode.Implicit : TlsMode.StartTls);

    private static string Expand(string template, string email)
    {
        var at = email.LastIndexOf('@');
        return template
            .Replace("%EMAILADDRESS%", email, StringComparison.Ordinal)
            .Replace("%EMAILLOCALPART%", email[..at], StringComparison.Ordinal)
            .Replace("%EMAILDOMAIN%", email[(at + 1)..], StringComparison.Ordinal);
    }

    private async Task<string?> MxHostAsync(string domain, CancellationToken ct)
    {
        try
        {
            var result = await dns.QueryAsync(domain, QueryType.MX, cancellationToken: ct);
            return result.Answers.MxRecords().OrderBy(r => r.Preference).FirstOrDefault()?.Exchange.Value.TrimEnd('.');
        }
        catch (Exception ex) when (ex is DnsResponseException or SocketException or OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<DiscoveredSettings?> SrvAsync(string domain, string email, CancellationToken ct)
    {
        async Task<(string Host, int Port)?> Lookup(string name)
        {
            try
            {
                var result = await dns.QueryAsync(name, QueryType.SRV, cancellationToken: ct);
                var record = result.Answers.SrvRecords().OrderBy(r => r.Priority).ThenByDescending(r => r.Weight).FirstOrDefault();
                // RFC 6186: a target of "." means the service is not offered.
                return record is null || record.Target.Value is "." or "" ? null : (record.Target.Value.TrimEnd('.'), record.Port);
            }
            catch (Exception ex) when (ex is DnsResponseException or SocketException or OperationCanceledException)
            {
                return null;
            }
        }

        ServerEndpoint? imap = await Lookup($"_imaps._tcp.{domain}") is { } imaps
            ? new ServerEndpoint(imaps.Host, imaps.Port, TlsMode.Implicit)
            : await Lookup($"_imap._tcp.{domain}") is { } plain ? new ServerEndpoint(plain.Host, plain.Port, TlsMode.StartTls) : null;
        if (imap is null)
        {
            return null;
        }

        ServerEndpoint? smtp = await Lookup($"_submissions._tcp.{domain}") is { } subs
            ? new ServerEndpoint(subs.Host, subs.Port, TlsMode.Implicit)
            : await Lookup($"_submission._tcp.{domain}") is { } sub ? new ServerEndpoint(sub.Host, sub.Port, TlsMode.StartTls) : null;
        return new DiscoveredSettings { Source = "srv", Imap = imap, Smtp = smtp, Username = email };
    }

    private static async Task<DiscoveredSettings> GuessAsync(string domain, string email, string? mx, CancellationToken ct)
    {
        var hosts = new[] { $"imap.{domain}", $"mail.{domain}", mx, domain }.OfType<string>().Distinct().ToList();
        foreach (var host in hosts)
        {
            foreach (var (port, tls) in new[] { (993, TlsMode.Implicit), (143, TlsMode.StartTls) })
            {
                if (await CanConnectAsync(host, port, ct))
                {
                    var smtpHost = host.StartsWith("imap.", StringComparison.Ordinal) ? "smtp." + host[5..] : host;
                    var smtp = await CanConnectAsync(smtpHost, 465, ct) ? new ServerEndpoint(smtpHost, 465, TlsMode.Implicit)
                        : await CanConnectAsync(smtpHost, 587, ct) ? new ServerEndpoint(smtpHost, 587, TlsMode.StartTls)
                        : null;
                    return new DiscoveredSettings
                    {
                        Source = "guess",
                        Imap = new ServerEndpoint(host, port, tls),
                        Smtp = smtp,
                        Username = email,
                        Note = "These settings were guessed from open ports. Check them with your provider's documentation.",
                    };
                }
            }
        }

        return new DiscoveredSettings
        {
            Source = "guess",
            Imap = new ServerEndpoint($"imap.{domain}", 993, TlsMode.Implicit),
            Smtp = new ServerEndpoint($"smtp.{domain}", 587, TlsMode.StartTls),
            Username = email,
            Note = "No settings found automatically. Enter the server names from your provider's documentation.",
        };
    }

    private static async Task<bool> CanConnectAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var socket = new TcpClient();
            await socket.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static string RegistrableDomain(string host)
    {
        var labels = host.Split('.');
        return labels.Length <= 2 ? host : string.Join('.', labels[^2..]);
    }
}
