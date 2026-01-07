using System;
using System.Collections.Generic;
using System.Linq;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

// Допоміжні методи для роботи з чернетками викладачів.
internal static class TeacherDraftsHelpers
{
    // Розбирає BatchKey перенесення, щоб визначити джерельну пару.
    internal static (bool isRescheduled, int? sourceItemId, int? originalLessonTypeId) ParseRescheduleBatchKey(string? batchKey)
    {
        if (string.IsNullOrWhiteSpace(batchKey)) return (false, null, null);
        if (!batchKey.StartsWith("rescheduled", StringComparison.OrdinalIgnoreCase)) return (false, null, null);
        var parts = batchKey.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int? sourceId = null;
        if (parts.Length >= 2 && int.TryParse(parts[1], out var parsedSource))
        {
            sourceId = parsedSource;
        }
        int? ltId = null;
        if (parts.Length >= 3 && int.TryParse(parts[2], out var parsedLt))
        {
            ltId = parsedLt;
        }
        return (true, sourceId, ltId);
    }
    // Повертає нормалізований код теми або null.
    internal static string? BuildModuleTopicCode(ModuleTopic? topic)
    {
        if (topic is null) return null;
        return string.IsNullOrWhiteSpace(topic.TopicCode) ? null : topic.TopicCode.Trim();
    }
    // Формує текст комірки для експорту у Excel.
    internal static string BuildExportCell(TeacherDraftItemDto item)
    {
        if (IsBreakLesson(item.LessonTypeCode) || IsCanceledLesson(item.LessonTypeCode))
        {
            var summary = new List<string> { item.LessonTypeName };
            if (item.IsRescheduled) summary.Add("Перенесено");
            if (item.Status == DraftStatusDto.Published) summary.Add("Опубліковано");
            return string.Join(Environment.NewLine, summary.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        var entries = new List<string>();
        if (item.IsSelfStudy) entries.Add("Самостійна робота");
        if (!string.IsNullOrWhiteSpace(item.Module)) entries.Add(item.Module);
        if (!string.IsNullOrWhiteSpace(item.TopicCode)) entries.Add(item.TopicCode);
        if (!string.IsNullOrWhiteSpace(item.Teacher)) entries.Add(item.Teacher);
        var lessonLine = item.LessonTypeName;
        if (item.RequiresRoom && !string.IsNullOrWhiteSpace(item.Room))
        {
            lessonLine = $"{lessonLine} (ауд. {item.Room})";
        }
        entries.Add(lessonLine);
        if (item.IsRescheduled) entries.Add("Перенесено");
        if (item.Status == DraftStatusDto.Published) entries.Add("Опубліковано");
        return string.Join(Environment.NewLine, entries.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
    private static bool IsBreakLesson(string? lessonTypeCode)
        => string.Equals(lessonTypeCode, "BREAK", StringComparison.OrdinalIgnoreCase);
    private static bool IsCanceledLesson(string? lessonTypeCode)
        => string.Equals(lessonTypeCode, "CANCELED", StringComparison.OrdinalIgnoreCase);
    private static readonly string[] UkrainianDayNames =
    {
        "Неділя",
        "Понеділок",
        "Вівторок",
        "Середа",
        "Четвер",
        "П'ятниця",
        "Субота"
    };
    // Повертає українську назву дня тижня.
    internal static string GetUkrainianDayName(DateOnly date)
        => UkrainianDayNames[(int)date.DayOfWeek];
    // Порівнює коди тем з урахуванням числових сегментів.
    internal static int CompareTopicCodes(string? left, string? right)
    {
        var leftParts = ParseTopicCodeSegments(left);
        var rightParts = ParseTopicCodeSegments(right);
        var maxLength = Math.Max(leftParts.Count, rightParts.Count);
        for (var i = 0; i < maxLength; i++)
        {
            var leftValue = i < leftParts.Count ? leftParts[i] : 0;
            var rightValue = i < rightParts.Count ? rightParts[i] : 0;
            var diff = leftValue.CompareTo(rightValue);
            if (diff != 0)
            {
                return diff;
            }
        }
        return string.Compare(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
    }
    // Розбиває код теми на числові сегменти.
    internal static IReadOnlyList<int> ParseTopicCodeSegments(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Array.Empty<int>();
        }
        return code.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var value) ? value : int.MaxValue)
            .ToArray();
    }
    // Визначає календарний виняток з урахуванням курсу та групи.
    internal static bool? ResolveCalendarOverride(IEnumerable<CalendarException> items, DateOnly date, int? courseId, int? groupId)
    {
        int? normCourse = (courseId is int cid && cid > 0) ? cid : null;
        int? normGroup = (groupId is int gid && gid > 0) ? gid : null;
        var match = items
            .Where(x => x.Date == date)
            .Where(x => normGroup != null ? (x.GroupId == normGroup || x.GroupId == null) : x.GroupId == null)
            .Where(x => normCourse != null ? (x.CourseId == normCourse || x.CourseId == null) : x.CourseId == null)
            .OrderByDescending(x => x.GroupId != null)
            .ThenByDescending(x => x.CourseId != null)
            .FirstOrDefault();
        return match?.IsWorkingDay;
    }
}
