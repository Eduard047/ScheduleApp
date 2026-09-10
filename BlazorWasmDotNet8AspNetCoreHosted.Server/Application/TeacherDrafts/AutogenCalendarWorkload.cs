using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public static class AutogenCalendarWorkload
{
    // Перевіряє фактичну сітку до завантаження історії та запуску пошуку.
    public static async Task<AutoGenCapacityDto> MeasureAsync(AppDbContext db, AutoGenJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var course = await db.Courses.AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.CourseId, cancellationToken)
            ?? throw new AutoGenJobValidationException("Вибраний курс не знайдено.");
        if (course.AcademicPeriodStartDate is not DateOnly start || course.DurationWeeks is < 1 or > 520
            || start.DayNumber > DateOnly.MaxValue.DayNumber - course.DurationWeeks * 7)
            throw new AutoGenJobValidationException("Налаштуйте початок і тривалість навчального періоду курсу.");
        var end = start.AddDays(course.DurationWeeks * 7 - 1);
        if (request.FromDate < start || request.ToDate > end)
            throw new AutoGenJobValidationException($"Діапазон має бути в межах навчального періоду {start:dd.MM.yyyy} – {end:dd.MM.yyyy}.");
        var groups = await db.Groups.AsNoTracking().Where(g => request.GroupIds.Contains(g.Id) && g.CourseId == request.CourseId)
            .OrderBy(g => g.Id).ToListAsync(cancellationToken);
        if (groups.Count != request.GroupIds.Count)
            throw new AutoGenJobValidationException("Усі вибрані групи мають належати вибраному курсу.");
        var slots = await db.TimeSlots.AsNoTracking().Where(s => s.CourseId == null || s.CourseId == request.CourseId)
            .ToListAsync(cancellationToken);
        var lunches = await db.LunchConfigs.AsNoTracking().Where(l => l.CourseId == null || l.CourseId == request.CourseId)
            .ToListAsync(cancellationToken);
        var calendar = await db.CalendarExceptions.AsNoTracking()
            .Where(c => c.Date >= request.FromDate && c.Date <= request.ToDate
                && (c.CourseId == null || c.CourseId == request.CourseId)
                && (c.GroupId == null || request.GroupIds.Contains(c.GroupId.Value)))
            .OrderBy(c => c.Id).ToListAsync(cancellationToken);
        var byDay = TimeSlotsResolver.ResolveForWeek(slots, request.CourseId, lunches);
        var groupResults = new List<AutoGenCapacityGroup>();
        var total = 0;
        foreach (var group in groups)
        {
            var count = 0;
            for (var date = request.FromDate; date <= request.ToDate; date = date.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.Days != WeekPreset.MonSun && date.DayOfWeek == DayOfWeek.Sunday
                    || request.Days == WeekPreset.MonFri && date.DayOfWeek == DayOfWeek.Saturday
                    || TeacherDraftsHelpers.ResolveCalendarOverride(calendar, date, request.CourseId, group.Id) == false)
                    continue;
                count += byDay[date.DayOfWeek].Slots.DistinctBy(s => (s.Start, s.End)).Count();
                if (total + count > AutoGenWorkloadLimits.MaxCalendarSlots)
                    throw new AutoGenJobValidationException($"Сітка містить понад {AutoGenWorkloadLimits.MaxCalendarSlots:N0} навчальних слотів. Зменште кількість груп або діапазон.");
            }
            total += count;
            groupResults.Add(new AutoGenCapacityGroup(group.Id, group.Name, count, request.ModuleHours.Values.Sum()));
        }
        return new AutoGenCapacityDto(total, request.GroupIds.Count * request.ModuleHours.Values.Sum(), groupResults);
    }
}
