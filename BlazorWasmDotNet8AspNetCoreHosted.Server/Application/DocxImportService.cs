using System.Globalization;
using System.Data;
using System.Buffers.Binary;
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
    private const int MaxPackageEntryCount = 2_048;
    private const long MaxPackageEntryUncompressedSizeBytes = 16 * 1024 * 1024;
    private const long MaxPackageTotalUncompressedSizeBytes = 64 * 1024 * 1024;
    private const long MaxCharactersPerPart = 2_000_000;
    private const int MaxTableCount = CurriculumInputLimits.ImportTableCountMax;
    private const int MaxRowCount = 20_000;
    private const int MaxCellCount = 100_000;
    private const int MaxHeaderOrCellTextLength = 16_384;
    private const long MaxEstimatedDatabaseOperationCount = 50_000;
    private const int MaxCanonicalLessonTypeMergeCount = 100;
    private const int LessonTypeMergeWorkloadPassCount = 3;
    private const long MaxLessonTypeMergeTraversalCharacters =
        LessonTypeMergeService.MaxHistoricalJsonCharacters;
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
        if (!HasSafePackageMetadata(buffer, ct))
        {
            return CreateUnsafePackageResult();
        }
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
        var parsedModules = new List<DocxImportModuleDto>();
        try
        {
            var modulesTable = tables.FirstOrDefault(LooksLikeModuleTable);
            parsedModules = modulesTable is not null
                ? ParseModules(modulesTable)
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
            ValidateParsedCurriculum(parsedModules);
        }
        catch (DocxImportValidationException exception)
        {
            return CreateValidationErrorResult(courseName, parsedModules, warnings, exception.Message);
        }
        var parsedTopicCount = parsedModules.Sum(module => module.Topics.Count);
        if (string.IsNullOrWhiteSpace(courseName))
        {
            return new DocxImportResultDto(string.Empty, null, false, parsedModules, warnings, "Не вдалося визначити назву курсу");
        }
        Course course;
        try
        {
            course = await ResolveCourseAsync(db, courseName, ct);
        }
        catch (DocxImportValidationException exception)
        {
            return CreateValidationErrorResult(courseName, parsedModules, warnings, exception.Message);
        }
        LessonTypeValidationSummary lessonTypeValidation;
        try
        {
            lessonTypeValidation = await ValidateLessonTypeCodesAsync(db, parsedModules, ct);
        }
        catch (DocxImportValidationException exception)
        {
            return CreateValidationErrorResult(course.Name, parsedModules, warnings, exception.Message, course.Id);
        }
        var result = new DocxImportResultDto(course.Name, course.Id, true, parsedModules, warnings, null);
        var operationBudgetError = await GetOperationBudgetErrorAsync(
            db,
            parsedModules.Count,
            parsedTopicCount,
            lessonTypeValidation.EstimatedRequestOperations,
            ct);
        if (operationBudgetError is not null)
        {
            return result with { Error = operationBudgetError };
        }
        if (!apply)
        {
            return result;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var committed = false;
        try
        {
            // Повторне визначення всередині serializable-транзакції не дозволяє
            // застосувати документ до іншого або вже неоднозначного курсу.
            var transactionCourse = await ResolveCourseAsync(db, courseName, ct);
            if (transactionCourse.Id != course.Id
                || !string.Equals(transactionCourse.Name, course.Name, StringComparison.Ordinal))
            {
                throw new DocxImportConflictException(
                    "Імпорт скасовано: вибраний курс змінився після перевірки. Повторіть попередній перегляд і застосування.");
            }
            course = transactionCourse;
            lessonTypeValidation = await ValidateLessonTypeCodesAsync(db, parsedModules, ct);
            operationBudgetError = await GetOperationBudgetErrorAsync(
                db,
                parsedModules.Count,
                parsedTopicCount,
                lessonTypeValidation.EstimatedRequestOperations,
                ct);
            if (operationBudgetError is not null)
            {
                throw new DocxImportValidationException(operationBudgetError);
            }
            await ApplyAsync(
                db,
                course,
                parsedModules,
                result,
                lessonTypeValidation.ValidatedMergeWorkloads,
                ct);
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            committed = true;
        }
        catch (Exception exception) when (exception is DocxImportConflictException or DocxImportValidationException)
        {
            if (!committed)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            db.ChangeTracker.Clear();
            return result with { Error = exception.Message };
        }
        catch (Exception exception) when (exception is DbUpdateException or OverflowException)
        {
            if (!committed)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            db.ChangeTracker.Clear();
            return result with
            {
                Error = "Імпорт скасовано: дані документа не відповідають безпечним обмеженням навчальних довідників. Перевірте коди, назви та числові значення."
            };
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            db.ChangeTracker.Clear();
            throw;
        }
        return result;
    }

    // Перевіряє центральний каталог ZIP до відкриття OPC-пакета, не розпаковуючи його вміст.
    private static bool HasSafePackageMetadata(Stream package, CancellationToken ct)
    {
        const uint endOfCentralDirectorySignature = 0x06054b50;
        const uint centralDirectoryEntrySignature = 0x02014b50;
        const int endOfCentralDirectorySize = 22;
        const int centralDirectoryEntrySize = 46;
        const int maximumZipCommentLength = ushort.MaxValue;

        try
        {
            if (!package.CanSeek || package.Length < endOfCentralDirectorySize)
            {
                return false;
            }

            var tailLength = (int)Math.Min(
                package.Length,
                endOfCentralDirectorySize + (long)maximumZipCommentLength);
            var tail = new byte[tailLength];
            package.Position = package.Length - tailLength;
            package.ReadExactly(tail);

            var endRecordOffsetInTail = -1;
            for (var index = tail.Length - endOfCentralDirectorySize; index >= 0; index--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, sizeof(uint)))
                    != endOfCentralDirectorySignature)
                {
                    continue;
                }

                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(index + 20, sizeof(ushort)));
                if (index + endOfCentralDirectorySize + commentLength == tail.Length)
                {
                    endRecordOffsetInTail = index;
                    break;
                }
            }

            if (endRecordOffsetInTail < 0)
            {
                return false;
            }

            var endRecord = tail.AsSpan(endRecordOffsetInTail, endOfCentralDirectorySize);
            var diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[4..]);
            var centralDirectoryDiskNumber = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[6..]);
            var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[8..]);
            var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[10..]);
            var centralDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(endRecord[12..]);
            var centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(endRecord[16..]);
            if (diskNumber != 0
                || centralDirectoryDiskNumber != 0
                || entriesOnDisk != entryCount
                || entryCount is 0 or ushort.MaxValue
                || entryCount > MaxPackageEntryCount
                || centralDirectorySize == uint.MaxValue
                || centralDirectoryOffset == uint.MaxValue)
            {
                return false;
            }

            var endRecordOffset = package.Length - tailLength + endRecordOffsetInTail;
            var centralDirectoryEnd = (long)centralDirectoryOffset + centralDirectorySize;
            if (centralDirectoryEnd > endRecordOffset || centralDirectoryEnd > package.Length)
            {
                return false;
            }

            package.Position = centralDirectoryOffset;
            long totalUncompressedSize = 0;
            Span<byte> entryHeader = stackalloc byte[centralDirectoryEntrySize];
            for (var index = 0; index < entryCount; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (centralDirectoryEnd - package.Position < centralDirectoryEntrySize)
                {
                    return false;
                }

                package.ReadExactly(entryHeader);
                if (BinaryPrimitives.ReadUInt32LittleEndian(entryHeader) != centralDirectoryEntrySignature)
                {
                    return false;
                }

                var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(entryHeader[24..]);
                if (uncompressedSize == uint.MaxValue
                    || uncompressedSize > MaxPackageEntryUncompressedSizeBytes
                    || totalUncompressedSize > MaxPackageTotalUncompressedSizeBytes - uncompressedSize)
                {
                    return false;
                }
                totalUncompressedSize += uncompressedSize;

                var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(entryHeader[28..]);
                var extraFieldLength = BinaryPrimitives.ReadUInt16LittleEndian(entryHeader[30..]);
                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(entryHeader[32..]);
                var variableMetadataLength = (long)fileNameLength + extraFieldLength + commentLength;
                if (variableMetadataLength > centralDirectoryEnd - package.Position)
                {
                    return false;
                }
                package.Position += variableMetadataLength;
            }

            return package.Position == centralDirectoryEnd;
        }
        catch (Exception exception) when (exception is IOException
                                                   or InvalidDataException
                                                   or ArgumentOutOfRangeException)
        {
            return false;
        }
        finally
        {
            package.Position = 0;
        }
    }

    private static DocxImportResultDto CreateUnsafePackageResult()
    {
        const string message = "Файл не є коректним DOCX-документом або ZIP-вміст перевищує безпечні обмеження";
        return new DocxImportResultDto(string.Empty, null, false, new(), new() { message }, message);
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

    private static async Task<Course> ResolveCourseAsync(
        AppDbContext db,
        string courseName,
        CancellationToken cancellationToken)
    {
        var normalizedCourseName = NormalizeCourseName(courseName);
        var allCourses = await db.Courses
            .AsNoTracking()
            .OrderBy(course => course.Id)
            .ToListAsync(cancellationToken);
        var exactNameMatches = allCourses
            .Where(course => NormalizeCourseName(course.Name) == normalizedCourseName)
            .ToList();
        if (exactNameMatches.Count > 1)
        {
            throw new DocxImportValidationException(
                $"Знайдено кілька курсів із назвою \"{courseName}\". Уточніть назву курсу в документі або файлі");
        }
        if (exactNameMatches.Count == 1)
        {
            return exactNameMatches[0];
        }

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
            throw new DocxImportValidationException($"Не знайдено курс \"{courseName}\"");
        }
        if (codeMatches.Count > 1)
        {
            throw new DocxImportValidationException(
                $"Знайдено кілька курсів із кодом \"{courseName}\". Уточніть назву курсу в документі або файлі");
        }
        return codeMatches[0];
    }

    // Парсить таблицю модулів у список DTO.
    private static List<DocxImportModuleDto> ParseModules(Table table)
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
                throw new DocxImportValidationException(
                    $"Імпорт скасовано: код модуля \"{DescribeValue(code)}\" повторюється у таблиці модулів.");
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
    private static async Task ApplyAsync(
        AppDbContext db,
        Course course,
        List<DocxImportModuleDto> modules,
        DocxImportResultDto result,
        IReadOnlyDictionary<int, LessonTypeMergeWorkload> transactionallyValidatedMergeWorkloads,
        CancellationToken ct)
    {
        ValidateParsedCurriculum(modules);
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
        var createdCanonicalTargets = new List<LessonTypeRef>();
        foreach (var canonicalName in canonicalLessonTypeNames.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var canonicalKey = canonicalName.ToUpperInvariant();
            if (lessonTypeLookup.ContainsKey(canonicalKey))
            {
                continue;
            }
            var canonicalTarget = CreateImportedLessonType(canonicalName);
            db.LessonTypes.Add(canonicalTarget);
            lessonTypeLookup[canonicalKey] = canonicalTarget;
            createdCanonicalTargets.Add(canonicalTarget);
        }
        if (createdCanonicalTargets.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
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
                if (!transactionallyValidatedMergeWorkloads.TryGetValue(
                        sourceType.Id,
                        out var validatedWorkload))
                {
                    throw new LessonTypeMergeException(
                        "Об'єднання типів занять не пройшло транзакційну попередню перевірку.");
                }
                await LessonTypeMergeService.MergeValidatedAsync(
                    db,
                    sourceType.Id,
                    targetType.Id,
                    validatedWorkload,
                    ct);
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
            var temporaryOrderAllocator = ModuleTopicOrdering.CreateTemporaryOrderAllocator(
                existingTopics.Select(topic => topic.Order),
                existingTopics.Count + module.Topics.Count);
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
                    lessonType = CreateImportedLessonType(effectiveLessonTypeName);
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
                        Order = temporaryOrderAllocator.Take()
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
                ModuleTopicOrdering.AssignCollisionFreeTemporaryOrders(ordered);
                await db.SaveChangesAsync(ct);
            }
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].Order = i + 1;
            }
            await db.SaveChangesAsync(ct);
        }
    }

    // Відхиляє неоднозначні або надмірні навчальні дані до preview/apply та до першої зміни БД.
    private static void ValidateParsedCurriculum(IReadOnlyList<DocxImportModuleDto> modules)
    {
        var topicCount = modules.Sum(module => (long)module.Topics.Count);
        if (modules.Count > CurriculumInputLimits.ImportModuleCountMax
            || topicCount > CurriculumInputLimits.ImportTopicCountMax)
        {
            throw new DocxImportValidationException(
                $"DOCX містить надто багато навчальних сутностей: дозволено до {CurriculumInputLimits.ImportModuleCountMax} модулів і {CurriculumInputLimits.ImportTopicCountMax} тем.");
        }

        var importedLessonTypeCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            var moduleCode = NormalizeText(module.Code);
            var moduleTitle = NormalizeText(module.Title);
            if (string.IsNullOrWhiteSpace(moduleCode))
            {
                throw new DocxImportValidationException("Імпорт скасовано: код модуля є обов'язковим.");
            }
            if (moduleCode.Length > CurriculumInputLimits.CodeMaxLength)
            {
                throw new DocxImportValidationException(
                    $"Імпорт скасовано: код модуля \"{DescribeValue(moduleCode)}\" перевищує {CurriculumInputLimits.CodeMaxLength} символи.");
            }
            if (string.IsNullOrWhiteSpace(moduleTitle))
            {
                throw new DocxImportValidationException(
                    $"Імпорт скасовано: назва модуля \"{DescribeValue(moduleCode)}\" є обов'язковою.");
            }
            if (moduleTitle.Length > CurriculumInputLimits.ModuleTitleMaxLength)
            {
                throw new DocxImportValidationException(
                    $"Імпорт скасовано: назва модуля \"{DescribeValue(moduleCode)}\" перевищує {CurriculumInputLimits.ModuleTitleMaxLength} символів.");
            }
            if (module.Credits is < 0 or > CurriculumInputLimits.ModuleCreditsMax)
            {
                throw new DocxImportValidationException(
                    $"Імпорт скасовано: кредити модуля \"{DescribeValue(moduleCode)}\" мають бути в діапазоні від 0 до {CurriculumInputLimits.ModuleCreditsMax}.");
            }
            if (!CurriculumInputLimits.HasSupportedModuleCreditScale(module.Credits))
            {
                throw new DocxImportValidationException(
                    $"Імпорт скасовано: кредити модуля \"{DescribeValue(moduleCode)}\" можуть містити не більше {CurriculumInputLimits.ModuleCreditsScale} знаків після коми.");
            }
            var targetHours = Math.Round(module.Credits * 30m);
            if (targetHours > CurriculumInputLimits.PlanHoursMax)
            {
                throw new DocxImportValidationException(
                    $"Імпорт скасовано: кредити модуля \"{DescribeValue(moduleCode)}\" утворюють понад {CurriculumInputLimits.PlanHoursMax} планових годин.");
            }

            var topicCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var topic in module.Topics)
            {
                var topicCode = NormalizeText(topic.TopicCode);
                var lessonTypeName = NormalizeText(topic.LessonTypeName);
                if (!string.Equals(topic.ModuleCode, module.Code, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DocxImportValidationException(
                        $"Імпорт скасовано: тема \"{DescribeValue(topicCode)}\" прив'язана до іншого модуля.");
                }
                if (string.IsNullOrWhiteSpace(topicCode))
                {
                    throw new DocxImportValidationException(
                        $"Імпорт скасовано: код теми модуля \"{DescribeValue(moduleCode)}\" є обов'язковим.");
                }
                if (topicCode.Length > CurriculumInputLimits.CodeMaxLength)
                {
                    throw new DocxImportValidationException(
                        $"Імпорт скасовано: код теми \"{DescribeValue(topicCode)}\" перевищує {CurriculumInputLimits.CodeMaxLength} символи.");
                }
                if (!topicCodes.Add(topicCode))
                {
                    throw new DocxImportValidationException(
                        $"Імпорт скасовано: код теми \"{DescribeValue(topicCode)}\" повторюється у модулі \"{DescribeValue(moduleCode)}\".");
                }
                if (string.IsNullOrWhiteSpace(lessonTypeName))
                {
                    throw new DocxImportValidationException(
                        $"Імпорт скасовано: тип заняття для теми \"{DescribeValue(topicCode)}\" є обов'язковим.");
                }
                if (lessonTypeName.Length > CurriculumInputLimits.LessonTypeNameMaxLength)
                {
                    throw new DocxImportValidationException(
                        $"Імпорт скасовано: назва типу заняття для теми \"{DescribeValue(topicCode)}\" перевищує {CurriculumInputLimits.LessonTypeNameMaxLength} символів.");
                }
                var lessonTypeCode = CreateLessonTypeCode(lessonTypeName);
                if (lessonTypeCode.Length > CurriculumInputLimits.CodeMaxLength)
                {
                    throw new DocxImportValidationException(
                        $"Імпорт скасовано: код, утворений із типу заняття \"{DescribeValue(lessonTypeName)}\", перевищує {CurriculumInputLimits.CodeMaxLength} символи.");
                }
                var lessonTypeNameKey = lessonTypeName.ToUpperInvariant();
                if (importedLessonTypeCodes.TryGetValue(lessonTypeCode, out var existingNameKey)
                    && !string.Equals(existingNameKey, lessonTypeNameKey, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DocxImportValidationException(
                        $"Імпорт скасовано: різні назви типів занять утворюють однаковий код \"{DescribeValue(lessonTypeCode)}\".");
                }
                importedLessonTypeCodes[lessonTypeCode] = lessonTypeNameKey;

                var hasOutOfRangeHours = topic.TotalHours < 0
                                         || topic.AuditoriumHours < 0
                                         || topic.SelfStudyHours < 0
                                         || topic.TotalHours > CurriculumInputLimits.TopicHoursMax
                                         || topic.AuditoriumHours > CurriculumInputLimits.TopicHoursMax
                                         || topic.SelfStudyHours > CurriculumInputLimits.TopicHoursMax;
                var assignedHours = (long)topic.AuditoriumHours + topic.SelfStudyHours;
                if (hasOutOfRangeHours || assignedHours > topic.TotalHours)
                {
                    throw new DocxImportValidationException(
                        $"Імпорт скасовано: тема \"{DescribeValue(topicCode)}\" модуля \"{DescribeValue(moduleCode)}\" має некоректний розподіл годин у межах 0–{CurriculumInputLimits.TopicHoursMax} (усього {topic.TotalHours}, аудиторних {topic.AuditoriumHours}, самостійних {topic.SelfStudyHours}).");
                }
            }
        }
    }

    // Перевіряє колізії з чинним довідником однаково для preview та apply.
    private static async Task<LessonTypeValidationSummary> ValidateLessonTypeCodesAsync(
        AppDbContext db,
        IReadOnlyList<DocxImportModuleDto> modules,
        CancellationToken ct)
    {
        var lessonTypes = await db.LessonTypes.AsNoTracking().ToListAsync(ct);
        var byNameGroups = lessonTypes
            .GroupBy(type => NormalizeText(type.Name).ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var duplicateName = byNameGroups.FirstOrDefault(group => group.Count() > 1);
        if (duplicateName is not null)
        {
            throw new DocxImportValidationException(
                $"Імпорт скасовано: у довіднику існує кілька типів занять із назвою \"{DescribeValue(duplicateName.Key)}\".");
        }

        var lessonTypesByName = byNameGroups.ToDictionary(
            group => group.Key,
            group => group.Single(),
            StringComparer.OrdinalIgnoreCase);
        var lessonTypesByCode = lessonTypes
            .GroupBy(type => NormalizeText(type.Code), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var canonicalNames = BuildCanonicalLessonTypeNames(modules, lessonTypesByName);
        var mergeCandidateCount = canonicalNames.Count(alias =>
            lessonTypesByName.TryGetValue(alias.Key, out var sourceType)
            && (!lessonTypesByName.TryGetValue(alias.Value.ToUpperInvariant(), out var targetType)
                || sourceType.Id != targetType.Id));
        if (mergeCandidateCount > MaxCanonicalLessonTypeMergeCount)
        {
            throw new DocxImportValidationException(
                $"Імпорт скасовано: документ потребує {mergeCandidateCount} об'єднань типів занять, що перевищує безпечний ліміт {MaxCanonicalLessonTypeMergeCount}.");
        }

        var estimatedRequestOperations = 0L;
        var estimatedHistoricalJsonTraversalCharacters = 0L;
        var validatedMergeWorkloads = new Dictionary<int, LessonTypeMergeWorkload>();
        foreach (var alias in canonicalNames)
        {
            if (!lessonTypesByName.TryGetValue(alias.Key, out var sourceType))
            {
                continue;
            }
            try
            {
                LessonTypeMergeWorkload workload;
                if (lessonTypesByName.TryGetValue(alias.Value.ToUpperInvariant(), out var targetType))
                {
                    if (sourceType.Id == targetType.Id)
                    {
                        continue;
                    }
                    workload = await LessonTypeMergeService.ValidateMergeWorkloadAsync(
                        db,
                        sourceType.Id,
                        targetType.Id,
                        ct);
                }
                else
                {
                    workload = await LessonTypeMergeService.ValidateMergeToNewTargetWorkloadAsync(
                        db,
                        sourceType.Id,
                        CreateImportedLessonType(alias.Value),
                        ct);
                }
                // Apply виконує advisory preflight, повтор у serializable-транзакції та один
                // bounded rewrite; preview використовує ту саму консервативну оцінку.
                estimatedRequestOperations += workload.EstimatedDatabaseOperations
                                              * LessonTypeMergeWorkloadPassCount;
                var estimatedTraversalCharacters = Math.Max(
                    workload.HistoricalJsonCharacters,
                    workload.EstimatedRewrittenJsonCharacters);
                estimatedHistoricalJsonTraversalCharacters = checked(
                    estimatedHistoricalJsonTraversalCharacters
                    + estimatedTraversalCharacters * LessonTypeMergeWorkloadPassCount);
                if (estimatedHistoricalJsonTraversalCharacters
                    > MaxLessonTypeMergeTraversalCharacters)
                {
                    throw new DocxImportValidationException(
                        $"Імпорт потребує обробки орієнтовно {estimatedHistoricalJsonTraversalCharacters} символів історичних JSON автогенерації, що перевищує безпечний ліміт {MaxLessonTypeMergeTraversalCharacters}.");
                }
                validatedMergeWorkloads[sourceType.Id] = workload;
            }
            catch (LessonTypeMergeException exception)
            {
                throw new DocxImportValidationException(
                    $"Імпорт скасовано: тип заняття \"{DescribeValue(sourceType.Name)}\" схожий на випадковий дубль \"{DescribeValue(alias.Value)}\", але безпечне об'єднання неможливе. {exception.Message}");
            }
            if (estimatedRequestOperations > MaxEstimatedDatabaseOperationCount)
            {
                return new LessonTypeValidationSummary(
                    estimatedRequestOperations,
                    validatedMergeWorkloads);
            }
        }
        var pendingCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var importedName in modules
                     .SelectMany(module => module.Topics)
                     .Select(topic => NormalizeText(topic.LessonTypeName))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var importedKey = importedName.ToUpperInvariant();
            var effectiveName = canonicalNames.GetValueOrDefault(importedKey, importedName);
            var effectiveKey = effectiveName.ToUpperInvariant();
            if (lessonTypesByName.ContainsKey(effectiveKey))
            {
                continue;
            }

            var generatedCode = CreateLessonTypeCode(effectiveName);
            if (lessonTypesByCode.TryGetValue(generatedCode, out var existingType)
                && !string.Equals(
                    NormalizeText(existingType.Name),
                    effectiveName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new DocxImportValidationException(
                    $"Імпорт скасовано: код типу заняття \"{DescribeValue(generatedCode)}\" уже використовується для іншої назви.");
            }
            if (pendingCodes.TryGetValue(generatedCode, out var pendingName)
                && !string.Equals(pendingName, effectiveName, StringComparison.OrdinalIgnoreCase))
            {
                throw new DocxImportValidationException(
                    $"Імпорт скасовано: різні назви типів занять утворюють однаковий код \"{DescribeValue(generatedCode)}\".");
            }
            pendingCodes[generatedCode] = effectiveName;
        }
        return new LessonTypeValidationSummary(
            estimatedRequestOperations,
            validatedMergeWorkloads);
    }

    // Preview оцінює той самий бюджет, а apply повторює його вже під serializable-транзакцією.
    private static async Task<string?> GetOperationBudgetErrorAsync(
        AppDbContext db,
        int moduleCount,
        int topicCount,
        long lessonTypeMergeOperationCount,
        CancellationToken ct)
    {
        var roomCount = await db.Rooms.AsNoTracking().CountAsync(ct);
        var buildingCount = await db.Buildings.AsNoTracking().CountAsync(ct);
        if (moduleCount > 0
            && (roomCount > CurriculumInputLimits.ModuleAssociationCountMax
                || buildingCount > CurriculumInputLimits.ModuleAssociationCountMax))
        {
            return $"Імпорт не може безпечно призначити модулю всі доступні аудиторії та корпуси: дозволено не більше {CurriculumInputLimits.ModuleAssociationCountMax} зв'язків кожного типу";
        }
        var estimatedDatabaseOperations = checked(
            (long)moduleCount * (8L + roomCount + buildingCount)
            + (long)topicCount * 3L
            + lessonTypeMergeOperationCount);
        return estimatedDatabaseOperations > MaxEstimatedDatabaseOperationCount
            ? $"Імпорт потребує орієнтовно {estimatedDatabaseOperations} операцій із базою даних, що перевищує безпечний ліміт {MaxEstimatedDatabaseOperationCount}"
            : null;
    }

    private static string CreateLessonTypeCode(string name)
        => NormalizeText(name).ToUpperInvariant().Replace(' ', '_');

    private static LessonTypeRef CreateImportedLessonType(string name)
    {
        var normalizedName = NormalizeText(name);
        return new LessonTypeRef
        {
            Code = CreateLessonTypeCode(normalizedName),
            Name = normalizedName,
            IsActive = true,
            RequiresRoom = true,
            RequiresTeacher = true,
            BlocksRoom = true,
            BlocksTeacher = true,
            CountInPlan = true,
            CountInLoad = true,
            PreferredFirstInWeek = false
        };
    }

    private static string DescribeValue(string value)
        => value.Length <= 80 ? value : value[..77] + "...";

    private static DocxImportResultDto CreateValidationErrorResult(
        string? courseName,
        List<DocxImportModuleDto> modules,
        List<string> warnings,
        string error,
        int? courseId = null)
        => new(
            courseName ?? string.Empty,
            courseId,
            courseId is not null,
            modules,
            warnings,
            error);

    private sealed class DocxImportConflictException(string message) : InvalidOperationException(message);
    private sealed class DocxImportValidationException(string message) : InvalidOperationException(message);

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
            var values = cells.Select(cell => cell.Trim().Trim('.')).ToArray();
            if (values.Any(value => !NumericHeaderRegex.IsMatch(value))) return false;
            for (var i = 0; i < 6; i++)
            {
                if (!int.TryParse(values[i], NumberStyles.None, CultureInfo.InvariantCulture, out var headerNumber))
                {
                    throw new DocxImportValidationException(
                        "Імпорт скасовано: номер колонки тематичної таблиці виходить за підтримуваний діапазон.");
                }
                if (headerNumber != i + 1) return false;
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
        foreach (var sourceKey in result.Keys.ToList())
        {
            var finalName = result[sourceKey];
            var visitedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourceKey };
            while (visitedKeys.Add(finalName.ToUpperInvariant())
                   && result.TryGetValue(finalName.ToUpperInvariant(), out var nextName))
            {
                finalName = nextName;
            }
            result[sourceKey] = finalName;
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
        if (IsEmptyNumericValue(raw)) return 0m;
        var normalized = raw!.Replace(',', '.');
        if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            throw new DocxImportValidationException(
                "Імпорт скасовано: значення кредитів має містити коректне число.");
        }
        if (value is < 0 or > CurriculumInputLimits.ModuleCreditsMax)
        {
            throw new DocxImportValidationException(
                $"Імпорт скасовано: значення кредитів має бути в діапазоні від 0 до {CurriculumInputLimits.ModuleCreditsMax}.");
        }
        if (!CurriculumInputLimits.HasSupportedModuleCreditScale(value))
        {
            throw new DocxImportValidationException(
                $"Імпорт скасовано: значення кредитів може містити не більше {CurriculumInputLimits.ModuleCreditsScale} знаків після коми.");
        }
        return value;
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
        if (IsEmptyNumericValue(raw)) return 0;
        var normalized = raw!.Replace(',', '.');
        if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new DocxImportValidationException(
                "Імпорт скасовано: значення годин має містити коректне число.");
        }
        if (parsed is < 0 or > CurriculumInputLimits.TopicHoursMax)
        {
            throw new DocxImportValidationException(
                $"Імпорт скасовано: значення годин має бути в діапазоні від 0 до {CurriculumInputLimits.TopicHoursMax}.");
        }
        var rounded = Math.Round(parsed, MidpointRounding.AwayFromZero);
        return decimal.ToInt32(rounded);
    }

    private static bool IsEmptyNumericValue(string? raw)
    {
        var value = raw?.Trim();
        return string.IsNullOrWhiteSpace(value)
               || value is "-" or "–" or "—";
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

    private sealed record LessonTypeValidationSummary(
        long EstimatedRequestOperations,
        IReadOnlyDictionary<int, LessonTypeMergeWorkload> ValidatedMergeWorkloads);
}
