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

// Сервіс публікації чернеток у офіційний розклад.
public sealed class TeacherDraftsPublishService
{
    private readonly AppDbContext _db;
    private readonly RulesService _rules;
    private readonly AggregatesService _aggregates;
    public TeacherDraftsPublishService(AppDbContext db, RulesService rules, AggregatesService aggregates)
    {
        _db = db;
        _rules = rules;
        _aggregates = aggregates;
    }
    // Позначає всі чернетки викладача за тиждень як опубліковані.
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
    // Публікує чернетки тижня в офіційний розклад із валідацією.
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
        await _aggregates.RecalcAsync(affectedPlans, affectedLoads);
        await tx.CommitAsync();
        return new OkObjectResult(new PublishWeekResults(created, skipped, warnings));
    }

}
