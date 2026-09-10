using System.Reflection;
using BlazorWasmDotNet8AspNetCoreHosted.Client.Services;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class ScheduleUiContractTests
{
    private const string ComponentNamespace =
        "BlazorWasmDotNet8AspNetCoreHosted.Client.Pages.";
    private static readonly BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public async Task Schedule_page_forwards_all_filters_and_keeps_base_context_for_empty_state()
    {
        var weekStart = new DateOnly(2026, 9, 7);
        var api = new RecordingScheduleApi(request => Task.FromResult(
            request.GroupId is null && request.TeacherId is null && request.RoomId is null
                ? new List<ScheduleItemDto> { CreateScheduleItem(11, weekStart) }
                : new List<ScheduleItemDto>()));
        var component = CreateScheduleComponent(api);
        SetField(component, "_weekStart", weekStart);
        SetField<int?>(component, "_courseId", 2);
        SetField<int?>(component, "_groupId", 3);
        SetField<int?>(component, "_teacherId", 4);
        SetField<int?>(component, "_roomId", 5);

        await InvokeTask(component, "LoadScheduleItemsAsync");

        Assert.Collection(
            api.Requests,
            request => Assert.Equal(
                new WeekRequest(weekStart, 2, 3, 4, 5),
                request),
            request => Assert.Equal(
                new WeekRequest(weekStart, 2, null, null, null),
                request));
        Assert.Empty(GetField<List<ScheduleItemDto>>(component, "_items"));
        Assert.True(GetField<bool>(component, "_hasScheduleItemsInContext"));
    }

    [Theory]
    [InlineData("_courseId", 20)]
    [InlineData("_groupId", 30)]
    [InlineData("_teacherId", 40)]
    [InlineData("_roomId", 50)]
    public async Task Schedule_page_ignores_response_when_any_filter_changes(
        string fieldName,
        int changedValue)
    {
        var weekStart = new DateOnly(2026, 9, 7);
        var filteredResponse = new TaskCompletionSource<List<ScheduleItemDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new RecordingScheduleApi(request =>
            request.GroupId is not null || request.TeacherId is not null || request.RoomId is not null
                ? filteredResponse.Task
                : Task.FromResult(new List<ScheduleItemDto> { CreateScheduleItem(11, weekStart) }));
        var component = CreateScheduleComponent(api);
        var currentItems = new List<ScheduleItemDto> { CreateScheduleItem(99, weekStart) };
        SetField(component, "_weekStart", weekStart);
        SetField<int?>(component, "_courseId", 2);
        SetField<int?>(component, "_groupId", 3);
        SetField<int?>(component, "_teacherId", 4);
        SetField<int?>(component, "_roomId", 5);
        SetField(component, "_items", currentItems);
        SetField<DateOnly?>(component, "_loadedWeekStart", weekStart);
        SetField<int?>(component, "_loadedCourseId", 2);

        var load = InvokeTask(component, "ReloadAsync", false);
        SetField<int?>(component, fieldName, changedValue);
        filteredResponse.TrySetResult(new List<ScheduleItemDto> { CreateScheduleItem(12, weekStart) });
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(99, Assert.Single(GetField<List<ScheduleItemDto>>(component, "_items")).Id);
    }

    [Theory]
    [InlineData(
        "AdminLessonTypes.razor",
        "SortedItems",
        "Поки що немає типів занять.",
        "За вашим пошуком типів занять не знайдено.")]
    [InlineData(
        "AdminCalendar.razor",
        "SortedAndFiltered",
        "Поки що немає календарних винятків.",
        "За вашим пошуком календарних винятків не знайдено.")]
    public void Admin_tables_distinguish_empty_data_from_empty_search_results(
        string fileName,
        string filteredCollection,
        string emptyMessage,
        string noResultsMessage)
    {
        var markup = ReadClientPage(fileName);

        Assert.Contains(
            $"@if (!loading && !loadFailed && !{filteredCollection}.Any())",
            markup,
            StringComparison.Ordinal);
        Assert.Contains("@(items.Count == 0", markup, StringComparison.Ordinal);
        Assert.Contains("class=\"admin-table-empty-state\" role=\"status\"", markup, StringComparison.Ordinal);
        Assert.Contains(emptyMessage, markup, StringComparison.Ordinal);
        Assert.Contains(noResultsMessage, markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Lesson_type_palette_has_distinct_loading_error_and_success_empty_states()
    {
        var markup = ReadClientPage("AdminLessonTypes.razor");

        Assert.Contains("paletteLoadState == PaletteLoadState.Loading", markup, StringComparison.Ordinal);
        Assert.Contains("paletteLoadState == PaletteLoadState.Failed", markup, StringComparison.Ordinal);
        Assert.Contains("else if (palette.Count == 0)", markup, StringComparison.Ordinal);
        Assert.Contains("На сервері немає доступних кольорів для вибору.", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("@if (palette.Count == 0)", markup, StringComparison.Ordinal);
    }

    private static object CreateScheduleComponent(IScheduleApi api)
    {
        var type = typeof(ScheduleApi).Assembly.GetType(
            ComponentNamespace + "Schedule",
            throwOnError: true)!;
        var component = Activator.CreateInstance(type)!;
        type.GetProperty("ScheduleApi", InstanceMembers)!.SetValue(component, api);
        return component;
    }

    private static Task InvokeTask(object component, string methodName, params object?[] arguments)
        => Assert.IsAssignableFrom<Task>(
            component.GetType().GetMethod(methodName, InstanceMembers)!.Invoke(component, arguments));

    private static T GetField<T>(object component, string fieldName)
        => (T)component.GetType().GetField(fieldName, InstanceMembers)!.GetValue(component)!;

    private static void SetField<T>(object component, string fieldName, T value)
        => component.GetType().GetField(fieldName, InstanceMembers)!.SetValue(component, value);

    private static ScheduleItemDto CreateScheduleItem(int id, DateOnly date)
        => new(
            Id: id,
            Date: date,
            TimeStart: "09:00",
            TimeEnd: "09:45",
            DayName: "понеділок",
            DayNumber: 1,
            Group: "Група",
            GroupId: 3,
            Module: "Модуль",
            ModuleId: 4,
            Teacher: "Викладач",
            TeacherId: 5,
            Room: "101",
            RoomId: 6,
            Building: "Головний",
            BuildingId: 7,
            RequiresRoom: true,
            LessonTypeId: 8,
            LessonTypeCode: "LECTURE",
            LessonTypeName: "Лекція",
            IsLocked: false,
            LessonTypeCss: "lec");

    private sealed record WeekRequest(
        DateOnly WeekStart,
        int? CourseId,
        int? GroupId,
        int? TeacherId,
        int? RoomId);

    private sealed class RecordingScheduleApi(
        Func<WeekRequest, Task<List<ScheduleItemDto>>> response) : IScheduleApi
    {
        public List<WeekRequest> Requests { get; } = new();

        public Task<MetaResponseDto> GetMeta(DateOnly? weekStart = null)
            => throw new NotSupportedException();

        public Task<List<ScheduleItemDto>> GetWeek(
            DateOnly weekStart,
            int? courseId = null,
            int? groupId = null,
            int? teacherId = null,
            int? roomId = null)
        {
            var request = new WeekRequest(weekStart, courseId, groupId, teacherId, roomId);
            Requests.Add(request);
            return response(request);
        }

        public Task<int> Upsert(UpsertScheduleItemRequest request)
            => throw new NotSupportedException();

        public Task Delete(int id, Guid expectedRevision)
            => throw new NotSupportedException();

        public Task<int> ClearWeek(ClearWeekRequest req)
            => throw new NotSupportedException();
    }

    private static string ReadClientPage(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "BlazorWasmDotNet8AspNetCoreHosted.Client",
                "Pages",
                fileName);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException(
            $"Не знайдено Razor-сторінку {fileName} від каталогу тестового процесу.");
    }
}
