using System.Data;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;

public static class DatabaseSchemaIntegrityVerifier
{
    private static readonly HashSet<string> RequiredUniqueIndexes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TimeSlots.UX_TimeSlots_NormalizedScope",
        "CalendarExceptions.UX_CalendarExceptions_NormalizedScope",
        "LunchConfigs.UX_LunchConfigs_NormalizedScope",
        "PreferredFirstSlotLimitConfigs.UX_PreferredFirstSlotLimitConfigs_NormalizedScope"
    };

    public static async Task<IReadOnlyList<string>> FindMissingRequiredIndexesAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT CONCAT(TABLE_NAME, '.', INDEX_NAME)
                FROM information_schema.statistics
                WHERE TABLE_SCHEMA = DATABASE()
                  AND NON_UNIQUE = 0
                  AND INDEX_NAME IN (
                      'UX_TimeSlots_NormalizedScope',
                      'UX_CalendarExceptions_NormalizedScope',
                      'UX_LunchConfigs_NormalizedScope',
                      'UX_PreferredFirstSlotLimitConfigs_NormalizedScope')
                """;

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0))
                {
                    existing.Add(reader.GetString(0));
                }
            }

            return RequiredUniqueIndexes
                .Except(existing, StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            if (shouldClose)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
