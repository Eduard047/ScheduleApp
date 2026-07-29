using System.Collections.Generic;
using System.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;

[ApiController]
[Route("api/admin/courses")]
// Контролер адміністратора для курсів
public class AdminCoursesController(AppDbContext db) : ControllerBase
{
    private static readonly DateOnly EarliestAcademicPeriodStartDate = new(2000, 1, 1);
    private static readonly DateOnly LatestAcademicPeriodStartDate = new(2100, 12, 31);

    [HttpGet]
    // Повертає список курсів.
    public async Task<IReadOnlyList<CourseEditDto>> List()
        => await db.Courses.AsNoTracking()
            .OrderBy(c => c.Id)
            .Select(c => new CourseEditDto(c.Id, c.Name, c.DurationWeeks, c.AcademicPeriodStartDate))
            .ToListAsync();
    [HttpPost("upsert")]
    // Створює або оновлює курс.
    public async Task<ActionResult<int>> Upsert(CourseEditDto dto)
    {
        var name = dto.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "Назва є обовʼязковою" });
        if (name.Length > CourseEditDto.NameMaxLength)
        {
            return BadRequest(new
            {
                message = $"Назва курсу не може перевищувати {CourseEditDto.NameMaxLength} символів."
            });
        }
        if (dto.DurationWeeks is < 1 or > 520)
            return BadRequest(new { message = "Тривалість курсу має бути від 1 до 520 тижнів." });
        if (dto.AcademicPeriodStartDate is not DateOnly academicPeriodStartDate)
        {
            return BadRequest(new { message = "Початок поточного навчального періоду є обов'язковим." });
        }
        if (academicPeriodStartDate < EarliestAcademicPeriodStartDate
            || academicPeriodStartDate > LatestAcademicPeriodStartDate)
        {
            return BadRequest(new
            {
                message = "Початок навчального періоду має бути в межах від 2000-01-01 до 2100-12-31."
            });
        }
        if (dto.Id is int id && id > 0)
        {
            var c = await db.Courses.FindAsync(id);
            if (c is null) return NotFound(new { message = "Курс не знайдено." });
            c.Name = name;
            c.DurationWeeks = dto.DurationWeeks;
            c.AcademicPeriodStartDate = dto.AcademicPeriodStartDate;
            await db.SaveChangesAsync();
            return Ok(c.Id);
        }
        else
        {
            var c = new Course
            {
                Name = name,
                DurationWeeks = dto.DurationWeeks,
                AcademicPeriodStartDate = dto.AcademicPeriodStartDate
            };
            db.Courses.Add(c);
            await db.SaveChangesAsync();
            return Ok(c.Id);
        }
    }
    [HttpDelete("{id:int}")]
    [RequireDeletionConfirmation("курс")]
    // Видаляє курс, за потреби з примусовим очищенням залежностей.
    public async Task<IActionResult> Delete(int id, [FromQuery] bool force = false)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
        if (!await db.Courses.AsNoTracking().AnyAsync(c => c.Id == id))
        {
            return NotFound();
        }
        var hasDrafts = await db.TeacherDraftItems
            .AsNoTracking()
            .AnyAsync(d => d.Group.CourseId == id || d.Module.CourseId == id);
        if (hasDrafts)
        {
            return Conflict(new
            {
                message = "Курс або його модулі використовуються у чернетках. Спочатку перенесіть або видаліть пов'язані чернетки."
            });
        }
        var used = await db.Groups.AnyAsync(g => g.CourseId == id)
                   || await db.Modules.AnyAsync(m => m.CourseId == id)
                   || await db.ModuleCourses.AnyAsync(mc => mc.CourseId == id)
                   || await db.ModulePlans.AnyAsync(p => p.CourseId == id)
                   || await db.TeacherCourseLoads.AnyAsync(l => l.CourseId == id)
                   || await db.ScheduleItems.AnyAsync(s => s.Group.CourseId == id);
        if (used && !force)
            return Conflict(new { message = "Курс використовується групами/модулями/розкладом" });

            if (used && force)
            {
                var linkedModuleIds = await db.ModuleCourses
                    .Where(mc => mc.CourseId == id)
                    .Select(mc => mc.ModuleId)
                    .Distinct()
                    .ToListAsync();
                var primaryModuleIds = await db.Modules
                    .Where(module => module.CourseId == id)
                    .Select(module => module.Id)
                    .ToListAsync();
                var moduleIdsForCourse = linkedModuleIds
                    .Concat(primaryModuleIds)
                    .Distinct()
                    .ToList();
                var moduleCourseRows = await db.ModuleCourses.AsNoTracking()
                    .Where(link => moduleIdsForCourse.Contains(link.ModuleId))
                    .Select(link => new { link.ModuleId, link.CourseId })
                    .ToListAsync();
                var moduleCourseMap = moduleCourseRows
                    .GroupBy(row => row.ModuleId)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(row => row.CourseId).Distinct().OrderBy(courseId => courseId).ToList());
                var modules = await db.Modules
                    .Where(module => moduleIdsForCourse.Contains(module.Id))
                    .ToListAsync();
                var alternativeCourseIdsForPrimaryModules = modules
                    .Where(module => module.CourseId == id)
                    .SelectMany(module => moduleCourseMap
                        .GetValueOrDefault(module.Id, new List<int>())
                        .Where(courseId => courseId != id))
                    .Distinct()
                    .ToList();
                var alternativeModules = await db.Modules.AsNoTracking()
                    .Where(module => alternativeCourseIdsForPrimaryModules.Contains(module.CourseId))
                    .Select(module => new { module.Id, module.CourseId, module.Code })
                    .ToListAsync();
                var safeAlternativeCourseByModule = new Dictionary<int, int>();
                foreach (var module in modules.Where(module => module.CourseId == id))
                {
                    var alternativeCourseIds = moduleCourseMap
                        .GetValueOrDefault(module.Id, new List<int>())
                        .Where(courseId => courseId != id)
                        .ToList();
                    foreach (var alternativeCourseId in alternativeCourseIds)
                    {
                        var normalizedCode = module.Code.Trim().ToUpperInvariant();
                        var duplicateCodeExists = alternativeModules.Any(candidate =>
                            candidate.Id != module.Id
                            && candidate.CourseId == alternativeCourseId
                            && string.Equals(
                                candidate.Code.Trim(),
                                normalizedCode,
                                StringComparison.OrdinalIgnoreCase));
                        if (!duplicateCodeExists)
                        {
                            safeAlternativeCourseByModule[module.Id] = alternativeCourseId;
                            break;
                        }
                    }

                    if (alternativeCourseIds.Count > 0
                        && !safeAlternativeCourseByModule.ContainsKey(module.Id))
                    {
                        return Conflict(new
                        {
                            message = $"Неможливо видалити курс: для модуля з кодом '{module.Code}' у всіх альтернативних курсах уже існує окремий модуль із таким кодом."
                        });
                    }
                }

                await db.ScheduleItems.Where(s => s.Group.CourseId == id).ExecuteDeleteAsync();
                await db.ModulePlans.Where(p => p.CourseId == id).ExecuteDeleteAsync();
                await db.ModuleSequenceItems.Where(si => si.CourseId == id).ExecuteDeleteAsync();
                await db.ModuleFillers.Where(f => f.CourseId == id).ExecuteDeleteAsync();
                await db.TeacherCourseLoads.Where(l => l.CourseId == id).ExecuteDeleteAsync();
                await db.TimeSlots.Where(ts => ts.CourseId == id).ExecuteDeleteAsync();
                await db.LunchConfigs.Where(lc => lc.CourseId == id).ExecuteDeleteAsync();
                await db.PreferredFirstSlotLimitConfigs.Where(pc => pc.CourseId == id).ExecuteDeleteAsync();
                if (moduleIdsForCourse.Count > 0)
                {
                    var modulesToDelete = new List<int>();
                    foreach (var module in modules)
                    {
                        if (module.CourseId == id)
                        {
                            if (safeAlternativeCourseByModule.TryGetValue(module.Id, out var alternativeCourseId))
                            {
                                module.CourseId = alternativeCourseId;
                            }
                            else
                            {
                                modulesToDelete.Add(module.Id);
                            }
                        }
                    }
                    if (modulesToDelete.Count > 0)
                    {
                        await db.TeacherModules.Where(tm => modulesToDelete.Contains(tm.ModuleId)).ExecuteDeleteAsync();
                        await db.ModuleRooms.Where(mr => modulesToDelete.Contains(mr.ModuleId)).ExecuteDeleteAsync();
                        await db.ModuleBuildings.Where(mb => modulesToDelete.Contains(mb.ModuleId)).ExecuteDeleteAsync();
                        await db.ModuleTopics.Where(mt => modulesToDelete.Contains(mt.ModuleId)).ExecuteDeleteAsync();
                    }
                    await db.SaveChangesAsync();
                    if (modulesToDelete.Count > 0)
                    {
                        await db.Modules.Where(m => modulesToDelete.Contains(m.Id)).ExecuteDeleteAsync();
                    }
                }
                await db.ModuleCourses.Where(mc => mc.CourseId == id).ExecuteDeleteAsync();
                await db.Groups.Where(g => g.CourseId == id).ExecuteDeleteAsync();
            }
            var rows = await db.Courses.Where(c => c.Id == id).ExecuteDeleteAsync();
            if (rows == 0)
            {
                await tx.RollbackAsync();
                return NotFound();
            }
            await tx.CommitAsync();
            return NoContent();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}
