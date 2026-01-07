using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;

[ApiController]
[Route("api/admin/courses")]
// Контролер адміністратора для курсів
public class AdminCoursesController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    // Повертає список курсів.
    public async Task<IReadOnlyList<CourseEditDto>> List()
        => await db.Courses.AsNoTracking()
            .OrderBy(c => c.Id)
            .Select(c => new CourseEditDto(c.Id, c.Name, c.DurationWeeks))
            .ToListAsync();
    [HttpPost("upsert")]
    // Створює або оновлює курс.
    public async Task<ActionResult<int>> Upsert(CourseEditDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest(new { message = "Назва є обовʼязковою" });
        if (dto.Id is int id && id > 0)
        {
            var c = await db.Courses.FindAsync(id) ?? throw new ArgumentException("Курс не знайдено");
            c.Name = dto.Name;
            c.DurationWeeks = dto.DurationWeeks;
            await db.SaveChangesAsync();
            return Ok(c.Id);
        }
        else
        {
            var c = new Course { Name = dto.Name, DurationWeeks = dto.DurationWeeks };
            db.Courses.Add(c);
            await db.SaveChangesAsync();
            return Ok(c.Id);
        }
    }
    [HttpDelete("{id:int}")]
    [RequireDeletionConfirmation("курс")]
    // Видаляє курс, за потреби з примусовим очищенням залежностей.
    public async Task<IActionResult> Delete(int id, [FromQuery] bool force = false)
    {
        var used = await db.Groups.AnyAsync(g => g.CourseId == id)
                   || await db.Modules.AnyAsync(m => m.CourseId == id)
                   || await db.ModuleCourses.AnyAsync(mc => mc.CourseId == id)
                   || await db.ModulePlans.AnyAsync(p => p.CourseId == id)
                   || await db.TeacherCourseLoads.AnyAsync(l => l.CourseId == id)
                   || await db.ScheduleItems.AnyAsync(s => s.Group.CourseId == id);
        if (used && !force)
            return Conflict(new { message = "Курс використовується групами/модулями/розкладом" });
        if (used && force)
        {
            var moduleIdsLinked = await db.ModuleCourses
                .Where(mc => mc.CourseId == id)
                .Select(mc => mc.ModuleId)
                .Distinct()
                .ToListAsync();
            await db.ScheduleItems.Where(s => s.Group.CourseId == id).ExecuteDeleteAsync();
            await db.ModulePlans.Where(p => p.CourseId == id).ExecuteDeleteAsync();
            await db.ModuleSequenceItems.Where(si => si.CourseId == id).ExecuteDeleteAsync();
            await db.ModuleFillers.Where(f => f.CourseId == id).ExecuteDeleteAsync();
            await db.TeacherCourseLoads.Where(l => l.CourseId == id).ExecuteDeleteAsync();
            await db.TimeSlots.Where(ts => ts.CourseId == id).ExecuteDeleteAsync();
            await db.LunchConfigs.Where(lc => lc.CourseId == id).ExecuteDeleteAsync();
            if (moduleIdsLinked.Count > 0)
            {
                var moduleCourseMap = await db.ModuleCourses.AsNoTracking()
                    .Where(mc => moduleIdsLinked.Contains(mc.ModuleId))
                    .GroupBy(mc => mc.ModuleId)
                    .ToDictionaryAsync(g => g.Key, g => g.Select(mc => mc.CourseId).ToList());
                var modules = await db.Modules
                    .Where(m => moduleIdsLinked.Contains(m.Id))
                    .ToListAsync();
                var modulesToDelete = new List<int>();
                foreach (var module in modules)
                {
                    if (!moduleCourseMap.TryGetValue(module.Id, out var courseIdsForModule))
                    {
                        continue;
                    }
                    var alternativeCourseIds = courseIdsForModule.Where(cid => cid != id).ToList();
                    if (module.CourseId == id)
                    {
                        if (alternativeCourseIds.Count > 0)
                        {
                            module.CourseId = alternativeCourseIds[0];
                        }
                        else
                        {
                            modulesToDelete.Add(module.Id);
                        }
                    }
                }
                if (modulesToDelete.Count > 0)
                {
                    await db.TeacherModules.Where(tm => modulesToDelete.Contains(tm.ModuleId)).ExecuteDeleteAsync();
                    await db.ModuleRooms.Where(mr => modulesToDelete.Contains(mr.ModuleId)).ExecuteDeleteAsync();
                    await db.ModuleBuildings.Where(mb => modulesToDelete.Contains(mb.ModuleId)).ExecuteDeleteAsync();
                    await db.ModuleTopics.Where(mt => modulesToDelete.Contains(mt.ModuleId)).ExecuteDeleteAsync();
                }
                await db.SaveChangesAsync();
                if (modulesToDelete.Count > 0)
                {
                    await db.Modules.Where(m => modulesToDelete.Contains(m.Id)).ExecuteDeleteAsync();
                }
            }
            await db.ModuleCourses.Where(mc => mc.CourseId == id).ExecuteDeleteAsync();
            await db.Groups.Where(g => g.CourseId == id).ExecuteDeleteAsync();
        }
        var rows = await db.Courses.Where(c => c.Id == id).ExecuteDeleteAsync();
        if (rows == 0) return NotFound();
        return NoContent();
    }
}
