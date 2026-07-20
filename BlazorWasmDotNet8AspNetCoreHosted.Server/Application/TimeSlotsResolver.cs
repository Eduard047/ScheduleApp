using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

public static class TimeSlotsResolver
{
    public sealed record ResolvedTimeSlots(bool UsingCourseSpecific, bool UsingDaySpecific, List<TimeSlot> Slots);

    public static async Task<ResolvedTimeSlots> ResolveForDayAsync(
        AppDbContext db,
        int? courseId,
        DayOfWeek day,
        bool activeOnly = true)
    {
        var slots = await db.TimeSlots.AsNoTracking()
            .Where(s => s.CourseId == null || s.CourseId == courseId)
            .ToListAsync();
        var lunches = await db.LunchConfigs
            .AsNoTracking()
            .Where(lunch => lunch.CourseId == null || lunch.CourseId == courseId)
            .ToListAsync();
        return ResolveForDay(slots, courseId, day, lunches, activeOnly);
    }

    public static ResolvedTimeSlots ResolveForDay(
        IEnumerable<TimeSlot> slots,
        int? courseId,
        DayOfWeek day,
        IEnumerable<LunchConfig>? lunches = null,
        bool activeOnly = true)
    {
        var slotList = slots.ToList();
        List<TimeSlot> Pick(IEnumerable<TimeSlot> source, DayOfWeek? targetDay)
            => source.Where(s => s.DayOfWeek == targetDay)
                     .OrderBy(s => s.SortOrder)
                     .ThenBy(s => s.Start)
                     .ToList();

        if (courseId is int cid)
        {
            var courseSlots = slotList.Where(s => s.CourseId == cid).ToList();
            if (courseSlots.Count > 0)
            {
                var courseDay = Pick(courseSlots, day);
                if (courseDay.Count > 0)
                {
                    return Finalize(new ResolvedTimeSlots(true, true, courseDay), courseId, lunches, activeOnly);
                }
                var courseAny = Pick(courseSlots, null);
                if (courseAny.Count > 0)
                {
                    return Finalize(new ResolvedTimeSlots(true, false, courseAny), courseId, lunches, activeOnly);
                }
                // Якщо курс має власну конфігурацію, глобальні слоти не підмішуємо.
                return Finalize(new ResolvedTimeSlots(true, false, new List<TimeSlot>()), courseId, lunches, activeOnly);
            }
        }

        var globalSlots = slotList.Where(s => s.CourseId == null).ToList();
        var globalDay = Pick(globalSlots, day);
        if (globalDay.Count > 0)
        {
            return Finalize(new ResolvedTimeSlots(false, true, globalDay), courseId, lunches, activeOnly);
        }
        var globalAny = Pick(globalSlots, null);
        return Finalize(new ResolvedTimeSlots(false, false, globalAny), courseId, lunches, activeOnly);
    }

    public static Dictionary<DayOfWeek, ResolvedTimeSlots> ResolveForWeek(
        IEnumerable<TimeSlot> slots,
        int? courseId,
        IEnumerable<LunchConfig>? lunches = null,
        bool activeOnly = true)
    {
        var map = new Dictionary<DayOfWeek, ResolvedTimeSlots>();
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            map[day] = ResolveForDay(slots, courseId, day, lunches, activeOnly);
        }
        return map;
    }

    private static ResolvedTimeSlots Finalize(
        ResolvedTimeSlots resolved,
        int? courseId,
        IEnumerable<LunchConfig>? lunches,
        bool activeOnly)
    {
        var filtered = activeOnly
            ? resolved with { Slots = resolved.Slots.Where(slot => slot.IsActive).ToList() }
            : resolved;
        return ExcludeLunch(filtered, courseId, lunches);
    }

    // Виключає всі слоти, що перетинаються з обідньою перервою курсу або глобальною перервою.
    private static ResolvedTimeSlots ExcludeLunch(
        ResolvedTimeSlots resolved,
        int? courseId,
        IEnumerable<LunchConfig>? lunches)
    {
        if (lunches is null) return resolved;
        var lunchRows = lunches.ToList();
        var lunch = courseId is int cid
            ? lunchRows.Where(item => item.CourseId == cid).OrderBy(item => item.Id).FirstOrDefault()
            : null;
        lunch ??= lunchRows.Where(item => item.CourseId == null).OrderBy(item => item.Id).FirstOrDefault();
        if (lunch is null) return resolved;
        var available = resolved.Slots
            .Where(slot => slot.End <= lunch.Start || slot.Start >= lunch.End)
            .ToList();
        return resolved with { Slots = available };
    }
}
