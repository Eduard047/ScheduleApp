using System.Security.Cryptography;
using System.Text;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;

// Формує стабільний неперсональний ключ для квот без збереження мережевої адреси у відкритому вигляді.
public static class ClientPartitionKey
{
    public static string Resolve(HttpContext? context)
    {
        var address = context?.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address)));
    }
}
