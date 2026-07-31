using System.Collections.Generic;

// DTO для конфігурації послідовностей модулів
namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// Елемент послідовності модулів.
public record ModuleSequenceItemDto(int Id, int ModuleId, string ModuleCode, string ModuleTitle, int Order, int GroupOrder);

// Конфігурація послідовності модулів для курсу.
public record ModuleSequenceConfigDto(int CourseId, List<ModuleSequenceItemDto> MainSequence, List<int> FillerModuleIds);

// Запит на збереження послідовності модулів.
public record ModuleSequenceSaveItemDto(int ModuleId, int GroupOrder);
public record ModuleSequenceSaveRequestDto(int CourseId, List<ModuleSequenceSaveItemDto> MainModules, List<int> FillerModuleIds);
