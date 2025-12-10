using System.Collections.Generic;

// DTO для адміністративних операцій і форм редагування
namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

public record class GroupEditDto
{
    public GroupEditDto() { }
    public GroupEditDto(int? id, string name, int studentsCount, int courseId)
    { Id = id; Name = name; StudentsCount = studentsCount; CourseId = courseId; }

    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public int StudentsCount { get; set; }
    public int CourseId { get; set; }
}

public record class ModuleEditDto
{
    public ModuleEditDto()
    {
        AllowedRoomIds = new();
        AllowedBuildingIds = new();
        CloneCourseIds = new();
    }

    public ModuleEditDto(int? id, string code, string title, int courseId,
        decimal credits = 0m)
        : this()
    {
        Id = id;
        Code = code;
        Title = title;
        CourseId = courseId;
        Credits = credits;
    }

    public ModuleEditDto(int? id, string code, string title, int courseId, List<int> allowedRoomIds, List<int> allowedBuildingIds,
        decimal credits = 0m)
        : this(id, code, title, courseId, credits)
    {
        AllowedRoomIds = allowedRoomIds ?? new();
        AllowedBuildingIds = allowedBuildingIds ?? new();
    }

    public int? Id { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public int CourseId { get; set; }
    public List<int> AllowedRoomIds { get; set; } = new();
    public List<int> AllowedBuildingIds { get; set; } = new();
    public List<int> CloneCourseIds { get; set; } = new();
    public decimal Credits { get; set; }
}

public record class TeacherEditDto
{
    public TeacherEditDto() { }
    public TeacherEditDto(int? id, string fullName, string? scientificDegree, string? academicTitle)
    { Id = id; FullName = fullName; ScientificDegree = scientificDegree; AcademicTitle = academicTitle; }

    public TeacherEditDto(int? id, string fullName, string? scientificDegree, string? academicTitle,
        List<int> moduleIds, List<int> supervisorModuleIds, List<TeacherLoadDto> loads, List<TeacherWorkingHourDto> workingHours)
    {
        Id = id; FullName = fullName; ScientificDegree = scientificDegree; AcademicTitle = academicTitle;
        ModuleIds = moduleIds ?? new();
        SupervisorModuleIds = supervisorModuleIds ?? new();
        Loads = loads ?? new();
        WorkingHours = workingHours ?? new();
    }

    public int? Id { get; set; }
    public string FullName { get; set; } = "";
    public string? ScientificDegree { get; set; }
    public string? AcademicTitle { get; set; }
    public List<int> ModuleIds { get; set; } = new();
    public List<int> SupervisorModuleIds { get; set; } = new();
    public List<TeacherLoadDto> Loads { get; set; } = new();
    public List<TeacherWorkingHourDto> WorkingHours { get; set; } = new();
}

public record class TeacherLoadDto
{
    public TeacherLoadDto() { }
    public TeacherLoadDto(int courseId, bool isActive, int scheduledHours = 0)
    { CourseId = courseId; IsActive = isActive; ScheduledHours = scheduledHours; }

    public int CourseId { get; set; }
    public bool IsActive { get; set; }
    public int ScheduledHours { get; set; }
}

public record class TeacherWorkingHourDto
{
    public TeacherWorkingHourDto() { }
    public TeacherWorkingHourDto(int dayOfWeek, string start, string end)
    { DayOfWeek = dayOfWeek; Start = start; End = end; }

    public int DayOfWeek { get; set; }
    public string Start { get; set; } = "09:00";
    public string End { get; set; } = "17:00";
}

public record class TeacherViewDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string? ScientificDegree { get; set; }
    public string? AcademicTitle { get; set; }
    public List<int> ModuleIds { get; set; } = new();
    public List<int> SupervisorModuleIds { get; set; } = new();
    public List<TeacherLoadDto> Loads { get; set; } = new();
    public List<TeacherWorkingHourDto> WorkingHours { get; set; } = new();
}

public record class RoomEditDto
{
    public RoomEditDto() { }
    public RoomEditDto(int? id, string name, int capacity, int buildingId)
    { Id = id; Name = name; Capacity = capacity; BuildingId = buildingId; }

    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public int Capacity { get; set; }
    public int BuildingId { get; set; }
}

public record class BuildingEditDto
{
    public BuildingEditDto() { }
    public BuildingEditDto(int? id, string name, string? address)
    { Id = id; Name = name; Address = address; }

    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public string? Address { get; set; }
}

public record class BuildingTravelEditDto
{
    public BuildingTravelEditDto() { }
    public BuildingTravelEditDto(int fromId, int toId, int minutes)
    { FromBuildingId = fromId; ToBuildingId = toId; Minutes = minutes; }

    public int FromBuildingId { get; set; }
    public int ToBuildingId { get; set; }
    public int Minutes { get; set; }
}

public record class CodeNameEditDto
{
    public CodeNameEditDto() { }
    public CodeNameEditDto(int? id, string code, string name, bool isActive)
    { Id = id; Code = code; Name = name; IsActive = isActive; }

    public int? Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public record class LunchConfigEditDto
{
    public LunchConfigEditDto() { }
    public LunchConfigEditDto(int? id, int? courseId, string start, string end)
    { Id = id; CourseId = courseId; Start = start; End = end; }

    public int? Id { get; set; }
    public int? CourseId { get; set; }
    public string Start { get; set; } = "12:00";
    public string End { get; set; } = "13:00";
}

public record class CalendarExceptionEditDto
{
    public CalendarExceptionEditDto() { }
    public CalendarExceptionEditDto(int? id, string date, bool isWorkingDay, string name, int? courseId = null, int? groupId = null)
    { Id = id; Date = date; IsWorkingDay = isWorkingDay; Name = name; CourseId = courseId; GroupId = groupId; }

    public int? Id { get; set; }
    public string Date { get; set; } = "2025-01-01";
    public bool IsWorkingDay { get; set; }
    public string Name { get; set; } = "";
    public int? CourseId { get; set; }
    public int? GroupId { get; set; }
}

public record class LessonTypeEditDto
{
    public int? Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;

    public string? CssKey { get; set; } = null;

    public bool RequiresRoom { get; set; } = true;
    public bool RequiresTeacher { get; set; } = true;
    public bool BlocksRoom { get; set; } = true;
    public bool BlocksTeacher { get; set; } = true;
    public bool CountInPlan { get; set; } = true;
    public bool CountInLoad { get; set; } = true;
    public bool PreferredFirstInWeek { get; set; } = false;
}

public record LessonColorDto(
    string Key,
    string Name,
    string Hex,
    bool IsUsed,
    int? UsedByTypeId
);


