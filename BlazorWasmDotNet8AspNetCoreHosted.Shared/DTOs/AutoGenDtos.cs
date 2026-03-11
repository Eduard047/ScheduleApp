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

// DTO запиту автогенерації чернеток.
public record AutoGenRequest(
    DateOnly WeekStart,
    bool ClearExisting = true,
    int? CourseId = null,
    int? GroupId = null,
    bool AllowOnDaysOff = false,
    WeekPreset Days = WeekPreset.MonFri,
    Dictionary<int, int>? ModuleHours = null,
    bool SoftFill = false,
    DateOnly? RangeStartDate = null,
    DateOnly? RangeEndDate = null
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
public record AutoGenResult(
    int Created,
    int Skipped,
    List<string> Warnings,
    List<AutoGenGapDetail>? GapDetails = null
);
