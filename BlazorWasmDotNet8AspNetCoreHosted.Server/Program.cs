using Microsoft.EntityFrameworkCore;
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

// Підключення до БД та конфігурація EF Core.
var cs = builder.Configuration.GetConnectionString("Default");
var serverVersion = new MySqlServerVersion(new Version(8, 0, 0));
builder.Services.AddDbContextPool<AppDbContext>(opt => opt.UseMySql(cs, serverVersion));

// Реєстрація доменних сервісів.
builder.Services.AddScoped<RulesService>();
builder.Services.AddScoped<AggregatesService>();
builder.Services.AddScoped<TeacherDraftsQueryService>();
builder.Services.AddScoped<TeacherDraftsExportService>();
builder.Services.AddScoped<TeacherDraftsAutogenService>();
builder.Services.AddSingleton<TeacherDraftsAutogenJobService>();
builder.Services.AddScoped<TeacherDraftsPublishService>();

var app = builder.Build();

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
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapRazorPages();
app.MapControllers();
app.MapFallbackToFile("index.html");

// Початкове заповнення довідника типів занять.
await DefaultLessonTypesSeeder.SeedAsync(app.Services);

app.Run();
