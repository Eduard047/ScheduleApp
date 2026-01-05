using System;
using System.Collections.Generic;

// DTO для автогенерації розкладу
namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

public enum WeekPreset
{
    MonFri,
    MonSat,
    MonSun
}

public record AutoGenRequest(
    DateOnly WeekStart,
    bool ClearExisting = true,
    int? CourseId = null,
    int? GroupId = null,
    bool AllowOnDaysOff = false,
    WeekPreset Days = WeekPreset.MonFri,
    Dictionary<int, int>? ModuleHours = null,
    bool SoftFill = false
);

public record AutoGenGapDetail(
    int GroupId,
    string GroupName,
    DateOnly Date,
    TimeOnly Start,
    TimeOnly End,
    string SlotLabel,
    string? Reason
);

public record AutoGenResult(
    int Created,
    int Skipped,
    List<string> Warnings,
    List<AutoGenGapDetail>? GapDetails = null
);
