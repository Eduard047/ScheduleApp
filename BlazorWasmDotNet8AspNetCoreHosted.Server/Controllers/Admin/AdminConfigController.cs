using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
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
        if (dto.CourseId is int cid && await db.Courses.FindAsync(cid) is null)
            return BadRequest("Course not found");
        if (dto.Id is int id && id > 0)
        {
            var l = await db.LunchConfigs.FindAsync(id) ?? throw new ArgumentException("LunchConfig not found");
            l.CourseId = dto.CourseId;
            l.Start = ParseTime(dto.Start);
            l.End = ParseTime(dto.End);
            await db.SaveChangesAsync();
            return Ok(l.Id);
        }
        else
        {
            var l = new LunchConfig { CourseId = dto.CourseId, Start = ParseTime(dto.Start), End = ParseTime(dto.End) };
            db.LunchConfigs.Add(l); await db.SaveChangesAsync(); return Ok(l.Id);
        }
    }
    [HttpDelete("lunch/{id:int}")]
    [RequireDeletionConfirmation("налаштування обідньої перерви")]
    // Видаляє обіднє налаштування.
    public async Task<IActionResult> LunchDelete(int id)
    {
        var rows = await db.LunchConfigs.Where(x => x.Id == id).ExecuteDeleteAsync();
        if (rows == 0) return NotFound();
        return NoContent();
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
        var date = DateOnly.Parse(dto.Date);
        int? courseId = dto.CourseId;
        Group? group = null;
        if (dto.GroupId is int gid && gid > 0)
        {
            group = await db.Groups.FindAsync(gid);
            if (group is null) return BadRequest("Group not found");
            courseId ??= group.CourseId;
            if (courseId != group.CourseId) return BadRequest("Group does not belong to selected course");
        }
        if (courseId is int cid && await db.Courses.FindAsync(cid) is null)
            return BadRequest("Course not found");
        if (dto.Id is int id && id > 0)
        {
            var x = await db.CalendarExceptions.FindAsync(id) ?? throw new ArgumentException("CalendarException not found");
            x.Date = date;
            x.IsWorkingDay = dto.IsWorkingDay;
            x.Name = dto.Name;
            x.CourseId = courseId;
            x.GroupId = group?.Id ?? dto.GroupId;
            await db.SaveChangesAsync(); return Ok(x.Id);
        }
        else
        {
            var x = new CalendarException
            {
                Date = date,
                IsWorkingDay = dto.IsWorkingDay,
                Name = dto.Name,
                CourseId = courseId,
                GroupId = group?.Id ?? dto.GroupId
            };
            db.CalendarExceptions.Add(x); await db.SaveChangesAsync(); return Ok(x.Id);
        }
    }
    [HttpDelete("calendar/{id:int}")]
    [RequireDeletionConfirmation("календарне виключення")]
    // Видаляє календарний виняток.
    public async Task<IActionResult> CalendarDelete(int id)
    {
        var rows = await db.CalendarExceptions.Where(x => x.Id == id).ExecuteDeleteAsync();
        if (rows == 0) return NotFound();
        return NoContent();
    }
    public sealed record BulkTimeSlotsSaveDto(int? CourseId, int? DayOfWeek, List<TimeSlotDto> Slots);
    public sealed record CloneRequest(int CourseId, int? DayOfWeek);
    private sealed record SlotRow(int Id, int? CourseId, DayOfWeek? DayOfWeek, TimeOnly Start, TimeOnly End, int SortOrder, bool IsActive);
    // Парсить час у форматі HH:mm.
    private static TimeOnly ParseTime(string s)
    {
        if (TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)) return v;
        return TimeOnly.Parse(s, CultureInfo.InvariantCulture);
    }
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
            var slotQuery = db.TimeSlots.AsNoTracking().Where(s => s.IsActive);
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
                    rows = await baseQuery.Where(s => s.CourseId == null && s.DayOfWeek == null)
                        .OrderBy(s => s.SortOrder).ThenBy(s => s.Start)
                        .ToListAsync();
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
        if (courseId is int cid && await db.Courses.FindAsync(cid) is null)
            return BadRequest(new { message = "Course not found" });
        var norm = rows.Select((r, i) => new
        {
            Start = ParseTime(r.Start),
            End = ParseTime(r.End),
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
        for (int i = 0; i < norm.Count; i++)
        {
            var s = norm[i].Start;
            var e = norm[i].End;
            if (e <= s) return BadRequest(new { message = $"Slot #{i + 1}: End must be after Start" });
            if (i > 0)
            {
                var prev = norm[i - 1];
                if (s < prev.End) return BadRequest(new { message = $"Overlap between slot #{i} and #{i + 1}" });
            }
        }
        var lunchSlot = norm.FirstOrDefault(x => x.IsLunch);
        if (lunchSlot is not null && !lunchSlot.IsActive)
        {
            return BadRequest(new { message = "Слот обіду має бути активним." });
        }
        await using var tx = await db.Database.BeginTransactionAsync();
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
        if (lunchSlot is not null)
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
        else
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
            return BadRequest(new { message = "Course not found" });
        var rows = await db.TimeSlots.Where(s => s.CourseId == courseId && s.DayOfWeek == day).ExecuteDeleteAsync();
        return rows > 0 ? NoContent() : NotFound();
    }
    [HttpPost("slots/clone-from-global")]
    // Копіює глобальні слоти в налаштування курсу.
    public async Task<IActionResult> CloneFromGlobal([FromBody] CloneRequest r)
    {
        var course = await db.Courses.FindAsync(r.CourseId);
        if (course is null) return BadRequest(new { message = "Course not found" });
        if (!TryParseDayOfWeek(r.DayOfWeek, out var day))
        {
            return BadRequest(new { message = "Некоректний день тижня." });
        }
        var global = await db.TimeSlots.AsNoTracking()
            .Where(s => s.CourseId == null && s.DayOfWeek == day)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Start)
            .ToListAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
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
}
