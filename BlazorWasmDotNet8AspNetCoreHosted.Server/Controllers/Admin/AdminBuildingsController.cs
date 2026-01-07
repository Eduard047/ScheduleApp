using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;

[ApiController]
[Route("api/admin/buildings")]
// Контролер адміністратора для будівель і переміщень
public class AdminBuildingsController(AppDbContext db) : ControllerBase
{

    private const int DefaultTravelMinutes = 20;
    [HttpGet]
    // Повертає список будівель і маршрутів між ними.
    public async Task<object> List()
    {
        var buildings = await db.Buildings.AsNoTracking()
            .Select(b => new BuildingEditDto(b.Id, b.Name, b.Address)).ToListAsync();
        var travels = await db.BuildingTravels.AsNoTracking()
            .Select(t => new BuildingTravelEditDto(t.FromBuildingId, t.ToBuildingId, t.Minutes)).ToListAsync();
        return new { buildings, travels };
    }
    [HttpPost("upsert")]
    // Створює або оновлює будівлю та маршрути за замовчуванням.
    public async Task<ActionResult<int>> Upsert(BuildingEditDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest(new { message = "Назва є обовʼязковою" });
        if (dto.Id is int id && id > 0)
        {
            var b = await db.Buildings.FindAsync(id);
            if (b is null) return NotFound(new { message = "Корпус не знайдено" });
            b.Name = dto.Name;
            b.Address = dto.Address;
            await db.SaveChangesAsync();
            await EnsureDefaultTravelsForBuilding(b.Id);
            return Ok(b.Id);
        }
        else
        {
            var b = new Building { Name = dto.Name, Address = dto.Address };
            db.Buildings.Add(b);
            await db.SaveChangesAsync();
            await EnsureDefaultTravelsForBuilding(b.Id);
            return Ok(b.Id);
        }
    }
    [HttpDelete("{id:int}")]
    [RequireDeletionConfirmation("корпус")]
    // Видаляє будівлю після перевірки залежностей.
    public async Task<IActionResult> Delete(int id)
    {
        var used = await db.Rooms.AnyAsync(r => r.BuildingId == id);
        if (used) return Conflict(new { message = "Корпус містить аудиторії; спочатку перенесіть або видаліть їх" });
        await db.ModuleBuildings.Where(x => x.BuildingId == id).ExecuteDeleteAsync();
        await db.BuildingTravels.Where(x => x.FromBuildingId == id || x.ToBuildingId == id).ExecuteDeleteAsync();
        var rows = await db.Buildings.Where(x => x.Id == id).ExecuteDeleteAsync();
        if (rows == 0) return NotFound();
        return NoContent();
    }
    // Нормалізує пару будівель для симетричних маршрутів.
    private static (int from, int to) Canon(int a, int b) => (Math.Min(a, b), Math.Max(a, b));
    [HttpPost("travel/upsert")]
    // Створює або оновлює маршрут між будівлями.
    public async Task<IActionResult> UpsertTravel(BuildingTravelEditDto dto)
    {
        if (dto.FromBuildingId == dto.ToBuildingId)
            return BadRequest(new { message = "Корпуси «звідки» та «куди» мають відрізнятися" });
        _ = await db.Buildings.FindAsync(dto.FromBuildingId) ?? throw new ArgumentException("Корпус «звідки» не знайдено");
        _ = await db.Buildings.FindAsync(dto.ToBuildingId) ?? throw new ArgumentException("Корпус «куди» не знайдено");
        var (fromId, toId) = Canon(dto.FromBuildingId, dto.ToBuildingId);
        var minutes = dto.Minutes <= 0 ? DefaultTravelMinutes : dto.Minutes;
        var row = await db.BuildingTravels
            .FirstOrDefaultAsync(x => x.FromBuildingId == fromId && x.ToBuildingId == toId);
        if (row is null)
        {
            db.BuildingTravels.Add(new BuildingTravel { FromBuildingId = fromId, ToBuildingId = toId, Minutes = minutes });
        }
        else
        {
            row.Minutes = minutes;
        }
        await db.SaveChangesAsync();
        return Ok();
    }
    [HttpPost("travel/delete")]
    [RequireDeletionConfirmation("маршрут між корпусами")]
    // Видаляє маршрут між будівлями.
    public async Task<IActionResult> DeleteTravel(BuildingTravelEditDto dto)
    {
        var (fromId, toId) = Canon(dto.FromBuildingId, dto.ToBuildingId);
        var rows = await db.BuildingTravels
            .Where(x => x.FromBuildingId == fromId && x.ToBuildingId == toId)
            .ExecuteDeleteAsync();
        if (rows == 0) return NotFound();
        return NoContent();
    }
    // Додає маршрути за замовчуванням для нової будівлі.
    private async Task EnsureDefaultTravelsForBuilding(int buildingId)
    {
        var others = await db.Buildings.AsNoTracking()
            .Where(b => b.Id != buildingId)
            .Select(b => b.Id)
            .ToListAsync();
        if (others.Count == 0) return;
        var existing = await db.BuildingTravels.AsNoTracking()
            .Where(t => t.FromBuildingId == buildingId || t.ToBuildingId == buildingId)
            .Select(t => new { t.FromBuildingId, t.ToBuildingId })
            .ToListAsync();
        var have = new HashSet<(int, int)>(existing.Select(p =>
        {
            var f = Math.Min(p.FromBuildingId, p.ToBuildingId);
            var t = Math.Max(p.FromBuildingId, p.ToBuildingId);
            return (f, t);
        }));
        foreach (var otherId in others)
        {
            var (fromId, toId) = Canon(buildingId, otherId);
            if (have.Contains((fromId, toId))) continue;
            db.BuildingTravels.Add(new BuildingTravel
            {
                FromBuildingId = fromId,
                ToBuildingId = toId,
                Minutes = DefaultTravelMinutes
            });
        }
        await db.SaveChangesAsync();
    }
}
