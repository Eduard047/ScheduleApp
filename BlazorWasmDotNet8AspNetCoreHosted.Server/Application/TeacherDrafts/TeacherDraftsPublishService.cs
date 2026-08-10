using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

// Сервіс публікації чернеток у офіційний розклад.
public sealed class TeacherDraftsPublishService
{
    private readonly AppDbContext _db;
    private readonly RulesService _rules;
    private readonly AggregatesService _aggregates;
    public TeacherDraftsPublishService(AppDbContext db, RulesService rules, AggregatesService aggregates)
    {
        _db = db;
        _rules = rules;
        _aggregates = aggregates;
    }
    // Схвалює вибір викладача разом з усіма рядками кожного логічного заняття.
    public async Task<IActionResult> ApproveWeekAsync(ApproveWeekRequest r)
    {
        if (!DateHelpers.IsSupportedScheduleDate(r.WeekStart))
        {
            return new BadRequestObjectResult(new { message = DateHelpers.SupportedScheduleDateMessage });
        }
        var start = r.WeekStart;
        var end = start.AddDays(7);
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var weekDrafts = await _db.TeacherDraftItems
            .Where(x => x.Date >= start && x.Date < end)
            .OrderBy(x => x.Date)
            .ThenBy(x => x.StartTime)
            .ThenBy(x => x.GroupId)
            .ThenBy(x => x.Id)
            .ToListAsync();
        var rows = ExpandTeacherPublishSelection(weekDrafts, r.TeacherId);
        foreach (var x in rows)
        {
            x.Status = DraftStatus.Published;
        }
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return new OkResult();
    }
    // Публікує чернетки тижня в офіційний розклад із валідацією.
    public async Task<ActionResult<PublishWeekResults>> PublishWeekAsync(PublishWeekRequest r)
    {
        if (!DateHelpers.IsSupportedScheduleDate(r.WeekStart))
        {
            return new BadRequestObjectResult(new { message = DateHelpers.SupportedScheduleDateMessage });
        }
        var start = r.WeekStart;
        var end = start.AddDays(7);
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var weekDrafts = await _db.TeacherDraftItems
            .Where(x => x.Date >= start && x.Date < end)
            .Include(x => x.Group)
            .OrderBy(x => x.Date)
            .ThenBy(x => x.StartTime)
            .ThenBy(x => x.GroupId)
            .ThenBy(x => x.Id)
            .ToListAsync();
        if (r.ExpectedScopeRevision is Guid expectedScopeRevision)
        {
            var actualScopeRevision = LogicalRevisionToken.Combine(weekDrafts.Select(item =>
                new KeyValuePair<int, Guid>(item.Id, item.Revision)));
            if (actualScopeRevision != expectedScopeRevision)
            {
                await tx.RollbackAsync();
                return new OkObjectResult(new PublishWeekResults(
                    0,
                    weekDrafts.Count,
                    new List<string>
                    {
                        "Публікацію скасовано: чернетки змінилися після останньої перевірки. Дані оновлено — перегляньте тиждень і повторіть публікацію."
                    }));
            }
        }
        var drafts = r.TeacherId is int teacherId
            ? ExpandTeacherPublishSelection(weekDrafts, teacherId)
            : weekDrafts;
        if (drafts.Count == 0)
        {
            await tx.CommitAsync();
            return new OkObjectResult(new PublishWeekResults(0, 0, new List<string>()));
        }

        var preflightViolations = FindWholeWeekPackageViolations(drafts).ToList();
        if (r.TeacherId is not null)
        {
            preflightViolations.AddRange(FindPartialTeacherSelectionViolations(weekDrafts, drafts));
        }
        if (preflightViolations.Count > 0)
        {
            await tx.RollbackAsync();
            var warnings = new List<string>
            {
                "Публікацію скасовано: вибраний пакет не утворює цілісні логічні події."
            };
            warnings.AddRange(preflightViolations.Distinct(StringComparer.Ordinal));
            return new OkObjectResult(new PublishWeekResults(0, drafts.Count, warnings));
        }

        var candidateValidation = await ValidatePublishCandidatesAsync(
            _db,
            _rules,
            drafts,
            start,
            end);
        var candidates = candidateValidation.Candidates.ToList();
        var violations = candidateValidation.Violations.ToList();

        var pendingDrafts = candidates
            .Select(candidate => new TeacherDraftsAutogenPendingDraft(
                candidate.Draft.Date,
                candidate.Draft.StartTime,
                candidate.Draft.EndTime,
                candidate.Draft.GroupId,
                candidate.Draft.ModuleId,
                candidate.Draft.LessonTypeId,
                candidate.Draft.ModuleTopicId,
                candidate.Draft.TeacherId,
                candidate.RoomId,
                candidate.Draft.IsSelfStudy,
                candidate.BatchKey))
            .ToList();
        var hardRuleValidator = new TeacherDraftsAutogenHardRuleValidator(_db);
        var publishedGroupIds = drafts.Select(draft => draft.GroupId).Distinct().ToList();
        foreach (var courseId in drafts.Select(draft => draft.Group.CourseId).Distinct().OrderBy(id => id))
        {
            var hardRuleResult = await hardRuleValidator.ValidateAsync(
                new TeacherDraftsAutogenHardRuleValidationRequest(
                    CourseId: courseId,
                    GroupIds: publishedGroupIds,
                    From: start,
                    To: end.AddDays(-1),
                    Days: WeekPreset.MonSun,
                    AllowIncompleteDrafts: false,
                    PendingDrafts: pendingDrafts,
                    IncludeStoredDrafts: false,
                    ScopePendingDraftsToCourse: true));
            violations.AddRange(hardRuleResult.Violations);
        }

        if (violations.Count > 0)
        {
            await tx.RollbackAsync();
            var warnings = new List<string>
            {
                "Публікацію скасовано: пакет містить порушення обов'язкових правил."
            };
            warnings.AddRange(violations.Distinct(StringComparer.Ordinal));
            return new OkObjectResult(new PublishWeekResults(0, drafts.Count, warnings));
        }

        var scheduleItems = new List<ScheduleItem>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var d = candidate.Draft;
            var item = new ScheduleItem
            {
                Date = d.Date,
                DayOfWeek = d.Date.DayOfWeek,
                StartTime = d.StartTime,
                EndTime = d.EndTime,
                GroupId = d.GroupId,
                ModuleId = d.ModuleId,
                RoomId = candidate.RoomId,
                TeacherId = d.TeacherId,
                ModuleTopicId = d.ModuleTopicId,
                LessonTypeId = d.LessonTypeId,
                BatchKey = candidate.BatchKey,
                IsLocked = false,
                IsSelfStudy = d.IsSelfStudy
            };
            scheduleItems.Add(item);
        }
        _db.ScheduleItems.AddRange(scheduleItems);
        await _db.SaveChangesAsync();
        var publishedIds = drafts.Select(draft => draft.Id).ToList();
        await _db.TeacherDraftItems
            .Where(x => publishedIds.Contains(x.Id))
            .ExecuteDeleteAsync();
        await TeacherDraftsAutogenPlanService.ExpireAppliedPlansConsumedByPublicationAsync(
            _db,
            drafts);
        var affectedPlans = drafts
            .Select(x => new { x.ModuleId, CourseId = x.Group.CourseId })
            .Distinct()
            .Select(x => (x.CourseId, x.ModuleId));
        var affectedLoads = drafts
            .Where(x => x.TeacherId != null)
            .Select(x => new { TeacherId = x.TeacherId!.Value, CourseId = x.Group.CourseId })
            .Distinct()
            .Select(x => (x.TeacherId, x.CourseId));
        await _aggregates.RecalcAsync(affectedPlans, affectedLoads);
        await tx.CommitAsync();
        return new OkObjectResult(new PublishWeekResults(scheduleItems.Count, 0, new List<string>()));
    }

    // Повертає структурні помилки пакета, які однаково блокують перевірку та публікацію тижня.
    internal static IReadOnlyList<string> FindWholeWeekPackageViolations(
        IReadOnlyList<TeacherDraftItem> drafts)
    {
        var violations = FindLegacyDuplicateViolations(drafts).ToList();
        violations.AddRange(FindMixedStatusLogicalEventViolations(drafts));
        return violations.Distinct(StringComparer.Ordinal).ToList();
    }

    // Перевіряє ресурси, календар і правила кожного рядка без зміни офіційного розкладу.
    internal static async Task<PublishCandidateValidationResult> ValidatePublishCandidatesAsync(
        AppDbContext db,
        RulesService _,
        IReadOnlyList<TeacherDraftItem> drafts,
        DateOnly start,
        DateOnly endExclusive,
        CancellationToken cancellationToken = default)
    {
        if (drafts.Count == 0)
        {
            return new PublishCandidateValidationResult(
                Array.Empty<PublishCandidate>(),
                Array.Empty<string>());
        }

        var lessonTypeIds = drafts.Select(draft => draft.LessonTypeId).Distinct().ToList();
        var lessonTypes = await db.LessonTypes
            .AsNoTracking()
            .Where(lessonType => lessonTypeIds.Contains(lessonType.Id))
            .ToDictionaryAsync(lessonType => lessonType.Id, cancellationToken);
        var resolvedBatchKeys = ResolvePublishBatchKeys(drafts);
        var candidates = drafts
            .Select(draft => new PublishCandidate(
                draft,
                lessonTypes.TryGetValue(draft.LessonTypeId, out var lessonType) && !lessonType.RequiresRoom
                    ? null
                    : draft.RoomId,
                resolvedBatchKeys[draft.Id]))
            .ToList();
        var violations = FindLogicalEventResourceViolations(candidates).ToList();

        var groupIds = drafts.Select(draft => draft.GroupId).Distinct().ToList();
        var groups = await db.Groups
            .AsNoTracking()
            .Where(group => groupIds.Contains(group.Id))
            .ToDictionaryAsync(group => group.Id, cancellationToken);
        var moduleIds = drafts.Select(draft => draft.ModuleId).Distinct().ToList();
        var modules = await db.Modules
            .AsNoTracking()
            .Where(module => moduleIds.Contains(module.Id))
            .Include(module => module.ModuleCourses)
            .Include(module => module.AllowedRooms)
            .Include(module => module.AllowedBuildings)
            .AsSplitQuery()
            .ToDictionaryAsync(module => module.Id, cancellationToken);
        var topicIds = drafts
            .Select(draft => draft.ModuleTopicId)
            .OfType<int>()
            .Distinct()
            .ToList();
        var moduleTopics = await db.ModuleTopics
            .AsNoTracking()
            .Where(topic => moduleIds.Contains(topic.ModuleId) || topicIds.Contains(topic.Id))
            .ToListAsync(cancellationToken);
        var topicsById = moduleTopics.ToDictionary(topic => topic.Id);
        var modulesWithAuditoriumTopics = moduleTopics
            .Where(topic => topic.AuditoriumHours > 0)
            .Select(topic => topic.ModuleId)
            .ToHashSet();
        var teacherIds = drafts
            .Select(draft => draft.TeacherId)
            .OfType<int>()
            .Distinct()
            .ToList();
        var existingTeacherIds = teacherIds.Count == 0
            ? new HashSet<int>()
            : (await db.Teachers
                .AsNoTracking()
                .Where(teacher => teacherIds.Contains(teacher.Id))
                .Select(teacher => teacher.Id)
                .ToListAsync(cancellationToken))
                .ToHashSet();
        var roomIds = candidates
            .Select(candidate => candidate.RoomId)
            .OfType<int>()
            .Distinct()
            .ToList();
        var rooms = roomIds.Count == 0
            ? new Dictionary<int, Room>()
            : await db.Rooms
                .AsNoTracking()
                .Where(room => roomIds.Contains(room.Id))
                .Include(room => room.Building)
                .ToDictionaryAsync(room => room.Id, cancellationToken);
        var courseIds = groups.Values.Select(group => group.CourseId).Distinct().ToList();
        var timeSlots = await db.TimeSlots
            .AsNoTracking()
            .Where(slot => slot.CourseId == null
                           || courseIds.Contains(slot.CourseId.Value))
            .ToListAsync(cancellationToken);
        var lunches = await db.LunchConfigs
            .AsNoTracking()
            .Where(lunch => lunch.CourseId == null
                            || courseIds.Contains(lunch.CourseId.Value))
            .ToListAsync(cancellationToken);
        var rangeStart = drafts.Min(draft => draft.Date) < start
            ? drafts.Min(draft => draft.Date)
            : start;
        var draftEndExclusive = drafts.Max(draft => draft.Date).AddDays(1);
        var rangeEndExclusive = draftEndExclusive > endExclusive
            ? draftEndExclusive
            : endExclusive;
        var calendar = await db.CalendarExceptions
            .AsNoTracking()
            .Where(item => item.Date >= rangeStart && item.Date < rangeEndExclusive)
            .ToListAsync(cancellationToken);
        var officialItems = await db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.Date >= rangeStart && item.Date < rangeEndExclusive)
            .Include(item => item.Group)
            .Include(item => item.LessonType)
            .Include(item => item.Room)
            .ThenInclude(room => room!.Building)
            .ToListAsync(cancellationToken);
        var officialByDate = officialItems
            .GroupBy(item => item.Date)
            .ToDictionary(group => group.Key, group => group.ToList());
        var travelMinutes = roomIds.Count == 0
            ? new Dictionary<(int FromBuildingId, int ToBuildingId), int>()
            : await db.BuildingTravels
                .AsNoTracking()
                .ToDictionaryAsync(
                    item => new ValueTuple<int, int>(item.FromBuildingId, item.ToBuildingId),
                    item => item.Minutes,
                    cancellationToken);
        var teacherWorkingHours = teacherIds.Count == 0
            ? new List<TeacherWorkingHour>()
            : await db.TeacherWorkingHours
                .AsNoTracking()
                .Where(item => teacherIds.Contains(item.TeacherId))
                .ToListAsync(cancellationToken);
        var workingHoursByTeacher = teacherWorkingHours
            .GroupBy(item => item.TeacherId)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var draft = candidate.Draft;
            var candidateErrors = new List<string>();
            void AppendCandidateErrors()
                => violations.AddRange(candidateErrors.Select(error =>
                    $"[{draft.Date:yyyy-MM-dd} {draft.StartTime:HH\\:mm}-{draft.EndTime:HH\\:mm}] {error}"));

            groups.TryGetValue(draft.GroupId, out var group);
            modules.TryGetValue(draft.ModuleId, out var module);
            lessonTypes.TryGetValue(draft.LessonTypeId, out var lessonType);
            var scoped = TeacherDraftsHelpers.ResolveCalendarOverride(
                calendar,
                draft.Date,
                group?.CourseId,
                draft.GroupId);
            if (scoped == false)
            {
                violations.Add(
                    $"[{draft.Date:yyyy-MM-dd} {draft.StartTime:HH\\:mm}-{draft.EndTime:HH\\:mm}] Публікацію заборонено: цей день у календарі позначено неробочим.");
            }

            if (!DateHelpers.IsSupportedScheduleDate(draft.Date))
            {
                candidateErrors.Add(DateHelpers.SupportedScheduleDateMessage);
                AppendCandidateErrors();
                continue;
            }
            if (group is null)
            {
                candidateErrors.Add("Групу не знайдено.");
            }
            if (module is null)
            {
                candidateErrors.Add("Модуль не знайдено.");
            }
            if (lessonType is null)
            {
                candidateErrors.Add("Тип заняття не знайдено.");
            }
            if (group is not null
                && module is not null
                && module.CourseId != group.CourseId
                && !module.ModuleCourses.Any(link => link.CourseId == group.CourseId))
            {
                candidateErrors.Add($"Модуль {module.Title} не належить курсу групи {group.Name}.");
            }
            if (draft.TeacherId is int teacherId && !existingTeacherIds.Contains(teacherId))
            {
                candidateErrors.Add($"Викладача з ідентифікатором {teacherId} не знайдено.");
            }
            if (draft.ModuleTopicId is int topicId)
            {
                if (!topicsById.TryGetValue(topicId, out var topic))
                {
                    candidateErrors.Add($"Тему з ідентифікатором {topicId} не знайдено.");
                }
                else
                {
                    if (topic.ModuleId != draft.ModuleId)
                    {
                        candidateErrors.Add($"Тема #{topicId} не належить модулю #{draft.ModuleId}.");
                    }
                    var preservesOriginalTopic = string.Equals(
                                                     lessonType?.Code,
                                                     "CANCELED",
                                                     StringComparison.OrdinalIgnoreCase)
                                                 || string.Equals(
                                                     lessonType?.Code,
                                                     "RESCHEDULED",
                                                     StringComparison.OrdinalIgnoreCase);
                    if (topic.LessonTypeId != draft.LessonTypeId && !preservesOriginalTopic)
                    {
                        candidateErrors.Add($"Тип заняття #{draft.LessonTypeId} не відповідає темі #{topicId}.");
                    }
                }
            }
            if (module is not null
                && lessonType?.CountInPlan == true
                && draft.ModuleTopicId is null
                && modulesWithAuditoriumTopics.Contains(module.Id))
            {
                candidateErrors.Add("Для модуля налаштовано тематичний план. Створіть заняття через чернетки викладачів, щоб обрати тему та правильно врахувати години.");
            }

            var requiresRoom = lessonType?.RequiresRoom ?? true;
            var requiresTeacher = lessonType?.RequiresTeacher ?? true;
            var blocksRoom = lessonType?.BlocksRoom ?? true;
            var blocksTeacher = lessonType?.BlocksTeacher ?? true;
            var occupiesSlot = lessonType is not null
                               && !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(lessonType.Code);
            Room? room = null;
            if (requiresRoom && candidate.RoomId is null)
            {
                candidateErrors.Add("Для цього заняття потрібно обрати аудиторію.");
                AppendCandidateErrors();
                continue;
            }
            if (requiresRoom && candidate.RoomId is int roomId && !rooms.TryGetValue(roomId, out room))
            {
                candidateErrors.Add("Аудиторію не знайдено.");
            }
            if (candidateErrors.Count > 0)
            {
                AppendCandidateErrors();
                continue;
            }
            if (requiresTeacher && draft.TeacherId is null)
            {
                candidateErrors.Add("Для цього заняття потрібно обрати викладача.");
                AppendCandidateErrors();
                continue;
            }
            if (draft.EndTime <= draft.StartTime)
            {
                candidateErrors.Add("Час завершення має бути більшим за час початку.");
            }
            var dayOfWeek = draft.Date.DayOfWeek;
            var effectiveSlots = TimeSlotsResolver.ResolveForDay(
                    timeSlots,
                    group!.CourseId,
                    dayOfWeek,
                    lunches)
                .Slots
                .Select(slot => (slot.Start, slot.End))
                .ToList();
            if (effectiveSlots.Count == 0)
            {
                candidateErrors.Add("Для курсу немає активних часових слотів у цей день.");
            }
            else if (!IsPublishSlotRangeAllowed(draft.StartTime, draft.EndTime, effectiveSlots))
            {
                candidateErrors.Add("Обраний часовий проміжок не входить до дозволених слотів.");
            }
            if (scoped == false)
            {
                candidateErrors.Add("Заняття потрапляє на неробочий день без явного дозволу.");
            }
            if (requiresRoom && room is not null)
            {
                if (room.Capacity < group.StudentsCount)
                {
                    candidateErrors.Add($"Аудиторія {room.Name} замала для групи {group.Name} ({room.Capacity} < {group.StudentsCount}).");
                }
                var allowedBuildingIds = module!.AllowedBuildings.Select(item => item.BuildingId).ToHashSet();
                if (allowedBuildingIds.Count > 0 && !allowedBuildingIds.Contains(room.BuildingId))
                {
                    candidateErrors.Add($"Корпус {room.Building.Name} не дозволений для цього модуля.");
                }
                var allowedRoomIds = module.AllowedRooms.Select(item => item.RoomId).ToHashSet();
                if (allowedRoomIds.Count > 0 && !allowedRoomIds.Contains(room.Id))
                {
                    candidateErrors.Add($"Аудиторія {room.Name} не входить до дозволених для цього модуля.");
                }
            }

            var officialForDate = officialByDate.TryGetValue(draft.Date, out var resolvedOfficial)
                ? resolvedOfficial
                : new List<ScheduleItem>();
            if (requiresRoom && room is not null)
            {
                var projectedStudentsByGroup = officialForDate
                    .Where(item => item.StartTime == draft.StartTime
                                   && item.EndTime == draft.EndTime
                                   && item.RoomId == room.Id
                                   && item.LessonType.RequiresRoom)
                    .GroupBy(item => item.GroupId)
                    .ToDictionary(items => items.Key, items => items.First().Group.StudentsCount);
                projectedStudentsByGroup[group.Id] = group.StudentsCount;
                var projectedStudents = projectedStudentsByGroup.Values.Sum();
                if (projectedStudents > room.Capacity)
                {
                    candidateErrors.Add(
                        $"Аудиторія {room.Name} має {room.Capacity} місць, але спільне заняття у слоті {draft.StartTime:HH\\:mm}-{draft.EndTime:HH\\:mm} охоплює {projectedStudents} студентів.");
                }
            }
            var hasOfficialConflict = occupiesSlot && officialForDate.Any(item =>
                !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(item.LessonType.Code)
                && item.StartTime < draft.EndTime
                && draft.StartTime < item.EndTime
                && (item.GroupId == draft.GroupId
                    || (blocksRoom && candidate.RoomId is not null && item.RoomId == candidate.RoomId)
                    || (blocksTeacher && draft.TeacherId is not null && item.TeacherId == draft.TeacherId)));
            if (hasOfficialConflict)
            {
                candidateErrors.Add($"Знайдено конфлікт вже опублікованого розкладу на дату {draft.Date:dd.MM.yyyy}.");
            }
            if (requiresRoom && room is not null)
            {
                var adjacentOfficial = officialForDate
                    .Where(item => item.LessonType.RequiresRoom
                                   && !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(item.LessonType.Code)
                                   && (item.GroupId == draft.GroupId
                                       || (draft.TeacherId is not null && item.TeacherId == draft.TeacherId)))
                    .ToList();
                foreach (var adjacent in adjacentOfficial)
                {
                    if (adjacent.Room is null)
                    {
                        continue;
                    }
                    var requiredMinutes = RoomTransitionPolicy.Resolve(
                        travelMinutes,
                        adjacent.Room.Id,
                        adjacent.Room.BuildingId,
                        room.Id,
                        room.BuildingId);
                    var gapBefore = (draft.StartTime.ToTimeSpan() - adjacent.EndTime.ToTimeSpan()).TotalMinutes;
                    var gapAfter = (adjacent.StartTime.ToTimeSpan() - draft.EndTime.ToTimeSpan()).TotalMinutes;
                    if (adjacent.EndTime <= draft.StartTime && gapBefore < requiredMinutes)
                    {
                        candidateErrors.Add("Замало часу на перехід (попереднє заняття).");
                    }
                    if (draft.EndTime <= adjacent.StartTime && gapAfter < requiredMinutes)
                    {
                        candidateErrors.Add("Замало часу на перехід (наступне заняття).");
                    }
                }
            }
            if ((requiresTeacher || blocksTeacher)
                && draft.TeacherId is int workingTeacherId
                && workingHoursByTeacher.TryGetValue(workingTeacherId, out var windows)
                && windows.Count > 0
                && !windows.Any(window =>
                    window.DayOfWeek == dayOfWeek
                    && window.Start <= draft.StartTime
                    && draft.EndTime <= window.End))
            {
                candidateErrors.Add("Заняття виходить за межі робочих годин викладача.");
            }
            AppendCandidateErrors();
        }

        return new PublishCandidateValidationResult(
            candidates,
            violations.Distinct(StringComparer.Ordinal).ToList());
    }

    // Розширює вибір викладача лише до рядків того самого BatchKey і сигнатури в межах запитаного тижня.
    private static List<TeacherDraftItem> ExpandTeacherPublishSelection(
        IReadOnlyList<TeacherDraftItem> weekDrafts,
        int teacherId)
    {
        var selected = weekDrafts
            .Where(draft => draft.TeacherId == teacherId)
            .ToList();
        if (selected.Count == 0)
        {
            return new List<TeacherDraftItem>();
        }

        return weekDrafts
            .Where(candidate => selected.Any(source =>
                HasSameEventSignature(source, candidate)
                && HasSameEventKey(source, candidate)))
            .ToList();
    }

    private static IEnumerable<string> FindPartialTeacherSelectionViolations(
        IReadOnlyList<TeacherDraftItem> weekDrafts,
        IReadOnlyList<TeacherDraftItem> selectedDrafts)
    {
        var selectedIds = selectedDrafts.Select(draft => draft.Id).ToHashSet();
        foreach (var outside in weekDrafts.Where(draft => !selectedIds.Contains(draft.Id)))
        {
            var colliding = selectedDrafts.FirstOrDefault(selected => HasSameEventSignature(selected, outside));
            if (colliding is null)
            {
                continue;
            }

            yield return $"{outside.Date:yyyy-MM-dd} {outside.StartTime:HH\\:mm}-{outside.EndTime:HH\\:mm}: знайдено рядок тієї самої сигнатури з іншим BatchKey; вибір за викладачем не може опублікувати подію частково.";
        }
    }

    private static IEnumerable<string> FindLegacyDuplicateViolations(
        IReadOnlyList<TeacherDraftItem> drafts)
    {
        foreach (var duplicateGroup in drafts
                     .Where(draft => string.IsNullOrWhiteSpace(draft.BatchKey))
                     .GroupBy(draft => new
                     {
                         draft.Date,
                         draft.StartTime,
                         draft.EndTime,
                         draft.GroupId,
                         draft.ModuleId,
                         draft.LessonTypeId,
                         draft.ModuleTopicId,
                         draft.TeacherId,
                         draft.RoomId,
                         draft.IsSelfStudy
                     })
                     .Where(group => group.Count() > 1))
        {
            yield return $"{duplicateGroup.Key.Date:yyyy-MM-dd} {duplicateGroup.Key.StartTime:HH\\:mm}-{duplicateGroup.Key.EndTime:HH\\:mm}: знайдено повністю однакові legacy-рядки без BatchKey; видаліть дублікати перед публікацією.";
        }
    }

    // Вимагає повторного цілісного схвалення для legacy-пакетів зі змішаними статусами рядків.
    private static IEnumerable<string> FindMixedStatusLogicalEventViolations(
        IReadOnlyList<TeacherDraftItem> drafts)
    {
        foreach (var mixedEvent in drafts
                     .Where(draft => !string.IsNullOrWhiteSpace(draft.BatchKey))
                     .GroupBy(draft => new
                     {
                         draft.BatchKey,
                         draft.Date,
                         draft.StartTime,
                         draft.EndTime,
                         draft.GroupId,
                         draft.ModuleId,
                         draft.LessonTypeId
                     })
                     .Where(group => group
                         .Select(draft => draft.Status)
                         .Distinct()
                         .Skip(1)
                         .Any()))
        {
            yield return $"{mixedEvent.Key.Date:yyyy-MM-dd} {mixedEvent.Key.StartTime:HH\\:mm}-{mixedEvent.Key.EndTime:HH\\:mm}: логічне заняття має змішані статуси рядків. Повторно схваліть заняття цілісним пакетом перед публікацією.";
        }
    }

    // Забороняє публікацію події, рядки якої мають різні аудиторії або різний режим самостійної роботи.
    private static IEnumerable<string> FindLogicalEventResourceViolations(
        IReadOnlyList<PublishCandidate> candidates)
    {
        foreach (var eventGroup in candidates
                     .Where(candidate => !string.IsNullOrWhiteSpace(candidate.BatchKey))
                     .GroupBy(candidate => new
                     {
                         candidate.BatchKey,
                         candidate.Draft.Date,
                         candidate.Draft.StartTime,
                         candidate.Draft.EndTime,
                         candidate.Draft.GroupId,
                         candidate.Draft.ModuleId,
                         candidate.Draft.LessonTypeId
                     })
                     .Where(group => group
                         .Select(candidate => new
                         {
                             candidate.RoomId,
                             candidate.Draft.IsSelfStudy
                         })
                         .Distinct()
                         .Skip(1)
                         .Any()))
        {
            yield return $"{eventGroup.Key.Date:yyyy-MM-dd} {eventGroup.Key.StartTime:HH\\:mm}-{eventGroup.Key.EndTime:HH\\:mm}: рядки одного логічного заняття мають різні аудиторії або режим самостійної роботи.";
        }
    }

    // Зберігає наявні ключі, а legacy-рядкам однієї багаторядкової події призначає спільний безпечний ключ.
    private static IReadOnlyDictionary<int, string?> ResolvePublishBatchKeys(
        IReadOnlyList<TeacherDraftItem> drafts)
    {
        var result = drafts.ToDictionary(
            draft => draft.Id,
            draft => string.IsNullOrWhiteSpace(draft.BatchKey) ? null : draft.BatchKey);
        foreach (var signatureGroup in drafts.GroupBy(draft => new
        {
            draft.Date,
            draft.StartTime,
            draft.EndTime,
            draft.GroupId,
            draft.ModuleId,
            draft.LessonTypeId
        }))
        {
            var legacyRows = signatureGroup
                .Where(draft => string.IsNullOrWhiteSpace(draft.BatchKey))
                .ToList();
            if (legacyRows.Count <= 1)
            {
                continue;
            }

            var sharedKey = CreateSafeBatchKey("publish");
            foreach (var legacyRow in legacyRows)
            {
                result[legacyRow.Id] = sharedKey;
            }
        }
        return result;
    }

    private static bool HasSameEventSignature(TeacherDraftItem left, TeacherDraftItem right)
        => left.Date == right.Date
           && left.StartTime == right.StartTime
           && left.EndTime == right.EndTime
           && left.GroupId == right.GroupId
           && left.ModuleId == right.ModuleId
           && left.LessonTypeId == right.LessonTypeId;

    private static bool HasSameEventKey(TeacherDraftItem left, TeacherDraftItem right)
        => !string.IsNullOrWhiteSpace(left.BatchKey)
            ? string.Equals(left.BatchKey, right.BatchKey, StringComparison.Ordinal)
            : string.IsNullOrWhiteSpace(right.BatchKey);

    // Дозволяє проміжок, складений із суміжних активних слотів у визначеному порядку.
    private static bool IsPublishSlotRangeAllowed(
        TimeOnly start,
        TimeOnly end,
        IReadOnlyList<(TimeOnly Start, TimeOnly End)> slots)
    {
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].Start != start)
            {
                continue;
            }
            for (var j = i; j < slots.Count; j++)
            {
                if (j > i && slots[j - 1].End != slots[j].Start)
                {
                    break;
                }
                if (slots[j].End == end)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string CreateSafeBatchKey(string prefix)
        => $"{prefix}:{Guid.NewGuid():N}";

    internal sealed record PublishCandidate(TeacherDraftItem Draft, int? RoomId, string? BatchKey);

    internal sealed record PublishCandidateValidationResult(
        IReadOnlyList<PublishCandidate> Candidates,
        IReadOnlyList<string> Violations);
}
