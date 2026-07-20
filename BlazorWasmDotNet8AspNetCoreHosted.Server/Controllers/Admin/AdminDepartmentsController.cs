using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;

// Контролер адміністратора для керування кафедрами
[ApiController]
[Route("api/admin/departments")]
public sealed class AdminDepartmentsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    // Повертає список кафедр.
    public async Task<ActionResult<List<DepartmentEditDto>>> GetAll()
    {
        var items = await db.Departments
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new DepartmentEditDto(x.Id, x.Name, x.IsActive))
            .ToListAsync();
        return Ok(items);
    }
    [HttpPost("upsert")]
    // Створює або оновлює кафедру з перевіркою дублю.
    public async Task<ActionResult<int>> Upsert([FromBody] DepartmentEditDto dto)
    {
        var name = (dto.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { message = "Назва кафедри є обов'язковою." });
        }
        var id = dto.Id ?? 0;
        var duplicate = await db.Departments.AnyAsync(x => x.Id != id && x.Name == name);
        if (duplicate)
        {
            return Conflict(new { message = "Кафедра з такою назвою вже існує." });
        }
        Department entity;
        if (id > 0)
        {
            var existing = await db.Departments.FirstOrDefaultAsync(x => x.Id == id);
            if (existing is null) return NotFound(new { message = $"Кафедру {id} не знайдено." });
            entity = existing;
        }
        else
        {
            entity = new Department();
            db.Departments.Add(entity);
        }
        entity.Name = name;
        entity.IsActive = dto.IsActive;
        await db.SaveChangesAsync();
        return Ok(entity.Id);
    }
    [HttpDelete("{id:int}")]
    [RequireDeletionConfirmation("кафедру")]
    // Видаляє кафедру, опціонально з очищенням зв'язків.
    public async Task<IActionResult> Delete(int id, [FromQuery] bool force = false)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var entity = await db.Departments.FirstOrDefaultAsync(x => x.Id == id);
            if (entity is null) return NotFound();
            var usedByTeachers = await db.Teachers.AnyAsync(x => x.DepartmentId == id);
            var usedByTopics = await db.ModuleTopics.AnyAsync(x => x.DepartmentId == id);
            if ((usedByTeachers || usedByTopics) && !force)
            {
                return Conflict(new
                {
                    message = "Кафедра використовується викладачами або темами занять. Для видалення потрібен параметр force=true."
                });
            }
            if (force)
            {
                await db.Teachers
                    .Where(x => x.DepartmentId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, (int?)null));
                await db.ModuleTopics
                    .Where(x => x.DepartmentId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, (int?)null));
            }
            db.Departments.Remove(entity);
            await db.SaveChangesAsync();
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
