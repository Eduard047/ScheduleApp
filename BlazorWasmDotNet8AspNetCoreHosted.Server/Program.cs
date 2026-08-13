using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure.Seed;

// Точка входу для налаштування серверного застосунку
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
});
builder.Services.AddHealthChecks();
// Стискаємо JSON, WebAssembly та статичні ресурси під час передавання через HTTPS.
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);

var allowedHosts = AllowedHostPolicy.Parse(builder.Configuration["AllowedHosts"]);
builder.Services.AddHostFiltering(options => AllowedHostPolicy.Apply(options, allowedHosts));

var trustedProxyAddresses = builder.Configuration
    .GetSection("ReverseProxy:KnownProxies")
    .Get<string[]>()
    ?.Where(value => !string.IsNullOrWhiteSpace(value))
    .Select(value => IPAddress.TryParse(value, out var address)
        ? address
        : throw new InvalidOperationException($"Некоректна IP-адреса довіреного reverse proxy: '{value}'."))
    .ToList() ?? new List<IPAddress>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    ReverseProxyForwardedHeadersPolicy.Apply(options, trustedProxyAddresses);
});

// Підключення до БД та конфігурація EF Core.
var cs = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(cs))
{
    throw new InvalidOperationException(
        "Рядок підключення 'ConnectionStrings:Default' не налаштовано.");
}

var serverVersion = new MySqlServerVersion(new Version(8, 0, 13));
builder.Services.AddDbContextPool<AppDbContext>(opt => opt.UseMySql(cs, serverVersion));

// Реєстрація доменних сервісів.
builder.Services.AddScoped<RulesService>();
builder.Services.AddScoped<AggregatesService>();
builder.Services.AddScoped<TeacherDraftsQueryService>();
builder.Services.AddScoped<TeacherDraftsWeekValidationService>();
builder.Services.AddScoped<TeacherDraftsExportService>();
builder.Services.AddScoped<TeacherDraftsAutogenService>();
builder.Services.AddScoped<TeacherDraftsAutogenPlanService>();
builder.Services.AddSingleton<TeacherDraftsAutogenJobService>();
builder.Services.AddHostedService<TeacherDraftsAutogenJobService>(services =>
    services.GetRequiredService<TeacherDraftsAutogenJobService>());
builder.Services.AddScoped<TeacherDraftsPublishService>();
builder.Services.AddSingleton<StartupReadinessState>();
builder.Services.AddHostedService<DefaultLessonTypesSeederHostedService>();

var app = builder.Build();

// Приймаємо схему та адресу клієнта лише від явно довірених reverse proxy.
app.UseForwardedHeaders();
app.UseHostFiltering();
// Перевіряємо браузерне походження всіх API-запитів, що можуть змінювати стан.
app.UseMiddleware<ApiRequestOriginPolicyMiddleware>();

if (app.Environment.IsDevelopment())
{
    // У режимі розробки вмикаємо Swagger та отладку WASM.
    app.UseWebAssemblyDebugging();
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    // Для продакшену використовуємо обробник помилок та HSTS.
    app.UseExceptionHandler();
    app.UseHsts();
}

if (!app.Environment.IsDevelopment())
{
    // Локальний HTTP-профіль не має HTTPS-порту; у робочому середовищі перенаправлення залишається обов'язковим.
    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments("/health"),
        branch => branch.UseHttpsRedirection());
}

app.UseResponseCompression();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapRazorPages();
app.MapControllers();
app.MapHealthChecks("/health/live").ExcludeFromDescription();
app.MapGet("/health/ready", async (
    AppDbContext db,
    StartupReadinessState startupReadiness,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    try
    {
        if (!startupReadiness.IsReady)
        {
            return Results.Problem(
                title: "Початкове налаштування не завершено",
                detail: startupReadiness.StatusMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            return Results.Problem(
                title: "База даних недоступна",
                detail: "Застосунок не готовий обробляти запити.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var pendingMigrations = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pendingMigrations.Count > 0)
        {
            return Results.Problem(
                title: "Схема бази даних застаріла",
                detail: $"Потрібно застосувати {pendingMigrations.Count} міграцій перед запуском робочого навантаження.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var missingIndexes = await DatabaseSchemaIntegrityVerifier.FindMissingRequiredIndexesAsync(
            db,
            cancellationToken);
        if (missingIndexes.Count > 0)
        {
            return Results.Problem(
                title: "Схема бази даних пошкоджена",
                detail: $"Відсутні обов'язкові унікальні індекси: {string.Join(", ", missingIndexes)}.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new { status = "готово" });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception exception)
    {
        loggerFactory
            .CreateLogger("HealthChecks.Readiness")
            .LogWarning(exception, "Не вдалося завершити перевірку готовності бази даних.");

        return Results.Problem(
            title: "Перевірка готовності не виконана",
            detail: "Застосунок тимчасово не готовий обробляти запити.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).ExcludeFromDescription();
Program.MapSpaFallbackRoutes(app);

app.Run();

public partial class Program
{
    // Відокремлює невідомі API-запити від клієнтського SPA fallback.
    public static void MapSpaFallbackRoutes(WebApplication app)
    {
        app.MapFallback("/api/{**path}", () => Results.Problem(
            title: "API-маршрут не знайдено",
            detail: "Запитаний API-маршрут не існує.",
            statusCode: StatusCodes.Status404NotFound));
        app.MapFallbackToFile("index.html");
    }
}
