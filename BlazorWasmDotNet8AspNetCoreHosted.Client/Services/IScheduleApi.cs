using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Client.Services;

// Контракт клієнта для доступу до даних розкладу
public interface IScheduleApi
{
    // Завантажує метадані розкладу.
    Task<MetaResponseDto> GetMeta(DateOnly? weekStart = null);
    // Повертає розклад тижня з фільтрами.
    Task<List<ScheduleItemDto>> GetWeek(DateOnly weekStart, int? courseId = null, int? groupId = null, int? teacherId = null, int? roomId = null);
    // Створює або оновлює пару.
    Task<int> Upsert(UpsertScheduleItemRequest request);
    // Видаляє пару з розкладу.
    Task Delete(int id, Guid expectedRevision);
    // Очищає розклад тижня.
    Task<int> ClearWeek(ClearWeekRequest req);
}
