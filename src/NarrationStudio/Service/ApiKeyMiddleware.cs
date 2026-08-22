using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NarrationStudio.Service;

public sealed class ApiKeyMiddleware(RequestDelegate next, IOptions<ServiceOptions> options, ILogger<ApiKeyMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) &&
            context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var expected = options.Value.ApiKey?.Trim() ?? "";
        if (string.IsNullOrEmpty(expected))
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var provided) ||
            !string.Equals(provided.ToString(), expected, StringComparison.Ordinal))
        {
            logger.LogWarning("Rejected request without a valid API key.");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid X-Api-Key." });
            return;
        }

        await next(context);
    }
}
