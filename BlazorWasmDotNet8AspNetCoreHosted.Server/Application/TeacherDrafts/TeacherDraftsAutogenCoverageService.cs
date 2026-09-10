using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

// Незалежно перечитує календар і результат: лічильники пошуку тут не використовуються.
public sealed class TeacherDraftsAutogenCoverageService(AppDbContext db)
{
    public async Task<AutoGenCoverageDto> MeasureAsync(
        IReadOnlyCollection<int> groupIds,
        DateOnly from,
        DateOnly to,
        WeekPreset days,
        IReadOnlyDictionary<int, int>? moduleHours,
        bool searchLimitReached,
        CancellationToken cancellationToken = default)
    {
        var groups = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id))
            .OrderBy(g => g.Id).ToListAsync(cancellationToken);
        var courseIds = groups.Select(g => g.CourseId).Distinct().ToArray();
        var slots = await db.TimeSlots.AsNoTracking()
            .Where(s => s.CourseId == null || courseIds.Contains(s.CourseId.Value)).ToListAsync(cancellationToken);
        var lunches = await db.LunchConfigs.AsNoTracking()
            .Where(s => s.CourseId == null || courseIds.Contains(s.CourseId.Value)).ToListAsync(cancellationToken);
        var calendar = await db.CalendarExceptions.AsNoTracking()
            .Where(c => c.Date >= from && c.Date <= to
                && (c.CourseId == null || courseIds.Contains(c.CourseId.Value))
                && (c.GroupId == null || groupIds.Contains(c.GroupId.Value)))
            .OrderBy(c => c.Id).ToListAsync(cancellationToken);
        var types = await db.LessonTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, cancellationToken);
        var storedDrafts = await db.TeacherDraftItems.AsNoTracking()
            .Where(d => groupIds.Contains(d.GroupId) && d.Date >= from && d.Date <= to)
            .OrderBy(d => d.Id).Take(AutoGenWorkloadLimits.MaxCalendarSlots + 1)
            .ToListAsync(cancellationToken);
        if (storedDrafts.Count > AutoGenWorkloadLimits.MaxCalendarSlots)
            throw new AutoGenJobValidationException("Забагато наявних чернеток для перевірки повноти. Зменште діапазон або кількість груп.");

        // Preview ще може мати незбережені зміни. Накладаємо їх на знімок БД без запису.
        db.ChangeTracker.DetectChanges();
        var edits = db.ChangeTracker.Entries<TeacherDraftItem>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        var replacedIds = edits.Where(e => e.State != EntityState.Added).Select(e => e.Entity.Id).ToHashSet();
        var drafts = storedDrafts.Where(d => !replacedIds.Contains(d.Id))
            .Concat(edits.Where(e => e.State != EntityState.Deleted).Select(e => e.Entity))
            .Where(d => groupIds.Contains(d.GroupId) && d.Date >= from && d.Date <= to && d.Status != DraftStatus.Published)
            .Select(d => new Lesson(d.GroupId, d.ModuleId, d.Date, d.StartTime, d.EndTime,
                d.LessonTypeId, d.ModuleTopicId, d.TeacherId, d.RoomId, d.IsSelfStudy, "draft", d.BatchKey));
        var published = await db.ScheduleItems.AsNoTracking()
            .Where(d => groupIds.Contains(d.GroupId) && d.Date >= from && d.Date <= to)
            .OrderBy(d => d.Id).Take(AutoGenWorkloadLimits.MaxCalendarSlots + 1)
            .Select(d => new Lesson(d.GroupId, d.ModuleId, d.Date, d.StartTime, d.EndTime,
                d.LessonTypeId, d.ModuleTopicId, d.TeacherId, d.RoomId, d.IsSelfStudy, "schedule", d.BatchKey))
            .ToListAsync(cancellationToken);
        if (published.Count > AutoGenWorkloadLimits.MaxCalendarSlots)
            throw new AutoGenJobValidationException("Забагато опублікованих занять для перевірки повноти. Зменште діапазон або кількість груп.");
        var workload = drafts.Concat(published)
            .Where(d => types.TryGetValue(d.TypeId, out var type)
                        && !LessonTypeOccupancyPolicy.IsExcludedFromAutogenWorkload(type.Code))
            .ToList();
        static Lesson Representative(IEnumerable<Lesson> rows) => rows.First() with
        {
            TeacherId = rows.Select(d => d.TeacherId).FirstOrDefault(id => id is not null),
            RoomId = rows.Select(d => d.RoomId).FirstOrDefault(id => id is not null)
        };
        var lessons = workload.Where(d => !string.IsNullOrWhiteSpace(d.BatchKey))
            .GroupBy(d => (d.Source, d.BatchKey, d.GroupId, d.ModuleId, d.Date, d.Start, d.End, d.TypeId))
            .Select(Representative).ToList();
        // Співвикладачі старої події рахуються один раз, однакові дублікати залишаються видимими.
        foreach (var legacy in workload.Where(d => string.IsNullOrWhiteSpace(d.BatchKey))
                     .GroupBy(d => (d.Source, d.GroupId, d.ModuleId, d.Date, d.Start, d.End, d.TypeId, d.RoomId, d.IsSelfStudy)))
        {
            if (legacy.Select(d => (d.TopicId, d.TeacherId)).Distinct().Skip(1).Any()) lessons.Add(Representative(legacy));
            else lessons.AddRange(legacy);
        }
        var byGroupDay = lessons.GroupBy(d => (d.GroupId, d.Date)).ToDictionary(g => g.Key, g => g.ToList());
        var actualByModule = lessons.GroupBy(d => (d.GroupId, d.ModuleId)).ToDictionary(g => g.Key, g => g.Count());
        var groupResults = new List<AutoGenCoverageGroup>();
        var weekResults = new Dictionary<DateOnly, AutoGenCoverageWeek>();
        var moduleResults = new Dictionary<int, AutoGenCoverageModule>();
        var excludedDays = 0;
        var demandKnown = moduleHours is { Count: > 0 };
        var demand = moduleHours ?? new Dictionary<int, int>();
        var resolved = courseIds.ToDictionary(id => id, id => TimeSlotsResolver.ResolveForWeek(slots, id, lunches));
        foreach (var group in groups)
        {
            var available = 0;
            var occupied = 0;
            var windows = 0;
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsAllowed(date.DayOfWeek, days)
                    || TeacherDraftsHelpers.ResolveCalendarOverride(calendar, date, group.CourseId, group.Id) == false)
                {
                    excludedDays++;
                    continue;
                }
                var daySlots = resolved[group.CourseId][date.DayOfWeek].Slots
                    .DistinctBy(s => (s.Start, s.End)).OrderBy(s => s.Start).ToArray();
                var dayLessons = byGroupDay.GetValueOrDefault((group.Id, date)) ?? new List<Lesson>();
                // Слот повний лише коли заняття покриває його цілком; частковий перетин не приховує прогалину.
                var filled = daySlots.Select(s => dayLessons.Any(d => d.Start <= s.Start && d.End >= s.End)).ToArray();
                var first = Array.FindIndex(filled, f => f);
                var last = Array.FindLastIndex(filled, f => f);
                var dayWindows = first < 0 ? 0 : filled.Skip(first).Take(last - first + 1).Count(f => !f);
                available += filled.Length;
                occupied += filled.Count(f => f);
                windows += dayWindows;
                var monday = DateHelpers.StartOfWeek(date);
                var week = weekResults.GetValueOrDefault(monday) ?? new AutoGenCoverageWeek(monday, 0, 0, 0);
                weekResults[monday] = week with
                {
                    AvailableSlots = week.AvailableSlots + filled.Length,
                    OccupiedSlots = week.OccupiedSlots + filled.Count(f => f),
                    InternalGapSlots = week.InternalGapSlots + dayWindows
                };
            }
            var missing = 0;
            var over = 0;
            foreach (var (moduleId, requested) in demand.OrderBy(d => d.Key))
            {
                var actual = actualByModule.GetValueOrDefault((group.Id, moduleId));
                var moduleMissing = Math.Max(0, requested - actual);
                var moduleOver = Math.Max(0, actual - requested);
                missing += moduleMissing;
                over += moduleOver;
                var module = moduleResults.GetValueOrDefault(moduleId) ?? new AutoGenCoverageModule(moduleId, 0, 0, 0, 0);
                moduleResults[moduleId] = module with
                {
                    RequestedLessons = module.RequestedLessons + requested,
                    ScheduledLessons = module.ScheduledLessons + actual,
                    MissingLessons = module.MissingLessons + moduleMissing,
                    OverplannedLessons = module.OverplannedLessons + moduleOver
                };
            }
            var incomplete = lessons.Count(d => d.GroupId == group.Id && !d.IsSelfStudy
                && (types[d.TypeId].RequiresTeacher && d.TeacherId is null
                    || types[d.TypeId].RequiresRoom && d.RoomId is null));
            groupResults.Add(new AutoGenCoverageGroup(group.Id, group.Name, available, occupied,
                available - occupied, windows, demand.Values.Sum(), missing, over, incomplete));
        }
        var totalEmpty = groupResults.Sum(g => g.EmptySlots);
        var totalMissing = groupResults.Sum(g => g.MissingLessons);
        var totalOver = groupResults.Sum(g => g.OverplannedLessons);
        var totalIncomplete = groupResults.Sum(g => g.IncompleteLessons);
        var complete = demandKnown && totalMissing == 0 && totalOver == 0 && totalIncomplete == 0;
        var status = complete
            ? totalEmpty == 0 ? AutoGenCoverageStatus.FullyOccupied : AutoGenCoverageStatus.CurriculumComplete
            : searchLimitReached ? AutoGenCoverageStatus.SearchLimited : AutoGenCoverageStatus.Partial;
        return new AutoGenCoverageDto(status, demandKnown, groupResults.Sum(g => g.AvailableSlots),
            groupResults.Sum(g => g.OccupiedSlots), totalEmpty, groupResults.Sum(g => g.InternalGapSlots),
            groupResults.Sum(g => g.RequestedLessons), moduleResults.Values.Sum(m => m.ScheduledLessons),
            totalMissing, totalOver, totalIncomplete, excludedDays, searchLimitReached,
            groupResults, weekResults.Values.OrderBy(w => w.WeekStart).ToList(), moduleResults.Values.ToList());
    }

    private static bool IsAllowed(DayOfWeek day, WeekPreset days)
        => days == WeekPreset.MonSun || day != DayOfWeek.Sunday && (days == WeekPreset.MonSat || day != DayOfWeek.Saturday);

    private sealed record Lesson(int GroupId, int ModuleId, DateOnly Date, TimeOnly Start, TimeOnly End,
        int TypeId, int? TopicId, int? TeacherId, int? RoomId, bool IsSelfStudy, string Source, string? BatchKey);
}
