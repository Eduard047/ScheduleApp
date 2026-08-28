namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;

// Дозволяє інтерфейсу завантажувати лише власні ресурси та забороняє вбудовування у фрейми.
public sealed class SecurityResponseHeadersMiddleware(
    RequestDelegate next,
    IWebHostEnvironment environment)
{
    internal const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "base-uri 'self'; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'; " +
        "script-src 'self' 'wasm-unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self' ws: wss:; " +
        "form-action 'self'";
    internal const string SwaggerContentSecurityPolicy =
        "default-src 'self'; " +
        "base-uri 'self'; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'; " +
        "script-src 'self' 'wasm-unsafe-eval' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self' ws: wss:; " +
        "form-action 'self'";

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.ContentSecurityPolicy =
                environment.IsDevelopment()
                && context.Request.Path.StartsWithSegments("/swagger")
                    ? SwaggerContentSecurityPolicy
                    : ContentSecurityPolicy;
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            context.Response.Headers["Permissions-Policy"] = "camera=(), geolocation=(), microphone=()";
            return Task.CompletedTask;
        });

        await next(context);
    }
}
