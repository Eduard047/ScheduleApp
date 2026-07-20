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
    private sealed record RescheduleEventSnapshot(
        DateOnly Date,
        TimeOnly Start,
        TimeOnly End,
        int GroupId,
        int ModuleId,
        int LessonTypeId,
        IReadOnlyList<RescheduleRowSnapshot> Rows);
    private sealed record RescheduleRowSnapshot(
        int SourceId,
        int? ModuleTopicId,
        int? TeacherId,
        int? RoomId,
        bool IsSelfStudy);
    private static bool TryParseClock(string value, out TimeOnly time)
        => TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    [HttpGet]
    // Повертає розклад за тиждень із фільтрами.
    public async Task<ActionResult<IReadOnlyList<ScheduleItemDto>>> Get(
        [FromQuery] DateOnly weekStart,
        [FromQuery] int? courseId,
        [FromQuery] int? groupId,
        [FromQuery] int? teacherId,
        [FromQuery] int? roomId)
    {
        if (!DateHelpers.IsSupportedScheduleDate(weekStart))
        {
            return BadRequest(new { message = DateHelpers.SupportedScheduleDateMessage });
        }
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
        return Ok(items.Select(item =>
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
        }).ToList());
    }
    [HttpPost("upsert")]
    // Створює або оновлює пару в розкладі з перевіркою правил.
    public async Task<ActionResult<int>> Upsert([FromBody] UpsertScheduleItemRequest r)
    {
        if (!DateHelpers.IsSupportedScheduleDate(r.Date))
        {
            return BadRequest(new { message = DateHelpers.SupportedScheduleDateMessage });
        }
        if (r.Id is int existingId && existingId > 0)
        {
            return await UpdateLogicalEventAsync(r, existingId);
        }

        var (errors, warnings) = await _rules.ValidateUpsertAsync(r);
        if (errors.Count > 0) return Conflict(new { message = "Перевірка правил не пройдена.", errors, warnings });
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var lt = await _db.LessonTypes.FindAsync(r.LessonTypeId);
        if (lt is null) return BadRequest(new { message = "Тип заняття не знайдено." });
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
            return Conflict(new { message = "Повторна перевірка правил не пройдена.", errors = recheck.errors, warnings = recheck.warnings });
        await _db.SaveChangesAsync();
        var courseId = await _db.Groups.Where(g => g.Id == r.GroupId).Select(g => g.CourseId).FirstAsync();
        await _aggregates.RecalcAsync(
            plans: new[] { (courseId, r.ModuleId) },
            loads: (r.TeacherId is int t) ? new[] { (t, courseId) } : null
        );
        await tx.CommitAsync();
        return Ok(item.Id);
    }

    // Атомарно оновлює всі рядки одного опублікованого логічного заняття.
    private async Task<ActionResult<int>> UpdateLogicalEventAsync(UpsertScheduleItemRequest request, int sourceId)
    {
        if (!TryParseClock(request.TimeStart, out var start) || !TryParseClock(request.TimeEnd, out var end))
        {
            return BadRequest(new { message = "Некоректний формат часу. Використовуйте формат HH:mm." });
        }

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var lessonType = await _db.LessonTypes.FindAsync(request.LessonTypeId);
        if (lessonType is null)
        {
            return BadRequest(new { message = "Тип заняття не знайдено." });
        }

        var source = await _db.ScheduleItems.FirstOrDefaultAsync(item => item.Id == sourceId);
        if (source is null)
        {
            return NotFound(new { message = $"Запис розкладу #{sourceId} не знайдено." });
        }

        var logicalEventRows = await LoadLogicalEventRowsAsync(source);
        if (logicalEventRows.Any(item => item.IsLocked))
        {
            return Conflict(new { message = "Логічне заняття містить заблоковані рядки. Часткову зміну заборонено." });
        }
        if (logicalEventRows
            .Select(item => item.IsSelfStudy)
            .Distinct()
            .Skip(1)
            .Any())
        {
            return Conflict(new
            {
                message = "Логічне заняття має неузгоджений режим самостійної роботи. Виправте дані перед оновленням."
            });
        }

        var sourceTeacherId = source.TeacherId;
        var previousLessonTypeId = source.LessonTypeId;
        var previousLessonType = await _db.LessonTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == previousLessonTypeId);
        var previousRequiresRoom = previousLessonType?.RequiresRoom ?? true;
        var rescheduleSnapshot = new RescheduleEventSnapshot(
            source.Date,
            source.StartTime,
            source.EndTime,
            source.GroupId,
            source.ModuleId,
            previousLessonTypeId,
            logicalEventRows
                .Select(item => new RescheduleRowSnapshot(
                    item.Id,
                    item.ModuleTopicId,
                    item.TeacherId,
                    item.RoomId,
                    item.IsSelfStudy))
                .ToList());
        var oldCourseId = await _db.Groups
            .Where(group => group.Id == source.GroupId)
            .Select(group => group.CourseId)
            .FirstAsync();
        var oldModuleId = source.ModuleId;
        var oldTeacherIds = logicalEventRows
            .Where(item => item.TeacherId is not null)
            .Select(item => item.TeacherId!.Value)
            .Distinct()
            .ToList();
        var scopeIds = logicalEventRows.Select(item => item.Id).ToList();
        var normalizedRoomId = NormalizeRoomId(lessonType, request.RoomId);
        var projectedRequests = logicalEventRows
            .Select(item => request with
            {
                Id = item.Id,
                TeacherId = item.TeacherId == sourceTeacherId ? request.TeacherId : item.TeacherId,
                RoomId = normalizedRoomId
            })
            .ToList();
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var projectedRequest in projectedRequests)
        {
            var validation = await _rules.ValidateUpsertAsync(projectedRequest, scopeIds);
            errors.AddRange(validation.errors.Select(error => $"[#{projectedRequest.Id}] {error}"));
            warnings.AddRange(validation.warnings.Select(warning => $"[#{projectedRequest.Id}] {warning}"));
        }
        if (errors.Count > 0)
        {
            await tx.RollbackAsync();
            return Conflict(new
            {
                message = "Перевірка правил не пройдена.",
                errors = errors.Distinct(StringComparer.Ordinal).ToList(),
                warnings = warnings.Distinct(StringComparer.Ordinal).ToList()
            });
        }

        var resolvedBatchKey = !string.IsNullOrWhiteSpace(source.BatchKey)
            ? source.BatchKey
            : logicalEventRows.Count > 1
                ? CreateSafeBatchKey("official")
                : null;
        for (var index = 0; index < logicalEventRows.Count; index++)
        {
            ApplyScheduleRequest(logicalEventRows[index], projectedRequests[index], start, end, normalizedRoomId);
            logicalEventRows[index].BatchKey = resolvedBatchKey;
        }
        await _db.SaveChangesAsync();

        var isRescheduled = string.Equals(lessonType.Code, "RESCHEDULED", StringComparison.OrdinalIgnoreCase);
        if (isRescheduled && previousLessonTypeId != request.LessonTypeId)
        {
            await TryCreateRescheduledCopiesAsync(rescheduleSnapshot, previousRequiresRoom);
        }

        var newCourseId = await _db.Groups
            .Where(group => group.Id == request.GroupId)
            .Select(group => group.CourseId)
            .FirstAsync();
        var newTeacherIds = logicalEventRows
            .Where(item => item.TeacherId is not null)
            .Select(item => item.TeacherId!.Value)
            .Distinct();
        await _aggregates.RecalcAsync(
            plans: new[]
            {
                (oldCourseId, oldModuleId),
                (newCourseId, request.ModuleId)
            }.Distinct(),
            loads: oldTeacherIds
                .Select(teacherId => (teacherId, oldCourseId))
                .Concat(newTeacherIds.Select(teacherId => (teacherId, newCourseId)))
                .Distinct());
        await tx.CommitAsync();
        return Ok(source.Id);
    }

    private static int? NormalizeRoomId(LessonTypeRef lessonType, int? roomId)
    {
        var code = lessonType.Code ?? string.Empty;
        var preservesServiceRoom = string.Equals(code, "CANCELED", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(code, "RESCHEDULED", StringComparison.OrdinalIgnoreCase);
        return lessonType.RequiresRoom || preservesServiceRoom ? roomId : null;
    }

    // Для нових даних визначає подію за BatchKey і сигнатурою, а для legacy-рядків застосовує консервативну евристику.
    private async Task<List<ScheduleItem>> LoadLogicalEventRowsAsync(ScheduleItem source)
    {
        var signatureQuery = _db.ScheduleItems
            .Where(item => item.Date == source.Date
                           && item.StartTime == source.StartTime
                           && item.EndTime == source.EndTime
                           && item.GroupId == source.GroupId
                           && item.ModuleId == source.ModuleId
                           && item.LessonTypeId == source.LessonTypeId);
        if (!string.IsNullOrWhiteSpace(source.BatchKey))
        {
            return await signatureQuery
                .Where(item => item.BatchKey == source.BatchKey)
                .OrderBy(item => item.Id)
                .ToListAsync();
        }

        var signatureRows = await signatureQuery
            .OrderBy(item => item.Id)
            .ToListAsync();
        var legacyRows = signatureRows
            .Where(item => string.IsNullOrWhiteSpace(item.BatchKey))
            .ToList();
        var hasRepresentationalSiblings = legacyRows
            .Select(item => new { item.ModuleTopicId, item.TeacherId })
            .Distinct()
            .Skip(1)
            .Any();
        return hasRepresentationalSiblings
            ? legacyRows
            : legacyRows.Where(item => item.Id == source.Id).ToList();
    }

    private static string CreateSafeBatchKey(string prefix)
        => $"{prefix}:{Guid.NewGuid():N}";
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
    // Створює всі чернеткові рядки перенесеної логічної події одним атомарним пакетом.
    private async Task TryCreateRescheduledCopiesAsync(
        RescheduleEventSnapshot snapshot,
        bool previousRequiresRoom)
    {
        var previousLessonType = await _db.LessonTypes.FindAsync(snapshot.LessonTypeId);
        if (previousLessonType is null || snapshot.Rows.Count == 0)
        {
            return;
        }

        var requiresRoom = previousRequiresRoom || previousLessonType.RequiresRoom;
        var courseId = await _db.Groups
            .Where(group => group.Id == snapshot.GroupId)
            .Select(group => (int?)group.CourseId)
            .FirstOrDefaultAsync();
        if (courseId is null)
        {
            return;
        }

        var nextWeekStart = DateHelpers.StartOfWeek(snapshot.Date).AddDays(7);
        if (!DateHelpers.IsSupportedScheduleDate(nextWeekStart))
        {
            return;
        }
        var earliestAllowed = nextWeekStart.ToDateTime(snapshot.Start);
        var sequenceItems = await _db.ModuleSequenceItems
            .Where(item => item.CourseId == courseId.Value)
            .OrderBy(item => item.Order)
            .Select(item => new { item.ModuleId, item.Order, item.GroupOrder })
            .ToListAsync();
        var currentSequence = sequenceItems.FirstOrDefault(item => item.ModuleId == snapshot.ModuleId);
        if (currentSequence is not null)
        {
            var predecessorIds = sequenceItems
                .Where(item => item.GroupOrder < currentSequence.GroupOrder)
                .Select(item => item.ModuleId)
                .Distinct()
                .ToList();
            if (predecessorIds.Count > 0)
            {
                var predecessorItems = await _db.ScheduleItems
                    .Where(item => item.GroupId == snapshot.GroupId && predecessorIds.Contains(item.ModuleId))
                    .Select(item => new { item.Date, item.EndTime })
                    .ToListAsync();
                if (predecessorItems.Count > 0)
                {
                    var predecessorMax = predecessorItems
                        .Select(item => item.Date.ToDateTime(item.EndTime))
                        .Max()
                        .AddMinutes(1);
                    if (predecessorMax > earliestAllowed)
                    {
                        earliestAllowed = predecessorMax;
                    }
                }
            }
        }

        var batchKey = $"rescheduled:{snapshot.Rows.Min(row => row.SourceId)}:{snapshot.LessonTypeId}";
        var packageAlreadyExists = await _db.TeacherDraftItems
            .AnyAsync(item => item.BatchKey == batchKey)
            || await _db.ScheduleItems.AnyAsync(item => item.BatchKey == batchKey);
        if (packageAlreadyExists)
        {
            return;
        }

        const int daySearchHorizon = 7;
        for (var offset = 0; offset < daySearchHorizon; offset++)
        {
            var candidateDate = nextWeekStart.AddDays(offset);
            var resolvedSlots = await TimeSlotsResolver.ResolveForDayAsync(
                _db,
                courseId.Value,
                candidateDate.DayOfWeek);
            var candidateSlots = new List<(TimeOnly Start, TimeOnly End)> { (snapshot.Start, snapshot.End) };
            foreach (var slot in resolvedSlots.Slots)
            {
                if (!candidateSlots.Contains((slot.Start, slot.End)))
                {
                    candidateSlots.Add((slot.Start, slot.End));
                }
            }

            foreach (var (slotStart, slotEnd) in candidateSlots)
            {
                if (candidateDate.ToDateTime(slotStart) < earliestAllowed)
                {
                    continue;
                }

                var requests = snapshot.Rows
                    .Select(row => new DraftUpsertRequest(
                        Id: null,
                        Date: candidateDate,
                        TimeStart: slotStart.ToString("HH:mm"),
                        TimeEnd: slotEnd.ToString("HH:mm"),
                        GroupId: snapshot.GroupId,
                        ModuleId: snapshot.ModuleId,
                        ModuleTopicId: row.ModuleTopicId,
                        TeacherId: row.TeacherId,
                        RoomId: requiresRoom ? row.RoomId : null,
                        RequiresRoom: requiresRoom,
                        LessonTypeId: snapshot.LessonTypeId,
                        OverrideNonWorkingDay: false,
                        BatchKey: batchKey,
                        IsLocked: false,
                        IgnoreValidationErrors: false,
                        IsSelfStudy: row.IsSelfStudy))
                    .ToList();
                var isValidPackage = true;
                foreach (var draftRequest in requests)
                {
                    var validation = await _rules.ValidateDraftAsync(draftRequest);
                    if (validation.Errors.Count == 0)
                    {
                        continue;
                    }

                    isValidPackage = false;
                    break;
                }
                if (!isValidPackage)
                {
                    continue;
                }

                var now = DateTime.UtcNow;
                var drafts = requests.Select(request => new TeacherDraftItem
                {
                    Date = request.Date,
                    DayOfWeek = request.Date.DayOfWeek,
                    StartTime = slotStart,
                    EndTime = slotEnd,
                    GroupId = request.GroupId,
                    ModuleId = request.ModuleId,
                    ModuleTopicId = request.ModuleTopicId,
                    TeacherId = request.TeacherId,
                    RoomId = request.RoomId,
                    LessonTypeId = request.LessonTypeId,
                    Status = DraftStatus.Draft,
                    PublishedItemId = null,
                    BatchKey = batchKey,
                    ValidationWarnings = null,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsLocked = true,
                    IsSelfStudy = request.IsSelfStudy
                }).ToList();
                _db.TeacherDraftItems.AddRange(drafts);
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
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var source = await _db.ScheduleItems.FirstOrDefaultAsync(item => item.Id == id);
        if (source is null)
            return NotFound(new { message = $"Запис розкладу #{id} не знайдено." });
        var logicalEventRows = await LoadLogicalEventRowsAsync(source);
        if (logicalEventRows.Any(item => item.IsLocked))
        {
            return Conflict(new { message = "Логічне заняття містить заблоковані рядки. Часткове видалення заборонено." });
        }

        var courseId = await _db.Groups
            .Where(group => group.Id == source.GroupId)
            .Select(group => group.CourseId)
            .FirstAsync();
        var moduleId = source.ModuleId;
        var teacherIds = logicalEventRows
            .Where(item => item.TeacherId is not null)
            .Select(item => item.TeacherId!.Value)
            .Distinct()
            .ToList();
        var ids = logicalEventRows.Select(item => item.Id).ToList();
        await _db.ScheduleItems.Where(item => ids.Contains(item.Id)).ExecuteDeleteAsync();
        await _aggregates.RecalcAsync(
            plans: new[] { (courseId, moduleId) },
            loads: teacherIds.Select(teacherId => (teacherId, courseId))
        );
        await tx.CommitAsync();
        return NoContent();
    }
    [HttpPost("clear")]
    [RequireDeletionConfirmation("незаблоковані записи розкладу за тиждень")]
    // Очищає розклад за тиждень із перерахунком агрегатів.
    public async Task<ActionResult<ClearWeekResult>> ClearWeek([FromBody] ClearWeekRequest r)
    {
        if (!DateHelpers.IsSupportedScheduleDate(r.WeekStart))
        {
            return BadRequest(new { message = DateHelpers.SupportedScheduleDateMessage });
        }
        if (r.CourseId is null && r.GroupId is null)
        {
            return BadRequest(new { message = "Для очищення тижня потрібно вказати курс або групу." });
        }
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
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
        await tx.CommitAsync();
        return Ok(new ClearWeekResult(deleted));
    }

}
