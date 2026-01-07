using System.Net.Http.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Client.Services;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// API-клієнт для роботи з викладацькими чернетками
public sealed class TeacherDraftsApi(HttpClient http) : ITeacherDraftsApi
{
    // Завантажує чернетки тижня для викладача.
    public async Task<List<TeacherDraftItemDto>> GetWeek(DateOnly weekStart, int? teacherId)
    {
        var url = $"api/teacher-drafts?weekStart={weekStart:yyyy-MM-dd}" +
                  (teacherId is int t ? $"&teacherId={t}" : "");
        return await http.GetFromJsonAsync<List<TeacherDraftItemDto>>(url) ?? new();
    }
    // Експортує чернетки тижня у файл.
    public async Task<byte[]> ExportWeek(DateOnly weekStart, int? teacherId, int? groupId, int? roomId)
    {
        var query = $"weekStart={weekStart:yyyy-MM-dd}";
        if (teacherId is int tid) query += $"&teacherId={tid}";
        if (groupId is int gid) query += $"&groupId={gid}";
        if (roomId is int rid) query += $"&roomId={rid}";
        var res = await http.GetAsync($"api/teacher-drafts/export?{query}");
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadAsByteArrayAsync();
    }
    // Запускає автогенерацію чернеток на тиждень.
    public async Task<AutoGenResult> AutogenWeek(AutoGenRequest req)
    {
        var res = await http.PostAsJsonAsync("api/teacher-drafts/autogen/week", req);
        await res.EnsureSuccessWithDetailsAsync();
        return (await res.Content.ReadFromJsonAsync<AutoGenResult>())!;
    }
    // Очищає чернетки тижня.
    public async Task<int> ClearWeek(ClearWeekRequest req)
    {
        var res = await http.PostAsJsonAsync("api/teacher-drafts/clear-week", req);
        await res.EnsureSuccessWithDetailsAsync();
        var dto = await res.Content.ReadFromJsonAsync<ClearWeekResult>();
        return dto?.Deleted ?? 0;
    }
    // Створює або оновлює чернетку.
    public async Task<int> Upsert(DraftUpsertRequest req)
    {
        var res = await http.PostAsJsonAsync("api/teacher-drafts/upsert", req);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<int>();
    }
    // Видаляє чернетку з параметрами підтвердження.
    public async Task Delete(int id, bool confirm = false, bool unrestricted = false)
    {
        var flag = confirm ? "true" : "false";
        var unrestrictedFlag = unrestricted ? "true" : "false";
        var res = await http.DeleteAsync($"api/teacher-drafts/{id}?confirm={flag}&unrestricted={unrestrictedFlag}");
        await res.EnsureSuccessWithDetailsAsync();
    }
    // Запускає автогенерацію для місяця.
    public async Task<AutoGenResult> AutogenMonth(AutogenMonthRequest req)
    {
        var res = await http.PostAsJsonAsync("api/teacher-drafts/autogen/month", req);
        await res.EnsureSuccessWithDetailsAsync();
        return (await res.Content.ReadFromJsonAsync<AutoGenResult>())!;
    }
    // Запускає автогенерацію для діапазону тижнів курсу.
    public async Task<AutoGenResult> AutogenCourse(AutogenCourseRequest req)
    {
        var res = await http.PostAsJsonAsync("api/teacher-drafts/autogen/course", req);
        await res.EnsureSuccessWithDetailsAsync();
        return (await res.Content.ReadFromJsonAsync<AutoGenResult>())!;
    }
    // Публікує чернетки тижня у розклад.
    public async Task PublishWeek(PublishWeekRequest req)
    {
        var res = await http.PostAsJsonAsync("api/teacher-drafts/publish-week", req);
        await res.EnsureSuccessWithDetailsAsync();
    }
}
