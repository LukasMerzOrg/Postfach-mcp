using Postfach.Send;

namespace Postfach.Server.Configuration;

/// <summary>Server settings from environment variables. Secrets never come from appsettings files.</summary>
public sealed class PostfachSettings
{
    public string? PublicUrl { get; init; }

    public required string DataDirectory { get; init; }

    public required string ConfigPath { get; init; }

    public string? SecretKey { get; init; }

    public bool InsecureTls { get; init; }

    public string? ApiToken { get; init; }

    public IReadOnlyList<string> ApiTokenScopes { get; init; } = Scopes.DefaultGrant;

    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    public required SendOptions Send { get; init; }

    public int RequestsPerMinute { get; init; } = 120;

    /// <summary>Networks whose X-Forwarded-For/-Proto headers are trusted (the reverse proxy, e.g. SWAG on a Docker network).</summary>
    public IReadOnlyList<string> TrustedProxies { get; init; } = DefaultTrustedProxies;

    public static readonly IReadOnlyList<string> DefaultTrustedProxies =
        ["127.0.0.1/32", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];

    public static PostfachSettings FromEnvironment(Func<string, string?> env)
    {
        var data = env("POSTFACH_DATA_DIR") ?? "/data";
        return new PostfachSettings
        {
            PublicUrl = env("POSTFACH_PUBLIC_URL")?.TrimEnd('/'),
            DataDirectory = data,
            ConfigPath = env("POSTFACH_CONFIG") ?? Path.Combine(data, "postfach.yaml"),
            SecretKey = env("POSTFACH_SECRET_KEY"),
            InsecureTls = IsTrue(env("POSTFACH_INSECURE_TLS")),
            ApiToken = env("POSTFACH_API_TOKEN"),
            ApiTokenScopes = SplitList(env("POSTFACH_API_TOKEN_SCOPES")) is { Count: > 0 } scopes ? scopes : Scopes.DefaultGrant,
            AllowedOrigins = SplitList(env("POSTFACH_ALLOWED_ORIGINS")),
            TrustedProxies = SplitList(env("POSTFACH_TRUSTED_PROXIES")) is { Count: > 0 } proxies ? proxies : DefaultTrustedProxies,
            RequestsPerMinute = int.TryParse(env("POSTFACH_RATE_LIMIT_PER_MINUTE"), out var rpm) && rpm > 0 ? rpm : 120,
            Send = new SendOptions
            {
                AllowSend = IsTrue(env("POSTFACH_ALLOW_SEND")),
                MaxRecipients = int.TryParse(env("POSTFACH_MAX_RECIPIENTS"), out var mr) && mr > 0 ? mr : 20,
                MaxSendsPerDay = int.TryParse(env("POSTFACH_MAX_SENDS_PER_DAY"), out var ms) && ms > 0 ? ms : 30,
                Allowlist = SplitList(env("POSTFACH_SEND_ALLOWLIST")),
            },
        };
    }

    private static bool IsTrue(string? value) =>
        value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static List<string> SplitList(string? value) =>
        (value ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

public static class Scopes
{
    public const string Read = "mail.read";
    public const string Organize = "mail.organize";
    public const string Draft = "mail.draft";
    public const string Send = "mail.send";
    public const string Delete = "mail.delete";

    public static readonly IReadOnlyList<string> All = [Read, Organize, Draft, Send, Delete];

    /// <summary>Read and draft on, send and delete off — the safe default.</summary>
    public static readonly IReadOnlyList<string> DefaultGrant = [Read, Organize, Draft];
}
