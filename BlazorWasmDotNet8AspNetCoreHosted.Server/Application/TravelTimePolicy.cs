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

// Визначає мінімальний час на зміну фізичної аудиторії між заняттями.
public static class RoomTransitionPolicy
{
    public const int MinimumRoomChangeMinutes = 10;

    // У тій самій аудиторії перехід не потрібен; для іншої аудиторії
    // враховуємо як внутрішній перехід, так і довший перехід між корпусами.
    public static int Resolve(
        IReadOnlyDictionary<(int FromBuildingId, int ToBuildingId), int> configuredBuildingMinutes,
        int fromRoomId,
        int fromBuildingId,
        int toRoomId,
        int toBuildingId)
    {
        if (fromRoomId == toRoomId)
        {
            return 0;
        }

        return Math.Max(
            MinimumRoomChangeMinutes,
            ResolveBuildingMinutes(configuredBuildingMinutes, fromBuildingId, toBuildingId));
    }

    private static int ResolveBuildingMinutes(
        IReadOnlyDictionary<(int FromBuildingId, int ToBuildingId), int> configuredBuildingMinutes,
        int fromBuildingId,
        int toBuildingId)
        => TravelTimePolicy.Resolve(configuredBuildingMinutes, fromBuildingId, toBuildingId);
}
