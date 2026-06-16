using System.Net.Http.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Client.Services;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// API-клієнт для роботи з викладацькими чернетками
public sealed class TeacherDraftsApi(HttpClient http) : ITeacherDraftsApi
{
    // Завантажує чернетки тижня для викладача.
    public async Task<List<TeacherDraftItemDto>> GetWeek(DateOnly weekStart, int? teacherId)
    {
        var url = ApiClientHelpers.WithQuery(
            "api/teacher-drafts",
            ("weekStart", weekStart.ToString("yyyy-MM-dd")),
            ("teacherId", teacherId?.ToString()));
        return await http.GetFromJsonAsync<List<TeacherDraftItemDto>>(url) ?? new();
    }
    // Експортує чернетки тижня у файл.
    public async Task<byte[]> ExportWeek(DateOnly weekStart, int? teacherId, int? groupId, int? roomId)
    {
        var url = ApiClientHelpers.WithQuery(
            "api/teacher-drafts/export",
            ("weekStart", weekStart.ToString("yyyy-MM-dd")),
            ("teacherId", teacherId?.ToString()),
            ("groupId", groupId?.ToString()),
            ("roomId", roomId?.ToString()));
        var res = await http.GetAsync(url);
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
    public async Task<AutoGenJobStartResult> StartAutogenJob(AutoGenJobRequest req)
    {
        var res = await http.PostAsJsonAsync("api/teacher-drafts/autogen/jobs", req);
        await res.EnsureSuccessWithDetailsAsync();
        return (await res.Content.ReadFromJsonAsync<AutoGenJobStartResult>())!;
    }
    public async Task<AutoGenJobStatus> GetAutogenJob(string jobId)
    {
        var res = await http.GetAsync($"api/teacher-drafts/autogen/jobs/{Uri.EscapeDataString(jobId)}");
        await res.EnsureSuccessWithDetailsAsync();
        return (await res.Content.ReadFromJsonAsync<AutoGenJobStatus>())!;
    }
    public async Task<AutoGenJobStatus> CancelAutogenJob(string jobId)
    {
        var res = await http.PostAsync($"api/teacher-drafts/autogen/jobs/{Uri.EscapeDataString(jobId)}/cancel", content: null);
        await res.EnsureSuccessWithDetailsAsync();
        return (await res.Content.ReadFromJsonAsync<AutoGenJobStatus>())!;
    }
    // Виконує попередню перевірку ресурсів без запису чернеток.
    public async Task<AutoGenResult> AutogenPreflightWeek(AutoGenRequest req)
    {
        var res = await http.PostAsJsonAsync("api/teacher-drafts/autogen/week", req with { PreflightOnly = true });
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
        var res = await http.DeleteAsync(ApiClientHelpers.WithQuery(
            $"api/teacher-drafts/{id}",
            ("confirm", confirm ? "true" : "false"),
            ("unrestricted", unrestricted ? "true" : "false")));
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
