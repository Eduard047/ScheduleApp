using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

// Сервіс імпорту модулів та тем із DOCX-документів.
public sealed class DocxImportService
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;
    private const long MaxCharactersPerPart = 2_000_000;
    private const int MaxTableCount = 500;
    private const int MaxRowCount = 20_000;
    private const int MaxCellCount = 100_000;
    private const int MaxHeaderOrCellTextLength = 16_384;
    private const int MaxParsedModuleCount = 500;
    private const int MaxParsedTopicCount = 5_000;
    private const long MaxEstimatedDatabaseOperationCount = 50_000;
    private static readonly TimeSpan ImportDeadline = TimeSpan.FromSeconds(45);
    private static readonly SemaphoreSlim ImportConcurrencyGate = new(1, 1);
    private const RegexOptions LinearRegexOptions = RegexOptions.Compiled
                                                    | RegexOptions.CultureInvariant
                                                    | RegexOptions.NonBacktracking;
    private static readonly Regex CourseCodeRegex = new(
        @"(?<![A-Za-zА-Яа-яІіЇїЄєҐґ0-9])[A-Za-zА-Яа-яІіЇїЄєҐґ]{1,6}-\d+(?![A-Za-zА-Яа-яІіЇїЄєҐґ0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ModulePrefixRegex = new(@"\b\d+\.\d+\.\d+\b", LinearRegexOptions);
    private static readonly Regex TopicCodeRegex = new(@"\d+(?:\.\d+){2,}", LinearRegexOptions);
    private static readonly Regex LetterTopicCodeRegex = new(@"[A-Za-zА-Яа-яІіЇїЄєҐґ]+\.?\d+(?:\.\d+)*\.?", LinearRegexOptions);
    private static readonly Regex LetteredModulePrefixRegex = new(@"[A-Za-zА-Яа-яІіЇїЄєҐґ]+\.?\d+(?:\.\d+)*\.?", LinearRegexOptions);
    private static readonly Regex NumericDottedCodeRegex = new(@"^\d+(?:\.\d+)+$", LinearRegexOptions);
    private static readonly Regex LetterPrefixedCodeRegex = new(@"^[A-Za-zА-Яа-яІіЇїЄєҐґ]+\.?(?<tail>\d+(?:\.\d+)*)$", LinearRegexOptions);
    private static readonly Regex NumericModuleCodeRegex = new(@"^\d+(?:\.\d+)*$", LinearRegexOptions);
    private static readonly Regex ModuleRowCodeRegex = new(@"^\d+(?:[.,]\d+)?$", LinearRegexOptions);
    private static readonly Regex LetterHeadRegex = new(@"^[A-Za-zА-Яа-яІіЇїЄєҐґ]+", LinearRegexOptions);
    private static readonly Regex HeaderPrefixRegex = new(@"(?:[A-Za-zА-Яа-яІіЇїЄєҐґ]+\.?)?\d+(?:\.\d+)+", LinearRegexOptions);
    private static readonly Regex HeaderModuleCodeRegex = new(
        @"(?:^|[^A-Za-zА-Яа-яІіЇїЄєҐґ0-9])(?<code>\d+(?:\.\d+)*)(?:$|[.\s])",
        LinearRegexOptions);
    private static readonly Regex NumericLessonTypeRegex = new(@"^\d+\.$", LinearRegexOptions);
    private static readonly Regex NumericHeaderRegex = new(@"^\d+$", LinearRegexOptions);
    // Зчитує DOCX і формує результат імпорту з опційним застосуванням у БД.
    public async Task<DocxImportResultDto> ImportAsync(IFormFile file, AppDbContext db, bool apply, CancellationToken ct)
    {
        if (!await ImportConcurrencyGate.WaitAsync(TimeSpan.Zero, ct))
        {
            return new DocxImportResultDto(
                string.Empty,
                null,
                false,
                new(),
                new() { "Інший DOCX-імпорт уже виконується" },
                "Одночасно дозволено лише один DOCX-імпорт. Повторіть спробу пізніше");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ImportDeadline);
        try
        {
            return await ImportCoreAsync(file, db, apply, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            return new DocxImportResultDto(
                string.Empty,
                null,
                false,
                new(),
                new() { "Імпорт перевищив безпечний час виконання" },
                "DOCX-імпорт не завершився у безпечний час. Зменште документ і повторіть спробу");
        }
        finally
        {
            ImportConcurrencyGate.Release();
        }
    }

    private static async Task<DocxImportResultDto> ImportCoreAsync(
        IFormFile file,
        AppDbContext db,
        bool apply,
        CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return new DocxImportResultDto(string.Empty, null, false, new(), new() { "Файл порожній або не надісланий" }, "Файл порожній або не надісланий");
        if (file.Length > MaxFileSizeBytes)
            return new DocxImportResultDto(string.Empty, null, false, new(), new() { "Розмір DOCX-файлу перевищує дозволені 10 МБ" }, "Розмір DOCX-файлу перевищує дозволені 10 МБ");
        if (file.FileName.Length > MaxHeaderOrCellTextLength)
            return CreateOversizedTextResult();
        using var buffer = new MemoryStream((int)Math.Min(file.Length, MaxFileSizeBytes));
        await using (var input = file.OpenReadStream())
        {
            var chunk = new byte[81920];
            long totalBytes = 0;
            int bytesRead;
            while ((bytesRead = await input.ReadAsync(chunk.AsMemory(0, chunk.Length), ct)) > 0)
            {
                totalBytes += bytesRead;
                if (totalBytes > MaxFileSizeBytes)
                {
                    return new DocxImportResultDto(string.Empty, null, false, new(), new() { "Розмір DOCX-файлу перевищує дозволені 10 МБ" }, "Розмір DOCX-файлу перевищує дозволені 10 МБ");
                }
                await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), ct);
            }
        }
        buffer.Position = 0;
        WordprocessingDocument openedDocument;
        try
        {
            openedDocument = WordprocessingDocument.Open(
                buffer,
                false,
                new OpenSettings { MaxCharactersInPart = MaxCharactersPerPart });
        }
        catch (Exception exception) when (exception is OpenXmlPackageException
                                                   or FileFormatException
                                                   or IOException
                                                   or System.Xml.XmlException)
        {
            return new DocxImportResultDto(string.Empty, null, false, new(), new() { "Файл не є коректним DOCX-документом або має надто великий вміст" }, "Файл не є коректним DOCX-документом або має надто великий вміст");
        }
        using var doc = openedDocument;
        List<string> allTexts;
        List<Table> tables;
        try
        {
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null)
                return new DocxImportResultDto(string.Empty, null, false, new(), new() { "Не вдалося прочитати тіло документа" }, "Не вдалося прочитати тіло документа");
            var documentTables = body.Descendants<Table>().Take(MaxTableCount + 1).ToList();
            var documentRows = body.Descendants<TableRow>().Take(MaxRowCount + 1).ToList();
            var documentCells = body.Descendants<TableCell>().Take(MaxCellCount + 1).ToList();
            if (documentTables.Count > MaxTableCount
                || documentRows.Count > MaxRowCount
                || documentCells.Count > MaxCellCount)
            {
                return new DocxImportResultDto(string.Empty, null, false, new(), new() { "DOCX-документ має надто складну структуру для безпечного імпорту" }, "DOCX-документ має надто складну структуру для безпечного імпорту");
            }
            if (documentCells.Any(cell => cell.InnerText.Length > MaxHeaderOrCellTextLength))
            {
                return CreateOversizedTextResult();
            }
            allTexts = body.Descendants<Text>().Select(text => text.Text ?? string.Empty).ToList();
            if (allTexts.Any(text => text.Length > MaxHeaderOrCellTextLength))
            {
                return CreateOversizedTextResult();
            }
            tables = documentTables;
        }
        catch (Exception exception) when (exception is OpenXmlPackageException
                                                   or FileFormatException
                                                   or IOException
                                                   or System.Xml.XmlException)
        {
            return new DocxImportResultDto(string.Empty, null, false, new(), new() { "Файл не є коректним DOCX-документом або має надто великий вміст" }, "Файл не є коректним DOCX-документом або має надто великий вміст");
        }
        var warnings = new List<string>();
        var courseName = ResolveCourseName(file.FileName, allTexts);
        var modulesTable = tables.FirstOrDefault(LooksLikeModuleTable);
        var parsedModules = modulesTable is not null
            ? ParseModules(modulesTable, warnings)
            : new List<DocxImportModuleDto>();
        var topicTables = tables.Where(LooksLikeTopicTable).ToList();
        var moduleOrder = parsedModules.Select(m => m.Code).ToList();
        var knownModuleCodes = new HashSet<string>(moduleOrder, StringComparer.OrdinalIgnoreCase);
        var topicsByModule = ParseTopics(topicTables, moduleOrder, knownModuleCodes, warnings);
        var modulesByCode = parsedModules.ToDictionary(module => module.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var (moduleCode, topics) in topicsByModule)
        {
            if (!modulesByCode.TryGetValue(moduleCode, out var target))
            {
                warnings.Add($"Для модуля з кодом \"{moduleCode}\" знайдено теми, але такого модуля немає у таблиці модулів");
                continue;
            }
            target.Topics.Clear();
            target.Topics.AddRange(topics);
        }
        var parsedTopicCount = parsedModules.Sum(module => module.Topics.Count);
        if (parsedModules.Count > MaxParsedModuleCount || parsedTopicCount > MaxParsedTopicCount)
        {
            return new DocxImportResultDto(
                courseName ?? string.Empty,
                null,
                false,
                parsedModules,
                warnings,
                $"DOCX містить надто багато навчальних сутностей: дозволено до {MaxParsedModuleCount} модулів і {MaxParsedTopicCount} тем");
        }
        if (string.IsNullOrWhiteSpace(courseName))
        {
            return new DocxImportResultDto(string.Empty, null, false, parsedModules, warnings, "Не вдалося визначити назву курсу");
        }
        var normalizedCourseName = NormalizeCourseName(courseName);
        var allCourses = await db.Courses.AsNoTracking().ToListAsync(ct);
        var exactNameMatches = allCourses
            .Where(course => NormalizeCourseName(course.Name) == normalizedCourseName)
            .ToList();
        if (exactNameMatches.Count > 1)
        {
            return new DocxImportResultDto(
                courseName,
                null,
                false,
                parsedModules,
                warnings,
                $"Знайдено кілька курсів із назвою \"{courseName}\". Уточніть назву курсу в документі або файлі");
        }

        Course? course = exactNameMatches.SingleOrDefault();
        if (course is null)
        {
            var requestedCodeMatch = CourseCodeRegex.Match(courseName);
            var normalizedRequestedCode = requestedCodeMatch.Success
                ? NormalizeCourseName(requestedCodeMatch.Value)
                : string.Empty;
            var codeMatches = string.IsNullOrWhiteSpace(normalizedRequestedCode)
                ? new List<Course>()
                : allCourses
                    .Where(candidate => CourseCodeRegex
                        .Matches(candidate.Name)
                        .Cast<Match>()
                        .Any(match => NormalizeCourseName(match.Value) == normalizedRequestedCode))
                    .ToList();
            if (codeMatches.Count == 0)
            {
                return new DocxImportResultDto(courseName, null, false, parsedModules, warnings, $"Не знайдено курс \"{courseName}\"");
            }
            if (codeMatches.Count > 1)
            {
                return new DocxImportResultDto(
                    courseName,
                    null,
                    false,
                    parsedModules,
                    warnings,
                    $"Знайдено кілька курсів із кодом \"{courseName}\". Уточніть назву курсу в документі або файлі");
            }
            course = codeMatches[0];
        }
        var result = new DocxImportResultDto(course.Name, course.Id, true, parsedModules, warnings, null);
        if (!apply)
        {
            return result;
        }
        var roomCount = await db.Rooms.AsNoTracking().CountAsync(ct);
        var buildingCount = await db.Buildings.AsNoTracking().CountAsync(ct);
        var estimatedDatabaseOperations = checked(
            (long)parsedModules.Count * (8L + roomCount + buildingCount)
            + (long)parsedTopicCount * 3L);
        if (estimatedDatabaseOperations > MaxEstimatedDatabaseOperationCount)
        {
            return result with
            {
                Error = $"Імпорт потребує орієнтовно {estimatedDatabaseOperations} операцій із базою даних, що перевищує безпечний ліміт {MaxEstimatedDatabaseOperationCount}"
            };
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await ApplyAsync(db, course, parsedModules, result, ct);
            await transaction.CommitAsync(ct);
        }
        catch (DocxImportConflictException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return result with { Error = exception.Message };
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
        return result;
    }
    // Визначає код/назву курсу за назвою файлу або текстом документа.
    private static string? ResolveCourseName(string fileName, IEnumerable<string> docTexts)
    {
        var candidates = CourseCodeRegex.Matches(fileName).Select(m => m.Value).ToList();
        if (candidates.Count == 0)
        {
            candidates = docTexts
                .SelectMany(t => CourseCodeRegex.Matches(t).Cast<Match>().Select(m => m.Value))
                .ToList();
        }
        return candidates.FirstOrDefault();
    }
    // Парсить таблицю модулів у список DTO.
    private static List<DocxImportModuleDto> ParseModules(Table table, List<string> warnings)
    {
        var rows = table.Elements<TableRow>().Skip(1).ToList();
        var modules = new List<DocxImportModuleDto>();
        var moduleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var cells = GetRowCells(row);
            if (cells.Count < 3) continue;
            var rawCode = cells[0];
            if (string.IsNullOrWhiteSpace(rawCode)) continue;
            if (!ModuleRowCodeRegex.IsMatch(rawCode)) continue; // пропускаємо службові рядки типу Усього
            var code = NormalizeCode(rawCode.Replace(',', '.'));
            var title = NormalizeText(cells.ElementAtOrDefault(1) ?? string.Empty);
            var credits = ParseDecimal(cells.ElementAtOrDefault(2));
            if (!moduleCodes.Add(code))
            {
                warnings.Add($"Модуль з кодом \"{code}\" повторюється у таблиці");
                continue;
            }
            modules.Add(new DocxImportModuleDto(code, title, credits, new List<DocxImportTopicDto>()));
        }
        return modules;
    }
    // Парсить таблиці тем і групує їх за модулем.
    private static Dictionary<string, List<DocxImportTopicDto>> ParseTopics(IEnumerable<Table> tables, List<string> moduleOrder, HashSet<string> knownModuleCodes, List<string> warnings)
    {
        var result = new Dictionary<string, List<DocxImportTopicDto>>(StringComparer.OrdinalIgnoreCase);
        var tableIndex = 0;
        var remainingModules = new Queue<string>(moduleOrder);
        string? lastModuleCode = null;
        string? lastModulePrefix = null;
        foreach (var table in tables)
        {
            var rows = table.Elements<TableRow>().ToList();
            if (rows.Count < 2) continue;
            var moduleHeader = GetRowCells(rows[1]).FirstOrDefault() ?? string.Empty;
            var modulePrefixMatch = ModulePrefixRegex.Match(moduleHeader);
            string? modulePrefix = null;
            string? moduleCode = null;
            var moduleCodeFromLetter = false;
            var moduleCodeFromOrder = false;
            if (modulePrefixMatch.Success)
            {
                modulePrefix = modulePrefixMatch.Value;
                var segments = modulePrefix.Split('.');
                var best = string.Empty;
                for (var take = Math.Min(segments.Length, 3); take >= 1; take--)
                {
                    var candidate = string.Join('.', segments.Take(take));
                    if (knownModuleCodes.Contains(candidate))
                    {
                        best = candidate;
                        break;
                    }
                }
                moduleCode = string.IsNullOrWhiteSpace(best) ? segments[0] : best;
            }
            var letterPrefixMatch = LetteredModulePrefixRegex.Match(moduleHeader);
            var hasLetterPrefix = letterPrefixMatch.Success;
            if (hasLetterPrefix)
            {
                modulePrefix ??= letterPrefixMatch.Value.Trim().Trim('.');
            }
            if (moduleCode is null && modulePrefix is not null && hasLetterPrefix)
            {
                var letterHead = LetterHeadRegex.Match(modulePrefix).Value.ToUpperInvariant();
                if (letterHead.StartsWith("КП") && knownModuleCodes.Contains("13"))
                {
                    moduleCode = "13";
                    moduleCodeFromLetter = true;
                }
                else if (letterHead.StartsWith("К") && knownModuleCodes.Contains("14"))
                {
                    moduleCode = "14";
                    moduleCodeFromLetter = true;
                }
            }
            if (moduleCode is null && !hasLetterPrefix)
            {
                foreach (Match match in HeaderModuleCodeRegex.Matches(moduleHeader))
                {
                    var segments = match.Groups["code"].Value.Split('.');
                    for (var take = segments.Length; take >= 1; take--)
                    {
                        var candidate = string.Join('.', segments.Take(take));
                        if (!knownModuleCodes.Contains(candidate)) continue;
                        moduleCode = candidate;
                        modulePrefix ??= candidate;
                        break;
                    }
                    if (moduleCode is not null) break;
                }
            }
            if (moduleCode is null && tableIndex >= moduleOrder.Count && modulePrefix is not null && lastModuleCode is not null)
            {
                moduleCode = lastModuleCode;
                warnings.Add($"Таблицю з префіксом \"{modulePrefix}\" прив'язано до попереднього модуля \"{moduleCode}\".");
            }
            if (moduleCode is null && tableIndex < moduleOrder.Count)
            {
                moduleCode = moduleOrder[tableIndex];
                moduleCodeFromOrder = true;
                modulePrefix ??= moduleCode;
            }
            if (modulePrefix is not null &&
                moduleCode is not null &&
                modulePrefix.Contains('.') &&
                !knownModuleCodes.Contains(modulePrefix))
            {
                var isSameTree = moduleCodeFromLetter
                    ? true
                    : hasLetterPrefix && moduleCodeFromOrder
                    ? true
                    : moduleCode.Contains('.')
                    ? modulePrefix.StartsWith(moduleCode + ".", StringComparison.OrdinalIgnoreCase)
                    : string.Equals(modulePrefix.Split('.')[0], moduleCode, StringComparison.OrdinalIgnoreCase);
                if (!isSameTree)
                {
                    warnings.Add($"Пропущено таблицю тем з префіксом \"{modulePrefix}\" — такого модуля немає у переліку (уникнуто додавання тем до агрегуючого модуля \"{moduleCode}\").");
                    tableIndex++;
                    continue;
                }
            }
            if (moduleCode is null)
            {
                while (remainingModules.Count > 0 && result.ContainsKey(remainingModules.Peek()))
                {
                    remainingModules.Dequeue();
                }
                if (remainingModules.Count > 0)
                {
                    moduleCode = remainingModules.Dequeue();
                    modulePrefix ??= moduleCode;
                    warnings.Add($"Невідомий заголовок таблиці, прив'язано за порядком до модуля \"{moduleCode}\"");
                }
                else if (lastModuleCode is not null && modulePrefix is not null)
                {
                    moduleCode = lastModuleCode;
                    modulePrefix ??= lastModulePrefix ?? moduleCode;
                    warnings.Add("Додаткову таблицю тем прив'язано до попереднього модуля через вичерпаний перелік модулів.");
                }
                else
                {
                    warnings.Add("Не вдалося визначити модуль для однієї з таблиць тем.");
                    tableIndex++;
                    continue;
                }
            }
            if (string.IsNullOrWhiteSpace(moduleCode))
            {
                warnings.Add("Пропущено таблицю тем через відсутній код модуля.");
                tableIndex++;
                continue;
            }
            lastModuleCode = moduleCode;
            lastModulePrefix = modulePrefix ?? lastModulePrefix;
            if (!result.TryGetValue(moduleCode, out var topics))
            {
                topics = new List<DocxImportTopicDto>();
                result[moduleCode] = topics;
            }
            var moduleCodeValue = moduleCode;
            var order = 1;
            foreach (var row in rows.Skip(2))
            {
                var cells = GetRowCells(row);
                if (cells.All(string.IsNullOrWhiteSpace)) continue;
                if (cells.Count == 1 && !string.IsNullOrWhiteSpace(cells[0]))
                {
                    var headerPrefix = HeaderPrefixRegex.Match(cells[0]);
                    if (headerPrefix.Success)
                    {
                        modulePrefix = NormalizeLetterPrefixedCode(headerPrefix.Value, moduleCodeValue);
                        order = 1; // новий блок тем у тій самій таблиці — починаємо нумерацію заново
                    }
                    continue;
                }
                if (cells.Count == 5)
                {
                    // Деякі рядки (наприклад, «Залік») можуть мати пропущену колонку номера — додаємо порожню, щоб індексація збігалася.
                    cells.Insert(0, string.Empty);
                }
                if (cells.Count < 5) continue;
                if (cells.Count > 6) cells = cells.Take(6).ToList();
                var topicCell = cells.ElementAtOrDefault(5) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(topicCell))
                {
                    // Якщо в останній колонці порожньо, намагаємося брати назву/код теми з четвертої (інколи таблиці з'їжджають).
                    topicCell = cells.ElementAtOrDefault(4) ?? string.Empty;
                }
                if (string.IsNullOrWhiteSpace(topicCell)) continue; // пропускаємо «Всього» тощо
                var topicCode = ExtractTopicCode(topicCell, modulePrefix, moduleCode, order);
                var total = ParseInt(cells[2]);
                var auditorium = ParseInt(cells[3]);
                var self = ParseInt(cells[4]);
                var lessonTypeName = NormalizeText(cells[1]);
                if (string.Equals(lessonTypeName, "Залік", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(lessonTypeName, "Екзамен", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(lessonTypeName, "Всього", StringComparison.OrdinalIgnoreCase) ||
                    lessonTypeName.StartsWith("Всього", StringComparison.OrdinalIgnoreCase) ||
                    NumericLessonTypeRegex.IsMatch(lessonTypeName))
                {
                    // Пропускаємо заліки, екзамени, підсумкові рядки та службові номери-типи, користувач створюватиме їх вручну.
                    continue;
                }
                topics.Add(new DocxImportTopicDto(
                    moduleCodeValue,
                    topicCode,
                    lessonTypeName,
                    total,
                    auditorium,
                    self,
                    order++
                ));
            }
            tableIndex++;
        }
        return result;
    }
    // Застосовує результат імпорту до бази даних.
    private static async Task ApplyAsync(AppDbContext db, Course course, List<DocxImportModuleDto> modules, DocxImportResultDto result, CancellationToken ct)
    {
        ValidateParsedTopics(modules);
        var moduleCodes = modules.Select(m => m.Code).ToList();
        // Підбираємо модулі лише в межах поточного курсу, щоб однакові коди в різних курсах не змішувалися.
        var existingModuleCandidates = await db.Modules
            .Include(m => m.ModuleCourses)
            .Where(m => m.CourseId == course.Id && moduleCodes.Contains(m.Code))
            .OrderBy(m => m.Id)
            .ToListAsync(ct);
        var existingModules = new Dictionary<string, Module>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in existingModuleCandidates)
        {
            if (!existingModules.TryAdd(candidate.Code, candidate))
            {
                result.Warnings.Add($"Для коду модуля \"{candidate.Code}\" знайдено дубль у курсі #{course.Id}; використано запис з меншим ідентифікатором.");
            }
        }
        foreach (var module in modules)
        {
            if (existingModules.TryGetValue(module.Code, out var entity))
            {
                entity.Title = module.Title;
                entity.Credits = module.Credits;
                entity.CourseId = course.Id;
                if (!entity.ModuleCourses.Any(mc => mc.CourseId == course.Id))
                {
                    db.ModuleCourses.Add(new ModuleCourse { ModuleId = entity.Id, CourseId = course.Id });
                }
            }
            else
            {
                var newModule = new Module
                {
                    Code = module.Code,
                    Title = module.Title,
                    Credits = module.Credits,
                    CourseId = course.Id
                };
                db.Modules.Add(newModule);
                await db.SaveChangesAsync(ct);
                db.ModuleCourses.Add(new ModuleCourse { ModuleId = newModule.Id, CourseId = course.Id });
                existingModules[module.Code] = newModule;
            }
        }
        await db.SaveChangesAsync(ct);
        var allBuildingIds = await db.Buildings.Select(b => b.Id).ToListAsync(ct);
        var allRoomIds = await db.Rooms.Select(r => r.Id).ToListAsync(ct);
        // Вимикаємо активність агрегуючих модулів, якщо існують підмодулі з тим самим цілим кодом (наприклад, 6 з підмодулями 6.1, 6.2).
        var rootModuleCodes = modules
            .Where(m => m.Code.Contains('.'))
            .Select(m => m.Code.Split('.')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var planDefaults = await db.ModulePlans
            .Where(p => p.CourseId == course.Id)
            .ToDictionaryAsync(p => p.ModuleId, ct);
        foreach (var module in modules)
        {
            if (!existingModules.TryGetValue(module.Code, out var entity)) continue;
            var targetHours = (int)Math.Max(0, Math.Round(module.Credits * 30m));
            if (planDefaults.TryGetValue(entity.Id, out var plan))
            {
                plan.TargetHours = targetHours;
                plan.IsActive = !rootModuleCodes.Contains(module.Code);
            }
            else
            {
                db.ModulePlans.Add(new ModulePlan
                {
                    CourseId = course.Id,
                    ModuleId = entity.Id,
                    TargetHours = targetHours,
                    ScheduledHours = 0,
                    IsActive = !rootModuleCodes.Contains(module.Code)
                });
            }
            // Призначаємо всі доступні корпуси й аудиторії модулю.
            var existingRoomIds = await db.ModuleRooms.Where(x => x.ModuleId == entity.Id).Select(x => x.RoomId).ToListAsync(ct);
            var existingBuildingIds = await db.ModuleBuildings.Where(x => x.ModuleId == entity.Id).Select(x => x.BuildingId).ToListAsync(ct);
            var toAddRooms = allRoomIds.Except(existingRoomIds).ToList();
            var toRemoveRooms = existingRoomIds.Except(allRoomIds).ToList();
            if (toRemoveRooms.Count > 0)
            {
                await db.ModuleRooms.Where(x => x.ModuleId == entity.Id && toRemoveRooms.Contains(x.RoomId)).ExecuteDeleteAsync(ct);
            }
            foreach (var rid in toAddRooms)
                db.ModuleRooms.Add(new ModuleRoom { ModuleId = entity.Id, RoomId = rid });
            var toAddBuildings = allBuildingIds.Except(existingBuildingIds).ToList();
            var toRemoveBuildings = existingBuildingIds.Except(allBuildingIds).ToList();
            if (toRemoveBuildings.Count > 0)
            {
                await db.ModuleBuildings.Where(x => x.ModuleId == entity.Id && toRemoveBuildings.Contains(x.BuildingId)).ExecuteDeleteAsync(ct);
            }
            foreach (var bid in toAddBuildings)
                db.ModuleBuildings.Add(new ModuleBuilding { ModuleId = entity.Id, BuildingId = bid });
        }
        await db.SaveChangesAsync(ct);
        var lessonTypes = await db.LessonTypes.ToListAsync(ct);
        var lessonTypeLookup = lessonTypes.ToDictionary(
            lt => NormalizeText(lt.Name).ToUpperInvariant(),
            lt => lt,
            StringComparer.OrdinalIgnoreCase);
        var canonicalLessonTypeNames = BuildCanonicalLessonTypeNames(modules, lessonTypeLookup);
        foreach (var alias in canonicalLessonTypeNames)
        {
            if (!lessonTypeLookup.TryGetValue(alias.Key, out var sourceType))
            {
                continue;
            }
            var targetLookupKey = alias.Value.ToUpperInvariant();
            if (!lessonTypeLookup.TryGetValue(targetLookupKey, out var targetType)
                || sourceType.Id == targetType.Id)
            {
                continue;
            }
            try
            {
                await LessonTypeMergeService.MergeAsync(db, sourceType.Id, targetType.Id, ct);
            }
            catch (LessonTypeMergeException exception)
            {
                throw new DocxImportConflictException(
                    $"Імпорт скасовано: тип заняття \"{sourceType.Name}\" схожий на випадковий дубль \"{targetType.Name}\", але безпечне об'єднання неможливе. {exception.Message}");
            }
            lessonTypeLookup.Remove(alias.Key);
            AddLessonTypeNormalizationWarning(result, sourceType.Name, targetType.Name);
        }
        foreach (var module in modules)
        {
            if (!existingModules.TryGetValue(module.Code, out var entity))
            {
                result.Warnings.Add($"Не вдалося знайти або створити модуль \"{module.Code}\"");
                continue;
            }
            var existingTopics = await db.ModuleTopics
                .Where(t => t.ModuleId == entity.Id)
                .ToListAsync(ct);
            var existingCodes = existingTopics
                .Select(t => t.TopicCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var normalizedLegacyCodes = false;
            foreach (var existingTopic in existingTopics)
            {
                var normalizedCode = NormalizeLetterPrefixedCode(existingTopic.TopicCode, module.Code);
                if (string.Equals(existingTopic.TopicCode, normalizedCode, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (existingCodes.Contains(normalizedCode))
                {
                    // Якщо нормалізований код уже зайнятий, лишаємо поточний, щоб не створити конфлікт унікальності.
                    continue;
                }
                existingCodes.Remove(existingTopic.TopicCode);
                existingTopic.TopicCode = normalizedCode;
                existingCodes.Add(normalizedCode);
                normalizedLegacyCodes = true;
            }
            if (normalizedLegacyCodes)
            {
                await db.SaveChangesAsync(ct);
            }
            var existingByCode = existingTopics.ToDictionary(t => t.TopicCode, StringComparer.OrdinalIgnoreCase);
            var parsedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parsedEntities = new List<(ModuleTopic topic, int desiredOrder)>();
            foreach (var topic in module.Topics.OrderBy(t => t.Order))
            {
                parsedCodes.Add(topic.TopicCode);
                var importedLessonTypeName = NormalizeText(topic.LessonTypeName);
                var importedLessonTypeLookupKey = importedLessonTypeName.ToUpperInvariant();
                var effectiveLessonTypeName = canonicalLessonTypeNames.TryGetValue(
                    importedLessonTypeLookupKey,
                    out var canonicalLessonTypeName)
                        ? canonicalLessonTypeName
                        : importedLessonTypeName;
                var lessonTypeLookupKey = effectiveLessonTypeName.ToUpperInvariant();
                if (!string.Equals(
                        importedLessonTypeName,
                        effectiveLessonTypeName,
                        StringComparison.CurrentCultureIgnoreCase))
                {
                    AddLessonTypeNormalizationWarning(
                        result,
                        importedLessonTypeName,
                        effectiveLessonTypeName);
                }
                if (!lessonTypeLookup.TryGetValue(lessonTypeLookupKey, out var lessonType))
                {
                    // Створюємо вже канонічне значення, визначене за всім документом, а не за порядком рядків.
                    lessonType = new LessonTypeRef
                    {
                        Code = effectiveLessonTypeName.ToUpperInvariant().Replace(" ", "_"),
                        Name = effectiveLessonTypeName,
                        IsActive = true,
                        RequiresRoom = true,
                        RequiresTeacher = true,
                        BlocksRoom = true,
                        BlocksTeacher = true,
                        CountInPlan = true,
                        CountInLoad = true
                    };
                    db.LessonTypes.Add(lessonType);
                    await db.SaveChangesAsync(ct);
                    lessonTypeLookup[lessonTypeLookupKey] = lessonType;
                }
                ModuleTopic entityTopic;
                if (existingByCode.TryGetValue(topic.TopicCode, out var existingTopic))
                {
                    entityTopic = existingTopic;
                    if (entityTopic.LessonTypeId != lessonType.Id)
                    {
                        var topicIsUsed = await db.ScheduleItems
                                .AnyAsync(item => item.ModuleTopicId == entityTopic.Id, ct)
                            || await db.TeacherDraftItems
                                .AnyAsync(item => item.ModuleTopicId == entityTopic.Id, ct);
                        if (topicIsUsed)
                        {
                            throw new DocxImportConflictException(
                                $"Імпорт скасовано: тема \"{entityTopic.TopicCode}\" модуля \"{module.Code}\" вже використовується у розкладі або чернетках, тому її тип заняття не можна змінити на \"{lessonType.Name}\".");
                        }
                    }
                }
                else
                {
                    entityTopic = new ModuleTopic
                    {
                        ModuleId = entity.Id,
                        TopicCode = topic.TopicCode,
                        // тимчасовий великий порядок щоб уникнути конфлікту унікального індексу (ModuleId, Order)
                        Order = 100000 + parsedEntities.Count
                    };
                    db.ModuleTopics.Add(entityTopic);
                    existingTopics.Add(entityTopic);
                    existingByCode[topic.TopicCode] = entityTopic;
                }
                entityTopic.LessonTypeId = lessonType.Id;
                entityTopic.TotalHours = Math.Max(0, topic.TotalHours);
                entityTopic.AuditoriumHours = Math.Max(0, topic.AuditoriumHours);
                entityTopic.SelfStudyHours = Math.Max(0, topic.SelfStudyHours);
                parsedEntities.Add((entityTopic, topic.Order));
            }
            var remaining = existingTopics
                .Where(t => !parsedCodes.Contains(t.TopicCode))
                .OrderBy(t => t.Order)
                .ToList();
            var ordered = parsedEntities
                .OrderBy(t => t.desiredOrder)
                .ThenBy(t => t.topic.TopicCode, StringComparer.OrdinalIgnoreCase)
                .Select(t => t.topic)
                .Concat(remaining)
                .ToList();
            // Двофазне оновлення порядку запобігає циклу при перестановках унікального індексу (ModuleId, Order).
            var needsTwoPhaseOrderUpdate = ordered.Where((t, idx) => t.Order != idx + 1).Any();
            if (needsTwoPhaseOrderUpdate)
            {
                for (var i = 0; i < ordered.Count; i++)
                {
                    ordered[i].Order = 100000 + i;
                }
                await db.SaveChangesAsync(ct);
            }
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].Order = i + 1;
            }
            await db.SaveChangesAsync(ct);
        }
    }

    // Відхиляє неоднозначні теми до першої зміни бази даних.
    private static void ValidateParsedTopics(IEnumerable<DocxImportModuleDto> modules)
    {
        foreach (var module in modules)
        {
            var topicCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var topic in module.Topics)
            {
                var topicCode = topic.TopicCode.Trim();
                if (!topicCodes.Add(topicCode))
                {
                    throw new DocxImportConflictException(
                        $"Імпорт скасовано: код теми \"{topicCode}\" повторюється у модулі \"{module.Code}\".");
                }

                var hasNegativeHours = topic.TotalHours < 0
                                       || topic.AuditoriumHours < 0
                                       || topic.SelfStudyHours < 0;
                var assignedHours = (long)topic.AuditoriumHours + topic.SelfStudyHours;
                if (hasNegativeHours || assignedHours > topic.TotalHours)
                {
                    throw new DocxImportConflictException(
                        $"Імпорт скасовано: тема \"{topic.TopicCode}\" модуля \"{module.Code}\" має некоректний розподіл годин (усього {topic.TotalHours}, аудиторних {topic.AuditoriumHours}, самостійних {topic.SelfStudyHours}).");
                }
            }
        }
    }

    private sealed class DocxImportConflictException(string message) : InvalidOperationException(message);

    // Евристика для визначення таблиці модулів.
    private static bool LooksLikeModuleTable(Table table)
    {
        var header = table.Elements<TableRow>().FirstOrDefault();
        if (header is null) return false;
        var cells = GetRowCells(header);
        var headerText = string.Join(" ", cells).ToLowerInvariant();
        return cells.Count >= 3 && headerText.Contains("кредит");
    }
    // Евристика для визначення таблиці тем.
    private static bool LooksLikeTopicTable(Table table)
    {
        var rows = table.Elements<TableRow>().Take(3).ToList();
        if (rows.Count == 0) return false;
        bool IsNumericHeader(IReadOnlyList<string> cells)
        {
            if (cells.Count != 6) return false;
            for (var i = 0; i < 6; i++)
            {
                var value = cells[i].Trim().Trim('.');
                if (!NumericHeaderRegex.IsMatch(value)) return false;
                if (int.Parse(value) != i + 1) return false;
            }
            return true;
        }
        var firstCells = GetRowCells(rows[0]);
        if (IsNumericHeader(firstCells)) return true;
        for (var i = 1; i < rows.Count; i++)
        {
            var cells = GetRowCells(rows[i]);
            if (IsNumericHeader(cells)) return true;
        }
        return false;
    }
    // Повертає очищені тексти клітинок рядка.
    private static List<string> GetRowCells(TableRow row)
    {
        return row.Elements<TableCell>()
            .Select(cell => NormalizeText(cell.InnerText))
            .ToList();
    }
    // Нормалізує текст із DOCX, прибираючи зайві пробіли.
    private static string NormalizeText(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var normalized = new StringBuilder(input.Length);
        var previousWasSpace = false;
        foreach (var sourceCharacter in input)
        {
            var character = sourceCharacter is '\r' or '\n' or '\t' or '\u00A0'
                ? ' '
                : sourceCharacter;
            if (character == ' ')
            {
                if (previousWasSpace) continue;
                previousWasSpace = true;
            }
            else
            {
                previousWasSpace = false;
            }
            normalized.Append(character);
        }
        return normalized.ToString().Trim();
    }

    // Будує відповідності за всім документом, щоб результат не залежав від порядку модулів або тем.
    private static Dictionary<string, string> BuildCanonicalLessonTypeNames(
        IEnumerable<DocxImportModuleDto> modules,
        IReadOnlyDictionary<string, LessonTypeRef> existingLessonTypes)
    {
        var importedNames = modules
            .SelectMany(module => module.Topics)
            .Select(topic => NormalizeText(topic.LessonTypeName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(name => name.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var importedName in importedNames)
        {
            var collapsedName = CollapseAdjacentDuplicateWords(importedName.Value);
            if (string.Equals(importedName.Value, collapsedName, StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }
            var collapsedLookupKey = collapsedName.ToUpperInvariant();
            if (importedNames.TryGetValue(collapsedLookupKey, out var canonicalImportedName))
            {
                result[importedName.Key] = canonicalImportedName;
            }
            else if (existingLessonTypes.TryGetValue(collapsedLookupKey, out var canonicalExistingType))
            {
                result[importedName.Key] = canonicalExistingType.Name;
            }
        }
        return result;
    }

    private static void AddLessonTypeNormalizationWarning(
        DocxImportResultDto result,
        string importedName,
        string canonicalName)
    {
        var warning = $"Тип заняття \"{importedName}\" нормалізовано до \"{canonicalName}\": видалено випадково повторене сусіднє слово.";
        if (!result.Warnings.Contains(warning, StringComparer.CurrentCulture))
        {
            result.Warnings.Add(warning);
        }
    }

    // Прибирає лише безпосередньо повторені слова; нормалізація діє лише за наявності канонічного варіанта.
    private static string CollapseAdjacentDuplicateWords(string input)
    {
        var words = NormalizeText(input)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length < 2)
        {
            return string.Join(' ', words);
        }
        var result = new List<string>(words.Length) { words[0] };
        for (var index = 1; index < words.Length; index++)
        {
            if (!string.Equals(words[index], result[^1], StringComparison.CurrentCultureIgnoreCase))
            {
                result.Add(words[index]);
            }
        }
        return string.Join(' ', result);
    }

    private static DocxImportResultDto CreateOversizedTextResult()
    {
        const string message = "DOCX-документ містить клітинку або заголовок, довший за дозволені 16384 символи";
        return new DocxImportResultDto(string.Empty, null, false, new(), new() { message }, message);
    }
    private static string NormalizeCode(string raw) => NormalizeText(raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? raw);
    // Перетворює рядок у число з плаваючою крапкою.
    private static decimal ParseDecimal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0m;
        var normalized = raw.Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;
    }
    // Нормалізує назву курсу для порівняння.
    private static string NormalizeCourseName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var span = name.Where(c => !char.IsWhiteSpace(c)).ToArray();
        return new string(span).ToUpperInvariant();
    }
    // Перетворює рядок у ціле число з округленням.
    private static int ParseInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var normalized = raw.Replace(',', '.');
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;
        if (decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var dec))
            return (int)Math.Round(dec, MidpointRounding.AwayFromZero);
        return 0;
    }
    // Витягує код теми або генерує його за порядком.
    private static string ExtractTopicCode(string topicCell, string? modulePrefix, string? moduleCode, int order)
    {
        if (!string.IsNullOrWhiteSpace(topicCell))
        {
            var token = topicCell
                .Trim()
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(token))
            {
                var cleaned = token.Trim().Trim('.');
                if (NumericDottedCodeRegex.IsMatch(cleaned))
                {
                    return cleaned;
                }
                if (LetterPrefixedCodeRegex.IsMatch(cleaned))
                {
                    return NormalizeLetterPrefixedCode(cleaned, moduleCode);
                }
            }
        }
        var prefix = string.IsNullOrWhiteSpace(modulePrefix) ? moduleCode : modulePrefix;
        var normalizedPrefix = string.IsNullOrWhiteSpace(prefix)
            ? moduleCode
            : NormalizeLetterPrefixedCode(prefix, moduleCode);
        return $"{normalizedPrefix}.{order}";
    }
    // Нормалізує коди формату "Д.1.1.1" до числового коду модуля, наприклад "13.1.1.1".
    private static string NormalizeLetterPrefixedCode(string rawCode, string? moduleCode)
    {
        var cleaned = rawCode.Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return cleaned;
        }
        if (string.IsNullOrWhiteSpace(moduleCode) || !NumericModuleCodeRegex.IsMatch(moduleCode))
        {
            return cleaned;
        }
        var match = LetterPrefixedCodeRegex.Match(cleaned);
        if (!match.Success)
        {
            return cleaned;
        }
        var tail = match.Groups["tail"].Value.Trim().Trim('.');
        return string.IsNullOrWhiteSpace(tail)
            ? moduleCode
            : $"{moduleCode}.{tail}";
    }
}
