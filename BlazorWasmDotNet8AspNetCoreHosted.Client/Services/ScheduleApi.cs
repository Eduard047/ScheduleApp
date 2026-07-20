using System.Net.Http.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Client.Services;

// API-клієнт для роботи з даними розкладу
public sealed class ScheduleApi(HttpClient http) : IScheduleApi
{
    // Додає параметр підтвердження до URL.
    // Отримує метадані для клієнта розкладу.
    public async Task<MetaResponseDto> GetMeta(DateOnly? weekStart = null)
    {
        var url = weekStart is DateOnly d
            ? ApiClientHelpers.WithQuery("api/meta", ("weekStart", d.ToString("yyyy-MM-dd")))
            : "api/meta";
        var res = await http.GetAsync(url);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<MetaResponseDto>() ?? ApiClientHelpers.EmptyMeta();
    }
    // Завантажує розклад тижня з фільтрами.
    public async Task<List<ScheduleItemDto>> GetWeek(DateOnly weekStart, int? courseId = null, int? groupId = null, int? teacherId = null, int? roomId = null)
    {
        var url = ApiClientHelpers.WithQuery(
            "api/schedule",
            ("weekStart", weekStart.ToString("yyyy-MM-dd")),
            ("courseId", courseId?.ToString()),
            ("groupId", groupId?.ToString()),
            ("teacherId", teacherId?.ToString()),
            ("roomId", roomId?.ToString()));
        var res = await http.GetAsync(url);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<List<ScheduleItemDto>>() ?? new();
    }
    // Створює або оновлює пару в розкладі.
    public async Task<int> Upsert(UpsertScheduleItemRequest request)
    {
        var res = await http.PostAsJsonAsync("api/schedule/upsert", request);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<int>();
    }
    // Видаляє пару з розкладу.
    public async Task Delete(int id, Guid expectedRevision)
    {
        var url = ApiClientHelpers.WithQuery(
            ApiClientHelpers.WithConfirm($"api/schedule/{id}"),
            ("expectedRevision", expectedRevision.ToString("D")));
        var res = await http.DeleteAsync(url);
        await res.EnsureSuccessWithDetailsAsync();
    }
    // Очищає розклад за тиждень і повертає кількість видалених.
    public async Task<int> ClearWeek(ClearWeekRequest req)
    {
        var res = await http.PostAsJsonAsync(ApiClientHelpers.WithConfirm("api/schedule/clear"), req);
        await res.EnsureSuccessWithDetailsAsync();
        var dto = await res.Content.ReadFromJsonAsync<ClearWeekResult>();
        return dto?.Deleted ?? 0;
    }
}
