// DTO представлення запису розкладу
namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// DTO елемента розкладу для відображення.
public record ScheduleItemDto(
    int Id,
    DateOnly Date,
    string TimeStart,
    string TimeEnd,
    string DayName,
    int DayNumber,
    string Group,
    int GroupId,
    string Module,
    int ModuleId,
    string Teacher,
    int? TeacherId,
    string Room,
    int? RoomId,
    string Building,
    int? BuildingId,
    bool RequiresRoom,
    int LessonTypeId,
    string LessonTypeCode,
    string LessonTypeName,
    bool IsLocked,
    string? LessonTypeCss = null
);
