using Microsoft.AspNetCore.HostFiltering;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;

public static class AllowedHostPolicy
{
    public static IReadOnlyList<string> Parse(string? configuredHosts)
    {
        var hosts = configuredHosts?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        if (hosts.Length == 0)
        {
            throw new InvalidOperationException(
                "Параметр 'AllowedHosts' має містити щонайменше одне точне ім'я хоста.");
        }

        if (hosts.Any(IsWildcard))
        {
            throw new InvalidOperationException(
                "Параметр 'AllowedHosts' не може містити шаблони або значення, що дозволяють будь-який хост.");
        }

        return hosts;
    }

    public static void Apply(HostFilteringOptions options, IReadOnlyList<string> allowedHosts)
    {
        options.AllowedHosts = allowedHosts.ToList();
        options.AllowEmptyHosts = false;
        options.IncludeFailureMessage = false;
    }

    private static bool IsWildcard(string host)
        => host.Contains('*', StringComparison.Ordinal)
           || string.Equals(host, "+", StringComparison.Ordinal);
}
