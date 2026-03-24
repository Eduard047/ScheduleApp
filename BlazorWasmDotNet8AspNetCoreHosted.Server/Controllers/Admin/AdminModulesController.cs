using System.Linq;

using System.Collections.Generic;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using Microsoft.AspNetCore.Http;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;

// Контролер адміністратора для роботи з модулями
[ApiController]
[Route("api/admin/modules")]
public class AdminModulesController(AppDbContext db) : ControllerBase
{

    [HttpGet]
    // Повертає список модулів із дозволеними аудиторіями та корпусами.
    public async Task<object> List()
    {
        await NormalizeSharedModulesAsync();
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
                CloneCourseIds = new List<int>()
            })
            .ToListAsync();
        return modules;
    }
    [HttpPost("upsert")]
    // Створює або оновлює модуль разом із зв'язками.
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
            if (dto.CourseId > 0 && dto.CourseId != m.CourseId)
            {
                var moduleIdForCourse = await EnsureCourseScopeCore(m.Id, dto.CourseId, requireCourseLink: false);
                m = await db.Modules
                    .Include(x => x.AllowedRooms)
                    .Include(x => x.AllowedBuildings)
                    .Include(x => x.ModuleCourses)
                    .FirstOrDefaultAsync(x => x.Id == moduleIdForCourse)
                    ?? throw new ArgumentException("Модуль не знайдено");
            }
            var extraCourseIds = m.ModuleCourses
                .Select(link => link.CourseId)
                .Where(cid => cid != dto.CourseId)
                .Distinct()
                .ToList();
            foreach (var extraCourseId in extraCourseIds)
            {
                await EnsureCourseScopeCore(m.Id, extraCourseId, requireCourseLink: true);
            }
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
            var linksToRemove = await db.ModuleCourses
                .Where(link => link.ModuleId == m.Id && link.CourseId != dto.CourseId)
                .ToListAsync();
            if (linksToRemove.Count > 0)
            {
                db.ModuleCourses.RemoveRange(linksToRemove);
            }
            var hasCurrentLink = await db.ModuleCourses
                .AnyAsync(link => link.ModuleId == m.Id && link.CourseId == dto.CourseId);
            if (!hasCurrentLink)
            {
                db.ModuleCourses.Add(new ModuleCourse
                {
                    ModuleId = m.Id,
                    CourseId = dto.CourseId
                });
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
            db.ModuleCourses.Add(new ModuleCourse
            {
                ModuleId = m.Id,
                CourseId = dto.CourseId
            });
            await db.SaveChangesAsync();
            foreach (var rid in dto.AllowedRoomIds.Distinct())
                db.ModuleRooms.Add(new ModuleRoom { ModuleId = m.Id, RoomId = rid });
            foreach (var bid in dto.AllowedBuildingIds.Distinct())
                db.ModuleBuildings.Add(new ModuleBuilding { ModuleId = m.Id, BuildingId = bid });
            await db.SaveChangesAsync();
            return Ok(m.Id);
        }
    }
    [HttpPost("{moduleId:int}/ensure-course-scope")]
    // Забезпечує окремий екземпляр модуля для вибраного курсу.
    public async Task<ActionResult<int>> EnsureCourseScope(int moduleId, [FromQuery] int courseId)
    {
        if (courseId <= 0)
            return BadRequest(new { message = "Потрібно вказати курс." });
        var courseExists = await db.Courses.AnyAsync(c => c.Id == courseId);
        if (!courseExists)
            return NotFound(new { message = "Курс не знайдено." });
        try
        {
            var scopedModuleId = await EnsureCourseScopeCore(moduleId, courseId, requireCourseLink: false);
            return Ok(scopedModuleId);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
    [HttpDelete("{id:int}")]
    [RequireDeletionConfirmation("модуль")]
    // Видаляє модуль, за потреби з примусовим очищенням.
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
    // Повертає теми модуля разом із статистикою планування.
    public async Task<ActionResult<List<ModuleTopicViewDto>>> GetTopics(int moduleId)
    {
        var module = await db.Modules.AsNoTracking().FirstOrDefaultAsync(m => m.Id == moduleId);
        if (module is null) return NotFound();
        var topics = await db.ModuleTopics
            .Where(t => t.ModuleId == moduleId)
            .Include(t => t.LessonType)
            .Include(t => t.Department)
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
                .Select(r => TeacherDraftsHelpers.ParseRescheduleBatchKey(r.BatchKey))
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
                    var info = TeacherDraftsHelpers.ParseRescheduleBatchKey(row.BatchKey);
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
                completedHours,
                DepartmentId: t.DepartmentId,
                DepartmentName: t.Department?.Name
            );
        }).ToList();
        return Ok(result);
    }
    [HttpPost("{moduleId:int}/topics/upsert")]
    // Створює або оновлює тему модуля.
    public async Task<ActionResult<int>> UpsertTopic(int moduleId, [FromBody] ModuleTopicDto dto)
    {
        var moduleExists = await db.Modules.AnyAsync(m => m.Id == moduleId);
        if (!moduleExists) return NotFound();
        var lessonTypeExists = await db.LessonTypes.AnyAsync(lt => lt.Id == dto.LessonTypeId);
        if (!lessonTypeExists) return BadRequest("Lesson type not found");
        var normalizedDepartmentId = dto.DepartmentId is int depId && depId > 0 ? depId : (int?)null;
        if (normalizedDepartmentId is int departmentId)
        {
            var departmentExists = await db.Departments.AnyAsync(x => x.Id == departmentId);
            if (!departmentExists) return BadRequest("Department not found");
        }
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
        entity.DepartmentId = normalizedDepartmentId;
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
    // Видаляє тему модуля після перевірки використання.
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
    // Імпортує модулі та теми з DOCX.
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
    // Повністю очищає модулі та пов'язані таблиці.
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
    // Нормалізує застарілі спільні модулі, розділяючи їх на окремі записи за курсами.
    private async Task NormalizeSharedModulesAsync()
    {
        // Уникаємо вкладених колекцій у LINQ-проєкції, бо Pomelo/MySQL може не транслювати OUTER APPLY.
        var sharedLinks = await db.ModuleCourses
            .AsNoTracking()
            .Join(
                db.Modules.AsNoTracking(),
                link => link.ModuleId,
                module => module.Id,
                (link, module) => new
                {
                    ModuleId = module.Id,
                    PrimaryCourseId = module.CourseId,
                    LinkedCourseId = link.CourseId
                })
            .Where(x => x.LinkedCourseId != x.PrimaryCourseId)
            .Select(x => new { x.ModuleId, ExtraCourseId = x.LinkedCourseId })
            .Distinct()
            .ToListAsync();
        var sharedRows = sharedLinks
            .GroupBy(x => x.ModuleId)
            .Select(g => new
            {
                ModuleId = g.Key,
                ExtraCourseIds = g.Select(x => x.ExtraCourseId).Distinct().ToList()
            })
            .ToList();
        foreach (var row in sharedRows)
        {
            foreach (var extraCourseId in row.ExtraCourseIds)
            {
                try
                {
                    await EnsureCourseScopeCore(row.ModuleId, extraCourseId, requireCourseLink: true);
                }
                catch
                {
                    // Пропускаємо збій нормалізації окремого модуля, щоб не блокувати запит списку.
                }
            }
        }
    }
    // Забезпечує окремий екземпляр модуля для курсу з переносом курсо-залежних даних.
    private async Task<int> EnsureCourseScopeCore(int moduleId, int courseId, bool requireCourseLink)
    {
        var source = await db.Modules
            .Include(m => m.ModuleCourses)
            .Include(m => m.AllowedRooms)
            .Include(m => m.AllowedBuildings)
            .FirstOrDefaultAsync(m => m.Id == moduleId)
            ?? throw new ArgumentException("Модуль не знайдено");
        var linkedCourseIds = source.ModuleCourses
            .Select(mc => mc.CourseId)
            .ToHashSet();
        linkedCourseIds.Add(source.CourseId);
        if (requireCourseLink && !linkedCourseIds.Contains(courseId))
            throw new ArgumentException("Модуль не прив'язаний до курсу");
        if (source.CourseId == courseId)
        {
            if (!source.ModuleCourses.Any(mc => mc.CourseId == courseId))
            {
                db.ModuleCourses.Add(new ModuleCourse { ModuleId = source.Id, CourseId = courseId });
                await db.SaveChangesAsync();
            }
            return source.Id;
        }
        await using var tx = await db.Database.BeginTransactionAsync();
        var targetCandidates = await db.Modules
            .Include(m => m.ModuleCourses)
            .Where(m => m.CourseId == courseId && m.Code == source.Code)
            .OrderBy(m => m.Id)
            .ToListAsync();
        var target = targetCandidates.FirstOrDefault();
        if (target is null)
        {
            target = new Module
            {
                Code = source.Code,
                Title = source.Title,
                Credits = source.Credits,
                CourseId = courseId
            };
            db.Modules.Add(target);
            await db.SaveChangesAsync();
            db.ModuleCourses.Add(new ModuleCourse { ModuleId = target.Id, CourseId = courseId });
            var roomIds = source.AllowedRooms
                .Select(x => x.RoomId)
                .Distinct()
                .ToList();
            foreach (var roomId in roomIds)
            {
                db.ModuleRooms.Add(new ModuleRoom { ModuleId = target.Id, RoomId = roomId });
            }
            var buildingIds = source.AllowedBuildings
                .Select(x => x.BuildingId)
                .Distinct()
                .ToList();
            foreach (var buildingId in buildingIds)
            {
                db.ModuleBuildings.Add(new ModuleBuilding { ModuleId = target.Id, BuildingId = buildingId });
            }
            var sourceTopics = await db.ModuleTopics
                .Where(t => t.ModuleId == source.Id)
                .OrderBy(t => t.Order)
                .ThenBy(t => t.Id)
                .ToListAsync();
            foreach (var topic in sourceTopics)
            {
                db.ModuleTopics.Add(new ModuleTopic
                {
                    ModuleId = target.Id,
                    Order = topic.Order,
                    TopicCode = topic.TopicCode,
                    LessonTypeId = topic.LessonTypeId,
                    DepartmentId = topic.DepartmentId,
                    TotalHours = topic.TotalHours,
                    AuditoriumHours = topic.AuditoriumHours,
                    SelfStudyHours = topic.SelfStudyHours,
                    IsInterAssembly = topic.IsInterAssembly,
                    SelfStudyBySupervisor = topic.SelfStudyBySupervisor
                });
            }
            await db.SaveChangesAsync();
        }
        else if (!target.ModuleCourses.Any(mc => mc.CourseId == courseId))
        {
            db.ModuleCourses.Add(new ModuleCourse { ModuleId = target.Id, CourseId = courseId });
            await db.SaveChangesAsync();
        }
        await MoveCourseScopedData(source.Id, target.Id, courseId);
        var detachedLink = source.ModuleCourses.FirstOrDefault(mc => mc.CourseId == courseId);
        if (detachedLink is not null)
        {
            db.ModuleCourses.Remove(detachedLink);
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return target.Id;
    }
    // Переносить курсо-залежні дані зі спільного модуля в окремий модуль курсу.
    private async Task MoveCourseScopedData(int sourceModuleId, int targetModuleId, int courseId)
    {
        var sourcePlan = await db.ModulePlans
            .FirstOrDefaultAsync(p => p.ModuleId == sourceModuleId && p.CourseId == courseId);
        if (sourcePlan is not null)
        {
            var targetPlan = await db.ModulePlans
                .FirstOrDefaultAsync(p => p.ModuleId == targetModuleId && p.CourseId == courseId);
            if (targetPlan is null)
            {
                sourcePlan.ModuleId = targetModuleId;
            }
            else
            {
                targetPlan.TargetHours = sourcePlan.TargetHours;
                targetPlan.ScheduledHours = sourcePlan.ScheduledHours;
                targetPlan.IsActive = sourcePlan.IsActive;
                db.ModulePlans.Remove(sourcePlan);
            }
        }
        var sourceSequenceRows = await db.ModuleSequenceItems
            .Where(si => si.ModuleId == sourceModuleId && si.CourseId == courseId)
            .ToListAsync();
        if (sourceSequenceRows.Count > 0)
        {
            var targetSequenceExists = await db.ModuleSequenceItems
                .AnyAsync(si => si.ModuleId == targetModuleId && si.CourseId == courseId);
            if (targetSequenceExists)
            {
                db.ModuleSequenceItems.RemoveRange(sourceSequenceRows);
            }
            else
            {
                foreach (var row in sourceSequenceRows)
                {
                    row.ModuleId = targetModuleId;
                }
            }
        }
        var sourceFillerRows = await db.ModuleFillers
            .Where(f => f.ModuleId == sourceModuleId && f.CourseId == courseId)
            .ToListAsync();
        if (sourceFillerRows.Count > 0)
        {
            var targetFillerExists = await db.ModuleFillers
                .AnyAsync(f => f.ModuleId == targetModuleId && f.CourseId == courseId);
            if (targetFillerExists)
            {
                db.ModuleFillers.RemoveRange(sourceFillerRows);
            }
            else
            {
                foreach (var row in sourceFillerRows)
                {
                    row.ModuleId = targetModuleId;
                }
            }
        }
        var sourceTopics = await db.ModuleTopics
            .Where(t => t.ModuleId == sourceModuleId)
            .Select(t => new { t.Id, t.TopicCode })
            .ToListAsync();
        var targetTopics = await db.ModuleTopics
            .Where(t => t.ModuleId == targetModuleId)
            .OrderBy(t => t.Id)
            .ToListAsync();
        var targetByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var topic in targetTopics)
        {
            if (!targetByCode.ContainsKey(topic.TopicCode))
            {
                targetByCode[topic.TopicCode] = topic.Id;
            }
        }
        var topicIdMap = new Dictionary<int, int?>();
        foreach (var topic in sourceTopics)
        {
            topicIdMap[topic.Id] = targetByCode.TryGetValue(topic.TopicCode, out var targetTopicId)
                ? targetTopicId
                : null;
        }
        var scheduleRows = await db.ScheduleItems
            .Include(si => si.Group)
            .Where(si => si.ModuleId == sourceModuleId && si.Group.CourseId == courseId)
            .ToListAsync();
        foreach (var row in scheduleRows)
        {
            row.ModuleId = targetModuleId;
            if (row.ModuleTopicId is int topicId && topicIdMap.TryGetValue(topicId, out var mappedTopicId))
            {
                row.ModuleTopicId = mappedTopicId;
            }
            else if (row.ModuleTopicId is not null)
            {
                row.ModuleTopicId = null;
            }
        }
        var draftRows = await db.TeacherDraftItems
            .Include(di => di.Group)
            .Where(di => di.ModuleId == sourceModuleId && di.Group.CourseId == courseId)
            .ToListAsync();
        foreach (var row in draftRows)
        {
            row.ModuleId = targetModuleId;
            if (row.ModuleTopicId is int topicId && topicIdMap.TryGetValue(topicId, out var mappedTopicId))
            {
                row.ModuleTopicId = mappedTopicId;
            }
            else if (row.ModuleTopicId is not null)
            {
                row.ModuleTopicId = null;
            }
        }
    }
    // Порівнює коди тем за числовими сегментами.
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
    // Розбиває код теми на числові сегменти.
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
    // Перераховує порядок тем модуля за кодом.
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
