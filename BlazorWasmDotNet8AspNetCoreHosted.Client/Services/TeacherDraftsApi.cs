using System.Net.Http.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Client.Services;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using System.Net;

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
        var res = await http.GetAsync(url);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<List<TeacherDraftItemDto>>() ?? new();
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
    public async Task<AutoGenPlanDetailsDto> GetAutogenPlan(string jobId)
    {
        var res = await http.GetAsync($"api/teacher-drafts/autogen/jobs/{Uri.EscapeDataString(jobId)}/plan");
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<AutoGenPlanDetailsDto>()
               ?? throw new InvalidOperationException("Сервер не повернув попередній план автогенерації.");
    }
    public async Task<AutoGenPlanDetailsDto> ApplyAutogenPlan(string jobId, AutoGenPlanActionRequest request)
    {
        var res = await http.PostAsJsonAsync(
            $"api/teacher-drafts/autogen/jobs/{Uri.EscapeDataString(jobId)}/apply",
            request);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<AutoGenPlanDetailsDto>()
               ?? throw new InvalidOperationException("Сервер не повернув результат застосування плану автогенерації.");
    }
    public async Task<AutoGenPlanDetailsDto> RollbackAutogenPlan(string jobId, AutoGenPlanActionRequest request)
    {
        var res = await http.PostAsJsonAsync(
            $"api/teacher-drafts/autogen/jobs/{Uri.EscapeDataString(jobId)}/rollback",
            request);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<AutoGenPlanDetailsDto>()
               ?? throw new InvalidOperationException("Сервер не повернув результат відкоту плану автогенерації.");
    }
    public async Task<AutoGenPlanDetailsDto?> GetLatestRollbackableAutogenPlan(int? courseId)
    {
        var url = ApiClientHelpers.WithQuery(
            "api/teacher-drafts/autogen/plans/latest-rollbackable",
            ("courseId", courseId?.ToString()));
        var res = await http.GetAsync(url);
        if (res.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            return null;
        }
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<AutoGenPlanDetailsDto>()
               ?? throw new InvalidOperationException("Сервер не повернув доступний план для відкоту автогенерації.");
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
        var res = await http.PostAsJsonAsync(ApiClientHelpers.WithConfirm("api/teacher-drafts/clear-week"), req);
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
    // Атомарно створює або оновлює пакет чернеток.
    public async Task<TeacherDraftBatchUpsertResult> UpsertBatch(TeacherDraftBatchUpsertRequest req)
    {
        var res = await http.PostAsJsonAsync("api/teacher-drafts/upsert-batch", req);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<TeacherDraftBatchUpsertResult>()
               ?? throw new InvalidOperationException("Сервер не повернув результат пакетного збереження чернеток.");
    }
    // Видаляє чернетку з параметрами підтвердження.
    public async Task Delete(int id, Guid expectedRevision, bool confirm = false, bool unrestricted = false)
    {
        var res = await http.DeleteAsync(ApiClientHelpers.WithQuery(
            $"api/teacher-drafts/{id}",
            ("expectedRevision", expectedRevision.ToString("D")),
            ("confirm", confirm ? "true" : "false"),
            ("unrestricted", unrestricted ? "true" : "false")));
        await res.EnsureSuccessWithDetailsAsync();
    }
    // Атомарно видаляє пакет чернеток.
    public async Task<TeacherDraftBatchDeleteResult> DeleteBatch(TeacherDraftBatchDeleteRequest req)
    {
        var res = await http.PostAsJsonAsync(
            ApiClientHelpers.WithQuery("api/teacher-drafts/delete-batch", ("confirm", "true")),
            req);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<TeacherDraftBatchDeleteResult>()
               ?? throw new InvalidOperationException("Сервер не повернув результат пакетного видалення чернеток.");
    }
    // Атомарно застосовує змішаний пакет створень, оновлень і видалень.
    public async Task<TeacherDraftBatchMutationResult> MutateBatch(TeacherDraftBatchMutationRequest req)
    {
        var res = await http.PostAsJsonAsync("api/teacher-drafts/mutate-batch", req);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<TeacherDraftBatchMutationResult>()
               ?? throw new InvalidOperationException("Сервер не повернув результат атомарної зміни чернеток.");
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
    public async Task<PublishWeekResultDto> PublishWeek(PublishWeekRequest req)
    {
        var res = await http.PostAsJsonAsync("api/teacher-drafts/publish-week", req);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<PublishWeekResultDto>()
               ?? throw new InvalidOperationException("Сервер не повернув результат публікації.");
    }
}
