using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using GroupEntity = BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities.Group;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;

[ApiController]
[Route("api/admin/groups")]
// Контролер адміністратора для груп
public class AdminGroupsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    // Повертає список груп.
    public async Task<IReadOnlyList<GroupEditDto>> List()
        => await db.Groups.AsNoTracking()
            .Select(g => new GroupEditDto(g.Id, g.Name, g.StudentsCount, g.CourseId))
            .ToListAsync();
    [HttpPost("upsert")]
    // Створює або оновлює групу.
    public async Task<ActionResult<int>> Upsert(GroupEditDto dto)
    {
        var course = await db.Courses.FindAsync(dto.CourseId) ?? throw new ArgumentException("Курс не знайдено");
        if (dto.Id is int id && id > 0)
        {
            var g = await db.Groups.FindAsync(id) ?? throw new ArgumentException("Групу не знайдено");
            g.Name = dto.Name;
            g.StudentsCount = dto.StudentsCount;
            g.CourseId = dto.CourseId;
            await db.SaveChangesAsync();
            return Ok(g.Id);
        }
        else
        {
            var g = new GroupEntity { Name = dto.Name, StudentsCount = dto.StudentsCount, CourseId = dto.CourseId };
            db.Groups.Add(g);
            await db.SaveChangesAsync();
            return Ok(g.Id);
        }
    }
    [HttpDelete("{id:int}")]
    [RequireDeletionConfirmation("групу")]
    // Видаляє групу, з примусовими перерахунками при force=true.
    public async Task<IActionResult> Delete(int id, [FromQuery] bool force = false)
    {
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id);
        if (group is null) return NotFound();
        var used = await db.ScheduleItems.AnyAsync(x => x.GroupId == id);
        if (used && !force)
            return Conflict(new { message = "Група використовується у розкладі" });
        if (force)
        {
            var q = db.ScheduleItems.Where(x => x.GroupId == id);
            var affectedLoads = await q.Where(x => x.TeacherId != null)
                .Select(x => new { x.TeacherId, CourseId = x.Group.CourseId })
                .Distinct()
                .ToListAsync();
            await q.ExecuteDeleteAsync();
            if (affectedLoads.Count > 0)
            {
                var tIds = affectedLoads.Select(a => a.TeacherId!.Value).Distinct().ToList();
                var cIds = affectedLoads.Select(a => a.CourseId).Distinct().ToList();
                var excludeLoadIds = await db.LessonTypes
                    .Where(lt =>
                        !lt.CountInLoad
                        || string.Equals(lt.Code, "CANCELED", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(lt.Code, "RESCHEDULED", StringComparison.OrdinalIgnoreCase))
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
            }
            var lessonTypes = await db.LessonTypes
                .Select(lt => new { lt.Id, lt.Code, lt.CountInPlan })
                .ToListAsync();
            var excludePlanIds = lessonTypes
                .Where(lt =>
                    !lt.CountInPlan
                    || string.Equals(lt.Code, "CANCELED", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(lt.Code, "RESCHEDULED", System.StringComparison.OrdinalIgnoreCase))
                .Select(lt => lt.Id)
                .ToHashSet();
            var planRows = await db.ModulePlans
                .Where(p => p.CourseId == group.CourseId)
                .ToListAsync();
            if (planRows.Count > 0)
            {
                var moduleIds = planRows.Select(p => p.ModuleId).Distinct().ToList();
                var countsByModule = await db.ScheduleItems
                    .Include(si => si.Group)
                    .Where(si => si.Group.CourseId == group.CourseId
                                 && moduleIds.Contains(si.ModuleId)
                                 && !excludePlanIds.Contains(si.LessonTypeId))
                    .GroupBy(si => si.ModuleId)
                    .Select(g => new { ModuleId = g.Key, C = g.Count() })
                    .ToListAsync();
                foreach (var p in planRows)
                    p.ScheduledHours = countsByModule.FirstOrDefault(c => c.ModuleId == p.ModuleId)?.C ?? 0;
                await db.SaveChangesAsync();
            }
        }
        var rows = await db.Groups.Where(x => x.Id == id).ExecuteDeleteAsync();
        if (rows == 0) return NotFound();
        return NoContent();
    }
}
