using System.Collections.Generic;

// DTO для імпорту модулів і тем з DOCX.
namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// Опис теми, прочитаної з документа.
public record DocxImportTopicDto(
    string ModuleCode,
    string TopicCode,
    string LessonTypeName,
    int TotalHours,
    int AuditoriumHours,
    int SelfStudyHours,
    int Order
);

// Опис модуля з набором тем для імпорту.
public record DocxImportModuleDto(
    string Code,
    string Title,
    decimal Credits,
    List<DocxImportTopicDto> Topics
);

// Підсумок імпорту з попередженнями та статусом.
public record DocxImportResultDto(
    string CourseName,
    int? CourseId,
    bool CourseFound,
    List<DocxImportModuleDto> Modules,
    List<string> Warnings,
    string? Error
);
