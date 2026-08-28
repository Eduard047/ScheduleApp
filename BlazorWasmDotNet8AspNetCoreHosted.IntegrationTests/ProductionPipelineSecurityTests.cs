using System.Net;
using System.Net.Http.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure.Seed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class ProductionPipelineSecurityTests
{
    [Fact]
    public async Task Real_entry_point_rejects_unlisted_host_before_routing()
    {
        await using var factory = new ProductionPipelineFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Host = "evil.example.test";

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Real_entry_point_rejects_cross_origin_unsafe_api_request()
    {
        await using var factory = new ProductionPipelineFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/definitely-missing");
        request.Headers.Host = "schedule.example.test";
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example.test");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Real_entry_point_sets_frame_denial_headers()
    {
        await using var factory = new ProductionPipelineFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Host = "schedule.example.test";

        using var response = await client.SendAsync(request);

        Assert.Equal(SecurityResponseHeadersMiddleware.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("camera=(), geolocation=(), microphone=()", response.Headers.GetValues("Permissions-Policy").Single());
    }

    [Fact]
    public async Task Real_entry_point_rate_limits_repeated_autogen_starts_before_controller_work()
    {
        await using var factory = new ProductionPipelineFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        HttpResponseMessage? lastResponse = null;
        var statuses = new List<HttpStatusCode>();
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                lastResponse?.Dispose();
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    "/api/teacher-drafts/autogen/jobs");
                request.Headers.Host = "schedule.example.test";
                request.Content = JsonContent.Create(new { });
                lastResponse = await client.SendAsync(request);
                statuses.Add(lastResponse.StatusCode);
            }

            Assert.NotNull(lastResponse);
            Assert.Equal(
                Enumerable.Repeat(HttpStatusCode.BadRequest, 4),
                statuses.Take(4));
            Assert.Equal(HttpStatusCode.TooManyRequests, lastResponse.StatusCode);
            Assert.Equal("application/problem+json", lastResponse.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            lastResponse?.Dispose();
        }
    }

    private sealed class ProductionPipelineFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AllowedHosts", "schedule.example.test");
            builder.UseSetting(
                "ConnectionStrings:Default",
                "Server=127.0.0.1;Database=unused;User=unused;Password=unused");
            builder.ConfigureServices(services =>
            {
                // Для HTTP-контракту не запускаємо фонові служби, що потребують робочої БД.
                services.RemoveAll<IHostedService>();
                services.RemoveAll<DefaultLessonTypesSeederHostedService>();
            });
        }
    }
}
