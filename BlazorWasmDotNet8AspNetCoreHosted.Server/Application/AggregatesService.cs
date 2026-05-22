using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

// Сервіс перерахунку агрегованих показників планів і навантажень.
public sealed class AggregatesService
{
    private readonly AppDbContext _db;

    public AggregatesService(AppDbContext db)
    {
        _db = db;
    }

    private static int ScheduledHours(TimeOnly start, TimeOnly end)
    {
        var hours = (end.ToTimeSpan() - start.ToTimeSpan()).TotalHours;
        return Math.Max(1, (int)Math.Ceiling(hours));
    }

    private static Dictionary<TKey, int> BuildCountLookup<TItem, TKey>(
        IEnumerable<TItem> items,
        Func<TItem, TKey> keySelector,
        Func<TItem, int> hoursSelector)
        where TKey : notnull
        => items
            .GroupBy(keySelector)
            .ToDictionary(g => g.Key, g => g.Sum(hoursSelector));

    // Перераховує години для модульних планів і навантаження викладачів.
    public async Task RecalcAsync(
        IEnumerable<(int CourseId, int ModuleId)>? plans = null,
        IEnumerable<(int TeacherId, int CourseId)>? loads = null)
    {
        var lessonTypes = await _db.LessonTypes
            .Select(lt => new { lt.Id, lt.Code, lt.CountInPlan, lt.CountInLoad })
            .ToListAsync();
        var excludePlanIds = lessonTypes
            .Where(lt =>
                !lt.CountInPlan
                || string.Equals(lt.Code, "CANCELED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lt.Code, "RESCHEDULED", StringComparison.OrdinalIgnoreCase))
            .Select(lt => lt.Id)
            .ToHashSet();
        var excludeLoadIds = lessonTypes
            .Where(lt =>
                !lt.CountInLoad
                || string.Equals(lt.Code, "CANCELED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lt.Code, "RESCHEDULED", StringComparison.OrdinalIgnoreCase))
            .Select(lt => lt.Id)
            .ToHashSet();

        if (plans is null)
        {
            var allPlans = await _db.ModulePlans.ToListAsync();
            var courseIds = allPlans.Select(p => p.CourseId).Distinct().ToList();
            var moduleIds = allPlans.Select(p => p.ModuleId).Distinct().ToList();
            var items = await _db.ScheduleItems
                .Where(si => !excludePlanIds.Contains(si.LessonTypeId)
                             && courseIds.Contains(si.Group.CourseId)
                             && moduleIds.Contains(si.ModuleId))
                .Select(si => new { CourseId = si.Group.CourseId, si.ModuleId, si.StartTime, si.EndTime })
                .ToListAsync();
            var counts = BuildCountLookup(
                items,
                item => (item.CourseId, item.ModuleId),
                item => ScheduledHours(item.StartTime, item.EndTime));

            foreach (var plan in allPlans)
            {
                plan.ScheduledHours = counts.GetValueOrDefault((plan.CourseId, plan.ModuleId));
            }
        }
        else
        {
            var keys = plans.Distinct().ToList();
            if (keys.Count > 0)
            {
                var courseIds = keys.Select(k => k.CourseId).Distinct().ToList();
                var moduleIds = keys.Select(k => k.ModuleId).Distinct().ToList();
                var items = await _db.ScheduleItems
                    .Where(si => !excludePlanIds.Contains(si.LessonTypeId)
                                 && courseIds.Contains(si.Group.CourseId)
                                 && moduleIds.Contains(si.ModuleId))
                    .Select(si => new { CourseId = si.Group.CourseId, si.ModuleId, si.StartTime, si.EndTime })
                    .ToListAsync();
                var counts = BuildCountLookup(
                    items,
                    item => (item.CourseId, item.ModuleId),
                    item => ScheduledHours(item.StartTime, item.EndTime));
                var plansToUpdate = await _db.ModulePlans
                    .Where(mp => courseIds.Contains(mp.CourseId) && moduleIds.Contains(mp.ModuleId))
                    .ToListAsync();

                foreach (var plan in plansToUpdate)
                {
                    plan.ScheduledHours = counts.GetValueOrDefault((plan.CourseId, plan.ModuleId));
                }
            }
        }

        if (loads is null)
        {
            var activeLoads = await _db.TeacherCourseLoads.Where(l => l.IsActive).ToListAsync();
            var teacherIds = activeLoads.Select(l => l.TeacherId).Distinct().ToList();
            var courseIds = activeLoads.Select(l => l.CourseId).Distinct().ToList();
            var items = await _db.ScheduleItems
                .Where(si => si.TeacherId != null
                             && !excludeLoadIds.Contains(si.LessonTypeId)
                             && teacherIds.Contains(si.TeacherId!.Value)
                             && courseIds.Contains(si.Group.CourseId))
                .Select(si => new { TeacherId = si.TeacherId!.Value, CourseId = si.Group.CourseId, si.StartTime, si.EndTime })
                .ToListAsync();
            var counts = BuildCountLookup(
                items,
                item => (item.TeacherId, item.CourseId),
                item => ScheduledHours(item.StartTime, item.EndTime));

            foreach (var load in activeLoads)
            {
                load.ScheduledHours = counts.GetValueOrDefault((load.TeacherId, load.CourseId));
            }

            await _db.TeacherCourseLoads
                .Where(l => !l.IsActive && l.ScheduledHours != 0)
                .ExecuteUpdateAsync(setters => setters.SetProperty(l => l.ScheduledHours, 0));
        }
        else
        {
            var keys = loads.Distinct().ToList();
            if (keys.Count > 0)
            {
                var teacherIds = keys.Select(k => k.TeacherId).Distinct().ToList();
                var courseIds = keys.Select(k => k.CourseId).Distinct().ToList();
                var items = await _db.ScheduleItems
                    .Where(si => si.TeacherId != null
                                 && !excludeLoadIds.Contains(si.LessonTypeId)
                                 && teacherIds.Contains(si.TeacherId!.Value)
                                 && courseIds.Contains(si.Group.CourseId))
                    .Select(si => new { TeacherId = si.TeacherId!.Value, CourseId = si.Group.CourseId, si.StartTime, si.EndTime })
                    .ToListAsync();
                var counts = BuildCountLookup(
                    items,
                    item => (item.TeacherId, item.CourseId),
                    item => ScheduledHours(item.StartTime, item.EndTime));
                var loadsToUpdate = await _db.TeacherCourseLoads
                    .Where(l => l.IsActive && teacherIds.Contains(l.TeacherId) && courseIds.Contains(l.CourseId))
                    .ToListAsync();

                foreach (var load in loadsToUpdate)
                {
                    load.ScheduledHours = counts.GetValueOrDefault((load.TeacherId, load.CourseId));
                }

                await _db.TeacherCourseLoads
                    .Where(l => !l.IsActive
                                && teacherIds.Contains(l.TeacherId)
                                && courseIds.Contains(l.CourseId)
                                && l.ScheduledHours != 0)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(l => l.ScheduledHours, 0));
            }
        }

        await _db.SaveChangesAsync();
    }
}
