using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
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
    private const int MaxWeekScheduleCandidateCount = 50_000;
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
    private sealed record ExistingRescheduledPackageRow(
        DateOnly Date,
        DayOfWeek DayOfWeek,
        TimeOnly StartTime,
        TimeOnly EndTime,
        int GroupId,
        int ModuleId,
        int LessonTypeId,
        int? ModuleTopicId,
        int? TeacherId,
        int? RoomId,
        bool IsSelfStudy);
    private sealed record RescheduledPackageRowSignature(
        int? ModuleTopicId,
        int? TeacherId,
        int? RoomId,
        bool IsSelfStudy);
    private sealed record ScheduleRevisionRow(
        int Id,
        Guid Revision,
        string? BatchKey,
        DateOnly Date,
        TimeOnly StartTime,
        TimeOnly EndTime,
        int GroupId,
        int ModuleId,
        int LessonTypeId,
        int? ModuleTopicId,
        int? TeacherId);
    private static bool TryParseClock(string value, out TimeOnly time)
        => TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    [HttpGet]
    // Повертає розклад за тиждень із фільтрами.
    public async Task<ActionResult<IReadOnlyList<ScheduleItemDto>>> Get(
        [FromQuery] DateOnly weekStart,
        [FromQuery] int? courseId,
        [FromQuery] int? groupId,
        [FromQuery] int? teacherId,
        [FromQuery] int? roomId,
        CancellationToken cancellationToken = default)
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
        // Курс і група є спільними для всіх технічних рядків логічного заняття,
        // тому ці фільтри безпечно застосовувати до матеріалізації та revision map.
        if (courseId is int requestedCourseId)
        {
            q = q.Where(x => x.Group.CourseId == requestedCourseId);
        }
        if (groupId is int requestedGroupId)
        {
            q = q.Where(x => x.GroupId == requestedGroupId);
        }
        var items = await q
            .OrderBy(x => x.Date)
            .ThenBy(x => x.StartTime)
            .Take(MaxWeekScheduleCandidateCount + 1)
            .Select(x => new
            {
                x.Id,
                x.Revision,
                x.BatchKey,
                x.Date,
                x.StartTime,
                x.EndTime,
                x.DayOfWeek,
                GroupName = x.Group.Name,
                x.GroupId,
                CourseId = x.Group.CourseId,
                ModuleTitle = x.Module.Title,
                x.ModuleId,
                x.ModuleTopicId,
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
            .ToListAsync(cancellationToken);
        if (items.Count > MaxWeekScheduleCandidateCount)
        {
            return UnprocessableEntity(new
            {
                message = $"Розклад тижня містить понад {MaxWeekScheduleCandidateCount} технічних рядків. Звузьте вибір курсу або групи."
            });
        }
        var revisionsByItemId = BuildLogicalRevisionMap(items.Select(item => new ScheduleRevisionRow(
            item.Id,
            item.Revision,
            item.BatchKey,
            item.Date,
            item.StartTime,
            item.EndTime,
            item.GroupId,
            item.ModuleId,
            item.LessonTypeId,
            item.ModuleTopicId,
            item.TeacherId)));
        var visibleItems = items
            .Where(item => courseId is not int cid || item.CourseId == cid)
            .Where(item => groupId is not int gidFilter || item.GroupId == gidFilter)
            .Where(item => teacherId is not int tid || item.TeacherId == tid)
            .Where(item => roomId is not int rid || item.RoomId == rid)
            .ToList();
        var uk = new CultureInfo("uk-UA");
        return Ok(visibleItems.Select(item =>
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
                LessonTypeCss: item.LessonTypeCss ?? (isBreak ? "brk" : null),
                Revision: revisionsByItemId[item.Id]
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
            if (r.ExpectedRevision is not Guid expectedRevision || expectedRevision == Guid.Empty)
            {
                return StatusCode(428, new
                {
                    message = "Для оновлення запису потрібна його актуальна версія. Оновіть сторінку та повторіть дію."
                });
            }
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
        if (ComputeLogicalEventRevision(logicalEventRows) != request.ExpectedRevision)
        {
            await tx.RollbackAsync();
            return Conflict(new
            {
                message = "Запис розкладу вже змінив інший користувач. Оновіть сторінку перед повторним збереженням."
            });
        }
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
        var createsRescheduledReplacement = string.Equals(
                                                lessonType.Code,
                                                "RESCHEDULED",
                                                StringComparison.OrdinalIgnoreCase)
                                            && previousLessonTypeId != request.LessonTypeId;
        if (createsRescheduledReplacement
            && previousRequiresRoom
            && logicalEventRows
                .Select(item => item.RoomId)
                .Distinct()
                .Skip(1)
                .Any())
        {
            await tx.RollbackAsync();
            return Conflict(new
            {
                message = "Логічне заняття має різні аудиторії в окремих рядках. Виправте дані перед перенесенням."
            });
        }
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
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            return Conflict(new
            {
                message = "Запис розкладу змінився під час збереження. Оновіть сторінку та повторіть дію."
            });
        }

        if (createsRescheduledReplacement)
        {
            var replacementCreated = await TryCreateRescheduledCopiesAsync(
                rescheduleSnapshot,
                previousRequiresRoom);
            if (!replacementCreated)
            {
                await tx.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();
                return Conflict(new
                {
                    message = "Не вдалося знайти допустимий слот для перенесеного заняття. Розклад не змінено."
                });
            }
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

    private static Guid ComputeLogicalEventRevision(IEnumerable<ScheduleItem> rows)
        => LogicalRevisionToken.Combine(rows.Select(item =>
            new KeyValuePair<int, Guid>(item.Id, item.Revision)));

    private static IReadOnlyDictionary<int, Guid> BuildLogicalRevisionMap(IEnumerable<ScheduleRevisionRow> sourceRows)
    {
        var rows = sourceRows.ToList();
        var result = rows.ToDictionary(item => item.Id, item => item.Revision);

        void AssignRevision(IEnumerable<ScheduleRevisionRow> eventRows)
        {
            var resolvedRows = eventRows.OrderBy(item => item.Id).ToList();
            var revision = LogicalRevisionToken.Combine(resolvedRows.Select(item =>
                new KeyValuePair<int, Guid>(item.Id, item.Revision)));
            foreach (var row in resolvedRows)
            {
                result[row.Id] = revision;
            }
        }

        foreach (var eventRows in rows
                     .Where(item => !string.IsNullOrWhiteSpace(item.BatchKey))
                     .GroupBy(item => new
                     {
                         item.BatchKey,
                         item.Date,
                         item.StartTime,
                         item.EndTime,
                         item.GroupId,
                         item.ModuleId,
                         item.LessonTypeId
                     }))
        {
            AssignRevision(eventRows);
        }

        foreach (var eventRows in rows
                     .Where(item => string.IsNullOrWhiteSpace(item.BatchKey))
                     .GroupBy(item => new
                     {
                         item.Date,
                         item.StartTime,
                         item.EndTime,
                         item.GroupId,
                         item.ModuleId,
                         item.LessonTypeId
                     })
                     .Where(group => group
                         .Select(item => new { item.ModuleTopicId, item.TeacherId })
                         .Distinct()
                         .Skip(1)
                         .Any()))
        {
            AssignRevision(eventRows);
        }

        return result;
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
    private async Task<bool> TryCreateRescheduledCopiesAsync(
        RescheduleEventSnapshot snapshot,
        bool previousRequiresRoom)
    {
        var previousLessonType = await _db.LessonTypes.FindAsync(snapshot.LessonTypeId);
        if (previousLessonType is null || snapshot.Rows.Count == 0)
        {
            return false;
        }

        var requiresRoom = previousRequiresRoom || previousLessonType.RequiresRoom;
        var courseId = await _db.Groups
            .Where(group => group.Id == snapshot.GroupId)
            .Select(group => (int?)group.CourseId)
            .FirstOrDefaultAsync();
        if (courseId is null)
        {
            return false;
        }

        var nextWeekStart = DateHelpers.StartOfWeek(snapshot.Date).AddDays(7);
        if (!DateHelpers.IsSupportedScheduleDate(nextWeekStart))
        {
            return false;
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
        var existingPackage = await ValidateExistingRescheduledPackageAsync(
            snapshot,
            requiresRoom,
            nextWeekStart,
            batchKey);
        if (existingPackage.Exists)
        {
            return existingPackage.IsComplete;
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
                    var hasNonWorkingDayWarning = validation.Report.Issues.Any(issue =>
                        string.Equals(issue.Code, "non-working-day", StringComparison.Ordinal));
                    if (validation.Errors.Count == 0 && !hasNonWorkingDayWarning)
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
                return true;
            }
        }

        return false;
    }

    // Повторне перенесення приймає лише повний раніше створений пакет,
    // а не довільний рядок із передбачуваним ключем.
    private async Task<(bool Exists, bool IsComplete)> ValidateExistingRescheduledPackageAsync(
        RescheduleEventSnapshot snapshot,
        bool requiresRoom,
        DateOnly nextWeekStart,
        string batchKey)
    {
        var rowLimit = snapshot.Rows.Count + 1;
        var draftRows = await _db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.BatchKey == batchKey)
            .OrderBy(item => item.Id)
            .Take(rowLimit)
            .Select(item => new ExistingRescheduledPackageRow(
                item.Date,
                item.DayOfWeek,
                item.StartTime,
                item.EndTime,
                item.GroupId,
                item.ModuleId,
                item.LessonTypeId,
                item.ModuleTopicId,
                item.TeacherId,
                item.RoomId,
                item.IsSelfStudy))
            .ToListAsync();
        var scheduleRows = await _db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.BatchKey == batchKey)
            .OrderBy(item => item.Id)
            .Take(rowLimit)
            .Select(item => new ExistingRescheduledPackageRow(
                item.Date,
                item.DayOfWeek,
                item.StartTime,
                item.EndTime,
                item.GroupId,
                item.ModuleId,
                item.LessonTypeId,
                item.ModuleTopicId,
                item.TeacherId,
                item.RoomId,
                item.IsSelfStudy))
            .ToListAsync();
        if (draftRows.Count == 0 && scheduleRows.Count == 0)
        {
            return (false, false);
        }
        if (draftRows.Count > 0 && scheduleRows.Count > 0)
        {
            return (true, false);
        }

        var rows = draftRows.Count > 0 ? draftRows : scheduleRows;
        if (rows.Count != snapshot.Rows.Count)
        {
            return (true, false);
        }

        var first = rows[0];
        var replacementEnd = nextWeekStart.AddDays(7);
        if (first.Date < nextWeekStart
            || first.Date >= replacementEnd
            || first.EndTime <= first.StartTime
            || rows.Any(row =>
                row.Date != first.Date
                || row.DayOfWeek != first.Date.DayOfWeek
                || row.StartTime != first.StartTime
                || row.EndTime != first.EndTime
                || row.GroupId != snapshot.GroupId
                || row.ModuleId != snapshot.ModuleId
                || row.LessonTypeId != snapshot.LessonTypeId))
        {
            return (true, false);
        }

        var expectedCounts = snapshot.Rows
            .Select(row => new RescheduledPackageRowSignature(
                row.ModuleTopicId,
                row.TeacherId,
                requiresRoom ? row.RoomId : null,
                row.IsSelfStudy))
            .GroupBy(signature => signature)
            .ToDictionary(group => group.Key, group => group.Count());
        var actualCounts = rows
            .Select(row => new RescheduledPackageRowSignature(
                row.ModuleTopicId,
                row.TeacherId,
                row.RoomId,
                row.IsSelfStudy))
            .GroupBy(signature => signature)
            .ToDictionary(group => group.Key, group => group.Count());
        var isComplete = expectedCounts.Count == actualCounts.Count
                         && expectedCounts.All(entry =>
                             actualCounts.TryGetValue(entry.Key, out var count)
                             && count == entry.Value);
        return (true, isComplete);
    }
    [HttpDelete("{id:int}")]
    [RequireDeletionConfirmation("запис розкладу")]
    // Видаляє пару та перераховує агрегати.
    public async Task<IActionResult> Delete(int id, [FromQuery] Guid? expectedRevision)
    {
        if (expectedRevision is not Guid revision || revision == Guid.Empty)
        {
            return StatusCode(428, new
            {
                message = "Для видалення запису потрібна його актуальна версія. Оновіть сторінку та повторіть дію."
            });
        }
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var source = await _db.ScheduleItems.FirstOrDefaultAsync(item => item.Id == id);
        if (source is null)
            return NotFound(new { message = $"Запис розкладу #{id} не знайдено." });
        var logicalEventRows = await LoadLogicalEventRowsAsync(source);
        if (ComputeLogicalEventRevision(logicalEventRows) != revision)
        {
            await tx.RollbackAsync();
            return Conflict(new
            {
                message = "Запис розкладу вже змінив інший користувач. Оновіть сторінку перед видаленням."
            });
        }
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
        _db.ScheduleItems.RemoveRange(logicalEventRows);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            return Conflict(new
            {
                message = "Запис розкладу змінився під час видалення. Оновіть сторінку та повторіть дію."
            });
        }
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
        var scopeQuery = _db.ScheduleItems.Where(x => x.Date >= start && x.Date < end);
        if (r.CourseId is int cid) scopeQuery = scopeQuery.Where(x => x.Group.CourseId == cid);
        if (r.GroupId is int gidFilter) scopeQuery = scopeQuery.Where(x => x.GroupId == gidFilter);
        var scopedRows = await scopeQuery
            .AsNoTracking()
            .OrderBy(x => x.Date)
            .ThenBy(x => x.StartTime)
            .ThenBy(x => x.GroupId)
            .ThenBy(x => x.Id)
            .Take(TeacherDraftsWeekValidationService.MaxStoredScheduleRowCount + 1)
            .ToListAsync();
        if (scopedRows.Count > TeacherDraftsWeekValidationService.MaxStoredScheduleRowCount)
        {
            await tx.RollbackAsync();
            return Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Забагато записів для очищення",
                detail: $"За одну операцію можна очистити не більше {TeacherDraftsWeekValidationService.MaxStoredScheduleRowCount} записів розкладу.");
        }
        if (r.ExpectedScopeRevision is not Guid expectedScopeRevision)
        {
            await tx.RollbackAsync();
            return Problem(
                statusCode: StatusCodes.Status428PreconditionRequired,
                title: "Потрібна версія тижня",
                detail: "Оновіть опублікований розклад перед очищенням.");
        }
        var actualScopeRevision = LogicalRevisionToken.Combine(scopedRows.Select(item =>
            new KeyValuePair<int, Guid>(item.Id, item.Revision)));
        if (actualScopeRevision != expectedScopeRevision)
        {
            await tx.RollbackAsync();
            return Conflict(new
            {
                message = "Опублікований розклад змінився після завантаження. Оновіть сторінку та повторіть очищення."
            });
        }
        var logicalEvents = scopedRows
            .Where(x => !string.IsNullOrWhiteSpace(x.BatchKey))
            .GroupBy(x => new
            {
                BatchKey = x.BatchKey!,
                x.Date,
                x.StartTime,
                x.EndTime,
                x.GroupId,
                x.ModuleId,
                x.LessonTypeId
            })
            .Select(group => group.ToList())
            .ToList();
        logicalEvents.AddRange(scopedRows
            .Where(x => string.IsNullOrWhiteSpace(x.BatchKey))
            .GroupBy(x => new
            {
                x.Date,
                x.StartTime,
                x.EndTime,
                x.GroupId,
                x.ModuleId,
                x.LessonTypeId
            })
            .Where(group => group
                .Select(x => new { x.ModuleTopicId, x.TeacherId })
                .Distinct()
                .Skip(1)
                .Any())
            .Select(group => group.ToList()));
        foreach (var rows in logicalEvents)
        {
            if (!rows.Any(x => x.IsLocked) || !rows.Any(x => !x.IsLocked))
            {
                continue;
            }

            await tx.RollbackAsync();
            return Conflict(new
            {
                message = "Логічне заняття містить заблоковані й незаблоковані рядки. Очищення тижня не може видалити його частково.",
                batchKey = rows[0].BatchKey,
                itemIds = rows.Select(x => x.Id).OrderBy(id => id).ToList()
            });
        }

        var q = scopeQuery.Where(x => !x.IsLocked);
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
