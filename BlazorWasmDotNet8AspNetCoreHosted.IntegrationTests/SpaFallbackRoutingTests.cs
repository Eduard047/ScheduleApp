using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class SpaFallbackRoutingTests
{
    [Theory]
    [InlineData("/api")]
    [InlineData("/api/")]
    [InlineData("/api/definitely-missing")]
    public async Task Unknown_api_route_returns_problem_404_instead_of_spa_index(string path)
    {
        await using var host = await RoutingTestHost.StartAsync();

        using var response = await host.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(RoutingTestHost.SpaMarker, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_non_api_client_route_returns_spa_index()
    {
        await using var host = await RoutingTestHost.StartAsync();

        using var response = await host.Client.GetAsync("/schedule/unregistered-client-route");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(RoutingTestHost.SpaMarker, body, StringComparison.Ordinal);
    }

    private sealed class RoutingTestHost : IAsyncDisposable
    {
        public const string SpaMarker = "scheduleapp-spa-fallback";

        private readonly WebApplication _app;
        private readonly string _webRoot;

        private RoutingTestHost(WebApplication app, HttpClient client, string webRoot)
        {
            _app = app;
            Client = client;
            _webRoot = webRoot;
        }

        public HttpClient Client { get; }

        public static async Task<RoutingTestHost> StartAsync()
        {
            var webRoot = Path.Combine(
                Path.GetTempPath(),
                $"scheduleapp-routing-{Guid.NewGuid():N}");
            Directory.CreateDirectory(webRoot);
            await File.WriteAllTextAsync(
                Path.Combine(webRoot, "index.html"),
                $"<!doctype html><html><body>{SpaMarker}</body></html>");

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Testing",
                WebRootPath = webRoot
            });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

            var app = builder.Build();
            app.UseStaticFiles();
            Program.MapSpaFallbackRoutes(app);
            await app.StartAsync();

            var addresses = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?.Addresses;
            var address = Assert.Single(addresses ?? Array.Empty<string>());
            var client = new HttpClient { BaseAddress = new Uri(address) };
            return new RoutingTestHost(app, client, webRoot);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            Directory.Delete(_webRoot, recursive: true);
        }
    }
}
