using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using BlazorWasmDotNet8AspNetCoreHosted.Client;
using BlazorWasmDotNet8AspNetCoreHosted.Client.Services;

// Точка входу клієнтського застосунку
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Реєструємо HTTP-клієнт і API-сервіси.
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<BlazorWasmDotNet8AspNetCoreHosted.Client.Services.TimeSlotsApi>();
builder.Services.AddScoped<IAdminApi, AdminApi>();
builder.Services.AddScoped<IScheduleApi, ScheduleApi>();
builder.Services.AddScoped<ITeacherDraftsApi, TeacherDraftsApi>();

await builder.Build().RunAsync();
