using System.Globalization;
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
    private static readonly Regex CourseCodeRegex = new(@"[A-Za-zА-Яа-яІіЇїЄєҐґ]{1,6}-\d+", RegexOptions.Compiled);
    private static readonly Regex ModulePrefixRegex = new(@"\b\d+\.\d+\.\d+\b", RegexOptions.Compiled);
    private static readonly Regex TopicCodeRegex = new(@"\d+(?:\.\d+){2,}", RegexOptions.Compiled);
    private static readonly Regex LetterTopicCodeRegex = new(@"[A-Za-zА-Яа-яІіЇїЄєҐґ]+\.?((\d+\.?)+)", RegexOptions.Compiled);
    private static readonly Regex LetteredModulePrefixRegex = new(@"[A-Za-zА-Яа-яІіЇїЄєҐґ]+\.?((\d+\.?)+)", RegexOptions.Compiled);
    // Зчитує DOCX і формує результат імпорту з опційним застосуванням у БД.
    public async Task<DocxImportResultDto> ImportAsync(IFormFile file, AppDbContext db, bool apply, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return new DocxImportResultDto(string.Empty, null, false, new(), new() { "Файл порожній або не надісланий" }, "Файл порожній або не надісланий");
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        using var doc = WordprocessingDocument.Open(buffer, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
            return new DocxImportResultDto(string.Empty, null, false, new(), new() { "Не вдалося прочитати тіло документа" }, "Не вдалося прочитати тіло документа");
        var warnings = new List<string>();
        var allTexts = body.Descendants<Text>().Select(t => t.Text ?? string.Empty).ToList();
        var courseName = ResolveCourseName(file.FileName, allTexts);
        var tables = body.Descendants<Table>().ToList();
        var modulesTable = tables.FirstOrDefault(LooksLikeModuleTable);
        var parsedModules = modulesTable is not null
            ? ParseModules(modulesTable, warnings)
            : new List<DocxImportModuleDto>();
        var topicTables = tables.Where(LooksLikeTopicTable).ToList();
        var moduleOrder = parsedModules.Select(m => m.Code).ToList();
        var knownModuleCodes = new HashSet<string>(moduleOrder, StringComparer.OrdinalIgnoreCase);
        var topicsByModule = ParseTopics(topicTables, moduleOrder, knownModuleCodes, warnings);
        foreach (var (moduleCode, topics) in topicsByModule)
        {
            var target = parsedModules.FirstOrDefault(m => string.Equals(m.Code, moduleCode, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                warnings.Add($"Для модуля з кодом \"{moduleCode}\" знайдено теми, але такого модуля немає у таблиці модулів");
                continue;
            }
            target.Topics.Clear();
            target.Topics.AddRange(topics);
        }
        if (string.IsNullOrWhiteSpace(courseName))
        {
            return new DocxImportResultDto(string.Empty, null, false, parsedModules, warnings, "Не вдалося визначити назву курсу");
        }
        var upperCourse = NormalizeCourseName(courseName);
        var allCourses = await db.Courses.AsNoTracking().ToListAsync(ct);
        var course = allCourses.FirstOrDefault(c => NormalizeCourseName(c.Name) == upperCourse)
                     ?? allCourses.FirstOrDefault(c => NormalizeCourseName(c.Name).Contains(upperCourse));
        if (course is null)
        {
            var codeMatch = CourseCodeRegex.Match(courseName);
            if (codeMatch.Success)
            {
                var codeUpper = NormalizeCourseName(codeMatch.Value);
                course = allCourses.FirstOrDefault(c => NormalizeCourseName(c.Name).Contains(codeUpper));
            }
        }
        if (course is null)
        {
            return new DocxImportResultDto(courseName, null, false, parsedModules, warnings, $"Не знайдено курс \"{courseName}\"");
        }
        var result = new DocxImportResultDto(courseName, course.Id, true, parsedModules, warnings, null);
        if (!apply)
        {
            return result;
        }
        await ApplyAsync(db, course, parsedModules, result, ct);
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
        foreach (var row in rows)
        {
            var cells = GetRowCells(row);
            if (cells.Count < 3) continue;
            var rawCode = cells[0];
            if (string.IsNullOrWhiteSpace(rawCode)) continue;
            if (!Regex.IsMatch(rawCode, @"^\d+(?:[\\.,]\d+)?$")) continue; // пропускаємо службові рядки типу Усього
            var code = NormalizeCode(rawCode.Replace(',', '.'));
            var title = NormalizeText(cells.ElementAtOrDefault(1) ?? string.Empty);
            var credits = ParseDecimal(cells.ElementAtOrDefault(2));
            if (modules.Any(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase)))
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
                var letterHead = Regex.Match(modulePrefix, @"^[A-Za-zА-Яа-яІіЇїЄєҐґ]+").Value.ToUpperInvariant();
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
                foreach (var code in knownModuleCodes.OrderByDescending(c => c.Length))
                {
                    var pattern = $@"(?<![A-Za-zА-Яа-яІіЇїЄєҐґ0-9]){Regex.Escape(code)}(?=[\.\s]|$)";
                    if (Regex.IsMatch(moduleHeader, pattern, RegexOptions.IgnoreCase))
                    {
                        moduleCode = code;
                        modulePrefix ??= code;
                        break;
                    }
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
                modulePrefix ??= moduleCode;
            }
            if (modulePrefix is not null &&
                moduleCode is not null &&
                modulePrefix.Contains('.') &&
                !knownModuleCodes.Contains(modulePrefix))
            {
                var isSameTree = moduleCodeFromLetter
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
                    var headerPrefix = Regex.Match(cells[0], @"\d+(?:\.\d+)+");
                    if (headerPrefix.Success)
                    {
                        modulePrefix = headerPrefix.Value.Trim().Trim('.');
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
                    Regex.IsMatch(lessonTypeName, @"^\d+\.$"))
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
        var moduleCodes = modules.Select(m => m.Code).ToList();
        var existingModules = await db.Modules
            .Include(m => m.ModuleCourses)
            .Where(m => moduleCodes.Contains(m.Code))
            .ToDictionaryAsync(m => m.Code, StringComparer.OrdinalIgnoreCase, ct);
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
            var existingByCode = existingTopics.ToDictionary(t => t.TopicCode, StringComparer.OrdinalIgnoreCase);
            var parsedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parsedEntities = new List<(ModuleTopic topic, int desiredOrder)>();
            foreach (var topic in module.Topics.OrderBy(t => t.Order))
            {
                parsedCodes.Add(topic.TopicCode);
                if (!lessonTypeLookup.TryGetValue(NormalizeText(topic.LessonTypeName).ToUpperInvariant(), out var lessonType))
                {
                    // якщо типу немає — створюємо тимчасовий активний тип, що рахується в плані.
                    lessonType = new LessonTypeRef
                    {
                        Code = NormalizeText(topic.LessonTypeName).ToUpperInvariant().Replace(" ", "_"),
                        Name = topic.LessonTypeName,
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
                    lessonTypeLookup[NormalizeText(topic.LessonTypeName).ToUpperInvariant()] = lessonType;
                }
                ModuleTopic entityTopic;
                if (existingByCode.TryGetValue(topic.TopicCode, out var existingTopic))
                {
                    entityTopic = existingTopic;
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
            var order = 1;
            foreach (var t in ordered)
            {
                t.Order = order++;
            }
            await db.SaveChangesAsync(ct);
        }
    }
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
                if (!Regex.IsMatch(value, @"^\d+$")) return false;
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
        var normalized = input
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Replace('\u00A0', ' '); // прибираємо нерозривні пробіли
        while (normalized.Contains("  "))
        {
            normalized = normalized.Replace("  ", " ");
        }
        return normalized.Trim();
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
                if (Regex.IsMatch(cleaned, @"^\d+(?:\.\d+)+$"))
                {
                    return cleaned;
                }
                if (Regex.IsMatch(cleaned, @"^[A-Za-zА-Яа-яІіЇїЄєҐґ]+\.?\d+(?:\.\d+)*$"))
                {
                    return cleaned;
                }
            }
        }
        var prefix = string.IsNullOrWhiteSpace(modulePrefix) ? moduleCode : modulePrefix;
        return $"{prefix}.{order}";
    }
}
