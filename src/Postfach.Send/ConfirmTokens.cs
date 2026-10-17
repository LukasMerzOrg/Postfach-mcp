using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MimeKit;
using Postfach.Imap;

namespace Postfach.Send;

/// <summary>
/// Content-bound send confirmation: HMAC over draft id + content hash + expiry. A draft edited after the token was
/// issued (in Postfach or in another mail client) no longer matches, so Claude cannot send something the user did not see.
/// </summary>
public sealed class ConfirmTokens(byte[] key, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public (string Token, DateTimeOffset ExpiresAt) Issue(MessageRef draft, string contentHash)
    {
        var expires = time.GetUtcNow().Add(Lifetime);
        var payload = string.Create(CultureInfo.InvariantCulture, $"{draft}|{contentHash}|{expires.ToUnixTimeSeconds()}");
        return (Encode(Encoding.UTF8.GetBytes(payload)) + "." + Encode(Sign(payload)), expires);
    }

    /// <summary>Returns a stable id for the token (for single-use bookkeeping) or throws <c>token_mismatch</c>.</summary>
    public string Verify(string? token, MessageRef draft, string contentHash)
    {
        var dot = token?.IndexOf('.') ?? -1;
        if (token is null || dot <= 0)
        {
            throw Mismatch("The confirm_token is missing or malformed.");
        }

        string payload;
        byte[] signature;
        try
        {
            payload = Encoding.UTF8.GetString(Decode(token[..dot]));
            signature = Decode(token[(dot + 1)..]);
        }
        catch (FormatException)
        {
            throw Mismatch("The confirm_token is malformed.");
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, Sign(payload)))
        {
            throw Mismatch("The confirm_token is not valid.");
        }

        var parts = payload.Split('|');
        if (parts.Length != 3 || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var exp))
        {
            throw Mismatch("The confirm_token is not valid.");
        }

        if (parts[0] != draft.ToString())
        {
            throw Mismatch("The confirm_token belongs to a different draft.");
        }

        if (DateTimeOffset.FromUnixTimeSeconds(exp) < time.GetUtcNow())
        {
            throw Mismatch("The confirm_token has expired.");
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[1]), Encoding.ASCII.GetBytes(contentHash)))
        {
            throw Mismatch("The draft changed after the confirm_token was issued.");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..32];
    }

    private static PostfachException Mismatch(string message) =>
        new(ErrorCodes.TokenMismatch, message,
            "Call update_draft with just the draft id to get a fresh confirm_token, show the draft to the user again and wait for their approval.");

    private byte[] Sign(string payload) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value)
    {
        var b64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '='));
    }

    /// <summary>Hash over what the recipient will see: addresses, subject, bodies and attachments.</summary>
    public static string ContentHash(MimeMessage message)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string? value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value ?? string.Empty));
            hash.AppendData([0]);
        }

        Add(string.Join(',', message.From.Mailboxes.Select(m => m.Address.ToLowerInvariant())));
        Add(string.Join(',', message.To.Mailboxes.Select(m => m.Address.ToLowerInvariant())));
        Add(string.Join(',', message.Cc.Mailboxes.Select(m => m.Address.ToLowerInvariant())));
        Add(string.Join(',', message.Bcc.Mailboxes.Select(m => m.Address.ToLowerInvariant())));
        Add(message.Subject);
        Add(Normalize(message.TextBody));
        Add(Normalize(message.HtmlBody));
        foreach (var attachment in message.Attachments.OfType<MimePart>())
        {
            Add(attachment.FileName);
            using var stream = new MemoryStream();
            attachment.Content?.DecodeTo(stream);
            Add(Convert.ToHexString(SHA256.HashData(stream.ToArray())));
        }

        return Convert.ToHexString(hash.GetHashAndReset())[..32].ToLowerInvariant();
    }

    // Servers may re-wrap line endings; the content is what matters.
    private static string Normalize(string? body) => (body ?? string.Empty).Replace("\r\n", "\n").TrimEnd();
}
