// DTO для опису параметрів часового слоту
namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// DTO для налаштування тайм-слоту.
public record class TimeSlotDto
{
    public int Id { get; set; }
    public int? CourseId { get; set; }
    public int? DayOfWeek { get; set; }
    public int SortOrder { get; set; } = 0;
    public string Start { get; set; } = "08:30";
    public string End { get; set; } = "10:00";
    public bool IsActive { get; set; } = true;
    public bool IsLunch { get; set; } = false;
}
