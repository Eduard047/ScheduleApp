using System.Net;
using System.Reflection;
using System.Text;
using BlazorWasmDotNet8AspNetCoreHosted.Client.Services;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.JSInterop;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class AdminTeacherReliabilityTests
{
    private const string ComponentTypeName =
        "BlazorWasmDotNet8AspNetCoreHosted.Client.Pages.AdminTeachers";
    private static readonly BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public async Task Completed_create_with_failed_refresh_keeps_returned_id_and_blocks_duplicate_post()
    {
        var handler = new TeacherMutationHandler(HttpMethod.Post, failedListReads: 2);
        var component = CreateComponent(handler, new ConfirmJsRuntime());
        SetField(component, "edit", CreateTeacher(id: null));

        await InvokeAsync(component, "Save");

        Assert.Equal(1, handler.PostCount);
        Assert.Equal(1, handler.ListReadCount);
        Assert.True(GetField<bool>(component, "listLoadFailed"));
        Assert.True(GetProperty<bool>(component, "IsMutationBlocked"));
        Assert.Equal(42, Assert.IsType<TeacherEditDto>(GetField<TeacherEditDto?>(component, "edit")).Id);
        Assert.Contains(
            "Збереження викладача виконано, але список не оновлено; повторно зберігати не потрібно.",
            GetField<string>(component, "error"));

        await InvokeAsync(component, "Save");

        Assert.Equal(1, handler.PostCount);
        Assert.Equal(1, handler.ListReadCount);

        await InvokeAsync(component, "RetryListAsync");

        Assert.Equal(2, handler.ListReadCount);
        Assert.True(GetField<bool>(component, "listLoadFailed"));
        Assert.Contains(
            "повторно зберігати не потрібно",
            GetField<string>(component, "error"),
            StringComparison.Ordinal);

        await InvokeAsync(component, "RetryListAsync");

        Assert.Equal(3, handler.ListReadCount);
        Assert.False(GetField<bool>(component, "listLoadFailed"));
        Assert.Null(GetField<TeacherEditDto?>(component, "edit"));
        Assert.Contains("збереження вже було виконано", GetField<string>(component, "ok"));
    }

    [Fact]
    public async Task Completed_delete_with_failed_refresh_removes_stale_row_and_blocks_repeat_delete()
    {
        var js = new ConfirmJsRuntime();
        var handler = new TeacherMutationHandler(HttpMethod.Delete, failedListReads: 1);
        var component = CreateComponent(handler, js);
        GetTeachers(component).Add(new TeacherViewDto { Id = 7, FullName = "Тестовий викладач" });

        await InvokeAsync(component, "Delete", 7);

        Assert.Equal(1, js.ConfirmCalls);
        Assert.Equal(1, handler.DeleteCount);
        Assert.Equal(1, handler.ListReadCount);
        Assert.Empty(GetTeachers(component));
        Assert.True(GetField<bool>(component, "listLoadFailed"));
        Assert.Contains(
            "Видалення викладача виконано, але список не оновлено; повторно видаляти не потрібно.",
            GetField<string>(component, "error"));

        await InvokeAsync(component, "Delete", 7);

        Assert.Equal(1, js.ConfirmCalls);
        Assert.Equal(1, handler.DeleteCount);
        Assert.Equal(1, handler.ListReadCount);

        await InvokeAsync(component, "RetryListAsync");

        Assert.Equal(2, handler.ListReadCount);
        Assert.False(GetField<bool>(component, "listLoadFailed"));
        Assert.Empty(GetTeachers(component));
        Assert.Contains("видалення вже було виконано", GetField<string>(component, "ok"));
    }

    [Fact]
    public async Task Failed_metadata_load_blocks_editing_and_can_be_retried_without_reloading_valid_list()
    {
        var handler = new TeacherInitialLoadHandler();
        var component = CreateComponent(handler, new ConfirmJsRuntime());

        await InvokeAsync(component, "OnInitializedAsync");

        Assert.True(GetField<bool>(component, "metaLoadFailed"));
        Assert.False(GetField<bool>(component, "listLoadFailed"));
        Assert.True(GetProperty<bool>(component, "IsMutationBlocked"));
        Assert.Contains("Редагування заблоковано", GetField<string>(component, "error"));
        Assert.Equal(1, handler.MetaReadCount);
        Assert.Equal(1, handler.ListReadCount);

        Invoke(component, "CreateNew");

        Assert.Null(GetField<TeacherEditDto?>(component, "edit"));

        await InvokeAsync(component, "RetryListAsync");

        Assert.False(GetField<bool>(component, "metaLoadFailed"));
        Assert.False(GetProperty<bool>(component, "IsMutationBlocked"));
        Assert.Equal(2, handler.MetaReadCount);
        Assert.Equal(1, handler.ListReadCount);
        Assert.Contains("Довідники викладачів оновлено", GetField<string>(component, "ok"));
    }

    private static object CreateComponent(HttpMessageHandler handler, IJSRuntime js)
    {
        var componentType = typeof(AdminApi).Assembly.GetType(ComponentTypeName, throwOnError: true)!;
        var component = Activator.CreateInstance(componentType)!;
        componentType.GetProperty("Http", InstanceMembers)!
            .SetValue(component, new HttpClient(handler) { BaseAddress = new Uri("https://schedule.test/") });
        componentType.GetProperty("JS", InstanceMembers)!.SetValue(component, js);
        SetField(component, "loading", false);
        return component;
    }

    private static TeacherEditDto CreateTeacher(int? id)
        => new(
            id,
            "Тестовий викладач",
            scientificDegree: null,
            academicTitle: null,
            departmentId: null,
            moduleIds: new(),
            supervisorModuleIds: new(),
            loads: new(),
            workingHours: new());

    private static List<TeacherViewDto> GetTeachers(object component)
        => GetField<List<TeacherViewDto>>(component, "list");

    private static async Task InvokeAsync(object component, string methodName, params object?[] arguments)
    {
        var task = Assert.IsAssignableFrom<Task>(
            component.GetType().GetMethod(methodName, InstanceMembers)!.Invoke(component, arguments));
        await task;
    }

    private static object? Invoke(object component, string methodName, params object?[] arguments)
        => component.GetType().GetMethod(methodName, InstanceMembers)!.Invoke(component, arguments);

    private static T GetField<T>(object component, string fieldName)
        => (T)component.GetType().GetField(fieldName, InstanceMembers)!.GetValue(component)!;

    private static T GetProperty<T>(object component, string propertyName)
        => (T)component.GetType().GetProperty(propertyName, InstanceMembers)!.GetValue(component)!;

    private static void SetField<T>(object component, string fieldName, T value)
        => component.GetType().GetField(fieldName, InstanceMembers)!.SetValue(component, value);

    private sealed class TeacherMutationHandler(HttpMethod mutationMethod, int failedListReads) : HttpMessageHandler
    {
        private int _remainingFailedListReads = failedListReads;

        public int PostCount { get; private set; }
        public int DeleteCount { get; private set; }
        public int ListReadCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post
                && mutationMethod == HttpMethod.Post
                && path.EndsWith("/api/admin/teachers/upsert", StringComparison.Ordinal))
            {
                PostCount++;
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "42"));
            }
            if (request.Method == HttpMethod.Delete
                && mutationMethod == HttpMethod.Delete
                && path.EndsWith("/api/admin/teachers/7", StringComparison.Ordinal))
            {
                DeleteCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            if (request.Method == HttpMethod.Get
                && path.EndsWith("/api/admin/teachers", StringComparison.Ordinal))
            {
                ListReadCount++;
                if (_remainingFailedListReads-- > 0)
                {
                    return Task.FromResult(JsonResponse(
                        HttpStatusCode.ServiceUnavailable,
                        """{ "detail": "Список тимчасово недоступний." }""",
                        "application/problem+json"));
                }
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "[]"));
            }
            throw new InvalidOperationException($"Неочікуваний запит: {request.Method} {request.RequestUri}");
        }
    }

    private sealed class TeacherInitialLoadHandler : HttpMessageHandler
    {
        public int MetaReadCount { get; private set; }
        public int ListReadCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get
                && path.EndsWith("/api/meta", StringComparison.Ordinal))
            {
                MetaReadCount++;
                if (MetaReadCount == 1)
                {
                    return Task.FromResult(JsonResponse(
                        HttpStatusCode.ServiceUnavailable,
                        """{ "detail": "Довідники тимчасово недоступні." }""",
                        "application/problem+json"));
                }
                return Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    """
                    {
                      "courses": [],
                      "groups": [],
                      "teachers": [],
                      "rooms": [],
                      "buildings": [],
                      "lessonTypes": [],
                      "lunches": [],
                      "modules": [],
                      "calendar": [],
                      "departments": []
                    }
                    """));
            }
            if (request.Method == HttpMethod.Get
                && path.EndsWith("/api/admin/teachers", StringComparison.Ordinal))
            {
                ListReadCount++;
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "[]"));
            }
            throw new InvalidOperationException($"Неочікуваний запит: {request.Method} {request.RequestUri}");
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
            Assert.Equal("confirm", identifier);
            ConfirmCalls++;
            return ValueTask.FromResult((TValue)(object)true);
        }
    }

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode statusCode,
        string payload,
        string mediaType = "application/json")
        => new(statusCode)
        {
            Content = new StringContent(payload, Encoding.UTF8, mediaType)
        };
}
