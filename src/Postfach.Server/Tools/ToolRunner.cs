using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using Postfach.Server.Configuration;
using Postfach.Server.Storage;

namespace Postfach.Server.Tools;

/// <summary>Scope checks, error mapping and JSON shaping shared by all tools.</summary>
public sealed class ToolRunner(IHttpContextAccessor http, IAuditLog audit, ILogger<ToolRunner> logger)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        WriteIndented = false,
        // Readable umlauts and quotes; the text is JSON inside a JSON string, so this is safe and saves tokens.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string? Actor => http.HttpContext?.User.Identity?.Name;

    public bool HasScope(string scope) =>
        http.HttpContext?.User.Claims.Any(c => c.Type == "scope" && c.Value == scope) ?? false;

    public async Task<CallToolResult> RunAsync<T>(string tool, string scope, Func<Task<T>> action, Func<T, (string? Account, string Detail)>? auditWith = null)
    {
        try
        {
            if (!HasScope(scope))
            {
                throw new PostfachException(ErrorCodes.Forbidden, $"This connection was not granted the '{scope}' scope.",
                    "The user can grant it when reconnecting Postfach.");
            }

            var result = await action();
            if (auditWith is not null)
            {
                var (account, detail) = auditWith(result);
                await audit.RecordAsync(tool, account, Actor, detail, CancellationToken.None);
            }

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result, Json) }],
            };
        }
        catch (PostfachException ex)
        {
            logger.LogInformation("Tool {Tool} failed with {Code}", tool, ex.Code);
            return Error(ex.Code, ex.Message, ex.Hint);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Scrubbed: details stay in the log (without message content), the client gets a generic error.
            logger.LogError(ex, "Tool {Tool} failed unexpectedly", tool);
            return Error(ErrorCodes.BackendUnavailable, "Internal error.", "Try again; if it persists, check the server log.");
        }
    }

    private static CallToolResult Error(string code, string message, string? hint) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { error = code, message, hint }, Json) }],
    };
}
