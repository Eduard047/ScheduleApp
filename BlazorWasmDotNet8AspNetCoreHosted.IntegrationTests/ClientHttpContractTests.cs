using System.Net;
using System.Text;
using BlazorWasmDotNet8AspNetCoreHosted.Client.Services;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class ClientHttpContractTests
{
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

    private static HttpClient CreateClient(HttpResponseMessage response)
        => new(new StaticResponseHandler(response))
        {
            BaseAddress = new Uri("https://schedule.test/")
        };

    private sealed record TestPayload(int Value);

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
