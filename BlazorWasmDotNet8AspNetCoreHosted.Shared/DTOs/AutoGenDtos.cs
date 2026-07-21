using System;
using System.Collections.Generic;

// DTO для автогенерації розкладу
namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// Набір днів тижня для автогенерації.
public enum WeekPreset
{
    MonFri,
    MonSat,
    MonSun
}

// Налаштування пріоритетного корпусу та аудиторій для конкретної групи.
public record GroupRoomPreferenceDto(
    int GroupId,
    int? BuildingId = null,
    List<int>? RoomIds = null
);

// М'які параметри тюнінгу автогенерації.
public record AutoGenSoftOptionsDto(
    int? MaxParallelGroupsPerModuleInSlot = null,
    int? RecentRepeatWindowDays = null,
    int? PreferredMaxDistinctModulesPerDay = null,
    int? MaxDistinctModulesPerDay = null,
    double? PreferredFirstPenaltyMultiplier = null,
    double? AdjacentRoomChangePenalty = null,
    double? TeacherLoadPenaltyWeight = null,
    double? BuildingDistancePenaltyWeight = null
);

// DTO запиту автогенерації чернеток.
public record AutoGenRequest(
    DateOnly WeekStart,
    bool ClearExisting = true,
    int? CourseId = null,
    int? GroupId = null,
    List<int>? GroupIds = null,
    bool AllowOnDaysOff = false,
    WeekPreset Days = WeekPreset.MonFri,
    Dictionary<int, int>? ModuleHours = null,
    bool SoftFill = false,
    bool AllowIncompleteDrafts = false,
    DateOnly? RangeStartDate = null,
    DateOnly? RangeEndDate = null,
    List<GroupRoomPreferenceDto>? GroupRoomPreferences = null,
    AutoGenSoftOptionsDto? SoftOptions = null,
    int? PreferredFirstMaxSlotOrderOverride = null,
    bool PreflightOnly = false
);

// DTO деталі пропущеного слота.
public record AutoGenGapDetail(
    int GroupId,
    string GroupName,
    DateOnly Date,
    TimeOnly Start,
    TimeOnly End,
    string SlotLabel,
    string? Reason,
    int? ModuleId = null,
    string? ModuleName = null,
    string? ReasonCode = null,
    string? ConstraintCode = null,
    bool SearchLimitReached = false,
    Dictionary<string, string>? Diagnostics = null
);

// Результат автогенерації чернеток.
public record AutoGenGapSummaryItem(
    string Code,
    string Title,
    int Count,
    List<string> Examples
);

public record AutoGenPreflightItem(
    string Code,
    string Title,
    int Count,
    string Recommendation,
    List<string> Examples
);

public record AutoGenResult(
    int Created,
    int Skipped,
    List<string> Warnings,
    List<AutoGenGapDetail>? GapDetails = null,
    List<AutoGenGapSummaryItem>? GapSummary = null,
    List<AutoGenPreflightItem>? Preflight = null
);

public enum AutoGenJobKind
{
    Generate,
    Preflight,
    Fill
}

public enum AutoGenJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled
}

public record AutoGenJobRequest(
    AutoGenJobKind Kind,
    DateOnly FromDate,
    DateOnly ToDate,
    int CourseId,
    List<int> GroupIds,
    Dictionary<int, int> ModuleHours,
    WeekPreset Days,
    bool ClearExisting,
    bool SoftFill,
    bool PreflightOnly,
    bool AllowIncompleteDrafts = false,
    List<GroupRoomPreferenceDto>? GroupRoomPreferences = null,
    AutoGenSoftOptionsDto? SoftOptions = null,
    int? PreferredFirstMaxSlotOrderOverride = null,
    string? Title = null,
    string? ClientJobId = null
);

public record AutoGenJobStartResult(
    string JobId,
    AutoGenJobStatus Status
);

public record AutoGenRunReportGroupItem(
    int GroupId,
    string GroupName,
    int GapCount,
    List<string> Examples
);

public record AutoGenRunReportModuleItem(
    int? ModuleId,
    string ModuleName,
    int GapCount,
    List<string> Examples
);

public record AutoGenRunReport(
    DateTimeOffset GeneratedAt,
    DateOnly RangeStartDate,
    DateOnly RangeEndDate,
    int TotalWeeks,
    int Created,
    int Skipped,
    int WarningCount,
    int GapCount,
    int DeficitCount,
    List<AutoGenGapSummaryItem> GapSummary,
    List<AutoGenPreflightItem> Preflight,
    List<AutoGenRunReportGroupItem> WorstGroups,
    List<AutoGenRunReportModuleItem> WorstModules,
    List<string> Recommendations
);

public record AutoGenJobStatus(
    string JobId,
    AutoGenJobState State,
    AutoGenJobKind Kind,
    string Title,
    string CurrentStage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateOnly RangeStartDate,
    DateOnly RangeEndDate,
    int TotalWeeks,
    int CompletedWeeks,
    int CurrentWeekNumber,
    DateOnly? CurrentWeekStartDate,
    DateOnly? CurrentRangeStartDate,
    DateOnly? CurrentRangeEndDate,
    int Created,
    int Skipped,
    int WarningCount,
    int GapCount,
    int DeficitCount,
    int Percent,
    bool CancellationRequested,
    string? LastCompletedMessage = null,
    AutoGenResult? Result = null,
    AutoGenRunReport? Report = null,
    string? Error = null
);
