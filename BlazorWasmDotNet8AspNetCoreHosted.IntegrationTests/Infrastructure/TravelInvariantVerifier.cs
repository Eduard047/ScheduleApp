using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests.Infrastructure;

internal static class TravelInvariantVerifier
{
    private sealed record PlacementRow(
        DateOnly Date,
        TimeOnly Start,
        TimeOnly End,
        int GroupId,
        string GroupName,
        int? TeacherId,
        string? TeacherName,
        int? RoomId,
        int? BuildingId,
        bool RequiresRoom);

    public static async Task<IReadOnlyList<string>> FindViolationsAsync(AppDbContext db, int courseId, DateOnly from, DateOnly to)
    {
        var travelMap = await db.BuildingTravels.AsNoTracking()
            .ToDictionaryAsync(x => (x.FromBuildingId, x.ToBuildingId), x => x.Minutes);

        int TravelMinutes(int fromBuildingId, int toBuildingId)
            => TravelTimePolicy.Resolve(travelMap, fromBuildingId, toBuildingId);

        var teacherNames = await db.Teachers.AsNoTracking()
            .Select(x => new { x.Id, x.FullName })
            .ToDictionaryAsync(x => x.Id, x => string.IsNullOrWhiteSpace(x.FullName) ? $"#{x.Id}" : x.FullName);

        var scheduleRows = await db.ScheduleItems.AsNoTracking()
            .Where(x => x.Date >= from && x.Date <= to && x.Group.CourseId == courseId)
            .Select(x => new
            {
                x.Date,
                x.StartTime,
                x.EndTime,
                x.GroupId,
                GroupName = x.Group.Name,
                x.TeacherId,
                x.RoomId,
                BuildingId = x.Room != null ? (int?)x.Room.BuildingId : null,
                x.LessonType.RequiresRoom
            })
            .ToListAsync();

        var draftRows = await db.TeacherDraftItems.AsNoTracking()
            .Where(x => x.Date >= from && x.Date <= to && x.Group.CourseId == courseId)
            .Select(x => new
            {
                x.Date,
                x.StartTime,
                x.EndTime,
                x.GroupId,
                GroupName = x.Group.Name,
                x.TeacherId,
                x.RoomId,
                BuildingId = x.Room != null ? (int?)x.Room.BuildingId : null,
                x.LessonType.RequiresRoom
            })
            .ToListAsync();

        var placements = scheduleRows
            .Concat(draftRows)
            .Select(x => new PlacementRow(
                x.Date,
                x.StartTime,
                x.EndTime,
                x.GroupId,
                x.GroupName,
                x.TeacherId,
                x.TeacherId is int teacherId && teacherNames.TryGetValue(teacherId, out var teacherName)
                    ? teacherName
                    : null,
                x.RoomId,
                x.BuildingId,
                x.RequiresRoom))
            .ToList();

        var violations = new List<string>();
        violations.AddRange(CheckScope("групи", placements.GroupBy(x => (x.GroupId, x.GroupName))));
        violations.AddRange(CheckTeacherScope(placements.Where(x => x.TeacherId is not null).GroupBy(x => (x.TeacherId!.Value, x.TeacherName ?? $"#{x.TeacherId.Value}"))));
        return violations;

        IEnumerable<string> CheckScope(
            string scopeLabel,
            IEnumerable<IGrouping<(int Id, string Name), PlacementRow>> groups)
        {
            foreach (var group in groups)
            {
                foreach (var violation in CheckOrderedPlacements($"{scopeLabel} {group.Key.Name}", group))
                {
                    yield return violation;
                }
            }
        }

        IEnumerable<string> CheckTeacherScope(
            IEnumerable<IGrouping<(int Id, string Name), PlacementRow>> groups)
        {
            foreach (var group in groups)
            {
                foreach (var violation in CheckOrderedPlacements($"викладача {group.Key.Name}", group))
                {
                    yield return violation;
                }
            }
        }

        IEnumerable<string> CheckOrderedPlacements(string scopeName, IEnumerable<PlacementRow> rows)
        {
            foreach (var dayGroup in rows
                         .GroupBy(x => x.Date)
                         .OrderBy(g => g.Key))
            {
                var ordered = dayGroup
                    .Where(x => x.RequiresRoom)
                    .OrderBy(x => x.Start)
                    .ThenBy(x => x.End)
                    .ToList();

                for (var i = 1; i < ordered.Count; i++)
                {
                    var prev = ordered[i - 1];
                    var current = ordered[i];
                    if (prev.BuildingId is null || current.BuildingId is null)
                    {
                        continue;
                    }

                    if (prev.End > current.Start)
                    {
                        continue;
                    }

                    if (prev.BuildingId == current.BuildingId)
                    {
                        continue;
                    }

                    var need = TravelMinutes(prev.BuildingId.Value, current.BuildingId.Value);
                    var gap = (current.Start.ToTimeSpan() - prev.End.ToTimeSpan()).TotalMinutes;
                    if (gap < need)
                    {
                        yield return $"Порушено перехід для {scopeName} на {current.Date:yyyy-MM-dd}: між {prev.Start:HH\\:mm}-{prev.End:HH\\:mm} і {current.Start:HH\\:mm}-{current.End:HH\\:mm} потрібно {need} хв, доступно лише {gap:N0} хв.";
                    }
                }
            }
        }
    }
}
