using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

[ApiController]
[Route("api/[controller]")]
// Контролер для отримання довідкових даних
public class MetaController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    // Повертає довідкові дані для клієнта, зокрема календар на тиждень.
    public async Task<MetaResponseDto> Get([FromQuery] DateOnly? weekStart)
    {
        var courses = await db.Courses.AsNoTracking()
            .Select(x => new LookupDto(x.Id, x.Name)
            {
                AcademicPeriodStartDate = x.AcademicPeriodStartDate
            })
            .ToListAsync();
        var groups = await db.Groups.AsNoTracking()
            .Select(x => new LookupDto(x.Id, x.Name) { CourseId = x.CourseId })
            .ToListAsync();
        var departments = await db.Departments.AsNoTracking()
            .Select(x => new LookupDto(x.Id, x.Name))
            .ToListAsync();
        var teachers = await db.Teachers.AsNoTracking()
            .Select(x => new LookupDto(x.Id, x.FullName) { DepartmentId = x.DepartmentId })
            .ToListAsync();
        var rooms = await db.Rooms.AsNoTracking().Select(x => new LookupDto(x.Id, x.Name)).ToListAsync();
        var buildings = await db.Buildings.AsNoTracking().Select(x => new LookupDto(x.Id, x.Name)).ToListAsync();
        var moduleRows = await db.Modules
            .AsNoTracking()
            .Select(x => new
            {
                x.Id,
                x.Code,
                x.Title,
                x.CourseId,
                CourseName = x.Course.Name,
                CourseIds = x.ModuleCourses.Select(mc => mc.CourseId).ToList()
            })
            .ToListAsync();
        var modules = moduleRows
            .Select(row =>
            {
                var courseIds = row.CourseIds ?? new List<int>();
                if (row.CourseId > 0 && !courseIds.Contains(row.CourseId))
                {
                    courseIds.Add(row.CourseId);
                }
                return new ModuleMetaDto(
                    row.Id,
                    row.Code,
                    row.Title,
                    row.CourseId,
                    row.CourseName
                )
                {
                    CourseIds = courseIds
                };
            })
            .ToList();
        var lessonTypes = await db.LessonTypes.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new IdCodeNameDto(x.Id, x.Code, x.Name)
            {
                RequiresRoom = x.RequiresRoom,
                CssKey = x.CssKey
            }).ToListAsync();
        var lunches = await db.LunchConfigs
            .Select(x => new LunchConfigDto(x.CourseId, x.Start.ToString("HH:mm"), x.End.ToString("HH:mm")))
            .ToListAsync();
        var cal = new List<CalendarExceptionDto>();
        if (weekStart is DateOnly s)
        {
            var end = s.AddDays(7);
            var xs = await db.CalendarExceptions.AsNoTracking().Where(x => x.Date >= s && x.Date < end).ToListAsync();
            cal = xs.Select(x => new CalendarExceptionDto(x.Date.ToString("yyyy-MM-dd"), x.IsWorkingDay, x.Name)
            {
                CourseId = x.CourseId,
                GroupId = x.GroupId
            }).ToList();
        }
        return new MetaResponseDto(courses, groups, teachers, rooms, buildings, lessonTypes, lunches)
        {
            Modules = modules,
            Calendar = cal,
            Departments = departments
        };
    }
}
