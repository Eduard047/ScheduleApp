using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

internal sealed record TravelConfigurationImpact(int Count, IReadOnlyList<string> Samples)
{
    public string ToMessage()
        => $"Зміна часу переходу зробить недійсними {Count} наявних сусідніх пар занять. "
           + $"Спочатку перенесіть заняття або збільште перерву: {string.Join("; ", Samples)}";
}

internal sealed class TravelConfigurationCapacityException(string message) : Exception(message);

// Перевіряє вплив зміни маршруту на вже збережені заняття.
internal static class TravelConfigurationImpactAnalyzer
{
    internal const int MaxPlacementRowCount = 50_000;
    private const int MaxImpactSampleCount = 10;

    public static async Task<TravelConfigurationImpact> FindNewViolationsAsync(
        AppDbContext db,
        int firstBuildingId,
        int secondBuildingId,
        int beforeMinutes,
        int afterMinutes,
        CancellationToken cancellationToken = default)
    {
        if (afterMinutes <= beforeMinutes)
        {
            return new TravelConfigurationImpact(0, Array.Empty<string>());
        }

        var scheduleRows = await db.ScheduleItems.AsNoTracking()
            .Where(item => item.RoomId != null)
            .Select(item => new
            {
                item.Id,
                item.BatchKey,
                item.Date,
                Start = item.StartTime,
                End = item.EndTime,
                item.GroupId,
                GroupName = item.Group.Name,
                item.ModuleId,
                item.LessonTypeId,
                item.TeacherId,
                TeacherName = item.Teacher != null ? item.Teacher.FullName : null,
                item.LessonType.RequiresRoom,
                item.LessonType.RequiresTeacher,
                item.LessonType.BlocksTeacher,
                BuildingId = item.Room!.BuildingId
            })
            .Take(MaxPlacementRowCount + 1)
            .ToListAsync(cancellationToken);
        if (scheduleRows.Count > MaxPlacementRowCount)
        {
            throw new TravelConfigurationCapacityException(
                $"Перевірка переходів підтримує не більше {MaxPlacementRowCount} записів розкладу та чернеток разом.");
        }
        var remainingDraftCapacity = MaxPlacementRowCount - scheduleRows.Count;
        var draftRows = await db.TeacherDraftItems.AsNoTracking()
            .Where(item => item.RoomId != null)
            .Select(item => new
            {
                item.Id,
                item.BatchKey,
                item.Date,
                Start = item.StartTime,
                End = item.EndTime,
                item.GroupId,
                GroupName = item.Group.Name,
                item.ModuleId,
                item.LessonTypeId,
                item.TeacherId,
                TeacherName = item.Teacher != null ? item.Teacher.FullName : null,
                item.LessonType.RequiresRoom,
                item.LessonType.RequiresTeacher,
                item.LessonType.BlocksTeacher,
                BuildingId = item.Room!.BuildingId
            })
            .Take(remainingDraftCapacity + 1)
            .ToListAsync(cancellationToken);
        if (draftRows.Count > remainingDraftCapacity)
        {
            throw new TravelConfigurationCapacityException(
                $"Перевірка переходів підтримує не більше {MaxPlacementRowCount} записів розкладу та чернеток разом.");
        }
        var placements = scheduleRows
            .Select(item => new TravelPlacement(
                "розклад",
                item.Id,
                item.BatchKey,
                item.Date,
                item.Start,
                item.End,
                item.GroupId,
                item.GroupName,
                item.ModuleId,
                item.LessonTypeId,
                item.TeacherId,
                item.TeacherName,
                item.RequiresRoom,
                item.RequiresTeacher,
                item.BlocksTeacher,
                item.BuildingId))
            .Concat(draftRows.Select(item => new TravelPlacement(
                "чернетка",
                item.Id,
                item.BatchKey,
                item.Date,
                item.Start,
                item.End,
                item.GroupId,
                item.GroupName,
                item.ModuleId,
                item.LessonTypeId,
                item.TeacherId,
                item.TeacherName,
                item.RequiresRoom,
                item.RequiresTeacher,
                item.BlocksTeacher,
                item.BuildingId)))
            .Where(item => item.RequiresRoom)
            .ToList();

        var targetPair = TravelTimePolicy.NormalizePair(firstBuildingId, secondBuildingId);
        var impactCount = 0;
        var samples = new List<string>(MaxImpactSampleCount);
        AddScopeImpacts(
            "групи",
            placements.GroupBy(item => (Id: item.GroupId, Name: item.GroupName)));
        AddScopeImpacts(
            "викладача",
            placements
                .Where(item => item.TeacherId is not null
                               && (item.RequiresTeacher || item.BlocksTeacher))
                .GroupBy(item => (
                    Id: item.TeacherId!.Value,
                    Name: string.IsNullOrWhiteSpace(item.TeacherName)
                        ? $"#{item.TeacherId.Value}"
                        : item.TeacherName!)));

        return new TravelConfigurationImpact(impactCount, samples);

        void AddScopeImpacts(
            string scope,
            IEnumerable<IGrouping<(int Id, string Name), TravelPlacement>> owners)
        {
            foreach (var owner in owners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var day in owner.GroupBy(item => item.Date))
                {
                    var ordered = CollapseLogicalEvents(day)
                        .OrderBy(item => item.Start)
                        .ThenBy(item => item.End)
                        .ThenBy(item => item.Source, StringComparer.Ordinal)
                        .ThenBy(item => item.Id)
                        .ToList();
                    for (var index = 1; index < ordered.Count; index++)
                    {
                        var previous = ordered[index - 1];
                        var current = ordered[index];
                        if (previous.End > current.Start
                            || TravelTimePolicy.NormalizePair(previous.BuildingId, current.BuildingId) != targetPair)
                        {
                            continue;
                        }

                        var availableMinutes = (current.Start.ToTimeSpan() - previous.End.ToTimeSpan()).TotalMinutes;
                        if (availableMinutes < beforeMinutes || availableMinutes >= afterMinutes)
                        {
                            continue;
                        }

                        impactCount++;
                        if (samples.Count < MaxImpactSampleCount)
                        {
                            samples.Add(
                                $"{scope} {owner.Key.Name}, {current.Date:yyyy-MM-dd} "
                                + $"{previous.End:HH\\:mm}→{current.Start:HH\\:mm} "
                                + $"({previous.Source} #{previous.Id} → {current.Source} #{current.Id})");
                        }
                    }
                }
            }
        }
    }

    // Згортає рядки одного спільного заняття, щоб вони не створювали хибну суміжність для викладача.
    private static IEnumerable<TravelPlacement> CollapseLogicalEvents(IEnumerable<TravelPlacement> rows)
    {
        foreach (var row in rows.Where(item => string.IsNullOrWhiteSpace(item.BatchKey)))
        {
            yield return row;
        }
        foreach (var logicalEvent in rows
                     .Where(item => !string.IsNullOrWhiteSpace(item.BatchKey))
                     .GroupBy(item => new
                     {
                         item.Source,
                         item.BatchKey,
                         item.Date,
                         item.Start,
                         item.End,
                         item.ModuleId,
                         item.LessonTypeId,
                         item.TeacherId,
                         item.BuildingId
                     }))
        {
            yield return logicalEvent.OrderBy(item => item.Id).First();
        }
    }

    private sealed record TravelPlacement(
        string Source,
        int Id,
        string? BatchKey,
        DateOnly Date,
        TimeOnly Start,
        TimeOnly End,
        int GroupId,
        string GroupName,
        int ModuleId,
        int LessonTypeId,
        int? TeacherId,
        string? TeacherName,
        bool RequiresRoom,
        bool RequiresTeacher,
        bool BlocksTeacher,
        int BuildingId);
}
