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
            var cIds = allPlans.Select(p => p.CourseId).Distinct().ToList();
            var mIds = allPlans.Select(p => p.ModuleId).Distinct().ToList();
            var counts = await _db.ScheduleItems
                .Include(si => si.Group)
                .Where(si => !excludePlanIds.Contains(si.LessonTypeId)
                             && cIds.Contains(si.Group.CourseId)
                             && mIds.Contains(si.ModuleId))
                .GroupBy(si => new { CourseId = si.Group.CourseId, si.ModuleId })
                .Select(g => new { g.Key.CourseId, g.Key.ModuleId, C = g.Count() })
                .ToListAsync();
            foreach (var p in allPlans)
                p.ScheduledHours = counts.FirstOrDefault(c => c.CourseId == p.CourseId && c.ModuleId == p.ModuleId)?.C ?? 0;
        }
        else
        {
            var keys = plans.Distinct().ToList();
            if (keys.Count > 0)
            {
                var cIds = keys.Select(k => k.CourseId).Distinct().ToList();
                var mIds = keys.Select(k => k.ModuleId).Distinct().ToList();
                var counts = await _db.ScheduleItems
                    .Include(si => si.Group)
                    .Where(si => !excludePlanIds.Contains(si.LessonTypeId)
                                 && cIds.Contains(si.Group.CourseId)
                                 && mIds.Contains(si.ModuleId))
                    .GroupBy(si => new { CourseId = si.Group.CourseId, si.ModuleId })
                    .Select(g => new { g.Key.CourseId, g.Key.ModuleId, C = g.Count() })
                    .ToListAsync();
                var plansToUpdate = await _db.ModulePlans
                    .Where(mp => cIds.Contains(mp.CourseId) && mIds.Contains(mp.ModuleId))
                    .ToListAsync();
                foreach (var p in plansToUpdate)
                    p.ScheduledHours = counts.FirstOrDefault(c => c.CourseId == p.CourseId && c.ModuleId == p.ModuleId)?.C ?? 0;
            }
        }
        if (loads is null)
        {
            var activeLoads = await _db.TeacherCourseLoads.Where(l => l.IsActive).ToListAsync();
            var tIds = activeLoads.Select(l => l.TeacherId).Distinct().ToList();
            var cIds = activeLoads.Select(l => l.CourseId).Distinct().ToList();
            var counts = await _db.ScheduleItems
                .Include(si => si.Group)
                .Where(si => si.TeacherId != null
                             && !excludeLoadIds.Contains(si.LessonTypeId)
                             && tIds.Contains(si.TeacherId!.Value)
                             && cIds.Contains(si.Group.CourseId))
                .GroupBy(si => new { TeacherId = si.TeacherId!.Value, si.Group.CourseId })
                .Select(g => new { g.Key.TeacherId, g.Key.CourseId, C = g.Count() })
                .ToListAsync();
            foreach (var l in activeLoads)
                l.ScheduledHours = counts.FirstOrDefault(c => c.TeacherId == l.TeacherId && c.CourseId == l.CourseId)?.C ?? 0;
            var inactive = await _db.TeacherCourseLoads.Where(l => !l.IsActive).ToListAsync();
            foreach (var l in inactive) l.ScheduledHours = 0;
        }
        else
        {
            var keys = loads.Distinct().ToList();
            if (keys.Count > 0)
            {
                var tIds = keys.Select(k => k.TeacherId).Distinct().ToList();
                var cIds = keys.Select(k => k.CourseId).Distinct().ToList();
                var counts = await _db.ScheduleItems
                    .Include(si => si.Group)
                    .Where(si => si.TeacherId != null
                                 && !excludeLoadIds.Contains(si.LessonTypeId)
                                 && tIds.Contains(si.TeacherId!.Value)
                                 && cIds.Contains(si.Group.CourseId))
                    .GroupBy(si => new { TeacherId = si.TeacherId!.Value, si.Group.CourseId })
                    .Select(g => new { g.Key.TeacherId, g.Key.CourseId, C = g.Count() })
                    .ToListAsync();
                var loadsToUpdate = await _db.TeacherCourseLoads
                    .Where(l => l.IsActive && tIds.Contains(l.TeacherId) && cIds.Contains(l.CourseId))
                    .ToListAsync();
                foreach (var l in loadsToUpdate)
                    l.ScheduledHours = counts.FirstOrDefault(c => c.TeacherId == l.TeacherId && c.CourseId == l.CourseId)?.C ?? 0;
                var inactive = await _db.TeacherCourseLoads
                    .Where(l => !l.IsActive && tIds.Contains(l.TeacherId) && cIds.Contains(l.CourseId))
                    .ToListAsync();
                foreach (var l in inactive) l.ScheduledHours = 0;
            }
        }
        await _db.SaveChangesAsync();
    }
}
