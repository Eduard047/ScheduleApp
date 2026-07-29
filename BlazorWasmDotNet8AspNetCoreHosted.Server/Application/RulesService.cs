using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

// Сервіс валідації правил розкладу
public sealed class RulesService(AppDbContext db)
{
    private static bool TryParseClock(string value, out TimeOnly time)
        => TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    // Перевіряє правила для створення/оновлення опублікованої пари.
    public async Task<(List<string> errors, List<string> warnings)> ValidateUpsertAsync(
        UpsertScheduleItemRequest r,
        IReadOnlyCollection<int>? excludedScheduleItemIds = null,
        int? projectedModuleTopicId = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (!DateHelpers.IsSupportedScheduleDate(r.Date))
        {
            errors.Add(DateHelpers.SupportedScheduleDateMessage);
            return (errors, warnings);
        }
        var group = await db.Groups.AsNoTracking().Include(g => g.Course).FirstOrDefaultAsync(x => x.Id == r.GroupId);
        if (group is null) errors.Add("Групу не знайдено.");
        var module = await db.Modules
            .AsNoTracking()
            .Include(m => m.AllowedRooms)
            .Include(m => m.AllowedBuildings)
            .Include(m => m.ModuleCourses)
            .FirstOrDefaultAsync(x => x.Id == r.ModuleId);
        if (module is null) errors.Add("Модуль не знайдено.");
        var ltype = await db.LessonTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.LessonTypeId);
        if (ltype is null) errors.Add("Тип заняття не знайдено.");
        if (group is not null
            && module is not null
            && module.CourseId != group.CourseId
            && !module.ModuleCourses.Any(link => link.CourseId == group.CourseId))
        {
            errors.Add($"Модуль {module.Title} не належить курсу групи {group.Name}.");
        }
        if (r.TeacherId is int providedTeacherId
            && !await db.Teachers.AsNoTracking().AnyAsync(teacher => teacher.Id == providedTeacherId))
        {
            errors.Add($"Викладача з ідентифікатором {providedTeacherId} не знайдено.");
        }
        var effectiveTopicId = projectedModuleTopicId;
        if (effectiveTopicId is null && r.Id is int persistedItemId && persistedItemId > 0)
        {
            effectiveTopicId = await db.ScheduleItems
                .AsNoTracking()
                .Where(item => item.Id == persistedItemId)
                .Select(item => item.ModuleTopicId)
                .FirstOrDefaultAsync();
        }
        if (effectiveTopicId is int topicId)
        {
            var topic = await db.ModuleTopics
                .AsNoTracking()
                .Where(item => item.Id == topicId)
                .Select(item => new { item.ModuleId, item.LessonTypeId })
                .FirstOrDefaultAsync();
            if (topic is null)
            {
                errors.Add($"Тему з ідентифікатором {topicId} не знайдено.");
            }
            else
            {
                if (topic.ModuleId != r.ModuleId)
                {
                    errors.Add($"Тема #{topicId} не належить модулю #{r.ModuleId}.");
                }
                var preservesOriginalTopic = string.Equals(
                                                 ltype?.Code,
                                                 "CANCELED",
                                                 StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(
                                                 ltype?.Code,
                                                 "RESCHEDULED",
                                                 StringComparison.OrdinalIgnoreCase);
                if (topic.LessonTypeId != r.LessonTypeId && !preservesOriginalTopic)
                {
                    errors.Add($"Тип заняття #{r.LessonTypeId} не відповідає темі #{topicId}.");
                }
            }
        }
        if (module is not null
            && ltype?.CountInPlan == true
            && effectiveTopicId is null
            && await db.ModuleTopics.AsNoTracking().AnyAsync(topic =>
                topic.ModuleId == module.Id && topic.AuditoriumHours > 0))
        {
            errors.Add("Для модуля налаштовано тематичний план. Створіть заняття через чернетки викладачів, щоб обрати тему та правильно врахувати години.");
        }
        var requiresRoom = ltype?.RequiresRoom ?? true;
        var requiresTeacher = ltype?.RequiresTeacher ?? true;
        var blocksRoom = ltype?.BlocksRoom ?? true;
        var blocksTeacher = ltype?.BlocksTeacher ?? true;
        var occupiesSlot = ltype is not null
                           && !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(ltype.Code);
        Room? room = null;
        if (requiresRoom && r.RoomId is null)
        {
            errors.Add("Для цього заняття потрібно обрати аудиторію.");
            return (errors, warnings);
        }
        if (requiresRoom && r.RoomId is int rid)
        {
            room = await db.Rooms.AsNoTracking().Include(x => x.Building)
                .FirstOrDefaultAsync(x => x.Id == rid);
            if (room is null) errors.Add("Аудиторію не знайдено.");
        }
        if (errors.Count > 0) return (errors, warnings);
        if (requiresTeacher && r.TeacherId is null)
        {
            errors.Add("Для цього заняття потрібно обрати викладача.");
            return (errors, warnings);
        }
        if (!TryParseClock(r.TimeStart, out var start) || !TryParseClock(r.TimeEnd, out var end))
        {
            errors.Add("Некоректний формат часу. Використовуйте формат HH:mm.");
            return (errors, warnings);
        }
        if (end <= start) errors.Add("Час завершення має бути більшим за час початку.");
        var dayOfWeek = r.Date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
        if (group is not null)
        {
            var resolved = await TimeSlotsResolver.ResolveForDayAsync(db, group.CourseId, dayOfWeek);
            var effectiveSlots = resolved.Slots
                .Select(s => (s.Start, s.End))
                .ToList();
            if (effectiveSlots.Count == 0)
                errors.Add("Для курсу немає активних часових слотів у цей день.");
            else if (!IsSlotRangeAllowed(start, end, effectiveSlots))
                errors.Add("Обраний часовий проміжок не входить до дозволених слотів.");
        }
        var courseId = group?.CourseId;
        var cal = await FindCalendarExceptionAsync(r.Date, courseId, r.GroupId);
        bool isWorking = cal?.IsWorkingDay ?? true;
        if (!isWorking && !r.OverrideNonWorkingDay)
            errors.Add("Заняття потрапляє на неробочий день без явного дозволу.");
        if (requiresRoom && room is not null)
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
        var canCheckTravel = requiresRoom && room is not null;
        var currentOfficialItem = currentId > 0
            ? await db.ScheduleItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == currentId)
            : null;
        var excludedIds = excludedScheduleItemIds?
            .Where(id => id > 0)
            .Distinct()
            .ToList() ?? new List<int>();
        if (requiresRoom && room is not null && group is not null)
        {
            var capacityQuery = db.ScheduleItems
                .AsNoTracking()
                .Where(item => item.Id != currentId
                               && item.Date == r.Date
                               && item.StartTime == start
                               && item.EndTime == end
                               && item.RoomId == room.Id
                               && item.LessonType.RequiresRoom);
            if (excludedIds.Count > 0)
            {
                capacityQuery = capacityQuery.Where(item => !excludedIds.Contains(item.Id));
            }

            var occupiedGroups = await capacityQuery
                .Select(item => new { item.GroupId, item.Group.StudentsCount })
                .ToListAsync();
            var projectedStudentsByGroup = occupiedGroups
                .GroupBy(item => item.GroupId)
                .ToDictionary(items => items.Key, items => items.First().StudentsCount);
            projectedStudentsByGroup[group.Id] = group.StudentsCount;
            var projectedStudents = projectedStudentsByGroup.Values.Sum();
            if (projectedStudents > room.Capacity)
            {
                errors.Add(
                    $"Аудиторія {room.Name} має {room.Capacity} місць, але спільне заняття у слоті {start:HH\\:mm}-{end:HH\\:mm} охоплює {projectedStudents} студентів.");
            }
        }
        var dayScheduleQuery = db.ScheduleItems
            .AsNoTracking()
            .Include(x => x.LessonType)
            .Include(x => x.Room).ThenInclude(rm => rm!.Building)
            .Where(x => x.Id != currentId
                        && x.Date == r.Date
                        && (
                            x.GroupId == r.GroupId
                            || (r.TeacherId != null && x.TeacherId == r.TeacherId)
                            || (r.RoomId != null && x.RoomId == r.RoomId)
                        ));
        if (excludedIds.Count > 0)
        {
            dayScheduleQuery = dayScheduleQuery.Where(x => !excludedIds.Contains(x.Id));
        }
        var dayScheduleCandidates = await dayScheduleQuery.ToListAsync();
        var conflicts = occupiesSlot && dayScheduleCandidates.Any(x =>
            !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(x.LessonType.Code)
            &&
            !IsSamePublishedLogicalEvent(r, currentOfficialItem, x, start, end)
            && x.StartTime < end && start < x.EndTime
            && (
                x.GroupId == r.GroupId
                || (blocksRoom && r.RoomId != null && x.RoomId == r.RoomId)
                || (blocksTeacher && r.TeacherId != null && x.TeacherId == r.TeacherId)
            ));
        if (conflicts)
            errors.Add($"Знайдено конфлікт вже опублікованого розкладу на дату {r.Date:dd.MM.yyyy}.");
        if (canCheckTravel)
        {
            var travel = await db.BuildingTravels.AsNoTracking()
                .ToDictionaryAsync(k => (k.FromBuildingId, k.ToBuildingId), v => v.Minutes);
            int TravelMinutes(int fromId, int toId)
                => TravelTimePolicy.Resolve(travel, fromId, toId);
            var adj = dayScheduleCandidates!
                .Where(x => x.LessonType.RequiresRoom
                            && !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(x.LessonType.Code)
                            && !IsSamePublishedLogicalEvent(r, currentOfficialItem, x, start, end))
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
                if (!fits) errors.Add("Заняття виходить за межі робочих годин викладача.");
            }
        }
        return (errors, warnings);
    }
    public sealed record DraftValidationResult(
        List<string> Errors,
        List<string> Warnings,
        DraftValidationReportDto Report
    );
    // Дозволяє режиму без обмежень обходити лише конфлікти розміщення, але не структурні помилки даних.
    public static bool IsBypassableDraftValidationIssue(DraftValidationIssueDto issue)
        => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)
           && (issue.Code.StartsWith("conflict-", StringComparison.Ordinal)
               || issue.Code.StartsWith("travel-", StringComparison.Ordinal));

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
        if (!DateHelpers.IsSupportedScheduleDate(r.Date))
        {
            AddError(
                "date-out-of-range",
                "Дата поза підтримуваним діапазоном",
                DateHelpers.SupportedScheduleDateMessage);
            return new DraftValidationResult(errors, warnings, new DraftValidationReportDto(DateTimeOffset.UtcNow, issues));
        }
        var group = await db.Groups.AsNoTracking().Include(g => g.Course).FirstOrDefaultAsync(x => x.Id == r.GroupId);
        if (group is null)
            AddError("group-not-found", "Групу не знайдено", $"Група з ідентифікатором {r.GroupId} відсутня у базі даних.");
        var module = await db.Modules
            .AsNoTracking()
            .Include(m => m.AllowedRooms)
            .Include(m => m.AllowedBuildings)
            .Include(m => m.ModuleCourses)
            .FirstOrDefaultAsync(x => x.Id == r.ModuleId);
        if (module is null)
            AddError("module-not-found", "Модуль не знайдено", $"Модуль з ідентифікатором {r.ModuleId} відсутній у базі.");
        var ltype = await db.LessonTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.LessonTypeId);
        if (ltype is null)
            AddError("lesson-type-not-found", "Тип заняття не знайдено", $"Тип заняття {r.LessonTypeId} не існує.");
        if (group is not null
            && module is not null
            && module.CourseId != group.CourseId
            && !module.ModuleCourses.Any(link => link.CourseId == group.CourseId))
        {
            AddError(
                "module-course-mismatch",
                "Модуль не належить курсу групи",
                $"Модуль {module.Title} не належить курсу групи {group.Name}.");
        }
        if (r.TeacherId is int providedTeacherId
            && !await db.Teachers.AsNoTracking().AnyAsync(teacher => teacher.Id == providedTeacherId))
        {
            AddError(
                "teacher-not-found",
                "Викладача не знайдено",
                $"Викладач з ідентифікатором {providedTeacherId} відсутній у базі даних.");
        }
        if (r.ModuleTopicId is int providedTopicId)
        {
            var topic = await db.ModuleTopics
                .AsNoTracking()
                .Where(item => item.Id == providedTopicId)
                .Select(item => new { item.ModuleId, item.LessonTypeId })
                .FirstOrDefaultAsync();
            if (topic is null)
            {
                AddError(
                    "module-topic-not-found",
                    "Тему не знайдено",
                    $"Тема з ідентифікатором {providedTopicId} відсутня у базі даних.");
            }
            else
            {
                if (topic.ModuleId != r.ModuleId)
                {
                    AddError(
                        "module-topic-module-mismatch",
                        "Тема не належить модулю",
                        $"Тема #{providedTopicId} не належить модулю #{r.ModuleId}.");
                }
                if (topic.LessonTypeId != r.LessonTypeId)
                {
                    AddError(
                        "module-topic-lesson-type-mismatch",
                        "Тип заняття не відповідає темі",
                        $"Тип заняття #{r.LessonTypeId} не відповідає темі #{providedTopicId}.");
                }
            }
        }
        var requiresRoom = ltype?.RequiresRoom ?? true;
        var requiresTeacher = ltype?.RequiresTeacher ?? true;
        var blocksRoom = ltype?.BlocksRoom ?? true;
        var blocksTeacher = ltype?.BlocksTeacher ?? true;
        var occupiesSlot = ltype is not null
                           && !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(ltype.Code);
        var effectiveRoomId = requiresRoom ? r.RoomId : null;
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
                AddWarning("room-required", "Потрібна аудиторія", "У чернетці пара збережена без аудиторії. Перед публікацією потрібно призначити аудиторію.");
            }
        }
        if (requiresTeacher && r.TeacherId is null)
        {
            AddWarning("teacher-required", "Потрібен викладач", "У чернетці пара збережена без викладача. Перед публікацією потрібно призначити викладача.");
        }
        if (errors.Count > 0)
            return new DraftValidationResult(errors, warnings, new DraftValidationReportDto(DateTimeOffset.UtcNow, issues));
        if (!TryParseClock(r.TimeStart, out var start) || !TryParseClock(r.TimeEnd, out var end))
        {
            AddError(
                "time-format-invalid",
                "Некоректний формат часу",
                "Час початку та завершення потрібно передавати у форматі HH:mm.");
            return new DraftValidationResult(errors, warnings, new DraftValidationReportDto(DateTimeOffset.UtcNow, issues));
        }
        if (end <= start)
            AddError("time-window-invalid", "Некоректний час", $"Час завершення {r.TimeEnd} не може бути меншим або рівним часу початку {r.TimeStart}.");
        var dayOfWeek = r.Date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
        if (group is not null)
        {
            var resolved = await TimeSlotsResolver.ResolveForDayAsync(db, group.CourseId, dayOfWeek);
            var effectiveSlots = resolved.Slots
                .Select(s => (s.Start, s.End))
                .ToList();
            if (effectiveSlots.Count == 0)
                AddError("slot-config-missing", "Немає часових слотів", $"Для курсу {group.Course.Name} немає активних часових слотів у цей день.");
            else if (!IsSlotRangeAllowed(start, end, effectiveSlots))
                AddError("slot-not-allowed", "Недозволений слот", $"Проміжок {r.TimeStart}-{r.TimeEnd} відсутній серед дозволених для курсу {group.Course.Name}.");
        }
        var courseId = group?.CourseId;
        var cal = await FindCalendarExceptionAsync(r.Date, courseId, r.GroupId);
        bool isWorking = cal?.IsWorkingDay ?? true;
        if (!isWorking && !r.OverrideNonWorkingDay)
        {
            var reason = cal?.Name ?? "неробочий день";
            AddWarning("non-working-day", "Заняття у вихідний", $"Дата {r.Date:yyyy-MM-dd} позначена як {reason}. Для публікації потрібно примусове збереження.");
        }
        if (requiresRoom && room is not null)
        {
            if (room.Capacity < group!.StudentsCount)
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
            .Include(x => x.LessonType)
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
                     occupiesSlot
                     && !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(x.LessonType.Code)
                     && x.Id != currentId
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
            .Include(x => x.LessonType)
            .Include(x => x.Teacher)
            .Include(x => x.Room).ThenInclude(rm => rm!.Building)
            .Where(x => x.Date == r.Date
                        && (
                            x.GroupId == r.GroupId
                            || (r.TeacherId != null && x.TeacherId == r.TeacherId)
                            || (r.RoomId != null && x.RoomId == r.RoomId)
                        ))
            .ToListAsync();
        if (!string.IsNullOrWhiteSpace(r.BatchKey)
            && draftCandidates.Any(candidate =>
                candidate.Id != currentId
                && HasSameDraftEventKeyAndSignature(r, candidate, start, end)
                && (candidate.RoomId != effectiveRoomId
                    || candidate.IsSelfStudy != r.IsSelfStudy)))
        {
            AddError(
                "logical-event-resource-mismatch",
                "Неузгоджені ресурси логічного заняття",
                "Усі рядки одного логічного заняття мають використовувати однакову аудиторію та однаковий режим самостійної роботи.");
        }
        foreach (var c in draftCandidates.Where(x =>
                     occupiesSlot
                     && !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(x.LessonType.Code)
                     && x.Id != currentId
                     && !IsSameLogicalDraftEvent(r, x, start, end, effectiveRoomId)
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
        if (requiresRoom && room is not null)
        {
            var travelMap = await db.BuildingTravels.AsNoTracking()
                .ToDictionaryAsync(k => (k.FromBuildingId, k.ToBuildingId), v => v.Minutes);
            int TravelMinutes(int fromId, int toId)
                => TravelTimePolicy.Resolve(travelMap, fromId, toId);
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
                         x.LessonType.RequiresRoom
                         && !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(x.LessonType.Code)
                         && (x.GroupId == r.GroupId
                             || (r.TeacherId != null && x.TeacherId == r.TeacherId))))
                CheckTravel(a.StartTime, a.EndTime, a.Room, "official", "Опубліковане заняття");
            foreach (var a in draftCandidates.Where(x =>
                         x.Id != currentId
                         && x.LessonType.RequiresRoom
                         && !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(x.LessonType.Code)
                         && !IsSameLogicalDraftEvent(r, x, start, end, effectiveRoomId)
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
    // Для нових офіційних рядків звіряє BatchKey, а для legacy-даних залишає консервативну евристику теми/викладача.
    private static bool IsSamePublishedLogicalEvent(
        UpsertScheduleItemRequest request,
        ScheduleItem? current,
        ScheduleItem candidate,
        TimeOnly start,
        TimeOnly end)
        => current is not null
           && request.Id == current.Id
           && current.Date == candidate.Date
           && current.StartTime == candidate.StartTime
           && current.EndTime == candidate.EndTime
           && current.GroupId == candidate.GroupId
           && current.ModuleId == candidate.ModuleId
           && current.LessonTypeId == candidate.LessonTypeId
           && request.Date == candidate.Date
           && start == candidate.StartTime
           && end == candidate.EndTime
           && request.GroupId == candidate.GroupId
           && request.ModuleId == candidate.ModuleId
           && request.LessonTypeId == candidate.LessonTypeId
           && request.RoomId == candidate.RoomId
           && (!string.IsNullOrWhiteSpace(current.BatchKey)
               ? string.Equals(current.BatchKey, candidate.BatchKey, StringComparison.Ordinal)
               : string.IsNullOrWhiteSpace(candidate.BatchKey)
                 && (current.ModuleTopicId != candidate.ModuleTopicId
                     || current.TeacherId != candidate.TeacherId));

    // Визначає рядки одного логічного заняття, які розділено за темами або співвикладачами.
    private static bool IsSameLogicalDraftEvent(
        DraftUpsertRequest request,
        TeacherDraftItem candidate,
        TimeOnly start,
        TimeOnly end,
        int? effectiveRoomId)
        => HasSameDraftEventKeyAndSignature(request, candidate, start, end)
           && effectiveRoomId == candidate.RoomId
           && request.IsSelfStudy == candidate.IsSelfStudy;

    private static bool HasSameDraftEventKeyAndSignature(
        DraftUpsertRequest request,
        TeacherDraftItem candidate,
        TimeOnly start,
        TimeOnly end)
        => !string.IsNullOrWhiteSpace(request.BatchKey)
           && string.Equals(request.BatchKey, candidate.BatchKey, StringComparison.Ordinal)
           && request.Date == candidate.Date
           && start == candidate.StartTime
           && end == candidate.EndTime
           && request.GroupId == candidate.GroupId
           && request.ModuleId == candidate.ModuleId
           && request.LessonTypeId == candidate.LessonTypeId;
    // Дозволяє проміжок, що складається з послідовних слотів за порядком.
    private static bool IsSlotRangeAllowed(TimeOnly start, TimeOnly end, List<(TimeOnly Start, TimeOnly End)> slots)
    {
        if (slots.Count == 0) return true;
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].Start != start) continue;
            for (var j = i; j < slots.Count; j++)
            {
                if (j > i && slots[j - 1].End != slots[j].Start) break;
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
