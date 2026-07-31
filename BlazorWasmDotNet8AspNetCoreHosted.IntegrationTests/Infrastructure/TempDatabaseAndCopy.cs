using System.Reflection;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests.Infrastructure;

internal sealed class SqliteTempDatabase : IAsyncDisposable
{
    private readonly string _path;
    private readonly SqliteConnection _connection;

    public SqliteTempDatabase(string? seedPath = null)
    {
        _path = Path.Combine(Path.GetTempPath(), $"scheduleapp-autogen-{Guid.NewGuid():N}.sqlite");
        if (!string.IsNullOrWhiteSpace(seedPath) && File.Exists(seedPath))
        {
            File.Copy(seedPath, _path, overwrite: true);
        }
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        _connection.Open();
    }

    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new AppDbContext(options);
    }

    public async ValueTask DisposeAsync()
    {
        _connection.Dispose();

        if (File.Exists(_path))
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
                // Файл лишаємо у temp, якщо ОС ще тримає його відкритим.
            }
        }

        await ValueTask.CompletedTask;
    }
}

internal sealed class SqliteSnapshotFile : IAsyncDisposable
{
    public string Path { get; }

    private SqliteSnapshotFile(string path)
    {
        Path = path;
    }

    public static async Task<SqliteSnapshotFile> CreateFromSourceAsync(AppDbContext source)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"scheduleapp-autogen-snapshot-{Guid.NewGuid():N}.sqlite");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            await using var target = new AppDbContext(options);
            await DatabaseSnapshotCopier.CopyAllAsync(source, target);
        }
        finally
        {
            await connection.DisposeAsync();
        }

        return new SqliteSnapshotFile(path);
    }

    public async ValueTask DisposeAsync()
    {
        if (File.Exists(Path))
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // Файл лишаємо у temp, якщо ОС ще тримає його відкритим.
            }
        }

        await ValueTask.CompletedTask;
    }
}

internal static class DatabaseSnapshotCopier
{
    public static async Task CopyAllAsync(AppDbContext source, AppDbContext target)
    {
        await target.Database.EnsureCreatedAsync();
        await target.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");

        var previousAutoDetect = target.ChangeTracker.AutoDetectChangesEnabled;
        target.ChangeTracker.AutoDetectChangesEnabled = false;

        try
        {
            // Копіюємо всі сутності моделі в ізольовану SQLite-базу.
            foreach (var entityType in source.Model.GetEntityTypes()
                         .Where(t => t.ClrType is not null && !t.IsOwned())
                         .OrderBy(t => t.ClrType!.Name))
            {
                await CopyEntitySetAsync(source, target, entityType.ClrType!);
            }
        }
        finally
        {
            target.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetect;
            await target.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        }
    }

    private static Task CopyEntitySetAsync(AppDbContext source, AppDbContext target, Type clrType)
    {
        var method = typeof(DatabaseSnapshotCopier).GetMethod(nameof(CopyEntitySetAsyncCore), BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Не вдалося знайти метод копіювання сутностей.");
        return (Task)method.MakeGenericMethod(clrType).Invoke(null, new object[] { source, target })!;
    }

    private static async Task CopyEntitySetAsyncCore<TEntity>(AppDbContext source, AppDbContext target)
        where TEntity : class, new()
    {
        var rows = await source.Set<TEntity>().AsNoTracking().ToListAsync();
        if (rows.Count == 0)
        {
            return;
        }

        var entityType = source.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"Не знайдено метадані для типу {typeof(TEntity).Name}.");

        var writableProps = entityType.GetProperties()
            .Select(p => p.PropertyInfo)
            .Where(pi => pi is not null && pi.CanWrite)
            .Cast<PropertyInfo>()
            .ToArray();

        var clones = new List<TEntity>(rows.Count);
        foreach (var row in rows)
        {
            var clone = new TEntity();
            foreach (var prop in writableProps)
            {
                prop.SetValue(clone, prop.GetValue(row));
            }

            clones.Add(clone);
        }

        await target.AddRangeAsync(clones);
        await target.SaveChangesAsync();
        target.ChangeTracker.Clear();
    }
}
