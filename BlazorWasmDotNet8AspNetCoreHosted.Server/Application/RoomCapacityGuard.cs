using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

// Перевіряє сумарну місткість аудиторії для логічних слотів розкладу й чернеток окремо.
public sealed class RoomCapacityGuard(AppDbContext db)
{
    public async Task<RoomCapacityViolation?> FindForGroupSizeAsync(
        int groupId,
        int proposedStudentsCount,
        CancellationToken cancellationToken = default)
    {
        var publishedRows = await LoadPublishedRowsForGroupSlotsAsync(groupId, cancellationToken);
        var violation = FindViolation(
            publishedRows,
            "опублікованому розкладі",
            groupId,
            proposedStudentsCount,
            capacityOverride: null);
        if (violation is not null)
        {
            return violation;
        }

        var draftRows = await LoadDraftRowsForGroupSlotsAsync(groupId, cancellationToken);
        return FindViolation(
            draftRows,
            "чернетках",
            groupId,
            proposedStudentsCount,
            capacityOverride: null);
    }

    public async Task<RoomCapacityViolation?> FindForRoomCapacityAsync(
        int roomId,
        int proposedCapacity,
        CancellationToken cancellationToken = default)
    {
        var publishedRows = await LoadPublishedRowsForRoomAsync(roomId, cancellationToken);
        var violation = FindViolation(
            publishedRows,
            "опублікованому розкладі",
            groupIdOverride: null,
            studentsCountOverride: null,
            proposedCapacity);
        if (violation is not null)
        {
            return violation;
        }

        var draftRows = await LoadDraftRowsForRoomAsync(roomId, cancellationToken);
        return FindViolation(
            draftRows,
            "чернетках",
            groupIdOverride: null,
            studentsCountOverride: null,
            proposedCapacity);
    }

    private async Task<List<RoomPlacementRow>> LoadPublishedRowsForGroupSlotsAsync(
        int groupId,
        CancellationToken cancellationToken)
    {
        var targetSlots = db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.GroupId == groupId
                           && item.RoomId != null
                           && item.LessonType.RequiresRoom)
            .Select(item => new
            {
                item.Date,
                item.StartTime,
                item.EndTime,
                item.RoomId
            });

        return await db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.RoomId != null && item.LessonType.RequiresRoom)
            .Where(item => targetSlots.Any(slot =>
                slot.Date == item.Date
                && slot.StartTime == item.StartTime
                && slot.EndTime == item.EndTime
                && slot.RoomId == item.RoomId))
            .Select(item => new RoomPlacementRow(
                item.Date,
                item.StartTime,
                item.EndTime,
                item.RoomId!.Value,
                item.Room!.Name,
                item.Room.Capacity,
                item.GroupId,
                item.Group.StudentsCount))
            .ToListAsync(cancellationToken);
    }

    private async Task<List<RoomPlacementRow>> LoadDraftRowsForGroupSlotsAsync(
        int groupId,
        CancellationToken cancellationToken)
    {
        var targetSlots = db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.GroupId == groupId
                           && item.RoomId != null
                           && item.LessonType.RequiresRoom)
            .Select(item => new
            {
                item.Date,
                item.StartTime,
                item.EndTime,
                item.RoomId
            });

        return await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.RoomId != null && item.LessonType.RequiresRoom)
            .Where(item => targetSlots.Any(slot =>
                slot.Date == item.Date
                && slot.StartTime == item.StartTime
                && slot.EndTime == item.EndTime
                && slot.RoomId == item.RoomId))
            .Select(item => new RoomPlacementRow(
                item.Date,
                item.StartTime,
                item.EndTime,
                item.RoomId!.Value,
                item.Room!.Name,
                item.Room.Capacity,
                item.GroupId,
                item.Group.StudentsCount))
            .ToListAsync(cancellationToken);
    }

    private Task<List<RoomPlacementRow>> LoadPublishedRowsForRoomAsync(
        int roomId,
        CancellationToken cancellationToken)
        => db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.RoomId == roomId && item.LessonType.RequiresRoom)
            .Select(item => new RoomPlacementRow(
                item.Date,
                item.StartTime,
                item.EndTime,
                roomId,
                item.Room!.Name,
                item.Room.Capacity,
                item.GroupId,
                item.Group.StudentsCount))
            .ToListAsync(cancellationToken);

    private Task<List<RoomPlacementRow>> LoadDraftRowsForRoomAsync(
        int roomId,
        CancellationToken cancellationToken)
        => db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.RoomId == roomId && item.LessonType.RequiresRoom)
            .Select(item => new RoomPlacementRow(
                item.Date,
                item.StartTime,
                item.EndTime,
                roomId,
                item.Room!.Name,
                item.Room.Capacity,
                item.GroupId,
                item.Group.StudentsCount))
            .ToListAsync(cancellationToken);

    private static RoomCapacityViolation? FindViolation(
        IReadOnlyCollection<RoomPlacementRow> rows,
        string source,
        int? groupIdOverride,
        int? studentsCountOverride,
        int? capacityOverride)
    {
        foreach (var slot in rows
                     .GroupBy(row => new
                     {
                         row.Date,
                         row.Start,
                         row.End,
                         row.RoomId
                     })
                     .OrderBy(group => group.Key.Date)
                     .ThenBy(group => group.Key.Start)
                     .ThenBy(group => group.Key.End)
                     .ThenBy(group => group.Key.RoomId))
        {
            var distinctGroups = slot
                .GroupBy(row => row.GroupId)
                .Select(group => group.First())
                .ToList();
            var totalStudents = distinctGroups.Sum(row =>
                (long)(row.GroupId == groupIdOverride
                    ? studentsCountOverride!.Value
                    : row.StudentsCount));
            var first = distinctGroups[0];
            var capacity = capacityOverride ?? first.RoomCapacity;
            if (totalStudents <= capacity)
            {
                continue;
            }

            return new RoomCapacityViolation(
                source,
                first.Date,
                first.Start,
                first.End,
                first.RoomId,
                first.RoomName,
                capacity,
                totalStudents,
                distinctGroups.Count);
        }

        return null;
    }

    private sealed record RoomPlacementRow(
        DateOnly Date,
        TimeOnly Start,
        TimeOnly End,
        int RoomId,
        string RoomName,
        int RoomCapacity,
        int GroupId,
        int StudentsCount);
}

public sealed record RoomCapacityViolation(
    string Source,
    DateOnly Date,
    TimeOnly Start,
    TimeOnly End,
    int RoomId,
    string RoomName,
    int Capacity,
    long StudentsCount,
    int GroupCount)
{
    public string ToMessage()
        => $"У {Source} слот {Date:yyyy-MM-dd} {Start:HH\\:mm}-{End:HH\\:mm} "
           + $"в аудиторії {RoomName} містить {StudentsCount} студентів із {GroupCount} груп "
           + $"при місткості {Capacity}. Спочатку змініть аудиторію, склад потоку або чисельність груп.";
}
