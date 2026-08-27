using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TimeSlotEditor;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using System.Data;
using System.Globalization;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;

[ApiController]
[Route("api/admin/config")]
// Контролер адміністратора для конфігурацій
public class AdminConfigController(AppDbContext db) : ControllerBase
{
    [HttpGet("lunch")]
    // Повертає список налаштувань обіду.
    public async Task<IReadOnlyList<LunchConfigEditDto>> LunchList()
        => await db.LunchConfigs
            .Select(l => new LunchConfigEditDto(l.Id, l.CourseId, l.Start.ToString(@"HH\:mm"), l.End.ToString(@"HH\:mm")))
            .ToListAsync();
    [HttpPost("lunch/upsert")]
    // Створює або оновлює обідній слот.
    public async Task<ActionResult<int>> LunchUpsert(LunchConfigEditDto dto)
    {
        if (dto.CourseId is <= 0)
            return BadRequest(new { message = "Ідентифікатор курсу має бути додатним числом." });
        if (!TryParseTime(dto.Start, out var start) || !TryParseTime(dto.End, out var end))
            return BadRequest(new { message = "Час обіду потрібно вказати у форматі HH:mm." });
        if (end <= start)
            return BadRequest(new { message = "Кінець обідньої перерви має бути пізніше за початок." });

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (dto.CourseId is int courseId && !await db.Courses.AnyAsync(course => course.Id == courseId))
                return NotFound(new { message = "Курс не знайдено." });
            var requestedId = dto.Id is > 0 ? dto.Id.Value : 0;
            LunchConfig lunch;
            if (requestedId > 0)
            {
                var existing = await db.LunchConfigs.FindAsync(requestedId);
                if (existing is null) return NotFound(new { message = "Налаштування обіду не знайдено." });
                lunch = existing;
            }
            else
            {
                lunch = new LunchConfig();
                db.LunchConfigs.Add(lunch);
            }

            var duplicateExists = await db.LunchConfigs.AnyAsync(existing =>
                existing.Id != lunch.Id && existing.CourseId == dto.CourseId);
            if (duplicateExists)
            {
                return Conflict(new
                {
                    message = "Для вибраного курсу або глобальної області вже налаштовано обідню перерву. Відредагуйте наявний запис."
                });
            }

            var originalScope = lunch.Id > 0 ? lunch.CourseId : dto.CourseId;
            var replacement = new LunchConfig
            {
                CourseId = dto.CourseId,
                Start = start,
                End = end
            };
            var impact = await FindSlotMutationImpactAsync(
                changedCourseId: null,
                changedDay: null,
                replacementSlots: Array.Empty<TimeSlot>(),
                replaceSlots: false,
                replaceLunch: true,
                lunchScopeToReplace: originalScope,
                replacementLunch: replacement);
            if (impact.Count > 0)
                return Conflict(new { message = impact.ToMessage() });

            lunch.CourseId = dto.CourseId;
            lunch.Start = start;
            lunch.End = end;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Ok(lunch.Id);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
    [HttpDelete("lunch/{id:int}")]
    [RequireDeletionConfirmation("налаштування обідньої перерви")]
    // Видаляє обіднє налаштування.
    public async Task<IActionResult> LunchDelete(int id)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var lunch = await db.LunchConfigs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id);
            if (lunch is null) return NotFound();
            var remainingInScope = await db.LunchConfigs
                .AsNoTracking()
                .Where(item => item.CourseId == lunch.CourseId && item.Id != id)
                .OrderBy(item => item.Id)
                .FirstOrDefaultAsync();
            var replacement = remainingInScope is null
                ? null
                : new LunchConfig
                {
                    CourseId = remainingInScope.CourseId,
                    Start = remainingInScope.Start,
                    End = remainingInScope.End
                };
            var impact = await FindSlotMutationImpactAsync(
                changedCourseId: null,
                changedDay: null,
                replacementSlots: Array.Empty<TimeSlot>(),
                replaceSlots: false,
                replaceLunch: true,
                lunchScopeToReplace: lunch.CourseId,
                replacementLunch: replacement);
            if (impact.Count > 0)
                return Conflict(new { message = impact.ToMessage() });
            var rows = await db.LunchConfigs.Where(item => item.Id == id).ExecuteDeleteAsync();
            if (rows == 0) return NotFound();
            await tx.CommitAsync();
            return NoContent();
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
    [HttpGet("preferred-first-slot-limit")]
    // Повертає ліміт слота для типу з прапорцем "Бажано першим у тижні".
    public async Task<ActionResult<PreferredFirstSlotLimitConfigEditDto>> PreferredFirstSlotLimitGet([FromQuery] int? courseId)
    {
        if (courseId is int cid && await db.Courses.FindAsync(cid) is null)
            return BadRequest(new { message = "Курс не знайдено." });
        var row = await db.PreferredFirstSlotLimitConfigs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CourseId == courseId);
        return Ok(row is null
            ? new PreferredFirstSlotLimitConfigEditDto(null, courseId, 0)
            : new PreferredFirstSlotLimitConfigEditDto(row.Id, row.CourseId, row.MaxSlotOrder));
    }
    [HttpPost("preferred-first-slot-limit/upsert")]
    // Створює або оновлює ліміт слота для типу з прапорцем "Бажано першим у тижні".
    public async Task<ActionResult<int>> PreferredFirstSlotLimitUpsert(PreferredFirstSlotLimitConfigEditDto dto)
    {
        if (dto.CourseId is int cid && await db.Courses.FindAsync(cid) is null)
            return BadRequest(new { message = "Курс не знайдено." });
        if (dto.MaxSlotOrder < 0)
            return BadRequest(new { message = "Ліміт слота не може бути від'ємним." });
        if (dto.MaxSlotOrder == 0)
        {
            await db.PreferredFirstSlotLimitConfigs.Where(x => x.CourseId == dto.CourseId).ExecuteDeleteAsync();
            return Ok(0);
        }
        var row = await db.PreferredFirstSlotLimitConfigs.FirstOrDefaultAsync(x => x.CourseId == dto.CourseId);
        if (row is null)
        {
            row = new PreferredFirstSlotLimitConfig
            {
                CourseId = dto.CourseId,
                MaxSlotOrder = dto.MaxSlotOrder
            };
            db.PreferredFirstSlotLimitConfigs.Add(row);
        }
        else
        {
            row.MaxSlotOrder = dto.MaxSlotOrder;
        }
        await db.SaveChangesAsync();
        return Ok(row.Id);
    }
    [HttpGet("calendar")]
    // Повертає календар винятків.
    public async Task<IReadOnlyList<CalendarExceptionEditDto>> CalendarList()
        => await db.CalendarExceptions
            .OrderBy(x => x.Date)
            .Select(x => new CalendarExceptionEditDto(x.Id, x.Date.ToString("yyyy-MM-dd"), x.IsWorkingDay, x.Name, x.CourseId, x.GroupId))
            .ToListAsync();
    [HttpPost("calendar/upsert")]
    // Створює або оновлює календарний виняток.
    public async Task<ActionResult<int>> CalendarUpsert(CalendarExceptionEditDto dto)
    {
        if (!DateOnly.TryParseExact(
                dto.Date,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return BadRequest(new { message = "Дату потрібно вказати у форматі yyyy-MM-dd." });
        }
        if (!DateHelpers.IsSupportedScheduleDate(date))
        {
            return BadRequest(new { message = DateHelpers.SupportedScheduleDateMessage });
        }
        if (dto.CourseId is <= 0)
            return BadRequest(new { message = "Ідентифікатор курсу має бути додатним числом." });
        if (dto.GroupId is <= 0)
            return BadRequest(new { message = "Ідентифікатор групи має бути додатним числом." });

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            int? courseId = dto.CourseId;
            Group? group = null;
            if (dto.GroupId is int groupId)
            {
                group = await db.Groups.FindAsync(groupId);
                if (group is null) return BadRequest(new { message = "Групу не знайдено." });
                courseId ??= group.CourseId;
                if (courseId != group.CourseId)
                    return BadRequest(new { message = "Група не належить вибраному курсу." });
            }
            if (courseId is int requestedCourseId && !await db.Courses.AnyAsync(course => course.Id == requestedCourseId))
                return BadRequest(new { message = "Курс не знайдено." });

            var resolvedGroupId = group?.Id;
            var requestedId = dto.Id is > 0 ? dto.Id.Value : 0;
            var duplicateExists = await db.CalendarExceptions.AnyAsync(exception =>
                exception.Id != requestedId
                && exception.Date == date
                && exception.CourseId == courseId
                && exception.GroupId == resolvedGroupId);
            if (duplicateExists)
            {
                return Conflict(new
                {
                    message = "Для цієї дати та області дії вже існує календарний виняток. Відредагуйте наявний запис."
                });
            }

            CalendarException? existingException = null;
            if (requestedId > 0)
            {
                var existing = await db.CalendarExceptions.FindAsync(requestedId);
                if (existing is null) return NotFound(new { message = "Календарний виняток не знайдено." });
                existingException = existing;
            }

            var replacement = new CalendarException
            {
                Id = requestedId,
                Date = date,
                IsWorkingDay = dto.IsWorkingDay,
                Name = dto.Name?.Trim() ?? string.Empty,
                CourseId = courseId,
                GroupId = resolvedGroupId
            };
            var affectedDates = new[] { date, existingException?.Date ?? date }
                .Distinct()
                .ToList();
            var impact = await FindCalendarMutationImpactAsync(requestedId, replacement, affectedDates);
            if (impact.Count > 0)
            {
                return Conflict(new { message = impact.ToMessage() });
            }

            var exception = existingException ?? new CalendarException();
            if (existingException is null)
            {
                db.CalendarExceptions.Add(exception);
            }
            exception.Date = date;
            exception.IsWorkingDay = dto.IsWorkingDay;
            exception.Name = replacement.Name;
            exception.CourseId = courseId;
            exception.GroupId = resolvedGroupId;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Ok(exception.Id);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
    [HttpDelete("calendar/{id:int}")]
    [RequireDeletionConfirmation("календарне виключення")]
    // Видаляє календарний виняток.
    public async Task<IActionResult> CalendarDelete(int id)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var exception = await db.CalendarExceptions.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == id);
            if (exception is null) return NotFound();
            var impact = await FindCalendarMutationImpactAsync(
                id,
                replacement: null,
                new[] { exception.Date });
            if (impact.Count > 0)
            {
                return Conflict(new { message = impact.ToMessage() });
            }

            var rows = await db.CalendarExceptions.Where(item => item.Id == id).ExecuteDeleteAsync();
            if (rows == 0) return NotFound();
            await tx.CommitAsync();
            return NoContent();
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private sealed record CalendarPlacement(
        string Source,
        int Id,
        DateOnly Date,
        int CourseId,
        int GroupId,
        string GroupName);

    private sealed record CalendarMutationImpact(int Count, IReadOnlyList<string> Samples)
    {
        public string ToMessage()
            => $"Зміна календаря зробить неробочими {Count} наявних занять. "
               + $"Спочатку перенесіть їх або збережіть точніше робоче виключення: {string.Join("; ", Samples)}";
    }

    // Симулює календар після зміни з урахуванням пріоритету групи, курсу та глобальної області.
    private async Task<CalendarMutationImpact> FindCalendarMutationImpactAsync(
        int excludedExceptionId,
        CalendarException? replacement,
        IReadOnlyCollection<DateOnly> affectedDates)
    {
        var dates = affectedDates.Distinct().ToList();
        var calendarBefore = await db.CalendarExceptions.AsNoTracking()
            .Where(item => dates.Contains(item.Date))
            .ToListAsync();
        var calendarAfter = calendarBefore
            .Where(item => item.Id != excludedExceptionId)
            .ToList();
        if (replacement is not null)
        {
            calendarAfter.Add(replacement);
        }

        var scheduleRows = await db.ScheduleItems.AsNoTracking()
            .Where(item => dates.Contains(item.Date))
            .Select(item => new
            {
                item.Id,
                item.Date,
                item.GroupId,
                item.Group.CourseId,
                GroupName = item.Group.Name
            })
            .ToListAsync();
        var draftRows = await db.TeacherDraftItems.AsNoTracking()
            .Where(item => dates.Contains(item.Date))
            .Select(item => new
            {
                item.Id,
                item.Date,
                item.GroupId,
                item.Group.CourseId,
                GroupName = item.Group.Name
            })
            .ToListAsync();
        var placements = scheduleRows
            .Select(item => new CalendarPlacement(
                "розклад",
                item.Id,
                item.Date,
                item.CourseId,
                item.GroupId,
                item.GroupName))
            .Concat(draftRows.Select(item => new CalendarPlacement(
                "чернетка",
                item.Id,
                item.Date,
                item.CourseId,
                item.GroupId,
                item.GroupName)))
            .ToList();
        var affected = placements
            .Where(placement => IsCalendarWorkingDay(
                                    calendarBefore,
                                    placement.Date,
                                    placement.CourseId,
                                    placement.GroupId)
                                && !IsCalendarWorkingDay(
                                    calendarAfter,
                                    placement.Date,
                                    placement.CourseId,
                                    placement.GroupId))
            .ToList();
        var samples = affected
            .Take(10)
            .Select(placement =>
                $"{placement.Source} #{placement.Id}, {placement.GroupName}, {placement.Date:yyyy-MM-dd}")
            .ToList();
        return new CalendarMutationImpact(affected.Count, samples);
    }

    private static bool IsCalendarWorkingDay(
        IEnumerable<CalendarException> calendar,
        DateOnly date,
        int courseId,
        int groupId)
    {
        var match = calendar
            .Where(item => item.Date == date)
            .Where(item => item.GroupId == groupId || item.GroupId == null)
            .Where(item => item.CourseId == courseId || item.CourseId == null)
            .OrderByDescending(item => item.GroupId != null)
            .ThenByDescending(item => item.CourseId != null)
            .FirstOrDefault();
        return match?.IsWorkingDay ?? true;
    }

    public sealed record BulkTimeSlotsSaveDto(int? CourseId, int? DayOfWeek, List<TimeSlotDto> Slots);
    public sealed record CloneRequest(int CourseId, int? DayOfWeek);
    private sealed record SlotRow(int Id, int? CourseId, DayOfWeek? DayOfWeek, TimeOnly Start, TimeOnly End, int SortOrder, bool IsActive);
    // Перевіряє час у стабільному форматі HH:mm.
    private static bool TryParseTime(string? value, out TimeOnly time)
        => TimeOnly.TryParseExact(
            value,
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out time);
    private static bool TryParseDayOfWeek(int? raw, out DayOfWeek? day)
    {
        day = null;
        if (raw is null) return true;
        if (!Enum.IsDefined(typeof(DayOfWeek), raw.Value)) return false;
        day = (DayOfWeek)raw.Value;
        return true;
    }
    // Перевіряє, чи слот співпадає з обідом.
    private static bool SlotMatchesLunch(TimeOnly start, TimeOnly end, LunchConfig? lunch)
        => lunch is not null && lunch.Start == start && lunch.End == end;
    [HttpGet("slots")]
    // Повертає ефективні тайм-слоти з урахуванням курсу.
    public async Task<IActionResult> GetEffectiveSlots([FromQuery] int? courseId, [FromQuery] int? dayOfWeek, [FromQuery] bool includeDayOverrides = false)
    {
        if (!TryParseDayOfWeek(dayOfWeek, out var day))
        {
            return BadRequest(new { message = "Некоректний день тижня." });
        }
        LunchConfig? lunch = null;
        if (courseId is int cidExact)
        {
            lunch = await db.LunchConfigs.AsNoTracking().FirstOrDefaultAsync(l => l.CourseId == cidExact);
        }
        lunch ??= await db.LunchConfigs.AsNoTracking().FirstOrDefaultAsync(l => l.CourseId == null);
        if (day is null && includeDayOverrides)
        {
            var slotQuery = db.TimeSlots.AsNoTracking();
            if (courseId is int cidForWeek)
            {
                slotQuery = slotQuery.Where(s => s.CourseId == null || s.CourseId == cidForWeek);
            }
            var allSlots = await slotQuery.ToListAsync();
            if (courseId is null)
            {
                var slots = allSlots
                    .OrderBy(s => s.DayOfWeek.HasValue ? (int)s.DayOfWeek.Value : 7)
                    .ThenBy(s => s.SortOrder)
                    .ThenBy(s => s.Start)
                    .Select(s => new TimeSlotDto
                    {
                        Id = s.Id,
                        CourseId = s.CourseId,
                        DayOfWeek = s.DayOfWeek is DayOfWeek d ? (int)d : null,
                        Start = s.Start.ToString("HH:mm"),
                        End = s.End.ToString("HH:mm"),
                        SortOrder = s.SortOrder,
                        IsActive = s.IsActive,
                        IsLunch = SlotMatchesLunch(s.Start, s.End, lunch) && s.IsActive
                    })
                    .ToList();
                var usingCourseSpecific = allSlots.Any(s => s.CourseId != null);
                return Ok(new { courseId, dayOfWeek = (int?)null, usingCourseSpecific, slots });
            }
            var resolved = TimeSlotsResolver.ResolveForWeek(allSlots, courseId);
            var resolvedSlots = new List<TimeSlotDto>();
            foreach (var entry in resolved.OrderBy(x => x.Key))
            {
                foreach (var s in entry.Value.Slots)
                {
                    resolvedSlots.Add(new TimeSlotDto
                    {
                        Id = s.Id,
                        CourseId = s.CourseId,
                        DayOfWeek = (int)entry.Key,
                        Start = s.Start.ToString("HH:mm"),
                        End = s.End.ToString("HH:mm"),
                        SortOrder = s.SortOrder,
                        IsActive = s.IsActive,
                        IsLunch = SlotMatchesLunch(s.Start, s.End, lunch) && s.IsActive
                    });
                }
            }
            var usingCourseSpecificResolved = resolved.Values.Any(x => x.UsingCourseSpecific);
            return Ok(new { courseId, dayOfWeek = (int?)null, usingCourseSpecific = usingCourseSpecificResolved, slots = resolvedSlots });
        }
        if (day is null)
        {
            var baseQuery = db.TimeSlots.AsNoTracking().Where(s => s.IsActive);
            List<TimeSlot> rows;
            bool usingCourseSpecific = false;
            if (courseId is int cid)
            {
                rows = await baseQuery.Where(s => s.CourseId == cid && s.DayOfWeek == null)
                    .OrderBy(s => s.SortOrder).ThenBy(s => s.Start)
                    .ToListAsync();
                if (rows.Count > 0)
                {
                    usingCourseSpecific = true;
                }
                else
                {
                    var hasAnyCourseSlots = await baseQuery.AnyAsync(s => s.CourseId == cid);
                    if (hasAnyCourseSlots)
                    {
                        // Якщо для курсу є власні слоти, не підмішуємо глобальний шаблон.
                        usingCourseSpecific = true;
                        rows = new List<TimeSlot>();
                    }
                    else
                    {
                        rows = await baseQuery.Where(s => s.CourseId == null && s.DayOfWeek == null)
                            .OrderBy(s => s.SortOrder).ThenBy(s => s.Start)
                            .ToListAsync();
                    }
                }
            }
            else
            {
                rows = await baseQuery.Where(s => s.CourseId == null && s.DayOfWeek == null)
                    .OrderBy(s => s.SortOrder).ThenBy(s => s.Start)
                    .ToListAsync();
            }
            var slots = rows.Select(s => new TimeSlotDto
            {
                Id = s.Id,
                CourseId = s.CourseId,
                DayOfWeek = null,
                Start = s.Start.ToString("HH:mm"),
                End = s.End.ToString("HH:mm"),
                SortOrder = s.SortOrder,
                IsActive = s.IsActive,
                IsLunch = SlotMatchesLunch(s.Start, s.End, lunch) && s.IsActive
            }).ToList();
            return Ok(new { courseId, dayOfWeek = (int?)null, usingCourseSpecific, slots });
        }
        var resolvedDay = await TimeSlotsResolver.ResolveForDayAsync(db, courseId, day.Value);
        var daySlots = resolvedDay.Slots.Select(s => new TimeSlotDto
        {
            Id = s.Id,
            CourseId = s.CourseId,
            DayOfWeek = (int)day.Value,
            Start = s.Start.ToString("HH:mm"),
            End = s.End.ToString("HH:mm"),
            SortOrder = s.SortOrder,
            IsActive = s.IsActive,
            IsLunch = SlotMatchesLunch(s.Start, s.End, lunch) && s.IsActive
        }).ToList();
        return Ok(new { courseId, dayOfWeek = (int)day.Value, usingCourseSpecific = resolvedDay.UsingCourseSpecific, usingDaySpecific = resolvedDay.UsingDaySpecific, slots = daySlots });
    }
    [HttpGet("slots/editor-context")]
    // Повертає компактний контекст для візуального редактора графіка пар.
    public async Task<IActionResult> GetTimeSlotEditorContext(
        [FromQuery] TimeSlotEditorTargetMode targetMode,
        [FromQuery] int? courseId,
        [FromQuery] int? dayOfWeek,
        CancellationToken cancellationToken)
    {
        var outcome = await new TimeSlotEditorService(db).GetContextAsync(
            targetMode,
            courseId,
            dayOfWeek,
            cancellationToken);
        return outcome.IsSuccess
            ? Ok(outcome.Value)
            : MapTimeSlotEditorFailure(outcome.Failure!);
    }

    [HttpPost("slots/editor/preview")]
    // Перевіряє графік і показує точний вплив без зміни даних.
    public async Task<IActionResult> PreviewTimeSlotSequence(
        [FromBody] TimeSlotSequenceApplyRequestDto request,
        CancellationToken cancellationToken)
    {
        var outcome = await new TimeSlotEditorService(db).PreviewAsync(request, cancellationToken);
        return outcome.IsSuccess
            ? Ok(outcome.Value)
            : MapTimeSlotEditorFailure(outcome.Failure!);
    }

    [HttpPost("slots/editor/apply")]
    // Атомарно застосовує лише попередньо перевірений графік.
    public async Task<IActionResult> ApplyTimeSlotSequence(
        [FromBody] TimeSlotSequenceApplyRequestDto request,
        CancellationToken cancellationToken)
    {
        var outcome = await new TimeSlotEditorService(db).ApplyAsync(request, cancellationToken);
        return outcome.IsSuccess
            ? Ok(outcome.Value)
            : MapTimeSlotEditorFailure(outcome.Failure!);
    }

    private IActionResult MapTimeSlotEditorFailure(TimeSlotEditorFailure failure)
    {
        var body = new
        {
            code = failure.Kind.ToString().ToLowerInvariant(),
            message = failure.Message,
            currentRevision = failure.CurrentRevision
        };
        return failure.Kind switch
        {
            TimeSlotEditorFailureKind.Validation => BadRequest(body),
            TimeSlotEditorFailureKind.NotFound => NotFound(body),
            TimeSlotEditorFailureKind.Stale => Conflict(body),
            TimeSlotEditorFailureKind.Conflict => Conflict(body),
            _ => BadRequest(body)
        };
    }

    [HttpGet("slots/raw")]
    // Повертає сирі тайм-слоти (глобальні та курсні).
    public async Task<IActionResult> GetRawSlots([FromQuery] int? courseId, [FromQuery] int? dayOfWeek)
    {
        if (!TryParseDayOfWeek(dayOfWeek, out var day))
        {
            return BadRequest(new { message = "Некоректний день тижня." });
        }
        var lunches = await db.LunchConfigs.AsNoTracking().ToListAsync();
        LunchConfig? courseLunch = null;
        if (courseId is int cidRaw)
        {
            courseLunch = lunches.FirstOrDefault(l => l.CourseId == cidRaw);
        }
        var globalLunch = lunches.FirstOrDefault(l => l.CourseId == null);
        var baseQuery = db.TimeSlots.AsNoTracking()
            .Where(s => s.DayOfWeek == day);
        List<SlotRow> courseRows = new();
        if (courseId is int cid)
        {
            courseRows = await baseQuery.Where(s => s.CourseId == cid)
                                  .OrderBy(s => s.SortOrder).ThenBy(s => s.Start)
                                  .Select(s => new SlotRow(s.Id, s.CourseId, s.DayOfWeek, s.Start, s.End, s.SortOrder, s.IsActive))
                                  .ToListAsync();
        }
        var globalRows = await baseQuery.Where(s => s.CourseId == null)
                                  .OrderBy(s => s.SortOrder).ThenBy(s => s.Start)
                                  .Select(s => new SlotRow(s.Id, s.CourseId, s.DayOfWeek, s.Start, s.End, s.SortOrder, s.IsActive))
                                  .ToListAsync();
        List<TimeSlotDto> course = courseRows.Select(s => new TimeSlotDto
        {
            Id = s.Id,
            CourseId = s.CourseId,
            DayOfWeek = s.DayOfWeek is DayOfWeek d ? (int)d : null,
            Start = s.Start.ToString("HH:mm"),
            End = s.End.ToString("HH:mm"),
            SortOrder = s.SortOrder,
            IsActive = s.IsActive,
            IsLunch = SlotMatchesLunch(s.Start, s.End, courseLunch) && s.IsActive
        }).ToList();
        List<TimeSlotDto> global = globalRows.Select(s => new TimeSlotDto
        {
            Id = s.Id,
            CourseId = s.CourseId,
            DayOfWeek = s.DayOfWeek is DayOfWeek d ? (int)d : null,
            Start = s.Start.ToString("HH:mm"),
            End = s.End.ToString("HH:mm"),
            SortOrder = s.SortOrder,
            IsActive = s.IsActive,
            IsLunch = SlotMatchesLunch(s.Start, s.End, globalLunch) && s.IsActive
        }).ToList();
        return Ok(new { course, global });
    }
    [HttpPost("slots/upsert-bulk")]
    // Зберігає набір тайм-слотів одним запитом.
    public async Task<IActionResult> UpsertSlots([FromBody] BulkTimeSlotsSaveDto body)
    {
        var courseId = body?.CourseId;
        var dayRaw = body?.DayOfWeek;
        if (!TryParseDayOfWeek(dayRaw, out var day))
        {
            return BadRequest(new { message = "Некоректний день тижня." });
        }
        var rows = body?.Slots ?? new();
        if (rows.Count > 100)
            return BadRequest(new { message = "Для одного дня можна зберегти не більше 100 часових слотів." });
        if (courseId is int cid && await db.Courses.FindAsync(cid) is null)
            return BadRequest(new { message = "Курс не знайдено." });
        var invalidTimeIndex = rows.FindIndex(row =>
            !TryParseTime(row.Start, out _) || !TryParseTime(row.End, out _));
        if (invalidTimeIndex >= 0)
        {
            return BadRequest(new
            {
                message = $"Слот #{invalidTimeIndex + 1}: час потрібно вказати у форматі HH:mm."
            });
        }
        var norm = rows.Select((r, i) => new
        {
            Start = TimeOnly.ParseExact(r.Start, "HH:mm", CultureInfo.InvariantCulture),
            End = TimeOnly.ParseExact(r.End, "HH:mm", CultureInfo.InvariantCulture),
            IsActive = r.IsActive,
            IsLunch = r.IsLunch,
            Sort = r.SortOrder <= 0 ? i + 1 : r.SortOrder
        })
        .OrderBy(x => x.Sort).ThenBy(x => x.Start).ToList();
        var lunchCount = norm.Count(x => x.IsLunch);
        if (lunchCount > 1)
        {
            return BadRequest(new { message = "Може бути лише один слот, позначений як обід." });
        }
        if (day is not null && lunchCount > 0)
        {
            return BadRequest(new { message = "Обідня перерва є спільною для всіх днів. Налаштуйте її у шаблоні «Усі дні»." });
        }
        for (int i = 0; i < norm.Count; i++)
        {
            var s = norm[i].Start;
            var e = norm[i].End;
            if (e <= s) return BadRequest(new { message = $"Слот #{i + 1}: час завершення має бути пізніше за час початку." });
            if (i > 0)
            {
                var prev = norm[i - 1];
                if (s < prev.End) return BadRequest(new { message = $"Слоти #{i} і #{i + 1} перетинаються." });
            }
        }
        var lunchSlot = norm.FirstOrDefault(x => x.IsLunch);
        if (lunchSlot is not null && !lunchSlot.IsActive)
        {
            return BadRequest(new { message = "Слот обіду має бути активним." });
        }
        var replacementSlots = norm.Select(x => new TimeSlot
        {
            CourseId = courseId,
            DayOfWeek = day,
            Start = x.Start,
            End = x.End,
            SortOrder = x.Sort,
            IsActive = x.IsActive
        }).ToList();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var managesLunch = day is null;
        var replacementLunch = !managesLunch || lunchSlot is null
            ? null
            : new LunchConfig
            {
                CourseId = courseId,
                Start = lunchSlot.Start,
                End = lunchSlot.End
            };
        var impact = await FindSlotMutationImpactAsync(
            courseId,
            day,
            replacementSlots,
            replaceSlots: true,
            replaceLunch: managesLunch,
            lunchScopeToReplace: courseId,
            replacementLunch: replacementLunch);
        if (impact.Count > 0)
        {
            return Conflict(new { message = impact.ToMessage() });
        }
        await db.TimeSlots.Where(s => s.CourseId == courseId && s.DayOfWeek == day).ExecuteDeleteAsync();
        int sort = 1;
        foreach (var x in norm)
        {
            db.TimeSlots.Add(new TimeSlot
            {
                CourseId = courseId,
                DayOfWeek = day,
                Start = x.Start,
                End = x.End,
                SortOrder = sort++,
                IsActive = x.IsActive
            });
        }
        if (managesLunch && lunchSlot is not null)
        {
            var lunchRow = await db.LunchConfigs.FirstOrDefaultAsync(l => l.CourseId == courseId);
            if (lunchRow is null)
            {
                db.LunchConfigs.Add(new LunchConfig
                {
                    CourseId = courseId,
                    Start = lunchSlot.Start,
                    End = lunchSlot.End
                });
            }
            else
            {
                lunchRow.Start = lunchSlot.Start;
                lunchRow.End = lunchSlot.End;
            }
        }
        else if (managesLunch)
        {
            await db.LunchConfigs.Where(l => l.CourseId == courseId).ExecuteDeleteAsync();
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return Ok();
    }
    [HttpDelete("slots/clear")]
    [RequireDeletionConfirmation("слоти розкладу", TargetArgumentName = nameof(courseId))]
    // Очищає тайм-слоти для курсу або глобальні.
    public async Task<IActionResult> ClearSlots([FromQuery] int? courseId, [FromQuery] int? dayOfWeek)
    {
        if (!TryParseDayOfWeek(dayOfWeek, out var day))
        {
            return BadRequest(new { message = "Некоректний день тижня." });
        }
        if (courseId is int cid && await db.Courses.FindAsync(cid) is null)
            return BadRequest(new { message = "Курс не знайдено." });
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var exists = await db.TimeSlots.AnyAsync(slot => slot.CourseId == courseId && slot.DayOfWeek == day);
        if (!exists) return NotFound();
        var impact = await FindSlotMutationImpactAsync(courseId, day, Array.Empty<TimeSlot>());
        if (impact.Count > 0)
        {
            return Conflict(new { message = impact.ToMessage() });
        }
        var rows = await db.TimeSlots.Where(s => s.CourseId == courseId && s.DayOfWeek == day).ExecuteDeleteAsync();
        if (rows == 0) return NotFound();
        await tx.CommitAsync();
        return NoContent();
    }
    [HttpPost("slots/clone-from-global")]
    // Копіює глобальні слоти в налаштування курсу.
    public async Task<IActionResult> CloneFromGlobal([FromBody] CloneRequest r)
    {
        if (r.CourseId <= 0)
            return BadRequest(new { message = "Ідентифікатор курсу має бути додатним числом." });
        if (!TryParseDayOfWeek(r.DayOfWeek, out var day))
        {
            return BadRequest(new { message = "Некоректний день тижня." });
        }
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var course = await db.Courses.FindAsync(r.CourseId);
        if (course is null) return BadRequest(new { message = "Курс не знайдено." });
        var global = await db.TimeSlots.AsNoTracking()
            .Where(s => s.CourseId == null && s.DayOfWeek == day)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Start)
            .ToListAsync();
        var replacementSlots = global.Select(slot => new TimeSlot
        {
            CourseId = r.CourseId,
            DayOfWeek = day,
            Start = slot.Start,
            End = slot.End,
            SortOrder = slot.SortOrder,
            IsActive = slot.IsActive
        }).ToList();
        var impact = await FindSlotMutationImpactAsync(r.CourseId, day, replacementSlots);
        if (impact.Count > 0)
        {
            return Conflict(new { message = impact.ToMessage() });
        }
        await db.TimeSlots.Where(s => s.CourseId == r.CourseId && s.DayOfWeek == day).ExecuteDeleteAsync();
        foreach (var s in global)
        {
            db.TimeSlots.Add(new TimeSlot
            {
                CourseId = r.CourseId,
                DayOfWeek = day,
                Start = s.Start,
                End = s.End,
                SortOrder = s.SortOrder,
                IsActive = s.IsActive
            });
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return Ok();
    }

    private sealed record SlotPlacement(
        string Source,
        int Id,
        DateOnly Date,
        TimeOnly Start,
        TimeOnly End,
        int CourseId,
        string GroupName);

    private sealed record SlotMutationImpact(int Count, IReadOnlyList<string> Samples)
    {
        public string ToMessage()
            => $"Зміна часових слотів зробить недійсними {Count} наявних занять. "
               + $"Спочатку перенесіть їх у дозволені слоти: {string.Join("; ", Samples)}";
    }

    // Перевіряє, що зміна конфігурації не зламає вже збережені заняття.
    private async Task<SlotMutationImpact> FindSlotMutationImpactAsync(
        int? changedCourseId,
        DayOfWeek? changedDay,
        IReadOnlyCollection<TimeSlot> replacementSlots,
        bool replaceSlots = true,
        bool replaceLunch = false,
        int? lunchScopeToReplace = null,
        LunchConfig? replacementLunch = null)
    {
        var slotsBefore = await db.TimeSlots
            .AsNoTracking()
            .ToListAsync();
        var slotsAfter = replaceSlots
            ? slotsBefore
                .Where(slot => slot.CourseId != changedCourseId || slot.DayOfWeek != changedDay)
                .ToList()
            : slotsBefore.ToList();
        if (replaceSlots)
        {
            slotsAfter.AddRange(replacementSlots);
        }
        var lunchesBefore = await db.LunchConfigs.AsNoTracking().ToListAsync();
        var lunchesAfter = replaceLunch
            ? lunchesBefore.Where(lunch => lunch.CourseId != lunchScopeToReplace).ToList()
            : lunchesBefore.ToList();
        if (replaceLunch && replacementLunch is not null)
        {
            lunchesAfter.Add(replacementLunch);
        }

        var scheduleQuery = db.ScheduleItems.AsNoTracking().AsQueryable();
        var draftQuery = db.TeacherDraftItems.AsNoTracking().AsQueryable();
        if (changedCourseId is int courseId)
        {
            scheduleQuery = scheduleQuery.Where(item => item.Group.CourseId == courseId);
            draftQuery = draftQuery.Where(item => item.Group.CourseId == courseId);
        }
        if (changedDay is DayOfWeek day)
        {
            scheduleQuery = scheduleQuery.Where(item => item.DayOfWeek == day);
            draftQuery = draftQuery.Where(item => item.DayOfWeek == day);
        }

        var scheduleRows = await scheduleQuery
            .Select(item => new
            {
                item.Id,
                item.Date,
                Start = item.StartTime,
                End = item.EndTime,
                item.Group.CourseId,
                GroupName = item.Group.Name
            })
            .ToListAsync();
        var draftRows = await draftQuery
            .Select(item => new
            {
                item.Id,
                item.Date,
                Start = item.StartTime,
                End = item.EndTime,
                item.Group.CourseId,
                GroupName = item.Group.Name
            })
            .ToListAsync();
        var placements = scheduleRows
            .Select(item => new SlotPlacement(
                "розклад",
                item.Id,
                item.Date,
                item.Start,
                item.End,
                item.CourseId,
                item.GroupName))
            .Concat(draftRows.Select(item => new SlotPlacement(
                "чернетка",
                item.Id,
                item.Date,
                item.Start,
                item.End,
                item.CourseId,
                item.GroupName)))
            .ToList();

        var affected = placements
            .Where(placement => PlacementFitsSlots(placement, slotsBefore, lunchesBefore)
                                && !PlacementFitsSlots(placement, slotsAfter, lunchesAfter))
            .ToList();
        var samples = affected
            .Take(10)
            .Select(placement =>
                $"{placement.Source} #{placement.Id}, {placement.GroupName}, {placement.Date:yyyy-MM-dd} {placement.Start:HH\\:mm}-{placement.End:HH\\:mm}")
            .ToList();
        return new SlotMutationImpact(affected.Count, samples);
    }

    private static bool PlacementFitsSlots(
        SlotPlacement placement,
        IReadOnlyCollection<TimeSlot> configuration,
        IReadOnlyCollection<LunchConfig> lunches)
    {
        var slots = TimeSlotsResolver.ResolveForDay(
                configuration,
                placement.CourseId,
                placement.Date.DayOfWeek,
                lunches)
            .Slots;
        return slots.Count > 0 && SlotRangeAllowed(placement.Start, placement.End, slots);
    }

    private static bool SlotRangeAllowed(TimeOnly start, TimeOnly end, IReadOnlyCollection<TimeSlot> slots)
    {
        var ordered = slots.OrderBy(slot => slot.Start).ThenBy(slot => slot.End).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Start != start) continue;
            for (var j = i; j < ordered.Count; j++)
            {
                if (j > i && ordered[j - 1].End != ordered[j].Start) break;
                if (ordered[j].End == end) return true;
            }
        }
        return false;
    }
}
