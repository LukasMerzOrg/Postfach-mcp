namespace Postfach;

/// <summary>Error codes returned to the MCP client. They are part of the tool contract.</summary>
public static class ErrorCodes
{
    public const string StaleId = "stale_id";
    public const string NotFound = "not_found";
    public const string Forbidden = "forbidden";
    public const string SendDisabled = "send_disabled";
    public const string TokenMismatch = "token_mismatch";
    public const string RateLimited = "rate_limited";
    public const string BackendUnavailable = "backend_unavailable";
    public const string InvalidArgument = "invalid_argument";
    public const string Unsupported = "unsupported";
}

/// <summary>An error that is safe to show to the client. Never put secrets or message content in it.</summary>
public sealed class PostfachException(string code, string message, string? hint = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;

    public string? Hint { get; } = hint;
}
