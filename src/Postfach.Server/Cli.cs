using System.Security.Cryptography;
using Postfach.Server.Security;

namespace Postfach.Server;

/// <summary>Small admin commands: <c>generate-key</c>, <c>generate-token</c>, <c>hash-token</c>, <c>hash-password</c>, <c>encrypt-secret</c>, <c>revoke-oauth</c>.</summary>
internal static class Cli
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        switch (args[0])
        {
            case "generate-key":
                Console.WriteLine(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                return true;
            case "generate-token":
            {
                var token = "pf_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                Console.WriteLine($"token:  {token}");
                Console.WriteLine($"sha256: {Convert.ToHexString(ApiTokenStore.Hash(token)).ToLowerInvariant()}");
                return true;
            }
            case "hash-token":
            {
                var token = args.Length > 1 ? args[1] : Console.In.ReadLine();
                if (string.IsNullOrWhiteSpace(token))
                {
                    Console.Error.WriteLine("usage: postfach hash-token <token>   (or pass it on stdin)");
                    exitCode = 2;
                    return true;
                }

                Console.WriteLine(Convert.ToHexString(ApiTokenStore.Hash(token.Trim())).ToLowerInvariant());
                return true;
            }
            case "hash-password":
            {
                // stdin, so the password does not end up in shell history.
                var password = Console.In.ReadToEnd().TrimEnd('\r', '\n');
                if (password.Length < 12)
                {
                    Console.Error.WriteLine("usage: printf '%s' 'a-long-password' | postfach hash-password   (min. 12 characters)");
                    exitCode = 2;
                    return true;
                }

                Console.WriteLine(Postfach.Server.OAuth.PasswordHasher.Hash(password));
                return true;
            }
            case "revoke-oauth":
            {
                var data = Environment.GetEnvironmentVariable("POSTFACH_DATA_DIR") ?? "/data";
                var store = new Postfach.Server.OAuth.OAuthStore(Path.Combine(data, "postfach.db"), TimeProvider.System);
                var count = store.RevokeAllAsync(CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine($"Revoked {count} OAuth token(s). Clients must sign in again.");
                return true;
            }
            case "encrypt-secret":
            {
                var key = Environment.GetEnvironmentVariable("POSTFACH_SECRET_KEY");
                if (string.IsNullOrWhiteSpace(key))
                {
                    Console.Error.WriteLine("POSTFACH_SECRET_KEY must be set.");
                    exitCode = 2;
                    return true;
                }

                // Read from stdin so the secret does not end up in shell history.
                var secret = Console.In.ReadToEnd().TrimEnd('\r', '\n');
                if (secret.Length == 0)
                {
                    Console.Error.WriteLine("usage: printf '%s' 'app-password' | postfach encrypt-secret");
                    exitCode = 2;
                    return true;
                }

                Console.WriteLine(new SecretProtector(key).Protect(secret));
                return true;
            }
            default:
                return false;
        }
    }
}
