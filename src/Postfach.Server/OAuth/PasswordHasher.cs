using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Postfach.Server.OAuth;

/// <summary>Argon2id in PHC string format: <c>$argon2id$v=19$m=19456,t=2,p=1$salt$hash</c> (OWASP minimum).</summary>
public static class PasswordHasher
{
    private const int MemoryKib = 19456;
    private const int Iterations = 2;
    private const int Parallelism = 1;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Compute(password, salt, MemoryKib, Iterations, Parallelism, 32);
        return $"$argon2id$v=19$m={MemoryKib},t={Iterations},p={Parallelism}${B64(salt)}${B64(hash)}";
    }

    public static bool Verify(string password, string phc)
    {
        try
        {
            var parts = phc.Split('$');
            if (parts.Length != 6 || parts[1] != "argon2id" || parts[2] != "v=19")
            {
                return false;
            }

            var p = parts[3].Split(',').Select(x => x.Split('=')).ToDictionary(x => x[0], x => int.Parse(x[1], System.Globalization.CultureInfo.InvariantCulture));
            var salt = FromB64(parts[4]);
            var expected = FromB64(parts[5]);
            var actual = Compute(password, salt, p["m"], p["t"], p["p"], expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    private static byte[] Compute(string password, byte[] salt, int memory, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memory,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(length);
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] FromB64(string value) => Convert.FromBase64String(value.PadRight(value.Length + ((4 - (value.Length % 4)) % 4), '='));
}
