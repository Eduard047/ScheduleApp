using System.Net.Http.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Client.Services;

// API-клієнт для керування часовими слотами
public class TimeSlotsApi
{
    private readonly HttpClient _http;
    public TimeSlotsApi(HttpClient http) => _http = http;

    // Завантажує весь стан графіка одним запитом, щоб редактор не складав його з кількох відповідей.
    public async Task<TimeSlotEditorContextDto> GetEditorContextAsync(
        TimeSlotEditorTargetMode targetMode,
        int? courseId = null,
        int? dayOfWeek = null)
    {
        var query = new List<string> { $"targetMode={targetMode}" };
        if (courseId is not null) query.Add($"courseId={courseId}");
        if (dayOfWeek is not null) query.Add($"dayOfWeek={dayOfWeek}");
        var url = $"api/admin/config/slots/editor-context?{string.Join("&", query)}";
        return await _http.GetFromJsonWithDetailsAsync<TimeSlotEditorContextDto>(url)
               ?? new TimeSlotEditorContextDto
               {
                   TargetMode = targetMode,
                   CourseId = courseId,
                   DayOfWeek = dayOfWeek
               };
    }

    // Перевіряє майбутню зміну без запису та повертає точний вплив на розклад.
    public async Task<TimeSlotSequencePreviewDto> PreviewEditorAsync(
        TimeSlotSequenceApplyRequestDto payload)
    {
        using var response = await _http.PostAsJsonAsync("api/admin/config/slots/editor/preview", payload);
        await response.EnsureSuccessWithDetailsAsync();
        return await response.Content.ReadFromJsonAsync<TimeSlotSequencePreviewDto>()
               ?? throw new InvalidOperationException("Сервер не повернув результат перевірки графіка.");
    }

    // Застосовує саме той варіант, який користувач щойно перевірив.
    public async Task<TimeSlotSequenceApplyResultDto> ApplyEditorAsync(
        TimeSlotSequenceApplyRequestDto payload)
    {
        using var response = await _http.PostAsJsonAsync("api/admin/config/slots/editor/apply", payload);
        await response.EnsureSuccessWithDetailsAsync();
        return await response.Content.ReadFromJsonAsync<TimeSlotSequenceApplyResultDto>()
               ?? throw new InvalidOperationException("Сервер не повернув результат застосування графіка.");
    }
    // Відповідь ефективних слотів.
    private sealed record EffectiveSlotsResponse(int? courseId, bool usingCourseSpecific, List<TimeSlotDto> slots);
    // Відповідь сирих слотів.
    private sealed record RawSlotsResponse(List<TimeSlotDto> course, List<TimeSlotDto> global);
    // Запит на збереження слотів.
    private sealed record BulkSaveReq(int? CourseId, int? DayOfWeek, List<TimeSlotDto> Slots);
    // Повертає ефективні слоти для курсу або глобальні.
    public async Task<List<TimeSlotDto>> GetEffectiveAsync(int? courseId, int? dayOfWeek = null, bool includeDayOverrides = false)
    {
        var query = new List<string>();
        if (courseId is not null) query.Add($"courseId={courseId}");
        if (dayOfWeek is not null) query.Add($"dayOfWeek={dayOfWeek}");
        if (includeDayOverrides) query.Add("includeDayOverrides=true");
        var url = "api/admin/config/slots" + (query.Count == 0 ? "" : $"?{string.Join("&", query)}");
        var res = await _http.GetFromJsonWithDetailsAsync<EffectiveSlotsResponse>(url);
        return res?.slots ?? new();
    }
    // Повертає сирі слоти для редагування.
    public async Task<List<TimeSlotDto>> GetRawAsync(int? courseId, int? dayOfWeek = null)
    {
        var query = new List<string>();
        if (courseId is not null) query.Add($"courseId={courseId}");
        if (dayOfWeek is not null) query.Add($"dayOfWeek={dayOfWeek}");
        var url = "api/admin/config/slots/raw" + (query.Count == 0 ? "" : $"?{string.Join("&", query)}");
        var res = await _http.GetFromJsonWithDetailsAsync<RawSlotsResponse>(url);
        return (courseId is null) ? (res?.global ?? new()) : (res?.course ?? new());
    }
    // Зберігає список слотів.
    // Повертає ліміт слота для типу з прапорцем "Бажано першим у тижні".
    public async Task<PreferredFirstSlotLimitConfigEditDto> GetPreferredFirstSlotLimitAsync(int? courseId)
    {
        var query = courseId is null ? "" : $"?courseId={courseId}";
        var res = await _http.GetFromJsonWithDetailsAsync<PreferredFirstSlotLimitConfigEditDto>($"api/admin/config/preferred-first-slot-limit{query}");
        return res ?? new PreferredFirstSlotLimitConfigEditDto(null, courseId, 0);
    }
    // Зберігає ліміт слота для типу з прапорцем "Бажано першим у тижні".
    public async Task SavePreferredFirstSlotLimitAsync(int? courseId, int maxSlotOrder)
    {
        var payload = new PreferredFirstSlotLimitConfigEditDto(null, courseId, maxSlotOrder);
        var resp = await _http.PostAsJsonAsync("api/admin/config/preferred-first-slot-limit/upsert", payload);
        await resp.EnsureSuccessWithDetailsAsync();
    }
    public async Task SaveAsync(int? courseId, List<TimeSlotDto> slots, int? dayOfWeek = null)
    {
        var payload = new BulkSaveReq(courseId, dayOfWeek, slots);
        var resp = await _http.PostAsJsonAsync("api/admin/config/slots/upsert-bulk", payload);
        await resp.EnsureSuccessWithDetailsAsync();
    }
}
