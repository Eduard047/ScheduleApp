// DTO для редагування інформації про курс
namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// DTO для редагування даних курсу.
public record class CourseEditDto
{
    public CourseEditDto() { }
    public CourseEditDto(int? id, string name, int durationWeeks, DateOnly? academicPeriodStartDate = null)
    {
        Id = id;
        Name = name;
        DurationWeeks = durationWeeks;
        AcademicPeriodStartDate = academicPeriodStartDate;
    }
    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public int DurationWeeks { get; set; } = 16;
    public DateOnly? AcademicPeriodStartDate { get; set; }
}
