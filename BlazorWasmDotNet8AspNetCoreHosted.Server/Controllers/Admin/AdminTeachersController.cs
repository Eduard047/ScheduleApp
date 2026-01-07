using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers;

[ApiController]
[Route("api/admin/teachers")]
// Контролер адміністратора для керування викладачами
public class AdminTeachersController(AppDbContext db) : ControllerBase
{

    // Форматує час для DTO.
    private static string T(TimeOnly t) => t.ToString("HH:mm");
    // Парсить час у форматі HH:mm.
    private static TimeOnly ParseTime(string s)
    {
        if (TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var v)) return v;
        return TimeOnly.Parse(s, CultureInfo.InvariantCulture);
    }
    // Перетворює викладача у DTO для списку.
    private static TeacherViewDto ToViewDto(
        Teacher t,
        List<TeacherCourseLoad> loads,
        List<TeacherWorkingHour> wh,
        List<int> supervisorModuleIds) =>
        new TeacherViewDto
        {
            Id = t.Id,
            FullName = t.FullName,
            ScientificDegree = t.ScientificDegree,
            AcademicTitle = t.AcademicTitle,
            DepartmentId = t.DepartmentId,
            ModuleIds = t.TeacherModules.Select(tm => tm.ModuleId).ToList(),
            SupervisorModuleIds = supervisorModuleIds,
            Loads = loads
                .Select(l => new TeacherLoadDto(l.CourseId, l.IsActive, l.ScheduledHours))
                .ToList(),
            WorkingHours = wh
                .Select(w => new TeacherWorkingHourDto((int)w.DayOfWeek, T(w.Start), T(w.End)))
                .ToList()
        };
    // Перетворює викладача у DTO для редагування.
    private static TeacherEditDto ToEditDto(
        Teacher t,
        List<int> moduleIds,
        List<int> supervisorModuleIds,
        List<TeacherCourseLoad> loads,
        List<TeacherWorkingHour> wh) =>
        new TeacherEditDto(
            id: t.Id,
            fullName: t.FullName,
            scientificDegree: t.ScientificDegree,
            academicTitle: t.AcademicTitle,
            departmentId: t.DepartmentId,
            moduleIds: moduleIds,
            supervisorModuleIds: supervisorModuleIds,
            loads: loads.Select(l => new TeacherLoadDto(l.CourseId, l.IsActive, l.ScheduledHours)).ToList(),
            workingHours: wh.Select(w => new TeacherWorkingHourDto((int)w.DayOfWeek, T(w.Start), T(w.End))).ToList()
        );
    [HttpGet]
    // Повертає список викладачів із пов'язаними даними.
    public async Task<ActionResult<List<TeacherViewDto>>> GetAll()
    {
        var teachers = await db.Teachers
            .AsNoTracking()
            .Include(t => t.TeacherModules)
            .ToListAsync();
        var ids = teachers.Select(t => t.Id).ToList();
        var loads = await db.TeacherCourseLoads
            .AsNoTracking()
            .Where(l => ids.Contains(l.TeacherId))
            .ToListAsync();
        var wh = await db.TeacherWorkingHours
            .AsNoTracking()
            .Where(w => ids.Contains(w.TeacherId))
            .ToListAsync();
        var supervisorLinks = await db.ModuleSupervisors
            .AsNoTracking()
            .Where(ms => ids.Contains(ms.TeacherId))
            .GroupBy(ms => ms.TeacherId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(x => x.ModuleId).ToList());
        var result = teachers
            .Select(t => ToViewDto(
                t,
                loads.Where(l => l.TeacherId == t.Id).ToList(),
                wh.Where(w => w.TeacherId == t.Id).ToList(),
                supervisorLinks.TryGetValue(t.Id, out var sup) ? sup : new List<int>()))
            .ToList();
        return Ok(result);
    }
    [HttpGet("{id:int}")]
    // Повертає дані викладача для форми редагування.
    public async Task<ActionResult<TeacherEditDto>> GetOne(int id)
    {
        var t = await db.Teachers
            .AsNoTracking()
            .Include(x => x.TeacherModules)
            .Include(x => x.ModuleSupervisions)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return NotFound(new { message = $"Викладача {id} не знайдено" });
        var moduleIds = t.TeacherModules.Select(tm => tm.ModuleId).ToList();
        var supervisorModuleIds = t.ModuleSupervisions.Select(ms => ms.ModuleId).ToList();
        var loads = await db.TeacherCourseLoads
            .AsNoTracking()
            .Where(l => l.TeacherId == id)
            .ToListAsync();
        var wh = await db.TeacherWorkingHours
            .AsNoTracking()
            .Where(w => w.TeacherId == id)
            .ToListAsync();
        return Ok(ToEditDto(t, moduleIds, supervisorModuleIds, loads, wh));
    }
    [HttpPost("upsert")]
    // Створює або оновлює викладача та його зв'язки.
    public async Task<ActionResult<int>> Upsert([FromBody] TeacherEditDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.FullName))
            return BadRequest(new { message = "ПІБ є обовʼязковим" });
        var normalizedDepartmentId = dto.DepartmentId is int depId && depId > 0 ? depId : (int?)null;
        if (normalizedDepartmentId is int departmentId)
        {
            var departmentExists = await db.Departments.AnyAsync(x => x.Id == departmentId);
            if (!departmentExists)
                return BadRequest(new { message = $"Кафедру {departmentId} не знайдено" });
        }
        Teacher entity;
        if (dto.Id is int id && id > 0)
        {
            var existing = await db.Teachers
                .Include(t => t.TeacherModules)
                .Include(t => t.ModuleSupervisions)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (existing is null)
                return NotFound(new { message = $"Викладача {id} не знайдено" });
            entity = existing;
            entity.FullName = dto.FullName;
            entity.ScientificDegree = dto.ScientificDegree;
            entity.AcademicTitle = dto.AcademicTitle;
            entity.DepartmentId = normalizedDepartmentId;
            db.TeacherModules.RemoveRange(entity.TeacherModules);
            db.ModuleSupervisors.RemoveRange(entity.ModuleSupervisions);
            await db.SaveChangesAsync();
            var newLinks = (dto.ModuleIds ?? new())
                .Distinct()
                .Select(mid => new TeacherModule { TeacherId = entity.Id, ModuleId = mid });
            await db.TeacherModules.AddRangeAsync(newLinks);
            var newSupLinks = (dto.SupervisorModuleIds ?? new())
                .Distinct()
                .Select(mid => new ModuleSupervisor { TeacherId = entity.Id, ModuleId = mid });
            await db.ModuleSupervisors.AddRangeAsync(newSupLinks);
        }
        else
        {
            entity = new Teacher
            {
                FullName = dto.FullName,
                ScientificDegree = dto.ScientificDegree,
                AcademicTitle = dto.AcademicTitle,
                DepartmentId = normalizedDepartmentId
            };
            db.Teachers.Add(entity);
            await db.SaveChangesAsync(); 
            if (dto.ModuleIds?.Count > 0)
            {
                var links = dto.ModuleIds.Distinct()
                    .Select(mid => new TeacherModule { TeacherId = entity.Id, ModuleId = mid });
                await db.TeacherModules.AddRangeAsync(links);
            }
            if (dto.SupervisorModuleIds?.Count > 0)
            {
                var supLinks = dto.SupervisorModuleIds.Distinct()
                    .Select(mid => new ModuleSupervisor { TeacherId = entity.Id, ModuleId = mid });
                await db.ModuleSupervisors.AddRangeAsync(supLinks);
            }
        }
        var oldLoads = await db.TeacherCourseLoads
            .Where(l => l.TeacherId == entity.Id)
            .ToListAsync();
        db.TeacherCourseLoads.RemoveRange(oldLoads);
        await db.SaveChangesAsync();
        if (dto.Loads?.Count > 0)
        {
            var toInsert = dto.Loads.Select(l =>
            {
                var prev = oldLoads.FirstOrDefault(p => p.CourseId == l.CourseId);
                return new TeacherCourseLoad
                {
                    TeacherId = entity.Id,
                    CourseId = l.CourseId,
                    ScheduledHours = prev?.ScheduledHours ?? l.ScheduledHours,
                    IsActive = l.IsActive
                };
            });
            await db.TeacherCourseLoads.AddRangeAsync(toInsert);
        }
        var oldWh = await db.TeacherWorkingHours
            .Where(w => w.TeacherId == entity.Id)
            .ToListAsync();
        db.TeacherWorkingHours.RemoveRange(oldWh);
        await db.SaveChangesAsync();
        if (dto.WorkingHours?.Count > 0)
        {
            var toInsertWh = dto.WorkingHours.Select(w => new TeacherWorkingHour
            {
                TeacherId = entity.Id,
                DayOfWeek = (DayOfWeek)w.DayOfWeek,
                Start = ParseTime(w.Start),
                End = ParseTime(w.End)
            });
            await db.TeacherWorkingHours.AddRangeAsync(toInsertWh);
        }
        await db.SaveChangesAsync();
        return Ok(entity.Id);
    }
    [HttpDelete("{id:int}")]
    [RequireDeletionConfirmation("викладача")]
    // Видаляє викладача з опційним примусовим відв'язуванням.
    public async Task<IActionResult> Delete(int id, [FromQuery] bool force = false)
    {
        var t = await db.Teachers.FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return NotFound(new { message = $"Викладача {id} не знайдено" });
        var used = await db.ScheduleItems.AnyAsync(s => s.TeacherId == id);
        if (used && !force)
            return Conflict(new { message = "Викладач використовується у розкладі" });
        if (used && force)
        {
            await db.ScheduleItems
                .Where(s => s.TeacherId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.TeacherId, (int?)null));
        }
        db.TeacherCourseLoads.RemoveRange(db.TeacherCourseLoads.Where(l => l.TeacherId == id));
        db.TeacherWorkingHours.RemoveRange(db.TeacherWorkingHours.Where(w => w.TeacherId == id));
        db.TeacherModules.RemoveRange(db.TeacherModules.Where(tm => tm.TeacherId == id));
        db.ModuleSupervisors.RemoveRange(db.ModuleSupervisors.Where(ms => ms.TeacherId == id));
        db.Teachers.Remove(t);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
