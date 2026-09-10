namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// Спільні межі для форми, запиту, збереженого плану та його читання.
public static class AutoGenWorkloadLimits
{
    public const int MaxRangeDays = 520 * 7;
    public const int MaxGroupDays = 40_000;
    public const int MaxCalendarSlots = 40_000;
    public const int MaxRequestedLessons = 20_000;
    // Заміна повного плану може містити видалення і додавання для кожного заняття.
    public const int MaxPlanChanges = 2 * MaxRequestedLessons;
    public const int MaxPlanSnapshotCharacters = 32_000_000;
}
