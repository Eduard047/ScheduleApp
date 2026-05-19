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
    int? PreferredFirstMaxSlotOrderOverride = null
);

// DTO деталі пропущеного слота.
public record AutoGenGapDetail(
    int GroupId,
    string GroupName,
    DateOnly Date,
    TimeOnly Start,
    TimeOnly End,
    string SlotLabel,
    string? Reason
);

// Результат автогенерації чернеток.
public record AutoGenGapSummaryItem(
    string Code,
    string Title,
    int Count,
    List<string> Examples
);

public record AutoGenResult(
    int Created,
    int Skipped,
    List<string> Warnings,
    List<AutoGenGapDetail>? GapDetails = null,
    List<AutoGenGapSummaryItem>? GapSummary = null
);
