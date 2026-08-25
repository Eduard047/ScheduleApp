using System.Collections;
using System.Reflection;
using BlazorWasmDotNet8AspNetCoreHosted.Client.Services;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class AdminModulesReliabilityTests
{
    private const string ModulesComponentTypeName =
        "BlazorWasmDotNet8AspNetCoreHosted.Client.Pages.AdminModules";
    private const string SequenceComponentTypeName =
        "BlazorWasmDotNet8AspNetCoreHosted.Client.Pages.AdminModuleSequence";
    private static readonly BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public async Task Sequence_failed_load_blocks_direct_save_and_preserves_server_state()
    {
        var api = DispatchProxy.Create<IAdminApi, RecordingAdminApiProxy>();
        var proxy = (RecordingAdminApiProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.GetModuleSequence) =>
                Task.FromException<ModuleSequenceConfigDto?>(new HttpRequestException("мережа недоступна")),
            nameof(IAdminApi.SaveModuleSequence) => Task.CompletedTask,
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateSequenceComponent(api, Module(11, 1));
        SetField(component, "_selectedCourseId", 1);

        await InvokeAsync(component, "LoadSequence", 1);
        await InvokeAsync(component, "SaveAsync");

        Assert.False(GetField<bool>(component, "_sequenceLoadSucceeded"));
        Assert.False(GetProperty<bool>(component, "CanSaveSequence"));
        Assert.Contains("Не вдалося завантажити конфігурацію", GetField<string>(component, "error"));
        Assert.Equal(0, proxy.SaveSequenceCalls);
    }

    [Fact]
    public async Task Sequence_partial_reference_failure_is_atomic_and_cannot_enable_empty_save()
    {
        var api = DispatchProxy.Create<IAdminApi, RecordingAdminApiProxy>();
        var proxy = (RecordingAdminApiProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.GetCourses) => Task.FromResult(new List<CourseEditDto>
            {
                new(1, "Курс 1", 16, new DateOnly(2026, 9, 1))
            }),
            nameof(IAdminApi.GetModules) =>
                Task.FromException<List<ModuleEditDto>>(new HttpRequestException("модулі недоступні")),
            nameof(IAdminApi.GetModuleSequence) =>
                Task.FromResult<ModuleSequenceConfigDto?>(Sequence(1, 11)),
            nameof(IAdminApi.SaveModuleSequence) => Task.CompletedTask,
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateSequenceComponent(api);

        await InvokeAsync(component, "OnInitializedAsync");

        Assert.False(GetField<bool>(component, "_referenceLoadSucceeded"));
        Assert.Empty(GetField<List<CourseEditDto>>(component, "courses"));
        Assert.Empty(GetField<List<ModuleEditDto>>(component, "modules"));
        Assert.Contains("довідники курсів і модулів", GetField<string>(component, "error"));

        SetField(component, "_selectedCourseId", 1);
        await InvokeAsync(component, "LoadSequence", 1);
        await InvokeAsync(component, "SaveAsync");

        Assert.False(GetProperty<bool>(component, "CanSaveSequence"));
        Assert.Equal(0, proxy.GetModuleSequenceCalls);
        Assert.Equal(0, proxy.SaveSequenceCalls);
    }

    [Fact]
    public async Task Sequence_stale_course_response_cannot_overwrite_newer_course()
    {
        var firstStarted = NewSignal();
        var releaseFirst = new TaskCompletionSource<ModuleSequenceConfigDto?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = DispatchProxy.Create<IAdminApi, RecordingAdminApiProxy>();
        var proxy = (RecordingAdminApiProxy)(object)api;
        proxy.Handler = (method, args) => method.Name switch
        {
            nameof(IAdminApi.GetModuleSequence) when (int)args![0]! == 1 =>
                CompleteSequenceAfter(firstStarted, releaseFirst),
            nameof(IAdminApi.GetModuleSequence) when (int)args![0]! == 2 =>
                Task.FromResult<ModuleSequenceConfigDto?>(Sequence(2, 22)),
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateSequenceComponent(api, Module(11, 1), Module(22, 2));
        SetField(component, "_selectedCourseId", 1);

        var firstLoad = InvokeTask(component, "LoadSequence", 1);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SetField(component, "_selectedCourseId", 2);
        await InvokeAsync(component, "LoadSequence", 2);

        Assert.Equal(new[] { 22 }, GetMainModuleIds(component));
        Assert.True(GetField<bool>(component, "_sequenceLoadSucceeded"));

        releaseFirst.TrySetResult(Sequence(1, 11));
        await firstLoad.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, GetField<int>(component, "_selectedCourseId"));
        Assert.Equal(new[] { 22 }, GetMainModuleIds(component));
        Assert.True(GetField<bool>(component, "_sequenceLoadSucceeded"));
        Assert.False(GetField<bool>(component, "isCourseLoading"));
    }

    [Fact]
    public async Task Sequence_save_captures_course_and_freezes_switch_and_duplicate_save()
    {
        var saveStarted = NewSignal();
        var releaseSave = NewSignal();
        var api = DispatchProxy.Create<IAdminApi, RecordingAdminApiProxy>();
        var proxy = (RecordingAdminApiProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.GetModuleSequence) =>
                Task.FromResult<ModuleSequenceConfigDto?>(Sequence(1, 11, fillerId: 12)),
            nameof(IAdminApi.SaveModuleSequence) => WaitForRelease(saveStarted, releaseSave),
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateSequenceComponent(api, Module(11, 1), Module(12, 1), Module(22, 2));
        SetField(component, "_selectedCourseId", 1);
        await InvokeAsync(component, "LoadSequence", 1);

        var firstSave = InvokeTask(component, "SaveAsync");
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(
            InvokeTask(component, "SaveAsync"),
            InvokeTask(component, "OnCourseChanged", new ChangeEventArgs { Value = "2" }))
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, proxy.SaveSequenceCalls);
        Assert.Equal(1, GetField<int>(component, "_selectedCourseId"));
        var payload = Assert.IsType<ModuleSequenceSaveRequestDto>(proxy.LastSequencePayload);
        Assert.Equal(1, payload.CourseId);
        Assert.Equal(new[] { 11 }, payload.MainModules.Select(item => item.ModuleId));
        Assert.Equal(new[] { 12 }, payload.FillerModuleIds);

        releaseSave.TrySetResult(true);
        await firstSave.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(GetField<bool>(component, "isSaving"));
        Assert.Equal("Збережено.", GetField<string>(component, "ok"));
    }

    [Fact]
    public async Task Module_save_gate_blocks_import_clear_delete_plan_and_topic_mutations()
    {
        var saveStarted = NewSignal();
        var releaseSave = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = DispatchProxy.Create<IAdminApi, RecordingAdminApiProxy>();
        var proxy = (RecordingAdminApiProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.UpsertModule) => CompleteIntAfter(saveStarted, releaseSave),
            nameof(IAdminApi.GetModules) => Task.FromResult(new List<ModuleEditDto>()),
            nameof(IAdminApi.GetMeta) => Task.FromResult(EmptyMeta()),
            nameof(IAdminApi.GetRooms) => Task.FromResult(new List<RoomEditDto>()),
            nameof(IAdminApi.GetBuildings) => Task.FromResult(new List<BuildingEditDto>()),
            _ => throw new NotSupportedException(method.Name)
        };
        var js = new RecordingJsRuntime();
        var component = CreateModulesComponent(api, js);
        SetField(component, "form", Module(id: null, courseId: 1));
        SetField(component, "moduleEditorOpen", true);
        SetField<IBrowserFile?>(component, "importFile", new TestBrowserFile());
        SetField<int?>(component, "plansModuleId", 7);

        var firstSave = InvokeTask(component, "Save");
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        InvokeVoid(component, "CloseModuleEditor");
        await Task.WhenAll(
            InvokeTask(component, "Save"),
            InvokeTask(component, "Delete", 7),
            InvokeTask(component, "ClearAllModules"),
            InvokeTask(component, "ImportDocx", true),
            InvokeTask(component, "OpenPlans", Module(7, 1)),
            InvokeTask(component, "SaveTopic"))
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(GetField<bool>(component, "_pageMutationInProgress"));
        Assert.True(GetField<bool>(component, "moduleEditorOpen"));
        Assert.Equal(1, proxy.UpsertModuleCalls);
        Assert.Equal(0, proxy.DeleteModuleCalls);
        Assert.Equal(0, proxy.ClearModulesCalls);
        Assert.Equal(0, proxy.ImportCalls);
        Assert.Equal(0, proxy.EnsureScopedModuleCalls);
        Assert.Equal(0, proxy.UpsertTopicCalls);
        Assert.Equal(0, js.InvocationCount);

        releaseSave.TrySetResult(42);
        await firstSave.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(GetField<bool>(component, "_pageMutationInProgress"));
        Assert.Equal(1, proxy.UpsertModuleCalls);
    }

    [Fact]
    public async Task Completed_module_save_with_failed_refresh_can_close_editor_and_retry_only_reads()
    {
        var api = DispatchProxy.Create<IAdminApi, RecordingAdminApiProxy>();
        var proxy = (RecordingAdminApiProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.UpsertModule) => Task.FromResult(42),
            nameof(IAdminApi.GetModules) when proxy.GetModulesCalls == 1 =>
                Task.FromException<List<ModuleEditDto>>(new HttpRequestException("мережа недоступна")),
            nameof(IAdminApi.GetModules) => Task.FromResult(new List<ModuleEditDto>()),
            nameof(IAdminApi.GetMeta) => Task.FromResult(EmptyMeta()),
            nameof(IAdminApi.GetRooms) => Task.FromResult(new List<RoomEditDto>()),
            nameof(IAdminApi.GetBuildings) => Task.FromResult(new List<BuildingEditDto>()),
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateModulesComponent(api, new RecordingJsRuntime());
        SetField(component, "form", Module(id: null, courseId: 1));
        SetField(component, "moduleEditorOpen", true);

        await InvokeAsync(component, "Save");

        Assert.Equal(1, proxy.UpsertModuleCalls);
        Assert.Equal(1, proxy.GetModulesCalls);
        Assert.True(GetField<bool>(component, "_refreshRecoveryRequired"));
        Assert.True(GetProperty<bool>(component, "IsPageInteractionBlocked"));
        Assert.False(GetProperty<bool>(component, "IsModalBusy"));
        Assert.True(GetField<bool>(component, "moduleEditorOpen"));
        Assert.Equal(42, GetField<ModuleEditDto>(component, "form").Id);
        Assert.Null(GetField<string?>(component, "ok"));
        Assert.Contains("Повторно зберігати модуль не потрібно", GetField<string>(component, "_refreshRecoveryMessage"));

        InvokeVoid(component, "CloseModuleEditor");
        Assert.False(GetField<bool>(component, "moduleEditorOpen"));
        Assert.True(GetField<bool>(component, "_refreshRecoveryRequired"));

        await InvokeAsync(component, "Save");
        Assert.Equal(1, proxy.UpsertModuleCalls);
        Assert.Equal(1, proxy.GetModulesCalls);

        await InvokeAsync(component, "RetryRefreshAsync");

        Assert.Equal(1, proxy.UpsertModuleCalls);
        Assert.Equal(2, proxy.GetModulesCalls);
        Assert.False(GetField<bool>(component, "_refreshRecoveryRequired"));
        Assert.False(GetField<bool>(component, "moduleEditorOpen"));
        Assert.Equal("Модуль успішно збережено.", GetField<string>(component, "ok"));
    }

    [Fact]
    public async Task Completed_topic_save_with_failed_refresh_can_close_editor_and_retry_only_reads()
    {
        var api = DispatchProxy.Create<IAdminApi, RecordingAdminApiProxy>();
        var proxy = (RecordingAdminApiProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.UpsertModuleTopic) => Task.FromResult(73),
            nameof(IAdminApi.GetModuleTopics) when proxy.GetModuleTopicsCalls == 1 =>
                Task.FromException<List<ModuleTopicViewDto>>(new HttpRequestException("мережа недоступна")),
            nameof(IAdminApi.GetModuleTopics) => Task.FromResult(new List<ModuleTopicViewDto>()),
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateModulesComponent(api, new RecordingJsRuntime());
        SetField<int?>(component, "plansModuleId", 7);
        SetField(component, "lessonTypes", new List<IdCodeNameDto>
        {
            new(3, "Л", "Лекція")
        });
        InvokeVoid(component, "ResetTopicForm", 7);
        SetField(component, "topicCodeInput", "1.1");
        SetField(component, "topicEditorOpen", true);

        await InvokeAsync(component, "SaveTopic");

        Assert.Equal(1, proxy.UpsertTopicCalls);
        Assert.Equal(1, proxy.GetModuleTopicsCalls);
        Assert.True(GetField<bool>(component, "_refreshRecoveryRequired"));
        Assert.True(GetProperty<bool>(component, "IsPageInteractionBlocked"));
        Assert.False(GetProperty<bool>(component, "IsModalBusy"));
        Assert.True(GetField<bool>(component, "topicEditorOpen"));

        InvokeVoid(component, "CloseTopicEditor");
        Assert.False(GetField<bool>(component, "topicEditorOpen"));
        Assert.True(GetField<bool>(component, "_refreshRecoveryRequired"));

        await InvokeAsync(component, "SaveTopic");
        Assert.Equal(1, proxy.UpsertTopicCalls);
        Assert.Equal(1, proxy.GetModuleTopicsCalls);

        await InvokeAsync(component, "RetryRefreshAsync");

        Assert.Equal(1, proxy.UpsertTopicCalls);
        Assert.Equal(2, proxy.GetModuleTopicsCalls);
        Assert.False(GetField<bool>(component, "_refreshRecoveryRequired"));
        Assert.False(GetField<bool>(component, "topicEditorOpen"));
        Assert.Equal("Заняття збережено.", GetField<string>(component, "ok"));
    }

    [Fact]
    public async Task Page_load_blocks_mutations_and_stale_load_cannot_overwrite_newer_snapshot()
    {
        var firstLoadStarted = NewSignal();
        var releaseFirstLoad = new TaskCompletionSource<List<ModuleEditDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = DispatchProxy.Create<IAdminApi, RecordingAdminApiProxy>();
        var proxy = (RecordingAdminApiProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.GetModules) when proxy.GetModulesCalls == 1 =>
                CompleteModulesAfter(firstLoadStarted, releaseFirstLoad),
            nameof(IAdminApi.GetModules) => Task.FromResult(new List<ModuleEditDto> { Module(22, 2) }),
            nameof(IAdminApi.GetMeta) => Task.FromResult(EmptyMeta()),
            nameof(IAdminApi.GetRooms) => Task.FromResult(new List<RoomEditDto>()),
            nameof(IAdminApi.GetBuildings) => Task.FromResult(new List<BuildingEditDto>()),
            nameof(IAdminApi.UpsertModule) => Task.FromResult(42),
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateModulesComponent(api, new RecordingJsRuntime());
        SetField(component, "form", Module(id: null, courseId: 1));

        var firstLoad = InvokeTask(component, "Load", false);
        await firstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(GetField<bool>(component, "_pageLoadInProgress"));
        Assert.True(GetProperty<bool>(component, "IsPageInteractionBlocked"));
        await InvokeAsync(component, "Save");
        Assert.Equal(0, proxy.UpsertModuleCalls);

        await InvokeAsync(component, "Load", false);
        Assert.Equal(new[] { 22 }, GetField<List<ModuleEditDto>>(component, "items").Select(x => x.Id!.Value));

        releaseFirstLoad.TrySetResult(new List<ModuleEditDto> { Module(11, 1) });
        await firstLoad.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { 22 }, GetField<List<ModuleEditDto>>(component, "items").Select(x => x.Id!.Value));
        Assert.False(GetField<bool>(component, "_pageLoadInProgress"));
        Assert.False(GetProperty<bool>(component, "IsPageInteractionBlocked"));
        Assert.Equal(0, proxy.UpsertModuleCalls);
    }

    [Fact]
    public async Task Course_plan_ensure_failure_is_in_modal_recovery_and_retry_only_reads_new_context()
    {
        var originalModule = Module(7, 1);
        var recoveredModule = new ModuleEditDto(
            8,
            originalModule.Code,
            originalModule.Title,
            2,
            credits: 1m);
        var api = DispatchProxy.Create<IAdminApi, RecordingAdminApiProxy>();
        var proxy = (RecordingAdminApiProxy)(object)api;
        proxy.Handler = (method, args) => method.Name switch
        {
            nameof(IAdminApi.EnsureCourseScopedModule) =>
                Task.FromException<int>(new HttpRequestException("невизначений результат запиту")),
            nameof(IAdminApi.GetModules) => Task.FromResult(new List<ModuleEditDto> { recoveredModule }),
            nameof(IAdminApi.GetMeta) => Task.FromResult(EmptyMeta()),
            nameof(IAdminApi.GetRooms) => Task.FromResult(new List<RoomEditDto>()),
            nameof(IAdminApi.GetBuildings) => Task.FromResult(new List<BuildingEditDto>()),
            nameof(IAdminApi.GetCourseModulePlan) => Task.FromResult(
                new CourseModulePlanDto((int)args![1]!, (int)args[0]!, 30, 0, true)),
            nameof(IAdminApi.GetModuleTopics) => Task.FromResult(new List<ModuleTopicViewDto>()),
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateModulesComponent(api, new RecordingJsRuntime());
        SetField(component, "items", new List<ModuleEditDto> { originalModule });
        SetField<int?>(component, "plansModuleId", 7);
        SetField(component, "planCourseIds", new List<int> { 1, 2 });
        SetField(component, "selectedPlanCourseId", 1);
        SetField(component, "plansModuleCode", originalModule.Code);
        SetField(component, "plansModuleTitle", originalModule.Title);

        await InvokeAsync(component, "OnPlanCourseChanged", new ChangeEventArgs { Value = "2" });

        Assert.Equal(1, proxy.EnsureScopedModuleCalls);
        Assert.True(GetField<bool>(component, "_refreshRecoveryRequired"));
        Assert.True(GetProperty<bool>(component, "IsPageInteractionBlocked"));
        Assert.Contains("Дані плану заблоковано", GetField<string>(component, "_refreshRecoveryMessage"));
        Assert.Null(GetField<string?>(component, "error"));

        InvokeVoid(component, "OpenCreateTopic");
        Assert.False(GetField<bool>(component, "topicEditorOpen"));

        await InvokeAsync(component, "RetryRefreshAsync");

        Assert.Equal(1, proxy.EnsureScopedModuleCalls);
        Assert.False(GetField<bool>(component, "_refreshRecoveryRequired"));
        Assert.Equal<int?>(8, GetField<int?>(component, "plansModuleId"));
        Assert.Equal(2, GetField<int>(component, "selectedPlanCourseId"));
        Assert.Equal("Дані плану модуля відновлено.", GetField<string>(component, "ok"));
    }

    private static object CreateSequenceComponent(IAdminApi api, params ModuleEditDto[] modules)
    {
        var component = Activator.CreateInstance(GetComponentType(SequenceComponentTypeName))!;
        GetComponentType(SequenceComponentTypeName).GetProperty("Admin", InstanceMembers)!.SetValue(component, api);
        SetField(component, "modules", modules.ToList());
        SetField(component, "moduleLookup", modules.Where(x => x.Id.HasValue).ToDictionary(x => x.Id!.Value));
        SetField(component, "_referenceLoadSucceeded", true);
        return component;
    }

    private static object CreateModulesComponent(IAdminApi api, IJSRuntime js)
    {
        var componentType = GetComponentType(ModulesComponentTypeName);
        var component = Activator.CreateInstance(componentType)!;
        componentType.GetProperty("Admin", InstanceMembers)!.SetValue(component, api);
        componentType.GetProperty("JS", InstanceMembers)!.SetValue(component, js);
        componentType.GetProperty("Http", InstanceMembers)!.SetValue(
            component,
            new HttpClient { BaseAddress = new Uri("https://schedule.test/") });
        return component;
    }

    private static ModuleEditDto Module(int id, int courseId)
        => Module((int?)id, courseId);

    private static ModuleEditDto Module(int? id, int courseId)
        => new(id, $"М-{id ?? 0}", $"Модуль {id ?? 0}", courseId, credits: 1m);

    private static ModuleSequenceConfigDto Sequence(int courseId, int mainId, int? fillerId = null)
        => new(
            courseId,
            new List<ModuleSequenceItemDto>
            {
                new(1, mainId, $"М-{mainId}", $"Модуль {mainId}", 1, 1)
            },
            fillerId.HasValue ? new List<int> { fillerId.Value } : new List<int>());

    private static MetaResponseDto EmptyMeta()
        => new(new(), new(), new(), new(), new(), new(), new())
        {
            Departments = new()
        };

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<ModuleSequenceConfigDto?> CompleteSequenceAfter(
        TaskCompletionSource<bool> started,
        TaskCompletionSource<ModuleSequenceConfigDto?> release)
    {
        started.TrySetResult(true);
        return await release.Task;
    }

    private static async Task<int> CompleteIntAfter(
        TaskCompletionSource<bool> started,
        TaskCompletionSource<int> release)
    {
        started.TrySetResult(true);
        return await release.Task;
    }

    private static async Task<List<ModuleEditDto>> CompleteModulesAfter(
        TaskCompletionSource<bool> started,
        TaskCompletionSource<List<ModuleEditDto>> release)
    {
        started.TrySetResult(true);
        return await release.Task;
    }

    private static async Task WaitForRelease(
        TaskCompletionSource<bool> started,
        TaskCompletionSource<bool> release)
    {
        started.TrySetResult(true);
        await release.Task;
    }

    private static Type GetComponentType(string typeName)
        => typeof(AdminApi).Assembly.GetType(typeName, throwOnError: true)!;

    private static async Task InvokeAsync(object component, string methodName, params object?[] arguments)
        => await InvokeTask(component, methodName, arguments);

    private static Task InvokeTask(object component, string methodName, params object?[] arguments)
        => Assert.IsAssignableFrom<Task>(
            component.GetType().GetMethod(methodName, InstanceMembers)!.Invoke(component, arguments));

    private static void InvokeVoid(object component, string methodName, params object?[] arguments)
        => component.GetType().GetMethod(methodName, InstanceMembers)!.Invoke(component, arguments);

    private static T GetField<T>(object component, string fieldName)
        => (T)component.GetType().GetField(fieldName, InstanceMembers)!.GetValue(component)!;

    private static T GetProperty<T>(object component, string propertyName)
        => (T)component.GetType().GetProperty(propertyName, InstanceMembers)!.GetValue(component)!;

    private static void SetField<T>(object component, string fieldName, T value)
        => component.GetType().GetField(fieldName, InstanceMembers)!.SetValue(component, value);

    private static int[] GetMainModuleIds(object component)
    {
        var entries = Assert.IsAssignableFrom<IEnumerable>(
            component.GetType().GetField("mainModules", InstanceMembers)!.GetValue(component));
        return entries.Cast<object>()
            .Select(entry => (int)entry.GetType().GetProperty("ModuleId", InstanceMembers)!.GetValue(entry)!)
            .ToArray();
    }

    public class RecordingAdminApiProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        public int SaveSequenceCalls { get; private set; }
        public int UpsertModuleCalls { get; private set; }
        public int DeleteModuleCalls { get; private set; }
        public int ClearModulesCalls { get; private set; }
        public int ImportCalls { get; private set; }
        public int EnsureScopedModuleCalls { get; private set; }
        public int UpsertTopicCalls { get; private set; }
        public int GetModulesCalls { get; private set; }
        public int GetModuleTopicsCalls { get; private set; }
        public int GetModuleSequenceCalls { get; private set; }
        public ModuleSequenceSaveRequestDto? LastSequencePayload { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = Assert.IsAssignableFrom<MethodInfo>(targetMethod);
            switch (method.Name)
            {
                case nameof(IAdminApi.SaveModuleSequence):
                    SaveSequenceCalls++;
                    LastSequencePayload = Assert.IsType<ModuleSequenceSaveRequestDto>(args![0]);
                    break;
                case nameof(IAdminApi.UpsertModule):
                    UpsertModuleCalls++;
                    break;
                case nameof(IAdminApi.DeleteModule):
                    DeleteModuleCalls++;
                    break;
                case nameof(IAdminApi.ClearModulesAndPlans):
                    ClearModulesCalls++;
                    break;
                case nameof(IAdminApi.ImportModulesFromDocx):
                    ImportCalls++;
                    break;
                case nameof(IAdminApi.EnsureCourseScopedModule):
                    EnsureScopedModuleCalls++;
                    break;
                case nameof(IAdminApi.UpsertModuleTopic):
                    UpsertTopicCalls++;
                    break;
                case nameof(IAdminApi.GetModules):
                    GetModulesCalls++;
                    break;
                case nameof(IAdminApi.GetModuleTopics):
                    GetModuleTopicsCalls++;
                    break;
                case nameof(IAdminApi.GetModuleSequence):
                    GetModuleSequenceCalls++;
                    break;
            }
            return Handler(method, args);
        }
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public int InvocationCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            InvocationCount++;
            object? value = identifier == "prompt" ? "ВИДАЛИТИ" : true;
            return ValueTask.FromResult((TValue)value!);
        }
    }

    private sealed class TestBrowserFile : IBrowserFile
    {
        public string Name => "modules.docx";
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => 8;
        public string ContentType => "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        public Stream OpenReadStream(long maxAllowedSize = 512_000, CancellationToken cancellationToken = default)
            => new MemoryStream(new byte[8], writable: false);
    }
}
