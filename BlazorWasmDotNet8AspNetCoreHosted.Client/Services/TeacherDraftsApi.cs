using System.Net;
using System.Net.Http.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Client.Services;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// API-клієнт для роботи з викладацькими чернетками
public sealed class TeacherDraftsApi(HttpClient http) : ITeacherDraftsApi
{
    private const int MaxAutogenPlanChanges = 2_000;

    // Завантажує чернетки тижня для викладача.
    public async Task<List<TeacherDraftItemDto>> GetWeek(DateOnly weekStart, int? teacherId)
    {
        var url = ApiClientHelpers.WithQuery(
            "api/teacher-drafts",
            ("weekStart", weekStart.ToString("yyyy-MM-dd")),
            ("teacherId", teacherId?.ToString()));
        using var res = await http.GetAsync(url);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<List<TeacherDraftItemDto>>() ?? new();
    }

    // Повторно перевіряє всі чернетки тижня незалежно від активних фільтрів.
    public async Task<DraftValidationReportDto> ValidateWeek(DateOnly weekStart)
    {
        var url = ApiClientHelpers.WithQuery(
            "api/teacher-drafts/validate-week",
            ("weekStart", weekStart.ToString("yyyy-MM-dd")));
        using var res = await http.GetAsync(url);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<DraftValidationReportDto>()
               ?? throw new InvalidOperationException("Сервер не повернув результат перевірки тижня.");
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
        using var res = await http.GetAsync(url);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadAsByteArrayAsync();
    }

    public async Task<AutoGenJobStartResult> StartAutogenJob(
        AutoGenJobRequest req,
        CancellationToken cancellationToken = default)
    {
        using var res = await http.PostAsJsonAsync(
            "api/teacher-drafts/autogen/jobs",
            req,
            cancellationToken);
        await res.EnsureSuccessWithDetailsAsync(cancellationToken);
        return (await res.Content.ReadFromJsonAsync<AutoGenJobStartResult>(
            cancellationToken: cancellationToken))!;
    }

    public async Task<AutoGenJobStatus> GetAutogenJob(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        using var res = await http.GetAsync(
            $"api/teacher-drafts/autogen/jobs/{Uri.EscapeDataString(jobId)}",
            cancellationToken);
        await res.EnsureSuccessWithDetailsAsync(cancellationToken);
        return (await res.Content.ReadFromJsonAsync<AutoGenJobStatus>(cancellationToken: cancellationToken))!;
    }

    public async Task<AutoGenJobStatus> CancelAutogenJob(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        using var res = await http.PostAsync(
            $"api/teacher-drafts/autogen/jobs/{Uri.EscapeDataString(jobId)}/cancel",
            content: null,
            cancellationToken);
        await res.EnsureSuccessWithDetailsAsync(cancellationToken);
        return (await res.Content.ReadFromJsonAsync<AutoGenJobStatus>(
            cancellationToken: cancellationToken))!;
    }

    public async Task<AutoGenPlanDetailsDto> GetAutogenPlan(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var firstPage = await GetAutogenPlanPage(jobId, changeOffset: 0, cancellationToken);
        return await LoadRemainingAutogenPlanChanges(jobId, firstPage, cancellationToken);
    }

    public async Task<AutoGenPlanDetailsDto> ApplyAutogenPlan(
        string jobId,
        AutoGenPlanActionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var res = await http.PostAsJsonAsync(
            $"api/teacher-drafts/autogen/jobs/{Uri.EscapeDataString(jobId)}/apply",
            request,
            cancellationToken);
        await res.EnsureSuccessWithDetailsAsync(cancellationToken);
        return await res.Content.ReadFromJsonAsync<AutoGenPlanDetailsDto>(
                   cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException("Сервер не повернув результат застосування плану автогенерації.");
    }

    public async Task<AutoGenPlanDetailsDto> RollbackAutogenPlan(
        string jobId,
        AutoGenPlanActionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var res = await http.PostAsJsonAsync(
            $"api/teacher-drafts/autogen/jobs/{Uri.EscapeDataString(jobId)}/rollback",
            request,
            cancellationToken);
        await res.EnsureSuccessWithDetailsAsync(cancellationToken);
        return await res.Content.ReadFromJsonAsync<AutoGenPlanDetailsDto>(
                   cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException("Сервер не повернув результат відкоту плану автогенерації.");
    }

    public async Task<AutoGenPlanDetailsDto?> GetLatestRollbackableAutogenPlan(
        int? courseId,
        CancellationToken cancellationToken = default)
    {
        var url = ApiClientHelpers.WithQuery(
            "api/teacher-drafts/autogen/plans/latest-rollbackable",
            ("courseId", courseId?.ToString()),
            ("changeOffset", "0"),
            ("changeLimit", "200"));
        using var res = await http.GetAsync(url, cancellationToken);
        if (res.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            return null;
        }
        await res.EnsureSuccessWithDetailsAsync(cancellationToken);
        var firstPage = await res.Content.ReadFromJsonAsync<AutoGenPlanDetailsDto>(
                            cancellationToken: cancellationToken)
                        ?? throw new InvalidOperationException("Сервер не повернув доступний план для відкоту автогенерації.");
        return await LoadRemainingAutogenPlanChanges(
            firstPage.Summary.PlanId,
            firstPage,
            cancellationToken);
    }

    private async Task<AutoGenPlanDetailsDto> GetAutogenPlanPage(
        string jobId,
        int changeOffset,
        CancellationToken cancellationToken)
    {
        var url = ApiClientHelpers.WithQuery(
            $"api/teacher-drafts/autogen/jobs/{Uri.EscapeDataString(jobId)}/plan",
            ("changeOffset", changeOffset.ToString()),
            ("changeLimit", "200"));
        using var res = await http.GetAsync(url, cancellationToken);
        await res.EnsureSuccessWithDetailsAsync(cancellationToken);
        return await res.Content.ReadFromJsonAsync<AutoGenPlanDetailsDto>(
                   cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException("Сервер не повернув попередній план автогенерації.");
    }

    private async Task<AutoGenPlanDetailsDto> LoadRemainingAutogenPlanChanges(
        string jobId,
        AutoGenPlanDetailsDto firstPage,
        CancellationToken cancellationToken)
    {
        if (firstPage.TotalChanges is null)
        {
            if (firstPage.ChangeOffset != 0
                || firstPage.Changes.Count > MaxAutogenPlanChanges)
            {
                throw new InvalidOperationException(
                    "Сервер повернув некоректну застарілу відповідь плану автогенерації.");
            }
            return firstPage with { TotalChanges = firstPage.Changes.Count };
        }

        var totalChanges = firstPage.TotalChanges.Value;
        if (firstPage.ChangeOffset != 0
            || totalChanges is < 0 or > MaxAutogenPlanChanges
            || firstPage.Changes.Count > totalChanges)
        {
            throw new InvalidOperationException(
                "Сервер повернув некоректну кількість змін плану автогенерації.");
        }
        if (!firstPage.HasMoreChanges)
        {
            return firstPage;
        }

        var changes = new List<AutoGenPlanChangeDto>(totalChanges);
        changes.AddRange(firstPage.Changes);
        var offset = firstPage.ChangeOffset + firstPage.Changes.Count;
        while (offset < totalChanges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await GetAutogenPlanPage(jobId, offset, cancellationToken);
            if (page.ChangeOffset != offset
                || page.TotalChanges != totalChanges
                || page.Summary.PlanId != firstPage.Summary.PlanId
                || page.Summary.Version != firstPage.Summary.Version
                || page.Changes.Count == 0
                || page.Changes.Count > totalChanges - offset)
            {
                throw new InvalidOperationException(
                    "Сервер повернув неузгоджену сторінку змін плану автогенерації.");
            }

            changes.AddRange(page.Changes);
            offset += page.Changes.Count;
        }
        if (changes.Count != totalChanges)
        {
            throw new InvalidOperationException(
                "Сервер повернув неповний план автогенерації.");
        }

        return firstPage with
        {
            Changes = changes,
            ChangeOffset = 0
        };
    }

    // Очищає чернетки тижня.
    public async Task<int> ClearWeek(ClearWeekRequest req)
    {
        using var res = await http.PostAsJsonAsync(
            ApiClientHelpers.WithConfirm("api/teacher-drafts/clear-week"),
            req);
        await res.EnsureSuccessWithDetailsAsync();
        var dto = await res.Content.ReadFromJsonAsync<ClearWeekResult>();
        return dto?.Deleted ?? 0;
    }

    // Створює або оновлює чернетку.
    public async Task<int> Upsert(DraftUpsertRequest req)
    {
        using var res = await http.PostAsJsonAsync("api/teacher-drafts/upsert", req);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<int>();
    }

    // Атомарно створює або оновлює пакет чернеток.
    public async Task<TeacherDraftBatchUpsertResult> UpsertBatch(TeacherDraftBatchUpsertRequest req)
    {
        using var res = await http.PostAsJsonAsync("api/teacher-drafts/upsert-batch", req);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<TeacherDraftBatchUpsertResult>()
               ?? throw new InvalidOperationException("Сервер не повернув результат пакетного збереження чернеток.");
    }

    // Видаляє чернетку з параметрами підтвердження.
    public async Task Delete(int id, Guid expectedRevision, bool confirm = false, bool unrestricted = false)
    {
        using var res = await http.DeleteAsync(ApiClientHelpers.WithQuery(
            $"api/teacher-drafts/{id}",
            ("expectedRevision", expectedRevision.ToString("D")),
            ("confirm", confirm ? "true" : "false"),
            ("unrestricted", unrestricted ? "true" : "false")));
        await res.EnsureSuccessWithDetailsAsync();
    }

    // Атомарно видаляє пакет чернеток.
    public async Task<TeacherDraftBatchDeleteResult> DeleteBatch(TeacherDraftBatchDeleteRequest req)
    {
        using var res = await http.PostAsJsonAsync(
            ApiClientHelpers.WithQuery("api/teacher-drafts/delete-batch", ("confirm", "true")),
            req);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<TeacherDraftBatchDeleteResult>()
               ?? throw new InvalidOperationException("Сервер не повернув результат пакетного видалення чернеток.");
    }

    // Атомарно застосовує змішаний пакет створень, оновлень і видалень.
    public async Task<TeacherDraftBatchMutationResult> MutateBatch(TeacherDraftBatchMutationRequest req)
    {
        using var res = await http.PostAsJsonAsync("api/teacher-drafts/mutate-batch", req);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<TeacherDraftBatchMutationResult>()
               ?? throw new InvalidOperationException("Сервер не повернув результат атомарної зміни чернеток.");
    }

    // Публікує чернетки тижня у розклад.
    public async Task<PublishWeekResultDto> PublishWeek(PublishWeekRequest req)
    {
        using var res = await http.PostAsJsonAsync("api/teacher-drafts/publish-week", req);
        await res.EnsureSuccessWithDetailsAsync();
        return await res.Content.ReadFromJsonAsync<PublishWeekResultDto>()
               ?? throw new InvalidOperationException("Сервер не повернув результат публікації.");
    }
}
