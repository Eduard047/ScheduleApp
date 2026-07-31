using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Client.Services;

internal static class ApiClientHelpers
{
    public static string WithQuery(string url, params (string Name, string? Value)[] values)
    {
        var query = string.Join("&", values
            .Where(v => v.Value is not null)
            .Select(v => $"{Uri.EscapeDataString(v.Name)}={Uri.EscapeDataString(v.Value!)}"));

        if (string.IsNullOrEmpty(query))
        {
            return url;
        }

        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}{query}";
    }

    public static string WithConfirm(string url)
        => WithQuery(url, ("confirm", "true"));

    public static MetaResponseDto EmptyMeta()
        => new(new(), new(), new(), new(), new(), new(), new());
}
