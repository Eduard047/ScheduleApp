using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

// DTO для конфігурації послідовностей модулів
namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// Елемент послідовності модулів.
public record ModuleSequenceItemDto(int Id, int ModuleId, string ModuleCode, string ModuleTitle, int Order, int GroupOrder);

// Конфігурація послідовності модулів для курсу.
public record ModuleSequenceConfigDto(
    int CourseId,
    List<ModuleSequenceItemDto> MainSequence,
    List<int> FillerModuleIds,
    Guid Revision = default);

// Запит на збереження послідовності модулів.
public record ModuleSequenceSaveItemDto(int ModuleId, int GroupOrder);
public record ModuleSequenceSaveRequestDto(
    int CourseId,
    List<ModuleSequenceSaveItemDto> MainModules,
    List<int> FillerModuleIds,
    Guid? ExpectedRevision = null);

// Формує стабільну версію конфігурації, щоб паралельне збереження не перезаписувало новіші зміни.
public static class ModuleSequenceRevisionToken
{
    public static Guid Create(
        IEnumerable<ModuleSequenceSaveItemDto> mainModules,
        IEnumerable<int> fillerModuleIds)
    {
        ArgumentNullException.ThrowIfNull(mainModules);
        ArgumentNullException.ThrowIfNull(fillerModuleIds);

        var canonical = new StringBuilder();
        foreach (var item in mainModules)
        {
            canonical.Append('M')
                .Append(':')
                .Append(item.ModuleId)
                .Append(':')
                .Append(item.GroupOrder)
                .Append(';');
        }
        foreach (var moduleId in fillerModuleIds.OrderBy(id => id))
        {
            canonical.Append('F')
                .Append(':')
                .Append(moduleId)
                .Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new Guid(hash.AsSpan(0, 16));
    }
}
