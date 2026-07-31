using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

internal static class TeacherDraftsAutogenInputFingerprint
{
    private static readonly byte[] ChunkSeparator = [0];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<string> CaptureAsync(
        AppDbContext db,
        AutoGenJobRequest request,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
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

        Append(hash, "lesson-types", await db.LessonTypes.AsNoTracking()
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
            })
            .ToListAsync(cancellationToken));

        var courses = await db.Courses.AsNoTracking()
            .Where(item => item.Id == courseId)
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.DurationWeeks,
                item.AcademicPeriodStartDate
            })
            .ToListAsync(cancellationToken);
        Append(hash, "course", courses);
        var academicPeriodStartDate = courses.SingleOrDefault()?.AcademicPeriodStartDate;
        var academicPeriodEndDateExclusive = academicPeriodStartDate is DateOnly academicStartDate
            ? academicStartDate.AddDays(courses[0].DurationWeeks * 7)
            : (DateOnly?)null;

        Append(hash, "course-groups", await db.Groups.AsNoTracking()
            .Where(item => item.CourseId == courseId)
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.StudentsCount,
                item.CourseId
            })
            .ToListAsync(cancellationToken));

        var activeModulePlans = await db.ModulePlans.AsNoTracking()
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
            })
            .ToListAsync(cancellationToken);
        Append(hash, "active-module-plans", activeModulePlans);

        var relevantModuleIds = activeModulePlans
            .Select(item => item.ModuleId)
            .Concat(requestedModuleIds)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        Append(hash, "modules", await db.Modules.AsNoTracking()
            .Where(item => relevantModuleIds.Contains(item.Id))
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Code,
                item.Title,
                item.Credits,
                item.CourseId
            })
            .ToListAsync(cancellationToken));

        Append(hash, "module-course-links", await db.ModuleCourses.AsNoTracking()
            .Where(item => item.CourseId == courseId && relevantModuleIds.Contains(item.ModuleId))
            .OrderBy(item => item.ModuleId)
            .ThenBy(item => item.CourseId)
            .Select(item => new
            {
                item.ModuleId,
                item.CourseId
            })
            .ToListAsync(cancellationToken));

        var teacherModuleLinks = await db.TeacherModules.AsNoTracking()
            .Where(item => relevantModuleIds.Contains(item.ModuleId))
            .OrderBy(item => item.ModuleId)
            .ThenBy(item => item.TeacherId)
            .Select(item => new
            {
                item.TeacherId,
                item.ModuleId
            })
            .ToListAsync(cancellationToken);
        Append(hash, "teacher-modules", teacherModuleLinks);

        var supervisorLinks = await db.ModuleSupervisors.AsNoTracking()
            .Where(item => relevantModuleIds.Contains(item.ModuleId))
            .OrderBy(item => item.ModuleId)
            .ThenBy(item => item.TeacherId)
            .Select(item => new
            {
                item.TeacherId,
                item.ModuleId
            })
            .ToListAsync(cancellationToken);
        Append(hash, "module-supervisors", supervisorLinks);

        var relevantTeacherIds = teacherModuleLinks
            .Select(item => item.TeacherId)
            .Concat(supervisorLinks.Select(item => item.TeacherId))
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        var teachers = await db.Teachers.AsNoTracking()
            .Where(item => relevantTeacherIds.Contains(item.Id))
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.FullName,
                item.DepartmentId
            })
            .ToListAsync(cancellationToken);
        Append(hash, "teachers", teachers);

        Append(hash, "teacher-working-hours", await db.TeacherWorkingHours.AsNoTracking()
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
            })
            .ToListAsync(cancellationToken));

        var moduleTopics = await db.ModuleTopics.AsNoTracking()
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
            })
            .ToListAsync(cancellationToken);
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
        Append(hash, "departments", await db.Departments.AsNoTracking()
            .Where(item => relevantDepartmentIds.Contains(item.Id))
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Name
            })
            .ToListAsync(cancellationToken));

        var rooms = await db.Rooms.AsNoTracking()
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.Capacity,
                item.BuildingId
            })
            .ToListAsync(cancellationToken);
        Append(hash, "rooms", rooms);
        var relevantRoomIds = rooms.Select(item => item.Id).ToList();
        var relevantBuildingIds = rooms.Select(item => item.BuildingId).Distinct().ToList();

        Append(hash, "building-travels", await db.BuildingTravels.AsNoTracking()
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
            })
            .ToListAsync(cancellationToken));

        Append(hash, "module-rooms", await db.ModuleRooms.AsNoTracking()
            .Where(item => relevantModuleIds.Contains(item.ModuleId))
            .OrderBy(item => item.ModuleId)
            .ThenBy(item => item.RoomId)
            .Select(item => new
            {
                item.ModuleId,
                item.RoomId
            })
            .ToListAsync(cancellationToken));

        Append(hash, "module-buildings", await db.ModuleBuildings.AsNoTracking()
            .Where(item => relevantModuleIds.Contains(item.ModuleId))
            .OrderBy(item => item.ModuleId)
            .ThenBy(item => item.BuildingId)
            .Select(item => new
            {
                item.ModuleId,
                item.BuildingId
            })
            .ToListAsync(cancellationToken));

        Append(hash, "preferred-first-slot-limits", await db.PreferredFirstSlotLimitConfigs.AsNoTracking()
            .Where(item => item.CourseId == null || item.CourseId == courseId)
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.CourseId,
                item.MaxSlotOrder
            })
            .ToListAsync(cancellationToken));

        Append(hash, "time-slots", await db.TimeSlots.AsNoTracking()
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
            })
            .ToListAsync(cancellationToken));

        Append(hash, "lunch-configs", await db.LunchConfigs.AsNoTracking()
            .Where(item => item.CourseId == null || item.CourseId == courseId)
            .OrderBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.CourseId,
                item.Start,
                item.End
            })
            .ToListAsync(cancellationToken));

        Append(hash, "calendar-exceptions", await db.CalendarExceptions.AsNoTracking()
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
            })
            .ToListAsync(cancellationToken));

        Append(hash, "module-sequence", await db.ModuleSequenceItems.AsNoTracking()
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
            })
            .ToListAsync(cancellationToken));

        Append(hash, "module-fillers", await db.ModuleFillers.AsNoTracking()
            .Where(item => item.CourseId == courseId && relevantModuleIds.Contains(item.ModuleId))
            .OrderBy(item => item.ModuleId)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.CourseId,
                item.ModuleId
            })
            .ToListAsync(cancellationToken));

        var historyStartDate = request.FromDate.AddMonths(-12);
        var planningEndDateExclusive = DateHelpers.StartOfWeek(request.ToDate).AddDays(7);

        Append(hash, "schedule-usage-and-occupancy", await db.ScheduleItems.AsNoTracking()
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
            })
            .ToListAsync(cancellationToken));

        Append(hash, "draft-usage-and-occupancy", await db.TeacherDraftItems.AsNoTracking()
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
            })
            .ToListAsync(cancellationToken));

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append<T>(IncrementalHash hash, string name, IReadOnlyCollection<T> rows)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(name));
        hash.AppendData(ChunkSeparator);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(rows, JsonOptions));
        hash.AppendData(ChunkSeparator);
    }
}
