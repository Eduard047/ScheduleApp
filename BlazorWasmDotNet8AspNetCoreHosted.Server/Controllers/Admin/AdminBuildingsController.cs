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
    internal const int MaxBuildingCount = 100;
    internal const int MaxTravelRowCount = MaxBuildingCount * (MaxBuildingCount - 1) / 2;
    private static readonly TimeSpan MaxTravelMutationDuration = TimeSpan.FromSeconds(20);
    private static readonly SemaphoreSlim TravelMutationGate = new(1, 1);

    [HttpGet]
    // Повертає список будівель і маршрутів між ними.
    public async Task<ActionResult<object>> List(CancellationToken cancellationToken = default)
    {
        var buildings = await db.Buildings.AsNoTracking()
            .OrderBy(b => b.Id)
            .Take(MaxBuildingCount + 1)
            .Select(b => new BuildingEditDto(b.Id, b.Name, b.Address))
            .ToListAsync(cancellationToken);
        if (buildings.Count > MaxBuildingCount)
        {
            return UnprocessableEntity(new
            {
                message = $"Каталог корпусів перевищує безпечний ліміт {MaxBuildingCount} записів."
            });
        }
        var travels = await db.BuildingTravels.AsNoTracking()
            .OrderBy(t => t.Id)
            .Take(MaxTravelRowCount + 1)
            .Select(t => new BuildingTravelEditDto(t.FromBuildingId, t.ToBuildingId, t.Minutes))
            .ToListAsync(cancellationToken);
        if (travels.Count > MaxTravelRowCount)
        {
            return UnprocessableEntity(new
            {
                message = $"Каталог переходів перевищує безпечний ліміт {MaxTravelRowCount} записів."
            });
        }
        return Ok(new { buildings, travels });
    }
    [HttpPost("upsert")]
    // Створює або оновлює будівлю та маршрути за замовчуванням.
    public async Task<ActionResult<int>> Upsert(
        BuildingEditDto dto,
        CancellationToken cancellationToken = default)
    {
        var name = dto.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "Назва є обовʼязковою" });
        if (name.Length > 256)
            return BadRequest(new { message = "Назва корпусу не може перевищувати 256 символів." });
        var address = string.IsNullOrWhiteSpace(dto.Address) ? null : dto.Address.Trim();
        if (address is { Length: > 512 })
            return BadRequest(new { message = "Адреса корпусу не може перевищувати 512 символів." });
        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            if (dto.Id is int id && id > 0)
            {
                var b = await db.Buildings.FindAsync(new object[] { id }, cancellationToken);
                if (b is null) return NotFound(new { message = "Корпус не знайдено" });
                b.Name = name;
                b.Address = address;
                await db.SaveChangesAsync(cancellationToken);
                await EnsureDefaultTravelsForBuilding(b.Id, cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return Ok(b.Id);
            }
            else
            {
                var currentCount = await db.Buildings.CountAsync(cancellationToken);
                if (currentCount >= MaxBuildingCount)
                {
                    return UnprocessableEntity(new
                    {
                        message = $"Не можна створити більше {MaxBuildingCount} корпусів."
                    });
                }
                var b = new Building { Name = name, Address = address };
                db.Buildings.Add(b);
                await db.SaveChangesAsync(cancellationToken);
                await EnsureDefaultTravelsForBuilding(b.Id, cancellationToken);
                await tx.CommitAsync(cancellationToken);
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
    public async Task<IActionResult> UpsertTravel(
        BuildingTravelEditDto dto,
        CancellationToken cancellationToken = default)
    {
        if (dto.FromBuildingId == dto.ToBuildingId)
            return BadRequest(new { message = "Корпуси «звідки» та «куди» мають відрізнятися" });
        return await RunTravelMutationAsync(
            token => UpsertTravelCoreAsync(dto, token),
            cancellationToken);
    }

    private async Task<IActionResult> UpsertTravelCoreAsync(
        BuildingTravelEditDto dto,
        CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            if (!await db.Buildings.AnyAsync(
                    building => building.Id == dto.FromBuildingId,
                    cancellationToken))
                return NotFound(new { message = "Корпус «звідки» не знайдено" });
            if (!await db.Buildings.AnyAsync(
                    building => building.Id == dto.ToBuildingId,
                    cancellationToken))
                return NotFound(new { message = "Корпус «куди» не знайдено" });
            var (fromId, toId) = TravelTimePolicy.NormalizePair(dto.FromBuildingId, dto.ToBuildingId);
            var minutes = dto.Minutes <= 0 ? TravelTimePolicy.DefaultMinutes : dto.Minutes;
            var row = await db.BuildingTravels
                .FirstOrDefaultAsync(
                    item => item.FromBuildingId == fromId && item.ToBuildingId == toId,
                    cancellationToken);
            var previousMinutes = row?.Minutes ?? TravelTimePolicy.DefaultMinutes;
            var impact = await TravelConfigurationImpactAnalyzer.FindNewViolationsAsync(
                db,
                fromId,
                toId,
                previousMinutes,
                minutes,
                cancellationToken);
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
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
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
    public async Task<IActionResult> DeleteTravel(
        BuildingTravelEditDto dto,
        CancellationToken cancellationToken = default)
        => await RunTravelMutationAsync(
            token => DeleteTravelCoreAsync(dto, token),
            cancellationToken);

    private async Task<IActionResult> DeleteTravelCoreAsync(
        BuildingTravelEditDto dto,
        CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            var (fromId, toId) = TravelTimePolicy.NormalizePair(dto.FromBuildingId, dto.ToBuildingId);
            var row = await db.BuildingTravels
                .FirstOrDefaultAsync(
                    item => item.FromBuildingId == fromId && item.ToBuildingId == toId,
                    cancellationToken);
            if (row is null) return NotFound();
            var impact = await TravelConfigurationImpactAnalyzer.FindNewViolationsAsync(
                db,
                fromId,
                toId,
                row.Minutes,
                TravelTimePolicy.DefaultMinutes,
                cancellationToken);
            if (impact.Count > 0)
            {
                return Conflict(new { message = impact.ToMessage() });
            }

            db.BuildingTravels.Remove(row);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return NoContent();
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<IActionResult> RunTravelMutationAsync(
        Func<CancellationToken, Task<IActionResult>> action,
        CancellationToken cancellationToken)
    {
        if (!await TravelMutationGate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                message = "Інша зміна переходів уже виконується. Дочекайтеся її завершення."
            });
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MaxTravelMutationDuration);
        try
        {
            return await action(deadline.Token);
        }
        catch (TravelConfigurationCapacityException ex)
        {
            return UnprocessableEntity(new { message = ex.Message });
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && deadline.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Перевірка переходів перевищила безпечний час виконання. Зменште обсяг даних або повторіть пізніше."
            });
        }
        finally
        {
            TravelMutationGate.Release();
        }
    }

    // Додає маршрути за замовчуванням для нової будівлі.
    private async Task EnsureDefaultTravelsForBuilding(
        int buildingId,
        CancellationToken cancellationToken)
    {
        var others = await db.Buildings.AsNoTracking()
            .Where(b => b.Id != buildingId)
            .Select(b => b.Id)
            .Take(MaxBuildingCount)
            .ToListAsync(cancellationToken);
        if (others.Count == 0) return;
        var existing = await db.BuildingTravels.AsNoTracking()
            .Where(t => t.FromBuildingId == buildingId || t.ToBuildingId == buildingId)
            .Select(t => new { t.FromBuildingId, t.ToBuildingId })
            .Take(MaxBuildingCount)
            .ToListAsync(cancellationToken);
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
        await db.SaveChangesAsync(cancellationToken);
    }
}
