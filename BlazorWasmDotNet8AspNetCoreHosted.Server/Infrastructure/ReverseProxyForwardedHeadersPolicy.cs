using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;

public static class ReverseProxyForwardedHeadersPolicy
{
    // Довіряє forwarded-заголовкам лише від адрес, явно заданих оператором.
    public static void Apply(
        ForwardedHeadersOptions options,
        IEnumerable<IPAddress> trustedProxyAddresses)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var address in trustedProxyAddresses)
        {
            options.KnownProxies.Add(address);
        }
    }
}
