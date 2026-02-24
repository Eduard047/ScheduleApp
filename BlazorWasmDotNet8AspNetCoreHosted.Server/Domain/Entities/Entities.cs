using System.ComponentModel.DataAnnotations.Schema;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;

// Доменно-дані сутності для розкладу
public class LessonTypeRef
{
    public int Id { get; set; } 
    public string Code { get; set; } = default!; 
    public string Name { get; set; } = default!; 
    public bool IsActive { get; set; } = true;
    public bool RequiresRoom { get; set; } = true;
    public bool RequiresTeacher { get; set; } = true;
    public bool BlocksRoom { get; set; } = true;
    public bool BlocksTeacher { get; set; } = true;
    public bool CountInPlan { get; set; } = true;
    public bool CountInLoad { get; set; } = true;
    public bool PreferredFirstInWeek { get; set; } = false;
    public string? CssKey { get; set; }
}

public class Course
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public int DurationWeeks { get; set; }
    public ICollection<Group> Groups { get; set; } = new List<Group>();
    public ICollection<Module> Modules { get; set; } = new List<Module>();
    public ICollection<ModuleCourse> ModuleCourses { get; set; } = new List<ModuleCourse>();
}

public class Group
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public int StudentsCount { get; set; }
    public int CourseId { get; set; }
    public Course Course { get; set; } = default!;
}

public class Department
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public ICollection<Teacher> Teachers { get; set; } = new List<Teacher>();
    public ICollection<ModuleTopic> ModuleTopics { get; set; } = new List<ModuleTopic>();
}

public class Teacher
{
    public int Id { get; set; }
    public string FullName { get; set; } = default!;
    public string? ScientificDegree { get; set; } 
    public string? AcademicTitle { get; set; } 
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public ICollection<TeacherModule> TeacherModules { get; set; } = new List<TeacherModule>();
    public ICollection<ModuleSupervisor> ModuleSupervisions { get; set; } = new List<ModuleSupervisor>();
}

public class TeacherCourseLoad
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public Teacher Teacher { get; set; } = default!;
    public int CourseId { get; set; }
    public Course Course { get; set; } = default!;
    public int ScheduledHours { get; set; }
    public bool IsActive { get; set; } = true;
}

public class TeacherWorkingHour
{
    public int Id { get; set; }
    public int TeacherId { get; set; }
    public Teacher Teacher { get; set; } = default!;
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
}

public class Module
{
    public int Id { get; set; }
    public string Code { get; set; } = default!;
    public string Title { get; set; } = default!;
    public decimal Credits { get; set; }
    public int CourseId { get; set; }
    public Course Course { get; set; } = default!;
    public ICollection<ModuleCourse> ModuleCourses { get; set; } = new List<ModuleCourse>();
    public ICollection<TeacherModule> TeacherModules { get; set; } = new List<TeacherModule>();
    public ICollection<ModuleSupervisor> ModuleSupervisors { get; set; } = new List<ModuleSupervisor>();
    public ICollection<ModuleRoom> AllowedRooms { get; set; } = new List<ModuleRoom>();
    public ICollection<ModuleBuilding> AllowedBuildings { get; set; } = new List<ModuleBuilding>();
    public ICollection<ModuleTopic> Topics { get; set; } = new List<ModuleTopic>();
}

public class ModuleCourse
{
    public int ModuleId { get; set; }
    public Module Module { get; set; } = default!;
    public int CourseId { get; set; }
    public Course Course { get; set; } = default!;
}

public class TeacherModule
{
    public int TeacherId { get; set; }
    public Teacher Teacher { get; set; } = default!;
    public int ModuleId { get; set; }
    public Module Module { get; set; } = default!;
}

public class ModuleSupervisor
{
    public int TeacherId { get; set; }
    public Teacher Teacher { get; set; } = default!;
    public int ModuleId { get; set; }
    public Module Module { get; set; } = default!;
}

public class Building
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public string? Address { get; set; }
}

public class BuildingTravel
{
    public int Id { get; set; }
    public int FromBuildingId { get; set; } 
    public Building From { get; set; } = default!;
    public int ToBuildingId { get; set; }
    public Building To { get; set; } = default!;
    public int Minutes { get; set; }
}

public class Room
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public int Capacity { get; set; }
    public int BuildingId { get; set; }
    public Building Building { get; set; } = default!;
    public ICollection<ModuleRoom> ModuleRooms { get; set; } = new List<ModuleRoom>();
}

public class ModuleRoom
{
    public int ModuleId { get; set; }
    public Module Module { get; set; } = default!;
    public int RoomId { get; set; }
    public Room Room { get; set; } = default!;
}

public class ModuleBuilding
{
    public int ModuleId { get; set; }
    public Module Module { get; set; } = default!;
    public int BuildingId { get; set; }
    public Building Building { get; set; } = default!;
}

public class ModuleTopic
{
    public int Id { get; set; }
    public int ModuleId { get; set; }
    public Module Module { get; set; } = default!;
    public int Order { get; set; }
    public string TopicCode { get; set; } = string.Empty;
    public int LessonTypeId { get; set; }
    public LessonTypeRef LessonType { get; set; } = default!;
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public int TotalHours { get; set; }
    public int AuditoriumHours { get; set; }
    public int SelfStudyHours { get; set; }
    public bool IsInterAssembly { get; set; }
    public bool SelfStudyBySupervisor { get; set; }
}

public class ModulePlan
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    [ForeignKey(nameof(CourseId))]
    public Course Course { get; set; } = null!;
    public int ModuleId { get; set; }
    [ForeignKey(nameof(ModuleId))]
    public Module Module { get; set; } = null!;
    public int TargetHours { get; set; }
    public int ScheduledHours { get; set; }
    public bool IsActive { get; set; }
}

public class ScheduleItem
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int LessonTypeId { get; set; }
    public LessonTypeRef LessonType { get; set; } = default!;
    public int GroupId { get; set; }
    public Group Group { get; set; } = default!;
    public int ModuleId { get; set; }
    public Module Module { get; set; } = default!;
    public int? ModuleTopicId { get; set; }
    public ModuleTopic? ModuleTopic { get; set; }
    public int? TeacherId { get; set; }
    public Teacher? Teacher { get; set; }
    public int? RoomId { get; set; }
    public Room? Room { get; set; }
    public bool IsLocked { get; set; } = false;
    public bool IsSelfStudy { get; set; } = false;
}

public class LunchConfig
{
    public int Id { get; set; }
    public int? CourseId { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
}

public class PreferredFirstSlotLimitConfig
{
    public int Id { get; set; }
    public int? CourseId { get; set; }
    public Course? Course { get; set; }
    public int MaxSlotOrder { get; set; }
}

public class TimeSlot
{
    public int Id { get; set; }
    public int? CourseId { get; set; }
    [ForeignKey(nameof(CourseId))]
    public Course? Course { get; set; }
    public DayOfWeek? DayOfWeek { get; set; }
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
    public int SortOrder { get; set; } = 0;
    public bool IsActive { get; set; } = true;
}

public class CalendarException
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public bool IsWorkingDay { get; set; } 
    public string Name { get; set; } = default!;
    public int? CourseId { get; set; }
    public Course? Course { get; set; }
    public int? GroupId { get; set; }
    public Group? Group { get; set; }
}

public class ModuleSequenceItem
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public int ModuleId { get; set; }
    public Module Module { get; set; } = null!;
    public int Order { get; set; }
    public int GroupOrder { get; set; }
}

public class ModuleFiller
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public int ModuleId { get; set; }
    public Module Module { get; set; } = null!;
}

public enum DraftStatus { Draft = 0, Published = 1 }

public class TeacherDraftItem
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int LessonTypeId { get; set; }
    public LessonTypeRef LessonType { get; set; } = default!;
    public int GroupId { get; set; }
    public Group Group { get; set; } = default!;
    public int ModuleId { get; set; }
    public Module Module { get; set; } = default!;
    public int? ModuleTopicId { get; set; }
    public ModuleTopic? ModuleTopic { get; set; }
    public int? TeacherId { get; set; }
    public Teacher? Teacher { get; set; }
    public int? RoomId { get; set; }
    public Room? Room { get; set; }
    public DraftStatus Status { get; set; } = DraftStatus.Draft;
    public int? PublishedItemId { get; set; } 
    public string? BatchKey { get; set; }     
    public string? ValidationWarnings { get; set; } 
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsLocked { get; set; }   
    public bool IsSelfStudy { get; set; } = false;
}
