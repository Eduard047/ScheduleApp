using System.Linq;

using System.Collections.Generic;

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using Microsoft.AspNetCore.Http;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;





// Контролер адміністратора для роботи з модулями
[ApiController]
[Route("api/admin/modules")]
public class AdminModulesController(AppDbContext db) : ControllerBase
{
    
    
    [HttpGet]
    public async Task<object> List()
    {
        
        var modules = await db.Modules.AsNoTracking()
            .Select(m => new
            {
                m.Id,
                m.Code,
                m.Title,
                m.CourseId,
                m.Credits,
                
                AllowedRoomIds = m.AllowedRooms.Select(ar => ar.RoomId).ToList(),
                AllowedBuildingIds = m.AllowedBuildings.Select(ab => ab.BuildingId).ToList(),
                CloneCourseIds = m.ModuleCourses
                    .Where(mc => mc.CourseId != m.CourseId)
                    .Select(mc => mc.CourseId)
                    .ToList()
            })
            .ToListAsync();

        return modules;
    }

    
    
    
    
    
    [HttpPost("upsert")]
    public async Task<ActionResult<int>> Upsert(ModuleEditDto dto)
    {
        
        _ = await db.Courses.FindAsync(dto.CourseId) ?? throw new ArgumentException("Курс не знайдено");

        Module m;
        if (dto.Id is int id && id > 0)
        {
            
            m = await db.Modules
                .Include(x => x.AllowedRooms)
                .Include(x => x.AllowedBuildings)
                .Include(x => x.ModuleCourses)
                .FirstOrDefaultAsync(x => x.Id == id)
                ?? throw new ArgumentException("Модуль не знайдено");

            
            m.Code = dto.Code;
            m.Title = dto.Title;
            m.CourseId = dto.CourseId;
            
            m.Credits = dto.Credits;

            
            var oldRoomIds = m.AllowedRooms.Select(x => x.RoomId).ToHashSet();
            var newRoomIds = dto.AllowedRoomIds.ToHashSet();

            
            db.ModuleRooms.RemoveRange(m.AllowedRooms.Where(x => !newRoomIds.Contains(x.RoomId)));
            
            foreach (var add in newRoomIds.Except(oldRoomIds))
                db.ModuleRooms.Add(new ModuleRoom { ModuleId = m.Id, RoomId = add });

            
            var oldBIds = m.AllowedBuildings.Select(x => x.BuildingId).ToHashSet();
            var newBIds = dto.AllowedBuildingIds.ToHashSet();

            db.ModuleBuildings.RemoveRange(m.AllowedBuildings.Where(x => !newBIds.Contains(x.BuildingId)));
            foreach (var add in newBIds.Except(oldBIds))
                db.ModuleBuildings.Add(new ModuleBuilding { ModuleId = m.Id, BuildingId = add });

            var additionalCourseIds = dto.CloneCourseIds?
                .Where(id => id > 0 && id != dto.CourseId)
                .Distinct()
                .ToList() ?? new List<int>();

            var desiredCourseIds = new HashSet<int>(additionalCourseIds) { dto.CourseId };

            var linksToRemove = m.ModuleCourses
                .Where(link => !desiredCourseIds.Contains(link.CourseId))
                .ToList();
            foreach (var link in linksToRemove)
            {
                m.ModuleCourses.Remove(link);
                db.ModuleCourses.Remove(link);
            }

            var removedCourseIds = linksToRemove
                .Select(link => link.CourseId)
                .Distinct()
                .ToList();

            if (removedCourseIds.Count > 0)
            {
                await db.ModulePlans.Where(p => p.ModuleId == m.Id && removedCourseIds.Contains(p.CourseId)).ExecuteDeleteAsync();
                await db.ModuleSequenceItems.Where(si => si.ModuleId == m.Id && removedCourseIds.Contains(si.CourseId)).ExecuteDeleteAsync();
                await db.ModuleFillers.Where(f => f.ModuleId == m.Id && removedCourseIds.Contains(f.CourseId)).ExecuteDeleteAsync();
            }

            foreach (var cid in desiredCourseIds)
            {
                if (!m.ModuleCourses.Any(link => link.CourseId == cid))
                {
                    m.ModuleCourses.Add(new ModuleCourse
                    {
                        ModuleId = m.Id,
                        CourseId = cid
                    });
                }
            }

            await db.SaveChangesAsync();

            return Ok(m.Id);
        }
        else
        {
            
            m = new Module
            {
                Code = dto.Code,
                Title = dto.Title,
                CourseId = dto.CourseId,
                Credits = dto.Credits
            };
            db.Modules.Add(m);
            await db.SaveChangesAsync();

            var additionalCourseIds = dto.CloneCourseIds?
                .Where(id => id > 0 && id != dto.CourseId)
                .Distinct()
                .ToList() ?? new List<int>();

            var allCourseIds = new HashSet<int>(additionalCourseIds) { dto.CourseId };
            foreach (var cid in allCourseIds)
            {
                db.ModuleCourses.Add(new ModuleCourse
                {
                    ModuleId = m.Id,
                    CourseId = cid
                });
            }

            await db.SaveChangesAsync();

            
            foreach (var rid in dto.AllowedRoomIds.Distinct())
                db.ModuleRooms.Add(new ModuleRoom { ModuleId = m.Id, RoomId = rid });
            foreach (var bid in dto.AllowedBuildingIds.Distinct())
                db.ModuleBuildings.Add(new ModuleBuilding { ModuleId = m.Id, BuildingId = bid });

            await db.SaveChangesAsync();

            return Ok(m.Id);
        }
    }

    
    
    
    
    [HttpDelete("{id:int}")]
    [RequireDeletionConfirmation("модуль")]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool force = false)
    {
        var module = await db.Modules.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
        if (module is null) return NotFound();

        
        var used = await db.ScheduleItems.AnyAsync(x => x.ModuleId == id);
        if (used && !force)
            return Conflict(new { message = "Модуль використовується у розкладі" });

        if (force)
        {
            
            var q = db.ScheduleItems.Where(x => x.ModuleId == id);

            var affectedLoads = await q.Where(x => x.TeacherId != null)
                .Select(x => new { x.TeacherId, CourseId = x.Group.CourseId })
                .Distinct()
                .ToListAsync();

            
            await q.ExecuteDeleteAsync();

            
            await db.ModulePlans.Where(p => p.ModuleId == id).ExecuteDeleteAsync();
            await db.ModuleRooms.Where(x => x.ModuleId == id).ExecuteDeleteAsync();
            await db.ModuleBuildings.Where(x => x.ModuleId == id).ExecuteDeleteAsync();

            
            if (affectedLoads.Count > 0)
            {
                var tIds = affectedLoads.Select(a => a.TeacherId!.Value).Distinct().ToList();
                var cIds = affectedLoads.Select(a => a.CourseId).Distinct().ToList();

                
                var excludeLoadIds = await db.LessonTypes
                    .Where(lt => !lt.CountInLoad)
                    .Select(lt => lt.Id)
                    .ToListAsync();

                
                var counts = await db.ScheduleItems
                    .Include(si => si.Group)
                    .Where(si => si.TeacherId != null
                                 && !excludeLoadIds.Contains(si.LessonTypeId)
                                 && tIds.Contains(si.TeacherId!.Value)
                                 && cIds.Contains(si.Group.CourseId))
                    .GroupBy(si => new { TeacherId = si.TeacherId!.Value, si.Group.CourseId })
                    .Select(g => new { g.Key.TeacherId, g.Key.CourseId, C = g.Count() })
                    .ToListAsync();

                var loadsToUpdate = await db.TeacherCourseLoads
                    .Where(l => tIds.Contains(l.TeacherId) && cIds.Contains(l.CourseId))
                    .ToListAsync();

                foreach (var l in loadsToUpdate)
                    l.ScheduledHours = counts.FirstOrDefault(c => c.TeacherId == l.TeacherId && c.CourseId == l.CourseId)?.C ?? 0;

                await db.SaveChangesAsync();
            }
        }
        else
        {
            
            await db.ModuleRooms.Where(x => x.ModuleId == id).ExecuteDeleteAsync();
            await db.ModuleBuildings.Where(x => x.ModuleId == id).ExecuteDeleteAsync();
        }

        
        var rows = await db.Modules.Where(x => x.Id == id).ExecuteDeleteAsync();
        if (rows == 0) return NotFound();

        return NoContent();
    }

    
    
    
    
    
    
    [HttpGet("{moduleId:int}/topics")]
    public async Task<ActionResult<List<ModuleTopicViewDto>>> GetTopics(int moduleId)
    {
        var module = await db.Modules.AsNoTracking().FirstOrDefaultAsync(m => m.Id == moduleId);
        if (module is null) return NotFound();

        

        var topics = await db.ModuleTopics
            .Where(t => t.ModuleId == moduleId)
            .Include(t => t.LessonType)
            .ToListAsync();

        topics.Sort((a, b) => CompareTopicCodes(a.TopicCode, b.TopicCode));

        var topicIds = topics.Select(t => t.Id).ToList();
        var plannedDict = new Dictionary<int, List<string>>();
        var completedDict = new Dictionary<int, List<string>>();
        var plannedHoursDict = new Dictionary<int, Dictionary<string, TopicGroupHoursDto>>();
        var completedHoursDict = new Dictionary<int, Dictionary<string, TopicGroupHoursDto>>();

        if (topicIds.Count > 0)
        {
            var excludeCompletedCodes = new[] { "CANCELED", "RESCHEDULED" };
            var excludePlannedCodes = new[] { "CANCELED" };

            var draftRows = await db.TeacherDraftItems
                .Include(di => di.LessonType)
                .Include(di => di.Group)
                .Where(di => di.Status == DraftStatus.Draft
                             && ((di.ModuleTopicId != null && topicIds.Contains(di.ModuleTopicId.Value))
                                 || (di.BatchKey != null && EF.Functions.Like(di.BatchKey, "rescheduled%"))))
                .Select(di => new
                {
                    di.Id,
                    di.ModuleTopicId,
                    di.BatchKey,
                    di.IsSelfStudy,
                    LessonTypeCode = di.LessonType != null ? (di.LessonType.Code ?? "") : "",
                    GroupName = di.Group != null ? di.Group.Name : null
                })
                .ToListAsync();

            var reschedSourceIds = draftRows
                .Select(r => TeacherDraftsController.ParseRescheduleBatchKey(r.BatchKey))
                .Where(info => info.isRescheduled && info.sourceItemId is int)
                .Select(info => info.sourceItemId!.Value)
                .Distinct()
                .ToList();

            var reschedSourceTopics = reschedSourceIds.Count == 0
                ? new Dictionary<int, int?>()
                : await db.ScheduleItems
                    .Where(si => reschedSourceIds.Contains(si.Id))
                    .Select(si => new { si.Id, si.ModuleTopicId })
                    .ToDictionaryAsync(x => x.Id, x => x.ModuleTopicId);

            foreach (var row in draftRows)
            {
                if (string.IsNullOrWhiteSpace(row.GroupName)) continue;

                var codeUpper = row.LessonTypeCode.ToUpperInvariant();
                if (excludePlannedCodes.Contains(codeUpper)) continue;

                int? resolvedTopicId = row.ModuleTopicId;
                if (resolvedTopicId is null)
                {
                    var info = TeacherDraftsController.ParseRescheduleBatchKey(row.BatchKey);
                    if (info.isRescheduled && info.sourceItemId is int sid && reschedSourceTopics.TryGetValue(sid, out var topicIdFromSource))
                    {
                        resolvedTopicId = topicIdFromSource;
                    }
                }

                if (resolvedTopicId is null) continue;
                if (!topicIds.Contains(resolvedTopicId.Value)) continue;

                if (!plannedDict.TryGetValue(resolvedTopicId.Value, out var groups))
                {
                    groups = new List<string>();
                    plannedDict[resolvedTopicId.Value] = groups;
                }

                if (!groups.Contains(row.GroupName))
                {
                    groups.Add(row.GroupName);
                }

                if (!plannedHoursDict.TryGetValue(resolvedTopicId.Value, out var hoursByGroup))
                {
                    hoursByGroup = new Dictionary<string, TopicGroupHoursDto>(StringComparer.CurrentCultureIgnoreCase);
                    plannedHoursDict[resolvedTopicId.Value] = hoursByGroup;
                }

                if (!hoursByGroup.TryGetValue(row.GroupName, out var stat))
                {
                    stat = new TopicGroupHoursDto(row.GroupName, 0, 0);
                }
                var aud = stat.AuditoriumHours + (row.IsSelfStudy ? 0 : 1);
                var self = stat.SelfStudyHours + (row.IsSelfStudy ? 1 : 0);
                hoursByGroup[row.GroupName] = new TopicGroupHoursDto(row.GroupName, aud, self);
            }

            foreach (var kvp in plannedDict.ToList())
            {
                plannedDict[kvp.Key] = kvp.Value.OrderBy(x => x).ToList();
            }

            var completedRows = await db.ScheduleItems
                .Where(si => si.ModuleTopicId != null && topicIds.Contains(si.ModuleTopicId!.Value))
                .Include(si => si.LessonType)
                .Where(si =>
                    si.LessonType != null
                    && !excludeCompletedCodes.Contains((si.LessonType.Code ?? "").ToUpper()))
                .Select(si => new { TopicId = si.ModuleTopicId!.Value, GroupName = si.Group.Name, si.IsSelfStudy })
                .ToListAsync();

            completedDict = completedRows
                .DistinctBy(x => new { x.TopicId, x.GroupName })
                .GroupBy(x => x.TopicId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.GroupName).OrderBy(x => x).ToList());

            foreach (var row in completedRows)
            {
                if (string.IsNullOrWhiteSpace(row.GroupName)) continue;
                if (!completedHoursDict.TryGetValue(row.TopicId, out var hoursByGroup))
                {
                    hoursByGroup = new Dictionary<string, TopicGroupHoursDto>(StringComparer.CurrentCultureIgnoreCase);
                    completedHoursDict[row.TopicId] = hoursByGroup;
                }

                if (!hoursByGroup.TryGetValue(row.GroupName, out var stat))
                {
                    stat = new TopicGroupHoursDto(row.GroupName, 0, 0);
                }
                var aud = stat.AuditoriumHours + (row.IsSelfStudy ? 0 : 1);
                var self = stat.SelfStudyHours + (row.IsSelfStudy ? 1 : 0);
                hoursByGroup[row.GroupName] = new TopicGroupHoursDto(row.GroupName, aud, self);
            }
        }

        var result = topics.Select(t =>
        {
            var planned = plannedDict.TryGetValue(t.Id, out var pg) ? new List<string>(pg) : new List<string>();
            var completed = completedDict.TryGetValue(t.Id, out var cg) ? new List<string>(cg) : new List<string>();
            var plannedHours = plannedHoursDict.TryGetValue(t.Id, out var ph)
                ? ph.Values.OrderBy(x => x.GroupName, StringComparer.CurrentCultureIgnoreCase).ToList()
                : new List<TopicGroupHoursDto>();
            var completedHours = completedHoursDict.TryGetValue(t.Id, out var ch)
                ? ch.Values.OrderBy(x => x.GroupName, StringComparer.CurrentCultureIgnoreCase).ToList()
                : new List<TopicGroupHoursDto>();
            var lessonTypeCode = t.LessonType?.Code ?? string.Empty;
            var lessonTypeName = t.LessonType?.Name ?? string.Empty;
            return new ModuleTopicViewDto(
                t.Id,
                t.ModuleId,
                t.Order,
                t.TopicCode,
                t.LessonTypeId,
                lessonTypeCode,
                lessonTypeName,
                t.TotalHours,
                t.AuditoriumHours,
                t.SelfStudyHours,
                planned,
                completed,
                t.IsInterAssembly,
                t.SelfStudyBySupervisor,
                plannedHours,
                completedHours
            );
        }).ToList();

        return Ok(result);
    }

    [HttpPost("{moduleId:int}/topics/upsert")]
    public async Task<ActionResult<int>> UpsertTopic(int moduleId, [FromBody] ModuleTopicDto dto)
    {
        var moduleExists = await db.Modules.AnyAsync(m => m.Id == moduleId);
        if (!moduleExists) return NotFound();

        

        var lessonTypeExists = await db.LessonTypes.AnyAsync(lt => lt.Id == dto.LessonTypeId);
        if (!lessonTypeExists) return BadRequest("Lesson type not found");

        var topicsQuery = db.ModuleTopics.Where(t => t.ModuleId == moduleId);
        var trimmedTopicCode = dto.TopicCode?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(trimmedTopicCode))
            return BadRequest("Topic code is required");

        var normalizedTopicCode = trimmedTopicCode;
        var topicId = dto.Id ?? 0;

        var duplicateExists = await topicsQuery
            .AnyAsync(t => t.Id != topicId && t.TopicCode == normalizedTopicCode);
        if (duplicateExists)
            return BadRequest("Topic code already exists");

        var entity = topicId > 0
            ? await topicsQuery.SingleOrDefaultAsync(t => t.Id == topicId)
            : null;

        if (entity is null)
        {
            if (topicId > 0) return NotFound();

            entity = new ModuleTopic
            {
                ModuleId = moduleId
            };
            db.ModuleTopics.Add(entity);
        }

        var desiredOrder = dto.Order > 0
            ? dto.Order
            : topicId > 0
                ? entity.Order
                : (await topicsQuery.MaxAsync(t => (int?)t.Order) ?? 0) + 1;

        entity.Order = desiredOrder;
        entity.TopicCode = normalizedTopicCode;
        entity.LessonTypeId = dto.LessonTypeId;

        var safeAuditorium = Math.Max(0, dto.AuditoriumHours);
        var safeSelfStudy = Math.Max(0, dto.SelfStudyHours);
        var totalHours = Math.Max(0, safeAuditorium + safeSelfStudy);
        entity.TotalHours = totalHours;
        entity.AuditoriumHours = safeAuditorium;
        entity.SelfStudyHours = safeSelfStudy;
        entity.IsInterAssembly = dto.IsInterAssembly;
        entity.SelfStudyBySupervisor = dto.SelfStudyBySupervisor;

        if (entity.AuditoriumHours + entity.SelfStudyHours > entity.TotalHours)
            return BadRequest("Hourly totals exceed overall value");

        await db.SaveChangesAsync();
        await RecalculateModuleTopicOrder(moduleId);
        return Ok(entity.Id);
    }

    [HttpDelete("{moduleId:int}/topics/{topicId:int}")]
    [RequireDeletionConfirmation("тему модуля")]
    public async Task<IActionResult> DeleteTopic(int moduleId, int topicId)
    {
        var topic = await db.ModuleTopics.FirstOrDefaultAsync(t => t.Id == topicId && t.ModuleId == moduleId);
        if (topic is null) return NotFound();
        

        var hasDrafts = await db.TeacherDraftItems.AnyAsync(di => di.ModuleTopicId == topicId);
        var hasSchedule = await db.ScheduleItems.AnyAsync(si => si.ModuleTopicId == topicId);
        if (hasDrafts || hasSchedule)
            return Conflict("Topic already used in schedule");

        db.ModuleTopics.Remove(topic);
        await db.SaveChangesAsync();
        await RecalculateModuleTopicOrder(moduleId);
        return NoContent();
    }

    [HttpPost("import-docx")]
    public async Task<ActionResult<DocxImportResultDto>> ImportDocx([FromForm] IFormFile file, [FromQuery] bool apply = false, CancellationToken ct = default)
    {
        var service = new DocxImportService();
        var result = await service.ImportAsync(file, db, apply, ct);

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            return BadRequest(new { message = result.Error, warnings = result.Warnings });
        }

        return Ok(result);
    }

    [HttpPost("clear-all")]
    public async Task<IActionResult> ClearAll()
    {
        // Обережно: тотальне очищення модулів і пов'язаних планів.
        await db.ModuleTopics.ExecuteDeleteAsync();
        await db.ModulePlans.ExecuteDeleteAsync();
        await db.ModuleSequenceItems.ExecuteDeleteAsync();
        await db.ModuleFillers.ExecuteDeleteAsync();
        await db.ModuleRooms.ExecuteDeleteAsync();
        await db.ModuleBuildings.ExecuteDeleteAsync();
        await db.ModuleCourses.ExecuteDeleteAsync();
        await db.TeacherModules.ExecuteDeleteAsync();
        await db.ModuleSupervisors.ExecuteDeleteAsync();
        await db.Modules.ExecuteDeleteAsync();
        return NoContent();
    }

    private static int CompareTopicCodes(string? left, string? right)
    {
        var leftParts = ParseTopicCodeSegments(left);
        var rightParts = ParseTopicCodeSegments(right);
        var maxLength = Math.Max(leftParts.Count, rightParts.Count);

        for (var i = 0; i < maxLength; i++)
        {
            var leftValue = i < leftParts.Count ? leftParts[i] : 0;
            var rightValue = i < rightParts.Count ? rightParts[i] : 0;
            var diff = leftValue.CompareTo(rightValue);
            if (diff != 0)
            {
                return diff;
            }
        }

        return string.Compare(left ?? string.Empty, right ?? string.Empty, System.StringComparison.Ordinal);
    }

    private static IReadOnlyList<int> ParseTopicCodeSegments(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return System.Array.Empty<int>();
        }

        return code.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var value) ? value : int.MaxValue)
            .ToArray();
    }

    private async Task RecalculateModuleTopicOrder(int moduleId)
    {
        var topics = await db.ModuleTopics
            .Where(t => t.ModuleId == moduleId)
            .ToListAsync();

        if (topics.Count == 0)
        {
            return;
        }

        topics.Sort((a, b) => CompareTopicCodes(a.TopicCode, b.TopicCode));

        var needsUpdate = false;
        for (var i = 0; i < topics.Count; i++)
        {
            if (topics[i].Order != i + 1)
            {
                needsUpdate = true;
                break;
            }
        }

        if (!needsUpdate)
        {
            return;
        }

        for (var i = 0; i < topics.Count; i++)
        {
            topics[i].Order = 1000 + i;
        }

        await db.SaveChangesAsync();

        for (var i = 0; i < topics.Count; i++)
        {
            topics[i].Order = i + 1;
        }

        await db.SaveChangesAsync();
    }


}
