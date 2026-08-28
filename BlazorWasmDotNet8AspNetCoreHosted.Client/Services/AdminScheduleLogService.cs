using System.Text.Json;
using Microsoft.JSInterop;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Client.Services;

// Сервіс зберігання журналу змін для адмінської сторінки розкладу.
public sealed class AdminScheduleLogService
{
    private const string StorageKey = "adminSchedule.logs.v1";
    private const int MaxEntries = 200;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IJSRuntime _js;

    public AdminScheduleLogService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<List<AdminScheduleLogEntry>> LoadAsync()
    {
        try
        {
            var stored = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (string.IsNullOrWhiteSpace(stored))
            {
                return new();
            }
            var items = JsonSerializer.Deserialize<List<AdminScheduleLogEntry?>>(stored, JsonOptions);
            return (items ?? new())
                .Where(entry => entry is not null)
                .Select(entry => NormalizeEntry(entry!))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            await RemoveCorruptedStorageAsync();
            return new();
        }
    }

    public async Task AddAsync(AdminScheduleLogEntry entry)
    {
        var entries = await LoadAsync();
        entries.Insert(0, entry);
        if (entries.Count > MaxEntries)
        {
            entries = entries
                .OrderByDescending(x => x.Timestamp)
                .Take(MaxEntries)
                .ToList();
        }
        await SaveAsync(entries);
    }

    public async Task SaveAsync(List<AdminScheduleLogEntry> entries)
    {
        if (entries.Count == 0)
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
            return;
        }
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
    }

    public async Task ClearAsync()
        => await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey);

    // Видаляє пошкоджене локальне значення, щоб наступне відкриття журналу не падало повторно.
    private async Task RemoveCorruptedStorageAsync()
        => await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey);

    // Нормалізує записи зі старих версій схеми, де колекції могли бути відсутніми.
    private static AdminScheduleLogEntry NormalizeEntry(AdminScheduleLogEntry entry)
        => entry with
        {
            ModuleHours = entry.ModuleHours ?? new(),
            Warnings = entry.Warnings ?? new(),
            GapDetails = entry.GapDetails ?? new(),
            Lessons = entry.Lessons ?? new()
        };
}

public sealed record AdminScheduleLogEntry(
    string Id,
    DateTimeOffset Timestamp,
    string ActionCode,
    string ActionLabel,
    string Summary,
    bool Success,
    string? Error,
    string? WeekStart,
    string? WeekEnd,
    string? DaysPreset,
    bool? AllowDaysOff,
    int? CourseId,
    string? CourseName,
    List<AdminScheduleLogModuleHours> ModuleHours,
    List<string> Warnings,
    List<AutoGenGapDetail> GapDetails,
    List<AdminScheduleLogLesson> Lessons,
    bool LessonsTrimmed
);

public sealed record AdminScheduleLogModuleHours(
    int ModuleId,
    string ModuleName,
    int Hours
);

public sealed record AdminScheduleLogLesson(
    int? Id,
    string Date,
    string TimeStart,
    string TimeEnd,
    string Group,
    string Module,
    string Teacher,
    string Room,
    string LessonType,
    string Status,
    string Source
);
