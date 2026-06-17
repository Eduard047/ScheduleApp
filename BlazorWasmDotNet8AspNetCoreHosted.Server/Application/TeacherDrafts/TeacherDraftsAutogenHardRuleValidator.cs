using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed class TeacherDraftsAutogenHardRuleValidator
{
    private readonly AppDbContext _db;

    public TeacherDraftsAutogenHardRuleValidator(AppDbContext db)
        => _db = db;

    public async Task<TeacherDraftsAutogenHardRuleValidationResult> ValidateAsync(
        TeacherDraftsAutogenHardRuleValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.To < request.From)
        {
            return new TeacherDraftsAutogenHardRuleValidationResult(
                Array.Empty<string>(),
                0,
                "Спільних потоків не знайдено");
        }

        var groupIds = request.GroupIds.Distinct().ToList();
        var calendar = await _db.CalendarExceptions
            .AsNoTracking()
            .Where(item => item.Date >= request.From && item.Date <= request.To)
            .ToListAsync(cancellationToken);
        var draftRows = await LoadDraftRowsAsync(request, groupIds, cancellationToken);
        var scheduleRows = await LoadScheduleRowsAsync(request, groupIds, draftRows, cancellationToken);
        var modulesWithAuditoriumTopics = await LoadModulesWithAuditoriumTopicsAsync(draftRows, cancellationToken);
        var placements = scheduleRows.Concat(draftRows).ToList();
        var violations = new List<string>();

        foreach (var row in draftRows)
        {
            if (!IsDayAllowed(row.Date.DayOfWeek, request.Days))
            {
                violations.Add($"{row.Date:yyyy-MM-dd} {row.GroupName}: заняття створено у день, який не входить у {request.Days}.");
            }

            var calendarOverride = ResolveCalendarOverride(calendar, row.Date, row.CourseId, row.GroupId);
            if (calendarOverride is false)
            {
                violations.Add($"{row.Date:yyyy-MM-dd} {row.GroupName}: заняття створено у неробочий день календаря.");
            }

            if (!request.AllowIncompleteDrafts && !row.IsSelfStudy && row.RequiresTeacher && row.TeacherId is null)
            {
                violations.Add($"{row.Date:yyyy-MM-dd} {row.GroupName} {row.Start:HH\\:mm}: бракує викладача.");
            }

            if (!request.AllowIncompleteDrafts && !row.IsSelfStudy && row.RequiresRoom && row.RoomId is null)
            {
                violations.Add($"{row.Date:yyyy-MM-dd} {row.GroupName} {row.Start:HH\\:mm}: бракує аудиторії.");
            }
        }

        violations.AddRange(await FindTimeSlotViolationsAsync(draftRows, cancellationToken));
        violations.AddRange(FindOverlapViolations(
            "групи",
            placements.GroupBy(row => (Id: row.GroupId, Name: row.GroupName))));
        violations.AddRange(FindOverlapViolations(
            "викладача",
            CollapseSharedFlowPlacements(placements.Where(row => row.TeacherId is not null && row.BlocksTeacher))
                .GroupBy(row => (Id: row.TeacherId!.Value, Name: row.TeacherName ?? $"#{row.TeacherId.Value}"))));
        violations.AddRange(FindOverlapViolations(
            "аудиторії",
            CollapseSharedFlowPlacements(placements.Where(row => row.RoomId is not null && row.BlocksRoom))
                .GroupBy(row => (Id: row.RoomId!.Value, Name: row.RoomName ?? $"#{row.RoomId.Value}"))));
        violations.AddRange(FindTopicOrderViolations(draftRows));
        violations.AddRange(FindModuleTopicPlanViolations(draftRows, modulesWithAuditoriumTopics));
        var roomCapacityStats = AnalyzeRoomCapacity(placements);
        violations.AddRange(roomCapacityStats.Violations);

        return new TeacherDraftsAutogenHardRuleValidationResult(
            violations,
            roomCapacityStats.MaxSharedGroupCount,
            roomCapacityStats.MaxSharedGroupLabel);
    }

    private async Task<IReadOnlyList<PlacementRow>> LoadDraftRowsAsync(
        TeacherDraftsAutogenHardRuleValidationRequest request,
        IReadOnlyCollection<int> groupIds,
        CancellationToken cancellationToken)
    {
        var query = _db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.Group.CourseId == request.CourseId
                           && item.Date >= request.From
                           && item.Date <= request.To
                           && item.Status == request.DraftStatus);

        if (groupIds.Count > 0)
        {
            query = query.Where(item => groupIds.Contains(item.GroupId));
        }

        var rows = await query
            .Select(item => new PlacementRow(
                true,
                item.Date,
                item.StartTime,
                item.EndTime,
                item.GroupId,
                item.Group.Name,
                item.Group.CourseId,
                item.Group.StudentsCount,
                item.ModuleId,
                item.LessonTypeId,
                item.LessonType.Code,
                item.LessonType.Name,
                item.LessonType.PreferredFirstInWeek,
                item.ModuleTopicId,
                item.ModuleTopic != null ? item.ModuleTopic.Order : null,
                item.ModuleTopic != null ? item.ModuleTopic.LessonTypeId : null,
                item.ModuleTopic != null ? item.ModuleTopic.ModuleId : null,
                item.TeacherId,
                item.Teacher != null ? item.Teacher.FullName : null,
                item.RoomId,
                item.Room != null ? item.Room.Name : null,
                item.Room != null ? item.Room.Capacity : null,
                item.LessonType.RequiresTeacher,
                item.LessonType.RequiresRoom,
                item.LessonType.BlocksTeacher,
                item.LessonType.BlocksRoom,
                item.IsSelfStudy))
            .ToListAsync(cancellationToken);

        if (request.PendingDrafts is { Count: > 0 })
        {
            rows.AddRange(await LoadPendingDraftRowsAsync(request.PendingDrafts, cancellationToken));
        }

        return rows;
    }

    private async Task<IReadOnlyList<PlacementRow>> LoadPendingDraftRowsAsync(
        IReadOnlyCollection<TeacherDraftsAutogenPendingDraft> pendingDrafts,
        CancellationToken cancellationToken)
    {
        var groupIds = pendingDrafts.Select(item => item.GroupId).Distinct().ToList();
        var lessonTypeIds = pendingDrafts.Select(item => item.LessonTypeId).Distinct().ToList();
        var teacherIds = pendingDrafts
            .Where(item => item.TeacherId is not null)
            .Select(item => item.TeacherId!.Value)
            .Distinct()
            .ToList();
        var roomIds = pendingDrafts
            .Where(item => item.RoomId is not null)
            .Select(item => item.RoomId!.Value)
            .Distinct()
            .ToList();
        var topicIds = pendingDrafts
            .Where(item => item.ModuleTopicId is not null)
            .Select(item => item.ModuleTopicId!.Value)
            .Distinct()
            .ToList();

        var groups = await _db.Groups
            .AsNoTracking()
            .Where(item => groupIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var lessonTypes = await _db.LessonTypes
            .AsNoTracking()
            .Where(item => lessonTypeIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var teachers = await _db.Teachers
            .AsNoTracking()
            .Where(item => teacherIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, item => item.FullName, cancellationToken);
        var rooms = await _db.Rooms
            .AsNoTracking()
            .Where(item => roomIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var topicPlans = await _db.ModuleTopics
            .AsNoTracking()
            .Where(item => topicIds.Contains(item.Id))
            .ToDictionaryAsync(
                item => item.Id,
                item => new TopicPlanRow(item.ModuleId, item.LessonTypeId, item.Order),
                cancellationToken);

        var rows = new List<PlacementRow>(pendingDrafts.Count);
        foreach (var item in pendingDrafts)
        {
            if (!groups.TryGetValue(item.GroupId, out var group)
                || !lessonTypes.TryGetValue(item.LessonTypeId, out var lessonType))
            {
                continue;
            }

            Room? room = null;
            if (item.RoomId is int roomId)
            {
                rooms.TryGetValue(roomId, out room);
            }
            TopicPlanRow? topicPlan = null;
            if (item.ModuleTopicId is int topicId)
            {
                topicPlans.TryGetValue(topicId, out topicPlan);
            }

            rows.Add(new PlacementRow(
                true,
                item.Date,
                item.Start,
                item.End,
                item.GroupId,
                group.Name,
                group.CourseId,
                group.StudentsCount,
                item.ModuleId,
                item.LessonTypeId,
                lessonType.Code,
                lessonType.Name,
                lessonType.PreferredFirstInWeek,
                item.ModuleTopicId,
                topicPlan?.Order,
                topicPlan?.LessonTypeId,
                topicPlan?.ModuleId,
                item.TeacherId,
                item.TeacherId is int teacherId && teachers.TryGetValue(teacherId, out var teacherName) ? teacherName : null,
                item.RoomId,
                room?.Name,
                room?.Capacity,
                lessonType.RequiresTeacher,
                lessonType.RequiresRoom,
                lessonType.BlocksTeacher,
                lessonType.BlocksRoom,
                item.IsSelfStudy));
        }

        return rows;
    }

    private async Task<IReadOnlyList<PlacementRow>> LoadScheduleRowsAsync(
        TeacherDraftsAutogenHardRuleValidationRequest request,
        IReadOnlyCollection<int> groupIds,
        IReadOnlyList<PlacementRow> draftRows,
        CancellationToken cancellationToken)
    {
        var teacherIds = draftRows
            .Where(row => row.TeacherId is not null)
            .Select(row => row.TeacherId!.Value)
            .Distinct()
            .ToList();
        var roomIds = draftRows
            .Where(row => row.RoomId is not null)
            .Select(row => row.RoomId!.Value)
            .Distinct()
            .ToList();
        var query = _db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.Date >= request.From && item.Date <= request.To)
            .Where(item => item.Group.CourseId == request.CourseId
                           || groupIds.Contains(item.GroupId)
                           || (item.TeacherId != null && teacherIds.Contains(item.TeacherId.Value))
                           || (item.RoomId != null && roomIds.Contains(item.RoomId.Value)));

        return await query
            .Select(item => new PlacementRow(
                false,
                item.Date,
                item.StartTime,
                item.EndTime,
                item.GroupId,
                item.Group.Name,
                item.Group.CourseId,
                item.Group.StudentsCount,
                item.ModuleId,
                item.LessonTypeId,
                item.LessonType.Code,
                item.LessonType.Name,
                item.LessonType.PreferredFirstInWeek,
                item.ModuleTopicId,
                item.ModuleTopic != null ? item.ModuleTopic.Order : null,
                item.ModuleTopic != null ? item.ModuleTopic.LessonTypeId : null,
                item.ModuleTopic != null ? item.ModuleTopic.ModuleId : null,
                item.TeacherId,
                item.Teacher != null ? item.Teacher.FullName : null,
                item.RoomId,
                item.Room != null ? item.Room.Name : null,
                item.Room != null ? item.Room.Capacity : null,
                item.LessonType.RequiresTeacher,
                item.LessonType.RequiresRoom,
                item.LessonType.BlocksTeacher,
                item.LessonType.BlocksRoom,
                item.IsSelfStudy))
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlySet<int>> LoadModulesWithAuditoriumTopicsAsync(
        IReadOnlyList<PlacementRow> draftRows,
        CancellationToken cancellationToken)
    {
        var moduleIds = draftRows
            .Select(row => row.ModuleId)
            .Distinct()
            .ToList();
        if (moduleIds.Count == 0)
        {
            return new HashSet<int>();
        }

        var modules = await _db.ModuleTopics
            .AsNoTracking()
            .Where(topic => moduleIds.Contains(topic.ModuleId) && topic.AuditoriumHours > 0)
            .Select(topic => topic.ModuleId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return modules.ToHashSet();
    }

    private async Task<IReadOnlyList<string>> FindTimeSlotViolationsAsync(
        IReadOnlyList<PlacementRow> draftRows,
        CancellationToken cancellationToken)
    {
        var courseIds = draftRows
            .Select(row => row.CourseId)
            .Distinct()
            .ToList();
        var timeSlots = await _db.TimeSlots
            .AsNoTracking()
            .Where(slot => slot.IsActive && (slot.CourseId == null || courseIds.Contains(slot.CourseId.Value)))
            .ToListAsync(cancellationToken);
        var cache = new Dictionary<(int CourseId, DayOfWeek Day), IReadOnlyList<TimeSlot>>();
        var violations = new List<string>();

        foreach (var row in draftRows)
        {
            var key = (row.CourseId, row.Date.DayOfWeek);
            if (!cache.TryGetValue(key, out var slots))
            {
                slots = TimeSlotsResolver.ResolveForDay(timeSlots, row.CourseId, row.Date.DayOfWeek).Slots;
                cache[key] = slots;
            }

            if (slots.Count == 0)
            {
                violations.Add($"{row.Date:yyyy-MM-dd} {row.GroupName}: немає активної конфігурації часових слотів для курсу #{row.CourseId} у цей день.");
                continue;
            }

            if (!SlotRangeAllowed(row.Start, row.End, slots))
            {
                violations.Add($"{row.Date:yyyy-MM-dd} {row.GroupName} {row.Start:HH\\:mm}-{row.End:HH\\:mm}: слот не відповідає активній конфігурації часу.");
            }
        }

        return violations;
    }

    private static IEnumerable<PlacementRow> CollapseSharedFlowPlacements(IEnumerable<PlacementRow> rows)
    {
        foreach (var row in rows.Where(row => !CanShareAcrossGroups(row)))
        {
            yield return row;
        }

        foreach (var group in rows
                     .Where(CanShareAcrossGroups)
                     .GroupBy(row => new
                     {
                         row.Date,
                         row.Start,
                         row.End,
                         row.ModuleId,
                         row.LessonTypeId,
                         row.ModuleTopicId,
                         row.TeacherId,
                         row.RoomId,
                         row.IsSelfStudy
                     }))
        {
            yield return group.First() with { IsDraft = group.Any(row => row.IsDraft) };
        }
    }

    private static bool SlotRangeAllowed(TimeOnly start, TimeOnly end, IReadOnlyList<TimeSlot> daySlots)
    {
        if (daySlots.Count == 0)
        {
            return true;
        }

        var orderedSlots = daySlots
            .OrderBy(slot => slot.Start)
            .ThenBy(slot => slot.End)
            .ToList();
        for (var i = 0; i < orderedSlots.Count; i++)
        {
            if (orderedSlots[i].Start != start)
            {
                continue;
            }

            for (var j = i; j < orderedSlots.Count; j++)
            {
                if (j > i && orderedSlots[j - 1].End != orderedSlots[j].Start)
                {
                    break;
                }

                if (orderedSlots[j].End == end)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool CanShareAcrossGroups(PlacementRow row)
        => !row.IsSelfStudy && (IsLectureType(row) || row.PreferredFirstInWeek);

    private static bool IsLectureType(PlacementRow row)
    {
        var code = row.LessonTypeCode.Trim().ToUpperInvariant();
        if (code is "LECTURE" or "LECT" or "LEC")
        {
            return true;
        }

        var name = row.LessonTypeName.Trim().ToUpperInvariant();
        return name.Contains("LECTURE", StringComparison.Ordinal)
            || name.Contains("ЛЕКЦ", StringComparison.Ordinal)
            || name.Contains("ЛЕКЦІ", StringComparison.Ordinal)
            || name.Contains("ЛЕКЦІЇ", StringComparison.Ordinal);
    }

    private static IEnumerable<string> FindOverlapViolations(
        string scopeLabel,
        IEnumerable<IGrouping<(int Id, string Name), PlacementRow>> groups)
    {
        foreach (var group in groups)
        {
            foreach (var dayGroup in group.GroupBy(row => row.Date))
            {
                var ordered = dayGroup
                    .OrderBy(row => row.Start)
                    .ThenBy(row => row.End)
                    .ToList();

                for (var i = 0; i < ordered.Count; i++)
                {
                    var previous = ordered[i];
                    for (var j = i + 1; j < ordered.Count; j++)
                    {
                        var current = ordered[j];
                        if (previous.End <= current.Start)
                        {
                            break;
                        }

                        if (!previous.IsDraft && !current.IsDraft)
                        {
                            continue;
                        }

                        yield return $"{current.Date:yyyy-MM-dd}: перетин {scopeLabel} {group.Key.Name} між {previous.Start:HH\\:mm}-{previous.End:HH\\:mm} та {current.Start:HH\\:mm}-{current.End:HH\\:mm}.";
                    }
                }
            }
        }
    }

    private static IEnumerable<string> FindTopicOrderViolations(IReadOnlyList<PlacementRow> draftRows)
    {
        foreach (var group in draftRows
                     .Where(row => row.ModuleTopicId is not null && row.ModuleTopicOrder is not null)
                     .GroupBy(row => new { row.GroupId, row.ModuleId }))
        {
            PlacementRow? previous = null;
            foreach (var current in group
                         .OrderBy(row => row.Date)
                         .ThenBy(row => row.Start)
                         .ThenBy(row => row.End))
            {
                if (previous?.ModuleTopicOrder is int previousOrder
                    && current.ModuleTopicOrder is int currentOrder
                    && currentOrder < previousOrder)
                {
                    yield return $"{current.Date:yyyy-MM-dd} {current.GroupName} {current.Start:HH\\:mm}: тема #{current.ModuleTopicId} модуля #{current.ModuleId} має порядок {currentOrder} після теми #{previous.ModuleTopicId} з порядком {previousOrder}.";
                }

                previous = current;
            }
        }
    }

    private static IEnumerable<string> FindModuleTopicPlanViolations(
        IReadOnlyList<PlacementRow> draftRows,
        IReadOnlySet<int> modulesWithAuditoriumTopics)
    {
        foreach (var row in draftRows.Where(row => !row.IsSelfStudy))
        {
            if (row.ModuleTopicId is null)
            {
                if (modulesWithAuditoriumTopics.Contains(row.ModuleId))
                {
                    yield return $"{row.Date:yyyy-MM-dd} {row.GroupName} {row.Start:HH\\:mm}: модуль #{row.ModuleId} має планові теми, але заняття створено без теми.";
                }

                continue;
            }

            if (row.ModuleTopicModuleId is int topicModuleId && topicModuleId != row.ModuleId)
            {
                yield return $"{row.Date:yyyy-MM-dd} {row.GroupName} {row.Start:HH\\:mm}: тема #{row.ModuleTopicId} не належить модулю #{row.ModuleId}.";
            }

            if (row.ModuleTopicLessonTypeId is int topicLessonTypeId && topicLessonTypeId != row.LessonTypeId)
            {
                yield return $"{row.Date:yyyy-MM-dd} {row.GroupName} {row.Start:HH\\:mm}: тип заняття #{row.LessonTypeId} не відповідає типу теми #{row.ModuleTopicId}.";
            }
        }
    }

    private static RoomCapacityStats AnalyzeRoomCapacity(IReadOnlyList<PlacementRow> placements)
    {
        var violations = new List<string>();
        var maxSharedGroupCount = 0;
        var maxSharedGroupLabel = "Спільних потоків не знайдено";

        foreach (var roomSlot in placements
                     .Where(row => row.RoomId is not null && row.RoomCapacity is not null && row.BlocksRoom)
                     .GroupBy(row => new { row.Date, row.Start, row.End, row.RoomId, row.RoomName, row.RoomCapacity }))
        {
            if (!roomSlot.Any(row => row.IsDraft))
            {
                continue;
            }

            var groups = roomSlot
                .GroupBy(row => row.GroupId)
                .Select(group => group.First())
                .ToList();
            var totalStudents = groups.Sum(row => row.GroupStudentsCount);
            if (groups.Count > maxSharedGroupCount)
            {
                maxSharedGroupCount = groups.Count;
                maxSharedGroupLabel = $"{roomSlot.Key.Date:yyyy-MM-dd} {roomSlot.Key.Start:HH\\:mm}-{roomSlot.Key.End:HH\\:mm}, ауд. {roomSlot.Key.RoomName}, groups={groups.Count}, students={totalStudents}/{roomSlot.Key.RoomCapacity}";
            }

            if (totalStudents > roomSlot.Key.RoomCapacity)
            {
                violations.Add($"{roomSlot.Key.Date:yyyy-MM-dd} {roomSlot.Key.Start:HH\\:mm}-{roomSlot.Key.End:HH\\:mm}: аудиторія {roomSlot.Key.RoomName} має {roomSlot.Key.RoomCapacity} місць для {totalStudents} студентів.");
            }
        }

        return new RoomCapacityStats(violations, maxSharedGroupCount, maxSharedGroupLabel);
    }

    private static bool? ResolveCalendarOverride(IEnumerable<CalendarException> items, DateOnly date, int? courseId, int? groupId)
    {
        var normCourse = courseId is > 0 ? courseId : null;
        var normGroup = groupId is > 0 ? groupId : null;
        var match = items
            .Where(item => item.Date == date)
            .Where(item => normGroup != null ? item.GroupId == normGroup || item.GroupId == null : item.GroupId == null)
            .Where(item => normCourse != null ? item.CourseId == normCourse || item.CourseId == null : item.CourseId == null)
            .OrderByDescending(item => item.GroupId != null)
            .ThenByDescending(item => item.CourseId != null)
            .FirstOrDefault();

        return match?.IsWorkingDay;
    }

    private static bool IsDayAllowed(DayOfWeek dayOfWeek, WeekPreset preset)
    {
        var day = dayOfWeek == DayOfWeek.Sunday ? 7 : (int)dayOfWeek;
        return preset switch
        {
            WeekPreset.MonSun => day is >= 1 and <= 7,
            WeekPreset.MonSat => day is >= 1 and <= 6,
            _ => day is >= 1 and <= 5
        };
    }

    private sealed record RoomCapacityStats(
        IReadOnlyList<string> Violations,
        int MaxSharedGroupCount,
        string MaxSharedGroupLabel);

    private sealed record TopicPlanRow(
        int ModuleId,
        int LessonTypeId,
        int Order);

    private sealed record PlacementRow(
        bool IsDraft,
        DateOnly Date,
        TimeOnly Start,
        TimeOnly End,
        int GroupId,
        string GroupName,
        int CourseId,
        int GroupStudentsCount,
        int ModuleId,
        int LessonTypeId,
        string LessonTypeCode,
        string LessonTypeName,
        bool PreferredFirstInWeek,
        int? ModuleTopicId,
        int? ModuleTopicOrder,
        int? ModuleTopicLessonTypeId,
        int? ModuleTopicModuleId,
        int? TeacherId,
        string? TeacherName,
        int? RoomId,
        string? RoomName,
        int? RoomCapacity,
        bool RequiresTeacher,
        bool RequiresRoom,
        bool BlocksTeacher,
        bool BlocksRoom,
        bool IsSelfStudy);
}

public sealed record TeacherDraftsAutogenHardRuleValidationRequest(
    int CourseId,
    IReadOnlyCollection<int> GroupIds,
    DateOnly From,
    DateOnly To,
    WeekPreset Days = WeekPreset.MonFri,
    bool AllowIncompleteDrafts = false,
    DraftStatus DraftStatus = DraftStatus.Draft,
    IReadOnlyCollection<TeacherDraftsAutogenPendingDraft>? PendingDrafts = null);

public sealed record TeacherDraftsAutogenPendingDraft(
    DateOnly Date,
    TimeOnly Start,
    TimeOnly End,
    int GroupId,
    int ModuleId,
    int LessonTypeId,
    int? ModuleTopicId,
    int? TeacherId,
    int? RoomId,
    bool IsSelfStudy);

public sealed record TeacherDraftsAutogenHardRuleValidationResult(
    IReadOnlyList<string> Violations,
    int MaxSharedGroupCount,
    string MaxSharedGroupLabel)
{
    public bool HasViolations => Violations.Count > 0;
}
