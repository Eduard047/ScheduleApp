namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public readonly record struct AutogenDayQuality(
    int FilledSlots, int GapCount, int DistinctModules, int RequiredDistinctModules,
    int ModuleSegments, int Transitions, int PendingSharedCatchUp, bool AddedIncomplete)
{
    // Залишок годин усього курсу порівнюється окремо й не спотворює якість повного дня.
    public double Score => FilledSlots * 10_000 - GapCount * 5_000
        - PendingSharedCatchUp * 1_300
        - Math.Max(0, RequiredDistinctModules - DistinctModules) * 900
        - Math.Max(0, ModuleSegments - DistinctModules) * 180 - Transitions * 40;

    // Усі штрафи дня досягли нижньої межі; інший порядок модулів не поліпшить цю оцінку.
    public bool IsAtUpperBound(int capacity) => FilledSlots >= capacity && GapCount == 0
        && !AddedIncomplete && PendingSharedCatchUp == 0
        && DistinctModules == Math.Min(capacity, Math.Max(1, RequiredDistinctModules))
        && ModuleSegments == DistinctModules && Transitions == Math.Max(0, DistinctModules - 1);
}
