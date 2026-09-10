using System.Globalization;
using System.Text.Json.Serialization;

namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimeSlotEditorTargetMode
{
    Course = 0,
    AllCourses = 1
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimeSlotLunchMutationMode
{
    Unchanged = 0,
    Set = 1,
    Remove = 2
}

public sealed record class TimeSlotEditorCourseDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed record class TimeSlotEditorContextDto
{
    public TimeSlotEditorTargetMode TargetMode { get; set; }
    public int? CourseId { get; set; }
    public int? DayOfWeek { get; set; }
    public List<TimeSlotEditorCourseDto> Courses { get; set; } = [];
    public List<TimeSlotDto> ExplicitSlots { get; set; } = [];
    public List<TimeSlotDto> GlobalSlots { get; set; } = [];
    public List<TimeSlotDto> EffectiveSlots { get; set; } = [];
    public bool HasCourseOverride { get; set; }
    public bool HasDayOverride { get; set; }
    public bool IsInherited { get; set; }
    public LunchConfigEditDto? ExplicitLunch { get; set; }
    public LunchConfigEditDto? EffectiveLunch { get; set; }
    public int PreferredFirstMaxSlotOrder { get; set; }
    public bool PreferredFirstInherited { get; set; }
    public int CourseOverrideCount { get; set; }
    public string CurrentRevision { get; set; } = string.Empty;
}

public sealed record class TimeSlotSequenceApplyRequestDto
{
    public TimeSlotEditorTargetMode TargetMode { get; set; }
    public int? CourseId { get; set; }
    public int? DayOfWeek { get; set; }
    public List<TimeSlotDto> Slots { get; set; } = [];
    public string CurrentRevision { get; set; } = string.Empty;
    public string? PreviewToken { get; set; }
    public bool ApplySlots { get; set; } = true;
    public bool Clear { get; set; }
    public bool ResetCourseToGlobal { get; set; }
    public TimeSlotLunchMutationMode LunchMutation { get; set; } = TimeSlotLunchMutationMode.Unchanged;
    public TimeSlotDto? LunchSlot { get; set; }
}

public sealed record class TimeSlotConflictSampleDto
{
    public string Source { get; set; } = string.Empty;
    public int Id { get; set; }
    public int CourseId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string Start { get; set; } = string.Empty;
    public string End { get; set; } = string.Empty;
}

public sealed record class TimeSlotSequencePreviewDto
{
    public TimeSlotEditorTargetMode TargetMode { get; set; }
    public int? CourseId { get; set; }
    public int? DayOfWeek { get; set; }
    public int AffectedCourseCount { get; set; }
    public int CourseOverridesToReplace { get; set; }
    public int MaterializedCourseCount { get; set; }
    public int ScheduleConflictCount { get; set; }
    public int DraftConflictCount { get; set; }
    public int ConflictCount => ScheduleConflictCount + DraftConflictCount;
    public List<TimeSlotConflictSampleDto> ConflictSamples { get; set; } = [];
    public bool CanApply => ConflictCount == 0;
    public bool NoChanges { get; set; }
    public string CurrentRevision { get; set; } = string.Empty;
    public string PreviewToken { get; set; } = string.Empty;
}

public sealed record class TimeSlotSequenceApplyResultDto
{
    public bool NoChanges { get; set; }
    public int AffectedCourseCount { get; set; }
    public string PreviousRevision { get; set; } = string.Empty;
    public string CurrentRevision { get; set; } = string.Empty;
}

public sealed record class NormalizedTimeSlotDto
{
    public string Start { get; set; } = string.Empty;
    public string End { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsLunch { get; set; }
    public int SortOrder { get; set; }
}

public sealed record class TimeSlotSequenceValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; set; } = [];
    public List<NormalizedTimeSlotDto> Slots { get; set; } = [];
}

// Єдині правила послідовності використовуються сервером і клієнтом.
public static class TimeSlotSequenceRules
{
    public const int MaxSlotsPerScope = 100;

    public static TimeSlotSequenceValidationResult Validate(
        IReadOnlyList<TimeSlotDto>? slots,
        int? dayOfWeek)
    {
        var result = new TimeSlotSequenceValidationResult();
        var rows = slots ?? [];
        if (rows.Count > MaxSlotsPerScope)
        {
            result.Errors.Add($"Для одного дня можна зберегти не більше {MaxSlotsPerScope} часових слотів.");
            return result;
        }

        if (dayOfWeek is < 0 or > 6)
        {
            result.Errors.Add("Некоректний день тижня.");
            return result;
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row is null)
            {
                result.Errors.Add($"Слот #{index + 1}: рядок не може бути порожнім.");
                continue;
            }
            if (!TimeOnly.TryParseExact(
                    row.Start,
                    "HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var start)
                || !TimeOnly.TryParseExact(
                    row.End,
                    "HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var end))
            {
                result.Errors.Add($"Слот #{index + 1}: час потрібно вказати у форматі HH:mm.");
                continue;
            }

            if (end <= start)
            {
                result.Errors.Add($"Слот #{index + 1}: час завершення має бути пізніше за час початку.");
            }

            if (row.IsLunch && !row.IsActive)
            {
                result.Errors.Add($"Слот #{index + 1}: обідня перерва має бути активною.");
            }

            result.Slots.Add(new NormalizedTimeSlotDto
            {
                Start = start.ToString("HH:mm", CultureInfo.InvariantCulture),
                End = end.ToString("HH:mm", CultureInfo.InvariantCulture),
                IsActive = row.IsActive,
                IsLunch = row.IsLunch,
                SortOrder = index + 1
            });
        }

        if (result.Errors.Count > 0)
        {
            return result;
        }

        var lunchCount = result.Slots.Count(row => row.IsLunch && row.IsActive);
        if (lunchCount > 1)
        {
            result.Errors.Add("Може бути лише один активний слот, позначений як обід.");
        }
        if (dayOfWeek is not null && lunchCount > 0)
        {
            result.Errors.Add("Обідня перерва є спільною для всіх днів. Налаштуйте її для режиму «Усі дні».");
        }

        for (var index = 1; index < result.Slots.Count; index++)
        {
            var previous = result.Slots[index - 1];
            var current = result.Slots[index];
            var previousStart = TimeOnly.ParseExact(previous.Start, "HH:mm", CultureInfo.InvariantCulture);
            var currentStart = TimeOnly.ParseExact(current.Start, "HH:mm", CultureInfo.InvariantCulture);
            if (currentStart < previousStart)
            {
                result.Errors.Add($"Слот #{index + 1}: розташуйте слоти у хронологічному порядку.");
                break;
            }
        }

        var chronological = result.Slots
            .Select((row, index) => new
            {
                Index = index,
                Start = TimeOnly.ParseExact(row.Start, "HH:mm", CultureInfo.InvariantCulture),
                End = TimeOnly.ParseExact(row.End, "HH:mm", CultureInfo.InvariantCulture)
            })
            .OrderBy(row => row.Start)
            .ThenBy(row => row.End)
            .ToList();
        for (var index = 1; index < chronological.Count; index++)
        {
            var previous = chronological[index - 1];
            var current = chronological[index];
            if (current.Start < previous.End)
            {
                result.Errors.Add($"Слоти #{previous.Index + 1} і #{current.Index + 1} перетинаються.");
                break;
            }
        }

        return result;
    }
}
