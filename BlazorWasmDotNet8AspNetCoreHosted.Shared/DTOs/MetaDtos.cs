// DTO для метаданих розкладу та довідників
namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

public record LookupDto(int Id, string Name)
{
    public int? CourseId { get; init; }
}
public record IdCodeNameDto(int Id, string Code, string Name)
{
    public bool RequiresRoom { get; init; } = true;
    public string? CssKey { get; init; } = null;
}
public record ModuleMetaDto(int Id, string Code, string Name, int CourseId, string CourseName)
{
    public List<int> CourseIds { get; init; } = new();
}

public record LunchConfigDto(int? CourseId, string Start, string End);
public record CalendarExceptionDto(string Date, bool IsWorkingDay, string Name)
{
    public int? CourseId { get; init; }
    public int? GroupId { get; init; }
}

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
}
