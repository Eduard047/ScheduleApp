// DTO для метаданих розкладу та довідників
namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// Універсальний довідковий елемент (id + назва).
public record LookupDto(int Id, string Name)
{
    public int? CourseId { get; init; }
    public int? DepartmentId { get; init; }
    public DateOnly? AcademicPeriodStartDate { get; init; }
}
// Довідник з кодом та назвою.
public record IdCodeNameDto(int Id, string Code, string Name)
{
    public bool RequiresRoom { get; init; } = true;
    public string? CssKey { get; init; } = null;
}
// Метадані модуля для клієнта.
public record ModuleMetaDto(int Id, string Code, string Name, int CourseId, string CourseName)
{
    public List<int> CourseIds { get; init; } = new();
}

// DTO налаштування обідньої перерви.
public record LunchConfigDto(int? CourseId, string Start, string End);
// DTO календарного винятку для метаданих.
public record CalendarExceptionDto(string Date, bool IsWorkingDay, string Name)
{
    public int? CourseId { get; init; }
    public int? GroupId { get; init; }
}

// DTO повного набору метаданих для клієнта.
public record MetaResponseDto(
    List<LookupDto> Courses,
    List<LookupDto> Groups,
    List<LookupDto> Teachers,
    List<LookupDto> Rooms,
    List<LookupDto> Buildings,
    List<IdCodeNameDto> LessonTypes,
    List<LunchConfigDto> Lunches)
{
    public List<ModuleMetaDto> Modules { get; init; } = new();
    public List<CalendarExceptionDto> Calendar { get; init; } = new();
    public List<LookupDto> Departments { get; init; } = new();
}
