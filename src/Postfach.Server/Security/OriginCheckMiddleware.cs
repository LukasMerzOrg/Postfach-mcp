namespace Postfach.Server.Security;

/// <summary>Rejects browser requests from foreign origins (DNS-rebinding and CSRF protection for the MCP endpoint).</summary>
public sealed class OriginCheckMiddleware(RequestDelegate next, IReadOnlySet<string> allowed, ILogger<OriginCheckMiddleware> logger)
{
    public static readonly IReadOnlyList<string> DefaultOrigins = ["https://claude.ai", "https://claude.com"];

    public async Task InvokeAsync(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && !allowed.Contains(origin.TrimEnd('/')))
        {
            logger.LogWarning("Rejected request from origin {Origin}", origin);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "forbidden", message = "Origin not allowed." });
            return;
        }

        await next(context);
    }
}
