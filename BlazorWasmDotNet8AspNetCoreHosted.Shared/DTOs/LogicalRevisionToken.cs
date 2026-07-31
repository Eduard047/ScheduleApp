using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// Формує стабільну версію цілого логічного заняття з версій усіх його рядків.
public static class LogicalRevisionToken
{
    public static Guid Combine(IEnumerable<KeyValuePair<int, Guid>> rowRevisions)
    {
        ArgumentNullException.ThrowIfNull(rowRevisions);
        var ordered = rowRevisions
            .OrderBy(item => item.Key)
            .ToList();
        if (ordered.Count == 0)
        {
            return Guid.Empty;
        }
        if (ordered.Count == 1)
        {
            return ordered[0].Value;
        }

        var payload = new byte[ordered.Count * 20];
        for (var index = 0; index < ordered.Count; index++)
        {
            var offset = index * 20;
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), ordered[index].Key);
            ordered[index].Value.TryWriteBytes(payload.AsSpan(offset + 4, 16));
        }

        var hash = SHA256.HashData(payload);
        return new Guid(hash.AsSpan(0, 16));
    }
}
