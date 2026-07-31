using System.Data;
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
        var name = dto.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "Назва аудиторії обов'язкова." });
        if (dto.Capacity < 0)
            return BadRequest(new { message = "Місткість аудиторії не може бути від'ємною." });
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (!await db.Buildings.AnyAsync(building => building.Id == dto.BuildingId))
                return NotFound(new { message = "Корпус не знайдено." });
            if (dto.Id is int id && id > 0)
            {
                var r = await db.Rooms.FindAsync(id);
                if (r is null) return NotFound(new { message = "Аудиторію не знайдено." });
                var capacityViolation = await new RoomCapacityGuard(db)
                    .FindForRoomCapacityAsync(id, dto.Capacity);
                if (capacityViolation is not null)
                {
                    return Conflict(new { message = capacityViolation.ToMessage() });
                }
                if (r.BuildingId != dto.BuildingId
                    && (await db.ScheduleItems.AnyAsync(item => item.RoomId == id)
                        || await db.TeacherDraftItems.AnyAsync(item => item.RoomId == id)))
                {
                    return Conflict(new
                    {
                        message = "Неможливо змінити корпус аудиторії, доки вона використовується у розкладі або чернетках."
                    });
                }
                var violatesModuleBuildingRestriction = await db.ModuleRooms
                    .AnyAsync(link => link.RoomId == id
                                      && link.Module.AllowedBuildings.Any()
                                      && !link.Module.AllowedBuildings.Any(allowed => allowed.BuildingId == dto.BuildingId));
                if (violatesModuleBuildingRestriction)
                {
                    return Conflict(new
                    {
                        message = "Новий корпус суперечить обмеженням модулів, для яких дозволена ця аудиторія."
                    });
                }
                r.Name = name;
                r.Capacity = dto.Capacity;
                r.BuildingId = dto.BuildingId;
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Ok(r.Id);
            }
            else
            {
                var r = new Room { Name = name, Capacity = dto.Capacity, BuildingId = dto.BuildingId };
                db.Rooms.Add(r);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Ok(r.Id);
            }
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
    [HttpDelete("{id:int}")]
    [RequireDeletionConfirmation("аудиторію")]
    // Видаляє аудиторію, за потреби примусово.
    public async Task<IActionResult> Delete(int id, [FromQuery] bool force = false)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var exists = await db.Rooms.AnyAsync(r => r.Id == id);
            if (!exists) return NotFound();
            var usedInSchedule = await db.ScheduleItems.AnyAsync(x => x.RoomId == id);
            var usedInDrafts = await db.TeacherDraftItems.AnyAsync(x => x.RoomId == id);
            if ((usedInSchedule || usedInDrafts) && !force)
                return Conflict(new { message = "Аудиторія використовується у розкладі або чернетках." });
            if (force)
            {
                var requiredBySchedule = await db.ScheduleItems
                    .AnyAsync(item => item.RoomId == id && item.LessonType.RequiresRoom);
                var requiredByDrafts = await db.TeacherDraftItems
                    .AnyAsync(item => item.RoomId == id && item.LessonType.RequiresRoom);
                if (requiredBySchedule || requiredByDrafts)
                {
                    return Conflict(new
                    {
                        message = "Неможливо видалити аудиторію: вона призначена заняттям, для яких аудиторія обов'язкова. Спочатку перенесіть ці заняття."
                    });
                }

                var scheduleRevision = Guid.NewGuid();
                await db.ScheduleItems
                    .Where(item => item.RoomId == id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.RoomId, (int?)null)
                        .SetProperty(item => item.Revision, scheduleRevision));
                var draftRevision = Guid.NewGuid();
                var updatedAt = DateTime.UtcNow;
                await db.TeacherDraftItems
                    .Where(item => item.RoomId == id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.RoomId, (int?)null)
                        .SetProperty(item => item.Revision, draftRevision)
                        .SetProperty(item => item.UpdatedAt, updatedAt));
            }
            await db.ModuleRooms.Where(x => x.RoomId == id).ExecuteDeleteAsync();
            var rows = await db.Rooms.Where(x => x.Id == id).ExecuteDeleteAsync();
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
