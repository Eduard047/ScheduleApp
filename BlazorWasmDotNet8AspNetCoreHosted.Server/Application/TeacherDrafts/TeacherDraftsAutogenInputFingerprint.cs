using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

internal static class TeacherDraftsAutogenInputFingerprint
{
    internal const int MaxRowsPerFingerprintSection = 50_000;
    internal const int MaxTotalFingerprintRows = 200_000;
    private static readonly byte[] ChunkSeparator = [0];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<string> CaptureAsync(
        AppDbContext db,
        AutoGenJobRequest request,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var rowBudget = new FingerprintRowBudget();
        var courseId = request.CourseId;
        var selectedGroupIds = (request.GroupIds ?? [])
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        var requestedModuleHours = (request.ModuleHours ?? new Dictionary<int, int>())
            .OrderBy(item => item.Key)
            .Select(item => new { ModuleId = item.Key, item.Value })
            .ToList();
        var requestedModuleIds = requestedModuleHours
            .Where(item => item.ModuleId > 0 && item.Value > 0)
            .Select(item => item.ModuleId)
            .Distinct()
            .ToList();

        Append(hash, "request-scope", new[]
        {
            new
            {
                request.Kind,
                request.FromDate,
                request.ToDate,
                request.CourseId,
                GroupIds = selectedGroupIds,
                ModuleHours = requestedModuleHours,
                request.Days,
                request.ClearExisting,
                request.SoftFill,
                request.PreflightOnly,
                request.AllowIncompleteDrafts,
                GroupRoomPreferences = request.GroupRoomPreferences?.Select((item, index) => new
                {
                    Index = index,
                    item.GroupId,
                    item.BuildingId,
                    RoomIds = item.RoomIds?.ToList()
                }).ToList(),
                request.SoftOptions,
                request.PreferredFirstMaxSlotOrderOverride,
                request.PreviewOnly
            }
        });

        Append(hash, "lesson-types", await LoadBoundedAsync(db.LessonTypes.AsNoTracking()
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Code,
                item.Name,
                item.IsActive,
                item.RequiresRoom,
                item.RequiresTeacher,
                item.BlocksRoom,
                item.BlocksTeacher,
                item.CountInPlan,
                item.CountInLoad,
                item.PreferredFirstInWeek
            }), rowBudget, cancellationToken));

        var courses = await LoadBoundedAsync(db.Courses.AsNoTracking()
            .Where(item => item.Id == courseId)
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.DurationWeeks,
                item.AcademicPeriodStartDate
            }), rowBudget, cancellationToken);
        Append(hash, "course", courses);
        var academicPeriodStartDate = courses.SingleOrDefault()?.AcademicPeriodStartDate;
        var academicPeriodEndDateExclusive = academicPeriodStartDate is DateOnly academicStartDate
            ? academicStartDate.AddDays(courses[0].DurationWeeks * 7)
            : (DateOnly?)null;

        Append(hash, "course-groups", await LoadBoundedAsync(db.Groups.AsNoTracking()
            .Where(item => item.CourseId == courseId)
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.StudentsCount,
                item.CourseId
            }), rowBudget, cancellationToken));

        var activeModulePlans = await LoadBoundedAsync(db.ModulePlans.AsNoTracking()
            .Where(item => item.CourseId == courseId && item.IsActive)
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.CourseId,
                item.ModuleId,
                item.TargetHours,
                item.ScheduledHours,
                item.IsActive
            }), rowBudget, cancellationToken);
        Append(hash, "active-module-plans", activeModulePlans);

        var relevantModuleIds = activeModulePlans
            .Select(item => item.ModuleId)
            .Concat(requestedModuleIds)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        Append(hash, "modules", await LoadBoundedAsync(db.Modules.AsNoTracking()
            .Where(item => relevantModuleIds.Contains(item.Id))
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Code,
                item.Title,
                item.Credits,
                item.CourseId
            }), rowBudget, cancellationToken));

        Append(hash, "module-course-links", await LoadBoundedAsync(db.ModuleCourses.AsNoTracking()
            .Where(item => item.CourseId == courseId && relevantModuleIds.Contains(item.ModuleId))
            .OrderBy(item => item.ModuleId)
            .ThenBy(item => item.CourseId)
            .Select(item => new
            {
                item.ModuleId,
                item.CourseId
            }), rowBudget, cancellationToken));

        var teacherModuleLinks = await LoadBoundedAsync(db.TeacherModules.AsNoTracking()
            .Where(item => relevantModuleIds.Contains(item.ModuleId))
            .OrderBy(item => item.ModuleId)
            .ThenBy(item => item.TeacherId)
            .Select(item => new
            {
                item.TeacherId,
                item.ModuleId
            }), rowBudget, cancellationToken);
        Append(hash, "teacher-modules", teacherModuleLinks);

        var supervisorLinks = await LoadBoundedAsync(db.ModuleSupervisors.AsNoTracking()
            .Where(item => relevantModuleIds.Contains(item.ModuleId))
            .OrderBy(item => item.ModuleId)
            .ThenBy(item => item.TeacherId)
            .Select(item => new
            {
                item.TeacherId,
                item.ModuleId
            }), rowBudget, cancellationToken);
        Append(hash, "module-supervisors", supervisorLinks);

        var relevantTeacherIds = teacherModuleLinks
            .Select(item => item.TeacherId)
            .Concat(supervisorLinks.Select(item => item.TeacherId))
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        var teachers = await LoadBoundedAsync(db.Teachers.AsNoTracking()
            .Where(item => relevantTeacherIds.Contains(item.Id))
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.FullName,
                item.DepartmentId
            }), rowBudget, cancellationToken);
        Append(hash, "teachers", teachers);

        Append(hash, "teacher-working-hours", await LoadBoundedAsync(db.TeacherWorkingHours.AsNoTracking()
            .Where(item => relevantTeacherIds.Contains(item.TeacherId))
            .OrderBy(item => item.TeacherId)
            .ThenBy(item => item.DayOfWeek)
            .ThenBy(item => item.Start)
            .ThenBy(item => item.End)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.TeacherId,
                item.DayOfWeek,
                item.Start,
                item.End
            }), rowBudget, cancellationToken));

        var moduleTopics = await LoadBoundedAsync(db.ModuleTopics.AsNoTracking()
            .Where(item => relevantModuleIds.Contains(item.ModuleId))
            .OrderBy(item => item.ModuleId)
            .ThenBy(item => item.Order)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.ModuleId,
                item.Order,
                item.TopicCode,
                item.LessonTypeId,
                item.DepartmentId,
                item.TotalHours,
                item.AuditoriumHours,
                item.SelfStudyHours,
                item.IsInterAssembly,
                item.SelfStudyBySupervisor
            }), rowBudget, cancellationToken);
        Append(hash, "module-topics", moduleTopics);

        var relevantDepartmentIds = teachers
            .Where(item => item.DepartmentId is not null)
            .Select(item => item.DepartmentId!.Value)
            .Concat(moduleTopics
                .Where(item => item.DepartmentId is not null)
                .Select(item => item.DepartmentId!.Value))
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        Append(hash, "departments", await LoadBoundedAsync(db.Departments.AsNoTracking()
            .Where(item => relevantDepartmentIds.Contains(item.Id))
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Name
            }), rowBudget, cancellationToken));

        var rooms = await LoadBoundedAsync(db.Rooms.AsNoTracking()
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.Capacity,
                item.BuildingId
            }), rowBudget, cancellationToken);
        Append(hash, "rooms", rooms);
        var relevantRoomIds = rooms.Select(item => item.Id).ToList();
        var relevantBuildingIds = rooms.Select(item => item.BuildingId).Distinct().ToList();

        Append(hash, "building-travels", await LoadBoundedAsync(db.BuildingTravels.AsNoTracking()
            .Where(item => relevantBuildingIds.Contains(item.FromBuildingId)
                           && relevantBuildingIds.Contains(item.ToBuildingId))
            .OrderBy(item => item.FromBuildingId)
            .ThenBy(item => item.ToBuildingId)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.FromBuildingId,
                item.ToBuildingId,
                item.Minutes
            }), rowBudget, cancellationToken));

        Append(hash, "module-rooms", await LoadBoundedAsync(db.ModuleRooms.AsNoTracking()
            .Where(item => relevantModuleIds.Contains(item.ModuleId))
            .OrderBy(item => item.ModuleId)
            .ThenBy(item => item.RoomId)
            .Select(item => new
            {
                item.ModuleId,
                item.RoomId
            }), rowBudget, cancellationToken));

        Append(hash, "module-buildings", await LoadBoundedAsync(db.ModuleBuildings.AsNoTracking()
            .Where(item => relevantModuleIds.Contains(item.ModuleId))
            .OrderBy(item => item.ModuleId)
            .ThenBy(item => item.BuildingId)
            .Select(item => new
            {
                item.ModuleId,
                item.BuildingId
            }), rowBudget, cancellationToken));

        Append(hash, "preferred-first-slot-limits", await LoadBoundedAsync(db.PreferredFirstSlotLimitConfigs.AsNoTracking()
            .Where(item => item.CourseId == null || item.CourseId == courseId)
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.CourseId,
                item.MaxSlotOrder
            }), rowBudget, cancellationToken));

        Append(hash, "time-slots", await LoadBoundedAsync(db.TimeSlots.AsNoTracking()
            .Where(item => item.CourseId == null || item.CourseId == courseId)
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.CourseId,
                item.DayOfWeek,
                item.Start,
                item.End,
                item.SortOrder,
                item.IsActive
            }), rowBudget, cancellationToken));

        Append(hash, "lunch-configs", await LoadBoundedAsync(db.LunchConfigs.AsNoTracking()
            .Where(item => item.CourseId == null || item.CourseId == courseId)
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.CourseId,
                item.Start,
                item.End
            }), rowBudget, cancellationToken));

        Append(hash, "calendar-exceptions", await LoadBoundedAsync(db.CalendarExceptions.AsNoTracking()
            .Where(item => item.Date >= request.FromDate
                           && item.Date <= request.ToDate
                           && (item.CourseId == null || item.CourseId == courseId)
                           && (item.GroupId == null || selectedGroupIds.Contains(item.GroupId.Value)))
            .OrderBy(item => item.Date)
            .ThenBy(item => item.CourseId)
            .ThenBy(item => item.GroupId)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Date,
                item.IsWorkingDay,
                item.Name,
                item.CourseId,
                item.GroupId
            }), rowBudget, cancellationToken));

        Append(hash, "module-sequence", await LoadBoundedAsync(db.ModuleSequenceItems.AsNoTracking()
            .Where(item => item.CourseId == courseId && relevantModuleIds.Contains(item.ModuleId))
            .OrderBy(item => item.Order)
            .ThenBy(item => item.GroupOrder)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.CourseId,
                item.ModuleId,
                item.Order,
                item.GroupOrder
            }), rowBudget, cancellationToken));

        Append(hash, "module-fillers", await LoadBoundedAsync(db.ModuleFillers.AsNoTracking()
            .Where(item => item.CourseId == courseId && relevantModuleIds.Contains(item.ModuleId))
            .OrderBy(item => item.ModuleId)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.CourseId,
                item.ModuleId
            }), rowBudget, cancellationToken));

        var historyStartDate = request.FromDate.AddMonths(-12);
        var planningEndDateExclusive = DateHelpers.StartOfWeek(request.ToDate).AddDays(7);

        Append(hash, "schedule-usage-and-occupancy", await LoadBoundedAsync(db.ScheduleItems.AsNoTracking()
            .Where(item =>
                (item.Group.CourseId == courseId
                 && (academicPeriodStartDate == null || item.Date >= academicPeriodStartDate.Value)
                 && item.Date < planningEndDateExclusive)
                || (item.Group.CourseId == courseId
                    && item.TeacherId != null
                    && relevantTeacherIds.Contains(item.TeacherId.Value)
                    && (academicPeriodStartDate == null
                        || (item.Date >= academicPeriodStartDate.Value
                            && item.Date < academicPeriodEndDateExclusive!.Value)))
                || (selectedGroupIds.Contains(item.GroupId)
                    && item.Date >= historyStartDate
                    && item.Date < planningEndDateExclusive)
                || (item.Date >= request.FromDate
                    && item.Date <= request.ToDate
                    && ((item.TeacherId != null && relevantTeacherIds.Contains(item.TeacherId.Value))
                        || (item.RoomId != null && relevantRoomIds.Contains(item.RoomId.Value)))))
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Date,
                item.DayOfWeek,
                item.StartTime,
                item.EndTime,
                item.LessonTypeId,
                item.GroupId,
                item.ModuleId,
                item.ModuleTopicId,
                item.TeacherId,
                item.RoomId,
                item.BatchKey,
                item.IsLocked,
                item.IsSelfStudy
            }), rowBudget, cancellationToken));

        Append(hash, "draft-usage-and-occupancy", await LoadBoundedAsync(db.TeacherDraftItems.AsNoTracking()
            .Where(item =>
                (item.Group.CourseId == courseId
                 && (academicPeriodStartDate == null || item.Date >= academicPeriodStartDate.Value)
                 && item.Date < planningEndDateExclusive)
                || (item.Group.CourseId == courseId
                    && item.TeacherId != null
                    && relevantTeacherIds.Contains(item.TeacherId.Value)
                    && (academicPeriodStartDate == null
                        || (item.Date >= academicPeriodStartDate.Value
                            && item.Date < academicPeriodEndDateExclusive!.Value)))
                || (selectedGroupIds.Contains(item.GroupId)
                    && item.Date >= historyStartDate
                    && item.Date < planningEndDateExclusive)
                || (item.Date >= request.FromDate
                    && item.Date <= request.ToDate
                    && ((item.TeacherId != null && relevantTeacherIds.Contains(item.TeacherId.Value))
                        || (item.RoomId != null && relevantRoomIds.Contains(item.RoomId.Value)))))
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Date,
                item.DayOfWeek,
                item.StartTime,
                item.EndTime,
                item.LessonTypeId,
                item.GroupId,
                item.ModuleId,
                item.ModuleTopicId,
                item.TeacherId,
                item.RoomId,
                item.Status,
                item.PublishedItemId,
                item.BatchKey,
                item.IsLocked,
                item.IsSelfStudy
            }), rowBudget, cancellationToken));

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<List<T>> LoadBoundedAsync<T>(
        IQueryable<T> query,
        FingerprintRowBudget rowBudget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rowLimit = Math.Min(MaxRowsPerFingerprintSection, rowBudget.RemainingRows);
        var rows = await query
            .Take(rowLimit + 1)
            .ToListAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (rows.Count > rowLimit)
        {
            throw new AutoGenPlanCapacityException(
                rowBudget.RemainingRows < MaxRowsPerFingerprintSection
                    ? $"Сукупний обсяг вхідних даних автогенерації перевищує безпечний ліміт {MaxTotalFingerprintRows} рядків. Зменште обсяг даних і повторіть спробу."
                    : $"Один із наборів вхідних даних автогенерації перевищує безпечний ліміт {MaxRowsPerFingerprintSection} рядків. Зменште обсяг даних і повторіть спробу.");
        }

        rowBudget.Consume(rows.Count);
        return rows;
    }

    private static void Append<T>(IncrementalHash hash, string name, IReadOnlyCollection<T> rows)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(name));
        hash.AppendData(ChunkSeparator);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(rows, JsonOptions));
        hash.AppendData(ChunkSeparator);
    }

    // Обмежує як кожен SQL-набір, так і сукупний відбиток; Take(limit + 1)
    // дає змогу відхилити завеликий набір без його повного завантаження.
    private sealed class FingerprintRowBudget
    {
        public int RemainingRows { get; private set; } = MaxTotalFingerprintRows;

        public void Consume(int count)
            => RemainingRows -= count;
    }
}
