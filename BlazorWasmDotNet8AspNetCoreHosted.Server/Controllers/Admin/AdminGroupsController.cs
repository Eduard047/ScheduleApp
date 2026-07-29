using System.Data;
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
        var name = dto.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { message = "Назва групи обов'язкова." });
        }
        if (name.Length > 256)
        {
            return BadRequest(new { message = "Назва групи не може перевищувати 256 символів." });
        }
        if (dto.StudentsCount is < 0 or > 10000)
        {
            return BadRequest(new { message = "Кількість студентів має бути від 0 до 10000." });
        }
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (!await db.Courses.AnyAsync(c => c.Id == dto.CourseId))
            {
                return NotFound(new { message = "Курс не знайдено." });
            }
            if (dto.Id is int id && id > 0)
            {
                var g = await db.Groups.FindAsync(id);
                if (g is null) return NotFound(new { message = "Групу не знайдено." });
                if (g.CourseId != dto.CourseId
                    && (await db.ScheduleItems.AnyAsync(item => item.GroupId == id)
                        || await db.TeacherDraftItems.AnyAsync(item => item.GroupId == id)))
                {
                    return Conflict(new
                    {
                        message = "Неможливо змінити курс групи, доки вона використовується у розкладі або чернетках."
                    });
                }
                if (g.CourseId != dto.CourseId)
                {
                    var scopedExceptionDates = await db.CalendarExceptions
                        .Where(exception => exception.GroupId == id)
                        .Select(exception => exception.Date)
                        .ToListAsync();
                    if (scopedExceptionDates.Count != scopedExceptionDates.Distinct().Count())
                    {
                        return Conflict(new
                        {
                            message = "Неможливо змінити курс групи: для неї існують дубльовані календарні винятки на одну дату. Спочатку усуньте дублікати."
                        });
                    }
                    await db.CalendarExceptions
                        .Where(exception => exception.GroupId == id)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(exception => exception.CourseId, dto.CourseId));
                }
                var capacityViolation = await new RoomCapacityGuard(db)
                    .FindForGroupSizeAsync(id, dto.StudentsCount);
                if (capacityViolation is not null)
                {
                    return Conflict(new { message = capacityViolation.ToMessage() });
                }
                g.Name = name;
                g.StudentsCount = dto.StudentsCount;
                g.CourseId = dto.CourseId;
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Ok(g.Id);
            }
            else
            {
                var g = new GroupEntity { Name = name, StudentsCount = dto.StudentsCount, CourseId = dto.CourseId };
                db.Groups.Add(g);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Ok(g.Id);
            }
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
    [HttpDelete("{id:int}")]
    [RequireDeletionConfirmation("групу")]
    // Видаляє групу, з примусовими перерахунками при force=true.
    public async Task<IActionResult> Delete(int id, [FromQuery] bool force = false)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
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
            await tx.CommitAsync();
            return NoContent();
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
