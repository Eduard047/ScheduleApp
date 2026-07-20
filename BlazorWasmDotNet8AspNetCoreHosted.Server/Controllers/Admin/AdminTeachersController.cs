using System.Globalization;
using System.Data;
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
    private static bool TryParseTime(string? value, out TimeOnly time)
        => TimeOnly.TryParseExact(
            value,
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out time);
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
        var supervisorRows = await db.ModuleSupervisors
            .AsNoTracking()
            .Where(ms => ids.Contains(ms.TeacherId))
            .Select(ms => new { ms.TeacherId, ms.ModuleId })
            .ToListAsync();
        var supervisorLinks = supervisorRows
            .GroupBy(row => row.TeacherId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.ModuleId).ToList());
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
        var fullName = dto.FullName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fullName))
            return BadRequest(new { message = "ПІБ є обовʼязковим" });
        if (fullName.Length > 256)
            return BadRequest(new { message = "ПІБ не може перевищувати 256 символів." });
        var moduleIds = (dto.ModuleIds ?? new List<int>()).Distinct().ToList();
        var supervisorModuleIds = (dto.SupervisorModuleIds ?? new List<int>()).Distinct().ToList();
        if (moduleIds.Count > 500 || supervisorModuleIds.Count > 500)
            return BadRequest(new { message = "Для одного викладача можна вибрати не більше 500 модулів у кожному переліку." });
        if (moduleIds.Any(moduleId => moduleId <= 0) || supervisorModuleIds.Any(moduleId => moduleId <= 0))
            return BadRequest(new { message = "Ідентифікатори модулів мають бути додатними числами." });
        var allModuleIds = moduleIds.Concat(supervisorModuleIds).Distinct().ToList();
        var existingModuleIds = await db.Modules
            .Where(module => allModuleIds.Contains(module.Id))
            .Select(module => module.Id)
            .ToListAsync();
        var missingModuleIds = allModuleIds.Except(existingModuleIds).OrderBy(moduleId => moduleId).ToList();
        if (missingModuleIds.Count > 0)
            return BadRequest(new { message = $"Модулі не знайдено: {string.Join(", ", missingModuleIds)}." });

        var loads = dto.Loads ?? new List<TeacherLoadDto>();
        if (loads.Count > 200 || loads.Any(load => load.CourseId <= 0 || load.ScheduledHours < 0))
            return BadRequest(new { message = "Навантаження містить некоректний курс або від'ємну кількість годин." });
        if (loads.Select(load => load.CourseId).Distinct().Count() != loads.Count)
            return BadRequest(new { message = "Навантаження містить повторювані курси." });
        var loadCourseIds = loads.Select(load => load.CourseId).ToList();
        var existingLoadCourseIds = await db.Courses
            .Where(course => loadCourseIds.Contains(course.Id))
            .Select(course => course.Id)
            .ToListAsync();
        var missingLoadCourseIds = loadCourseIds.Except(existingLoadCourseIds).OrderBy(courseId => courseId).ToList();
        if (missingLoadCourseIds.Count > 0)
            return BadRequest(new { message = $"Курси навантаження не знайдено: {string.Join(", ", missingLoadCourseIds)}." });

        var workingHours = dto.WorkingHours ?? new List<TeacherWorkingHourDto>();
        if (workingHours.Count > 100)
            return BadRequest(new { message = "Для одного викладача можна вказати не більше 100 робочих інтервалів." });
        var parsedWorkingHours = new List<(DayOfWeek Day, TimeOnly Start, TimeOnly End)>(workingHours.Count);
        for (var index = 0; index < workingHours.Count; index++)
        {
            var row = workingHours[index];
            if (!Enum.IsDefined(typeof(DayOfWeek), row.DayOfWeek)
                || !TryParseTime(row.Start, out var start)
                || !TryParseTime(row.End, out var end)
                || end <= start)
            {
                return BadRequest(new
                {
                    message = $"Робочий інтервал #{index + 1} має некоректний день або час. Використовуйте формат HH:mm, кінець має бути пізніше за початок."
                });
            }
            parsedWorkingHours.Add(((DayOfWeek)row.DayOfWeek, start, end));
        }
        foreach (var dayRows in parsedWorkingHours.GroupBy(row => row.Day))
        {
            var ordered = dayRows.OrderBy(row => row.Start).ThenBy(row => row.End).ToList();
            if (ordered.Zip(ordered.Skip(1), (left, right) => left.End > right.Start).Any(overlap => overlap))
                return BadRequest(new { message = $"Робочі інтервали за {dayRows.Key} перетинаються." });
        }

        var normalizedDepartmentId = dto.DepartmentId is int depId && depId > 0 ? depId : (int?)null;
        if (normalizedDepartmentId is int departmentId)
        {
            var departmentExists = await db.Departments.AnyAsync(x => x.Id == departmentId);
            if (!departmentExists)
                return BadRequest(new { message = $"Кафедру {departmentId} не знайдено" });
        }
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
        Teacher entity;
        if (dto.Id is int id && id > 0)
        {
            var existing = await db.Teachers
                .Include(t => t.TeacherModules)
                .Include(t => t.ModuleSupervisions)
                .FirstOrDefaultAsync(t => t.Id == id);
            if (existing is null)
                return NotFound(new { message = $"Викладача {id} не знайдено" });
            var workingHoursViolation = await FindWorkingHoursViolationAsync(id, parsedWorkingHours);
            if (workingHoursViolation is not null)
                return Conflict(new { message = workingHoursViolation });
            entity = existing;
            entity.FullName = fullName;
            entity.ScientificDegree = dto.ScientificDegree;
            entity.AcademicTitle = dto.AcademicTitle;
            entity.DepartmentId = normalizedDepartmentId;
            db.TeacherModules.RemoveRange(entity.TeacherModules);
            db.ModuleSupervisors.RemoveRange(entity.ModuleSupervisions);
            await db.SaveChangesAsync();
            var newLinks = moduleIds
                .Select(mid => new TeacherModule { TeacherId = entity.Id, ModuleId = mid });
            await db.TeacherModules.AddRangeAsync(newLinks);
            var newSupLinks = supervisorModuleIds
                .Select(mid => new ModuleSupervisor { TeacherId = entity.Id, ModuleId = mid });
            await db.ModuleSupervisors.AddRangeAsync(newSupLinks);
        }
        else
        {
            entity = new Teacher
            {
                FullName = fullName,
                ScientificDegree = dto.ScientificDegree,
                AcademicTitle = dto.AcademicTitle,
                DepartmentId = normalizedDepartmentId
            };
            db.Teachers.Add(entity);
            await db.SaveChangesAsync(); 
            if (moduleIds.Count > 0)
            {
                var links = moduleIds
                    .Select(mid => new TeacherModule { TeacherId = entity.Id, ModuleId = mid });
                await db.TeacherModules.AddRangeAsync(links);
            }
            if (supervisorModuleIds.Count > 0)
            {
                var supLinks = supervisorModuleIds
                    .Select(mid => new ModuleSupervisor { TeacherId = entity.Id, ModuleId = mid });
                await db.ModuleSupervisors.AddRangeAsync(supLinks);
            }
        }
        var oldLoads = await db.TeacherCourseLoads
            .Where(l => l.TeacherId == entity.Id)
            .ToListAsync();
        db.TeacherCourseLoads.RemoveRange(oldLoads);
        await db.SaveChangesAsync();
        if (loads.Count > 0)
        {
            var toInsert = loads.Select(l =>
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
        if (parsedWorkingHours.Count > 0)
        {
            var toInsertWh = parsedWorkingHours.Select(w => new TeacherWorkingHour
            {
                TeacherId = entity.Id,
                DayOfWeek = w.Day,
                Start = w.Start,
                End = w.End
            });
            await db.TeacherWorkingHours.AddRangeAsync(toInsertWh);
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return Ok(entity.Id);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
    [HttpDelete("{id:int}")]
    [RequireDeletionConfirmation("викладача")]
    // Видаляє викладача з опційним примусовим відв'язуванням.
    public async Task<IActionResult> Delete(int id, [FromQuery] bool force = false)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            var t = await db.Teachers.FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return NotFound(new { message = $"Викладача {id} не знайдено" });
            var usedInSchedule = await db.ScheduleItems.AnyAsync(s => s.TeacherId == id);
            var usedInDrafts = await db.TeacherDraftItems.AnyAsync(draft => draft.TeacherId == id);
            if ((usedInSchedule || usedInDrafts) && !force)
                return Conflict(new { message = "Викладач використовується у розкладі або чернетках." });
            if (force)
            {
                var requiredInSchedule = await db.ScheduleItems.AnyAsync(item =>
                    item.TeacherId == id
                    && !item.IsSelfStudy
                    && item.LessonType.RequiresTeacher);
                var requiredInDrafts = await db.TeacherDraftItems.AnyAsync(item =>
                    item.TeacherId == id
                    && !item.IsSelfStudy
                    && item.LessonType.RequiresTeacher);
                if (requiredInSchedule || requiredInDrafts)
                {
                    return Conflict(new
                    {
                        message = "Неможливо видалити викладача, доки він призначений заняттям, для яких викладач є обов'язковим. Спочатку перепризначте ці заняття."
                    });
                }
                await db.ScheduleItems
                    .Where(s => s.TeacherId == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.TeacherId, (int?)null));
                await db.TeacherDraftItems
                    .Where(draft => draft.TeacherId == id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(draft => draft.TeacherId, (int?)null));
            }
            db.TeacherCourseLoads.RemoveRange(db.TeacherCourseLoads.Where(l => l.TeacherId == id));
            db.TeacherWorkingHours.RemoveRange(db.TeacherWorkingHours.Where(w => w.TeacherId == id));
            db.TeacherModules.RemoveRange(db.TeacherModules.Where(tm => tm.TeacherId == id));
            db.ModuleSupervisors.RemoveRange(db.ModuleSupervisors.Where(ms => ms.TeacherId == id));
            db.Teachers.Remove(t);
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

    private async Task<string?> FindWorkingHoursViolationAsync(
        int teacherId,
        IReadOnlyCollection<(DayOfWeek Day, TimeOnly Start, TimeOnly End)> proposedWindows)
    {
        if (proposedWindows.Count == 0)
        {
            return null;
        }

        var windowsByDay = proposedWindows
            .GroupBy(window => window.Day)
            .ToDictionary(group => group.Key, group => group.ToList());
        var publishedRows = await db.ScheduleItems
            .AsNoTracking()
            .Where(item => item.TeacherId == teacherId
                           && (item.LessonType.RequiresTeacher || item.LessonType.BlocksTeacher))
            .Select(item => new TeacherPlacementRow(
                item.Date,
                item.StartTime,
                item.EndTime,
                item.Group.Name))
            .ToListAsync();
        var violation = FindWorkingHoursViolation(publishedRows, windowsByDay, "опублікованому розкладі");
        if (violation is not null)
        {
            return violation;
        }

        var draftRows = await db.TeacherDraftItems
            .AsNoTracking()
            .Where(item => item.TeacherId == teacherId
                           && (item.LessonType.RequiresTeacher || item.LessonType.BlocksTeacher))
            .Select(item => new TeacherPlacementRow(
                item.Date,
                item.StartTime,
                item.EndTime,
                item.Group.Name))
            .ToListAsync();
        return FindWorkingHoursViolation(draftRows, windowsByDay, "чернетках");
    }

    private static string? FindWorkingHoursViolation(
        IEnumerable<TeacherPlacementRow> rows,
        IReadOnlyDictionary<DayOfWeek, List<(DayOfWeek Day, TimeOnly Start, TimeOnly End)>> windowsByDay,
        string source)
    {
        var row = rows
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Start)
            .FirstOrDefault(item =>
                !windowsByDay.TryGetValue(item.Date.DayOfWeek, out var windows)
                || !windows.Any(window => window.Start <= item.Start && item.End <= window.End));
        return row is null
            ? null
            : $"Нові робочі години не охоплюють заняття групи {row.GroupName} "
              + $"{row.Date:yyyy-MM-dd} {row.Start:HH\\:mm}-{row.End:HH\\:mm} у {source}.";
    }

    private sealed record TeacherPlacementRow(
        DateOnly Date,
        TimeOnly Start,
        TimeOnly End,
        string GroupName);
}
