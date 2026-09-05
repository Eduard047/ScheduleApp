namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

public record AutoGenCapacityGroup(int GroupId, string GroupName, int CalendarSlots, int RequestedLessons);
public record AutoGenCapacityDto(int CalendarSlots, int RequestedLessons, List<AutoGenCapacityGroup> Groups);

// Якість розкладу не є станом виконання фонового завдання.
public enum AutoGenCoverageStatus
{
    Partial,
    SearchLimited,
    CurriculumComplete,
    FullyOccupied
}

public record AutoGenCoverageGroup(
    int GroupId, string GroupName, int AvailableSlots, int OccupiedSlots,
    int EmptySlots, int InternalGapSlots, int RequestedLessons, int MissingLessons,
    int OverplannedLessons, int IncompleteLessons);

public record AutoGenCoverageWeek(
    DateOnly WeekStart, int AvailableSlots, int OccupiedSlots, int InternalGapSlots);

public record AutoGenCoverageModule(
    int ModuleId, int RequestedLessons, int ScheduledLessons, int MissingLessons, int OverplannedLessons);

public record AutoGenCoverageDto(
    AutoGenCoverageStatus Status,
    bool DemandKnown,
    int AvailableSlots,
    int OccupiedSlots,
    int EmptySlots,
    int InternalGapSlots,
    int RequestedLessons,
    int ScheduledRequestedLessons,
    int MissingLessons,
    int OverplannedLessons,
    int IncompleteLessons,
    int ExcludedGroupDays,
    bool SearchLimitReached,
    List<AutoGenCoverageGroup> Groups,
    List<AutoGenCoverageWeek> Weeks,
    List<AutoGenCoverageModule> Modules);
