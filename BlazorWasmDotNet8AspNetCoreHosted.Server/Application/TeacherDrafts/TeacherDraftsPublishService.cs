using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed class TeacherDraftsPublishService
{
    private readonly AppDbContext _db;
    private readonly RulesService _rules;

    public TeacherDraftsPublishService(AppDbContext db, RulesService rules)
    {
        _db = db;
        _rules = rules;
    }

    public async Task<IActionResult> ApproveWeekAsync(ApproveWeekRequest r)
    {
        var start = r.WeekStart;
        var end = start.AddDays(7);

        var rows = await _db.TeacherDraftItems
            .Where(x => x.TeacherId == r.TeacherId && x.Date >= start && x.Date < end)
            .ToListAsync();

        foreach (var x in rows) x.Status = DraftStatus.Published;

        await _db.SaveChangesAsync();
        return new OkResult();
    }

    public async Task<ActionResult<PublishWeekResults>> PublishWeekAsync(PublishWeekRequest r)
    {
        var start = r.WeekStart;
        var end = start.AddDays(7);

        var q = _db.TeacherDraftItems.Where(x => x.Date >= start && x.Date < end);
        if (r.TeacherId is int tid) q = q.Where(x => x.TeacherId == tid);

        var drafts = await q
            .Include(x => x.Group)
            .ToListAsync();

        var lessonTypeIds = drafts.Select(d => d.LessonTypeId).Distinct().ToList();
        var lessonTypeRoomMap = await _db.LessonTypes.AsNoTracking()
            .Where(lt => lessonTypeIds.Contains(lt.Id))
            .ToDictionaryAsync(lt => lt.Id, lt => lt.RequiresRoom);

        var calendar = await _db.CalendarExceptions.AsNoTracking()
            .Where(x => x.Date >= start && x.Date < end)
            .ToListAsync();

        int created = 0, skipped = 0;
        var warnings = new List<string>();
        var publishedIds = new List<int>();

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        foreach (var d in drafts)
        {
            var isWeekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var courseId = d.Group?.CourseId;
            var scoped = TeacherDraftsHelpers.ResolveCalendarOverride(calendar, d.Date, courseId, d.GroupId);
            var isWorking = scoped ?? !isWeekend;
            var overrideNonWorking = !isWorking;

            var requiresRoom = lessonTypeRoomMap.TryGetValue(d.LessonTypeId, out var reqRoom) ? reqRoom : true;
            var normalizedRoomId = requiresRoom ? d.RoomId : null;

            var req = new UpsertScheduleItemRequest(
                Id: null,
                Date: d.Date,
                TimeStart: d.StartTime.ToString("HH:mm"),
                TimeEnd: d.EndTime.ToString("HH:mm"),
                GroupId: d.GroupId,
                ModuleId: d.ModuleId,
                TeacherId: d.TeacherId,
                RoomId: normalizedRoomId,
                LessonTypeId: d.LessonTypeId,
                IsLocked: false,
                OverrideNonWorkingDay: overrideNonWorking
            );

            var (errors, warn) = await _rules.ValidateUpsertAsync(req);
            if (errors.Count > 0)
            {
                skipped++;
                warnings.Add($"[{d.Date:yyyy-MM-dd} {d.StartTime:HH\\:mm}-{d.EndTime:HH\\:mm}] {string.Join("; ", errors)}");
                continue;
            }

            var item = new ScheduleItem
            {
                Date = d.Date,
                DayOfWeek = d.DayOfWeek,
                StartTime = d.StartTime,
                EndTime = d.EndTime,
                GroupId = d.GroupId,
                ModuleId = d.ModuleId,
                RoomId = normalizedRoomId,
                TeacherId = d.TeacherId,
                ModuleTopicId = d.ModuleTopicId,
                LessonTypeId = d.LessonTypeId,
                IsLocked = false,
                IsSelfStudy = d.IsSelfStudy
            };
            _db.ScheduleItems.Add(item);
            created++;
            publishedIds.Add(d.Id);
        }

        await _db.SaveChangesAsync();

        if (publishedIds.Count > 0)
            await _db.TeacherDraftItems
                .Where(x => publishedIds.Contains(x.Id))
                .ExecuteDeleteAsync();

        var publishedDrafts = drafts.Where(d => publishedIds.Contains(d.Id)).ToList();
        var affectedPlans = publishedDrafts
            .Select(x => new { x.ModuleId, CourseId = x.Group.CourseId })
            .Distinct()
            .Select(x => (x.CourseId, x.ModuleId));
        var affectedLoads = publishedDrafts
            .Where(x => x.TeacherId != null)
            .Select(x => new { TeacherId = x.TeacherId!.Value, CourseId = x.Group.CourseId })
            .Distinct()
            .Select(x => (x.TeacherId, x.CourseId));

        await RecalcAggregatesAsync(affectedPlans, affectedLoads);

        await tx.CommitAsync();
        return new OkObjectResult(new PublishWeekResults(created, skipped, warnings));
    }

    private async Task RecalcAggregatesAsync(
        IEnumerable<(int CourseId, int ModuleId)> plans,
        IEnumerable<(int TeacherId, int CourseId)> loads)
    {
        var lessonTypes = await _db.LessonTypes
            .Select(lt => new { lt.Id, lt.Code, lt.CountInPlan, lt.CountInLoad })
            .ToListAsync();

        var excludePlanIds = lessonTypes
            .Where(lt =>
                !lt.CountInPlan
                || string.Equals(lt.Code, "CANCELED", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(lt.Code, "RESCHEDULED", System.StringComparison.OrdinalIgnoreCase))
            .Select(lt => lt.Id)
            .ToHashSet();

        var excludeLoadIds = lessonTypes
            .Where(lt =>
                !lt.CountInLoad
                || string.Equals(lt.Code, "CANCELED", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(lt.Code, "RESCHEDULED", System.StringComparison.OrdinalIgnoreCase))
            .Select(lt => lt.Id)
            .ToHashSet();

        var planKeys = plans.Distinct().ToList();
        if (planKeys.Count > 0)
        {
            var cIds = planKeys.Select(k => k.CourseId).Distinct().ToList();
            var mIds = planKeys.Select(k => k.ModuleId).Distinct().ToList();

            var counts = await _db.ScheduleItems
                .Include(si => si.Group)
                .Where(si => !excludePlanIds.Contains(si.LessonTypeId)
                             && cIds.Contains(si.Group.CourseId)
                             && mIds.Contains(si.ModuleId))
                .GroupBy(si => new { CourseId = si.Group.CourseId, si.ModuleId })
                .Select(g => new { g.Key.CourseId, g.Key.ModuleId, GCount = g.Count() })
                .ToListAsync();

            var plansToUpdate = await _db.ModulePlans
                .Where(mp => cIds.Contains(mp.CourseId) && mIds.Contains(mp.ModuleId))
                .ToListAsync();

            foreach (var p in plansToUpdate)
                p.ScheduledHours = counts.FirstOrDefault(c => c.CourseId == p.CourseId && c.ModuleId == p.ModuleId)?.GCount ?? 0;
        }

        var loadKeys = loads.Distinct().ToList();
        if (loadKeys.Count > 0)
        {
            var tIds = loadKeys.Select(k => k.TeacherId).Distinct().ToList();
            var cIds = loadKeys.Select(k => k.CourseId).Distinct().ToList();

            var counts = await _db.ScheduleItems
                .Include(si => si.Group)
                .Where(si => si.TeacherId != null
                             && !excludeLoadIds.Contains(si.LessonTypeId)
                             && tIds.Contains(si.TeacherId!.Value)
                             && cIds.Contains(si.Group.CourseId))
                .GroupBy(si => new { TeacherId = si.TeacherId!.Value, si.Group.CourseId })
                .Select(g => new { g.Key.TeacherId, g.Key.CourseId, GCount = g.Count() })
                .ToListAsync();

            var loadsToUpdate = await _db.TeacherCourseLoads
                .Where(l => l.IsActive && tIds.Contains(l.TeacherId) && cIds.Contains(l.CourseId))
                .ToListAsync();

            foreach (var l in loadsToUpdate)
                l.ScheduledHours = counts.FirstOrDefault(c => c.TeacherId == l.TeacherId && c.CourseId == l.CourseId)?.GCount ?? 0;
        }

        await _db.SaveChangesAsync();
    }
}
