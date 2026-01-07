using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;

[ApiController]
[Route("api/admin/module-sequence")]
// Контролер адміністратора для послідовностей модулів
public sealed class AdminModuleSequenceController(AppDbContext db) : ControllerBase
{
    [HttpGet("{courseId:int}")]
    // Повертає послідовність модулів для курсу.
    public async Task<ActionResult<ModuleSequenceConfigDto>> Get(int courseId)
    {
        var courseExists = await db.Courses.AsNoTracking().AnyAsync(c => c.Id == courseId);
        if (!courseExists)
        {
            return NotFound(new { message = "Курс не знайдено." });
        }
        var main = await db.ModuleSequenceItems
            .AsNoTracking()
            .Where(x => x.CourseId == courseId)
            .OrderBy(x => x.Order)
            .Select(x => new ModuleSequenceItemDto(
                x.Id,
                x.ModuleId,
                x.Module.Code,
                x.Module.Title,
                x.Order))
            .ToListAsync();
        var fillers = await db.ModuleFillers
            .AsNoTracking()
            .Where(x => x.CourseId == courseId)
            .Select(x => x.ModuleId)
            .ToListAsync();
        return new ModuleSequenceConfigDto(courseId, main, fillers);
    }
    [HttpPost("save")]
    // Зберігає послідовність основних і заповнювальних модулів.
    public async Task<IActionResult> Save([FromBody] ModuleSequenceSaveRequestDto dto)
    {
        if (dto is null)
        {
            return BadRequest(new { message = "Невірний запит." });
        }
        var course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(c => c.Id == dto.CourseId);
        if (course is null)
        {
            return NotFound(new { message = "Курс не знайдено." });
        }
        var moduleIds = await db.ModuleCourses.AsNoTracking()
            .Where(mc => mc.CourseId == dto.CourseId)
            .Select(mc => mc.ModuleId)
            .ToListAsync();
        var invalidMain = dto.MainModuleIds.Except(moduleIds).ToList();
        var invalidFillers = dto.FillerModuleIds.Except(moduleIds).ToList();
        if (invalidMain.Count > 0 || invalidFillers.Count > 0)
        {
            return BadRequest(new { message = "Є модулі, що не належать до вибраного курсу." });
        }
        var orderedMain = new List<int>();
        var seen = new HashSet<int>();
        foreach (var mid in dto.MainModuleIds)
        {
            if (seen.Add(mid))
            {
                orderedMain.Add(mid);
            }
        }
        var fillerUnique = dto.FillerModuleIds.Distinct().ToList();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.ModuleSequenceItems
            .Where(x => x.CourseId == dto.CourseId)
            .ExecuteDeleteAsync();
        await db.ModuleFillers
            .Where(x => x.CourseId == dto.CourseId)
            .ExecuteDeleteAsync();
        for (int i = 0; i < orderedMain.Count; i++)
        {
            db.ModuleSequenceItems.Add(new ModuleSequenceItem
            {
                CourseId = dto.CourseId,
                ModuleId = orderedMain[i],
                Order = i
            });
        }
        foreach (var mid in fillerUnique)
        {
            db.ModuleFillers.Add(new ModuleFiller
            {
                CourseId = dto.CourseId,
                ModuleId = mid
            });
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return NoContent();
    }
}
