namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;

// Забороняє стороннім сайтам вбудовувати інтерфейс розкладу у фрейм.
public sealed class SecurityResponseHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
            context.Response.Headers.XFrameOptions = "DENY";
            return Task.CompletedTask;
        });

        await next(context);
    }
}
