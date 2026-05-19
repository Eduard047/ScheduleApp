using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
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
        var name = dto.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { message = "Назва групи обов'язкова." });
        }
        if (dto.StudentsCount < 0)
        {
            return BadRequest(new { message = "Кількість студентів не може бути від'ємною." });
        }
        if (!await db.Courses.AnyAsync(c => c.Id == dto.CourseId))
        {
            return NotFound(new { message = "Курс не знайдено." });
        }
        if (dto.Id is int id && id > 0)
        {
            var g = await db.Groups.FindAsync(id);
            if (g is null) return NotFound(new { message = "Групу не знайдено." });
            g.Name = name;
            g.StudentsCount = dto.StudentsCount;
            g.CourseId = dto.CourseId;
            await db.SaveChangesAsync();
            return Ok(g.Id);
        }
        else
        {
            var g = new GroupEntity { Name = name, StudentsCount = dto.StudentsCount, CourseId = dto.CourseId };
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
        var hasScheduleItems = await db.ScheduleItems.AnyAsync(x => x.GroupId == id);
        var hasDraftItems = await db.TeacherDraftItems.AnyAsync(x => x.GroupId == id);
        var used = hasScheduleItems || hasDraftItems;
        if (used && !force)
            return Conflict(new { message = "Група використовується у розкладі або чернетках" });
        if (force)
        {
            var q = db.ScheduleItems.Where(x => x.GroupId == id);
            var affectedPlans = await q
                .Select(x => new { CourseId = x.Group.CourseId, x.ModuleId })
                .Distinct()
                .ToListAsync();
            var affectedLoads = await q.Where(x => x.TeacherId != null)
                .Select(x => new { x.TeacherId, CourseId = x.Group.CourseId })
                .Distinct()
                .ToListAsync();
            await q.ExecuteDeleteAsync();
            await new AggregatesService(db).RecalcAsync(
                affectedPlans.Select(a => (a.CourseId, a.ModuleId)),
                affectedLoads.Select(a => (a.TeacherId!.Value, a.CourseId)));
            await db.TeacherDraftItems.Where(x => x.GroupId == id).ExecuteDeleteAsync();
        }
        var rows = await db.Groups.Where(x => x.Id == id).ExecuteDeleteAsync();
        if (rows == 0) return NotFound();
        return NoContent();
    }
}
