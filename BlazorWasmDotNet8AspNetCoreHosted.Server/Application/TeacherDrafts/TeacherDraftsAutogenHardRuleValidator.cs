using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed class TeacherDraftsAutogenHardRuleValidator
{
    private const int MaxOverlapViolationCount = 1_000;
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
                new[] { "Діапазон перевірки некоректний: кінцева дата передує початковій." },
                0,
                "Перевірку не виконано");
        }

        var groupIds = request.GroupIds.Distinct().ToList();
        IQueryable<CalendarException> calendarQuery = _db.CalendarExceptions
            .AsNoTracking()
            .Where(item => item.Date >= request.From
                           && item.Date <= request.To
                           && ((item.CourseId == null && item.GroupId == null)
                               || item.CourseId == request.CourseId
                               || (item.GroupId != null && groupIds.Contains(item.GroupId.Value))))
            .OrderBy(item => item.Id);
        if (request.MaxStoredContextRows is int maxCalendarRows)
        {
            calendarQuery = calendarQuery.Take(maxCalendarRows + 1);
        }
        var calendar = await calendarQuery.ToListAsync(cancellationToken);
        EnsureStoredContextCapacity(request, calendar.Count, "календарних винятків");
        var draftRows = await LoadDraftRowsAsync(request, groupIds, cancellationToken);
        var activeDraftRows = draftRows
            .Where(row => !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(row.LessonTypeCode))
            .ToList();
        var currentDraftRows = activeDraftRows
            .Where(row => row.IsDraft)
            .ToList();
        var coursePeriod = await _db.Courses
            .AsNoTracking()
            .Where(course => course.Id == request.CourseId)
            .Select(course => new
            {
                course.AcademicPeriodStartDate,
                course.DurationWeeks
            })
            .SingleOrDefaultAsync(cancellationToken);
        var academicPeriodStartDate = coursePeriod?.AcademicPeriodStartDate;
        var academicPeriodEndDateExclusive = ResolveAcademicPeriodEndDateExclusive(
            academicPeriodStartDate,
            coursePeriod?.DurationWeeks);
        var scheduleRows = await LoadScheduleRowsAsync(request, groupIds, currentDraftRows, cancellationToken);
        var modulesWithAuditoriumTopics = await LoadModulesWithAuditoriumTopicsAsync(currentDraftRows, cancellationToken);
        var placements = scheduleRows.Concat(draftRows).ToList();
        var violations = new List<string>();

        foreach (var row in currentDraftRows)
        {
            if (!IsDayAllowed(row.Date.DayOfWeek, request.Days))
            {
                violations.Add($"{row.Date:yyyy-MM-dd} {row.GroupName}: заняття створено у день, який не входить у {request.Days}.");
            }

            var calendarOverride = TeacherDraftsHelpers.ResolveCalendarOverride(
                calendar,
                row.Date,
                row.CourseId,
                row.GroupId);
            if (calendarOverride == false)
            {
                violations.Add($"{row.Date:yyyy-MM-dd} {row.GroupName}: заняття створено у день, який у календарі позначено неробочим.");
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

        var resourceEligibilityRows = request.PendingDrafts is not null
            ? (await LoadPendingDraftRowsAsync(
                    request.PendingDrafts,
                    request.ScopePendingDraftsToCourse ? request.CourseId : null,
                    cancellationToken))
                .Where(row => row.IsDraft)
                .ToList()
            : currentDraftRows;
        violations.AddRange(await FindResourceEligibilityViolationsAsync(
            request,
            resourceEligibilityRows,
            cancellationToken));
        violations.AddRange(await FindTimeSlotViolationsAsync(request, currentDraftRows, cancellationToken));
        violations.AddRange(await FindTeacherWorkingHourViolationsAsync(request, currentDraftRows, cancellationToken));
        if (request.MaxParallelGroupsPerModuleInSlot is int maxParallelGroups && maxParallelGroups > 0)
        {
            violations.AddRange(FindParallelModuleSlotViolations(
                placements,
                maxParallelGroups,
                cancellationToken));
        }
        violations.AddRange(FindOverlapViolations(
            "групи",
            placements.GroupBy(row => (Id: row.GroupId, Name: row.GroupName)),
            cancellationToken));
        violations.AddRange(FindOverlapViolations(
            "викладача",
            CollapseSharedFlowPlacements(placements.Where(row => row.TeacherId is not null && row.BlocksTeacher))
                .GroupBy(row => (Id: row.TeacherId!.Value, Name: row.TeacherName ?? $"#{row.TeacherId.Value}")),
            cancellationToken));
        violations.AddRange(FindOverlapViolations(
            "аудиторії",
            CollapseSharedFlowPlacements(placements.Where(row => row.RoomId is not null && row.BlocksRoom))
                .GroupBy(row => (Id: row.RoomId!.Value, Name: row.RoomName ?? $"#{row.RoomId.Value}")),
            cancellationToken));
        violations.AddRange(await FindModuleSequenceViolationsAsync(
            request,
            placements,
            currentDraftRows,
            academicPeriodStartDate,
            academicPeriodEndDateExclusive,
            cancellationToken));
        violations.AddRange(FindModuleTopicPlanViolations(currentDraftRows, modulesWithAuditoriumTopics));
        violations.AddRange(FindLectureBlockOrderViolations(placements, cancellationToken));
        violations.AddRange(await FindEmptyCanonicalLectureSlotViolationsAsync(
            request,
            placements,
            cancellationToken));
        var roomCapacityStats = AnalyzeRoomCapacity(placements, cancellationToken);
        violations.AddRange(roomCapacityStats.Violations);
        violations.AddRange(await FindTravelViolationsAsync(request, placements, cancellationToken));

        return new TeacherDraftsAutogenHardRuleValidationResult(
            violations,
            roomCapacityStats.MaxSharedGroupCount,
            roomCapacityStats.MaxSharedGroupLabel);
    }

    private static DateOnly? ResolveAcademicPeriodEndDateExclusive(
        DateOnly? academicPeriodStartDate,
        int? durationWeeks)
    {
        if (academicPeriodStartDate is not DateOnly periodStart
            || durationWeeks is not > 0)
        {
            return null;
        }

        var durationDays = (long)durationWeeks.Value * 7L;
        var availableDays = (long)DateOnly.MaxValue.DayNumber - periodStart.DayNumber;
        return durationDays <= availableDays
            ? periodStart.AddDays((int)durationDays)
            : null;
    }

    private async Task<IReadOnlyList<string>> FindResourceEligibilityViolationsAsync(
        TeacherDraftsAutogenHardRuleValidationRequest request,
        IReadOnlyList<PlacementRow> currentDraftRows,
        CancellationToken cancellationToken)
    {
        var moduleIds = currentDraftRows
            .Select(row => row.ModuleId)
            .Distinct()
            .ToList();
        if (moduleIds.Count == 0)
        {
            return Array.Empty<string>();
        }

        var contextTake = request.MaxStoredContextRows is int contextLimit
            ? contextLimit + 1
            : int.MaxValue;
        var teacherLinkRows = await _db.TeacherModules
            .AsNoTracking()
            .Where(link => moduleIds.Contains(link.ModuleId))
            .OrderBy(link => link.ModuleId)
            .ThenBy(link => link.TeacherId)
            .Take(contextTake)
            .Select(link => new { link.ModuleId, link.TeacherId })
            .ToListAsync(cancellationToken);
        EnsureStoredContextCapacity(request, teacherLinkRows.Count, "призначень викладачів модулям");
        var teacherLinks = teacherLinkRows
            .Select(link => (link.ModuleId, link.TeacherId))
            .ToHashSet();
        var supervisorLinkRows = await _db.ModuleSupervisors
            .AsNoTracking()
            .Where(link => moduleIds.Contains(link.ModuleId))
            .OrderBy(link => link.ModuleId)
            .ThenBy(link => link.TeacherId)
            .Take(contextTake)
            .Select(link => new { link.ModuleId, link.TeacherId })
            .ToListAsync(cancellationToken);
        EnsureStoredContextCapacity(request, supervisorLinkRows.Count, "керівників модулів");
        var supervisorLinks = supervisorLinkRows
            .Select(link => (link.ModuleId, link.TeacherId))
            .ToHashSet();
        var allowedRoomRows = await _db.ModuleRooms
            .AsNoTracking()
            .Where(link => moduleIds.Contains(link.ModuleId))
            .OrderBy(link => link.ModuleId)
            .ThenBy(link => link.RoomId)
            .Take(contextTake)
            .Select(link => new { link.ModuleId, link.RoomId })
            .ToListAsync(cancellationToken);
        EnsureStoredContextCapacity(request, allowedRoomRows.Count, "дозволених аудиторій модулів");
        var allowedRoomIdsByModule = allowedRoomRows
            .GroupBy(link => link.ModuleId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(link => link.RoomId).ToHashSet());
        var allowedBuildingRows = await _db.ModuleBuildings
            .AsNoTracking()
            .Where(link => moduleIds.Contains(link.ModuleId))
            .OrderBy(link => link.ModuleId)
            .ThenBy(link => link.BuildingId)
            .Take(contextTake)
            .Select(link => new { link.ModuleId, link.BuildingId })
            .ToListAsync(cancellationToken);
        EnsureStoredContextCapacity(request, allowedBuildingRows.Count, "дозволених корпусів модулів");
        var allowedBuildingIdsByModule = allowedBuildingRows
            .GroupBy(link => link.ModuleId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(link => link.BuildingId).ToHashSet());

        var violations = new List<string>();
        foreach (var row in currentDraftRows)
        {
            if (row.TeacherId is int teacherId)
            {
                var teacherAllowed = row.IsSelfStudy
                    ? supervisorLinks.Contains((row.ModuleId, teacherId))
                    : teacherLinks.Contains((row.ModuleId, teacherId));
                if (!teacherAllowed)
                {
                    var role = row.IsSelfStudy ? "керівник самостійної роботи" : "викладач";
                    violations.Add(
                        $"{row.Date:yyyy-MM-dd} {row.GroupName} {row.Start:HH\\:mm}: {role} "
                        + $"{row.TeacherName ?? $"#{teacherId}"} не призначений модулю #{row.ModuleId}.");
                }
            }

            if (row.RoomId is not int roomId)
            {
                continue;
            }

            if (allowedRoomIdsByModule.TryGetValue(row.ModuleId, out var allowedRoomIds)
                && allowedRoomIds.Count > 0
                && !allowedRoomIds.Contains(roomId))
            {
                violations.Add(
                    $"{row.Date:yyyy-MM-dd} {row.GroupName} {row.Start:HH\\:mm}: аудиторія "
                    + $"{row.RoomName ?? $"#{roomId}"} не входить до дозволених аудиторій модуля #{row.ModuleId}.");
            }

            if (allowedBuildingIdsByModule.TryGetValue(row.ModuleId, out var allowedBuildingIds)
                && allowedBuildingIds.Count > 0
                && (row.RoomBuildingId is not int buildingId
                    || !allowedBuildingIds.Contains(buildingId)))
            {
                violations.Add(
                    $"{row.Date:yyyy-MM-dd} {row.GroupName} {row.Start:HH\\:mm}: аудиторія "
                    + $"{row.RoomName ?? $"#{roomId}"} розташована поза дозволеними корпусами модуля #{row.ModuleId}.");
            }
        }

        return violations;
    }

    private async Task<IReadOnlyList<PlacementRow>> LoadDraftRowsAsync(
        TeacherDraftsAutogenHardRuleValidationRequest request,
        IReadOnlyCollection<int> groupIds,
        CancellationToken cancellationToken)
    {
        var rows = new List<PlacementRow>();
        if (!request.IncludeStoredDrafts)
        {
            if (request.PendingDrafts is { Count: > 0 })
            {
                rows.AddRange(await LoadPendingDraftRowsAsync(
                    request.PendingDrafts,
                    request.ScopePendingDraftsToCourse ? request.CourseId : null,
                    cancellationToken));
            }

            return rows;
        }

        var selectedScopeQuery = _db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.Group.CourseId == request.CourseId
                           && item.Date >= request.From
                           && item.Date <= request.To);
        if (request.ExcludedDraftIds is { Count: > 0 })
        {
            selectedScopeQuery = selectedScopeQuery.Where(item => !request.ExcludedDraftIds.Contains(item.Id));
        }

        if (groupIds.Count > 0)
        {
            selectedScopeQuery = selectedScopeQuery.Where(item => groupIds.Contains(item.GroupId));
        }

        var selectedResources = await selectedScopeQuery
            .Select(item => new { item.TeacherId, item.RoomId })
            .ToListAsync(cancellationToken);
        var teacherIds = selectedResources
            .Where(item => item.TeacherId is not null)
            .Select(item => item.TeacherId!.Value)
            .Concat(request.PendingDrafts?
                .Where(item => item.TeacherId is not null)
                .Select(item => item.TeacherId!.Value)
                ?? Array.Empty<int>())
            .Distinct()
            .ToList();
        var roomIds = selectedResources
            .Where(item => item.RoomId is not null)
            .Select(item => item.RoomId!.Value)
            .Concat(request.PendingDrafts?
                .Where(item => item.RoomId is not null)
                .Select(item => item.RoomId!.Value)
                ?? Array.Empty<int>())
            .Distinct()
            .ToList();

        var query = _db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.Date >= request.From && item.Date <= request.To)
            .Where(item => (item.Group.CourseId == request.CourseId
                            && (groupIds.Count == 0 || groupIds.Contains(item.GroupId)))
                           || (item.TeacherId != null && teacherIds.Contains(item.TeacherId.Value))
                           || (item.RoomId != null && roomIds.Contains(item.RoomId.Value)));
        if (request.ExcludedDraftIds is { Count: > 0 })
        {
            query = query.Where(item => !request.ExcludedDraftIds.Contains(item.Id));
        }

        rows.AddRange(await query
            .Select(item => new PlacementRow(
                item.Group.CourseId == request.CourseId
                && (groupIds.Count == 0 || groupIds.Contains(item.GroupId)),
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
                item.Room != null ? (int?)item.Room.BuildingId : null,
                item.LessonType.RequiresTeacher,
                item.LessonType.RequiresRoom,
                item.LessonType.BlocksTeacher,
                item.LessonType.BlocksRoom,
                item.IsSelfStudy,
                item.BatchKey))
            .ToListAsync(cancellationToken));

        if (request.PendingDrafts is { Count: > 0 })
        {
            rows.AddRange(await LoadPendingDraftRowsAsync(
                request.PendingDrafts,
                request.ScopePendingDraftsToCourse ? request.CourseId : null,
                cancellationToken));
        }

        return rows;
    }

    private async Task<IReadOnlyList<PlacementRow>> LoadPendingDraftRowsAsync(
        IReadOnlyCollection<TeacherDraftsAutogenPendingDraft> pendingDrafts,
        int? draftCourseId,
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
                draftCourseId is null || group.CourseId == draftCourseId,
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
                room?.BuildingId,
                lessonType.RequiresTeacher,
                lessonType.RequiresRoom,
                lessonType.BlocksTeacher,
                lessonType.BlocksRoom,
                item.IsSelfStudy,
                item.BatchKey));
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
            .Where(item => (groupIds.Count == 0 && item.Group.CourseId == request.CourseId)
                           || groupIds.Contains(item.GroupId)
                           || (item.TeacherId != null && teacherIds.Contains(item.TeacherId.Value))
                           || (item.RoomId != null && roomIds.Contains(item.RoomId.Value)));

        IQueryable<ScheduleItem> boundedQuery = query.OrderBy(item => item.Id);
        if (request.MaxStoredContextRows is int maxStoredContextRows)
        {
            boundedQuery = boundedQuery.Take(maxStoredContextRows + 1);
        }
        var rows = await boundedQuery
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
                item.Room != null ? (int?)item.Room.BuildingId : null,
                item.LessonType.RequiresTeacher,
                item.LessonType.RequiresRoom,
                item.LessonType.BlocksTeacher,
                item.LessonType.BlocksRoom,
                item.IsSelfStudy,
                item.BatchKey))
            .ToListAsync(cancellationToken);
        EnsureStoredContextCapacity(request, rows.Count, "офіційного розкладу в діапазоні перевірки");
        return rows;
    }

    private async Task<IReadOnlyList<PlacementRow>> LoadTopicOrderContextRowsAsync(
        TeacherDraftsAutogenHardRuleValidationRequest request,
        IReadOnlyList<PlacementRow> currentDraftRows,
        DateOnly? academicPeriodStartDate,
        DateOnly? academicPeriodEndDateExclusive,
        CancellationToken cancellationToken)
    {
        var affectedKeys = currentDraftRows
            .Where(row => row.ModuleTopicId is not null && row.ModuleTopicOrder is not null)
            .Select(row => (row.GroupId, row.ModuleId))
            .ToHashSet();
        if (affectedKeys.Count == 0)
        {
            return Array.Empty<PlacementRow>();
        }

        var affectedGroupIds = affectedKeys.Select(key => key.GroupId).Distinct().ToList();
        var affectedModuleIds = affectedKeys.Select(key => key.ModuleId).Distinct().ToList();
        var draftQuery = _db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => (item.Date < request.From || item.Date > request.To)
                           && item.ModuleTopicId != null
                           && affectedGroupIds.Contains(item.GroupId)
                           && affectedModuleIds.Contains(item.ModuleId));
        if (academicPeriodStartDate is DateOnly draftPeriodStart)
        {
            draftQuery = draftQuery.Where(item => item.Date >= draftPeriodStart);
        }
        if (academicPeriodEndDateExclusive is DateOnly draftPeriodEnd)
        {
            draftQuery = draftQuery.Where(item => item.Date < draftPeriodEnd);
        }
        if (request.ExcludedDraftIds is { Count: > 0 })
        {
            draftQuery = draftQuery.Where(item => !request.ExcludedDraftIds.Contains(item.Id));
        }

        var draftHistoryRows = await draftQuery
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
                item.Room != null ? (int?)item.Room.BuildingId : null,
                item.LessonType.RequiresTeacher,
                item.LessonType.RequiresRoom,
                item.LessonType.BlocksTeacher,
                item.LessonType.BlocksRoom,
                item.IsSelfStudy,
                item.BatchKey))
            .ToListAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var scheduleQuery = _db.ScheduleItems
            .AsNoTracking()
            .Where(item => (item.Date < request.From || item.Date > request.To)
                           && item.ModuleTopicId != null
                           && affectedGroupIds.Contains(item.GroupId)
                           && affectedModuleIds.Contains(item.ModuleId));
        if (academicPeriodStartDate is DateOnly schedulePeriodStart)
        {
            scheduleQuery = scheduleQuery.Where(item => item.Date >= schedulePeriodStart);
        }
        if (academicPeriodEndDateExclusive is DateOnly schedulePeriodEnd)
        {
            scheduleQuery = scheduleQuery.Where(item => item.Date < schedulePeriodEnd);
        }

        var scheduleHistoryRows = await scheduleQuery
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
                item.Room != null ? (int?)item.Room.BuildingId : null,
                item.LessonType.RequiresTeacher,
                item.LessonType.RequiresRoom,
                item.LessonType.BlocksTeacher,
                item.LessonType.BlocksRoom,
                item.IsSelfStudy,
                item.BatchKey))
            .ToListAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return draftHistoryRows
            .Concat(scheduleHistoryRows)
            .Where(row => affectedKeys.Contains((row.GroupId, row.ModuleId)))
            .ToList();
    }

    private static void EnsureStoredContextCapacity(
        TeacherDraftsAutogenHardRuleValidationRequest request,
        int rowCount,
        string scopeLabel)
    {
        if (request.MaxStoredContextRows is int limit && rowCount > limit)
        {
            throw new DraftValidationCapacityException(
                $"Перевірка {scopeLabel} підтримує не більше {limit} збережених записів.");
        }
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
        TeacherDraftsAutogenHardRuleValidationRequest request,
        IReadOnlyList<PlacementRow> draftRows,
        CancellationToken cancellationToken)
    {
        var courseIds = draftRows
            .Select(row => row.CourseId)
            .Distinct()
            .ToList();
        var timeSlots = await _db.TimeSlots
            .AsNoTracking()
            .Where(slot => slot.CourseId == null || courseIds.Contains(slot.CourseId.Value))
            .OrderBy(slot => slot.Id)
            .Take(request.MaxStoredContextRows is int maxTimeSlotRows
                ? maxTimeSlotRows + 1
                : int.MaxValue)
            .ToListAsync(cancellationToken);
        EnsureStoredContextCapacity(request, timeSlots.Count, "часових слотів");
        var lunches = await _db.LunchConfigs
            .AsNoTracking()
            .Where(lunch => lunch.CourseId == null || courseIds.Contains(lunch.CourseId.Value))
            .OrderBy(lunch => lunch.Id)
            .Take(request.MaxStoredContextRows is int maxLunchRows
                ? maxLunchRows + 1
                : int.MaxValue)
            .ToListAsync(cancellationToken);
        EnsureStoredContextCapacity(request, lunches.Count, "обідніх перерв");
        var cache = new Dictionary<(int CourseId, DayOfWeek Day), IReadOnlyList<TimeSlot>>();
        var rangeCache = new Dictionary<(int CourseId, DayOfWeek Day, TimeOnly Start, TimeOnly End), bool>();
        var violations = new List<string>();

        foreach (var row in draftRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (row.CourseId, row.Date.DayOfWeek);
            if (!cache.TryGetValue(key, out var slots))
            {
                slots = TimeSlotsResolver
                    .ResolveForDay(timeSlots, row.CourseId, row.Date.DayOfWeek, lunches)
                    .Slots
                    .OrderBy(slot => slot.Start)
                    .ThenBy(slot => slot.End)
                    .ToList();
                cache[key] = slots;
            }

            if (slots.Count == 0)
            {
                violations.Add($"{row.Date:yyyy-MM-dd} {row.GroupName}: немає активної конфігурації часових слотів для курсу #{row.CourseId} у цей день.");
                continue;
            }

            var rangeKey = (row.CourseId, row.Date.DayOfWeek, row.Start, row.End);
            if (!rangeCache.TryGetValue(rangeKey, out var rangeAllowed))
            {
                rangeAllowed = SlotRangeAllowed(
                    row.Start,
                    row.End,
                    slots,
                    cancellationToken);
                rangeCache[rangeKey] = rangeAllowed;
            }
            if (!rangeAllowed)
            {
                violations.Add($"{row.Date:yyyy-MM-dd} {row.GroupName} {row.Start:HH\\:mm}-{row.End:HH\\:mm}: слот не відповідає активній конфігурації часу.");
            }
        }

        return violations;
    }

    private async Task<IReadOnlyList<string>> FindTeacherWorkingHourViolationsAsync(
        TeacherDraftsAutogenHardRuleValidationRequest request,
        IReadOnlyList<PlacementRow> draftRows,
        CancellationToken cancellationToken)
    {
        var teacherIds = draftRows
            .Where(row => row.TeacherId is not null)
            .Select(row => row.TeacherId!.Value)
            .Distinct()
            .ToList();
        if (teacherIds.Count == 0)
        {
            return Array.Empty<string>();
        }

        IQueryable<TeacherWorkingHour> workingHoursQuery = _db.TeacherWorkingHours
            .AsNoTracking()
            .Where(item => teacherIds.Contains(item.TeacherId))
            .OrderBy(item => item.Id);
        if (request.MaxStoredContextRows is int maxWorkingHourRows)
        {
            workingHoursQuery = workingHoursQuery.Take(maxWorkingHourRows + 1);
        }
        var workingHours = await workingHoursQuery
            .Select(item => new { item.TeacherId, item.DayOfWeek, item.Start, item.End })
            .ToListAsync(cancellationToken);
        EnsureStoredContextCapacity(request, workingHours.Count, "робочих інтервалів викладачів");
        var hoursByTeacher = workingHours
            .GroupBy(item => item.TeacherId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(item => item.DayOfWeek)
                    .ToDictionary(day => day.Key, day => day.Select(item => (item.Start, item.End)).ToList()));
        var coverageCache = new Dictionary<(int TeacherId, DayOfWeek Day, TimeOnly Start, TimeOnly End), bool>();
        var violations = new List<string>();
        foreach (var row in draftRows.Where(row => row.TeacherId is not null
                                                   && (row.RequiresTeacher || row.BlocksTeacher)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var teacherId = row.TeacherId!.Value;
            if (!hoursByTeacher.TryGetValue(teacherId, out var dayHours) || dayHours.Count == 0)
            {
                continue;
            }

            var coverageKey = (teacherId, row.Date.DayOfWeek, row.Start, row.End);
            if (!coverageCache.TryGetValue(coverageKey, out var covered))
            {
                covered = false;
                if (dayHours.TryGetValue(row.Date.DayOfWeek, out var windows))
                {
                    foreach (var window in windows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (window.Start <= row.Start && row.End <= window.End)
                        {
                            covered = true;
                            break;
                        }
                    }
                }
                coverageCache[coverageKey] = covered;
            }
            if (!covered)
            {
                violations.Add($"{row.Date:yyyy-MM-dd} {row.GroupName} {row.Start:HH\\:mm}-{row.End:HH\\:mm}: заняття виходить за робочі години викладача {row.TeacherName ?? $"#{teacherId}"}.");
            }
        }

        return violations;
    }

    private async Task<IReadOnlyList<string>> FindTravelViolationsAsync(
        TeacherDraftsAutogenHardRuleValidationRequest request,
        IReadOnlyList<PlacementRow> placements,
        CancellationToken cancellationToken)
    {
        var buildingIds = placements
            .Select(item => item.RoomBuildingId)
            .OfType<int>()
            .Distinct()
            .ToList();
        IQueryable<BuildingTravel> travelQuery = _db.BuildingTravels
            .AsNoTracking()
            .Where(item => buildingIds.Contains(item.FromBuildingId)
                           && buildingIds.Contains(item.ToBuildingId))
            .OrderBy(item => item.Id);
        if (request.MaxStoredContextRows is int maxTravelRows)
        {
            travelQuery = travelQuery.Take(maxTravelRows + 1);
        }
        var travelRows = await travelQuery
            .Select(item => new { item.FromBuildingId, item.ToBuildingId, item.Minutes })
            .ToListAsync(cancellationToken);
        EnsureStoredContextCapacity(request, travelRows.Count, "переходів між корпусами");
        var travelMinutesByPair = travelRows.ToDictionary(
            item => (item.FromBuildingId, item.ToBuildingId),
            item => item.Minutes);

        int TravelMinutes(int fromBuildingId, int toBuildingId)
            => TravelTimePolicy.Resolve(travelMinutesByPair, fromBuildingId, toBuildingId);
        int TransitionMinutes(PlacementRow from, PlacementRow to)
            => from.RoomId is int fromRoomId
               && from.RoomBuildingId is int fromBuildingId
               && to.RoomId is int toRoomId
               && to.RoomBuildingId is int toBuildingId
                ? RoomTransitionPolicy.Resolve(
                    travelMinutesByPair,
                    fromRoomId,
                    fromBuildingId,
                    toRoomId,
                    toBuildingId)
                : TravelMinutes(from.RoomBuildingId ?? 0, to.RoomBuildingId ?? 0);

        var violations = new List<string>();
        AddScopeViolations(
            "групи",
            placements.GroupBy(row => (Id: row.GroupId, Name: row.GroupName)));
        AddScopeViolations(
            "викладача",
            CollapseSharedFlowPlacements(placements.Where(row => row.TeacherId is not null
                                                                  && (row.RequiresTeacher || row.BlocksTeacher)))
                .GroupBy(row => (Id: row.TeacherId!.Value, Name: row.TeacherName ?? $"#{row.TeacherId.Value}")));
        return violations;

        void AddScopeViolations(
            string scopeLabel,
            IEnumerable<IGrouping<(int Id, string Name), PlacementRow>> groups)
        {
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var day in group.GroupBy(row => row.Date).OrderBy(day => day.Key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Події без фізичної аудиторії не розривають маршрут між двома відомими корпусами.
                    var ordered = CollapseLogicalDraftEventPlacements(day)
                        .Where(row => row.RequiresRoom
                                      && !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(row.LessonTypeCode)
                                      && row.RoomBuildingId is not null)
                        .OrderBy(row => row.Start)
                        .ThenBy(row => row.End)
                        .ToList();
                    for (var index = 1; index < ordered.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var previous = ordered[index - 1];
                        var current = ordered[index];
                        if (previous.RoomBuildingId is not int previousBuildingId
                            || current.RoomBuildingId is not int currentBuildingId)
                        {
                            continue;
                        }
                        if (!previous.IsDraft && !current.IsDraft
                            || previous.End > current.Start)
                        {
                            continue;
                        }

                        var requiredMinutes = TransitionMinutes(previous, current);
                        var availableMinutes = (current.Start.ToTimeSpan() - previous.End.ToTimeSpan()).TotalMinutes;
                        if (availableMinutes < requiredMinutes)
                        {
                            var transitionLabel = previousBuildingId == currentBuildingId
                                ? "зміну аудиторії"
                                : "перехід між корпусами";
                            violations.Add($"{current.Date:yyyy-MM-dd}: для {scopeLabel} {group.Key.Name} між {previous.Start:HH\\:mm}-{previous.End:HH\\:mm} і {current.Start:HH\\:mm}-{current.End:HH\\:mm} потрібно {requiredMinutes} хв на {transitionLabel}, доступно лише {availableMinutes:N0} хв.");
                        }
                    }
                }
            }
        }
    }

    private static IReadOnlyList<string> FindParallelModuleSlotViolations(
        IReadOnlyList<PlacementRow> placements,
        int maxParallelGroups,
        CancellationToken cancellationToken)
    {
        var violations = new List<string>();
        foreach (var moduleDay in placements
                     .Where(row => !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(row.LessonTypeCode)
                                   && !CanShareAcrossGroups(row))
                     .GroupBy(row => new { row.Date, row.ModuleId }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = new PriorityQueue<PlacementRow, long>();
            var activeGroupCounts = new Dictionary<int, (string Name, int Count)>();
            foreach (var startGroup in moduleDay
                         .OrderBy(row => row.Start)
                         .ThenBy(row => row.End)
                         .GroupBy(row => row.Start))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = startGroup.Key;
                while (active.TryPeek(out var expired, out _)
                       && expired.End <= start)
                {
                    active.Dequeue();
                    var state = activeGroupCounts[expired.GroupId];
                    if (state.Count == 1)
                    {
                        activeGroupCounts.Remove(expired.GroupId);
                    }
                    else
                    {
                        activeGroupCounts[expired.GroupId] = (state.Name, state.Count - 1);
                    }
                }
                foreach (var row in startGroup)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    active.Enqueue(row, row.End.Ticks);
                    activeGroupCounts.TryGetValue(row.GroupId, out var state);
                    activeGroupCounts[row.GroupId] = (row.GroupName, state.Count + 1);
                }
                if (activeGroupCounts.Count <= maxParallelGroups)
                {
                    continue;
                }
                if (violations.Count >= MaxOverlapViolationCount)
                {
                    throw new DraftValidationCapacityException(
                        $"Перевірка паралельних модулів зупинена після {MaxOverlapViolationCount} порушень; звузьте обсяг даних тижня.");
                }

                active.TryPeek(out var earliest, out _);
                var end = earliest!.End;
                var orderedNames = activeGroupCounts.Values
                    .Select(state => state.Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .Take(50)
                    .ToList();
                var omittedCount = activeGroupCounts.Count - orderedNames.Count;
                var groupLabel = string.Join(", ", orderedNames)
                                 + (omittedCount > 0 ? $", ще {omittedCount}" : string.Empty);
                violations.Add(
                    $"{moduleDay.Key.Date:yyyy-MM-dd} {start:HH\\:mm}-{end:HH\\:mm}: модуль #{moduleDay.Key.ModuleId} одночасно поставлено для {activeGroupCounts.Count} груп ({groupLabel}), дозволено не більше {maxParallelGroups}.");
            }
        }

        return violations;
    }

    private static IReadOnlyList<string> FindLectureBlockOrderViolations(
        IReadOnlyList<PlacementRow> placements,
        CancellationToken cancellationToken)
    {
        var violations = new List<string>();
        foreach (var group in placements.GroupBy(row => new
        {
            row.GroupId,
            row.GroupName,
            row.Date
        }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = CollapseLogicalDraftEventPlacements(group)
                .Where(row => !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(row.LessonTypeCode))
                .OrderBy(row => row.Start)
                .ThenBy(row => row.End)
                .ToList();
            PlacementRow? lastLecture = null;
            foreach (var row in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (CanShareAcrossGroups(row))
                {
                    lastLecture = row;
                }
            }
            if (lastLecture is null)
            {
                continue;
            }

            PlacementRow? interruption = null;
            foreach (var row in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row.Start < lastLecture.Start
                    && !CanShareAcrossGroups(row)
                    && (row.IsDraft || lastLecture.IsDraft))
                {
                    interruption = row;
                    break;
                }
            }
            if (interruption is null)
            {
                continue;
            }

            violations.Add(
                $"{group.Key.Date:yyyy-MM-dd} {group.Key.GroupName}: лекційний блок розірвано заняттям "
                + $"{interruption.Start:HH\\:mm}-{interruption.End:HH\\:mm}; після початку нелекційних занять "
                + $"повертатися до лекцій цього дня не можна.");
        }

        return violations;
    }

    private async Task<IReadOnlyList<string>> FindEmptyCanonicalLectureSlotViolationsAsync(
        TeacherDraftsAutogenHardRuleValidationRequest request,
        IReadOnlyList<PlacementRow> placements,
        CancellationToken cancellationToken)
    {
        var activePlacements = placements
            .Where(row => !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(row.LessonTypeCode))
            .ToList();
        if (activePlacements.Count == 0)
        {
            return Array.Empty<string>();
        }

        var courseIds = activePlacements
            .Select(row => row.CourseId)
            .Distinct()
            .ToList();
        var timeSlots = await _db.TimeSlots
            .AsNoTracking()
            .Where(slot => slot.CourseId == null || courseIds.Contains(slot.CourseId.Value))
            .OrderBy(slot => slot.Id)
            .Take(request.MaxStoredContextRows is int maxTimeSlotRows
                ? maxTimeSlotRows + 1
                : int.MaxValue)
            .ToListAsync(cancellationToken);
        EnsureStoredContextCapacity(request, timeSlots.Count, "часових слотів");
        var lunches = await _db.LunchConfigs
            .AsNoTracking()
            .Where(lunch => lunch.CourseId == null || courseIds.Contains(lunch.CourseId.Value))
            .OrderBy(lunch => lunch.Id)
            .Take(request.MaxStoredContextRows is int maxLunchRows
                ? maxLunchRows + 1
                : int.MaxValue)
            .ToListAsync(cancellationToken);
        EnsureStoredContextCapacity(request, lunches.Count, "обідніх перерв");
        var slotsByCourseDay = new Dictionary<(int CourseId, DayOfWeek Day), CanonicalSlotIndex>();
        var violations = new List<string>();

        foreach (var group in activePlacements.GroupBy(row => new
        {
            row.GroupId,
            row.GroupName,
            row.CourseId,
            row.Date
        }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = CollapseLogicalDraftEventPlacements(group)
                .OrderBy(row => row.Start)
                .ThenBy(row => row.End)
                .ToList();
            var lectures = ordered
                .Where(CanShareAcrossGroups)
                .OrderBy(row => row.Start)
                .ThenBy(row => row.End)
                .ToList();
            if (lectures.Count < 2 || !lectures.Any(row => row.IsDraft))
            {
                continue;
            }

            var firstLecture = lectures[0];
            var lastLecture = lectures[^1];
            var cacheKey = (group.Key.CourseId, group.Key.Date.DayOfWeek);
            if (!slotsByCourseDay.TryGetValue(cacheKey, out var slotIndex))
            {
                var canonicalSlots = TimeSlotsResolver
                    .ResolveForDay(timeSlots, group.Key.CourseId, group.Key.Date.DayOfWeek, lunches)
                    .Slots
                    .OrderBy(slot => slot.Start)
                    .ThenBy(slot => slot.End)
                    .ToList();
                slotIndex = new CanonicalSlotIndex(
                    canonicalSlots,
                    canonicalSlots
                        .Select((slot, index) => new { slot.Start, Index = index })
                        .GroupBy(item => item.Start)
                        .ToDictionary(grouping => grouping.Key, grouping => grouping.Min(item => item.Index)),
                    canonicalSlots
                        .Select((slot, index) => new { slot.End, Index = index })
                        .GroupBy(item => item.End)
                        .ToDictionary(grouping => grouping.Key, grouping => grouping.Max(item => item.Index)));
                slotsByCourseDay[cacheKey] = slotIndex;
            }

            if (!slotIndex.FirstIndexByStart.TryGetValue(firstLecture.Start, out var firstIndex)
                || !slotIndex.LastIndexByEnd.TryGetValue(lastLecture.End, out var lastIndex)
                || firstIndex >= lastIndex)
            {
                continue;
            }

            var emptySlot = FindFirstUncoveredSlot(
                slotIndex.Slots,
                firstIndex,
                lastIndex,
                ordered,
                cancellationToken);
            if (emptySlot is null)
            {
                continue;
            }

            violations.Add(
                $"{group.Key.Date:yyyy-MM-dd} {group.Key.GroupName}: лекційний блок розірвано порожнім "
                + $"канонічним слотом {emptySlot.Start:HH\\:mm}-{emptySlot.End:HH\\:mm} між першою та останньою лекцією.");
        }

        return violations;
    }

    private static TimeSlot? FindFirstUncoveredSlot(
        IReadOnlyList<TimeSlot> slots,
        int firstIndex,
        int lastIndex,
        IReadOnlyList<PlacementRow> orderedPlacements,
        CancellationToken cancellationToken)
    {
        var placementIndex = 0;
        TimeOnly? furthestEnd = null;
        for (var slotIndex = 0; slotIndex <= lastIndex; slotIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = slots[slotIndex];
            while (placementIndex < orderedPlacements.Count
                   && orderedPlacements[placementIndex].Start <= slot.Start)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var placementEnd = orderedPlacements[placementIndex].End;
                if (furthestEnd is null || placementEnd > furthestEnd.Value)
                {
                    furthestEnd = placementEnd;
                }
                placementIndex++;
            }

            if (slotIndex >= firstIndex
                && (furthestEnd is null || furthestEnd.Value < slot.End))
            {
                return slot;
            }
        }
        return null;
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

    // Схлопує явні пакети та консервативно розпізнані багаторядкові legacy-події.
    private static IEnumerable<PlacementRow> CollapseLogicalDraftEventPlacements(IEnumerable<PlacementRow> rows)
    {
        var buffered = rows.ToList();
        foreach (var legacyGroup in buffered
                     .Where(row => !HasLogicalDraftEventKey(row))
                     .GroupBy(row => new
                     {
                         row.Date,
                         row.Start,
                         row.End,
                         row.GroupId,
                         row.ModuleId,
                         row.LessonTypeId,
                         row.RoomId,
                         row.IsSelfStudy
                     }))
        {
            var legacyRows = legacyGroup.ToList();
            var isLogicalEvent = legacyRows.Count > 1
                                 && legacyRows
                                     .Select(row => (row.ModuleTopicId, row.TeacherId))
                                     .Distinct()
                                     .Skip(1)
                                     .Any();
            if (isLogicalEvent)
            {
                yield return legacyRows[0] with { IsDraft = legacyRows.Any(row => row.IsDraft) };
                continue;
            }

            foreach (var row in legacyRows)
            {
                yield return row;
            }
        }

        foreach (var group in buffered
                     .Where(HasLogicalDraftEventKey)
                     .GroupBy(row => new
                     {
                         row.BatchKey,
                         row.Date,
                         row.Start,
                         row.End,
                         row.GroupId,
                         row.ModuleId,
                         row.LessonTypeId
                     }))
        {
            yield return group.First() with { IsDraft = group.Any(row => row.IsDraft) };
        }
    }

    private static bool HasLogicalDraftEventKey(PlacementRow row)
        => !string.IsNullOrWhiteSpace(row.BatchKey);

    private static bool SlotRangeAllowed(
        TimeOnly start,
        TimeOnly end,
        IReadOnlyList<TimeSlot> orderedSlots,
        CancellationToken cancellationToken)
    {
        if (orderedSlots.Count == 0)
        {
            return true;
        }

        for (var i = 0; i < orderedSlots.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (orderedSlots[i].Start != start)
            {
                continue;
            }

            for (var j = i; j < orderedSlots.Count; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

    private static IReadOnlyList<string> FindOverlapViolations(
        string scopeLabel,
        IEnumerable<IGrouping<(int Id, string Name), PlacementRow>> groups,
        CancellationToken cancellationToken)
    {
        var violations = new List<string>();
        foreach (var group in groups)
        {
            foreach (var dayGroup in group.GroupBy(row => row.Date))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ordered = CollapseLogicalDraftEventPlacements(dayGroup)
                    .Where(row => !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(row.LessonTypeCode))
                    .OrderBy(row => row.Start)
                    .ThenBy(row => row.End)
                    .ToList();
                var activeDrafts = new List<PlacementRow>();
                var activeOfficial = new List<PlacementRow>();

                foreach (var current in ordered)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    activeDrafts.RemoveAll(previous => previous.End <= current.Start);
                    activeOfficial.RemoveAll(previous => previous.End <= current.Start);
                    var candidates = current.IsDraft
                        ? activeDrafts.Concat(activeOfficial)
                        : activeDrafts;
                    foreach (var previous in candidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (violations.Count >= MaxOverlapViolationCount)
                        {
                            throw new DraftValidationCapacityException(
                                $"Перевірка перетинів зупинена після {MaxOverlapViolationCount} порушень; звузьте обсяг даних тижня.");
                        }
                        violations.Add(
                            $"{current.Date:yyyy-MM-dd}: перетин {scopeLabel} {group.Key.Name} між {previous.Start:HH\\:mm}-{previous.End:HH\\:mm} та {current.Start:HH\\:mm}-{current.End:HH\\:mm}.");
                    }

                    (current.IsDraft ? activeDrafts : activeOfficial).Add(current);
                }
            }
        }
        return violations;
    }

    private async Task<IReadOnlyList<string>> FindModuleSequenceViolationsAsync(
        TeacherDraftsAutogenHardRuleValidationRequest request,
        IReadOnlyList<PlacementRow> currentPlacements,
        IReadOnlyList<PlacementRow> currentDraftRows,
        DateOnly? academicPeriodStartDate,
        DateOnly? academicPeriodEndDateExclusive,
        CancellationToken cancellationToken)
    {
        var sequenceRows = await _db.ModuleSequenceItems
            .AsNoTracking()
            .Where(item => item.CourseId == request.CourseId)
            .OrderBy(item => item.Id)
            .Take(request.MaxStoredContextRows is int maxSequenceRows
                ? maxSequenceRows + 1
                : int.MaxValue)
            .Select(item => new { item.ModuleId, item.GroupOrder })
            .ToListAsync(cancellationToken);
        EnsureStoredContextCapacity(request, sequenceRows.Count, "налаштувань послідовності модулів");
        if (sequenceRows.Count == 0)
        {
            return Array.Empty<string>();
        }

        var groupOrderByModule = sequenceRows
            .GroupBy(item => item.ModuleId)
            .ToDictionary(group => group.Key, group => group.Min(item => item.GroupOrder));
        var configuredModuleIds = groupOrderByModule.Keys.ToList();
        var fillerRows = await _db.ModuleFillers
            .AsNoTracking()
            .Where(item => item.CourseId == request.CourseId)
            .OrderBy(item => item.ModuleId)
            .Take(request.MaxStoredContextRows is int maxFillerRows
                ? maxFillerRows + 1
                : int.MaxValue)
            .Select(item => item.ModuleId)
            .ToListAsync(cancellationToken);
        EnsureStoredContextCapacity(request, fillerRows.Count, "заповнювальних модулів");
        var fillerModuleIds = fillerRows
            .ToHashSet();
        var affectedGroupIds = currentDraftRows
            .Where(row => row.CourseId == request.CourseId
                          && groupOrderByModule.ContainsKey(row.ModuleId)
                          && !fillerModuleIds.Contains(row.ModuleId))
            .Select(row => row.GroupId)
            .Distinct()
            .ToList();
        if (affectedGroupIds.Count == 0)
        {
            return Array.Empty<string>();
        }

        var currentRows = CollapseLogicalDraftEventPlacements(currentPlacements)
            .Where(row => row.CourseId == request.CourseId
                          && affectedGroupIds.Contains(row.GroupId)
                          && groupOrderByModule.ContainsKey(row.ModuleId)
                          && !fillerModuleIds.Contains(row.ModuleId)
                          && !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(row.LessonTypeCode))
            .Select(row => new ModuleSequencePlacement(
                row.Date,
                row.Start,
                row.End,
                row.GroupId,
                row.GroupName,
                row.ModuleId,
                row.IsDraft))
            .ToList();

        if (academicPeriodStartDate is DateOnly currentPeriodStart)
        {
            currentRows = currentRows
                .Where(row => row.Date >= currentPeriodStart)
                .ToList();
        }
        if (academicPeriodEndDateExclusive is DateOnly currentPeriodEnd)
        {
            currentRows = currentRows
                .Where(row => row.Date < currentPeriodEnd)
                .ToList();
        }
        var draftContextQuery = _db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => (item.Date < request.From || item.Date > request.To)
                           && affectedGroupIds.Contains(item.GroupId)
                           && configuredModuleIds.Contains(item.ModuleId));
        var scheduleContextQuery = _db.ScheduleItems
            .AsNoTracking()
            .Where(item => (item.Date < request.From || item.Date > request.To)
                           && affectedGroupIds.Contains(item.GroupId)
                           && configuredModuleIds.Contains(item.ModuleId));
        if (academicPeriodStartDate is DateOnly periodStart)
        {
            draftContextQuery = draftContextQuery.Where(item => item.Date >= periodStart);
            scheduleContextQuery = scheduleContextQuery.Where(item => item.Date >= periodStart);
        }
        if (academicPeriodEndDateExclusive is DateOnly periodEnd)
        {
            draftContextQuery = draftContextQuery.Where(item => item.Date < periodEnd);
            scheduleContextQuery = scheduleContextQuery.Where(item => item.Date < periodEnd);
        }
        if (request.ExcludedDraftIds is { Count: > 0 })
        {
            draftContextQuery = draftContextQuery.Where(item => !request.ExcludedDraftIds.Contains(item.Id));
        }

        IQueryable<TeacherDraftItem> boundedDraftContextQuery = draftContextQuery.OrderBy(item => item.Id);
        IQueryable<ScheduleItem> boundedScheduleContextQuery = scheduleContextQuery.OrderBy(item => item.Id);
        if (request.MaxStoredContextRows is int maxStoredContextRows)
        {
            boundedDraftContextQuery = boundedDraftContextQuery.Take(maxStoredContextRows + 1);
            boundedScheduleContextQuery = boundedScheduleContextQuery.Take(maxStoredContextRows + 1);
        }

        var draftContextRows = await boundedDraftContextQuery
            .Select(item => new ModuleSequenceHistoryRow(
                item.Date,
                item.StartTime,
                item.EndTime,
                item.GroupId,
                item.Group.Name,
                item.ModuleId,
                item.LessonType.Code))
            .ToListAsync(cancellationToken);
        EnsureStoredContextCapacity(
            request,
            draftContextRows.Count,
            "контексту послідовності модулів у чернетках");
        var remainingContextCapacity = request.MaxStoredContextRows is int contextLimit
            ? contextLimit - draftContextRows.Count
            : (int?)null;
        if (remainingContextCapacity is int remaining)
        {
            boundedScheduleContextQuery = scheduleContextQuery
                .OrderBy(item => item.Id)
                .Take(remaining + 1);
        }
        var scheduleContextRows = await boundedScheduleContextQuery
            .Select(item => new ModuleSequenceHistoryRow(
                item.Date,
                item.StartTime,
                item.EndTime,
                item.GroupId,
                item.Group.Name,
                item.ModuleId,
                item.LessonType.Code))
            .ToListAsync(cancellationToken);
        EnsureStoredContextCapacity(
            request,
            draftContextRows.Count + scheduleContextRows.Count,
            "контексту послідовності модулів");
        var contextRows = draftContextRows
            .Concat(scheduleContextRows)
            .Where(row => !fillerModuleIds.Contains(row.ModuleId)
                          && !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(row.LessonTypeCode))
            .Select(row => new ModuleSequencePlacement(
                row.Date,
                row.Start,
                row.End,
                row.GroupId,
                row.GroupName,
                row.ModuleId,
                false));

        var violations = new List<string>();
        foreach (var group in currentRows.Concat(contextRows).GroupBy(row => row.GroupId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModuleSequencePlacement? highest = null;
            var highestGroupOrder = int.MinValue;
            foreach (var current in group
                         .OrderBy(row => row.Date)
                         .ThenBy(row => row.Start)
                         .ThenBy(row => row.End)
                         .ThenBy(row => groupOrderByModule[row.ModuleId])
                         .ThenBy(row => row.ModuleId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentGroupOrder = groupOrderByModule[current.ModuleId];
                if (highest is not null
                    && currentGroupOrder < highestGroupOrder
                    && (current.IsCurrentValidationDraft || highest.IsCurrentValidationDraft))
                {
                    violations.Add(
                        $"{current.Date:yyyy-MM-dd} {current.GroupName} {current.Start:HH\\:mm}: модуль #{current.ModuleId} "
                        + $"із блоком послідовності {currentGroupOrder} розміщено після модуля #{highest.ModuleId} "
                        + $"із блоком {highestGroupOrder}.");
                }

                if (currentGroupOrder > highestGroupOrder
                    || (currentGroupOrder == highestGroupOrder
                        && current.IsCurrentValidationDraft
                        && highest?.IsCurrentValidationDraft == false))
                {
                    highest = current;
                    highestGroupOrder = currentGroupOrder;
                }
            }
        }

        return violations;
    }

    private static IEnumerable<string> FindTopicOrderViolations(
        IReadOnlyList<PlacementRow> currentPlacements,
        IReadOnlyList<PlacementRow> historyPlacements,
        CancellationToken cancellationToken)
    {
        var topicPlacements = currentPlacements
            .Select(row => new TopicOrderPlacement(row, row.IsDraft))
            .Concat(historyPlacements.Select(row => new TopicOrderPlacement(row, false)))
            .Where(item => item.Row.ModuleTopicId is not null
                           && item.Row.ModuleTopicOrder is not null
                           && !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(item.Row.LessonTypeCode))
            .ToList();
        foreach (var group in CollapseTopicOrderPlacements(topicPlacements, cancellationToken)
                     .GroupBy(item => new { item.Row.GroupId, item.Row.ModuleId }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TopicOrderPlacement? highestTopic = null;
            foreach (var current in group
                         .OrderBy(item => item.Row.Date)
                         .ThenBy(item => item.Row.Start)
                         .ThenBy(item => item.Row.End)
                         .ThenBy(item => item.Row.ModuleTopicOrder)
                         .ThenBy(item => item.Row.ModuleTopicId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (highestTopic?.Row.ModuleTopicOrder is int highestOrder
                    && current.Row.ModuleTopicOrder is int currentOrder
                    && currentOrder < highestOrder
                    && (current.IsCurrentValidationDraft || highestTopic.IsCurrentValidationDraft))
                {
                    yield return $"{current.Row.Date:yyyy-MM-dd} {current.Row.GroupName} {current.Row.Start:HH\\:mm}: тема #{current.Row.ModuleTopicId} модуля #{current.Row.ModuleId} має порядок {currentOrder} після теми #{highestTopic.Row.ModuleTopicId} з порядком {highestOrder}.";
                }

                if (highestTopic is null
                    || current.Row.ModuleTopicOrder > highestTopic.Row.ModuleTopicOrder
                    || (current.Row.ModuleTopicOrder == highestTopic.Row.ModuleTopicOrder
                        && current.IsCurrentValidationDraft
                        && !highestTopic.IsCurrentValidationDraft))
                {
                    highestTopic = current;
                }
            }
        }
    }

    private static IEnumerable<TopicOrderPlacement> CollapseTopicOrderPlacements(
        IEnumerable<TopicOrderPlacement> placements,
        CancellationToken cancellationToken)
    {
        var buffered = placements.ToList();
        foreach (var group in buffered
                     .Where(item => !HasLogicalDraftEventKey(item.Row))
                     .GroupBy(item => new
                     {
                         item.Row.Date,
                         item.Row.Start,
                         item.Row.End,
                         item.Row.GroupId,
                         item.Row.ModuleId,
                         item.Row.LessonTypeId,
                         item.Row.ModuleTopicId,
                         item.Row.RoomId,
                         item.Row.IsSelfStudy
                     }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = group.ToList();
            yield return new TopicOrderPlacement(
                rows[0].Row,
                rows.Any(item => item.IsCurrentValidationDraft));
        }

        foreach (var group in buffered
                     .Where(item => HasLogicalDraftEventKey(item.Row))
                     .GroupBy(item => new
                     {
                         item.Row.BatchKey,
                         item.Row.Date,
                         item.Row.Start,
                         item.Row.End,
                         item.Row.GroupId,
                         item.Row.ModuleId,
                         item.Row.LessonTypeId,
                         item.Row.ModuleTopicId
                     }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = group.ToList();
            yield return new TopicOrderPlacement(
                rows[0].Row,
                rows.Any(item => item.IsCurrentValidationDraft));
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

    private static RoomCapacityStats AnalyzeRoomCapacity(
        IReadOnlyList<PlacementRow> placements,
        CancellationToken cancellationToken)
    {
        var violations = new List<string>();
        var maxSharedGroupCount = 0;
        var maxSharedGroupLabel = "Спільних потоків не знайдено";

        foreach (var roomDay in placements
                     .Where(row => row.RoomId is not null
                                   && row.RoomCapacity is not null
                                   && row.RequiresRoom
                                   && row.End > row.Start
                                   && !LessonTypeOccupancyPolicy.IsNonOccupyingMarker(row.LessonTypeCode))
                     .GroupBy(row => new { row.Date, row.RoomId, row.RoomName, row.RoomCapacity }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var boundaries = roomDay
                .SelectMany(row => new[] { row.Start, row.End })
                .Distinct()
                .OrderBy(value => value)
                .ToList();
            for (var boundaryIndex = 0; boundaryIndex + 1 < boundaries.Count; boundaryIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var intervalStart = boundaries[boundaryIndex];
                var intervalEnd = boundaries[boundaryIndex + 1];
                if (intervalEnd <= intervalStart)
                {
                    continue;
                }

                var activeRows = roomDay
                    .Where(row => row.Start < intervalEnd && intervalStart < row.End)
                    .ToList();
                if (!activeRows.Any(row => row.IsDraft))
                {
                    continue;
                }

                var groups = activeRows
                    .GroupBy(row => row.GroupId)
                    .Select(group => group.First())
                    .ToList();
                var totalStudents = groups.Sum(row => row.GroupStudentsCount);
                if (groups.Count > maxSharedGroupCount)
                {
                    maxSharedGroupCount = groups.Count;
                    maxSharedGroupLabel = $"{roomDay.Key.Date:yyyy-MM-dd} {intervalStart:HH\\:mm}-{intervalEnd:HH\\:mm}, ауд. {roomDay.Key.RoomName}, groups={groups.Count}, students={totalStudents}/{roomDay.Key.RoomCapacity}";
                }

                if (totalStudents > roomDay.Key.RoomCapacity)
                {
                    violations.Add($"{roomDay.Key.Date:yyyy-MM-dd} {intervalStart:HH\\:mm}-{intervalEnd:HH\\:mm}: аудиторія {roomDay.Key.RoomName} має {roomDay.Key.RoomCapacity} місць для {totalStudents} студентів.");
                }
            }
        }

        return new RoomCapacityStats(violations, maxSharedGroupCount, maxSharedGroupLabel);
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

    private sealed record CanonicalSlotIndex(
        IReadOnlyList<TimeSlot> Slots,
        IReadOnlyDictionary<TimeOnly, int> FirstIndexByStart,
        IReadOnlyDictionary<TimeOnly, int> LastIndexByEnd);

    private sealed record TopicPlanRow(
        int ModuleId,
        int LessonTypeId,
        int Order);

    private sealed record TopicOrderPlacement(
        PlacementRow Row,
        bool IsCurrentValidationDraft);

    private sealed record ModuleSequenceHistoryRow(
        DateOnly Date,
        TimeOnly Start,
        TimeOnly End,
        int GroupId,
        string GroupName,
        int ModuleId,
        string LessonTypeCode);

    private sealed record ModuleSequencePlacement(
        DateOnly Date,
        TimeOnly Start,
        TimeOnly End,
        int GroupId,
        string GroupName,
        int ModuleId,
        bool IsCurrentValidationDraft);

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
        int? RoomBuildingId,
        bool RequiresTeacher,
        bool RequiresRoom,
        bool BlocksTeacher,
        bool BlocksRoom,
        bool IsSelfStudy,
        string? BatchKey);
}

public sealed record TeacherDraftsAutogenHardRuleValidationRequest(
    int CourseId,
    IReadOnlyCollection<int> GroupIds,
    DateOnly From,
    DateOnly To,
    WeekPreset Days = WeekPreset.MonFri,
    bool AllowIncompleteDrafts = false,
    DraftStatus DraftStatus = DraftStatus.Draft,
    IReadOnlyCollection<TeacherDraftsAutogenPendingDraft>? PendingDrafts = null,
    IReadOnlyCollection<int>? ExcludedDraftIds = null,
    bool IncludeStoredDrafts = true,
    int? MaxParallelGroupsPerModuleInSlot = null,
    bool ScopePendingDraftsToCourse = false,
    int? MaxStoredContextRows = null);

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
    bool IsSelfStudy,
    string? BatchKey = null);

public sealed record TeacherDraftsAutogenHardRuleValidationResult(
    IReadOnlyList<string> Violations,
    int MaxSharedGroupCount,
    string MaxSharedGroupLabel)
{
    public bool HasViolations => Violations.Count > 0;
}
