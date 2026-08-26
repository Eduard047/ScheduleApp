using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;

public static class ReverseProxyForwardedHeadersPolicy
{
    private static readonly IPAddress RejectAllSentinel = IPAddress.None;

    // Довіряє forwarded-заголовкам лише від адрес, явно заданих оператором.
    public static void Apply(
        ForwardedHeadersOptions options,
        IEnumerable<IPAddress> trustedProxyAddresses)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        var configuredAddresses = trustedProxyAddresses.Distinct().ToList();
        if (configuredAddresses.Count == 0)
        {
            // Непридатна як адреса клієнта broadcast-адреса зберігає політику в режимі «не довіряти нікому».
            options.KnownProxies.Add(RejectAllSentinel);
            return;
        }

        foreach (var address in configuredAddresses)
        {
            options.KnownProxies.Add(address);
        }
    }
}
