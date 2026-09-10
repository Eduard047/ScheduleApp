using System.Net;
using System.Reflection;
using System.Text;
using BlazorWasmDotNet8AspNetCoreHosted.Client.Services;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.JSInterop;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class AdminCrudReliabilityTests
{
    private const string ComponentNamespace =
        "BlazorWasmDotNet8AspNetCoreHosted.Client.Pages.";
    private static readonly BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData("AdminBuildings", "Admin", "SaveBuilding", "Корпус збережено", "Назва корпусу", "buildingEditorOpen")]
    [InlineData("AdminRooms", "Admin", "Save", "Аудиторію збережено", "Аудиторія 101", "editorOpen")]
    [InlineData("AdminGroups", "Api", "Save", "Групу збережено", "Група 1", "editorOpen")]
    [InlineData("AdminLessonTypes", "Api", "Save", "Тип заняття збережено", "LECTURE", null)]
    [InlineData("AdminCalendar", "Admin", "Save", "Виняток календаря збережено", "Свято", "editorOpen")]
    public async Task Api_crud_pages_keep_editor_state_and_block_repeat_save_when_refresh_fails(
        string componentName,
        string apiProperty,
        string saveMethod,
        string completedAction,
        string draftMarker,
        string? editorField)
    {
        var api = DispatchProxy.Create<IAdminApi, RecordingAdminApiProxy>();
        var proxy = (RecordingAdminApiProxy)(object)api;
        proxy.Handler = (method, _) => HandleSaveThenFailedRefresh(componentName, method);
        var component = CreateComponent(componentName);
        SetInjectedProperty(component, apiProperty, api);
        ConfigureDraft(componentName, component, draftMarker, editorField);

        await InvokeTask(component, saveMethod);

        Assert.True(GetField<bool>(component, "loadFailed"));
        Assert.True(GetProperty<bool>(component, "IsInteractionBlocked"));
        Assert.Null(GetField<string?>(component, "ok"));
        Assert.Contains(completedAction, GetField<string>(component, "error"));
        Assert.Contains("повторно зберігати", GetField<string>(component, "error"));
        Assert.Equal(draftMarker, GetDraftMarker(componentName, component));
        if (editorField is not null)
        {
            Assert.True(GetField<bool>(component, editorField));
        }
        Assert.Equal(1, proxy.MutationCalls);

        await InvokeTask(component, saveMethod);

        Assert.Equal(1, proxy.MutationCalls);
    }

    [Fact]
    public async Task Department_save_keeps_editor_state_and_blocks_repeat_when_refresh_fails()
    {
        var handler = new DepartmentSaveThenFailHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://schedule.test/")
        };
        var component = CreateComponent("AdminDepartments");
        SetInjectedProperty(component, "Http", http);
        SetField(component, "form", new DepartmentEditDto(null, "Кафедра тестування", true));
        SetField(component, "editorOpen", true);

        await InvokeTask(component, "Save");

        Assert.True(GetField<bool>(component, "loadFailed"));
        Assert.True(GetProperty<bool>(component, "IsInteractionBlocked"));
        Assert.Null(GetField<string?>(component, "ok"));
        Assert.Contains("Кафедру збережено", GetField<string>(component, "error"));
        Assert.Contains("повторно зберігати кафедру не потрібно", GetField<string>(component, "error"));
        Assert.Equal("Кафедра тестування", GetField<DepartmentEditDto>(component, "form").Name);
        Assert.True(GetField<bool>(component, "editorOpen"));
        Assert.Equal(1, handler.PostCalls);

        await InvokeTask(component, "Save");

        Assert.Equal(1, handler.PostCalls);
    }

    [Fact]
    public async Task Successful_delete_with_failed_refresh_blocks_accidental_repeat_until_reload()
    {
        var buildingLoadCalls = 0;
        var api = DispatchProxy.Create<IAdminApi, RecordingAdminApiProxy>();
        var proxy = (RecordingAdminApiProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.DeleteBuilding) => Task.CompletedTask,
            nameof(IAdminApi.GetBuildingCatalog) => ++buildingLoadCalls == 1
                ? Task.FromException<BuildingCatalogDto>(
                    new HttpRequestException("мережа недоступна"))
                : Task.FromResult(new BuildingCatalogDto(new(), new())),
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateComponent("AdminBuildings");
        SetInjectedProperty(component, "Admin", api);
        SetInjectedProperty(component, "JS", new ConfirmJsRuntime());
        GetField<List<BuildingEditDto>>(component, "buildings").Add(
            new BuildingEditDto(7, "Корпус 7", null));

        await InvokeTask(component, "DeleteBuilding", 7);

        Assert.True(GetField<bool>(component, "loadFailed"));
        Assert.Null(GetField<string?>(component, "ok"));
        Assert.Contains("Корпус видалено", GetField<string>(component, "error"));
        Assert.Contains("повторно видаляти корпус не потрібно", GetField<string>(component, "error"));
        Assert.Single(GetField<List<BuildingEditDto>>(component, "buildings"));
        Assert.Equal(1, proxy.MutationCalls);

        await InvokeTask(component, "DeleteBuilding", 7);

        Assert.Equal(1, proxy.MutationCalls);

        await InvokeTask(component, "RetryLoad");

        Assert.False(GetField<bool>(component, "loadFailed"));
        Assert.False(GetProperty<bool>(component, "IsInteractionBlocked"));
        Assert.Empty(GetField<List<BuildingEditDto>>(component, "buildings"));
        Assert.Equal("Видалено.", GetField<string>(component, "ok"));
    }

    [Fact]
    public async Task Room_dependency_conflict_requires_second_confirmation_before_force_delete()
    {
        var api = DispatchProxy.Create<IAdminApi, RecordingAdminApiProxy>();
        var proxy = (RecordingAdminApiProxy)(object)api;
        proxy.Handler = (method, args) => method.Name switch
        {
            nameof(IAdminApi.DeleteRoom) when !(bool)args![1]! =>
                Task.FromException(new ApiErrorException(
                    HttpStatusCode.Conflict,
                    "Аудиторія використовується.")),
            nameof(IAdminApi.DeleteRoom) => Task.CompletedTask,
            nameof(IAdminApi.GetRooms) => Task.FromResult(new List<RoomEditDto>()),
            nameof(IAdminApi.GetBuildings) => Task.FromResult(new List<BuildingEditDto>()),
            _ => throw new NotSupportedException(method.Name)
        };
        var js = new ConfirmJsRuntime();
        var component = CreateComponent("AdminRooms");
        SetInjectedProperty(component, "Admin", api);
        SetInjectedProperty(component, "JS", js);
        GetField<List<RoomEditDto>>(component, "items").Add(
            new RoomEditDto(7, "Аудиторія 7", 20, 1));

        await InvokeTask(component, "Delete", 7);

        Assert.Equal(new[] { false, true }, proxy.DeleteForces);
        Assert.Equal(2, js.ConfirmCalls);
        Assert.Contains("необов’язкові прив’язки очищено", GetField<string>(component, "ok"));
    }

    [Theory]
    [InlineData("AdminRooms.razor", "Поки що немає аудиторій.", "За вашим пошуком аудиторій не знайдено.")]
    [InlineData("AdminModules.razor", "Поки що немає модулів.", "За вашим пошуком модулів не знайдено.")]
    public void Reference_tables_distinguish_empty_data_from_empty_filter_results(
        string fileName,
        string emptyMessage,
        string noResultsMessage)
    {
        var markup = ReadAdminPage(fileName);

        Assert.Contains("class=\"admin-table-empty-state\" role=\"status\"", markup, StringComparison.Ordinal);
        Assert.Contains(emptyMessage, markup, StringComparison.Ordinal);
        Assert.Contains(noResultsMessage, markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Modal_recovery_retry_finishes_completed_save_without_repeating_mutation()
    {
        var buildingLoadCalls = 0;
        var api = DispatchProxy.Create<IAdminApi, RecordingAdminApiProxy>();
        var proxy = (RecordingAdminApiProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.UpsertBuilding) => Task.FromResult(41),
            nameof(IAdminApi.GetBuildingCatalog) => ++buildingLoadCalls == 1
                ? Task.FromException<BuildingCatalogDto>(
                    new HttpRequestException("мережа недоступна"))
                : Task.FromResult(new BuildingCatalogDto(
                    new() { new(41, "Новий корпус", "Адреса") },
                    new())),
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateComponent("AdminBuildings");
        SetInjectedProperty(component, "Admin", api);
        SetField(component, "form", new BuildingEditDto(null, "Новий корпус", "Адреса"));
        SetField(component, "buildingEditorOpen", true);

        await InvokeTask(component, "SaveBuilding");

        Assert.True(GetField<bool>(component, "loadFailed"));
        Assert.True(GetField<bool>(component, "buildingEditorOpen"));
        Assert.Equal(1, proxy.MutationCalls);

        await InvokeTask(component, "RetryLoad");

        Assert.False(GetField<bool>(component, "loadFailed"));
        Assert.False(GetField<bool>(component, "buildingEditorOpen"));
        Assert.Equal("Збережено.", GetField<string>(component, "ok"));
        Assert.Equal(1, proxy.MutationCalls);
    }

    [Fact]
    public async Task Shared_buildings_gate_blocks_duplicate_save_and_cross_entity_delete()
    {
        var mutationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = DispatchProxy.Create<IAdminApi, RecordingAdminApiProxy>();
        var proxy = (RecordingAdminApiProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.UpsertBuilding) => StartAndWait(mutationStarted, releaseMutation),
            nameof(IAdminApi.GetBuildingCatalog) => Task.FromResult(new BuildingCatalogDto(new(), new())),
            nameof(IAdminApi.DeleteBuildingTravel) => Task.CompletedTask,
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateComponent("AdminBuildings");
        SetInjectedProperty(component, "Admin", api);
        SetInjectedProperty(component, "JS", new ConfirmJsRuntime());
        SetField(component, "form", new BuildingEditDto(null, "Новий корпус", null));

        var firstSave = InvokeTask(component, "SaveBuilding");
        await mutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Task.WhenAll(
            InvokeTask(component, "SaveBuilding"),
            InvokeTask(component, "DeleteTravel", new BuildingTravelEditDto(1, 2, 10)))
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(GetField<bool>(component, "mutationInProgress"));
        Assert.Equal(1, proxy.MutationCalls);

        releaseMutation.TrySetResult(41);
        await firstSave.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(GetField<bool>(component, "mutationInProgress"));
        Assert.Equal(1, proxy.MutationCalls);
    }

    [Fact]
    public async Task Lesson_type_edit_blocks_save_until_palette_refresh_completes()
    {
        var releasePalette = new TaskCompletionSource<List<LessonColorDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = DispatchProxy.Create<IAdminApi, RecordingAdminApiProxy>();
        var proxy = (RecordingAdminApiProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.GetLessonColorPalette) => releasePalette.Task,
            nameof(IAdminApi.UpsertLessonType) => Task.CompletedTask,
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateComponent("AdminLessonTypes");
        SetInjectedProperty(component, "Api", api);
        var editTask = InvokeTask(
            component,
            "Edit",
            new LessonTypeEditDto { Id = 7, Code = "LECTURE", Name = "Лекція" });

        Assert.True(GetField<bool>(component, "paletteRefreshInProgress"));
        Assert.True(GetProperty<bool>(component, "IsInteractionBlocked"));

        await InvokeTask(component, "Save");

        Assert.Equal(0, proxy.MutationCalls);

        releasePalette.TrySetResult(new List<LessonColorDto>());
        await editTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(GetField<bool>(component, "paletteRefreshInProgress"));
        Assert.False(GetProperty<bool>(component, "IsInteractionBlocked"));
    }

    [Fact]
    public async Task Lesson_type_palette_ignores_stale_failure_after_newer_refresh()
    {
        var stalePalette = new TaskCompletionSource<List<LessonColorDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var paletteCalls = 0;
        var api = DispatchProxy.Create<IAdminApi, RecordingAdminApiProxy>();
        var proxy = (RecordingAdminApiProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.GetLessonColorPalette) when ++paletteCalls == 1 => stalePalette.Task,
            nameof(IAdminApi.GetLessonColorPalette) => Task.FromResult(new List<LessonColorDto>
            {
                new("fresh", "Свіжий", "#ffffff", false, null)
            }),
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateComponent("AdminLessonTypes");
        SetInjectedProperty(component, "Api", api);

        var staleTask = InvokeTask(component, "RefreshPalette");
        await InvokeTask(component, "RefreshPalette");
        SetField<string?>(component, "ok", "Збережено.");

        stalePalette.TrySetException(new HttpRequestException("застаріла помилка"));
        await staleTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(GetField<string?>(component, "error"));
        Assert.Equal("Збережено.", GetField<string>(component, "ok"));
        Assert.Equal("fresh", Assert.Single(GetField<List<LessonColorDto>>(component, "palette")).Key);
        Assert.False(GetField<bool>(component, "paletteRefreshInProgress"));
    }

    private static object? HandleSaveThenFailedRefresh(string componentName, MethodInfo method)
        => (componentName, method.Name) switch
        {
            ("AdminBuildings", nameof(IAdminApi.UpsertBuilding)) => Task.FromResult(41),
            ("AdminBuildings", nameof(IAdminApi.GetBuildingCatalog)) => Failed<BuildingCatalogDto>(),
            ("AdminRooms", nameof(IAdminApi.UpsertRoom)) => Task.FromResult(42),
            ("AdminRooms", nameof(IAdminApi.GetRooms)) => Failed<List<RoomEditDto>>(),
            ("AdminGroups", nameof(IAdminApi.UpsertGroup)) => Task.FromResult(43),
            ("AdminGroups", nameof(IAdminApi.GetGroups)) => Failed<List<GroupEditDto>>(),
            ("AdminLessonTypes", nameof(IAdminApi.UpsertLessonType)) => Task.CompletedTask,
            ("AdminLessonTypes", nameof(IAdminApi.GetLessonTypes)) => Failed<List<LessonTypeEditDto>>(),
            ("AdminCalendar", nameof(IAdminApi.UpsertCalendar)) => Task.FromResult(44),
            ("AdminCalendar", nameof(IAdminApi.GetCalendar)) => Failed<List<CalendarExceptionEditDto>>(),
            _ => throw new NotSupportedException($"{componentName}.{method.Name}")
        };

    private static Task<T> Failed<T>()
        => Task.FromException<T>(new HttpRequestException("мережа недоступна"));

    private static void ConfigureDraft(
        string componentName,
        object component,
        string marker,
        string? editorField)
    {
        switch (componentName)
        {
            case "AdminBuildings":
                SetField(component, "form", new BuildingEditDto(null, marker, "Адреса"));
                break;
            case "AdminRooms":
                SetField(component, "form", new RoomEditDto(null, marker, 25, 1));
                break;
            case "AdminGroups":
                SetField(component, "form", new GroupEditDto(null, marker, 20, 1));
                break;
            case "AdminLessonTypes":
                SetField(component, "form", new LessonTypeEditDto
                {
                    Code = marker,
                    Name = "Лекція"
                });
                break;
            case "AdminCalendar":
                SetField(component, "form", new CalendarExceptionEditDto(
                    null,
                    "2026-09-01",
                    false,
                    marker,
                    courseId: 1));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(componentName));
        }

        if (editorField is not null)
        {
            SetField(component, editorField, true);
        }
    }

    private static string GetDraftMarker(string componentName, object component)
        => componentName switch
        {
            "AdminBuildings" => GetField<BuildingEditDto>(component, "form").Name,
            "AdminRooms" => GetField<RoomEditDto>(component, "form").Name,
            "AdminGroups" => GetField<GroupEditDto>(component, "form").Name,
            "AdminLessonTypes" => GetField<LessonTypeEditDto>(component, "form").Code,
            "AdminCalendar" => GetField<CalendarExceptionEditDto>(component, "form").Name,
            _ => throw new ArgumentOutOfRangeException(nameof(componentName))
        };

    private static Task<int> StartAndWait(
        TaskCompletionSource<bool> started,
        TaskCompletionSource<int> release)
    {
        started.TrySetResult(true);
        return release.Task;
    }

    private static object CreateComponent(string componentName)
        => Activator.CreateInstance(GetComponentType(componentName))!;

    private static string ReadAdminPage(string fileName)
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

        throw new FileNotFoundException($"Не знайдено Razor-сторінку {fileName} від каталогу тестового процесу.");
    }

    private static Type GetComponentType(string componentName)
        => typeof(AdminApi).Assembly.GetType(
            ComponentNamespace + componentName,
            throwOnError: true)!;

    private static void SetInjectedProperty(object component, string propertyName, object value)
        => component.GetType().GetProperty(propertyName, InstanceMembers)!.SetValue(component, value);

    private static Task InvokeTask(object component, string methodName, params object?[] arguments)
        => Assert.IsAssignableFrom<Task>(
            component.GetType().GetMethod(methodName, InstanceMembers)!.Invoke(component, arguments));

    private static T GetField<T>(object component, string fieldName)
        => (T)component.GetType().GetField(fieldName, InstanceMembers)!.GetValue(component)!;

    private static void SetField<T>(object component, string fieldName, T value)
        => component.GetType().GetField(fieldName, InstanceMembers)!.SetValue(component, value);

    private static T GetProperty<T>(object component, string propertyName)
        => (T)component.GetType().GetProperty(propertyName, InstanceMembers)!.GetValue(component)!;

    public class RecordingAdminApiProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        public int MutationCalls { get; private set; }
        public List<bool> DeleteForces { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = Assert.IsAssignableFrom<MethodInfo>(targetMethod);
            if (method.Name.StartsWith("Upsert", StringComparison.Ordinal)
                || method.Name.StartsWith("Delete", StringComparison.Ordinal))
            {
                MutationCalls++;
            }
            if (method.Name is nameof(IAdminApi.DeleteRoom)
                or nameof(IAdminApi.DeleteTeacher)
                or nameof(IAdminApi.DeleteModule))
            {
                DeleteForces.Add((bool)args![1]!);
            }
            return Handler(method, args);
        }
    }

    private sealed class ConfirmJsRuntime : IJSRuntime
    {
        public int ConfirmCalls { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            ConfirmCalls++;
            return ValueTask.FromResult((TValue)(object)true);
        }
    }

    private sealed class DepartmentSaveThenFailHandler : HttpMessageHandler
    {
        public int PostCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                PostCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    "{\"detail\":\"мережа недоступна\"}",
                    Encoding.UTF8,
                    "application/problem+json")
            });
        }
    }
}
