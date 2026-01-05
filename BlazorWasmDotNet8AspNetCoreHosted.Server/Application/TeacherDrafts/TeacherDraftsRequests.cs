using System;
using System.Collections.Generic;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed record DraftAutoGenRequest(
    DateOnly WeekStart,
    bool ClearExisting = true,
    int? CourseId = null,
    int? GroupId = null,
    int? TeacherId = null,
    bool AllowOnDaysOff = false,
    WeekPreset Days = WeekPreset.MonFri,
    Dictionary<int, int>? ModuleHours = null,
    bool SoftFill = false
);

public sealed record ApproveWeekRequest(DateOnly WeekStart, int TeacherId);
public sealed record PublishWeekResults(int Created, int Skipped, List<string> Warnings);
