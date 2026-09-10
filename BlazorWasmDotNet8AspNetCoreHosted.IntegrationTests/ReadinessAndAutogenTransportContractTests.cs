using System.Net;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure.Seed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class ReadinessAndAutogenTransportContractTests
{
    [Fact]
    public async Task Readiness_fails_closed_until_startup_seeding_completes()
    {
        await using var factory = new ContractPipelineFactory(markStartupReady: false);
        using var client = CreateClient(factory);

        using var liveResponse = await SendAsync(client, "/health/live");
        using var readyResponse = await SendAsync(client, "/health/ready");

        Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readyResponse.StatusCode);
        Assert.Equal("application/problem+json", readyResponse.Content.Headers.ContentType?.MediaType);

        using var problem = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());
        Assert.Equal(503, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            "Початкове налаштування не завершено",
            problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "Триває початкове налаштування довідників.",
            problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Readiness_requires_database_connectivity_after_startup_is_ready()
    {
        await using var factory = new ContractPipelineFactory(markStartupReady: true);
        using var client = CreateClient(factory);

        using var response = await SendAsync(client, "/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(503, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            "База даних недоступна",
            problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Swagger_describes_active_autogen_transport_contract()
    {
        await using var factory = new ContractPipelineFactory(
            markStartupReady: false,
            environmentName: "Development");
        using var client = CreateClient(factory);

        using var response = await SendAsync(client, "/swagger/v1/swagger.json");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Swagger повернув {(int)response.StatusCode}: {body}");

        using var document = JsonDocument.Parse(body);
        var paths = document.RootElement.GetProperty("paths");

        AssertJsonBody(paths, "/api/teacher-drafts/autogen/jobs", "post");
        AssertPathParameter(paths, "/api/teacher-drafts/autogen/jobs/{jobId}", "get", "jobId");
        AssertPathParameter(paths, "/api/teacher-drafts/autogen/jobs/{jobId}/cancel", "post", "jobId");
        AssertPathParameter(paths, "/api/teacher-drafts/autogen/jobs/{jobId}/plan", "get", "jobId");
        AssertQueryParameters(
            paths,
            "/api/teacher-drafts/autogen/jobs/{jobId}/plan",
            "get",
            "changeOffset",
            "changeLimit");
        AssertJsonBody(paths, "/api/teacher-drafts/autogen/jobs/{jobId}/apply", "post");
        AssertPathParameter(paths, "/api/teacher-drafts/autogen/jobs/{jobId}/apply", "post", "jobId");
        AssertJsonBody(paths, "/api/teacher-drafts/autogen/jobs/{jobId}/rollback", "post");
        AssertPathParameter(paths, "/api/teacher-drafts/autogen/jobs/{jobId}/rollback", "post", "jobId");
        AssertQueryParameters(
            paths,
            "/api/teacher-drafts/autogen/plans/latest-rollbackable",
            "get",
            "courseId",
            "changeOffset",
            "changeLimit");
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = "schedule.example.test";
        return await client.SendAsync(request);
    }

    private static void AssertJsonBody(JsonElement paths, string path, string method)
    {
        var operation = paths.GetProperty(path).GetProperty(method);
        var content = operation.GetProperty("requestBody").GetProperty("content");
        Assert.True(content.TryGetProperty("application/json", out _));
    }

    private static void AssertPathParameter(
        JsonElement paths,
        string path,
        string method,
        string parameterName)
        => AssertParameter(paths, path, method, parameterName, "path", required: true);

    private static void AssertQueryParameters(
        JsonElement paths,
        string path,
        string method,
        params string[] parameterNames)
    {
        foreach (var parameterName in parameterNames)
        {
            AssertParameter(paths, path, method, parameterName, "query", required: null);
        }
    }

    private static void AssertParameter(
        JsonElement paths,
        string path,
        string method,
        string parameterName,
        string location,
        bool? required)
    {
        var parameter = paths
            .GetProperty(path)
            .GetProperty(method)
            .GetProperty("parameters")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == parameterName);

        Assert.Equal(location, parameter.GetProperty("in").GetString());
        if (required.HasValue)
        {
            Assert.Equal(required.Value, parameter.GetProperty("required").GetBoolean());
        }
    }

    private sealed class ContractPipelineFactory(
        bool markStartupReady,
        string environmentName = "Testing") : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environmentName);
            builder.UseSetting("AllowedHosts", "schedule.example.test");
            builder.UseSetting(
                "ConnectionStrings:Default",
                "Server=127.0.0.1;Port=1;Database=unused;User=unused;Password=unused;Connection Timeout=1");
            builder.ConfigureServices(services =>
            {
                // HTTP-контракт не запускає фонові служби й ніколи не змінює робочу базу.
                services.RemoveAll<IHostedService>();
                services.RemoveAll<DefaultLessonTypesSeederHostedService>();
                if (markStartupReady)
                {
                    var readiness = new StartupReadinessState();
                    readiness.MarkReady();
                    services.RemoveAll<StartupReadinessState>();
                    services.AddSingleton(readiness);
                }
            });
        }
    }
}
