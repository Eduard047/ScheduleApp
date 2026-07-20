namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

// Зберігає єдині правила визначення часу переходу між корпусами.
public static class TravelTimePolicy
{
    public const int DefaultMinutes = 20;

    // Нормалізує неорієнтовану пару корпусів для зберігання та порівняння.
    public static (int FromBuildingId, int ToBuildingId) NormalizePair(int firstBuildingId, int secondBuildingId)
        => (Math.Min(firstBuildingId, secondBuildingId), Math.Max(firstBuildingId, secondBuildingId));

    // Повертає налаштований час в обох напрямках або доменне значення за замовчуванням.
    public static int Resolve(
        IReadOnlyDictionary<(int FromBuildingId, int ToBuildingId), int> configuredMinutes,
        int fromBuildingId,
        int toBuildingId)
    {
        if (fromBuildingId == toBuildingId)
        {
            return 0;
        }
        if (configuredMinutes.TryGetValue((fromBuildingId, toBuildingId), out var minutes)
            || configuredMinutes.TryGetValue((toBuildingId, fromBuildingId), out minutes))
        {
            return minutes;
        }
        return DefaultMinutes;
    }
}
