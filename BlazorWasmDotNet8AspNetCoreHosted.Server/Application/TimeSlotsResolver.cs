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
        var query = db.TimeSlots.AsNoTracking();
        if (activeOnly)
        {
            query = query.Where(s => s.IsActive);
        }
        var slots = await query
            .Where(s => s.CourseId == null || s.CourseId == courseId)
            .ToListAsync();
        return ResolveForDay(slots, courseId, day);
    }

    public static ResolvedTimeSlots ResolveForDay(
        IEnumerable<TimeSlot> slots,
        int? courseId,
        DayOfWeek day)
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
                    return new ResolvedTimeSlots(true, true, courseDay);
                }
                var courseAny = Pick(courseSlots, null);
                if (courseAny.Count > 0)
                {
                    return new ResolvedTimeSlots(true, false, courseAny);
                }
            }
        }

        var globalSlots = slotList.Where(s => s.CourseId == null).ToList();
        var globalDay = Pick(globalSlots, day);
        if (globalDay.Count > 0)
        {
            return new ResolvedTimeSlots(false, true, globalDay);
        }
        var globalAny = Pick(globalSlots, null);
        return new ResolvedTimeSlots(false, false, globalAny);
    }

    public static Dictionary<DayOfWeek, ResolvedTimeSlots> ResolveForWeek(
        IEnumerable<TimeSlot> slots,
        int? courseId)
    {
        var map = new Dictionary<DayOfWeek, ResolvedTimeSlots>();
        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            map[day] = ResolveForDay(slots, courseId, day);
        }
        return map;
    }
}
