// DTO структури для чернеток викладачів
namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// Статус чернетки викладача.
public enum DraftStatusDto { Draft = 0, Published = 1 }

// DTO елемента чернетки викладача.
public record TeacherDraftItemDto(
    int Id,
    DateOnly Date,
    string TimeStart,
    string TimeEnd,
    int DayNumber,
    string Group,
    int GroupId,
    string Module,
    int ModuleId,
    string? TopicCode,
    int? ModuleTopicId,
    string Teacher,
    int? TeacherId,
    string Room,
    int? RoomId,
    bool RequiresRoom,
    bool MissingTeacherAssignment,
    bool MissingRoomAssignment,
    int LessonTypeId,
    string LessonTypeCode,
    string LessonTypeName,
    DraftStatusDto Status,
    int? PublishedItemId,
    string? Warnings,
    bool IsLocked = false,
    bool IsRescheduled = false,
    int? RescheduledFromLessonTypeId = null,
    string? BatchKey = null,
    List<string>? TeacherNames = null,
    string? LessonTypeCss = null,
    bool IsSelfStudy = false,
    Guid Revision = default
);

// DTO для створення або оновлення чернетки.
public record DraftUpsertRequest(
    int? Id,
    DateOnly Date,
    string TimeStart,
    string TimeEnd,
    int GroupId,
    int ModuleId,
    int? ModuleTopicId,
    int? TeacherId,
    int? RoomId,
    bool RequiresRoom,
    int LessonTypeId,
    bool OverrideNonWorkingDay = false,
    string? BatchKey = null,
    bool IsLocked = false,
    bool IgnoreValidationErrors = false,
    bool IsSelfStudy = false,
    Guid? ExpectedRevision = null
);

// DTO атомарного пакета створення або оновлення чернеток.
public record TeacherDraftBatchUpsertRequest(
    List<DraftUpsertRequest> Items,
    bool Unrestricted = false
);

// Результат атомарного пакета створення або оновлення чернеток.
public record TeacherDraftBatchUpsertResult(
    List<int> Ids,
    int Processed
);

// DTO атомарного пакета видалення чернеток.
public record TeacherDraftBatchDeleteRequest(
    List<int> Ids,
    bool Unrestricted = false,
    Dictionary<int, Guid>? ExpectedRevisions = null
);

// Результат атомарного пакета видалення чернеток.
public record TeacherDraftBatchDeleteResult(
    int Deleted
);

// DTO єдиної атомарної операції зі створенням, оновленням і видаленням чернеток.
public record TeacherDraftBatchMutationRequest(
    List<DraftUpsertRequest> Upserts,
    List<int> DeleteIds,
    bool Confirm = false,
    bool Unrestricted = false,
    Dictionary<int, Guid>? DeleteExpectedRevisions = null
);

// Результат єдиної атомарної зміни пакета чернеток.
public record TeacherDraftBatchMutationResult(
    List<int> UpsertedIds,
    int Upserted,
    int Deleted
);

// DTO для опису проблеми валідації.
public record DraftValidationIssueDto(
    string Severity,
    string Code,
    string Title,
    string Description
);

// DTO звіту про валідацію чернетки.
public record DraftValidationReportDto(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<DraftValidationIssueDto> Issues,
    Guid ScopeRevision = default
);

// DTO для запиту чернеток за тиждень.
public record DraftWeekQuery(DateOnly WeekStart, int? TeacherId);

// DTO для публікації тижня.
public record PublishWeekRequest(
    DateOnly WeekStart,
    int? TeacherId,
    Guid? ExpectedScopeRevision = null
);

// DTO для автогенерації чернеток по місяцю.
public record AutogenMonthRequest(
    DateOnly MonthStart,
    int? CourseId,
    int? GroupId,
    int? TeacherId,
    bool AllowOnDaysOff,
    WeekPreset Days,
    bool AllowIncompleteDrafts = false,
    List<GroupRoomPreferenceDto>? GroupRoomPreferences = null,
    AutoGenSoftOptionsDto? SoftOptions = null,
    int? PreferredFirstMaxSlotOrderOverride = null,
    bool PreflightOnly = false
);

// DTO для автогенерації чернеток по курсу за діапазон.
public record AutogenCourseRequest(
    DateOnly From,
    DateOnly To,
    int? CourseId,
    int? GroupId,
    int? TeacherId,
    bool AllowOnDaysOff,
    WeekPreset Days,
    bool AllowIncompleteDrafts = false,
    List<GroupRoomPreferenceDto>? GroupRoomPreferences = null,
    AutoGenSoftOptionsDto? SoftOptions = null,
    int? PreferredFirstMaxSlotOrderOverride = null,
    bool PreflightOnly = false
);
