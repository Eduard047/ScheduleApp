using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;

[ApiController]
[Route("api/admin/buildings")]
// Контролер адміністратора для будівель і переміщень
public class AdminBuildingsController(AppDbContext db) : ControllerBase
{
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
        var name = dto.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "Назва є обовʼязковою" });
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
        if (dto.Id is int id && id > 0)
        {
            var b = await db.Buildings.FindAsync(id);
            if (b is null) return NotFound(new { message = "Корпус не знайдено" });
            b.Name = name;
            b.Address = dto.Address;
            await db.SaveChangesAsync();
            await EnsureDefaultTravelsForBuilding(b.Id);
            await tx.CommitAsync();
            return Ok(b.Id);
        }
        else
        {
            var b = new Building { Name = name, Address = dto.Address };
            db.Buildings.Add(b);
            await db.SaveChangesAsync();
            await EnsureDefaultTravelsForBuilding(b.Id);
            await tx.CommitAsync();
            return Ok(b.Id);
        }
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
    [HttpDelete("{id:int}")]
    [RequireDeletionConfirmation("корпус")]
    // Видаляє будівлю після перевірки залежностей.
    public async Task<IActionResult> Delete(int id)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (!await db.Buildings.AnyAsync(building => building.Id == id)) return NotFound();
            var used = await db.Rooms.AnyAsync(r => r.BuildingId == id);
            if (used) return Conflict(new { message = "Корпус містить аудиторії; спочатку перенесіть або видаліть їх" });
            await db.ModuleBuildings.Where(x => x.BuildingId == id).ExecuteDeleteAsync();
            await db.BuildingTravels.Where(x => x.FromBuildingId == id || x.ToBuildingId == id).ExecuteDeleteAsync();
            var rows = await db.Buildings.Where(x => x.Id == id).ExecuteDeleteAsync();
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
    [HttpPost("travel/upsert")]
    // Створює або оновлює маршрут між будівлями.
    public async Task<IActionResult> UpsertTravel(BuildingTravelEditDto dto)
    {
        if (dto.FromBuildingId == dto.ToBuildingId)
            return BadRequest(new { message = "Корпуси «звідки» та «куди» мають відрізнятися" });
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            if (!await db.Buildings.AnyAsync(building => building.Id == dto.FromBuildingId))
                return NotFound(new { message = "Корпус «звідки» не знайдено" });
            if (!await db.Buildings.AnyAsync(building => building.Id == dto.ToBuildingId))
                return NotFound(new { message = "Корпус «куди» не знайдено" });
            var (fromId, toId) = TravelTimePolicy.NormalizePair(dto.FromBuildingId, dto.ToBuildingId);
            var minutes = dto.Minutes <= 0 ? TravelTimePolicy.DefaultMinutes : dto.Minutes;
            var row = await db.BuildingTravels
                .FirstOrDefaultAsync(item => item.FromBuildingId == fromId && item.ToBuildingId == toId);
            var previousMinutes = row?.Minutes ?? TravelTimePolicy.DefaultMinutes;
            var impact = await TravelConfigurationImpactAnalyzer.FindNewViolationsAsync(
                db,
                fromId,
                toId,
                previousMinutes,
                minutes);
            if (impact.Count > 0)
            {
                return Conflict(new { message = impact.ToMessage() });
            }

            if (row is null)
            {
                db.BuildingTravels.Add(new BuildingTravel
                {
                    FromBuildingId = fromId,
                    ToBuildingId = toId,
                    Minutes = minutes
                });
            }
            else
            {
                row.Minutes = minutes;
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Ok();
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }
    [HttpPost("travel/delete")]
    [RequireDeletionConfirmation("маршрут між корпусами")]
    // Видаляє маршрут між будівлями.
    public async Task<IActionResult> DeleteTravel(BuildingTravelEditDto dto)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var (fromId, toId) = TravelTimePolicy.NormalizePair(dto.FromBuildingId, dto.ToBuildingId);
            var row = await db.BuildingTravels
                .FirstOrDefaultAsync(item => item.FromBuildingId == fromId && item.ToBuildingId == toId);
            if (row is null) return NotFound();
            var impact = await TravelConfigurationImpactAnalyzer.FindNewViolationsAsync(
                db,
                fromId,
                toId,
                row.Minutes,
                TravelTimePolicy.DefaultMinutes);
            if (impact.Count > 0)
            {
                return Conflict(new { message = impact.ToMessage() });
            }

            db.BuildingTravels.Remove(row);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return NoContent();
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
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
            var (fromId, toId) = TravelTimePolicy.NormalizePair(buildingId, otherId);
            if (have.Contains((fromId, toId))) continue;
            db.BuildingTravels.Add(new BuildingTravel
            {
                FromBuildingId = fromId,
                ToBuildingId = toId,
                Minutes = TravelTimePolicy.DefaultMinutes
            });
        }
        await db.SaveChangesAsync();
    }
}
