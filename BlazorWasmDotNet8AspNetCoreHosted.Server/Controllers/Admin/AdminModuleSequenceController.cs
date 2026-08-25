using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
                x.Order,
                x.GroupOrder))
            .ToListAsync();
        var fillers = await db.ModuleFillers
            .AsNoTracking()
            .Where(x => x.CourseId == courseId)
            .Select(x => x.ModuleId)
            .ToListAsync();
        return new ModuleSequenceConfigDto(courseId, main, fillers);
    }
    [HttpPost("save")]
    [RequestSizeLimit(131_072)]
    [EnableRateLimiting("module-sequence-save")]
    // Зберігає послідовність основних і заповнювальних модулів.
    public async Task<IActionResult> Save(
        [FromBody] ModuleSequenceSaveRequestDto dto,
        [FromServices] ExpensiveOperationGate operationGate,
        CancellationToken cancellationToken)
    {
        if (dto is null || dto.MainModules is null || dto.FillerModuleIds is null)
        {
            return BadRequest(new { message = "Невірний запит." });
        }
        if (dto.MainModules.Count > CurriculumInputLimits.ModuleAssociationCountMax
            || dto.FillerModuleIds.Count > CurriculumInputLimits.ModuleAssociationCountMax)
        {
            return BadRequest(new
            {
                message = $"Послідовність може містити не більше {CurriculumInputLimits.ModuleAssociationCountMax} основних і {CurriculumInputLimits.ModuleAssociationCountMax} заповнювальних модулів."
            });
        }

        var orderedMain = new List<ModuleSequenceSaveItemDto>(dto.MainModules.Count);
        var requestedModuleIds = new HashSet<int>(
            dto.MainModules.Count + dto.FillerModuleIds.Count);
        foreach (var item in dto.MainModules)
        {
            if (item.ModuleId <= 0)
            {
                return BadRequest(new { message = "Ідентифікатори модулів мають бути додатними." });
            }
            if (requestedModuleIds.Add(item.ModuleId))
            {
                var normalizedGroupOrder = item.GroupOrder > 0 ? item.GroupOrder : 1;
                orderedMain.Add(new ModuleSequenceSaveItemDto(item.ModuleId, normalizedGroupOrder));
            }
        }
        var fillerUnique = new HashSet<int>(dto.FillerModuleIds.Count);
        foreach (var moduleId in dto.FillerModuleIds)
        {
            if (moduleId <= 0)
            {
                return BadRequest(new { message = "Ідентифікатори модулів мають бути додатними." });
            }
            fillerUnique.Add(moduleId);
            requestedModuleIds.Add(moduleId);
        }

        using var lease = await operationGate.TryEnterAsync(
            ExpensiveOperationKind.ModuleSequenceSave,
            cancellationToken);
        if (lease is null)
        {
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Забагато одночасних змін послідовності",
                detail: "Дочекайтеся завершення поточних змін і повторіть запит.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var course = await db.Courses
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == dto.CourseId, cancellationToken);
        if (course is null)
        {
            return NotFound(new { message = "Курс не знайдено." });
        }
        var moduleIds = (await db.ModuleCourses.AsNoTracking()
            .Where(mc => mc.CourseId == dto.CourseId
                         && requestedModuleIds.Contains(mc.ModuleId))
            .Select(mc => mc.ModuleId)
            .ToListAsync(cancellationToken))
            .ToHashSet();
        if (moduleIds.Count != requestedModuleIds.Count)
        {
            return BadRequest(new { message = "Є модулі, що не належать до вибраного курсу." });
        }
        await db.ModuleSequenceItems
            .Where(x => x.CourseId == dto.CourseId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.ModuleFillers
            .Where(x => x.CourseId == dto.CourseId)
            .ExecuteDeleteAsync(cancellationToken);
        for (int i = 0; i < orderedMain.Count; i++)
        {
            var entry = orderedMain[i];
            db.ModuleSequenceItems.Add(new ModuleSequenceItem
            {
                CourseId = dto.CourseId,
                ModuleId = entry.ModuleId,
                Order = i,
                GroupOrder = entry.GroupOrder
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
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return NoContent();
    }
}
