using System.Net.Http.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Client.Services;

// API-клієнт для керування часовими слотами
public class TimeSlotsApi
{
    private readonly HttpClient _http;
    public TimeSlotsApi(HttpClient http) => _http = http;
    // Відповідь ефективних слотів.
    private sealed record EffectiveSlotsResponse(int? courseId, bool usingCourseSpecific, List<TimeSlotDto> slots);
    // Відповідь сирих слотів.
    private sealed record RawSlotsResponse(List<TimeSlotDto> course, List<TimeSlotDto> global);
    // Запит на збереження слотів.
    private sealed record BulkSaveReq(int? CourseId, List<TimeSlotDto> Slots);
    // Повертає ефективні слоти для курсу або глобальні.
    public async Task<List<TimeSlotDto>> GetEffectiveAsync(int? courseId)
    {
        var url = $"api/admin/config/slots{(courseId is null ? "" : $"?courseId={courseId}")}";
        var res = await _http.GetFromJsonAsync<EffectiveSlotsResponse>(url);
        return res?.slots ?? new();
    }
    // Повертає сирі слоти для редагування.
    public async Task<List<TimeSlotDto>> GetRawAsync(int? courseId)
    {
        var url = $"api/admin/config/slots/raw{(courseId is null ? "" : $"?courseId={courseId}")}";
        var res = await _http.GetFromJsonAsync<RawSlotsResponse>(url);
        return (courseId is null) ? (res?.global ?? new()) : (res?.course ?? new());
    }
    // Зберігає список слотів.
    public async Task SaveAsync(int? courseId, List<TimeSlotDto> slots)
    {
        var payload = new BulkSaveReq(courseId, slots);
        var resp = await _http.PostAsJsonAsync("api/admin/config/slots/upsert-bulk", payload);
        resp.EnsureSuccessStatusCode();
    }
}
