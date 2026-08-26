using System.Reflection;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests.Infrastructure;

internal sealed class SqliteTempDatabase : IAsyncDisposable
{
    private readonly string _directoryPath;
    private readonly string _path;
    private readonly SqliteConnection _connection;

    public SqliteTempDatabase(string? seedPath = null)
    {
        _directoryPath = PrivateTempDirectory.Create("scheduleapp-autogen");
        _path = Path.Combine(_directoryPath, "working.sqlite");
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        try
        {
            if (!string.IsNullOrWhiteSpace(seedPath) && File.Exists(seedPath))
            {
                File.Copy(seedPath, _path, overwrite: true);
            }
            connection.Open();
            PrivateTempDirectory.RestrictFile(_path);
            _connection = connection;
        }
        catch
        {
            connection.Dispose();
            PrivateTempDirectory.Delete(_directoryPath);
            throw;
        }
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
        PrivateTempDirectory.Delete(_directoryPath);
        await ValueTask.CompletedTask;
    }
}

internal sealed class SqliteSnapshotFile : IAsyncDisposable
{
    private readonly string _directoryPath;
    public string Path { get; }

    private SqliteSnapshotFile(string directoryPath, string path)
    {
        _directoryPath = directoryPath;
        Path = path;
    }

    public static async Task<SqliteSnapshotFile> CreateFromSourceAsync(
        AppDbContext source,
        int? courseId = null)
    {
        var directoryPath = PrivateTempDirectory.Create("scheduleapp-autogen-snapshot");
        var path = System.IO.Path.Combine(directoryPath, "reference.sqlite");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        try
        {
            await connection.OpenAsync();
            PrivateTempDirectory.RestrictFile(path);
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            await using var target = new AppDbContext(options);
            await DatabaseSnapshotCopier.CopyAllAsync(source, target, courseId);
            return new SqliteSnapshotFile(directoryPath, path);
        }
        catch
        {
            await connection.DisposeAsync();
            PrivateTempDirectory.Delete(directoryPath);
            throw;
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        PrivateTempDirectory.Delete(_directoryPath);
        await ValueTask.CompletedTask;
    }
}

internal sealed class PrivateTemporaryFile : IDisposable
{
    private readonly string _directoryPath;
    public string Path { get; }

    public PrivateTemporaryFile(string prefix, string fileName)
    {
        _directoryPath = PrivateTempDirectory.Create(prefix);
        Path = System.IO.Path.Combine(_directoryPath, fileName);
    }

    public void RestrictIfCreated()
    {
        if (File.Exists(Path))
        {
            PrivateTempDirectory.RestrictFile(Path);
        }
    }

    public void Dispose()
        => PrivateTempDirectory.Delete(_directoryPath);
}

internal static class DatabaseSnapshotCopier
{
    private const int MaxRowsPerReferenceTable = 50_000;
    private static readonly Type[] AllowedEntityTypes =
    [
        typeof(Course),
        typeof(Department),
        typeof(LessonTypeRef),
        typeof(Building),
        typeof(BuildingTravel),
        typeof(Room),
        typeof(Group),
        typeof(BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities.Module),
        typeof(ModuleCourse),
        typeof(Teacher),
        typeof(TeacherModule),
        typeof(ModuleSupervisor),
        typeof(ModuleRoom),
        typeof(ModuleBuilding),
        typeof(ModulePlan),
        typeof(TeacherCourseLoad),
        typeof(TeacherWorkingHour),
        typeof(ModuleTopic),
        typeof(LunchConfig),
        typeof(PreferredFirstSlotLimitConfig),
        typeof(CalendarException),
        typeof(ModuleSequenceItem),
        typeof(ModuleFiller),
        typeof(TimeSlot)
    ];

    internal static IReadOnlySet<Type> AllowedTypes { get; }
        = AllowedEntityTypes.ToHashSet();

    public static async Task CopyAllAsync(
        AppDbContext source,
        AppDbContext target,
        int? courseId = null)
    {
        var scope = courseId is int selectedCourseId
            ? await DatabaseSnapshotScope.CreateAsync(source, selectedCourseId)
            : null;
        await target.Database.EnsureCreatedAsync();
        await target.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");

        var previousAutoDetect = target.ChangeTracker.AutoDetectChangesEnabled;
        target.ChangeTracker.AutoDetectChangesEnabled = false;

        try
        {
            // Копіюємо лише необхідні для автогенерації довідники; робочі чернетки,
            // розклад, job і plan payload навмисно не потрапляють до snapshot.
            foreach (var entityType in AllowedEntityTypes)
            {
                await CopyEntitySetAsync(source, target, entityType, scope);
            }
        }
        finally
        {
            target.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetect;
            await target.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        }
    }

    private static Task CopyEntitySetAsync(
        AppDbContext source,
        AppDbContext target,
        Type clrType,
        DatabaseSnapshotScope? scope)
    {
        var method = typeof(DatabaseSnapshotCopier).GetMethod(nameof(CopyEntitySetAsyncCore), BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Не вдалося знайти метод копіювання сутностей.");
        return (Task)method.MakeGenericMethod(clrType).Invoke(null, new object?[] { source, target, scope })!;
    }

    private static async Task CopyEntitySetAsyncCore<TEntity>(
        AppDbContext source,
        AppDbContext target,
        DatabaseSnapshotScope? scope)
        where TEntity : class, new()
    {
        var query = ApplyScope(source.Set<TEntity>().AsNoTracking(), scope);
        var rows = await query
            .Take(MaxRowsPerReferenceTable + 1)
            .ToListAsync();
        if (rows.Count > MaxRowsPerReferenceTable)
        {
            throw new InvalidOperationException(
                $"Довідник {typeof(TEntity).Name} перевищує безпечний ліміт {MaxRowsPerReferenceTable} рядків для локального snapshot.");
        }
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

            Sanitize(clone);

            clones.Add(clone);
        }

        await target.AddRangeAsync(clones);
        await target.SaveChangesAsync();
        target.ChangeTracker.Clear();
    }

    private static IQueryable<TEntity> ApplyScope<TEntity>(
        IQueryable<TEntity> query,
        DatabaseSnapshotScope? scope)
        where TEntity : class
    {
        if (scope is null)
        {
            return query;
        }

        object scoped = query;
        if (typeof(TEntity) == typeof(Course))
            scoped = ((IQueryable<Course>)scoped).Where(item => scope.CourseIds.Contains(item.Id));
        else if (typeof(TEntity) == typeof(Group))
            scoped = ((IQueryable<Group>)scoped).Where(item => item.CourseId == scope.SelectedCourseId);
        else if (typeof(TEntity) == typeof(BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities.Module))
            scoped = ((IQueryable<BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities.Module>)scoped)
                .Where(item => scope.ModuleIds.Contains(item.Id));
        else if (typeof(TEntity) == typeof(ModuleCourse))
            scoped = ((IQueryable<ModuleCourse>)scoped)
                .Where(item => item.CourseId == scope.SelectedCourseId && scope.ModuleIds.Contains(item.ModuleId));
        else if (typeof(TEntity) == typeof(Teacher))
            scoped = ((IQueryable<Teacher>)scoped).Where(item => scope.TeacherIds.Contains(item.Id));
        else if (typeof(TEntity) == typeof(Department))
            scoped = ((IQueryable<Department>)scoped).Where(item => scope.DepartmentIds.Contains(item.Id));
        else if (typeof(TEntity) == typeof(TeacherModule))
            scoped = ((IQueryable<TeacherModule>)scoped)
                .Where(item => scope.TeacherIds.Contains(item.TeacherId) && scope.ModuleIds.Contains(item.ModuleId));
        else if (typeof(TEntity) == typeof(ModuleSupervisor))
            scoped = ((IQueryable<ModuleSupervisor>)scoped)
                .Where(item => scope.TeacherIds.Contains(item.TeacherId) && scope.ModuleIds.Contains(item.ModuleId));
        else if (typeof(TEntity) == typeof(TeacherCourseLoad))
            scoped = ((IQueryable<TeacherCourseLoad>)scoped)
                .Where(item => item.CourseId == scope.SelectedCourseId && scope.TeacherIds.Contains(item.TeacherId));
        else if (typeof(TEntity) == typeof(TeacherWorkingHour))
            scoped = ((IQueryable<TeacherWorkingHour>)scoped)
                .Where(item => scope.TeacherIds.Contains(item.TeacherId));
        else if (typeof(TEntity) == typeof(ModuleRoom))
            scoped = ((IQueryable<ModuleRoom>)scoped).Where(item => scope.ModuleIds.Contains(item.ModuleId));
        else if (typeof(TEntity) == typeof(ModuleBuilding))
            scoped = ((IQueryable<ModuleBuilding>)scoped).Where(item => scope.ModuleIds.Contains(item.ModuleId));
        else if (typeof(TEntity) == typeof(ModulePlan))
            scoped = ((IQueryable<ModulePlan>)scoped)
                .Where(item => item.CourseId == scope.SelectedCourseId && scope.ModuleIds.Contains(item.ModuleId));
        else if (typeof(TEntity) == typeof(ModuleTopic))
            scoped = ((IQueryable<ModuleTopic>)scoped).Where(item => scope.ModuleIds.Contains(item.ModuleId));
        else if (typeof(TEntity) == typeof(LunchConfig))
            scoped = ((IQueryable<LunchConfig>)scoped)
                .Where(item => item.CourseId == null || item.CourseId == scope.SelectedCourseId);
        else if (typeof(TEntity) == typeof(PreferredFirstSlotLimitConfig))
            scoped = ((IQueryable<PreferredFirstSlotLimitConfig>)scoped)
                .Where(item => item.CourseId == null || item.CourseId == scope.SelectedCourseId);
        else if (typeof(TEntity) == typeof(TimeSlot))
            scoped = ((IQueryable<TimeSlot>)scoped)
                .Where(item => item.CourseId == null || item.CourseId == scope.SelectedCourseId);
        else if (typeof(TEntity) == typeof(CalendarException))
            scoped = ((IQueryable<CalendarException>)scoped)
                .Where(item => (item.CourseId == null || item.CourseId == scope.SelectedCourseId)
                               && (item.GroupId == null || scope.GroupIds.Contains(item.GroupId.Value)));
        else if (typeof(TEntity) == typeof(ModuleSequenceItem))
            scoped = ((IQueryable<ModuleSequenceItem>)scoped)
                .Where(item => item.CourseId == scope.SelectedCourseId && scope.ModuleIds.Contains(item.ModuleId));
        else if (typeof(TEntity) == typeof(ModuleFiller))
            scoped = ((IQueryable<ModuleFiller>)scoped)
                .Where(item => item.CourseId == scope.SelectedCourseId && scope.ModuleIds.Contains(item.ModuleId));

        return (IQueryable<TEntity>)scoped;
    }

    private static void Sanitize<TEntity>(TEntity clone)
        where TEntity : class
    {
        if (clone is Teacher teacher)
        {
            teacher.FullName = $"Викладач {teacher.Id}";
            teacher.ScientificDegree = null;
            teacher.AcademicTitle = null;
        }
        else if (clone is Building building)
        {
            building.Address = null;
        }
    }

    private sealed record DatabaseSnapshotScope(
        int SelectedCourseId,
        IReadOnlySet<int> CourseIds,
        IReadOnlySet<int> GroupIds,
        IReadOnlySet<int> ModuleIds,
        IReadOnlySet<int> TeacherIds,
        IReadOnlySet<int> DepartmentIds)
    {
        public static async Task<DatabaseSnapshotScope> CreateAsync(
            AppDbContext source,
            int courseId)
        {
            var courseExists = await source.Courses.AsNoTracking().AnyAsync(item => item.Id == courseId);
            if (!courseExists)
            {
                throw new InvalidOperationException($"Курс #{courseId} не знайдено для локального snapshot.");
            }

            var groupIds = (await source.Groups.AsNoTracking()
                .Where(item => item.CourseId == courseId)
                .Select(item => item.Id)
                .ToListAsync())
                .ToHashSet();
            var modules = await source.Modules.AsNoTracking()
                .Where(item => item.CourseId == courseId
                               || item.ModuleCourses.Any(link => link.CourseId == courseId))
                .Select(item => new { item.Id, item.CourseId })
                .ToListAsync();
            var moduleIds = modules.Select(item => item.Id).ToHashSet();
            var courseIds = modules.Select(item => item.CourseId).Append(courseId).ToHashSet();
            var teacherIds = (await source.TeacherCourseLoads.AsNoTracking()
                    .Where(item => item.CourseId == courseId)
                    .Select(item => item.TeacherId)
                    .ToListAsync())
                .Concat(await source.TeacherModules.AsNoTracking()
                    .Where(item => moduleIds.Contains(item.ModuleId))
                    .Select(item => item.TeacherId)
                    .ToListAsync())
                .Concat(await source.ModuleSupervisors.AsNoTracking()
                    .Where(item => moduleIds.Contains(item.ModuleId))
                    .Select(item => item.TeacherId)
                    .ToListAsync())
                .ToHashSet();
            var departmentIds = (await source.Teachers.AsNoTracking()
                    .Where(item => teacherIds.Contains(item.Id) && item.DepartmentId != null)
                    .Select(item => item.DepartmentId!.Value)
                    .ToListAsync())
                .Concat(await source.ModuleTopics.AsNoTracking()
                    .Where(item => moduleIds.Contains(item.ModuleId) && item.DepartmentId != null)
                    .Select(item => item.DepartmentId!.Value)
                    .ToListAsync())
                .ToHashSet();
            return new DatabaseSnapshotScope(
                courseId,
                courseIds,
                groupIds,
                moduleIds,
                teacherIds,
                departmentIds);
        }
    }
}

internal static class PrivateTempDirectory
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static string Create(string prefix)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
        return path;
    }

    public static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
    }

    public static void Delete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, recursive: true);
        if (Directory.Exists(path))
        {
            throw new IOException($"Не вдалося видалити приватний тимчасовий каталог '{path}'.");
        }
    }
}
