using System.Net;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class HttpBoundarySecurityTests
{
    [Fact]
    public async Task Host_filter_accepts_configured_host_and_rejects_unlisted_host()
    {
        await using var host = await SecurityTestHost.StartAsync("schedule.example.test");

        using var accepted = await host.SendAsync(HttpMethod.Get, "schedule.example.test");
        using var rejected = await host.SendAsync(HttpMethod.Get, "evil.example.test");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public async Task Default_local_host_entries_are_accepted(string requestHost)
    {
        const string defaultHosts = "localhost;127.0.0.1;[::1]";
        await using var host = await SecurityTestHost.StartAsync(defaultHosts);

        using var response = await host.SendAsync(HttpMethod.Get, requestHost);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ; ")]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData("localhost;*.example.test")]
    public void Host_policy_rejects_empty_or_wildcard_configuration(string? configuredHosts)
    {
        Assert.Throws<InvalidOperationException>(() => AllowedHostPolicy.Parse(configuredHosts));
    }

    [Fact]
    public async Task Unsafe_api_rejects_cross_site_fetch_even_without_origin()
    {
        await using var host = await SecurityTestHost.StartAsync("schedule.example.test");

        using var response = await host.SendAsync(
            HttpMethod.Post,
            "schedule.example.test",
            request => request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example.test")]
    [InlineData("null")]
    [InlineData("not-an-origin")]
    [InlineData("http://schedule.example.test/path")]
    public async Task Unsafe_api_rejects_non_matching_or_invalid_origin(string origin)
    {
        await using var host = await SecurityTestHost.StartAsync("schedule.example.test");

        using var response = await host.SendAsync(
            HttpMethod.Post,
            "schedule.example.test",
            request => request.Headers.TryAddWithoutValidation("Origin", origin));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Unsafe_api_allows_same_origin_browser_and_headerless_api_clients()
    {
        await using var host = await SecurityTestHost.StartAsync("schedule.example.test");

        using var browserResponse = await host.SendAsync(
            HttpMethod.Post,
            "schedule.example.test",
            request =>
            {
                request.Headers.TryAddWithoutValidation("Origin", "http://schedule.example.test");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            });
        using var apiClientResponse = await host.SendAsync(HttpMethod.Post, "schedule.example.test");

        Assert.Equal(HttpStatusCode.OK, browserResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, apiClientResponse.StatusCode);
    }

    [Fact]
    public async Task Safe_api_request_is_not_blocked_by_fetch_metadata()
    {
        await using var host = await SecurityTestHost.StartAsync("schedule.example.test");

        using var response = await host.SendAsync(
            HttpMethod.Get,
            "schedule.example.test",
            request => request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Application_responses_deny_cross_origin_framing()
    {
        await using var host = await SecurityTestHost.StartAsync("schedule.example.test");

        using var response = await host.SendAsync(HttpMethod.Get, "schedule.example.test");

        Assert.Equal("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Empty_proxy_policy_does_not_trust_forwarded_headers()
    {
        await using var host = await SecurityTestHost.StartAsync("schedule.example.test");

        using var response = await host.SendAsync(
            HttpMethod.Get,
            "schedule.example.test",
            AddForwardedHeaders);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http", body.RootElement.GetProperty("scheme").GetString());
        Assert.Equal(IPAddress.Loopback, IPAddress.Parse(body.RootElement.GetProperty("remoteIp").GetString()!));
    }

    [Fact]
    public async Task Explicit_proxy_policy_trusts_forwarded_headers_from_configured_proxy()
    {
        await using var host = await SecurityTestHost.StartAsync(
            "schedule.example.test",
            new[] { IPAddress.Loopback });

        using var response = await host.SendAsync(
            HttpMethod.Get,
            "schedule.example.test",
            AddForwardedHeaders);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https", body.RootElement.GetProperty("scheme").GetString());
        Assert.Equal(IPAddress.Parse("203.0.113.10"), IPAddress.Parse(body.RootElement.GetProperty("remoteIp").GetString()!));
    }

    private static void AddForwardedHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.10");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
    }

    private sealed class SecurityTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private SecurityTestHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        private HttpClient Client { get; }

        public static async Task<SecurityTestHost> StartAsync(
            string allowedHostsSetting,
            IEnumerable<IPAddress>? trustedProxies = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Testing"
            });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var allowedHosts = AllowedHostPolicy.Parse(allowedHostsSetting);
            builder.Services.AddHostFiltering(options => AllowedHostPolicy.Apply(options, allowedHosts));
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
                ReverseProxyForwardedHeadersPolicy.Apply(
                    options,
                    trustedProxies ?? Array.Empty<IPAddress>()));

            var app = builder.Build();
            app.UseForwardedHeaders();
            app.UseHostFiltering();
            app.UseMiddleware<SecurityResponseHeadersMiddleware>();
            app.UseMiddleware<ApiRequestOriginPolicyMiddleware>();
            app.MapMethods(
                "/api/security-boundary",
                new[] { HttpMethods.Get, HttpMethods.Post },
                (HttpContext context) => Results.Ok(new
                {
                    scheme = context.Request.Scheme,
                    remoteIp = context.Connection.RemoteIpAddress?.ToString()
                }));
            await app.StartAsync();

            var addresses = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?.Addresses;
            var address = Assert.Single(addresses ?? Array.Empty<string>());
            return new SecurityTestHost(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string host,
            Action<HttpRequestMessage>? configure = null)
        {
            using var request = new HttpRequestMessage(method, "/api/security-boundary");
            request.Headers.Host = host;
            configure?.Invoke(request);
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
