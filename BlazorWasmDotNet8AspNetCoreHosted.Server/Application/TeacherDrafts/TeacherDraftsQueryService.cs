using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

// Сервіс читання чернеток викладачів з бази.
public sealed class TeacherDraftsQueryService
{
    private readonly AppDbContext _db;
    public TeacherDraftsQueryService(AppDbContext db)
    {
        _db = db;
    }
    // Повертає чернетки за тиждень з опційними фільтрами.
    public async Task<IReadOnlyList<TeacherDraftItemDto>> GetAsync(
        DateOnly weekStart,
        int? teacherId,
        int? groupId,
        int? roomId)
    {
        var weekEnd = weekStart.AddDays(7);
        var q = _db.TeacherDraftItems
            .AsNoTracking()
            .Include(x => x.Group).ThenInclude(g => g.Course)
            .Include(x => x.Module)
            .Include(x => x.ModuleTopic)
            .Include(x => x.Teacher)
            .Include(x => x.Room).ThenInclude(r => r!.Building)
            .Include(x => x.LessonType)
            .Where(x => x.Date >= weekStart && x.Date < weekEnd)
            .AsQueryable();
        if (teacherId is int tid) q = q.Where(x => x.TeacherId == tid);
        if (groupId is int gid) q = q.Where(x => x.GroupId == gid);
        if (roomId is int rid) q = q.Where(x => x.RoomId == rid);
        var items = await q.OrderBy(x => x.Date).ThenBy(x => x.StartTime).ToListAsync();
        var topicIds = items
            .Where(i => i.ModuleTopicId is int)
            .Select(i => i.ModuleTopicId!.Value)
            .Distinct()
            .ToList();
        var topicCodeLookup = topicIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.ModuleTopics
                .Where(mt => topicIds.Contains(mt.Id))
                .Select(mt => new { mt.Id, mt.TopicCode })
                .ToDictionaryAsync(mt => mt.Id, mt => (mt.TopicCode ?? string.Empty).Trim());
        var rescheduleSourceIds = items
            .Select(i => TeacherDraftsHelpers.ParseRescheduleBatchKey(i.BatchKey))
            .Where(info => info.isRescheduled && info.sourceItemId is int)
            .Select(info => info.sourceItemId!.Value)
            .Distinct()
            .ToList();
        var rescheduleTopicLookup = rescheduleSourceIds.Count == 0
            ? new Dictionary<int, (int? topicId, string? topicCode)>()
            : await _db.ScheduleItems
                .AsNoTracking()
                .Where(si => rescheduleSourceIds.Contains(si.Id))
                .Select(si => new
                {
                    si.Id,
                    si.ModuleTopicId,
                    TopicCode = si.ModuleTopic != null ? si.ModuleTopic.TopicCode : null
                })
                .ToDictionaryAsync(
                    x => x.Id,
                    x => (topicId: x.ModuleTopicId, topicCode: string.IsNullOrWhiteSpace(x.TopicCode) ? null : x.TopicCode!.Trim()));
        // Формує ключ для групування викладачів у межах одного слоту та групи.
        static string ResolveTeacherGroupKey(TeacherDraftItem item)
        {
            var roomPart = item.RoomId.HasValue ? item.RoomId.Value.ToString() : "none";
            return $"slot:{item.Date:yyyyMMdd}|{item.StartTime:HHmm}|{item.EndTime:HHmm}|g{item.GroupId}|m{item.ModuleId}|lt{item.LessonTypeId}|r{roomPart}";
        }
        var teacherGroups = items
            .GroupBy(ResolveTeacherGroupKey)
            .ToDictionary(
                g => g.Key,
                g => g.Select(t => t.Teacher?.FullName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToList()
            );
        return items.Select(i =>
        {
            var rescheduleInfo = TeacherDraftsHelpers.ParseRescheduleBatchKey(i.BatchKey);
            var groupKey = ResolveTeacherGroupKey(i);
            var teacherNames = teacherGroups.TryGetValue(groupKey, out var groupedNames)
                ? groupedNames
                : new List<string>();
            if (teacherNames.Count == 0 && !string.IsNullOrWhiteSpace(i.Teacher?.FullName))
            {
                teacherNames = new List<string> { i.Teacher!.FullName };
            }
            var teacherLabel = teacherNames.Count > 0 ? string.Join(", ", teacherNames) : (i.Teacher?.FullName ?? "");
            var resolvedTopicId = i.ModuleTopicId;
            if (resolvedTopicId is null && rescheduleInfo.sourceItemId is int sid && rescheduleTopicLookup.TryGetValue(sid, out var topicInfoFromSource))
            {
                resolvedTopicId = topicInfoFromSource.topicId;
            }
            var topicCode = TeacherDraftsHelpers.BuildModuleTopicCode(i.ModuleTopic);
            if (topicCode is null && resolvedTopicId is int mtId && topicCodeLookup.TryGetValue(mtId, out var resolvedCode) && !string.IsNullOrWhiteSpace(resolvedCode))
            {
                topicCode = resolvedCode;
            }
            if (topicCode is null && rescheduleInfo.sourceItemId is int sourceId && rescheduleTopicLookup.TryGetValue(sourceId, out var reschedTopic) && !string.IsNullOrWhiteSpace(reschedTopic.topicCode))
            {
                topicCode = reschedTopic.topicCode;
            }
            var requiresRoom = i.LessonType.RequiresRoom;
            var missingTeacherAssignment = i.LessonType.RequiresTeacher && i.TeacherId is null;
            var missingRoomAssignment = requiresRoom && i.RoomId is null;
            return new TeacherDraftItemDto(
                Id: i.Id,
                Date: i.Date,
                TimeStart: i.StartTime.ToString("HH:mm"),
                TimeEnd: i.EndTime.ToString("HH:mm"),
                DayNumber: (int)i.DayOfWeek,
                Group: i.Group.Name,
                GroupId: i.GroupId,
                Module: i.Module.Title,
                ModuleId: i.ModuleId,
                TopicCode: topicCode,
                ModuleTopicId: resolvedTopicId,
                Teacher: teacherLabel,
                TeacherId: i.TeacherId,
                Room: requiresRoom && i.Room is not null ? i.Room.Name : "",
                RoomId: requiresRoom ? i.RoomId : null,
                RequiresRoom: requiresRoom,
                MissingTeacherAssignment: missingTeacherAssignment,
                MissingRoomAssignment: missingRoomAssignment,
                LessonTypeId: i.LessonTypeId,
                LessonTypeCode: i.LessonType.Code,
                LessonTypeName: i.LessonType.Name,
                Status: (DraftStatusDto)(int)i.Status,
                PublishedItemId: i.PublishedItemId,
                Warnings: i.ValidationWarnings,
                IsLocked: i.IsLocked,
                IsRescheduled: rescheduleInfo.isRescheduled,
                RescheduledFromLessonTypeId: rescheduleInfo.originalLessonTypeId,
                BatchKey: i.BatchKey,
                TeacherNames: teacherNames,
                LessonTypeCss: i.LessonType.CssKey,
                IsSelfStudy: i.IsSelfStudy,
                Revision: i.Revision
            );
        }).ToList();
    }
}
