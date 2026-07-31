using System.Globalization;

namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

public sealed record ScheduleOverviewSlotRow(string Start, string End);

public sealed record ScheduleOverviewDisplayGroup(
    IReadOnlyList<ScheduleItemDto> Items,
    bool IsMergedLecture)
{
    public ScheduleItemDto Anchor => Items[0];
}

public static class ScheduleOverviewLayoutPlanner
{
    // Формує окремі картки для паралельних подій і об'єднує лише однакове візуальне представлення заняття.
    public static IReadOnlyList<ScheduleOverviewDisplayGroup> GroupEvents(
        IEnumerable<ScheduleItemDto> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        return items
            .GroupBy(item => (item.DayNumber, item.TimeStart, item.TimeEnd))
            .SelectMany(slot => slot
                .GroupBy(BuildPresentationKey)
                .Select(group =>
                {
                    var rows = group.OrderBy(item => item.Id).ToList();
                    var isMergedLecture = IsLecture(rows[0])
                        && rows.Select(item => item.GroupId).Distinct().Skip(1).Any();
                    return new ScheduleOverviewDisplayGroup(rows, isMergedLecture);
                }))
            .OrderBy(group => group.Anchor.DayNumber)
            .ThenBy(group => ParseTime(group.Anchor.TimeStart))
            .ThenBy(group => group.Anchor.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.Anchor.Module, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.Anchor.Teacher, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.Anchor.Id)
            .ToList();
    }

    // Будує об'єднану часову вісь та не приховує заняття з історичним або нестандартним часом.
    public static IReadOnlyList<ScheduleOverviewSlotRow> BuildSlotRows(
        IEnumerable<TimeSlotDto> slots,
        IEnumerable<ScheduleItemDto> visibleItems)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(visibleItems);

        return slots
            .Where(slot => slot.IsActive && !slot.IsLunch)
            .Select(slot => new ScheduleOverviewSlotRow(slot.Start, slot.End))
            .Concat(visibleItems.Select(item => new ScheduleOverviewSlotRow(item.TimeStart, item.TimeEnd)))
            .Where(row => !string.IsNullOrWhiteSpace(row.Start) && !string.IsNullOrWhiteSpace(row.End))
            .Distinct()
            .OrderBy(row => ParseTime(row.Start))
            .ThenBy(row => ParseTime(row.End))
            .ThenBy(row => row.Start, StringComparer.Ordinal)
            .ThenBy(row => row.End, StringComparer.Ordinal)
            .ToList();
    }

    private static PresentationKey BuildPresentationKey(ScheduleItemDto item)
    {
        var isBreak = string.Equals(item.LessonTypeCode?.Trim(), "BREAK", StringComparison.OrdinalIgnoreCase);
        if (isBreak)
        {
            return new PresentationKey("break", null, 0, item.LessonTypeId, string.Empty, string.Empty, false, item.IsLocked);
        }

        var groupId = IsLecture(item) ? (int?)null : item.GroupId;
        return new PresentationKey(
            "lesson",
            groupId,
            item.ModuleId,
            item.LessonTypeId,
            ResolveIdentity(item.TeacherId, item.Teacher),
            item.RequiresRoom
                ? ResolveRoomIdentity(item)
                : "without-room",
            item.RequiresRoom,
            item.IsLocked);
    }

    private static bool IsLecture(ScheduleItemDto item)
    {
        var code = item.LessonTypeCode?.Trim();
        if (string.Equals(code, "LECTURE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "LECT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "LEC", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = item.LessonTypeName?.Trim();
        return !string.IsNullOrWhiteSpace(name)
               && (name.Contains("ЛЕКЦ", StringComparison.CurrentCultureIgnoreCase)
                   || name.Contains("LECTURE", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveRoomIdentity(ScheduleItemDto item)
    {
        if (item.RoomId is int roomId)
        {
            return $"id:{roomId}";
        }

        return $"name:{Normalize(item.Room)}|building:{ResolveIdentity(item.BuildingId, item.Building)}";
    }

    private static string ResolveIdentity(int? id, string? name)
        => id is int value ? $"id:{value}" : $"name:{Normalize(name)}";

    private static string Normalize(string? value)
        => value?.Trim().ToUpperInvariant() ?? string.Empty;

    private static TimeOnly ParseTime(string? value)
        => TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : TimeOnly.MaxValue;

    private sealed record PresentationKey(
        string Kind,
        int? GroupId,
        int ModuleId,
        int LessonTypeId,
        string Teacher,
        string Room,
        bool RequiresRoom,
        bool IsLocked);
}
