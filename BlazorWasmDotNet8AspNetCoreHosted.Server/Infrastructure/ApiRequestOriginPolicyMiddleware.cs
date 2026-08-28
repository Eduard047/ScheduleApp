using Microsoft.Extensions.Primitives;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;

public sealed class ApiRequestOriginPolicyMiddleware(RequestDelegate next)
{
    private const string SecFetchSiteHeader = "Sec-Fetch-Site";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        if (IsCrossSiteFetch(context.Request.Headers[SecFetchSiteHeader])
            || !HasValidOriginWhenPresent(context.Request))
        {
            await Results.Problem(
                    title: "Міжсайтовий API-запит відхилено",
                    detail: "Джерело запиту не збігається з адресою застосунку.",
                    statusCode: StatusCodes.Status403Forbidden)
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private static bool IsCrossSiteFetch(StringValues values)
        => values
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries))
            .Any(value => string.Equals(value, "cross-site", StringComparison.OrdinalIgnoreCase));

    private static bool HasValidOriginWhenPresent(HttpRequest request)
    {
        var origins = request.Headers.Origin;
        if (origins.Count == 0)
        {
            // Запити CLI та серверних інтеграцій можуть не мати браузерних заголовків.
            return true;
        }

        if (origins.Count != 1)
        {
            return false;
        }

        var value = origins[0]?.Trim();
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(value, UriKind.Absolute, out var origin)
            || (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(origin.UserInfo)
            || origin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment))
        {
            return false;
        }

        if (!Uri.TryCreate($"{request.Scheme}://{request.Host.Value}", UriKind.Absolute, out var requestOrigin))
        {
            return false;
        }

        return string.Equals(origin.Scheme, requestOrigin.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(origin.IdnHost, requestOrigin.IdnHost, StringComparison.OrdinalIgnoreCase)
               && origin.Port == requestOrigin.Port;
    }
}
