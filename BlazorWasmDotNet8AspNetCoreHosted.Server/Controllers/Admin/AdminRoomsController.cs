using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;

[ApiController]
[Route("api/admin/rooms")]
// Контролер адміністратора для приміщень
public class AdminRoomsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    // Повертає список аудиторій.
    public async Task<IReadOnlyList<RoomEditDto>> List()
        => await db.Rooms.AsNoTracking()
            .Select(r => new RoomEditDto(r.Id, r.Name, r.Capacity, r.BuildingId))
            .ToListAsync();
    [HttpPost("upsert")]
    // Створює або оновлює аудиторію.
    public async Task<ActionResult<int>> Upsert(RoomEditDto dto)
    {
        _ = await db.Buildings.FindAsync(dto.BuildingId) ?? throw new ArgumentException("Корпус не знайдено");
        if (dto.Id is int id && id > 0)
        {
            var r = await db.Rooms.FindAsync(id) ?? throw new ArgumentException("Аудиторію не знайдено");
            r.Name = dto.Name; r.Capacity = dto.Capacity; r.BuildingId = dto.BuildingId;
            await db.SaveChangesAsync(); return Ok(r.Id);
        }
        else
        {
            var r = new Room { Name = dto.Name, Capacity = dto.Capacity, BuildingId = dto.BuildingId };
            db.Rooms.Add(r); await db.SaveChangesAsync(); return Ok(r.Id);
        }
    }
    [HttpDelete("{id:int}")]
    [RequireDeletionConfirmation("аудиторію")]
    // Видаляє аудиторію, за потреби примусово.
    public async Task<IActionResult> Delete(int id, [FromQuery] bool force = false)
    {
        var exists = await db.Rooms.AnyAsync(r => r.Id == id);
        if (!exists) return NotFound();
        var used = await db.ScheduleItems.AnyAsync(x => x.RoomId == id);
        if (used && !force)
            return Conflict(new { message = "Аудиторія використовується у розкладі" });
        if (force)
        {
            var q = db.ScheduleItems.Where(x => x.RoomId == id);
            var affectedPlans = await q
                .Select(x => new { CourseId = x.Group.CourseId, x.ModuleId })
                .Distinct()
                .ToListAsync();
            var affectedLoads = await q.Where(x => x.TeacherId != null)
                .Select(x => new { x.TeacherId, CourseId = x.Group.CourseId })
                .Distinct()
                .ToListAsync();
            await q.ExecuteDeleteAsync();
            await db.ModuleRooms.Where(x => x.RoomId == id).ExecuteDeleteAsync();
            await new AggregatesService(db).RecalcAsync(
                affectedPlans.Select(a => (a.CourseId, a.ModuleId)),
                affectedLoads.Select(a => (a.TeacherId!.Value, a.CourseId)));
        }
        var rows = await db.Rooms.Where(x => x.Id == id).ExecuteDeleteAsync();
        if (rows == 0) return NotFound();
        return NoContent();
    }
}
