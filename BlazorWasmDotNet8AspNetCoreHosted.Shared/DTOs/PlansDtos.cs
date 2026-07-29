// DTO для планів навантаження курсів та модулів
namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// План годин модуля для курсу.
public record CourseModulePlanDto(
    int CourseId,
    int ModuleId,
    int TargetHours,
    int ScheduledHours,
    bool IsActive
);

// DTO для збереження плану модуля.
public record SaveCourseModulePlanDto(
    int TargetHours,
    bool IsActive
);
