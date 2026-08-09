using System.Linq;
using System.Data;

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
        var modules = await db.Modules
            .AsNoTracking()
            .AsSplitQuery()
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
        var code = dto.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        var title = dto.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(title))
            return BadRequest(new { message = "Код і назва модуля є обов'язковими." });
        if (code.Length > 64)
            return BadRequest(new { message = "Код модуля не може перевищувати 64 символи." });
        if (dto.CourseId <= 0)
            return BadRequest(new { message = "Потрібно вибрати коректний курс." });
        if (dto.Credits is < 0 or > 9999.99m)
            return BadRequest(new { message = "Кількість кредитів має бути від 0 до 9999,99." });
        var requestedRoomIds = (dto.AllowedRoomIds ?? new List<int>()).Distinct().ToList();
        var requestedBuildingIds = (dto.AllowedBuildingIds ?? new List<int>()).Distinct().ToList();
        if (requestedRoomIds.Count > 500 || requestedBuildingIds.Count > 500)
            return BadRequest(new { message = "Для одного модуля можна вибрати не більше 500 аудиторій або корпусів." });
        if (requestedRoomIds.Any(id => id <= 0) || requestedBuildingIds.Any(id => id <= 0))
            return BadRequest(new { message = "Ідентифікатори аудиторій і корпусів мають бути додатними числами." });

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (!await db.Courses.AnyAsync(course => course.Id == dto.CourseId))
                return NotFound(new { message = "Курс не знайдено." });
            var existingRoomIds = await db.Rooms
                .Where(room => requestedRoomIds.Contains(room.Id))
                .Select(room => room.Id)
                .ToListAsync();
            var missingRoomIds = requestedRoomIds.Except(existingRoomIds).OrderBy(id => id).ToList();
            if (missingRoomIds.Count > 0)
                return BadRequest(new { message = $"Аудиторії не знайдено: {string.Join(", ", missingRoomIds)}." });
            var existingBuildingIds = await db.Buildings
                .Where(building => requestedBuildingIds.Contains(building.Id))
                .Select(building => building.Id)
                .ToListAsync();
            var missingBuildingIds = requestedBuildingIds.Except(existingBuildingIds).OrderBy(id => id).ToList();
            if (missingBuildingIds.Count > 0)
                return BadRequest(new { message = $"Корпуси не знайдено: {string.Join(", ", missingBuildingIds)}." });

            Module m;
            if (dto.Id is int id && id > 0)
            {
                var existingModule = await db.Modules
                    .AsSplitQuery()
                    .Include(x => x.AllowedRooms)
                    .Include(x => x.AllowedBuildings)
                    .Include(x => x.ModuleCourses)
                    .FirstOrDefaultAsync(x => x.Id == id);
                if (existingModule is null) return NotFound(new { message = "Модуль не знайдено." });
                m = existingModule;
                if (dto.CourseId != m.CourseId)
                {
                    var moduleIdForCourse = await EnsureCourseScopeCore(m.Id, dto.CourseId, requireCourseLink: false);
                    var courseScopedModule = await db.Modules
                        .AsSplitQuery()
                        .Include(x => x.AllowedRooms)
                        .Include(x => x.AllowedBuildings)
                        .Include(x => x.ModuleCourses)
                        .FirstOrDefaultAsync(x => x.Id == moduleIdForCourse);
                    if (courseScopedModule is null)
                    {
                        await tx.RollbackAsync(CancellationToken.None);
                        db.ChangeTracker.Clear();
                        return Conflict(new { message = "Не вдалося підготувати модуль для вибраного курсу." });
                    }
                    m = courseScopedModule;
                }
                var duplicateCodeExists = await HasDuplicateModuleCodeAsync(dto.CourseId, m.Id, code);
                if (duplicateCodeExists)
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    db.ChangeTracker.Clear();
                    return Conflict(new
                    {
                        message = $"Модуль із кодом '{code}' вже існує для вибраного курсу."
                    });
                }
                var oldRoomIds = m.AllowedRooms.Select(x => x.RoomId).ToHashSet();
                var newRoomIds = requestedRoomIds.ToHashSet();
                var oldBuildingIds = m.AllowedBuildings.Select(x => x.BuildingId).ToHashSet();
                var newBuildingIds = requestedBuildingIds.ToHashSet();
                var roomRestrictionsChanged = !oldRoomIds.SetEquals(newRoomIds)
                                              || !oldBuildingIds.SetEquals(newBuildingIds);
                if (roomRestrictionsChanged
                    && await HasPlacementOutsideRoomRestrictionsAsync(
                        m.Id,
                        requestedRoomIds,
                        requestedBuildingIds))
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    db.ChangeTracker.Clear();
                    return Conflict(new
                    {
                        message = "Неможливо змінити дозволені аудиторії або корпуси: наявне заняття, для якого потрібна аудиторія, використовує приміщення поза новими обмеженнями."
                    });
                }
                var extraCourseIds = m.ModuleCourses
                    .Select(link => link.CourseId)
                    .Where(courseId => courseId != dto.CourseId)
                    .Distinct()
                    .ToList();
                foreach (var extraCourseId in extraCourseIds)
                {
                    await EnsureCourseScopeCore(m.Id, extraCourseId, requireCourseLink: true);
                }
                m.Code = code;
                m.Title = title;
                m.CourseId = dto.CourseId;
                m.Credits = dto.Credits;
                db.ModuleRooms.RemoveRange(m.AllowedRooms.Where(x => !newRoomIds.Contains(x.RoomId)));
                foreach (var add in newRoomIds.Except(oldRoomIds))
                    db.ModuleRooms.Add(new ModuleRoom { ModuleId = m.Id, RoomId = add });
                db.ModuleBuildings.RemoveRange(m.AllowedBuildings.Where(x => !newBuildingIds.Contains(x.BuildingId)));
                foreach (var add in newBuildingIds.Except(oldBuildingIds))
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
            }
            else
            {
                var duplicateCodeExists = await HasDuplicateModuleCodeAsync(dto.CourseId, 0, code);
                if (duplicateCodeExists)
                {
                    return Conflict(new
                    {
                        message = $"Модуль із кодом '{code}' вже існує для вибраного курсу."
                    });
                }
                m = new Module
                {
                    Code = code,
                    Title = title,
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
                foreach (var roomId in requestedRoomIds)
                    db.ModuleRooms.Add(new ModuleRoom { ModuleId = m.Id, RoomId = roomId });
                foreach (var buildingId in requestedBuildingIds)
                    db.ModuleBuildings.Add(new ModuleBuilding { ModuleId = m.Id, BuildingId = buildingId });
                await db.SaveChangesAsync();
            }
            await tx.CommitAsync();
            return Ok(m.Id);
        }
        catch (ArgumentException exception)
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return BadRequest(new { message = exception.Message });
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
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
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var module = await db.Modules.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id);
            if (module is null) return NotFound();
            var hasDrafts = await db.TeacherDraftItems
                .AsNoTracking()
                .AnyAsync(x => x.ModuleId == id);
            if (hasDrafts)
            {
                return Conflict(new
                {
                    message = "Модуль використовується у чернетках. Спочатку перенесіть або видаліть пов'язані чернетки."
                });
            }
            var used = await db.ScheduleItems.AnyAsync(x => x.ModuleId == id);
            if (used && !force)
                return Conflict(new { message = "Модуль використовується у розкладі" });

            if (force)
            {
                var q = db.ScheduleItems.Where(x => x.ModuleId == id);
                var affectedPlans = await q
                    .Select(x => new { CourseId = x.Group.CourseId, x.ModuleId })
                    .Distinct()
                    .ToListAsync();
                var affectedLoads = await q.Where(x => x.TeacherId != null)
                    .Select(x => new { x.TeacherId, CourseId = x.Group.CourseId })
                    .Distinct()
                    .ToListAsync();
                await q.ExecuteDeleteAsync();
                await db.ModulePlans.Where(p => p.ModuleId == id).ExecuteDeleteAsync();
                await db.ModuleRooms.Where(x => x.ModuleId == id).ExecuteDeleteAsync();
                await db.ModuleBuildings.Where(x => x.ModuleId == id).ExecuteDeleteAsync();
                await new AggregatesService(db).RecalcAsync(
                    affectedPlans.Select(a => (a.CourseId, a.ModuleId)),
                    affectedLoads.Select(a => (a.TeacherId!.Value, a.CourseId)));
            }
            else
            {
                await db.ModuleRooms.Where(x => x.ModuleId == id).ExecuteDeleteAsync();
                await db.ModuleBuildings.Where(x => x.ModuleId == id).ExecuteDeleteAsync();
            }
            var rows = await db.Modules.Where(x => x.Id == id).ExecuteDeleteAsync();
            if (rows == 0)
            {
                await tx.RollbackAsync();
                return NotFound();
            }
            await tx.CommitAsync();
            return NoContent();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
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
                .Where(di => (di.ModuleTopicId != null && topicIds.Contains(di.ModuleTopicId.Value))
                             || (di.BatchKey != null && EF.Functions.Like(di.BatchKey, "rescheduled%")))
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
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var moduleExists = await db.Modules.AnyAsync(m => m.Id == moduleId);
        if (!moduleExists) return NotFound();
        var lessonTypeExists = await db.LessonTypes.AnyAsync(lt => lt.Id == dto.LessonTypeId);
        if (!lessonTypeExists)
            return BadRequest(new { message = "Тип заняття не знайдено." });
        var normalizedDepartmentId = dto.DepartmentId is int depId && depId > 0 ? depId : (int?)null;
        if (normalizedDepartmentId is int departmentId)
        {
            var departmentExists = await db.Departments.AnyAsync(x => x.Id == departmentId);
            if (!departmentExists)
                return BadRequest(new { message = "Кафедру не знайдено." });
        }
        var topicsQuery = db.ModuleTopics.Where(t => t.ModuleId == moduleId);
        var trimmedTopicCode = dto.TopicCode?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedTopicCode))
            return BadRequest(new { message = "Код теми є обов'язковим." });
        var normalizedTopicCode = trimmedTopicCode;
        if (dto.AuditoriumHours < 0 || dto.SelfStudyHours < 0)
        {
            return BadRequest(new { message = "Кількість годин теми не може бути від'ємною." });
        }
        var requestedTotalHours = (long)dto.AuditoriumHours + dto.SelfStudyHours;
        if (requestedTotalHours > int.MaxValue)
        {
            return BadRequest(new { message = "Сума годин теми перевищує підтримуване значення." });
        }
        var topicId = dto.Id ?? 0;
        var duplicateExists = await topicsQuery
            .AnyAsync(t => t.Id != topicId && t.TopicCode == normalizedTopicCode);
        if (duplicateExists)
            return BadRequest(new { message = "Тема з таким кодом уже існує в модулі." });
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
        else if (entity.LessonTypeId != dto.LessonTypeId
                 || !string.Equals(entity.TopicCode, normalizedTopicCode, StringComparison.Ordinal))
        {
            var topicIsUsed = await db.ScheduleItems.AsNoTracking()
                                  .AnyAsync(item => item.ModuleTopicId == entity.Id)
                              || await db.TeacherDraftItems.AsNoTracking()
                                  .AnyAsync(item => item.ModuleTopicId == entity.Id);
            if (topicIsUsed)
            {
                return Conflict(new
                {
                    message = "Неможливо змінити код або тип заняття: тема вже використовується в розкладі або чернетках."
                });
            }
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
        entity.TotalHours = (int)requestedTotalHours;
        entity.AuditoriumHours = dto.AuditoriumHours;
        entity.SelfStudyHours = dto.SelfStudyHours;
        entity.IsInterAssembly = dto.IsInterAssembly;
        entity.SelfStudyBySupervisor = dto.SelfStudyBySupervisor;
        if (entity.AuditoriumHours + entity.SelfStudyHours > entity.TotalHours)
            return BadRequest(new { message = "Сума аудиторних годин і самостійної роботи перевищує загальну кількість годин." });
        await db.SaveChangesAsync();
        await RecalculateModuleTopicOrder(moduleId);
        await tx.CommitAsync();
        return Ok(entity.Id);
    }
    [HttpDelete("{moduleId:int}/topics/{topicId:int}")]
    [RequireDeletionConfirmation("тему модуля")]
    // Видаляє тему модуля після перевірки використання.
    public async Task<IActionResult> DeleteTopic(int moduleId, int topicId)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var topic = await db.ModuleTopics.FirstOrDefaultAsync(t => t.Id == topicId && t.ModuleId == moduleId);
        if (topic is null) return NotFound();
        var hasDrafts = await db.TeacherDraftItems.AnyAsync(di => di.ModuleTopicId == topicId);
        var hasSchedule = await db.ScheduleItems.AnyAsync(si => si.ModuleTopicId == topicId);
        if (hasDrafts || hasSchedule)
            return Conflict(new { message = "Тема вже використовується в розкладі або чернетках." });
        db.ModuleTopics.Remove(topic);
        await db.SaveChangesAsync();
        await RecalculateModuleTopicOrder(moduleId);
        await tx.CommitAsync();
        return NoContent();
    }
    [HttpPost("import-docx")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 11 * 1024 * 1024)]
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
    [RequireDeletionConfirmation(
        "усі модулі, тематичні плани та пов'язані налаштування",
        Message = "Підтвердіть повне очищення модулів і планів. Цю дію неможливо скасувати.")]
    // Повністю очищає модулі та пов'язані таблиці.
    public async Task<IActionResult> ClearAll()
    {
        // Обережно: тотальне очищення модулів і пов'язаних планів.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (await db.TeacherDraftItems.AsNoTracking().AnyAsync())
            {
                return Conflict(new
                {
                    message = "Неможливо очистити модулі, доки існують пов'язані чернетки."
                });
            }
            if (await db.ScheduleItems.AsNoTracking().AnyAsync())
            {
                return Conflict(new
                {
                    message = "Неможливо очистити модулі, доки вони використовуються в опублікованому розкладі."
                });
            }

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
            await tx.CommitAsync();
            return NoContent();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
    // Перевіряє, чи нові обмеження модуля виключають аудиторію вже створеного заняття.
    private async Task<bool> HasPlacementOutsideRoomRestrictionsAsync(
        int moduleId,
        IReadOnlyCollection<int> allowedRoomIds,
        IReadOnlyCollection<int> allowedBuildingIds)
    {
        var restrictRooms = allowedRoomIds.Count > 0;
        var restrictBuildings = allowedBuildingIds.Count > 0;
        if (!restrictRooms && !restrictBuildings)
        {
            return false;
        }

        var scheduleHasViolation = await db.ScheduleItems.AsNoTracking()
            .AnyAsync(item => item.ModuleId == moduleId
                              && item.LessonType.RequiresRoom
                              && item.RoomId != null
                              && ((restrictRooms && !allowedRoomIds.Contains(item.RoomId.Value))
                                  || (restrictBuildings
                                      && !allowedBuildingIds.Contains(item.Room!.BuildingId))));
        if (scheduleHasViolation)
        {
            return true;
        }

        return await db.TeacherDraftItems.AsNoTracking()
            .AnyAsync(item => item.ModuleId == moduleId
                              && item.LessonType.RequiresRoom
                              && item.RoomId != null
                              && ((restrictRooms && !allowedRoomIds.Contains(item.RoomId.Value))
                                  || (restrictBuildings
                                      && !allowedBuildingIds.Contains(item.Room!.BuildingId))));
    }

    // Шукає дублі коду модуля після однакової нормалізації для всіх підтримуваних баз даних.
    private async Task<bool> HasDuplicateModuleCodeAsync(int courseId, int excludedModuleId, string normalizedCode)
    {
        var candidates = await db.Modules.AsNoTracking()
            .Where(module => module.CourseId == courseId && module.Id != excludedModuleId)
            .Select(module => module.Code)
            .ToListAsync();
        return candidates.Any(candidate => string.Equals(
            candidate.Trim(),
            normalizedCode,
            StringComparison.OrdinalIgnoreCase));
    }

    // Забезпечує окремий екземпляр модуля для курсу з переносом курсо-залежних даних.
    private async Task<int> EnsureCourseScopeCore(int moduleId, int courseId, bool requireCourseLink)
    {
        var source = await db.Modules
            .AsSplitQuery()
            .Include(m => m.ModuleCourses)
            .Include(m => m.AllowedRooms)
            .Include(m => m.AllowedBuildings)
            .Include(m => m.TeacherModules)
            .Include(m => m.ModuleSupervisors)
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
        var ownsTransaction = db.Database.CurrentTransaction is null;
        var tx = ownsTransaction
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable)
            : null;
        try
        {
            var normalizedSourceCode = source.Code.Trim().ToUpperInvariant();
            var targetCandidates = await db.Modules
                .Include(m => m.ModuleCourses)
                .Where(m => m.CourseId == courseId)
                .OrderBy(m => m.Id)
                .ToListAsync();
            targetCandidates = targetCandidates
                .Where(module => string.Equals(
                    module.Code.Trim(),
                    normalizedSourceCode,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (targetCandidates.Count > 1)
            {
                throw new ArgumentException(
                    $"Для курсу знайдено кілька модулів із кодом '{normalizedSourceCode}'. Усуньте дублікати перед перенесенням даних.");
            }
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
            await CopyModuleStaffLinksAsync(source, target.Id);
            var detachedLink = source.ModuleCourses.FirstOrDefault(mc => mc.CourseId == courseId);
            if (detachedLink is not null)
            {
                db.ModuleCourses.Remove(detachedLink);
            }
            await db.SaveChangesAsync();
            if (tx is not null)
            {
                await tx.CommitAsync();
            }
            return target.Id;
        }
        catch
        {
            if (tx is not null)
            {
                await tx.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
            }
            throw;
        }
        finally
        {
            if (tx is not null)
            {
                await tx.DisposeAsync();
            }
        }
    }

    // Копіює допустимих викладачів і керівників у курсовий клон модуля без дублів.
    private async Task CopyModuleStaffLinksAsync(Module source, int targetModuleId)
    {
        var targetTeacherIds = await db.TeacherModules
            .Where(link => link.ModuleId == targetModuleId)
            .Select(link => link.TeacherId)
            .ToListAsync();
        var targetTeacherSet = targetTeacherIds.ToHashSet();
        foreach (var teacherId in source.TeacherModules.Select(link => link.TeacherId).Distinct())
        {
            if (targetTeacherSet.Add(teacherId))
            {
                db.TeacherModules.Add(new TeacherModule
                {
                    TeacherId = teacherId,
                    ModuleId = targetModuleId
                });
            }
        }

        var targetSupervisorIds = await db.ModuleSupervisors
            .Where(link => link.ModuleId == targetModuleId)
            .Select(link => link.TeacherId)
            .ToListAsync();
        var targetSupervisorSet = targetSupervisorIds.ToHashSet();
        foreach (var teacherId in source.ModuleSupervisors.Select(link => link.TeacherId).Distinct())
        {
            if (targetSupervisorSet.Add(teacherId))
            {
                db.ModuleSupervisors.Add(new ModuleSupervisor
                {
                    TeacherId = teacherId,
                    ModuleId = targetModuleId
                });
            }
        }
    }

    // Переносить курсо-залежні дані зі спільного модуля в окремий модуль курсу.
    private async Task MoveCourseScopedData(int sourceModuleId, int targetModuleId, int courseId)
    {
        var sourceTopics = await db.ModuleTopics.AsNoTracking()
            .Where(topic => topic.ModuleId == sourceModuleId)
            .Select(topic => new { topic.Id, topic.TopicCode, topic.LessonTypeId })
            .ToListAsync();
        var targetTopics = await db.ModuleTopics.AsNoTracking()
            .Where(topic => topic.ModuleId == targetModuleId)
            .Select(topic => new { topic.Id, topic.TopicCode, topic.LessonTypeId })
            .ToListAsync();
        var scheduleRows = await db.ScheduleItems
            .Include(item => item.LessonType)
            .Include(item => item.Room)
            .Where(item => item.ModuleId == sourceModuleId && item.Group.CourseId == courseId)
            .ToListAsync();
        var draftRows = await db.TeacherDraftItems
            .Include(item => item.LessonType)
            .Include(item => item.Room)
            .Where(item => item.ModuleId == sourceModuleId && item.Group.CourseId == courseId)
            .ToListAsync();

        static string NormalizeTopicCode(string value) => value.Trim().ToUpperInvariant();

        var duplicateSourceCode = sourceTopics
            .GroupBy(topic => NormalizeTopicCode(topic.TopicCode))
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateSourceCode is not null)
        {
            throw new ArgumentException(
                "Не вдалося перенести дані модуля: коди тем джерельного модуля не є однозначними.");
        }

        var targetTopicsByCode = targetTopics
            .GroupBy(topic => NormalizeTopicCode(topic.TopicCode))
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var topicIdMap = new Dictionary<int, int>();
        foreach (var sourceTopic in sourceTopics)
        {
            var normalizedCode = NormalizeTopicCode(sourceTopic.TopicCode);
            if (!targetTopicsByCode.TryGetValue(normalizedCode, out var matches) || matches.Count != 1)
            {
                throw new ArgumentException(
                    $"Не вдалося перенести дані модуля: для теми '{sourceTopic.TopicCode}' немає однозначної відповідності у цільовому модулі.");
            }
            if (matches[0].LessonTypeId != sourceTopic.LessonTypeId)
            {
                throw new ArgumentException(
                    $"Не вдалося перенести дані модуля: тема '{sourceTopic.TopicCode}' має різні типи заняття у джерельному та цільовому модулях.");
            }

            topicIdMap[sourceTopic.Id] = matches[0].Id;
        }

        var unmappedTopicId = scheduleRows
            .Select(item => item.ModuleTopicId)
            .Concat(draftRows.Select(item => item.ModuleTopicId))
            .FirstOrDefault(topicId => topicId is int value && !topicIdMap.ContainsKey(value));
        if (unmappedTopicId is not null)
        {
            throw new ArgumentException(
                "Не вдалося перенести дані модуля: заняття посилається на тему, яка не належить джерельному модулю.");
        }

        var targetAllowedRoomIds = (await db.ModuleRooms.AsNoTracking()
                .Where(link => link.ModuleId == targetModuleId)
                .Select(link => link.RoomId)
                .ToListAsync())
            .ToHashSet();
        var targetAllowedBuildingIds = (await db.ModuleBuildings.AsNoTracking()
                .Where(link => link.ModuleId == targetModuleId)
                .Select(link => link.BuildingId)
                .ToListAsync())
            .ToHashSet();
        var restrictRooms = targetAllowedRoomIds.Count > 0;
        var restrictBuildings = targetAllowedBuildingIds.Count > 0;

        bool ViolatesTargetRoomRestrictions(LessonTypeRef lessonType, int? roomId, Room? room)
            => lessonType.RequiresRoom
               && roomId is int assignedRoomId
               && ((restrictRooms && !targetAllowedRoomIds.Contains(assignedRoomId))
                   || (restrictBuildings
                       && (room is null || !targetAllowedBuildingIds.Contains(room.BuildingId))));

        var invalidSchedule = scheduleRows.FirstOrDefault(item =>
            ViolatesTargetRoomRestrictions(item.LessonType, item.RoomId, item.Room));
        if (invalidSchedule is not null)
        {
            throw new ArgumentException(
                $"Не вдалося перенести дані модуля: заняття розкладу #{invalidSchedule.Id} використовує аудиторію поза обмеженнями цільового модуля.");
        }

        var invalidDraft = draftRows.FirstOrDefault(item =>
            ViolatesTargetRoomRestrictions(item.LessonType, item.RoomId, item.Room));
        if (invalidDraft is not null)
        {
            throw new ArgumentException(
                $"Не вдалося перенести дані модуля: чернетка #{invalidDraft.Id} використовує аудиторію поза обмеженнями цільового модуля.");
        }

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
        foreach (var row in scheduleRows)
        {
            row.ModuleId = targetModuleId;
            if (row.ModuleTopicId is int topicId)
            {
                row.ModuleTopicId = topicIdMap[topicId];
            }
        }
        foreach (var row in draftRows)
        {
            row.ModuleId = targetModuleId;
            if (row.ModuleTopicId is int topicId)
            {
                row.ModuleTopicId = topicIdMap[topicId];
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
