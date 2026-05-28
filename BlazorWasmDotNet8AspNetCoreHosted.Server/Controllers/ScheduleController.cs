using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
// Контролер для управління основним розкладом
public class ScheduleController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly RulesService _rules;
    private readonly AggregatesService _aggregates;
    public ScheduleController(AppDbContext db, RulesService rules, AggregatesService aggregates)
    {
        _db = db;
        _rules = rules;
        _aggregates = aggregates;
    }
    private sealed record BusySlot(
        int GroupId,
        int? TeacherId,
        int? RoomId,
        DateOnly Date,
        TimeOnly StartTime,
        TimeOnly EndTime,
        int? BuildingId,
        int ModuleId,
        int LessonTypeId
    );
    private static bool TryParseClock(string value, out TimeOnly time)
        => TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    [HttpGet]
    // Повертає розклад за тиждень із фільтрами.
    public async Task<IReadOnlyList<ScheduleItemDto>> Get(
        [FromQuery] DateOnly weekStart,
        [FromQuery] int? courseId,
        [FromQuery] int? groupId,
        [FromQuery] int? teacherId,
        [FromQuery] int? roomId)
    {
        var weekEnd = weekStart.AddDays(7);
        var q = _db.ScheduleItems
            .AsNoTracking()
            .Where(x => x.Date >= weekStart && x.Date < weekEnd)
            .AsQueryable();
        if (courseId is int cid) q = q.Where(x => x.Group.CourseId == cid);
        if (groupId is int gidFilter) q = q.Where(x => x.GroupId == gidFilter);
        if (teacherId is int tid) q = q.Where(x => x.TeacherId == tid);
        if (roomId is int rid) q = q.Where(x => x.RoomId == rid);
        var items = await q
            .OrderBy(x => x.Date)
            .ThenBy(x => x.StartTime)
            .Select(x => new
            {
                x.Id,
                x.Date,
                x.StartTime,
                x.EndTime,
                x.DayOfWeek,
                GroupName = x.Group.Name,
                x.GroupId,
                ModuleTitle = x.Module.Title,
                x.ModuleId,
                TeacherName = x.Teacher != null ? x.Teacher.FullName : "",
                x.TeacherId,
                RoomName = x.Room != null ? x.Room.Name : "",
                x.RoomId,
                BuildingName = x.Room != null && x.Room.Building != null ? x.Room.Building.Name : "",
                BuildingId = x.Room != null ? (int?)x.Room.BuildingId : null,
                x.LessonTypeId,
                LessonTypeCode = x.LessonType.Code.ToString(),
                LessonTypeName = x.LessonType.Name,
                x.LessonType.RequiresRoom,
                LessonTypeCss = x.LessonType.CssKey,
                x.IsLocked
            })
            .ToListAsync();
        var uk = new CultureInfo("uk-UA");
        return items.Select(item =>
        {
            var lessonTypeCode = (item.LessonTypeCode ?? string.Empty).ToUpperInvariant();
            var isBreak = string.Equals(lessonTypeCode, "BREAK", StringComparison.OrdinalIgnoreCase);
            var isCanceled = string.Equals(lessonTypeCode, "CANCELED", StringComparison.OrdinalIgnoreCase);
            var isRescheduled = string.Equals(lessonTypeCode, "RESCHEDULED", StringComparison.OrdinalIgnoreCase);
            var requiresRoom = item.RequiresRoom;
            if ((isCanceled || isRescheduled) && item.RoomId is not null)
            {
                requiresRoom = true;
            }

            return new ScheduleItemDto(
                Id: item.Id,
                Date: item.Date,
                TimeStart: item.StartTime.ToString("HH\\:mm"),
                TimeEnd: item.EndTime.ToString("HH\\:mm"),
                DayName: item.Date.ToDateTime(TimeOnly.MinValue).ToString("dddd", uk),
                DayNumber: (int)item.DayOfWeek,
                Group: item.GroupName,
                GroupId: item.GroupId,
                Module: isBreak ? "Перерва" : item.ModuleTitle,
                ModuleId: item.ModuleId,
                Teacher: isBreak ? "" : item.TeacherName,
                TeacherId: item.TeacherId,
                Room: !isBreak && requiresRoom ? item.RoomName : "",
                RoomId: requiresRoom ? item.RoomId : null,
                Building: !isBreak && requiresRoom ? item.BuildingName : "",
                BuildingId: requiresRoom ? item.BuildingId : null,
                RequiresRoom: requiresRoom,
                LessonTypeId: item.LessonTypeId,
                LessonTypeCode: lessonTypeCode,
                LessonTypeName: item.LessonTypeName,
                IsLocked: item.IsLocked,
                LessonTypeCss: item.LessonTypeCss ?? (isBreak ? "brk" : null)
            );
        }).ToList();
    }
    [HttpPost("upsert")]
    // Створює або оновлює пару в розкладі з перевіркою правил.
    public async Task<ActionResult<int>> Upsert([FromBody] UpsertScheduleItemRequest r)
    {
        var (errors, warnings) = await _rules.ValidateUpsertAsync(r);
        if (errors.Count > 0) return Conflict(new { message = "Validation failed", errors, warnings });
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var lt = await _db.LessonTypes.FindAsync(r.LessonTypeId);
        if (lt is null) return BadRequest(new { message = "LessonType not found" });
        var ltCode = lt.Code ?? string.Empty;
        var normalizedRoomId = lt.RequiresRoom ? r.RoomId : null;
        if ((string.Equals(ltCode, "CANCELED", StringComparison.OrdinalIgnoreCase) || string.Equals(ltCode, "RESCHEDULED", StringComparison.OrdinalIgnoreCase)) && r.RoomId is int keepRoomId)
        {
            normalizedRoomId = keepRoomId;
        }
        if (!TryParseClock(r.TimeStart, out var start) || !TryParseClock(r.TimeEnd, out var end))
        {
            return BadRequest(new { message = "Некоректний формат часу. Використовуйте формат HH:mm." });
        }
        if (r.Id is int id && id > 0)
        {
            var item = await _db.ScheduleItems.FirstOrDefaultAsync(x => x.Id == id);
            if (item is null) return NotFound(new { message = $"ScheduleItem {id} not found" });
            if (item.IsLocked) return Conflict(new { message = "Запис розкладу заблоковано. Зміна заблокованих записів через API заборонена." });
            var previousLessonTypeId = item.LessonTypeId;
            var previousRoomId = item.RoomId;
            var previousLessonType = await _db.LessonTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == previousLessonTypeId);
            var previousRequiresRoom = previousLessonType?.RequiresRoom ?? true;
            var oldGroupId = item.GroupId;
            var oldModuleId = item.ModuleId;
            var oldTeacherId = item.TeacherId;
            var oldCourseId = await _db.Groups.Where(g => g.Id == oldGroupId).Select(g => g.CourseId).FirstAsync();
            ApplyScheduleRequest(item, r, start, end, normalizedRoomId);
            var recheck = await _rules.ValidateUpsertAsync(new UpsertScheduleItemRequest(
                item.Id, item.Date, item.StartTime.ToString("HH:mm"), item.EndTime.ToString("HH:mm"),
                item.GroupId, item.ModuleId, item.TeacherId, item.RoomId, item.LessonTypeId, item.IsLocked, r.OverrideNonWorkingDay));
            if (recheck.errors.Count > 0)
                return Conflict(new { message = "Validation failed (recheck)", errors = recheck.errors, warnings = recheck.warnings });
            await _db.SaveChangesAsync();
            var isRescheduled = string.Equals(lt.Code, "RESCHEDULED", StringComparison.OrdinalIgnoreCase);
            if (isRescheduled && previousLessonTypeId != r.LessonTypeId)
            {
                await TryCreateRescheduledCopyAsync(item, previousLessonTypeId, start, end, previousRoomId, previousRequiresRoom);
            }
            var newCourseId = await _db.Groups.Where(g => g.Id == r.GroupId).Select(g => g.CourseId).FirstAsync();
            await _aggregates.RecalcAsync(
                plans: new[] { (newCourseId, r.ModuleId) },
                loads: new[]
                {
                    (oldTeacherId is int t1) ? (t1, oldCourseId) : default,
                    (r.TeacherId  is int t2) ? (t2, newCourseId) : default
                }.Where(x => x != default)!);
            await tx.CommitAsync();
            return Ok(item.Id);
        }
        else
        {
            var item = new ScheduleItem();
            ApplyScheduleRequest(item, r, start, end, normalizedRoomId);
            _db.ScheduleItems.Add(item);
            var recheck = await _rules.ValidateUpsertAsync(new UpsertScheduleItemRequest(
                r.Id,
                r.Date,
                r.TimeStart,
                r.TimeEnd,
                r.GroupId,
                r.ModuleId,
                r.TeacherId,
                normalizedRoomId,
                r.LessonTypeId,
                r.IsLocked,
                r.OverrideNonWorkingDay));
            if (recheck.errors.Count > 0)
                return Conflict(new { message = "Validation failed (recheck)", errors = recheck.errors, warnings = recheck.warnings });
            await _db.SaveChangesAsync();
            var courseId = await _db.Groups.Where(g => g.Id == r.GroupId).Select(g => g.CourseId).FirstAsync();
            await _aggregates.RecalcAsync(
                plans: new[] { (courseId, r.ModuleId) },
                loads: (r.TeacherId is int t) ? new[] { (t, courseId) } : null
            );
            await tx.CommitAsync();
            return Ok(item.Id);
        }
    }
    // Переносить дані запиту в доменний об'єкт.
    private static void ApplyScheduleRequest(ScheduleItem item, UpsertScheduleItemRequest request, TimeOnly start, TimeOnly end, int? normalizedRoomId)
    {
        item.Date = request.Date;
        item.DayOfWeek = request.Date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
        item.StartTime = start;
        item.EndTime = end;
        item.GroupId = request.GroupId;
        item.ModuleId = request.ModuleId;
        item.RoomId = normalizedRoomId;
        item.TeacherId = request.TeacherId;
        item.LessonTypeId = request.LessonTypeId;
        item.IsLocked = request.IsLocked;
    }
    // Створює чернетку для перенесеної пари, якщо це можливо.
    private async Task TryCreateRescheduledCopyAsync(
        ScheduleItem source,
        int previousLessonTypeId,
        TimeOnly originalStart,
        TimeOnly originalEnd,
        int? previousRoomId,
        bool previousRequiresRoom)
    {
        var prevType = await _db.LessonTypes.FindAsync(previousLessonTypeId);
        if (prevType is null) return;
        var requiresRoom = previousRequiresRoom || prevType.RequiresRoom;
        var normalizedRoomId = requiresRoom ? (previousRoomId ?? source.RoomId) : null;
        var groupInfo = await _db.Groups
            .Where(g => g.Id == source.GroupId)
            .Select(g => new { g.CourseId })
            .FirstOrDefaultAsync();
        if (groupInfo is null) return;
        var nextWeekStart = DateHelpers.StartOfWeek(source.Date).AddDays(7);
        var earliestAllowed = nextWeekStart.ToDateTime(originalStart);
        var sequenceItems = await _db.ModuleSequenceItems
            .Where(x => x.CourseId == groupInfo.CourseId)
            .OrderBy(x => x.Order)
            .Select(x => new { x.ModuleId, x.Order, x.GroupOrder })
            .ToListAsync();
        var currentSequence = sequenceItems.FirstOrDefault(x => x.ModuleId == source.ModuleId);
        if (currentSequence is not null)
        {
            var predecessors = sequenceItems
                .Where(x => x.GroupOrder < currentSequence.GroupOrder)
                .Select(x => x.ModuleId)
                .Distinct()
                .ToList();
            if (predecessors.Count > 0)
            {
                var predecessorItems = await _db.ScheduleItems
                    .Where(x => x.GroupId == source.GroupId && predecessors.Contains(x.ModuleId))
                    .Select(x => new { x.Date, x.EndTime })
                    .ToListAsync();
                if (predecessorItems.Count > 0)
                {
                    var predecessorMax = predecessorItems
                        .Select(x => x.Date.ToDateTime(x.EndTime))
                        .Max()
                        .AddMinutes(1);
                    if (predecessorMax > earliestAllowed)
                    {
                        earliestAllowed = predecessorMax;
                    }
                }
            }
        }
        const int daySearchHorizon = 7; 
        for (int offset = 0; offset < daySearchHorizon; offset++)
        {
            var candidate = nextWeekStart.AddDays(offset);
            var dayOfWeek = candidate.ToDateTime(TimeOnly.MinValue).DayOfWeek;
            var resolvedSlots = await TimeSlotsResolver.ResolveForDayAsync(_db, groupInfo.CourseId, dayOfWeek);
            var candidateSlots = new List<(TimeOnly Start, TimeOnly End)> { (originalStart, originalEnd) };
            foreach (var slot in resolvedSlots.Slots)
            {
                if (slot.Start == originalStart && slot.End == originalEnd) continue;
                if (candidateSlots.Contains((slot.Start, slot.End))) continue;
                candidateSlots.Add((slot.Start, slot.End));
            }
            if (candidateSlots.Count == 0)
            {
                candidateSlots.Add((originalStart, originalEnd));
            }
            foreach (var (slotStart, slotEnd) in candidateSlots)
            {
                var candidateMoment = candidate.ToDateTime(slotStart);
                if (candidateMoment < earliestAllowed) continue;
                var batchKey = $"rescheduled:{source.Id}:{previousLessonTypeId}";
                var draftRequest = new DraftUpsertRequest(
                    Id: null,
                    Date: candidate,
                    TimeStart: slotStart.ToString("HH:mm"),
                    TimeEnd: slotEnd.ToString("HH:mm"),
                    GroupId: source.GroupId,
                    ModuleId: source.ModuleId,
                    ModuleTopicId: source.ModuleTopicId,
                    TeacherId: source.TeacherId,
                    RoomId: normalizedRoomId,
                    RequiresRoom: requiresRoom,
                    LessonTypeId: previousLessonTypeId,
                    OverrideNonWorkingDay: false,
                    BatchKey: batchKey,
                    IsLocked: false,
                    IgnoreValidationErrors: false
                );
                var validation = await _rules.ValidateDraftAsync(draftRequest);
                if (validation.Errors.Count > 0) continue;
                var exists = await _db.ScheduleItems.AnyAsync(x =>
                    x.Date == candidate
                    && x.StartTime == slotStart
                    && x.EndTime == slotEnd
                    && x.GroupId == source.GroupId
                    && x.ModuleId == source.ModuleId
                    && x.RoomId == normalizedRoomId
                    && x.TeacherId == source.TeacherId
                    && x.LessonTypeId == previousLessonTypeId);
                if (exists) continue;
                var existsDraft = await _db.TeacherDraftItems.AnyAsync(x =>
                    x.Date == candidate
                    && x.StartTime == slotStart
                    && x.EndTime == slotEnd
                    && x.GroupId == source.GroupId
                    && x.ModuleId == source.ModuleId
                    && x.RoomId == normalizedRoomId
                    && x.TeacherId == source.TeacherId);
                if (existsDraft) continue;
                var newDraft = new TeacherDraftItem
                {
                    Date = candidate,
                    DayOfWeek = candidate.ToDateTime(TimeOnly.MinValue).DayOfWeek,
                    StartTime = slotStart,
                    EndTime = slotEnd,
                    GroupId = source.GroupId,
                    ModuleId = source.ModuleId,
                    TeacherId = source.TeacherId,
                    RoomId = normalizedRoomId,
                    LessonTypeId = previousLessonTypeId,
                    Status = DraftStatus.Draft,
                    PublishedItemId = null,
                    BatchKey = batchKey,
                    ValidationWarnings = null,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    IsLocked = true 
                };
                _db.TeacherDraftItems.Add(newDraft);
                await _db.SaveChangesAsync();
                return;
            }
        }
    }
    [HttpDelete("{id:int}")]
    [RequireDeletionConfirmation("запис розкладу")]
    // Видаляє пару та перераховує агрегати.
    public async Task<IActionResult> Delete(int id)
    {
        var info = await _db.ScheduleItems
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.GroupId, x.ModuleId, x.TeacherId, x.IsLocked, CourseId = x.Group.CourseId })
            .FirstOrDefaultAsync();
        if (info is null)
            return NotFound(new { message = $"ScheduleItem {id} not found" });
        if (info.IsLocked) return Conflict(new { message = "Запис розкладу заблоковано. Видалення заблокованих записів через API заборонено." });
        await _db.ScheduleItems.Where(x => x.Id == id).ExecuteDeleteAsync();
        await _aggregates.RecalcAsync(
            plans: new[] { (info.CourseId, info.ModuleId) },
            loads: (info.TeacherId is int t) ? new[] { (t, info.CourseId) } : null
        );
        return NoContent();
    }
    [HttpPost("clear")]
    // Очищає розклад за тиждень із перерахунком агрегатів.
    public async Task<ActionResult<ClearWeekResult>> ClearWeek([FromBody] ClearWeekRequest r)
    {
        if (r.CourseId is null && r.GroupId is null)
        {
            return BadRequest(new { message = "Для очищення тижня потрібно вказати курс або групу." });
        }
        var start = r.WeekStart;
        var end = start.AddDays(7);
        var q = _db.ScheduleItems.Where(x => x.Date >= start && x.Date < end && !x.IsLocked);
        if (r.CourseId is int cid) q = q.Where(x => x.Group.CourseId == cid);
        if (r.GroupId is int gidFilter) q = q.Where(x => x.GroupId == gidFilter);
        var affectedPlans = await q
            .Select(x => new { x.ModuleId, CourseId = x.Group.CourseId })
            .Distinct()
            .ToListAsync();
        var affectedLoads = await q.Where(x => x.TeacherId != null)
            .Select(x => new { x.TeacherId, CourseId = x.Group.CourseId })
            .Distinct()
            .ToListAsync();
        var deleted = await q.ExecuteDeleteAsync();
        await _aggregates.RecalcAsync(
            plans: affectedPlans.Select(a => (a.CourseId, a.ModuleId)),
            loads: affectedLoads.Select(a => (a.TeacherId!.Value, a.CourseId))
        );
        return Ok(new ClearWeekResult(deleted));
    }

}
