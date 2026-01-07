using System.Collections.Generic;

namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// DTO для зміни теми модуля в адмінці.
public record ModuleTopicDto(
    int? Id,
    int ModuleId,
    int Order,
    string TopicCode,
    int LessonTypeId,
    int? DepartmentId,
    int TotalHours,
    int AuditoriumHours,
    int SelfStudyHours,
    bool IsInterAssembly,
    bool SelfStudyBySupervisor
);

// DTO годин по групі для теми.
public record TopicGroupHoursDto(
    string GroupName,
    int AuditoriumHours,
    int SelfStudyHours
);

// DTO для відображення теми модуля з плануванням по групах.
public record ModuleTopicViewDto(
    int Id,
    int ModuleId,
    int Order,
    string TopicCode,
    int LessonTypeId,
    string LessonTypeCode,
    string LessonTypeName,
    int TotalHours,
    int AuditoriumHours,
    int SelfStudyHours,
    List<string> PlannedGroups,
    List<string> CompletedGroups,
    bool IsInterAssembly,
    bool SelfStudyBySupervisor,
    List<TopicGroupHoursDto>? PlannedGroupsHours = null,
    List<TopicGroupHoursDto>? CompletedGroupsHours = null,
    int? DepartmentId = null,
    string? DepartmentName = null
);
