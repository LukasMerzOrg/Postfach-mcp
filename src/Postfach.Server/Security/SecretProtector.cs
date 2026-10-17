using System.Security.Cryptography;
using System.Text;

namespace Postfach.Server.Security;

/// <summary>
/// AES-256-GCM for secrets at rest (mailbox passwords, refresh tokens). Format: <c>enc:v1:base64(nonce|ciphertext|tag)</c>.
/// Sub-keys for different purposes are derived from <c>POSTFACH_SECRET_KEY</c> with HKDF.
/// </summary>
public sealed class SecretProtector
{
    private const string Prefix = "enc:v1:";
    private readonly byte[] _masterKey;
    private readonly byte[] _encryptionKey;

    public SecretProtector(string secretKey)
    {
        _masterKey = ParseKey(secretKey);
        _encryptionKey = DeriveKey("postfach/secrets/v1");
    }

    public static byte[] ParseKey(string secretKey)
    {
        try
        {
            var bytes = Convert.FromBase64String(secretKey.Trim());
            if (bytes.Length == 32)
            {
                return bytes;
            }
        }
        catch (FormatException)
        {
        }

        throw new InvalidOperationException("POSTFACH_SECRET_KEY must be 32 random bytes, base64-encoded (see `postfach generate-key`).");
    }

    public byte[] DeriveKey(string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _masterKey, 32, salt: null, info: Encoding.UTF8.GetBytes(purpose));

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_encryptionKey, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public string Unprotect(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Encrypted secrets must start with 'enc:v1:'.");
        }

        var data = Convert.FromBase64String(value[Prefix.Length..]);
        if (data.Length < 28)
        {
            throw new InvalidOperationException("Encrypted secret is truncated.");
        }

        var nonce = data.AsSpan(0, 12);
        var tag = data.AsSpan(data.Length - 16);
        var cipher = data.AsSpan(12, data.Length - 28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_encryptionKey, 16);
        try
        {
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Cannot decrypt a secret: wrong POSTFACH_SECRET_KEY or corrupted value.");
        }

        return Encoding.UTF8.GetString(plain);
    }
}
