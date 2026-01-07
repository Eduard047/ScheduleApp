// DTO для очищення тижня в розкладі
namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// DTO запиту на очищення тижня.
public record ClearWeekRequest(
    DateOnly WeekStart,
    int? CourseId = null,
    int? GroupId = null
);

// DTO відповіді з кількістю видалених записів.
public record ClearWeekResult(int Deleted);
