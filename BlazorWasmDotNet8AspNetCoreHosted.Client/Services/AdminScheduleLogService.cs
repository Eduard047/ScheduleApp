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
            var items = JsonSerializer.Deserialize<List<AdminScheduleLogEntry>>(stored, JsonOptions);
            return items ?? new();
        }
        catch (JSException)
        {
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
        try
        {
            if (entries.Count == 0)
            {
                await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
                return;
            }
            var json = JsonSerializer.Serialize(entries, JsonOptions);
            await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
        }
        catch (JSException)
        {
        }
    }

    public async Task ClearAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
        }
        catch (JSException)
        {
        }
    }
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
