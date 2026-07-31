// DTO запиту для оновлення або створення елемента розкладу
namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// DTO для створення або оновлення пари розкладу.
public record UpsertScheduleItemRequest(
    int? Id,
    DateOnly Date,
    string TimeStart,
    string TimeEnd,
    int GroupId,
    int ModuleId,
    int? TeacherId,
    int? RoomId,
    int LessonTypeId,
    bool IsLocked,
    bool OverrideNonWorkingDay = false,
    Guid? ExpectedRevision = null
);
