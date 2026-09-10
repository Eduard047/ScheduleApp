using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorWasmDotNet8AspNetCoreHosted.Client.Services;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Components.Forms;
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
    public async Task Buildings_catalog_client_reads_buildings_and_travels_with_one_request()
    {
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                buildings = new[] { new BuildingEditDto(7, "Головний", "вул. Тестова, 1") },
                travels = new[] { new BuildingTravelEditDto(7, 9, 12) }
            })
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://schedule.test/") };

        var catalog = await new AdminApi(client).GetBuildingCatalog();

        Assert.Equal("Головний", Assert.Single(catalog.Buildings).Name);
        Assert.Equal(12, Assert.Single(catalog.Travels).Minutes);
        Assert.Equal("/api/admin/buildings", handler.LastRequestUri?.AbsolutePath);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Autogen_job_poll_propagates_cancellation_to_http_request()
    {
        var handler = new CancellationAwareHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://schedule.test/") };
        using var cancellation = new CancellationTokenSource();

        var request = new TeacherDraftsApi(client).GetAutogenJob("job-1", cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(handler.CancellationObserved);
    }

    [Fact]
    public async Task Autogen_plan_paging_propagates_cancellation_to_followup_request()
    {
        var handler = new BlockingSecondAutogenPlanPageHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://schedule.test/") };
        using var cancellation = new CancellationTokenSource();

        var request = new TeacherDraftsApi(client).GetAutogenPlan("plan-1", cancellation.Token);
        await handler.SecondPageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(handler.CancellationObserved);
    }

    [Fact]
    public async Task Teacher_drafts_client_disposes_completed_http_response()
    {
        var content = new TrackingStringContent(
            JsonSerializer.Serialize(CreateJobStatus()),
            Encoding.UTF8,
            "application/json");
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://schedule.test/") };

        var status = await new TeacherDraftsApi(client).GetAutogenJob("job-1");

        Assert.Equal("job-1", status.JobId);
        Assert.True(content.IsDisposed);
    }

    [Fact]
    public async Task Mutation_clients_dispose_completed_http_responses()
    {
        var scheduleContent = new TrackingStringContent("{}", Encoding.UTF8, "application/json");
        using (var scheduleClient = new HttpClient(new StaticResponseHandler(
                   new HttpResponseMessage(HttpStatusCode.NoContent) { Content = scheduleContent }))
               {
                   BaseAddress = new Uri("https://schedule.test/")
               })
        {
            await new ScheduleApi(scheduleClient).Delete(1, Guid.NewGuid());
        }

        var slotContent = new TrackingStringContent("{}", Encoding.UTF8, "application/json");
        using (var slotClient = new HttpClient(new StaticResponseHandler(
                   new HttpResponseMessage(HttpStatusCode.NoContent) { Content = slotContent }))
               {
                   BaseAddress = new Uri("https://schedule.test/")
               })
        {
            await new TimeSlotsApi(slotClient).SavePreferredFirstSlotLimitAsync(null, 1);
        }

        var adminContent = new TrackingStringContent("{}", Encoding.UTF8, "application/json");
        using (var adminClient = new HttpClient(new StaticResponseHandler(
                   new HttpResponseMessage(HttpStatusCode.NoContent) { Content = adminContent }))
               {
                   BaseAddress = new Uri("https://schedule.test/")
               })
        {
            await new AdminApi(adminClient).DeleteBuilding(1);
        }

        Assert.True(scheduleContent.IsDisposed);
        Assert.True(slotContent.IsDisposed);
        Assert.True(adminContent.IsDisposed);
    }

    [Fact]
    public async Task Docx_import_disposes_upload_stream_and_http_response()
    {
        var file = new TrackingBrowserFile();
        var content = new TrackingStringContent(
            JsonSerializer.Serialize(new DocxImportResultDto(
                "Курс",
                1,
                true,
                new(),
                new(),
                null)),
            Encoding.UTF8,
            "application/json");
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://schedule.test/") };

        var result = await new AdminApi(client).ImportModulesFromDocx(file, apply: false);

        Assert.Equal("Курс", result.CourseName);
        Assert.True(Assert.IsType<TrackingStream>(file.OpenedStream).IsDisposed);
        Assert.True(content.IsDisposed);
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
              "traceId": "trace-42",
              "code": "Stale"
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
        Assert.Equal("Stale", exception.Code);
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
    [InlineData("room")]
    [InlineData("teacher")]
    [InlineData("module")]
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
            "room" => api.DeleteRoom(7),
            "teacher" => api.DeleteTeacher(7),
            "module" => api.DeleteModule(7),
            _ => api.DeleteLessonType(7)
        });

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal("Об’єкт використовується.", exception.Message);
    }

    [Theory]
    [InlineData("course")]
    [InlineData("group")]
    [InlineData("room")]
    [InlineData("teacher")]
    [InlineData("module")]
    public async Task Admin_force_delete_sends_force_query(string entity)
    {
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://schedule.test/") };
        var api = new AdminApi(client);

        switch (entity)
        {
            case "course":
                await api.DeleteCourse(7, force: true);
                break;
            case "group":
                await api.DeleteGroup(7, force: true);
                break;
            case "room":
                await api.DeleteRoom(7, force: true);
                break;
            case "teacher":
                await api.DeleteTeacher(7, force: true);
                break;
            case "module":
                await api.DeleteModule(7, force: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entity));
        }

        var requestUri = Assert.IsType<Uri>(handler.LastRequestUri);
        Assert.Contains("force=true", requestUri.Query, StringComparison.Ordinal);
        Assert.Contains("confirm=true", requestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Teacher_drafts_client_exposes_only_job_based_autogeneration()
    {
        var legacyMethods = new[]
        {
            "AutogenWeek",
            "AutogenPreflightWeek",
            "AutogenMonth",
            "AutogenCourse"
        };

        foreach (var methodName in legacyMethods)
        {
            Assert.DoesNotContain(
                typeof(ITeacherDraftsApi).GetMethods(),
                method => method.Name == methodName);
            Assert.DoesNotContain(
                typeof(TeacherDraftsApi).GetMethods(),
                method => method.Name == methodName);
        }

        Assert.Contains(
            typeof(ITeacherDraftsApi).GetMethods(),
            method => method.Name == nameof(ITeacherDraftsApi.StartAutogenJob));
    }

    private static HttpClient CreateClient(HttpResponseMessage response)
        => new(new StaticResponseHandler(response))
        {
            BaseAddress = new Uri("https://schedule.test/")
        };

    private sealed record TestPayload(int Value);

    private static AutoGenJobStatus CreateJobStatus()
    {
        var now = DateTimeOffset.UtcNow;
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        return new AutoGenJobStatus(
            JobId: "job-1",
            State: AutoGenJobState.Succeeded,
            Kind: AutoGenJobKind.Generate,
            Title: "Автогенерація",
            CurrentStage: "Завершено",
            CreatedAt: now,
            StartedAt: now,
            CompletedAt: now,
            RangeStartDate: day,
            RangeEndDate: day,
            TotalWeeks: 1,
            CompletedWeeks: 1,
            CurrentWeekNumber: 1,
            CurrentWeekStartDate: day,
            CurrentRangeStartDate: day,
            CurrentRangeEndDate: day,
            Created: 0,
            Skipped: 0,
            WarningCount: 0,
            GapCount: 0,
            DeficitCount: 0,
            Percent: 100,
            CancellationRequested: false);
    }

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            RequestCount++;
            return Task.FromResult(response);
        }
    }

    private sealed class CancellationAwareHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> RequestStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }

            throw new InvalidOperationException("Запит мав бути скасований.");
        }
    }

    private sealed class TrackingStringContent(
        string content,
        Encoding encoding,
        string mediaType) : StringContent(content, encoding, mediaType)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed |= disposing;
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingBrowserFile : IBrowserFile
    {
        public string Name => "modules.docx";
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => 8;
        public string ContentType => "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        public Stream? OpenedStream { get; private set; }

        public Stream OpenReadStream(
            long maxAllowedSize = 512_000,
            CancellationToken cancellationToken = default)
        {
            OpenedStream = new TrackingStream(new byte[8]);
            return OpenedStream;
        }
    }

    private sealed class TrackingStream(byte[] buffer) : MemoryStream(buffer, writable: false)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed |= disposing;
            base.Dispose(disposing);
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

    private sealed class BlockingSecondAutogenPlanPageHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> SecondPageStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var isSecondPage = request.RequestUri?.Query.Contains(
                "changeOffset=200",
                StringComparison.Ordinal) == true;
            if (isSecondPage)
            {
                SecondPageStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }

                throw new InvalidOperationException("Другий запит мав бути скасований.");
            }

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
                    201,
                    0,
                    0,
                    true,
                    false),
                Enumerable.Range(1, 200)
                    .Select(ordinal => new AutoGenPlanChangeDto(
                        ordinal,
                        AutoGenPlanOperation.Add,
                        null,
                        null))
                    .ToList(),
                new AutoGenResult(201, 0, new List<string>()),
                0,
                201);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(details)
            };
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

public sealed class AdminTimeSlotSequenceEditorTests
{
    private const string ComponentTypeName =
        "BlazorWasmDotNet8AspNetCoreHosted.Client.Pages.AdminTimeSlots";
    private static readonly BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public async Task Context_load_uses_single_editor_request_and_hydrates_complete_state()
    {
        var handler = new SequenceHandler(EditorContextResponse("09:00", preferredLimit: 4));
        var component = CreateComponent(handler);

        await InvokeAsync(component, "InitializeAsync");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.EndsWith("/api/admin/config/slots/editor-context", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("targetMode=AllCourses", request.RequestUri.Query, StringComparison.Ordinal);
        Assert.True(GetField<bool>(component, "_metaLoadSucceeded"));
        Assert.True(GetField<bool>(component, "_slotsLoadSucceeded"));
        Assert.Equal("09:00", Assert.Single(GetField<List<TimeSlotDto>>(component, "_rows")).Start);
        Assert.Equal(4, GetField<int>(component, "_preferredFirstMaxSlotOrder"));
        Assert.Equal("revision-1", GetField<string>(component, "_currentRevision"));
        Assert.Equal(TimeSlotLunchMutationMode.Unchanged, GetField<TimeSlotLunchMutationMode>(component, "_lunchMutation"));
    }

    [Fact]
    public async Task Failed_initial_load_keeps_context_controls_disabled_until_retry_succeeds()
    {
        var handler = new SequenceHandler(
            JsonResponse(
                HttpStatusCode.ServiceUnavailable,
                """{ "detail": "EnableRetryOnFailure internal provider guidance" }""",
                "application/problem+json"),
            EditorContextResponse("10:00", preferredLimit: 3));
        var component = CreateComponent(handler);

        await InvokeAsync(component, "InitializeAsync");

        Assert.False(GetField<bool>(component, "_metaLoadSucceeded"));
        Assert.False(GetField<bool>(component, "_slotsLoadSucceeded"));
        Assert.True(GetField<bool>(component, "_initialLoadFailed"));
        Assert.True(GetProperty<bool>(component, "AreContextControlsDisabled"));
        var initialError = GetField<string>(component, "_error");
        Assert.Contains("тимчасову помилку", initialError, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableRetryOnFailure", initialError, StringComparison.Ordinal);

        await InvokeAsync(component, "SelectTargetAsync", TimeSlotEditorTargetMode.Course);

        Assert.Single(handler.Requests);
        Assert.Equal(TimeSlotEditorTargetMode.AllCourses, GetField<TimeSlotEditorTargetMode>(component, "_targetMode"));
        Assert.Equal(initialError, GetField<string>(component, "_error"));

        await InvokeAsync(component, "InitializeAsync");

        Assert.Equal(2, handler.Requests.Count);
        Assert.True(GetField<bool>(component, "_metaLoadSucceeded"));
        Assert.True(GetField<bool>(component, "_slotsLoadSucceeded"));
        Assert.False(GetField<bool>(component, "_initialLoadFailed"));
        Assert.False(GetProperty<bool>(component, "AreContextControlsDisabled"));
        Assert.Null(GetField<string?>(component, "_error"));
        Assert.Equal("10:00", Assert.Single(GetField<List<TimeSlotDto>>(component, "_rows")).Start);
    }

    [Fact]
    public async Task Transport_failure_shows_recoverable_message_without_internal_exception_details()
    {
        const string internalDetails = "EnableRetryOnFailure internal provider guidance";
        var component = CreateComponent(new ThrowingHandler(new HttpRequestException(internalDetails)));

        await InvokeAsync(component, "InitializeAsync");

        var error = GetField<string>(component, "_error");
        Assert.Contains("тимчасову помилку", error, StringComparison.Ordinal);
        Assert.Contains("повторіть спробу", error, StringComparison.Ordinal);
        Assert.DoesNotContain(internalDetails, error, StringComparison.Ordinal);
        Assert.True(GetField<bool>(component, "_initialLoadFailed"));
        Assert.True(GetProperty<bool>(component, "AreContextControlsDisabled"));
    }

    [Fact]
    public async Task Incomplete_course_context_clears_pending_lunch_intent_without_an_api_request()
    {
        var handler = new SequenceHandler();
        var component = CreateComponent(handler);
        SetField(component, "_targetMode", TimeSlotEditorTargetMode.Course);
        SetField<int?>(component, "_courseId", null);
        SetField(component, "_lunchMutation", TimeSlotLunchMutationMode.Remove);

        await InvokeAsync(component, "LoadRaw");

        Assert.Empty(handler.Requests);
        Assert.Equal(TimeSlotLunchMutationMode.Unchanged, GetField<TimeSlotLunchMutationMode>(component, "_lunchMutation"));
        Assert.False(GetProperty<bool>(component, "HasUnsavedSlotChanges"));
        Assert.Equal(string.Empty, GetField<string>(component, "_currentRevision"));
    }

    [Fact]
    public void Quick_builder_creates_a_numbered_non_overlapping_sequence()
    {
        var component = CreateReadyComponent();
        SetField(component, "_builderStart", "08:30");
        SetField(component, "_builderCount", 3);
        SetField(component, "_builderDurationMinutes", 45);
        SetField(component, "_builderBreakMinutes", 10);

        InvokeVoid(component, "BuildSequence");

        var rows = GetField<List<TimeSlotDto>>(component, "_rows");
        Assert.Collection(
            rows,
            row => AssertRow(row, 1, "08:30", "09:15"),
            row => AssertRow(row, 2, "09:25", "10:10"),
            row => AssertRow(row, 3, "10:20", "11:05"));
        Assert.True(GetProperty<bool>(component, "HasUnsavedSlotChanges"));
        Assert.Null(Invoke<string?>(component, "Validate"));
    }

    [Theory]
    [InlineData("10:25:00", "10:25")]
    [InlineData(" 09:00 ", "09:00")]
    public void Browser_time_values_are_normalized_to_the_editor_minute_format(
        string browserValue,
        string expected)
    {
        var componentType = typeof(TimeSlotsApi).Assembly.GetType(ComponentTypeName, throwOnError: true)!;
        var method = componentType.GetMethod(
            "NormalizeTimeInputValue",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.Equal(expected, method.Invoke(null, [browserValue]));
    }

    [Fact]
    public void Insert_after_shifts_following_periods_and_preserves_valid_breaks()
    {
        var component = CreateReadyComponent();
        SetField(component, "_rows", new List<TimeSlotDto>
        {
            Row(1, "09:00", "09:45"),
            Row(2, "09:55", "10:40")
        });

        InvokeVoid(component, "InsertAfter", 0);

        var rows = GetField<List<TimeSlotDto>>(component, "_rows");
        Assert.Collection(
            rows,
            row => AssertRow(row, 1, "09:00", "09:45"),
            row => AssertRow(row, 2, "09:55", "10:40"),
            row => AssertRow(row, 3, "10:50", "11:35"));
        Assert.Null(Invoke<string?>(component, "Validate"));
    }

    [Fact]
    public void Add_after_late_period_rejects_midnight_wrap_instead_of_appending_morning_time()
    {
        var component = CreateReadyComponent();
        SetField(component, "_rows", new List<TimeSlotDto> { Row(1, "23:30", "23:50") });

        InvokeVoid(component, "AddRow");

        var row = Assert.Single(GetField<List<TimeSlotDto>>(component, "_rows"));
        Assert.Equal("23:30", row.Start);
        Assert.Contains("за межі поточного дня", GetField<string>(component, "_error"), StringComparison.Ordinal);
    }

    [Fact]
    public void Move_buttons_derive_sort_order_from_visual_position()
    {
        var component = CreateReadyComponent();
        var first = Row(40, "09:00", "09:45");
        first.Id = 1;
        var second = Row(10, "09:55", "10:55");
        second.Id = 2;
        second.IsLunch = true;
        SetField(component, "_rows", new List<TimeSlotDto>
        {
            first,
            second
        });

        InvokeVoid(component, "MoveRow", 1, -1);

        var rows = GetField<List<TimeSlotDto>>(component, "_rows");
        Assert.Equal(new[] { 2, 1 }, rows.Select(row => row.Id));
        Assert.Equal(new[] { "09:00", "10:10" }, rows.Select(row => row.Start));
        Assert.Equal(new[] { "10:00", "10:55" }, rows.Select(row => row.End));
        Assert.Equal(new[] { 1, 2 }, rows.Select(row => row.SortOrder));
        Assert.Equal(TimeSlotLunchMutationMode.Set, GetField<TimeSlotLunchMutationMode>(component, "_lunchMutation"));
        Assert.Null(Invoke<string?>(component, "Validate"));
    }

    [Fact]
    public void Explicit_lunch_assignment_and_removal_change_only_lunch_intent_until_row_state_changes()
    {
        var component = CreateReadyComponent();
        SetField(component, "_rows", new List<TimeSlotDto> { Row(1, "12:00", "12:45") });
        SetField(component, "_loadedRowsSnapshot", new List<TimeSlotDto> { Row(1, "12:00", "12:45") });

        InvokeVoid(component, "ToggleLunch", 0, new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

        Assert.Equal(TimeSlotLunchMutationMode.Set, GetField<TimeSlotLunchMutationMode>(component, "_lunchMutation"));
        Assert.False(GetProperty<bool>(component, "HasUnsavedSequenceChanges"));
        Assert.True(GetProperty<bool>(component, "HasUnsavedLunchChanges"));
        Assert.True(GetProperty<bool>(component, "CanPreviewChanges"));

        InvokeVoid(component, "ToggleLunch", 0, new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = false });

        Assert.Equal(TimeSlotLunchMutationMode.Remove, GetField<TimeSlotLunchMutationMode>(component, "_lunchMutation"));
        Assert.False(GetProperty<bool>(component, "HasUnsavedSequenceChanges"));
    }

    [Fact]
    public void Empty_sequence_requires_explicit_clear_intent()
    {
        var component = CreateReadyComponent();
        SetField(component, "_rows", new List<TimeSlotDto>());
        SetField(component, "_loadedRowsSnapshot", new List<TimeSlotDto> { Row(1, "09:00", "09:45") });

        Assert.False(GetProperty<bool>(component, "CanPreviewChanges"));
        Assert.Contains("підтвердьте явний намір", Invoke<string>(component, "Validate"), StringComparison.Ordinal);

        SetField(component, "_clearRequested", true);

        Assert.True(GetProperty<bool>(component, "CanPreviewChanges"));
        Assert.Null(Invoke<string?>(component, "Validate"));
        var request = Invoke<TimeSlotSequenceApplyRequestDto>(component, "BuildRequest", (object?)null);
        Assert.True(request.ApplySlots);
        Assert.True(request.Clear);
        Assert.Empty(request.Slots);
        Assert.Equal(TimeSlotLunchMutationMode.Unchanged, request.LunchMutation);
        Assert.Null(request.LunchSlot);
    }

    [Fact]
    public void Reset_to_shared_request_never_combines_reset_with_slot_replacement()
    {
        var component = CreateReadyComponent();
        SetField(component, "_targetMode", TimeSlotEditorTargetMode.Course);
        SetField<int?>(component, "_courseId", 4);
        SetField(component, "_rows", new List<TimeSlotDto> { Row(1, "09:00", "09:45") });
        SetField(component, "_resetCourseToGlobal", true);

        var request = Invoke<TimeSlotSequenceApplyRequestDto>(component, "BuildRequest", (object?)null);

        Assert.True(request.ResetCourseToGlobal);
        Assert.False(request.ApplySlots);
        Assert.True(GetProperty<bool>(component, "CanPreviewChanges"));
        Assert.False(request.Clear);
        Assert.Empty(request.Slots);
        Assert.Equal(4, request.CourseId);
        Assert.Equal(TimeSlotLunchMutationMode.Unchanged, request.LunchMutation);
        Assert.Null(request.LunchSlot);
    }

    [Fact]
    public void Lunch_only_set_sends_selected_interval_without_replacing_slots()
    {
        var component = CreateReadyComponent();
        var draft = Row(1, "12:00", "12:45");
        draft.IsLunch = true;
        SetField(component, "_rows", new List<TimeSlotDto> { draft });
        SetField(component, "_loadedRowsSnapshot", new List<TimeSlotDto> { Row(1, "12:00", "12:45") });
        SetField(component, "_lunchMutation", TimeSlotLunchMutationMode.Set);

        var request = Invoke<TimeSlotSequenceApplyRequestDto>(component, "BuildRequest", (object?)null);

        Assert.False(GetProperty<bool>(component, "HasUnsavedSequenceChanges"));
        Assert.True(GetProperty<bool>(component, "HasUnsavedLunchChanges"));
        Assert.False(request.ApplySlots);
        Assert.True(GetProperty<bool>(component, "CanPreviewChanges"));
        Assert.Empty(request.Slots);
        Assert.False(request.Clear);
        Assert.Null(request.DayOfWeek);
        Assert.Equal(TimeSlotLunchMutationMode.Set, request.LunchMutation);
        Assert.NotNull(request.LunchSlot);
        Assert.Equal("12:00", request.LunchSlot.Start);
        Assert.Equal("12:45", request.LunchSlot.End);
        Assert.True(request.LunchSlot.IsLunch);
    }

    [Fact]
    public void Lunch_only_remove_sends_no_slots_or_lunch_interval()
    {
        var component = CreateReadyComponent();
        SetField(component, "_rows", new List<TimeSlotDto> { Row(1, "12:00", "12:45") });
        SetField(component, "_loadedRowsSnapshot", new List<TimeSlotDto> { Row(1, "12:00", "12:45") });
        SetField(component, "_lunchMutation", TimeSlotLunchMutationMode.Remove);

        var request = Invoke<TimeSlotSequenceApplyRequestDto>(component, "BuildRequest", (object?)null);

        Assert.False(request.ApplySlots);
        Assert.True(GetProperty<bool>(component, "CanPreviewChanges"));
        Assert.Empty(request.Slots);
        Assert.False(request.Clear);
        Assert.Equal(TimeSlotLunchMutationMode.Remove, request.LunchMutation);
        Assert.Null(request.LunchSlot);
    }

    [Fact]
    public void Course_lunch_removal_targets_only_its_override_and_cancel_restores_the_marker()
    {
        var component = CreateReadyComponent();
        SetField(component, "_targetMode", TimeSlotEditorTargetMode.Course);
        SetField<int?>(component, "_courseId", 4);
        SetField(component, "_context", new TimeSlotEditorContextDto
        {
            TargetMode = TimeSlotEditorTargetMode.Course,
            CourseId = 4,
            ExplicitLunch = new LunchConfigEditDto(7, 4, "12:00", "12:45"),
            EffectiveLunch = new LunchConfigEditDto(7, 4, "12:00", "12:45"),
            CurrentRevision = "revision-1"
        });
        var row = Row(1, "12:00", "12:45");
        row.IsLunch = true;
        SetField(component, "_rows", new List<TimeSlotDto> { row });
        var snapshot = Row(1, "12:00", "12:45");
        snapshot.IsLunch = true;
        SetField(component, "_loadedRowsSnapshot", new List<TimeSlotDto> { snapshot });

        InvokeVoid(component, "RequestLunchRemoval");
        var request = Invoke<TimeSlotSequenceApplyRequestDto>(component, "BuildRequest", (object?)null);

        Assert.Equal(4, request.CourseId);
        Assert.False(request.ApplySlots);
        Assert.Empty(request.Slots);
        Assert.Equal(TimeSlotLunchMutationMode.Remove, request.LunchMutation);
        Assert.Null(request.LunchSlot);
        Assert.DoesNotContain(GetField<List<TimeSlotDto>>(component, "_rows"), slot => slot.IsLunch);

        InvokeVoid(component, "CancelLunchRemoval");

        Assert.Equal(TimeSlotLunchMutationMode.Unchanged, GetField<TimeSlotLunchMutationMode>(component, "_lunchMutation"));
        Assert.True(Assert.Single(GetField<List<TimeSlotDto>>(component, "_rows")).IsLunch);
    }

    [Fact]
    public void Inherited_lunch_cannot_be_unchecked_but_selecting_another_row_creates_an_explicit_set()
    {
        var component = CreateInheritedLunchComponent();

        InvokeVoid(component, "ToggleLunch", 0, new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = false });

        Assert.True(GetField<List<TimeSlotDto>>(component, "_rows")[0].IsLunch);
        Assert.Equal(TimeSlotLunchMutationMode.Unchanged, GetField<TimeSlotLunchMutationMode>(component, "_lunchMutation"));
        Assert.Contains("успадкована зі спільного графіка", GetField<string>(component, "_ok"), StringComparison.Ordinal);

        InvokeVoid(component, "ToggleLunch", 1, new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        var request = Invoke<TimeSlotSequenceApplyRequestDto>(component, "BuildRequest", (object?)null);

        var rows = GetField<List<TimeSlotDto>>(component, "_rows");
        Assert.False(rows[0].IsLunch);
        Assert.True(rows[1].IsLunch);
        Assert.Equal(TimeSlotLunchMutationMode.Set, GetField<TimeSlotLunchMutationMode>(component, "_lunchMutation"));
        Assert.False(request.ApplySlots);
        Assert.Equal("13:00", Assert.IsType<TimeSlotDto>(request.LunchSlot).Start);
    }

    [Fact]
    public void Disabling_inherited_lunch_row_changes_slots_without_removing_shared_lunch()
    {
        var component = CreateInheritedLunchComponent();

        InvokeVoid(component, "ToggleActive", 0, new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = false });
        var request = Invoke<TimeSlotSequenceApplyRequestDto>(component, "BuildRequest", (object?)null);

        var row = GetField<List<TimeSlotDto>>(component, "_rows")[0];
        Assert.False(row.IsActive);
        Assert.False(row.IsLunch);
        Assert.Equal(TimeSlotLunchMutationMode.Unchanged, GetField<TimeSlotLunchMutationMode>(component, "_lunchMutation"));
        Assert.True(request.ApplySlots);
        Assert.Equal(TimeSlotLunchMutationMode.Unchanged, request.LunchMutation);
        Assert.Null(request.LunchSlot);
        Assert.Contains("Спільна обідня перерва не змінюється", GetField<string>(component, "_ok"), StringComparison.Ordinal);

        InvokeVoid(component, "ToggleActive", 0, new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

        Assert.True(GetField<List<TimeSlotDto>>(component, "_rows")[0].IsLunch);
        Assert.False(GetProperty<bool>(component, "HasUnsavedSequenceChanges"));
    }

    [Fact]
    public void Unchanged_lunch_without_sequence_edits_sends_an_empty_non_applying_payload()
    {
        var component = CreateReadyComponent();
        SetField(component, "_rows", new List<TimeSlotDto> { Row(1, "09:00", "09:45") });
        SetField(component, "_loadedRowsSnapshot", new List<TimeSlotDto> { Row(1, "09:00", "09:45") });

        var request = Invoke<TimeSlotSequenceApplyRequestDto>(component, "BuildRequest", (object?)null);

        Assert.False(request.ApplySlots);
        Assert.False(GetProperty<bool>(component, "CanPreviewChanges"));
        Assert.Empty(request.Slots);
        Assert.False(request.Clear);
        Assert.Equal(TimeSlotLunchMutationMode.Unchanged, request.LunchMutation);
        Assert.Null(request.LunchSlot);
    }

    [Fact]
    public void Slot_and_lunch_change_replaces_slots_and_carries_lunch_marker_in_sequence()
    {
        var component = CreateReadyComponent();
        var draft = Row(1, "12:00", "12:50");
        draft.IsLunch = true;
        SetField(component, "_rows", new List<TimeSlotDto> { draft });
        SetField(component, "_loadedRowsSnapshot", new List<TimeSlotDto> { Row(1, "12:00", "12:45") });
        SetField(component, "_lunchMutation", TimeSlotLunchMutationMode.Set);

        var request = Invoke<TimeSlotSequenceApplyRequestDto>(component, "BuildRequest", (object?)null);

        Assert.True(request.ApplySlots);
        Assert.True(Assert.Single(request.Slots).IsLunch);
        Assert.Equal(TimeSlotLunchMutationMode.Set, request.LunchMutation);
        Assert.Null(request.LunchSlot);
    }

    [Fact]
    public void Explicit_course_configuration_applies_slots_even_when_times_match_inherited_graph()
    {
        var component = CreateReadyComponent();
        SetField(component, "_targetMode", TimeSlotEditorTargetMode.Course);
        SetField<int?>(component, "_courseId", 4);
        SetField(component, "_rows", new List<TimeSlotDto> { Row(1, "09:00", "09:45") });
        SetField(component, "_loadedRowsSnapshot", new List<TimeSlotDto> { Row(1, "09:00", "09:45") });
        SetField(component, "_explicitOverrideRequested", true);

        var request = Invoke<TimeSlotSequenceApplyRequestDto>(component, "BuildRequest", (object?)null);

        Assert.True(request.ApplySlots);
        Assert.Single(request.Slots);
        Assert.False(request.Clear);
        Assert.Equal(TimeSlotLunchMutationMode.Unchanged, request.LunchMutation);
        Assert.Null(request.LunchSlot);
    }

    [Fact]
    public void Day_override_removal_is_an_explicit_clear_without_slot_or_lunch_edits()
    {
        var component = CreateReadyComponent();
        SetField(component, "_targetMode", TimeSlotEditorTargetMode.Course);
        SetField<int?>(component, "_courseId", 4);
        SetField<int?>(component, "_dayOfWeek", 2);
        SetField(component, "_rows", new List<TimeSlotDto> { Row(1, "09:00", "09:45") });
        SetField(component, "_loadedRowsSnapshot", new List<TimeSlotDto> { Row(1, "09:00", "09:45") });
        SetField(component, "_context", new TimeSlotEditorContextDto
        {
            TargetMode = TimeSlotEditorTargetMode.Course,
            CourseId = 4,
            DayOfWeek = 2,
            HasDayOverride = true,
            CurrentRevision = "revision-1"
        });

        InvokeVoid(component, "RequestDayOverrideRemoval");
        var request = Invoke<TimeSlotSequenceApplyRequestDto>(component, "BuildRequest", (object?)null);

        Assert.True(request.ApplySlots);
        Assert.True(request.Clear);
        Assert.Empty(request.Slots);
        Assert.False(request.ResetCourseToGlobal);
        Assert.Equal(TimeSlotLunchMutationMode.Unchanged, request.LunchMutation);
        Assert.Null(request.LunchSlot);
    }

    [Fact]
    public void Day_override_removal_is_unavailable_when_the_day_uses_its_base_graph()
    {
        var component = CreateReadyComponent();
        SetField<int?>(component, "_dayOfWeek", 2);
        SetField(component, "_context", new TimeSlotEditorContextDto
        {
            TargetMode = TimeSlotEditorTargetMode.AllCourses,
            DayOfWeek = 2,
            HasDayOverride = false,
            CurrentRevision = "revision-1"
        });

        Assert.False(GetProperty<bool>(component, "CanRequestDayOverrideRemoval"));

        InvokeVoid(component, "RequestDayOverrideRemoval");

        Assert.False(GetField<bool>(component, "_removeDayOverrideRequested"));
    }

    [Fact]
    public void Quick_builder_preserves_unmatched_existing_lunch_as_unchanged()
    {
        var component = CreateReadyComponent();
        SetField(component, "_context", new TimeSlotEditorContextDto
        {
            TargetMode = TimeSlotEditorTargetMode.AllCourses,
            EffectiveLunch = new LunchConfigEditDto(null, null, "12:00", "12:45"),
            CurrentRevision = "revision-1"
        });
        var previous = Row(1, "12:00", "12:45");
        previous.IsLunch = true;
        SetField(component, "_rows", new List<TimeSlotDto> { previous });
        SetField(component, "_loadedRowsSnapshot", new List<TimeSlotDto> { Row(1, "12:00", "12:45") });
        SetField(component, "_builderStart", "08:30");
        SetField(component, "_builderCount", 2);
        SetField(component, "_builderDurationMinutes", 45);
        SetField(component, "_builderBreakMinutes", 10);

        InvokeVoid(component, "BuildSequence");
        var request = Invoke<TimeSlotSequenceApplyRequestDto>(component, "BuildRequest", (object?)null);

        Assert.Equal(TimeSlotLunchMutationMode.Unchanged, GetField<TimeSlotLunchMutationMode>(component, "_lunchMutation"));
        Assert.DoesNotContain(GetField<List<TimeSlotDto>>(component, "_rows"), row => row.IsLunch);
        Assert.Contains("залишиться без змін", GetField<string>(component, "_ok"), StringComparison.Ordinal);
        Assert.True(request.ApplySlots);
        Assert.Equal(TimeSlotLunchMutationMode.Unchanged, request.LunchMutation);
        Assert.Null(request.LunchSlot);
    }

    [Fact]
    public void Revert_restores_loaded_rows_and_resets_lunch_mutation()
    {
        var component = CreateReadyComponent();
        SetField(component, "_rows", new List<TimeSlotDto> { Row(1, "10:00", "10:45") });
        SetField(component, "_loadedRowsSnapshot", new List<TimeSlotDto> { Row(1, "09:00", "09:45") });
        SetField(component, "_lunchMutation", TimeSlotLunchMutationMode.Remove);

        InvokeVoid(component, "RevertChanges");

        Assert.Equal("09:00", Assert.Single(GetField<List<TimeSlotDto>>(component, "_rows")).Start);
        Assert.Equal(TimeSlotLunchMutationMode.Unchanged, GetField<TimeSlotLunchMutationMode>(component, "_lunchMutation"));
        Assert.False(GetProperty<bool>(component, "HasUnsavedSlotChanges"));
    }

    [Fact]
    public async Task Preview_posts_one_scoped_request_without_group_fan_out()
    {
        var handler = new SequenceHandler(JsonResponse(HttpStatusCode.OK, """
            {
              "targetMode": "AllCourses",
              "affectedCourseCount": 5,
              "courseOverridesToReplace": 2,
              "materializedCourseCount": 1,
              "scheduleConflictCount": 0,
              "draftConflictCount": 0,
              "noChanges": false,
              "currentRevision": "revision-1",
              "previewToken": "preview-1"
            }
            """));
        var component = CreateReadyComponent(handler);
        SetField(component, "_rows", new List<TimeSlotDto> { Row(7, "09:00", "09:45") });

        await InvokeAsync(component, "PreviewAsync");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/api/admin/config/slots/editor/preview", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        var payload = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"targetMode\":\"AllCourses\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"currentRevision\":\"revision-1\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"applySlots\":true", payload, StringComparison.Ordinal);
        Assert.Contains("\"sortOrder\":1", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("groupId", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("preview-1", GetField<TimeSlotSequencePreviewDto>(component, "_preview").PreviewToken);
        Assert.True(GetProperty<bool>(component, "CanApplyPreview"));
    }

    [Fact]
    public async Task Machine_readable_stale_response_preserves_draft_until_explicit_discard_refresh()
    {
        var handler = new SequenceHandler(JsonResponse(
            HttpStatusCode.Conflict,
            """{ "detail": "Конфігурація застаріла.", "code": "Stale" }""",
            "application/problem+json"));
        var component = CreateReadyComponent(handler);
        SetField(component, "_rows", new List<TimeSlotDto> { Row(1, "10:00", "10:45") });

        await InvokeAsync(component, "PreviewAsync");

        Assert.Equal("10:00", Assert.Single(GetField<List<TimeSlotDto>>(component, "_rows")).Start);
        Assert.True(GetField<bool>(component, "_staleData"));
        var staleError = GetField<string>(component, "_error");
        Assert.Contains("Поточні правки залишаться в редакторі", staleError, StringComparison.Ordinal);
        Assert.Contains("Оновлення відкине їх", staleError, StringComparison.Ordinal);
        Assert.DoesNotContain("Ваші правки збережено", staleError, StringComparison.Ordinal);
        Assert.Equal("Відкинути правки й оновити", GetProperty<string>(component, "RefreshButtonLabel"));
        Assert.Null(GetField<TimeSlotSequencePreviewDto?>(component, "_preview"));
    }

    [Fact]
    public async Task Ordinary_conflict_preserves_draft_and_shows_server_detail_without_marking_stale()
    {
        var handler = new SequenceHandler(JsonResponse(
            HttpStatusCode.Conflict,
            """{ "detail": "Час перетинається з уже запланованим заняттям." }""",
            "application/problem+json"));
        var component = CreateReadyComponent(handler);
        SetField(component, "_rows", new List<TimeSlotDto> { Row(1, "10:00", "10:45") });

        await InvokeAsync(component, "PreviewAsync");

        Assert.Equal("10:00", Assert.Single(GetField<List<TimeSlotDto>>(component, "_rows")).Start);
        Assert.False(GetField<bool>(component, "_staleData"));
        Assert.Equal("Час перетинається з уже запланованим заняттям.", GetField<string>(component, "_error"));
        Assert.Null(GetField<TimeSlotSequencePreviewDto?>(component, "_preview"));
    }

    [Fact]
    public async Task Apply_posts_preview_token_then_refreshes_the_saved_context()
    {
        var handler = new SequenceHandler(
            JsonResponse(HttpStatusCode.OK, """
                {
                  "noChanges": false,
                  "affectedCourseCount": 5,
                  "previousRevision": "revision-1",
                  "currentRevision": "revision-2"
                }
                """),
            EditorContextResponse("10:00"));
        var component = CreateReadyComponent(handler);
        SetField(component, "_rows", new List<TimeSlotDto> { Row(1, "09:00", "09:50") });
        SetField(component, "_preview", new TimeSlotSequencePreviewDto
        {
            TargetMode = TimeSlotEditorTargetMode.AllCourses,
            AffectedCourseCount = 5,
            CurrentRevision = "revision-1",
            PreviewToken = "preview-1"
        });

        await InvokeAsync(component, "ApplyPreviewAsync");

        Assert.Equal(2, handler.Requests.Count);
        var apply = handler.Requests[0];
        Assert.EndsWith("/api/admin/config/slots/editor/apply", apply.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        var payload = await apply.Content!.ReadAsStringAsync();
        Assert.Contains("\"previewToken\":\"preview-1\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"currentRevision\":\"revision-1\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"applySlots\":true", payload, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal("10:00", Assert.Single(GetField<List<TimeSlotDto>>(component, "_rows")).Start);
        Assert.Contains("Оновлено курсів: 5", GetField<string>(component, "_ok"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delayed_apply_cannot_reload_or_mark_a_new_day_draft_as_saved()
    {
        var handler = new DelayedMutationHandler();
        var component = CreateReadyComponent(handler);
        SetField(component, "_rows", new List<TimeSlotDto> { Row(1, "09:00", "09:50") });
        SetField(component, "_preview", new TimeSlotSequencePreviewDto
        {
            TargetMode = TimeSlotEditorTargetMode.AllCourses,
            AffectedCourseCount = 5,
            CurrentRevision = "revision-1",
            PreviewToken = "preview-1"
        });

        var applyTask = InvokeTask(component, "ApplyPreviewAsync");
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));
        SetField<int?>(component, "_dayOfWeek", 2);
        SetField(component, "_contextLoadVersion", 1);
        SetField(component, "_rows", new List<TimeSlotDto> { Row(1, "12:00", "12:45") });
        handler.Complete(JsonResponse(HttpStatusCode.OK, """
            {
              "noChanges": false,
              "affectedCourseCount": 5,
              "previousRevision": "revision-1",
              "currentRevision": "revision-2"
            }
            """));

        await applyTask;

        Assert.Single(handler.Requests);
        Assert.Equal("12:00", Assert.Single(GetField<List<TimeSlotDto>>(component, "_rows")).Start);
        Assert.Null(GetField<string?>(component, "_ok"));
        Assert.False(GetField<bool>(component, "_applying"));
    }

    [Fact]
    public async Task Stale_context_response_cannot_replace_new_day_or_unlock_its_load()
    {
        var handler = new DelayedContextHandler();
        var component = CreateComponent(handler);

        var firstLoad = InvokeTask(component, "LoadRaw");
        await handler.FirstStarted.WaitAsync(TimeSpan.FromSeconds(5));
        SetField<int?>(component, "_dayOfWeek", 2);
        var secondLoad = InvokeTask(component, "LoadRaw");
        await handler.SecondStarted.WaitAsync(TimeSpan.FromSeconds(5));

        handler.CompleteFirst(EditorContextResponse("09:00"));
        await firstLoad;

        Assert.True(GetField<bool>(component, "_slotsLoading"));
        Assert.False(GetField<bool>(component, "_slotsLoadSucceeded"));

        handler.CompleteSecond(EditorContextResponse("10:00", dayOfWeek: 2));
        await secondLoad;

        Assert.False(GetField<bool>(component, "_slotsLoading"));
        Assert.True(GetField<bool>(component, "_slotsLoadSucceeded"));
        Assert.Equal("10:00", Assert.Single(GetField<List<TimeSlotDto>>(component, "_rows")).Start);
    }

    private static object CreateReadyComponent(HttpMessageHandler? handler = null)
    {
        var component = CreateComponent(handler ?? new SequenceHandler());
        SetField(component, "_metaLoadSucceeded", true);
        SetField(component, "_slotsLoadSucceeded", true);
        SetField(component, "_currentRevision", "revision-1");
        SetField(component, "_rows", new List<TimeSlotDto>());
        SetField(component, "_loadedRowsSnapshot", new List<TimeSlotDto>());
        SetField(component, "_context", new TimeSlotEditorContextDto
        {
            TargetMode = TimeSlotEditorTargetMode.AllCourses,
            CurrentRevision = "revision-1"
        });
        return component;
    }

    private static object CreateInheritedLunchComponent()
    {
        var component = CreateReadyComponent();
        SetField(component, "_targetMode", TimeSlotEditorTargetMode.Course);
        SetField<int?>(component, "_courseId", 4);
        SetField(component, "_context", new TimeSlotEditorContextDto
        {
            TargetMode = TimeSlotEditorTargetMode.Course,
            CourseId = 4,
            ExplicitLunch = null,
            EffectiveLunch = new LunchConfigEditDto(7, null, "12:00", "12:45"),
            CurrentRevision = "revision-1"
        });
        var inherited = Row(1, "12:00", "12:45");
        inherited.IsLunch = true;
        var alternative = Row(2, "13:00", "13:45");
        SetField(component, "_rows", new List<TimeSlotDto> { inherited, alternative });
        var inheritedSnapshot = Row(1, "12:00", "12:45");
        inheritedSnapshot.IsLunch = true;
        SetField(component, "_loadedRowsSnapshot", new List<TimeSlotDto>
        {
            inheritedSnapshot,
            Row(2, "13:00", "13:45")
        });
        return component;
    }

    private static object CreateComponent(HttpMessageHandler handler)
    {
        var componentType = typeof(TimeSlotsApi).Assembly.GetType(ComponentTypeName, throwOnError: true)!;
        var component = Activator.CreateInstance(componentType)!;
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://schedule.test/") };
        componentType.GetProperty("Api", InstanceMembers)!.SetValue(component, new TimeSlotsApi(client));
        return component;
    }

    private static async Task InvokeAsync(object component, string methodName, params object?[] arguments)
        => await InvokeTask(component, methodName, arguments);

    private static Task InvokeTask(object component, string methodName, params object?[] arguments)
        => Assert.IsAssignableFrom<Task>(component.GetType().GetMethod(methodName, InstanceMembers)!.Invoke(component, arguments));

    private static T Invoke<T>(object component, string methodName, params object?[] arguments)
    {
        var result = component.GetType().GetMethod(methodName, InstanceMembers)!.Invoke(component, arguments);
        return result is null ? default! : Assert.IsType<T>(result);
    }

    private static void InvokeVoid(object component, string methodName, params object?[] arguments)
        => component.GetType().GetMethod(methodName, InstanceMembers)!.Invoke(component, arguments);

    private static T GetField<T>(object component, string fieldName)
        => (T)component.GetType().GetField(fieldName, InstanceMembers)!.GetValue(component)!;

    private static void SetField<T>(object component, string fieldName, T value)
        => component.GetType().GetField(fieldName, InstanceMembers)!.SetValue(component, value);

    private static T GetProperty<T>(object component, string propertyName)
        => (T)component.GetType().GetProperty(propertyName, InstanceMembers)!.GetValue(component)!;

    private static TimeSlotDto Row(int order, string start, string end)
        => new()
        {
            SortOrder = order,
            Start = start,
            End = end,
            IsActive = true
        };

    private static void AssertRow(TimeSlotDto row, int order, string start, string end)
    {
        Assert.Equal(order, row.SortOrder);
        Assert.Equal(start, row.Start);
        Assert.Equal(end, row.End);
        Assert.True(row.IsActive);
    }

    private static HttpResponseMessage EditorContextResponse(
        string start,
        int? dayOfWeek = null,
        int preferredLimit = 0)
        => JsonResponse(HttpStatusCode.OK, $$"""
            {
              "targetMode": "AllCourses",
              "courseId": null,
              "dayOfWeek": {{(dayOfWeek is null ? "null" : dayOfWeek.Value)}},
              "courses": [{ "id": 1, "name": "Курс 1" }],
              "explicitSlots": [{
                "id": 1,
                "courseId": null,
                "dayOfWeek": {{(dayOfWeek is null ? "null" : dayOfWeek.Value)}},
                "sortOrder": 1,
                "start": "{{start}}",
                "end": "{{TimeOnly.Parse(start).AddMinutes(45):HH:mm}}",
                "isActive": true,
                "isLunch": false
              }],
              "globalSlots": [],
              "effectiveSlots": [],
              "isInherited": false,
              "preferredFirstMaxSlotOrder": {{preferredLimit}},
              "courseOverrideCount": 0,
              "currentRevision": "revision-1"
            }
            """);

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode statusCode,
        string payload,
        string mediaType = "application/json")
        => new(statusCode) { Content = new StringContent(payload, Encoding.UTF8, mediaType) };

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Assert.NotEmpty(_responses);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class DelayedContextHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _firstResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _secondResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public Task FirstStarted => _firstStarted.Task;
        public Task SecondStarted => _secondStarted.Task;
        public void CompleteFirst(HttpResponseMessage response) => _firstResponse.TrySetResult(response);
        public void CompleteSecond(HttpResponseMessage response) => _secondResponse.TrySetResult(response);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref _requestCount);
            if (requestNumber == 1)
            {
                _firstStarted.TrySetResult(true);
                return _firstResponse.Task.WaitAsync(cancellationToken);
            }
            _secondStarted.TrySetResult(true);
            return _secondResponse.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class DelayedMutationHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;
        public List<HttpRequestMessage> Requests { get; } = [];
        public void Complete(HttpResponseMessage response) => _response.TrySetResult(response);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            _started.TrySetResult(true);
            return _response.Task.WaitAsync(cancellationToken);
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

    [Fact]
    public async Task Add_js_failure_is_reported_instead_of_silent_log_loss()
    {
        var service = new AdminScheduleLogService(
            new LocalStorageJsRuntime(null, failingIdentifier: "localStorage.setItem"));
        var entry = new AdminScheduleLogEntry(
            Id: "entry-1",
            Timestamp: DateTimeOffset.UtcNow,
            ActionCode: "delete",
            ActionLabel: "Видалення",
            Summary: "Зміну застосовано",
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

        var exception = await Assert.ThrowsAsync<JSException>(() => service.AddAsync(entry));

        Assert.Contains("localStorage.setItem", exception.Message, StringComparison.Ordinal);
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

public sealed class AdminScheduleActiveJobStorageTests
{
    private const BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string ActiveJobStorageKey = "adminSchedule.activeAutoGenJob.v1";

    [Fact]
    public async Task Active_autogen_job_persistence_fails_closed_and_cleans_partial_context()
    {
        var componentType = typeof(AdminApi).Assembly.GetType(
            "BlazorWasmDotNet8AspNetCoreHosted.Client.Pages.AdminSchedule",
            throwOnError: true)!;
        var component = Activator.CreateInstance(componentType)!;
        var js = new FailingSetStorageJsRuntime();
        componentType.GetProperty("JS", InstanceMembers)!.SetValue(component, js);
        var method = componentType.GetMethod("PersistActiveAutoGenJobIdAsync", InstanceMembers)!;

        var persisted = await Assert.IsType<Task<bool>>(method.Invoke(
            component,
            [Guid.NewGuid().ToString("N"), 1, new DateOnly(2026, 8, 24)]));

        Assert.False(persisted);
        Assert.Contains("localStorage.setItem", js.Invocations);
        Assert.Equal(2, js.Invocations.Count(call => call == "localStorage.removeItem"));
    }

    [Fact]
    public async Task Confirmed_job_is_restored_after_another_tab_removes_the_preflight_id()
    {
        var componentType = typeof(AdminApi).Assembly.GetType(
            "BlazorWasmDotNet8AspNetCoreHosted.Client.Pages.AdminSchedule",
            throwOnError: true)!;
        var shared = new SharedLocalStorage();
        var firstTabJs = new SharedStorageJsRuntime(shared);
        var secondTabJs = new SharedStorageJsRuntime(shared);
        var firstTab = CreateComponent(componentType, firstTabJs);
        var secondTab = CreateComponent(componentType, secondTabJs);
        var jobId = Guid.NewGuid().ToString("N");
        var persist = componentType.GetMethod("PersistActiveAutoGenJobIdAsync", InstanceMembers)!;
        var remove = componentType.GetMethod("RemoveActiveAutoGenJobIdAsync", InstanceMembers)!;
        var confirm = componentType.GetMethod("ConfirmActiveAutoGenJobPersistenceAsync", InstanceMembers)!;

        Assert.True(await Assert.IsType<Task<bool>>(persist.Invoke(
            firstTab,
            [jobId, 1, new DateOnly(2026, 8, 24)])));

        await Assert.IsAssignableFrom<Task>(remove.Invoke(secondTab, [jobId]));
        Assert.Null(shared.Values.GetValueOrDefault(ActiveJobStorageKey));

        var confirmed = await Assert.IsType<Task<bool>>(confirm.Invoke(
            firstTab,
            [jobId, jobId, 1, new DateOnly(2026, 8, 24)]));

        Assert.True(confirmed);
        Assert.Equal(jobId, shared.Values[ActiveJobStorageKey]);
        Assert.Equal(jobId, firstTabJs.SessionValues[ActiveJobStorageKey]);
    }

    [Fact]
    public async Task Confirmation_keeps_a_newer_shared_job_and_tracks_the_current_job_per_tab()
    {
        var componentType = typeof(AdminApi).Assembly.GetType(
            "BlazorWasmDotNet8AspNetCoreHosted.Client.Pages.AdminSchedule",
            throwOnError: true)!;
        var shared = new SharedLocalStorage();
        var firstTabJs = new SharedStorageJsRuntime(shared);
        var secondTabJs = new SharedStorageJsRuntime(shared);
        var firstTab = CreateComponent(componentType, firstTabJs);
        var secondTab = CreateComponent(componentType, secondTabJs);
        var firstJobId = Guid.NewGuid().ToString("N");
        var secondJobId = Guid.NewGuid().ToString("N");
        var persist = componentType.GetMethod("PersistActiveAutoGenJobIdAsync", InstanceMembers)!;
        var confirm = componentType.GetMethod("ConfirmActiveAutoGenJobPersistenceAsync", InstanceMembers)!;

        Assert.True(await Assert.IsType<Task<bool>>(persist.Invoke(
            firstTab,
            [firstJobId, 1, new DateOnly(2026, 8, 24)])));
        Assert.True(await Assert.IsType<Task<bool>>(persist.Invoke(
            secondTab,
            [secondJobId, 2, new DateOnly(2026, 8, 31)])));

        var confirmed = await Assert.IsType<Task<bool>>(confirm.Invoke(
            firstTab,
            [firstJobId, firstJobId, 1, new DateOnly(2026, 8, 24)]));

        Assert.True(confirmed);
        Assert.Equal(secondJobId, shared.Values[ActiveJobStorageKey]);
        Assert.Equal(firstJobId, firstTabJs.SessionValues[ActiveJobStorageKey]);
        Assert.Equal(secondJobId, secondTabJs.SessionValues[ActiveJobStorageKey]);
    }

    private static object CreateComponent(Type componentType, IJSRuntime js)
    {
        var component = Activator.CreateInstance(componentType)!;
        componentType.GetProperty("JS", InstanceMembers)!.SetValue(component, js);
        return component;
    }

    private sealed class FailingSetStorageJsRuntime : IJSRuntime
    {
        public List<string> Invocations { get; } = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Invocations.Add(identifier);
            if (identifier == "localStorage.setItem")
            {
                throw new JSException("localStorage.setItem недоступний");
            }
            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class SharedLocalStorage
    {
        public Dictionary<string, string?> Values { get; } = new(StringComparer.Ordinal);
    }

    private sealed class SharedStorageJsRuntime(SharedLocalStorage shared) : IJSRuntime
    {
        public Dictionary<string, string?> SessionValues { get; } = new(StringComparer.Ordinal);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            var values = identifier.StartsWith("localStorage", StringComparison.Ordinal)
                ? shared.Values
                : SessionValues;
            var key = Assert.IsType<string>(args![0]);
            if (identifier.EndsWith(".getItem", StringComparison.Ordinal))
            {
                values.TryGetValue(key, out var value);
                return ValueTask.FromResult((TValue)(object?)value!);
            }
            if (identifier.EndsWith(".setItem", StringComparison.Ordinal))
            {
                values[key] = Assert.IsType<string>(args[1]);
            }
            else if (identifier.EndsWith(".removeItem", StringComparison.Ordinal))
            {
                values.Remove(key);
            }
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}

public sealed class AdminScheduleDraftPolicyTests
{
    private const BindingFlags InstanceMembers =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData(DraftStatusDto.Draft, false)]
    [InlineData(DraftStatusDto.Published, true)]
    public void Approved_unlocked_draft_requires_unrestricted_delete(
        DraftStatusDto status,
        bool expected)
    {
        var componentType = typeof(AdminApi).Assembly.GetType(
            "BlazorWasmDotNet8AspNetCoreHosted.Client.Pages.AdminSchedule",
            throwOnError: true)!;
        var component = Activator.CreateInstance(componentType)!;
        var draft = new TeacherDraftItemDto(
            Id: 1,
            Date: new DateOnly(2026, 8, 24),
            TimeStart: "09:00",
            TimeEnd: "10:00",
            DayNumber: 1,
            Group: "Група 1",
            GroupId: 1,
            Module: "Модуль 1",
            ModuleId: 1,
            TopicCode: null,
            ModuleTopicId: null,
            Teacher: "Викладач",
            TeacherId: 1,
            Room: "101",
            RoomId: 1,
            RequiresRoom: true,
            MissingTeacherAssignment: false,
            MissingRoomAssignment: false,
            LessonTypeId: 1,
            LessonTypeCode: "LECTURE",
            LessonTypeName: "Лекція",
            Status: status,
            PublishedItemId: null,
            Warnings: null,
            IsLocked: false,
            Revision: Guid.NewGuid());

        var item = componentType.GetMethod("CreateTableItemFromDraft", InstanceMembers)!
            .Invoke(component, [draft])!;
        var requiresUnrestricted = Assert.IsType<bool>(
            item.GetType().GetProperty("RequiresUnrestricted", InstanceMembers)!.GetValue(item));

        Assert.Equal(expected, requiresUnrestricted);
    }
}
