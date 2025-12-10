using System.Collections.Generic;

namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

/// <summary>
/// DTO для зміни теми модуля в адмінці.
/// </summary>
public record ModuleTopicDto(
    int? Id,
    int ModuleId,
    int Order,
    string TopicCode,
    int LessonTypeId,
    int TotalHours,
    int AuditoriumHours,
    int SelfStudyHours,
    bool IsInterAssembly,
    bool SelfStudyBySupervisor
);

public record TopicGroupHoursDto(
    string GroupName,
    int AuditoriumHours,
    int SelfStudyHours
);

/// <summary>
/// DTO для відображення теми модуля з плануванням по групах.
/// </summary>
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
    List<TopicGroupHoursDto>? CompletedGroupsHours = null
);
