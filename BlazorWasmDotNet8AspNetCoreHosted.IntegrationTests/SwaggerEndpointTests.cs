using System.Net;
using System.Text.Json;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers.Admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class SwaggerEndpointTests
{
    [Fact]
    public async Task Swagger_document_is_available_and_describes_docx_upload()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(AdminModulesController).Assembly);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        await using var app = builder.Build();
        app.UseSwagger();

        await app.StartAsync();
        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            using var response = await client.GetAsync("/swagger/v1/swagger.json");
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"Swagger повернув {(int)response.StatusCode}: {body}");

            using var document = JsonDocument.Parse(body);
            var operation = document.RootElement
                .GetProperty("paths")
                .GetProperty("/api/admin/modules/import-docx")
                .GetProperty("post");
            var formSchema = operation
                .GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("multipart/form-data")
                .GetProperty("schema");

            Assert.Equal("object", formSchema.GetProperty("type").GetString());
            Assert.Equal("string", formSchema.GetProperty("properties").GetProperty("file").GetProperty("type").GetString());
            Assert.Equal("binary", formSchema.GetProperty("properties").GetProperty("file").GetProperty("format").GetString());
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
