using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class DocxImportSecurityTests
{
    [Fact]
    public async Task Import_rejects_valid_docx_with_oversized_cell_before_database_matching()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        fixture.Db.Courses.Add(new Course { Name = "КН-1", DurationWeeks = 52 });
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateModuleDocx(new string('А', 20_000));
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply: false,
            CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("16384", result.Error, StringComparison.Ordinal);
        Assert.False(result.CourseFound);
        Assert.Empty(result.Modules);
    }

    [Fact]
    public async Task Import_preserves_normal_docx_behavior_with_bounded_parser()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var course = new Course { Name = "КН-1", DurationWeeks = 52 };
        fixture.Db.Courses.Add(course);
        await fixture.Db.SaveChangesAsync();
        var bytes = CreateModuleDocx("Тестовий модуль");
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "КН-1.docx");

        var result = await new DocxImportService().ImportAsync(
            file,
            fixture.Db,
            apply: false,
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.CourseFound);
        Assert.Equal(course.Id, result.CourseId);
        var module = Assert.Single(result.Modules);
        Assert.Equal("1", module.Code);
        Assert.Equal("Тестовий модуль", module.Title);
    }

    private static byte[] CreateModuleDocx(string moduleTitle)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(
                new Table(
                    CreateRow("Код", "Назва", "Кредити"),
                    CreateRow("1", moduleTitle, "1"))));
            mainPart.Document.Save();
        }
        return stream.ToArray();
    }

    private static TableRow CreateRow(params string[] values)
        => new(values.Select(value => new TableCell(new Paragraph(new Run(new Text(value))))));

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(SqliteConnection connection, AppDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public AppDbContext Db { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
