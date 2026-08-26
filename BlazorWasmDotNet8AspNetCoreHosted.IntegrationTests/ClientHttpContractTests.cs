using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorWasmDotNet8AspNetCoreHosted.Client.Services;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.JSInterop;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class ClientHttpContractTests
{
    [Fact]
    public async Task Autogen_plan_client_assembles_bounded_server_pages()
    {
        var handler = new AutogenPlanPagingHandler(totalChanges: 450);
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://schedule.test/")
        };

        var plan = await new TeacherDraftsApi(client).GetAutogenPlan("plan-1");

        Assert.Equal(450, plan.TotalChanges);
        Assert.Equal(450, plan.Changes.Count);
        Assert.False(plan.HasMoreChanges);
        Assert.Equal(new[] { 0, 200, 400 }, handler.RequestedOffsets);
        Assert.Equal(Enumerable.Range(1, 450), plan.Changes.Select(change => change.Ordinal));
    }

    [Fact]
    public async Task Autogen_plan_client_accepts_complete_legacy_response_without_paging_metadata()
    {
        var handler = new LegacyAutogenPlanHandler();
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://schedule.test/")
        };

        var plan = await new TeacherDraftsApi(client).GetAutogenPlan("plan-1");

        Assert.Equal(1, plan.TotalChanges);
        Assert.Single(plan.Changes);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Get_preserves_problem_details_validation_and_trace_context()
    {
        const string payload = """
            {
              "title": "Конфлікт",
              "detail": "Дані змінилися після завантаження.",
              "errors": { "Name": ["Назва вже використовується."] },
              "warnings": ["Оновіть список."],
              "traceId": "trace-42"
            }
            """;
        using var client = CreateClient(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/problem+json")
        });

        var exception = await Assert.ThrowsAsync<ApiErrorException>(() =>
            client.GetFromJsonWithDetailsAsync<object>("api/test"));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal("Дані змінилися після завантаження.", exception.Message);
        Assert.Equal(new[] { "Назва вже використовується." }, exception.Errors);
        Assert.Equal(new[] { "Оновіть список." }, exception.Warnings);
        Assert.Equal("trace-42", exception.TraceId);
    }

    [Fact]
    public async Task Get_returns_default_for_no_content_without_json_parsing()
    {
        using var client = CreateClient(new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await client.GetFromJsonWithDetailsAsync<TestPayload>("api/test");

        Assert.Null(result);
    }

    [Fact]
    public async Task Get_deserializes_success_payload_and_preserves_plain_text_error()
    {
        using var successClient = CreateClient(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"value\":17}", Encoding.UTF8, "application/json")
        });
        using var errorClient = CreateClient(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("Шлюз тимчасово недоступний.", Encoding.UTF8, "text/plain")
        });

        var payload = await successClient.GetFromJsonWithDetailsAsync<TestPayload>("api/test");
        var exception = await Assert.ThrowsAsync<ApiErrorException>(() =>
            errorClient.GetFromJsonWithDetailsAsync<TestPayload>("api/test"));

        Assert.Equal(17, payload?.Value);
        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Equal("Шлюз тимчасово недоступний.", exception.Message);
    }

    [Theory]
    [InlineData("course")]
    [InlineData("group")]
    [InlineData("lesson-type")]
    public async Task Admin_delete_preserves_conflict_status_for_ui_confirmation(string entity)
    {
        using var client = CreateClient(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"message\":\"Об’єкт використовується.\"}", Encoding.UTF8, "application/json")
        });
        var api = new AdminApi(client);

        var exception = await Assert.ThrowsAsync<ApiErrorException>(() => entity switch
        {
            "course" => api.DeleteCourse(7),
            "group" => api.DeleteGroup(7),
            _ => api.DeleteLessonType(7)
        });

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal("Об’єкт використовується.", exception.Message);
    }

    [Theory]
    [InlineData("course")]
    [InlineData("group")]
    public async Task Admin_force_delete_sends_force_query(string entity)
    {
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://schedule.test/") };
        var api = new AdminApi(client);

        if (entity == "course")
        {
            await api.DeleteCourse(7, force: true);
        }
        else
        {
            await api.DeleteGroup(7, force: true);
        }

        var requestUri = Assert.IsType<Uri>(handler.LastRequestUri);
        Assert.Contains("force=true", requestUri.Query, StringComparison.Ordinal);
    }

    private static HttpClient CreateClient(HttpResponseMessage response)
        => new(new StaticResponseHandler(response))
        {
            BaseAddress = new Uri("https://schedule.test/")
        };

    private sealed record TestPayload(int Value);

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(response);
        }
    }

    private sealed class AutogenPlanPagingHandler(int totalChanges) : HttpMessageHandler
    {
        public List<int> RequestedOffsets { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var query = request.RequestUri?.Query.TrimStart('?') ?? string.Empty;
            var offsetPair = query
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Single(parts => parts.Length == 2 && parts[0] == "changeOffset");
            var offset = int.Parse(Uri.UnescapeDataString(offsetPair[1]));
            RequestedOffsets.Add(offset);
            var count = Math.Min(200, Math.Max(0, totalChanges - offset));
            var now = DateTimeOffset.UtcNow;
            var details = new AutoGenPlanDetailsDto(
                new AutoGenPlanSummaryDto(
                    "plan-1",
                    AutoGenPlanState.Ready,
                    1,
                    now,
                    now.AddHours(1),
                    null,
                    null,
                    totalChanges,
                    0,
                    0,
                    true,
                    false),
                Enumerable.Range(offset + 1, count)
                    .Select(ordinal => new AutoGenPlanChangeDto(
                        ordinal,
                        AutoGenPlanOperation.Add,
                        null,
                        null))
                    .ToList(),
                new AutoGenResult(totalChanges, 0, new List<string>()),
                offset,
                totalChanges);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(details)
            });
        }
    }

    private sealed class LegacyAutogenPlanHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var now = DateTimeOffset.UtcNow;
            var details = new AutoGenPlanDetailsDto(
                new AutoGenPlanSummaryDto(
                    "plan-1",
                    AutoGenPlanState.Ready,
                    1,
                    now,
                    now.AddHours(1),
                    null,
                    null,
                    1,
                    0,
                    0,
                    true,
                    false),
                new List<AutoGenPlanChangeDto>
                {
                    new(1, AutoGenPlanOperation.Add, null, null)
                },
                new AutoGenResult(1, 0, new List<string>()));
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(details, options: options)
            });
        }
    }
}

public sealed class AdminTimeSlotsReliabilityTests
{
    private const string ComponentTypeName =
        "BlazorWasmDotNet8AspNetCoreHosted.Client.Pages.AdminTimeSlots";
    private static readonly BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public async Task Preferred_limit_success_enables_editor_with_server_value()
    {
        var component = CreateComponent(new SequenceResponseHandler(
            () => JsonResponse(HttpStatusCode.OK, """
                { "id": 1, "courseId": null, "maxSlotOrder": 4 }
                """)));

        await InvokeAsync(component, "LoadPreferredFirstSlotLimit", null, 0);

        Assert.True(GetField<bool>(component, "_hasLoadedPreferredFirstLimit"));
        Assert.False(GetField<bool>(component, "_preferredFirstLimitLoading"));
        Assert.Null(GetField<string?>(component, "_preferredFirstLimitLoadError"));
        Assert.Equal(4, GetField<int>(component, "_preferredFirstMaxSlotOrder"));
        Assert.Equal(4, GetField<int>(component, "_loadedPreferredFirstMaxSlotOrder"));
        Assert.True(GetProperty<bool>(component, "CanEditPreferredFirstLimit"));
        Assert.False(GetProperty<bool>(component, "CanSavePreferredFirstLimit"));
    }

    [Fact]
    public async Task Preferred_limit_failure_stays_unavailable_until_retry_succeeds()
    {
        var component = CreateComponent(new SequenceResponseHandler(
            () => JsonResponse(HttpStatusCode.ServiceUnavailable, """
                { "detail": "Ліміт тимчасово недоступний." }
                """, "application/problem+json"),
            () => JsonResponse(HttpStatusCode.OK, """
                { "id": 1, "courseId": null, "maxSlotOrder": 6 }
                """)));

        await InvokeAsync(component, "LoadPreferredFirstSlotLimit", null, 0);

        Assert.False(GetField<bool>(component, "_hasLoadedPreferredFirstLimit"));
        Assert.False(GetField<bool>(component, "_preferredFirstLimitLoading"));
        Assert.False(GetProperty<bool>(component, "CanEditPreferredFirstLimit"));
        Assert.False(GetProperty<bool>(component, "CanSavePreferredFirstLimit"));
        Assert.Contains(
            "Ліміт тимчасово недоступний",
            GetField<string>(component, "_preferredFirstLimitLoadError"),
            StringComparison.Ordinal);

        await InvokeAsync(component, "RetryPreferredFirstSlotLimitAsync");

        Assert.True(GetField<bool>(component, "_hasLoadedPreferredFirstLimit"));
        Assert.Null(GetField<string?>(component, "_preferredFirstLimitLoadError"));
        Assert.Equal(6, GetField<int>(component, "_preferredFirstMaxSlotOrder"));
        Assert.True(GetProperty<bool>(component, "CanEditPreferredFirstLimit"));
    }

    [Fact]
    public async Task Preferred_limit_retry_preserves_dirty_draft_against_refreshed_snapshot()
    {
        var component = CreateComponent(new SequenceResponseHandler(
            () => JsonResponse(HttpStatusCode.OK, """
                { "id": 1, "courseId": null, "maxSlotOrder": 4 }
                """),
            () => JsonResponse(HttpStatusCode.ServiceUnavailable, """
                { "detail": "Повторне завантаження не вдалося." }
                """, "application/problem+json"),
            () => JsonResponse(HttpStatusCode.OK, """
                { "id": 1, "courseId": null, "maxSlotOrder": 5 }
                """)));
        await InvokeAsync(component, "LoadPreferredFirstSlotLimit", null, 0);
        SetField(component, "_preferredFirstMaxSlotOrder", 7);

        await InvokeAsync(component, "LoadPreferredFirstSlotLimit", null, 0);

        Assert.True(GetProperty<bool>(component, "HasUnsavedPreferredFirstLimit"));
        Assert.False(GetProperty<bool>(component, "CanSavePreferredFirstLimit"));

        await InvokeAsync(component, "RetryPreferredFirstSlotLimitAsync");

        Assert.Equal(7, GetField<int>(component, "_preferredFirstMaxSlotOrder"));
        Assert.Equal(5, GetField<int>(component, "_loadedPreferredFirstMaxSlotOrder"));
        Assert.True(GetProperty<bool>(component, "HasUnsavedPreferredFirstLimit"));
        Assert.True(GetProperty<bool>(component, "CanSavePreferredFirstLimit"));
    }

    [Fact]
    public async Task Copy_preview_failure_is_not_reported_as_empty_and_retry_recovers()
    {
        var component = CreateComponent(new SequenceResponseHandler(
            () => JsonResponse(HttpStatusCode.BadGateway, """
                { "detail": "Шаблон тимчасово недоступний." }
                """, "application/problem+json"),
            () => JsonResponse(HttpStatusCode.OK, """
                {
                  "course": [],
                  "global": [
                    {
                      "id": 1,
                      "courseId": null,
                      "dayOfWeek": null,
                      "sortOrder": 1,
                      "start": "09:00",
                      "end": "09:45",
                      "isActive": true,
                      "isLunch": false
                    }
                  ]
                }
                """)));
        SetField(component, "_slotsLoadSucceeded", true);

        await InvokeAsync(component, "LoadCopyPreviewAsync");

        Assert.False(GetField<bool>(component, "_copyPreviewLoading"));
        Assert.False(GetProperty<bool>(component, "CanCopyToEditor"));
        Assert.Empty(GetField<List<TimeSlotDto>>(component, "_copyPreviewRows"));
        Assert.Contains(
            "Шаблон тимчасово недоступний",
            GetField<string>(component, "_copyPreviewError"),
            StringComparison.Ordinal);
        Assert.Contains(
            "недоступний",
            Invoke<string>(component, "GetCopyHint"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "немає слотів",
            Invoke<string>(component, "GetCopyHint"),
            StringComparison.Ordinal);

        await InvokeAsync(component, "LoadCopyPreviewAsync");

        Assert.False(GetField<bool>(component, "_copyPreviewLoading"));
        Assert.Null(GetField<string?>(component, "_copyPreviewError"));
        Assert.Single(GetField<List<TimeSlotDto>>(component, "_copyPreviewRows"));
        Assert.True(GetProperty<bool>(component, "CanCopyToEditor"));
    }

    [Fact]
    public async Task Preferred_limit_delayed_save_does_not_mark_new_course_draft_as_saved()
    {
        var handler = new DelayedMutationHandler();
        var component = CreateComponent(handler);
        SetField(component, "_hasLoadedPreferredFirstLimit", true);
        SetField<int?>(component, "_preferredFirstLimitCourseId", null);
        SetField(component, "_preferredFirstMaxSlotOrder", 7);
        SetField(component, "_loadedPreferredFirstMaxSlotOrder", 4);

        var saveTask = InvokeTask(component, "SavePreferredFirstSlotLimit");
        await handler.MutationStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(GetProperty<bool>(component, "AreContextControlsDisabled"));
        SetEnumField(component, "_scope", "Course");
        SetField<int?>(component, "_courseId", 2);
        SetField(component, "_contextLoadVersion", 1);
        SetField<int?>(component, "_preferredFirstLimitCourseId", 2);
        SetField(component, "_preferredFirstMaxSlotOrder", 9);
        SetField(component, "_loadedPreferredFirstMaxSlotOrder", 8);

        handler.CompleteMutation(new HttpResponseMessage(HttpStatusCode.NoContent));
        await saveTask;

        Assert.False(GetField<bool>(component, "_savingPreferredFirstLimit"));
        Assert.Equal(9, GetField<int>(component, "_preferredFirstMaxSlotOrder"));
        Assert.Equal(8, GetField<int>(component, "_loadedPreferredFirstMaxSlotOrder"));
        Assert.True(GetProperty<bool>(component, "HasUnsavedPreferredFirstLimit"));
        Assert.Null(GetField<string?>(component, "_ok"));
    }

    [Fact]
    public async Task Slot_delayed_save_does_not_reload_new_day_context()
    {
        var handler = new DelayedMutationHandler();
        var component = CreateComponent(handler);
        SetField(component, "_slotsLoadSucceeded", true);
        SetField(component, "_rows", Rows(1));
        SetField(component, "_loadedRowsSnapshot", new List<TimeSlotDto>());

        var saveTask = InvokeTask(component, "Save");
        await handler.MutationStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(GetProperty<bool>(component, "AreContextControlsDisabled"));
        SetField<int?>(component, "_dayOfWeek", 2);
        SetField(component, "_contextLoadVersion", 1);
        SetField(component, "_rows", Rows(9));
        SetField(component, "_loadedRowsSnapshot", Rows(8));

        handler.CompleteMutation(new HttpResponseMessage(HttpStatusCode.NoContent));
        await saveTask;

        Assert.False(GetField<bool>(component, "_savingSlots"));
        Assert.Equal(9, Assert.Single(GetField<List<TimeSlotDto>>(component, "_rows")).SortOrder);
        Assert.Equal(8, Assert.Single(GetField<List<TimeSlotDto>>(component, "_loadedRowsSnapshot")).SortOrder);
        Assert.Null(GetField<string?>(component, "_ok"));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Template_delayed_save_does_not_reload_new_day_context()
    {
        var handler = new DelayedMutationHandler(returnEmptyTemplateForGet: true);
        var component = CreateComponent(handler);
        SetField(component, "_slotsLoadSucceeded", true);
        SetField(component, "_rows", Rows(1));

        var saveTask = InvokeTask(component, "SaveEditorAsTemplateAsync");
        await handler.MutationStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(GetProperty<bool>(component, "AreContextControlsDisabled"));
        SetField<int?>(component, "_dayOfWeek", 2);
        SetField(component, "_contextLoadVersion", 1);
        SetField(component, "_rows", Rows(9));

        handler.CompleteMutation(new HttpResponseMessage(HttpStatusCode.NoContent));
        await saveTask;

        Assert.False(GetField<bool>(component, "_savingTemplate"));
        Assert.Equal(9, Assert.Single(GetField<List<TimeSlotDto>>(component, "_rows")).SortOrder);
        Assert.Null(GetField<string?>(component, "_ok"));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Slot_load_failure_blocks_direct_mutations_until_retry_succeeds()
    {
        var handler = new SlotsLoadRetryHandler();
        var component = CreateComponent(handler);

        await InvokeAsync(component, "InitializeAsync");

        Assert.True(GetField<bool>(component, "_metaLoadSucceeded"));
        Assert.False(GetField<bool>(component, "_initialLoadFailed"));
        Assert.False(GetField<bool>(component, "_slotsLoading"));
        Assert.False(GetField<bool>(component, "_slotsLoadSucceeded"));
        Assert.False(GetProperty<bool>(component, "CanMutateSlotEditor"));
        Assert.False(GetProperty<bool>(component, "AreContextControlsDisabled"));
        Assert.Contains("Не вдалося завантажити слоти", GetField<string>(component, "_error"));
        Assert.Contains("Редактор недоступний", Invoke<string>(component, "GetEditorStateHint"));
        Assert.DoesNotContain("Слотів у редакторі: 0", Invoke<string>(component, "GetEditorStateHint"));

        InvokeVoid(component, "AddRow");
        Assert.Empty(GetField<List<TimeSlotDto>>(component, "_rows"));
        SetField(component, "_rows", Rows(7));
        SetField(component, "_loadedRowsSnapshot", new List<TimeSlotDto>());

        await InvokeAsync(component, "Save");

        Assert.Equal(0, handler.PostCount);
        Assert.Null(GetField<string?>(component, "_ok"));

        await InvokeAsync(component, "RetrySlotsLoadAsync");

        Assert.False(GetField<bool>(component, "_slotsLoading"));
        Assert.True(GetField<bool>(component, "_slotsLoadSucceeded"));
        Assert.True(GetProperty<bool>(component, "CanMutateSlotEditor"));
        Assert.False(GetProperty<bool>(component, "AreContextControlsDisabled"));
        Assert.Equal(2, Assert.Single(GetField<List<TimeSlotDto>>(component, "_rows")).SortOrder);

        InvokeVoid(component, "AddRow");
        Assert.Equal(2, GetField<List<TimeSlotDto>>(component, "_rows").Count);
    }

    [Fact]
    public async Task Metadata_failure_keeps_context_controls_and_editor_disabled()
    {
        var component = CreateComponent(new MetadataFailureHandler());

        await InvokeAsync(component, "InitializeAsync");

        Assert.False(GetField<bool>(component, "_metaLoadSucceeded"));
        Assert.True(GetField<bool>(component, "_initialLoadFailed"));
        Assert.False(GetField<bool>(component, "_slotsLoadSucceeded"));
        Assert.True(GetProperty<bool>(component, "AreContextControlsDisabled"));
        Assert.False(GetProperty<bool>(component, "CanMutateSlotEditor"));
        Assert.Contains("Не вдалося завантажити довідник курсів", GetField<string>(component, "_error"));
        InvokeVoid(component, "AddRow");
        Assert.Empty(GetField<List<TimeSlotDto>>(component, "_rows"));
    }

    [Fact]
    public async Task Stale_context_load_cannot_unlock_editor_while_new_context_is_loading()
    {
        var handler = new DelayedContextLoadHandler();
        var component = CreateComponent(handler);

        var firstLoad = InvokeTask(component, "LoadRaw");
        await handler.FirstRawStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(GetField<bool>(component, "_slotsLoading"));
        Assert.False(GetField<bool>(component, "_slotsLoadSucceeded"));
        Assert.True(GetProperty<bool>(component, "AreContextControlsDisabled"));
        Assert.False(GetProperty<bool>(component, "CanMutateSlotEditor"));
        InvokeVoid(component, "AddRow");
        Assert.Empty(GetField<List<TimeSlotDto>>(component, "_rows"));

        SetField<int?>(component, "_dayOfWeek", 2);
        var secondLoad = InvokeTask(component, "LoadRaw");
        await handler.SecondRawStarted.WaitAsync(TimeSpan.FromSeconds(5));

        handler.CompleteFirstRaw(RawSlotsResponse(1));
        await firstLoad;

        Assert.True(GetField<bool>(component, "_slotsLoading"));
        Assert.False(GetField<bool>(component, "_slotsLoadSucceeded"));
        Assert.True(GetProperty<bool>(component, "AreContextControlsDisabled"));

        handler.CompleteSecondRaw(RawSlotsResponse(2));
        await secondLoad;

        Assert.False(GetField<bool>(component, "_slotsLoading"));
        Assert.True(GetField<bool>(component, "_slotsLoadSucceeded"));
        Assert.True(GetProperty<bool>(component, "CanMutateSlotEditor"));
        Assert.False(GetProperty<bool>(component, "AreContextControlsDisabled"));
        Assert.Equal(2, Assert.Single(GetField<List<TimeSlotDto>>(component, "_rows")).SortOrder);
    }

    private static object CreateComponent(HttpMessageHandler handler)
    {
        var componentType = typeof(TimeSlotsApi).Assembly.GetType(ComponentTypeName, throwOnError: true)!;
        var component = Activator.CreateInstance(componentType)!;
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://schedule.test/") };
        componentType.GetProperty("Api", InstanceMembers)!.SetValue(component, new TimeSlotsApi(client));
        componentType.GetProperty("AdminApi", InstanceMembers)!.SetValue(component, new AdminApi(client));
        SetField(component, "_metaLoadSucceeded", true);
        return component;
    }

    private static async Task InvokeAsync(object component, string methodName, params object?[] arguments)
    {
        await InvokeTask(component, methodName, arguments);
    }

    private static Task InvokeTask(object component, string methodName, params object?[] arguments)
        => Assert.IsAssignableFrom<Task>(
            component.GetType().GetMethod(methodName, InstanceMembers)!.Invoke(component, arguments));

    private static T Invoke<T>(object component, string methodName)
        => Assert.IsType<T>(component.GetType().GetMethod(methodName, InstanceMembers)!.Invoke(component, null));

    private static void InvokeVoid(object component, string methodName, params object?[] arguments)
        => component.GetType().GetMethod(methodName, InstanceMembers)!.Invoke(component, arguments);

    private static T GetField<T>(object component, string fieldName)
        => (T)component.GetType().GetField(fieldName, InstanceMembers)!.GetValue(component)!;

    private static void SetField<T>(object component, string fieldName, T value)
        => component.GetType().GetField(fieldName, InstanceMembers)!.SetValue(component, value);

    private static void SetEnumField(object component, string fieldName, string value)
    {
        var field = component.GetType().GetField(fieldName, InstanceMembers)!;
        field.SetValue(component, Enum.Parse(field.FieldType, value));
    }

    private static T GetProperty<T>(object component, string propertyName)
        => (T)component.GetType().GetProperty(propertyName, InstanceMembers)!.GetValue(component)!;

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode statusCode,
        string payload,
        string mediaType = "application/json")
        => new(statusCode)
        {
            Content = new StringContent(payload, Encoding.UTF8, mediaType)
        };

    private static HttpResponseMessage RawSlotsResponse(int sortOrder)
        => JsonResponse(HttpStatusCode.OK, $$"""
            {
              "course": [],
              "global": [
                {
                  "id": {{sortOrder}},
                  "courseId": null,
                  "dayOfWeek": null,
                  "sortOrder": {{sortOrder}},
                  "start": "09:00",
                  "end": "09:45",
                  "isActive": true,
                  "isLunch": false
                }
              ]
            }
            """);

    private static List<TimeSlotDto> Rows(int sortOrder)
        =>
        [
            new TimeSlotDto
            {
                Id = sortOrder,
                CourseId = null,
                DayOfWeek = null,
                SortOrder = sortOrder,
                Start = "09:00",
                End = "09:45",
                IsActive = true,
                IsLunch = false
            }
        ];

    private sealed class SequenceResponseHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.NotEmpty(_responses);
            return Task.FromResult(_responses.Dequeue()());
        }
    }

    private sealed class DelayedMutationHandler(bool returnEmptyTemplateForGet = false) : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpRequestMessage> _mutationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _mutationResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HttpRequestMessage> MutationStarted => _mutationStarted.Task;
        public int RequestCount { get; private set; }

        public void CompleteMutation(HttpResponseMessage response)
            => _mutationResponse.TrySetResult(response);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (returnEmptyTemplateForGet && request.Method == HttpMethod.Get)
            {
                return JsonResponse(HttpStatusCode.OK, """
                    { "course": [], "global": [] }
                    """);
            }

            _mutationStarted.TrySetResult(request);
            return await _mutationResponse.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class SlotsLoadRetryHandler : HttpMessageHandler
    {
        private int _rawGetCount;

        public int PostCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get)
            {
                PostCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            if (path.EndsWith("/api/meta", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, """
                    {
                      "courses": [], "groups": [], "teachers": [], "rooms": [],
                      "buildings": [], "lessonTypes": [], "lunches": []
                    }
                    """));
            }
            if (path.EndsWith("/slots/raw", StringComparison.Ordinal))
            {
                var requestNumber = Interlocked.Increment(ref _rawGetCount);
                return Task.FromResult(requestNumber == 1
                    ? JsonResponse(HttpStatusCode.ServiceUnavailable, """
                        { "detail": "Слоти тимчасово недоступні." }
                        """, "application/problem+json")
                    : RawSlotsResponse(2));
            }
            if (path.EndsWith("/config/lunch", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, "[]"));
            }
            if (path.EndsWith("/preferred-first-slot-limit", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, """
                    { "id": 1, "courseId": null, "maxSlotOrder": 4 }
                    """));
            }
            throw new InvalidOperationException($"Неочікуваний запит: {request.Method} {path}");
        }
    }

    private sealed class MetadataFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(JsonResponse(HttpStatusCode.ServiceUnavailable, """
                { "detail": "Довідник тимчасово недоступний." }
                """, "application/problem+json"));
    }

    private sealed class DelayedContextLoadHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _firstRawStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _secondRawStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _firstRawResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _secondRawResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _rawGetCount;

        public Task FirstRawStarted => _firstRawStarted.Task;
        public Task SecondRawStarted => _secondRawStarted.Task;

        public void CompleteFirstRaw(HttpResponseMessage response)
            => _firstRawResponse.TrySetResult(response);

        public void CompleteSecondRaw(HttpResponseMessage response)
            => _secondRawResponse.TrySetResult(response);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/slots/raw", StringComparison.Ordinal))
            {
                var requestNumber = Interlocked.Increment(ref _rawGetCount);
                if (requestNumber == 1)
                {
                    _firstRawStarted.TrySetResult(true);
                    return await _firstRawResponse.Task.WaitAsync(cancellationToken);
                }
                if (requestNumber == 2)
                {
                    _secondRawStarted.TrySetResult(true);
                    return await _secondRawResponse.Task.WaitAsync(cancellationToken);
                }
                return RawSlotsResponse(3);
            }
            if (path.EndsWith("/config/lunch", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, "[]");
            }
            if (path.EndsWith("/preferred-first-slot-limit", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, """
                    { "id": 1, "courseId": null, "maxSlotOrder": 4 }
                    """);
            }
            throw new InvalidOperationException($"Неочікуваний запит: {request.Method} {path}");
        }
    }
}

public sealed class AdminScheduleLogServiceTests
{
    [Fact]
    public async Task Load_corrupt_json_returns_empty_and_removes_poisoned_value()
    {
        var js = new LocalStorageJsRuntime("{not-json");
        var service = new AdminScheduleLogService(js);

        var entries = await service.LoadAsync();

        Assert.Empty(entries);
        Assert.Null(js.StoredValue);
        Assert.Contains("localStorage.removeItem", js.Invocations);
    }

    [Fact]
    public async Task Load_valid_json_preserves_log_entry()
    {
        var expected = new AdminScheduleLogEntry(
            Id: "entry-1",
            Timestamp: new DateTimeOffset(2026, 8, 24, 10, 15, 0, TimeSpan.Zero),
            ActionCode: "preview",
            ActionLabel: "Попередній перегляд",
            Summary: "Перевірено план",
            Success: true,
            Error: null,
            WeekStart: "2026-08-24",
            WeekEnd: "2026-08-28",
            DaysPreset: "Пн–Пт",
            AllowDaysOff: false,
            CourseId: 3,
            CourseName: "Курс 3",
            ModuleHours: new(),
            Warnings: new(),
            GapDetails: new(),
            Lessons: new(),
            LessonsTrimmed: false);
        var js = new LocalStorageJsRuntime(JsonSerializer.Serialize(new[] { expected }));
        var service = new AdminScheduleLogService(js);

        var entries = await service.LoadAsync();

        var actual = Assert.Single(entries);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Timestamp, actual.Timestamp);
        Assert.Equal(expected.Summary, actual.Summary);
        Assert.DoesNotContain("localStorage.removeItem", js.Invocations);
    }

    [Fact]
    public async Task Load_legacy_json_normalizes_missing_collections_and_ignores_null_rows()
    {
        const string legacyJson = """
            [
              null,
              {
                "Id": "legacy-1",
                "Timestamp": "2026-08-24T10:15:00+00:00",
                "ActionCode": "preview",
                "ActionLabel": "Попередній перегляд",
                "Summary": "Старий запис",
                "Success": true
              }
            ]
            """;
        var service = new AdminScheduleLogService(new LocalStorageJsRuntime(legacyJson));

        var entry = Assert.Single(await service.LoadAsync());

        Assert.Empty(entry.ModuleHours);
        Assert.Empty(entry.Warnings);
        Assert.Empty(entry.GapDetails);
        Assert.Empty(entry.Lessons);
    }

    [Fact]
    public async Task Load_js_failure_is_reported_to_page_instead_of_false_empty_log()
    {
        var service = new AdminScheduleLogService(
            new LocalStorageJsRuntime(null, failingIdentifier: "localStorage.getItem"));

        var exception = await Assert.ThrowsAsync<JSException>(() => service.LoadAsync());

        Assert.Contains("localStorage.getItem", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clear_js_failure_is_reported_and_keeps_stored_value()
    {
        var js = new LocalStorageJsRuntime("[]", failingIdentifier: "localStorage.removeItem");
        var service = new AdminScheduleLogService(js);

        var exception = await Assert.ThrowsAsync<JSException>(() => service.ClearAsync());

        Assert.Contains("localStorage.removeItem", exception.Message, StringComparison.Ordinal);
        Assert.Equal("[]", js.StoredValue);
    }

    [Fact]
    public async Task Corrupt_json_cleanup_failure_is_reported_instead_of_repeating_false_empty_state()
    {
        var js = new LocalStorageJsRuntime("{not-json", failingIdentifier: "localStorage.removeItem");
        var service = new AdminScheduleLogService(js);

        await Assert.ThrowsAsync<JSException>(() => service.LoadAsync());

        Assert.Equal("{not-json", js.StoredValue);
    }

    private sealed class LocalStorageJsRuntime(string? storedValue, string? failingIdentifier = null) : IJSRuntime
    {
        public string? StoredValue { get; private set; } = storedValue;
        public List<string> Invocations { get; } = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Invocations.Add(identifier);
            if (identifier == failingIdentifier)
            {
                throw new JSException($"Збій виклику {identifier}.");
            }
            if (identifier == "localStorage.getItem")
            {
                return ValueTask.FromResult((TValue)(object?)StoredValue!);
            }
            if (identifier == "localStorage.removeItem")
            {
                StoredValue = null;
            }
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}

public sealed class AdminCoursesDeleteReliabilityTests
{
    private const string ComponentTypeName =
        "BlazorWasmDotNet8AspNetCoreHosted.Client.Pages.AdminCourses";
    private static readonly BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public async Task Successful_delete_with_failed_refresh_does_not_report_plain_success_or_allow_repeat_delete()
    {
        var api = DispatchProxy.Create<IAdminApi, AdminApiDispatchProxy>();
        var proxy = (AdminApiDispatchProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.DeleteCourse) => Task.CompletedTask,
            nameof(IAdminApi.GetCourses) => Task.FromException<List<CourseEditDto>>(
                new HttpRequestException("мережа недоступна")),
            _ => throw new NotSupportedException(method.Name)
        };
        var component = Activator.CreateInstance(GetComponentType())!;
        GetComponentType().GetProperty("Api", InstanceMembers)!.SetValue(component, api);
        GetComponentType().GetProperty("JS", InstanceMembers)!.SetValue(component, new ConfirmJsRuntime());
        GetField<List<CourseEditDto>>(component, "items").Add(
            new CourseEditDto(7, "Курс 7", 16, new DateOnly(2026, 9, 1)));

        await InvokeAsync(component, "Delete", 7);

        Assert.True(GetField<bool>(component, "loadFailed"));
        Assert.Null(GetField<string?>(component, "ok"));
        Assert.Contains("Курс видалено, але оновити список не вдалося", GetField<string>(component, "error"));
        Assert.Contains("повторно видаляти курс не потрібно", GetField<string>(component, "error"));
        Assert.Single(GetField<List<CourseEditDto>>(component, "items"));
        Assert.Equal(1, proxy.DeleteCalls);
    }

    [Fact]
    public async Task Completed_save_with_failed_refresh_defers_success_and_resets_form_only_after_retry()
    {
        var loadCalls = 0;
        var api = DispatchProxy.Create<IAdminApi, AdminApiDispatchProxy>();
        var proxy = (AdminApiDispatchProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.UpsertCourse) => Task.FromResult(42),
            nameof(IAdminApi.GetCourses) => ++loadCalls == 1
                ? Task.FromException<List<CourseEditDto>>(
                    new HttpRequestException("мережа недоступна"))
                : Task.FromResult(new List<CourseEditDto>
                {
                    new(42, "Новий курс", 16, new DateOnly(2026, 9, 1))
                }),
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateComponent(api);
        SetField(
            component,
            "model",
            new CourseEditDto(null, "Новий курс", 16, new DateOnly(2026, 9, 1)));

        await InvokeAsync(component, "Save");

        Assert.Equal(1, proxy.UpsertCalls);
        Assert.True(GetField<bool>(component, "loadFailed"));
        Assert.Null(GetField<string?>(component, "ok"));
        Assert.Contains("повторно зберігати курс не потрібно", GetField<string>(component, "error"));
        Assert.Equal(42, GetField<CourseEditDto>(component, "model").Id);

        await InvokeAsync(component, "Save");

        Assert.Equal(1, proxy.UpsertCalls);

        await InvokeAsync(component, "RetryLoad");

        Assert.False(GetField<bool>(component, "loadFailed"));
        Assert.Equal("Збережено.", GetField<string>(component, "ok"));
        Assert.Null(GetField<CourseEditDto>(component, "model").Id);
        Assert.Equal("Новий курс", GetField<string>(component, "formTitle"));
    }

    [Fact]
    public async Task Delete_of_open_course_resets_editor_after_nonempty_refresh()
    {
        var api = DispatchProxy.Create<IAdminApi, AdminApiDispatchProxy>();
        var proxy = (AdminApiDispatchProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.DeleteCourse) => Task.CompletedTask,
            nameof(IAdminApi.GetCourses) => Task.FromResult(new List<CourseEditDto>
            {
                new(8, "Курс 8", 16, new DateOnly(2026, 9, 1))
            }),
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateComponent(api);
        SetField(
            component,
            "model",
            new CourseEditDto(7, "Курс 7", 16, new DateOnly(2026, 9, 1)));
        SetField(component, "formTitle", "Редагування: Курс 7");

        await InvokeAsync(component, "Delete", 7);

        Assert.Equal(1, proxy.DeleteCalls);
        Assert.Null(GetField<CourseEditDto>(component, "model").Id);
        Assert.Equal("Новий курс", GetField<string>(component, "formTitle"));
        Assert.Equal("Видалено.", GetField<string>(component, "ok"));
    }

    [Fact]
    public async Task Delete_of_open_course_with_failed_refresh_resets_editor_after_retry_without_repeat_delete()
    {
        var loadCalls = 0;
        var api = DispatchProxy.Create<IAdminApi, AdminApiDispatchProxy>();
        var proxy = (AdminApiDispatchProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.DeleteCourse) => Task.CompletedTask,
            nameof(IAdminApi.GetCourses) => ++loadCalls == 1
                ? Task.FromException<List<CourseEditDto>>(
                    new HttpRequestException("мережа недоступна"))
                : Task.FromResult(new List<CourseEditDto>
                {
                    new(8, "Курс 8", 16, new DateOnly(2026, 9, 1))
                }),
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateComponent(api);
        SetField(
            component,
            "model",
            new CourseEditDto(7, "Курс 7", 16, new DateOnly(2026, 9, 1)));
        SetField(component, "formTitle", "Редагування: Курс 7");

        await InvokeAsync(component, "Delete", 7);

        Assert.Equal(1, proxy.DeleteCalls);
        Assert.True(GetField<bool>(component, "loadFailed"));
        Assert.Equal(7, GetField<CourseEditDto>(component, "model").Id);

        await InvokeAsync(component, "RetryLoad");

        Assert.Equal(1, proxy.DeleteCalls);
        Assert.False(GetField<bool>(component, "loadFailed"));
        Assert.Null(GetField<CourseEditDto>(component, "model").Id);
        Assert.Equal("Новий курс", GetField<string>(component, "formTitle"));
        Assert.Equal("Видалено.", GetField<string>(component, "ok"));
    }

    [Fact]
    public async Task Delete_gate_blocks_duplicate_delete_and_save_until_refresh_completes()
    {
        var deleteStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = DispatchProxy.Create<IAdminApi, AdminApiDispatchProxy>();
        var proxy = (AdminApiDispatchProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.DeleteCourse) => StartAndWait(deleteStarted, releaseDelete),
            nameof(IAdminApi.GetCourses) => Task.FromResult(new List<CourseEditDto>()),
            nameof(IAdminApi.UpsertCourse) => Task.FromResult(7),
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateComponent(api);

        var firstDelete = InvokeTask(component, "Delete", 7);
        await deleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Task.WhenAll(
            InvokeTask(component, "Delete", 7),
            InvokeTask(component, "Save"))
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(GetField<bool>(component, "mutationInProgress"));
        Assert.Equal(1, proxy.DeleteCalls);
        Assert.Equal(0, proxy.UpsertCalls);

        releaseDelete.TrySetResult(true);
        await firstDelete.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(GetField<bool>(component, "mutationInProgress"));
        Assert.Equal(1, proxy.DeleteCalls);
        Assert.Equal(0, proxy.UpsertCalls);
    }

    [Fact]
    public async Task Save_gate_blocks_delete_until_save_refresh_completes()
    {
        var saveStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = DispatchProxy.Create<IAdminApi, AdminApiDispatchProxy>();
        var proxy = (AdminApiDispatchProxy)(object)api;
        proxy.Handler = (method, _) => method.Name switch
        {
            nameof(IAdminApi.UpsertCourse) => StartAndWait(saveStarted, releaseSave),
            nameof(IAdminApi.GetCourses) => Task.FromResult(new List<CourseEditDto>()),
            nameof(IAdminApi.DeleteCourse) => Task.CompletedTask,
            _ => throw new NotSupportedException(method.Name)
        };
        var component = CreateComponent(api);

        var firstSave = InvokeTask(component, "Save");
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await InvokeTask(component, "Delete", 7);

        Assert.True(GetField<bool>(component, "mutationInProgress"));
        Assert.Equal(1, proxy.UpsertCalls);
        Assert.Equal(0, proxy.DeleteCalls);

        releaseSave.TrySetResult(7);
        await firstSave.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(GetField<bool>(component, "mutationInProgress"));
        Assert.Equal(1, proxy.UpsertCalls);
        Assert.Equal(0, proxy.DeleteCalls);
    }

    private static object CreateComponent(IAdminApi api)
    {
        var component = Activator.CreateInstance(GetComponentType())!;
        GetComponentType().GetProperty("Api", InstanceMembers)!.SetValue(component, api);
        GetComponentType().GetProperty("JS", InstanceMembers)!.SetValue(component, new ConfirmJsRuntime());
        GetField<List<CourseEditDto>>(component, "items").Add(
            new CourseEditDto(7, "Курс 7", 16, new DateOnly(2026, 9, 1)));
        return component;
    }

    private static Task StartAndWait(
        TaskCompletionSource<bool> started,
        TaskCompletionSource<bool> release)
    {
        started.TrySetResult(true);
        return release.Task;
    }

    private static Task<int> StartAndWait(
        TaskCompletionSource<bool> started,
        TaskCompletionSource<int> release)
    {
        started.TrySetResult(true);
        return release.Task;
    }

    private static Type GetComponentType()
        => typeof(TimeSlotsApi).Assembly.GetType(ComponentTypeName, throwOnError: true)!;

    private static async Task InvokeAsync(object component, string methodName, params object?[] arguments)
    {
        await InvokeTask(component, methodName, arguments);
    }

    private static Task InvokeTask(object component, string methodName, params object?[] arguments)
        => Assert.IsAssignableFrom<Task>(
            component.GetType().GetMethod(methodName, InstanceMembers)!.Invoke(component, arguments));

    private static T GetField<T>(object component, string fieldName)
        => (T)component.GetType().GetField(fieldName, InstanceMembers)!.GetValue(component)!;

    private static void SetField<T>(object component, string fieldName, T value)
        => component.GetType().GetField(fieldName, InstanceMembers)!.SetValue(component, value);

    public class AdminApiDispatchProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        public int DeleteCalls { get; private set; }
        public int UpsertCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = Assert.IsAssignableFrom<MethodInfo>(targetMethod);
            if (method.Name == nameof(IAdminApi.DeleteCourse))
            {
                DeleteCalls++;
            }
            if (method.Name == nameof(IAdminApi.UpsertCourse))
            {
                UpsertCalls++;
            }
            return Handler(method, args);
        }
    }

    private sealed class ConfirmJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => ValueTask.FromResult((TValue)(object)true);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
            => ValueTask.FromResult((TValue)(object)true);
    }
}

public sealed class AdminScheduleLogsPageReliabilityTests
{
    private const string ComponentTypeName =
        "BlazorWasmDotNet8AspNetCoreHosted.Client.Pages.AdminScheduleLogs";
    private static readonly BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public async Task Reload_storage_failure_preserves_visible_entries_and_surfaces_retry_error()
    {
        var storage = new PageStorageJsRuntime(failGetCount: 1);
        var component = CreateComponent(storage);
        GetEntries(component).Add(CreateEntry());

        await InvokeAsync(component, "ReloadAsync");

        Assert.Single(GetEntries(component));
        Assert.Contains("Не вдалося завантажити журнал", GetField<string>(component, "_error"));
        Assert.False(GetField<bool>(component, "_retryClearAfterError"));
        Assert.False(GetField<bool>(component, "_loading"));
    }

    [Fact]
    public async Task Clear_storage_failure_preserves_entries_and_retry_clears_after_success()
    {
        var storage = new PageStorageJsRuntime(failRemoveCount: 1);
        var component = CreateComponent(storage);
        GetEntries(component).Add(CreateEntry());

        await InvokeAsync(component, "ClearLogsAsync");

        Assert.Single(GetEntries(component));
        Assert.Contains("Не вдалося очистити журнал", GetField<string>(component, "_error"));
        Assert.True(GetField<bool>(component, "_retryClearAfterError"));

        await InvokeAsync(component, "RetryFailedOperationAsync");

        Assert.Empty(GetEntries(component));
        Assert.Null(GetField<string?>(component, "_error"));
        Assert.False(GetField<bool>(component, "_retryClearAfterError"));
    }

    private static object CreateComponent(IJSRuntime storage)
    {
        var componentType = typeof(TimeSlotsApi).Assembly.GetType(ComponentTypeName, throwOnError: true)!;
        var component = Activator.CreateInstance(componentType)!;
        componentType.GetProperty("LogService", InstanceMembers)!
            .SetValue(component, new AdminScheduleLogService(storage));
        componentType.GetProperty("JS", InstanceMembers)!.SetValue(component, new ConfirmJsRuntime());
        return component;
    }

    private static List<AdminScheduleLogEntry> GetEntries(object component)
        => GetField<List<AdminScheduleLogEntry>>(component, "_entries");

    private static async Task InvokeAsync(object component, string methodName, params object?[] arguments)
    {
        var task = Assert.IsAssignableFrom<Task>(
            component.GetType().GetMethod(methodName, InstanceMembers)!.Invoke(component, arguments));
        await task;
    }

    private static T GetField<T>(object component, string fieldName)
        => (T)component.GetType().GetField(fieldName, InstanceMembers)!.GetValue(component)!;

    private static AdminScheduleLogEntry CreateEntry()
        => new(
            Id: "entry-1",
            Timestamp: DateTimeOffset.UtcNow,
            ActionCode: "preview",
            ActionLabel: "Попередній перегляд",
            Summary: "Тестовий запис",
            Success: true,
            Error: null,
            WeekStart: null,
            WeekEnd: null,
            DaysPreset: null,
            AllowDaysOff: null,
            CourseId: null,
            CourseName: null,
            ModuleHours: new(),
            Warnings: new(),
            GapDetails: new(),
            Lessons: new(),
            LessonsTrimmed: false);

    private sealed class PageStorageJsRuntime(int failGetCount = 0, int failRemoveCount = 0) : IJSRuntime
    {
        private int _remainingGetFailures = failGetCount;
        private int _remainingRemoveFailures = failRemoveCount;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier == "localStorage.getItem" && _remainingGetFailures-- > 0)
            {
                throw new JSException("localStorage.getItem недоступний");
            }
            if (identifier == "localStorage.removeItem" && _remainingRemoveFailures-- > 0)
            {
                throw new JSException("localStorage.removeItem недоступний");
            }
            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class ConfirmJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => ValueTask.FromResult((TValue)(object)true);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
            => ValueTask.FromResult((TValue)(object)true);
    }
}
