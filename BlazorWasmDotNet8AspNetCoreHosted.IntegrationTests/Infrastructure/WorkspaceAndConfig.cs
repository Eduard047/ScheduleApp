using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests.Infrastructure;

internal static class WorkspacePaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();
    public static string ServerProjectPath => Path.Combine(RepositoryRoot, "BlazorWasmDotNet8AspNetCoreHosted.Server");

    private static string FindRepositoryRoot()
    {
        // Шукаємо корінь репозиторію від папки запуску тестів угору по дереву.
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BlazorWasmDotNet8AspNetCoreHosted.Server.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Не вдалося знайти корінь репозиторію для integration tests.");
    }
}

internal static class ServerConfigurationFactory
{
    public static IConfigurationRoot Create()
    {
        // Беремо ті самі локальні appsettings і user-secrets, що й сервер.
        return new ConfigurationBuilder()
            .SetBasePath(WorkspacePaths.ServerProjectPath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
            .AddUserSecrets<AppDbContext>(optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    public static AppDbContext CreateSourceContext()
    {
        var cs = Create().GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(cs))
        {
            throw new InvalidOperationException("Не знайдено connection string 'Default' у локальній конфігурації сервера.");
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(cs, new MySqlServerVersion(new Version(8, 0, 13)))
            .Options;

        return new AppDbContext(options);
    }
}
