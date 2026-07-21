namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// Єдине джерело параметрів рекомендованого профілю для UI та матриці якості.
public static class AutoGenRecommendedProfile
{
    public const string Name = "soft-repeat-0-par3-distinct5-pref015-teacher0-build0";
    public const int PreferredFirstMaxSlotOrderOverride = 6;
    public const int RecentRepeatWindowDays = 0;
    public const int MaxParallelGroupsPerModuleInSlot = 3;
    public const int PreferredMaxDistinctModulesPerDay = 5;
    public const int MaxDistinctModulesPerDay = 6;
    public const double PreferredFirstPenaltyMultiplier = 0.15;
    public const double TeacherLoadPenaltyWeight = 0.0;
    public const double BuildingDistancePenaltyWeight = 0.0;

    public static AutoGenSoftOptionsDto CreateSoftOptions()
        => new(
            MaxParallelGroupsPerModuleInSlot: MaxParallelGroupsPerModuleInSlot,
            RecentRepeatWindowDays: RecentRepeatWindowDays,
            PreferredMaxDistinctModulesPerDay: PreferredMaxDistinctModulesPerDay,
            MaxDistinctModulesPerDay: MaxDistinctModulesPerDay,
            PreferredFirstPenaltyMultiplier: PreferredFirstPenaltyMultiplier,
            TeacherLoadPenaltyWeight: TeacherLoadPenaltyWeight,
            BuildingDistancePenaltyWeight: BuildingDistancePenaltyWeight);
}
