using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;

[ApiController]
[Route("api/admin/plans")]
// Контролер адміністратора для планів модулів
public sealed class AdminPlansController : ControllerBase
{
    private const int MaxPlanAggregationRowCount = 50_000;
    private static readonly SemaphoreSlim PlanUpsertGate = new(1, 1);
    private readonly AppDbContext _db;
    public AdminPlansController(AppDbContext db) => _db = db;
    [HttpGet("module/{moduleId:int}")]
    // Повертає план годин для модуля з урахуванням курсу.
    public async Task<ActionResult<List<CourseModulePlanDto>>> GetByModule(
        int moduleId,
        [FromQuery] int? courseId = null,
        CancellationToken cancellationToken = default)
    {
        var module = await _db.Modules
            .AsNoTracking()
            .Include(m => m.ModuleCourses)
            .FirstOrDefaultAsync(m => m.Id == moduleId, cancellationToken);
        if (module is null)
            return NotFound(new { message = "Модуль не знайдено" });
        var linkedCourseIds = module.ModuleCourses
            .Select(mc => mc.CourseId)
            .ToHashSet();
        linkedCourseIds.Add(module.CourseId);
        if (linkedCourseIds.Count == 0)
            return NotFound(new { message = "Модуль не прив'язаний до курсу" });
        int resolvedCourseId;
        if (courseId is int requested && requested > 0)
        {
            if (!linkedCourseIds.Contains(requested))
                return NotFound(new { message = "Модуль не прив'язаний до зазначеного курсу" });
            resolvedCourseId = requested;
        }
        else
        {
            resolvedCourseId = module.CourseId;
        }
        var lessonTypes = await _db.LessonTypes
            .AsNoTracking()
            .Select(t => new { t.Id, t.Code, t.CountInPlan })
            .ToListAsync(cancellationToken);
        var excludePlanIds = lessonTypes
            .Where(t =>
                !t.CountInPlan
                || string.Equals(t.Code, "CANCELED", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Code, "RESCHEDULED", System.StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Id)
            .ToHashSet();
        var scheduledRows = await _db.ScheduleItems
            .Where(si => si.ModuleId == moduleId
                         && si.Group.CourseId == resolvedCourseId
                         && !excludePlanIds.Contains(si.LessonTypeId))
            .Take(MaxPlanAggregationRowCount + 1)
            .Select(si => new CurriculumScheduleRow(
                si.Id,
                si.Group.CourseId,
                si.BatchKey,
                si.Date,
                si.StartTime,
                si.EndTime,
                si.GroupId,
                si.ModuleId,
                si.LessonTypeId,
                si.ModuleTopicId,
                si.TeacherId,
                si.RoomId,
                si.IsSelfStudy))
            .ToListAsync(cancellationToken);
        if (scheduledRows.Count > MaxPlanAggregationRowCount)
        {
            return UnprocessableEntity(new
            {
                message = $"Агрегація плану охоплює понад {MaxPlanAggregationRowCount} рядків розкладу. Скоротіть історичний обсяг даних."
            });
        }
        var scheduled = CurriculumScheduleAggregation
            .CollapseForPlan(scheduledRows)
            .Sum(row => CurriculumScheduleAggregation.ScheduledHours(row.StartTime, row.EndTime));
        var plan = await _db.ModulePlans.AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.CourseId == resolvedCourseId && p.ModuleId == moduleId,
                cancellationToken);
        var row = new CourseModulePlanDto(
            CourseId: resolvedCourseId,
            ModuleId: moduleId,
            TargetHours: plan?.TargetHours ?? 0,
            ScheduledHours: scheduled,
            IsActive: plan?.IsActive ?? false
        );
        return Ok(new List<CourseModulePlanDto> { row });
    }
    [HttpPost("module/{moduleId:int}/upsert")]
    [RequestSizeLimit(16 * 1024)]
    // Зберігає план годин для модуля та курсу.
    public async Task<IActionResult> Upsert(
        int moduleId,
        [FromBody] List<SaveCourseModulePlanDto> items,
        [FromQuery] int? courseId = null,
        CancellationToken cancellationToken = default)
    {
        if (items is not { Count: 1 })
            return BadRequest(new { message = "Запит має містити рівно один план модуля." });
        var dto = items[0];
        if (dto.TargetHours is < 0 or > CurriculumInputLimits.PlanHoursMax)
            return BadRequest(new { message = $"Планові години мають бути в діапазоні від 0 до {CurriculumInputLimits.PlanHoursMax}." });

        await PlanUpsertGate.WaitAsync(cancellationToken);
        try
        {
            var module = await _db.Modules
                .Include(m => m.ModuleCourses)
                .FirstOrDefaultAsync(m => m.Id == moduleId, cancellationToken);
            if (module is null)
                return NotFound(new { message = "Модуль не знайдено" });
            var linkedCourseIds = module.ModuleCourses
                .Select(mc => mc.CourseId)
                .ToHashSet();
            linkedCourseIds.Add(module.CourseId);
            if (linkedCourseIds.Count == 0)
                return BadRequest(new { message = "Модуль не прив'язаний до жодного курсу" });
            int resolvedCourseId;
            if (courseId is int requested && requested > 0)
            {
                if (!linkedCourseIds.Contains(requested))
                    return NotFound(new { message = "Модуль не прив'язаний до зазначеного курсу" });
                resolvedCourseId = requested;
            }
            else
            {
                resolvedCourseId = module.CourseId;
            }
            var plan = await _db.ModulePlans
                .FirstOrDefaultAsync(
                    p => p.CourseId == resolvedCourseId && p.ModuleId == moduleId,
                    cancellationToken);
            if (plan is null)
            {
                _db.ModulePlans.Add(new ModulePlan
                {
                    CourseId = resolvedCourseId,
                    ModuleId = moduleId,
                    TargetHours = dto.TargetHours,
                    ScheduledHours = 0,
                    IsActive = dto.IsActive
                });
            }
            else
            {
                plan.TargetHours = dto.TargetHours;
                plan.IsActive = dto.IsActive;
            }
            module.Credits = Math.Round(dto.TargetHours / 30m, 2);
            await _db.SaveChangesAsync(cancellationToken);
            return NoContent();
        }
        finally
        {
            PlanUpsertGate.Release();
        }
    }

}
