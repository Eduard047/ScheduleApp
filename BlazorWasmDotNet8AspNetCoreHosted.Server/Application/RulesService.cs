using System;
using System.Collections.Generic;
using System.Linq;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

// Сервіс валідації правил розкладу
public sealed class RulesService(AppDbContext db)
{
    // Перевіряє правила для створення/оновлення опублікованої пари.
    public async Task<(List<string> errors, List<string> warnings)> ValidateUpsertAsync(UpsertScheduleItemRequest r)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var group = await db.Groups.AsNoTracking().Include(g => g.Course).FirstOrDefaultAsync(x => x.Id == r.GroupId);
        if (group is null) errors.Add("Групу не знайдено.");
        var module = await db.Modules
            .AsNoTracking()
            .Include(m => m.AllowedRooms)
            .Include(m => m.AllowedBuildings)
            .FirstOrDefaultAsync(x => x.Id == r.ModuleId);
        if (module is null) errors.Add("Модуль не знайдено.");
        var ltype = await db.LessonTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.LessonTypeId);
        if (ltype is null) errors.Add("Тип заняття не знайдено.");
        var requiresRoom = ltype?.RequiresRoom ?? true;
        var requiresTeacher = ltype?.RequiresTeacher ?? true;
        var blocksRoom = ltype?.BlocksRoom ?? true;
        var blocksTeacher = ltype?.BlocksTeacher ?? true;
        Room? room = null;
        if (requiresRoom)
        {
            if (r.RoomId is int rid)
            {
                room = await db.Rooms.AsNoTracking().Include(x => x.Building)
                    .FirstOrDefaultAsync(x => x.Id == rid);
                if (room is null) errors.Add("Аудиторію не знайдено.");
            }
            else errors.Add("Для цього заняття потрібно обрати аудиторію.");
        }
        if (errors.Count > 0) return (errors, warnings);
        var start = TimeOnly.Parse(r.TimeStart);
        var end = TimeOnly.Parse(r.TimeEnd);
        if (end <= start) errors.Add("Час завершення має бути більшим за час початку.");
        var dayOfWeek = r.Date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
        if (group is not null)
        {
            var resolved = await TimeSlotsResolver.ResolveForDayAsync(db, group.CourseId, dayOfWeek);
            var effectiveSlots = resolved.Slots
                .Select(s => (s.Start, s.End))
                .ToList();
            if (effectiveSlots.Count > 0 && !IsSlotRangeAllowed(start, end, effectiveSlots))
                errors.Add("Обраний часовий проміжок не входить до дозволених слотів.");
        }
        bool isWeekend = dayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var courseId = group?.CourseId;
        var cal = await FindCalendarExceptionAsync(r.Date, courseId, r.GroupId);
        bool isWorking = cal?.IsWorkingDay ?? !isWeekend;
        if (!isWorking && !r.OverrideNonWorkingDay)
            warnings.Add("Увага: заняття потрапляє на вихідний день.");
        if (requiresRoom)
        {
            if (room!.Capacity < group!.StudentsCount)
                errors.Add($"Аудиторія {room.Name} замала для групи {group.Name} ({room.Capacity} < {group.StudentsCount}).");
            var allowedBuildingIds = module!.AllowedBuildings.Select(b => b.BuildingId).ToList();
            if (allowedBuildingIds.Count > 0 && !allowedBuildingIds.Contains(room.BuildingId))
                errors.Add($"Корпус {room.Building.Name} не дозволений для цього модуля.");
            var allowedRoomIds = module.AllowedRooms.Select(ar => ar.RoomId).ToList();
            if (allowedRoomIds.Count > 0 && !allowedRoomIds.Contains(room.Id))
                errors.Add($"Аудиторія {room.Name} не входить до дозволених для цього модуля.");
        }
        var currentId = r.Id ?? 0;
        var canCheckTravel = requiresRoom && blocksRoom && r.RoomId is int;
        List<ScheduleItem>? dayScheduleCandidates = null;
        bool conflicts;
        if (canCheckTravel)
        {
            dayScheduleCandidates = await db.ScheduleItems
                .AsNoTracking()
                .Include(x => x.Room).ThenInclude(rm => rm!.Building)
                .Where(x => x.Id != currentId
                            && x.Date == r.Date
                            && (
                                x.GroupId == r.GroupId
                                || (r.TeacherId != null && x.TeacherId == r.TeacherId)
                                || (r.RoomId != null && x.RoomId == r.RoomId)
                            ))
                .ToListAsync();
            conflicts = dayScheduleCandidates.Any(x =>
                x.StartTime < end && start < x.EndTime
                && (
                    x.GroupId == r.GroupId
                    || (blocksRoom && r.RoomId != null && x.RoomId == r.RoomId)
                    || (blocksTeacher && r.TeacherId != null && x.TeacherId == r.TeacherId)
                ));
        }
        else
        {
            conflicts = await db.ScheduleItems
                .Where(x => x.Id != currentId
                            && x.Date == r.Date
                            && (
                                x.GroupId == r.GroupId
                                || (blocksRoom && r.RoomId != null && x.RoomId == r.RoomId)
                                || (blocksTeacher && r.TeacherId != null && x.TeacherId == r.TeacherId)
                            )
                            && x.StartTime < end && start < x.EndTime)
                .AnyAsync();
        }
        if (conflicts)
            errors.Add($"Знайдено конфлікт вже опублікованого розкладу на дату {r.Date:dd.MM.yyyy}.");
        if (canCheckTravel)
        {
            var travel = await db.BuildingTravels.AsNoTracking()
                .ToDictionaryAsync(k => (k.FromBuildingId, k.ToBuildingId), v => v.Minutes);
            int TravelMinutes(int fromId, int toId)
            {
                if (fromId == toId) return 0;
                if (travel.TryGetValue((fromId, toId), out var m)) return m;
                if (travel.TryGetValue((toId, fromId), out m)) return m;
                return 10;
            }
            var adj = dayScheduleCandidates!
                .Where(x => x.GroupId == r.GroupId || (r.TeacherId != null && x.TeacherId == r.TeacherId))
                .ToList();
            foreach (var a in adj)
            {
                if (a.Room is null) continue;
                var need = TravelMinutes(a.Room.BuildingId, room!.BuildingId);
                var gapBefore = (start.ToTimeSpan() - a.EndTime.ToTimeSpan()).TotalMinutes;
                var gapAfter = (a.StartTime.ToTimeSpan() - end.ToTimeSpan()).TotalMinutes;
                if (a.EndTime <= start && gapBefore < need)
                    errors.Add("Замало часу на перехід (попереднє заняття).");
                if (end <= a.StartTime && gapAfter < need)
                    errors.Add("Замало часу на перехід (наступне заняття).");
            }
        }
        if (requiresTeacher && r.TeacherId is int tWin)
        {
            var dayEnum = r.Date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
            var windows = await db.TeacherWorkingHours
                .Where(w => w.TeacherId == tWin && w.DayOfWeek == dayEnum)
                .Select(w => new { w.Start, w.End })
                .ToListAsync();
            if (windows.Count > 0)
            {
                bool fits = windows.Any(w => w.Start <= start && end <= w.End);
                if (!fits) warnings.Add("Заняття виходить за межі робочих годин викладача.");
            }
        }
        return (errors, warnings);
    }
    public sealed record DraftValidationResult(
        List<string> Errors,
        List<string> Warnings,
        DraftValidationReportDto Report
    );
    // Валідатор чернеток із деталізованим звітом проблем.
    public async Task<DraftValidationResult> ValidateDraftAsync(DraftUpsertRequest r)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var issues = new List<DraftValidationIssueDto>();
        void AddError(string code, string title, string description)
        {
            errors.Add(description);
            issues.Add(new DraftValidationIssueDto("error", code, title, description));
        }
        void AddWarning(string code, string title, string description)
        {
            warnings.Add(description);
            issues.Add(new DraftValidationIssueDto("warning", code, title, description));
        }
        var group = await db.Groups.AsNoTracking().Include(g => g.Course).FirstOrDefaultAsync(x => x.Id == r.GroupId);
        if (group is null)
            AddError("group-not-found", "Групу не знайдено", $"Група з ідентифікатором {r.GroupId} відсутня у базі даних.");
        var module = await db.Modules
            .AsNoTracking()
            .Include(m => m.AllowedRooms)
            .Include(m => m.AllowedBuildings)
            .FirstOrDefaultAsync(x => x.Id == r.ModuleId);
        if (module is null)
            AddError("module-not-found", "Модуль не знайдено", $"Модуль з ідентифікатором {r.ModuleId} відсутній у базі.");
        var ltype = await db.LessonTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.LessonTypeId);
        if (ltype is null)
            AddError("lesson-type-not-found", "Тип заняття не знайдено", $"Тип заняття {r.LessonTypeId} не існує.");
        var requiresRoom = ltype?.RequiresRoom ?? true;
        var requiresTeacher = ltype?.RequiresTeacher ?? true;
        var blocksRoom = ltype?.BlocksRoom ?? true;
        var blocksTeacher = ltype?.BlocksTeacher ?? true;
        Room? room = null;
        if (requiresRoom)
        {
            if (r.RoomId is int rid)
            {
                room = await db.Rooms.AsNoTracking().Include(x => x.Building)
                    .FirstOrDefaultAsync(x => x.Id == rid);
                if (room is null)
                    AddError("room-not-found", "Аудиторію не знайдено", $"Аудиторія з ідентифікатором {rid} відсутня.");
            }
            else
            {
                AddError("room-required", "Потрібна аудиторія", "Цей тип заняття потребує вибраної аудиторії.");
            }
        }
        if (errors.Count > 0)
            return new DraftValidationResult(errors, warnings, new DraftValidationReportDto(DateTimeOffset.UtcNow, issues));
        var start = TimeOnly.Parse(r.TimeStart);
        var end = TimeOnly.Parse(r.TimeEnd);
        if (end <= start)
            AddError("time-window-invalid", "Некоректний час", $"Час завершення {r.TimeEnd} не може бути меншим або рівним часу початку {r.TimeStart}.");
        var dayOfWeek = r.Date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
        if (group is not null)
        {
            var resolved = await TimeSlotsResolver.ResolveForDayAsync(db, group.CourseId, dayOfWeek);
            var effectiveSlots = resolved.Slots
                .Select(s => (s.Start, s.End))
                .ToList();
            if (effectiveSlots.Count > 0 && !IsSlotRangeAllowed(start, end, effectiveSlots))
                AddError("slot-not-allowed", "Недозволений слот", $"Проміжок {r.TimeStart}-{r.TimeEnd} відсутній серед дозволених для курсу {group.Course.Name}.");
        }
        bool isWeekend = dayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var courseId = group?.CourseId;
        var cal = await FindCalendarExceptionAsync(r.Date, courseId, r.GroupId);
        bool isWorking = cal?.IsWorkingDay ?? !isWeekend;
        if (!isWorking && !r.OverrideNonWorkingDay)
        {
            var reason = cal is not null ? cal.Name : (isWeekend ? "вихідний день" : "неробочий день");
            AddWarning("non-working-day", "Заняття у вихідний", $"Дата {r.Date:yyyy-MM-dd} позначена як {reason}. Для публікації потрібно примусове збереження.");
        }
        if (requiresRoom)
        {
            if (room!.Capacity < group!.StudentsCount)
                AddError("room-capacity", "Недостатня місткість", $"Аудиторія {room.Name} вміщує {room.Capacity} осіб, у групі {group.Name} {group.StudentsCount} студентів.");
            var allowedBuildingIds = module!.AllowedBuildings.Select(b => b.BuildingId).ToList();
            if (allowedBuildingIds.Count > 0 && !allowedBuildingIds.Contains(room.BuildingId))
                AddError("building-not-allowed", "Корпус заборонено", $"Модуль {module.Title} заборонено проводити у корпусі {room.Building.Name}.");
            var allowedRoomIds = module.AllowedRooms.Select(ar => ar.RoomId).ToList();
            if (allowedRoomIds.Count > 0 && !allowedRoomIds.Contains(room.Id))
                AddError("room-not-allowed", "Аудиторія заборонена", $"Аудиторія {room.Name} не входить до списку дозволених для модуля {module.Title}.");
        }
        var currentId = r.Id ?? 0;
        var dateLabel = r.Date.ToString("dd.MM.yyyy");
        var officialCandidates = await db.ScheduleItems
            .AsNoTracking()
            .Include(x => x.Group)
            .Include(x => x.Module)
            .Include(x => x.Teacher)
            .Include(x => x.Room).ThenInclude(rm => rm!.Building)
            .Where(x => x.Date == r.Date
                        && (
                            x.GroupId == r.GroupId
                            || (r.TeacherId != null && x.TeacherId == r.TeacherId)
                            || (r.RoomId != null && x.RoomId == r.RoomId)
                        ))
            .ToListAsync();
        foreach (var c in officialCandidates.Where(x =>
                     x.Id != currentId
                     && x.StartTime < end && start < x.EndTime
                     && (
                         x.GroupId == r.GroupId
                         || (blocksTeacher && r.TeacherId != null && x.TeacherId == r.TeacherId)
                         || (blocksRoom && r.RoomId != null && x.RoomId == r.RoomId)
                     )))
        {
            var slot = $"{c.StartTime:HH\\:mm}-{c.EndTime:HH\\:mm}";
            if (c.GroupId == r.GroupId)
                AddError("conflict-official-group", "Група зайнята", $"Група {c.Group.Name} має опубліковане заняття {c.Module.Title} на {dateLabel} у слоті {slot}.");
            if (blocksTeacher && r.TeacherId != null && c.TeacherId == r.TeacherId)
            {
                var teacherName = c.Teacher?.FullName ?? $"ID {r.TeacherId}";
                AddError("conflict-official-teacher", "Викладач зайнятий", $"Викладач {teacherName} проводить заняття {c.Module.Title} для групи {c.Group.Name} на {dateLabel} у слоті {slot}.");
            }
            if (blocksRoom && r.RoomId != null && c.RoomId == r.RoomId && c.Room is not null)
            {
                var buildingName = c.Room.Building?.Name is { Length: > 0 } b ? $" ({b})" : string.Empty;
                AddError("conflict-official-room", "Аудиторія зайнята", $"Аудиторія {c.Room.Name}{buildingName} використовується для заняття {c.Module.Title} на {dateLabel} у слоті {slot}.");
            }
        }
        var draftCandidates = await db.TeacherDraftItems
            .AsNoTracking()
            .Include(x => x.Group)
            .Include(x => x.Module)
            .Include(x => x.Teacher)
            .Include(x => x.Room).ThenInclude(rm => rm!.Building)
            .Where(x => x.Date == r.Date
                        && x.Status == DraftStatus.Draft
                        && (
                            x.GroupId == r.GroupId
                            || (r.TeacherId != null && x.TeacherId == r.TeacherId)
                            || (r.RoomId != null && x.RoomId == r.RoomId)
                        ))
            .ToListAsync();
        foreach (var c in draftCandidates.Where(x =>
                     x.Id != currentId
                     && x.StartTime < end && start < x.EndTime
                     && (
                         x.GroupId == r.GroupId
                         || (blocksTeacher && r.TeacherId != null && x.TeacherId == r.TeacherId)
                         || (blocksRoom && r.RoomId != null && x.RoomId == r.RoomId)
                     )))
        {
            var slot = $"{c.StartTime:HH\\:mm}-{c.EndTime:HH\\:mm}";
            if (c.GroupId == r.GroupId)
                AddError("conflict-draft-group", "Група вже має чернетку", $"Група {c.Group.Name} вже має чернетку {c.Module.Title} на {dateLabel} у слоті {slot}.");
            if (blocksTeacher && r.TeacherId != null && c.TeacherId == r.TeacherId)
            {
                var teacherName = c.Teacher?.FullName ?? $"ID {r.TeacherId}";
                AddError("conflict-draft-teacher", "Викладач зайнятий у чернетці", $"Викладач {teacherName} вже запланований на чернетку {c.Module.Title} на {dateLabel} у слоті {slot}.");
            }
            if (blocksRoom && r.RoomId != null && c.RoomId == r.RoomId && c.Room is not null)
            {
                var buildingName = c.Room.Building?.Name is { Length: > 0 } b ? $" ({b})" : string.Empty;
                AddError("conflict-draft-room", "Аудиторія зайнята у чернетці", $"Аудиторія {c.Room.Name}{buildingName} вже використовується для чернетки {c.Module.Title} на {dateLabel} у слоті {slot}.");
            }
        }
        if (requiresRoom && blocksRoom && room is not null)
        {
            var travelMap = await db.BuildingTravels.AsNoTracking()
                .ToDictionaryAsync(k => (k.FromBuildingId, k.ToBuildingId), v => v.Minutes);
            int TravelMinutes(int fromId, int toId)
            {
                if (fromId == toId) return 0;
                if (travelMap.TryGetValue((fromId, toId), out var m)) return m;
                if (travelMap.TryGetValue((toId, fromId), out m)) return m;
                return 10;
            }
            void CheckTravel(TimeOnly otherStart, TimeOnly otherEnd, Room? otherRoom, string scope, string label)
            {
                if (otherRoom is null) return;
                var need = TravelMinutes(otherRoom.BuildingId, room.BuildingId);
                var gapBefore = (start.ToTimeSpan() - otherEnd.ToTimeSpan()).TotalMinutes;
                var gapAfter = (otherStart.ToTimeSpan() - end.ToTimeSpan()).TotalMinutes;
                if (otherEnd <= start && gapBefore < need)
                    AddError($"travel-{scope}-before", "Недостатньо часу на перехід", $"{label} завершується о {otherEnd:HH\\:mm} в аудиторії {otherRoom.Name}. Для переходу потрібно {need} хвилин, доступно лише {gapBefore:N0} хв.");
                if (end <= otherStart && gapAfter < need)
                    AddError($"travel-{scope}-after", "Недостатньо часу на перехід", $"{label} починається о {otherStart:HH\\:mm} в аудиторії {otherRoom.Name}. Для переходу потрібно {need} хвилин, доступно лише {gapAfter:N0} хв.");
            }
            foreach (var a in officialCandidates.Where(x =>
                         x.GroupId == r.GroupId
                         || (r.TeacherId != null && x.TeacherId == r.TeacherId)))
                CheckTravel(a.StartTime, a.EndTime, a.Room, "official", "Опубліковане заняття");
            foreach (var a in draftCandidates.Where(x =>
                         x.Id != currentId
                         && (x.GroupId == r.GroupId || (r.TeacherId != null && x.TeacherId == r.TeacherId))))
                CheckTravel(a.StartTime, a.EndTime, a.Room, "draft", "Чернетка");
        }
        if (requiresTeacher && r.TeacherId is int tWin)
        {
            var windows = await db.TeacherWorkingHours
                .Where(w => w.TeacherId == tWin && w.DayOfWeek == dayOfWeek)
                .Select(w => new { w.Start, w.End })
                .ToListAsync();
            if (windows.Count > 0)
            {
                bool fits = windows.Any(w => w.Start <= start && end <= w.End);
                if (!fits)
                    AddWarning("teacher-working-hours", "Поза робочими годинами", $"Інтервал {r.TimeStart}-{r.TimeEnd} виходить за межі робочих годин викладача для {dayOfWeek}.");
            }
        }
        var report = new DraftValidationReportDto(DateTimeOffset.UtcNow, issues);
        return new DraftValidationResult(errors, warnings, report);
    }
    // Дозволяє проміжок, що складається з послідовних слотів за порядком.
    private static bool IsSlotRangeAllowed(TimeOnly start, TimeOnly end, List<(TimeOnly Start, TimeOnly End)> slots)
    {
        if (slots.Count == 0) return true;
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].Start != start) continue;
            for (var j = i; j < slots.Count; j++)
            {
                if (slots[j].End == end) return true;
            }
        }
        return false;
    }
    // Повертає найточніший календарний виняток для дати/курсу/групи.
    private async Task<CalendarException?> FindCalendarExceptionAsync(DateOnly date, int? courseId, int? groupId)
    {
        var query = db.CalendarExceptions.AsNoTracking().Where(x => x.Date == date);
        if (groupId is int gid && gid > 0)
            query = query.Where(x => x.GroupId == gid || x.GroupId == null);
        else
            query = query.Where(x => x.GroupId == null);
        if (courseId is int cid && cid > 0)
            query = query.Where(x => x.CourseId == cid || x.CourseId == null);
        else
            query = query.Where(x => x.CourseId == null);
        return await query
            .OrderByDescending(x => x.GroupId != null)
            .ThenByDescending(x => x.CourseId != null)
            .FirstOrDefaultAsync();
    }
}
