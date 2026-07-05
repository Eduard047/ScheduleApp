using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

// Сервіс автоматичної генерації чернеток розкладу.
public sealed class TeacherDraftsAutogenService
{
    private static readonly SemaphoreSlim GenerationLock = new(1, 1);

    // Контекст БД для читання довідників та запису чернеток.
    private readonly AppDbContext _db;
    public TeacherDraftsAutogenService(AppDbContext db)
    {
        // Зберігаємо залежність для подальших запитів.
        _db = db;
    }
    private sealed record BusySlot(
        int GroupId, // Група, для якої слот зайнятий.
        int? TeacherId, // Викладач, якщо призначено.
        int? RoomId, // Аудиторія, якщо призначено.
        DateOnly Date, // Дата заняття.
        TimeOnly StartTime, // Час початку.
        TimeOnly EndTime, // Час завершення.
        int? BuildingId, // Будівля аудиторії (для штрафів за переходи).
        int ModuleId, // Модуль, до якого належить слот.
        int LessonTypeId, // Тип заняття (для обмежень).
        int? ModuleTopicId, // Тема модуля для точного зіставлення спільних потоків.
        bool JoinableDraft // Чи можна приєднати до цього слоту нові групи як до чернеткового потоку.
    );
    private sealed record PlacementCandidate(
        TimeSlot Slot, // Слот розкладу для можливого розміщення.
        int TeacherId, // Обраний викладач.
        Room? Room, // Обрана аудиторія (може бути null).
        int LessonTypeId, // Обраний тип заняття.
        ModuleTopic? Topic, // Обрана тема модуля (може бути null).
        bool IsSelfStudy, // Ознака самостійної роботи.
        IReadOnlyList<int> SharedGroupIds, // Групи, для яких формуємо спільне заняття.
        int TotalSharedGroupCount, // Повний розмір спільного потоку разом із вже наявними групами.
        bool StartsNewDistinctModule,
        bool ExpandsContiguousBlock,
        double Penalty, // Сумарний штраф за правилами.
        List<string> Notes); // Пояснення нарахованих штрафів.
    private sealed record IncompletePlacementCandidate(
        TimeSlot Slot,
        int? TeacherId,
        Room? Room,
        int LessonTypeId,
        ModuleTopic? Topic,
        bool IsSelfStudy,
        double Penalty,
        bool MissingTeacher,
        bool MissingRoom,
        List<string> Notes);
    private sealed record SequenceItem(int CourseId, int ModuleId, int GroupOrder, int Order);
    private sealed record MainModuleGroup(int GroupOrder, List<int> ModuleIds);
    // Уніфіковані відповіді для API.
    private static ActionResult<AutoGenResult> Ok(AutoGenResult value) => new OkObjectResult(value);
    private static ActionResult<AutoGenResult> BadRequest(object value) => new BadRequestObjectResult(value);
    // Перетворює shared-DTO м'яких параметрів на серверний record для єдиної логіки автогену.
    private static DraftAutoGenSoftOptions? MapSoftOptions(AutoGenSoftOptionsDto? dto)
        => dto is null
            ? null
            : new DraftAutoGenSoftOptions(
                MaxParallelGroupsPerModuleInSlot: dto.MaxParallelGroupsPerModuleInSlot,
                RecentRepeatWindowDays: dto.RecentRepeatWindowDays,
                PreferredMaxDistinctModulesPerDay: dto.PreferredMaxDistinctModulesPerDay,
                MaxDistinctModulesPerDay: dto.MaxDistinctModulesPerDay,
                PreferredFirstPenaltyMultiplier: dto.PreferredFirstPenaltyMultiplier,
                AdjacentRoomChangePenalty: dto.AdjacentRoomChangePenalty,
                TeacherLoadPenaltyWeight: dto.TeacherLoadPenaltyWeight,
                BuildingDistancePenaltyWeight: dto.BuildingDistancePenaltyWeight);
    private static string CompactGapReasonExample(AutoGenGapDetail gap)
    {
        var reason = string.IsNullOrWhiteSpace(gap.Reason) ? "Причину не визначено." : gap.Reason.Trim();
        if (reason.Length > 220)
        {
            reason = reason[..220] + "...";
        }
        return $"{gap.Date:yyyy-MM-dd} {gap.SlotLabel}, {gap.GroupName}: {reason}";
    }
    private static (string Code, string Title) ClassifyGapReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return ("unknown", "Причину не визначено");
        }
        var text = reason.ToLowerInvariant();
        if (text.Contains("немає доступних викладач", StringComparison.Ordinal)
            || text.Contains("викладач", StringComparison.Ordinal) && (text.Contains("кафедр", StringComparison.Ordinal) || text.Contains("зайнят", StringComparison.Ordinal)))
        {
            return ("teacher", "Немає доступного викладача");
        }
        if (text.Contains("усі аудитор", StringComparison.Ordinal)
            || text.Contains("аудитор", StringComparison.Ordinal) && (text.Contains("зайнят", StringComparison.Ordinal) || text.Contains("не належ", StringComparison.Ordinal)))
        {
            return ("room", "Немає доступної аудиторії");
        }
        if (text.Contains("переход", StringComparison.Ordinal)
            || text.Contains("корпус", StringComparison.Ordinal) && text.Contains("хв", StringComparison.Ordinal))
        {
            return ("travel", "Недостатньо часу на перехід");
        }
        if (text.Contains("хронолог", StringComparison.Ordinal)
            || text.Contains("порядок тем", StringComparison.Ordinal)
            || text.Contains("відкладено", StringComparison.Ordinal) && text.Contains("порядком тем", StringComparison.Ordinal))
        {
            return ("topic-order", "Порядок тем не дозволив слот");
        }
        if (text.Contains("суцільним блоком", StringComparison.Ordinal))
        {
            return ("module-block", "Модуль має йти суцільним блоком");
        }
        if (text.Contains("більше двох", StringComparison.Ordinal)
            || text.Contains("ліміт", StringComparison.Ordinal)
            || text.Contains("обмеж", StringComparison.Ordinal))
        {
            return ("limit", "Спрацювали денні або слотні ліміти");
        }
        if (text.Contains("спільн", StringComparison.Ordinal))
        {
            return ("shared-flow", "Спільний потік не готовий");
        }
        return ("other", "Інші причини");
    }
    private static List<AutoGenGapSummaryItem> BuildAutoGenGapSummary(IEnumerable<AutoGenGapDetail> gaps)
        => gaps
            .GroupBy(gap => ClassifyGapReason(gap.Reason))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.Title, StringComparer.Ordinal)
            .Select(group => new AutoGenGapSummaryItem(
                group.Key.Code,
                group.Key.Title,
                group.Count(),
                group.Take(5).Select(CompactGapReasonExample).ToList()))
            .ToList();

    private static List<AutoGenPreflightItem> MergeAutoGenPreflight(IEnumerable<AutoGenPreflightItem> items)
        => items
            .GroupBy(item => item.Code, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return new AutoGenPreflightItem(
                    first.Code,
                    first.Title,
                    group.Sum(item => item.Count),
                    first.Recommendation,
                    group.SelectMany(item => item.Examples)
                        .Where(example => !string.IsNullOrWhiteSpace(example))
                        .Distinct(StringComparer.Ordinal)
                        .Take(5)
                        .ToList());
            })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Title, StringComparer.Ordinal)
            .ToList();

    private static List<string> BuildAutoGenRepairSuggestions(IEnumerable<AutoGenGapDetail> gaps)
    {
        var gapList = gaps.ToList();
        if (gapList.Count == 0)
        {
            return new();
        }

        var gapsByCode = gapList
            .GroupBy(gap => ClassifyGapReason(gap.Reason).Code)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var suggestions = new List<string>();

        void AddSuggestion(string code, Func<IReadOnlyList<AutoGenGapDetail>, string> build)
        {
            if (gapsByCode.TryGetValue(code, out var codeGaps) && codeGaps.Count > 0)
            {
                suggestions.Add(build(codeGaps));
            }
        }

        AddSuggestion("teacher", codeGaps =>
            $"Рекомендація автогенерації: {codeGaps.Count} незаповнених слотів упираються у викладачів. Додайте або звільніть викладачів для відповідних модулів, перевірте прив'язку тем до кафедр і робочі години. Приклади: {FormatGapSuggestionExamples(codeGaps)}.");
        AddSuggestion("room", codeGaps =>
            $"Рекомендація автогенерації: {codeGaps.Count} незаповнених слотів упираються в аудиторії. Додайте аудиторії потрібної місткості, розширте дозволені аудиторії або корпуси для груп і звільніть зайняті аудиторії в цих слотах. Приклади: {FormatGapSuggestionExamples(codeGaps)}.");
        AddSuggestion("travel", codeGaps =>
            $"Рекомендація автогенерації: {codeGaps.Count} незаповнених слотів упираються у переходи між корпусами. Додайте аудиторії в тому самому корпусі, збільшіть перерву між корпусами або рознесіть заняття по інших слотах. Приклади: {FormatGapSuggestionExamples(codeGaps)}.");
        AddSuggestion("topic-order", codeGaps =>
            $"Рекомендація автогенерації: {codeGaps.Count} незаповнених слотів заблоковані порядком тем. Перевірте послідовність тем і години модулів; якщо методично допустимо, додайте ранніші слоти або послабте порядок для проблемних тем. Приклади: {FormatGapSuggestionExamples(codeGaps)}.");
        AddSuggestion("limit", codeGaps =>
            $"Рекомендація автогенерації: {codeGaps.Count} незаповнених слотів упираються у денні або слотні ліміти. Додайте навчальні дні чи часові слоти, зменште обсяг на діапазон або розширте ліміти для дозаповнення. Приклади: {FormatGapSuggestionExamples(codeGaps)}.");
        AddSuggestion("shared-flow", codeGaps =>
            $"Рекомендація автогенерації: {codeGaps.Count} незаповнених слотів пов'язані зі спільним потоком. Перевірте однаковий модуль, тип заняття, тему, викладача й аудиторію для груп потоку та додайте містку аудиторію. Приклади: {FormatGapSuggestionExamples(codeGaps)}.");
        AddSuggestion("module-block", codeGaps =>
            $"Рекомендація автогенерації: {codeGaps.Count} незаповнених слотів заблоковані правилом суцільного блоку модуля. Залиште поруч кілька слотів для одного модуля або перенесіть зайві заняття цього дня. Приклади: {FormatGapSuggestionExamples(codeGaps)}.");
        AddSuggestion("unknown", codeGaps =>
            $"Рекомендація автогенерації: {codeGaps.Count} незаповнених слотів не мають точної причини. Перевірте приклади вручну та додайте обмежений ресурс, який там повторюється. Приклади: {FormatGapSuggestionExamples(codeGaps)}.");
        AddSuggestion("other", codeGaps =>
            $"Рекомендація автогенерації: {codeGaps.Count} незаповнених слотів мають інші причини. Перегляньте приклади й перевірте сумісність груп, тем, викладачів, аудиторій та календаря. Приклади: {FormatGapSuggestionExamples(codeGaps)}.");

        var moduleHotspots = gapList
            .Where(gap => gap.ModuleId is not null || !string.IsNullOrWhiteSpace(gap.ModuleName))
            .GroupBy(gap => string.IsNullOrWhiteSpace(gap.ModuleName)
                ? $"Модуль #{gap.ModuleId}"
                : gap.ModuleName!.Trim(), StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(group => $"{group.Key}: {group.Count()}")
            .ToList();
        if (moduleHotspots.Count > 0)
        {
            suggestions.Insert(0, $"Рекомендація автогенерації: найчастіше незаповнені слоти пов'язані з модулями {string.Join(", ", moduleHotspots)}. Перевірте для них викладачів, аудиторії, порядок тем і доступні години.");
        }

        return suggestions;
    }

    private static string FormatGapSuggestionExamples(IEnumerable<AutoGenGapDetail> gaps)
    {
        var examples = gaps
            .Select(gap => $"{gap.Date:yyyy-MM-dd} {gap.SlotLabel}, {gap.GroupName}")
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();

        return examples.Count == 0 ? "немає прикладів" : string.Join("; ", examples);
    }

    private static int StableSeed(params int[] values)
    {
        unchecked
        {
            var hash = 17;
            foreach (var value in values)
            {
                hash = hash * 31 + value;
            }
            return hash;
        }
    }
    // Викликає автогенерацію чернеток для одного тижня.
    private static string? BuildIncompleteDraftWarningJson(bool missingTeacher, bool missingRoom)
    {
        var issues = new List<DraftValidationIssueDto>();
        if (missingTeacher)
        {
            issues.Add(new DraftValidationIssueDto(
                Severity: "warning",
                Code: "teacher-required",
                Title: "Потрібен викладач",
                Description: "У чернетці пара збережена без викладача. Перед публікацією потрібно призначити викладача."));
        }
        if (missingRoom)
        {
            issues.Add(new DraftValidationIssueDto(
                Severity: "warning",
                Code: "room-required",
                Title: "Потрібна аудиторія",
                Description: "У чернетці пара збережена без аудиторії. Перед публікацією потрібно призначити аудиторію."));
        }
        if (issues.Count == 0)
        {
            return null;
        }
        return JsonSerializer.Serialize(new DraftValidationReportDto(DateTimeOffset.UtcNow, issues));
    }
    public Task<ActionResult<AutoGenResult>> DraftAutoGenWeek(DraftAutoGenRequest r, CancellationToken cancellationToken = default)
        => DraftAutoGen(r, cancellationToken);
    // Автоматично генерує чернетки для кожного тижня в межах місяця.
    public async Task<ActionResult<AutoGenResult>> AutogenMonth(AutogenMonthRequest r, CancellationToken cancellationToken = default)
    {
        // Розраховуємо межі місяця і формуємо базовий шаблон запиту.
        var monthStart = r.MonthStart;
        var nextMonth = new DateOnly(monthStart.Year, monthStart.Month, 1).AddMonths(1);
        var template = new DraftAutoGenRequest(
            WeekStart: monthStart,
            ClearExisting: true,
            CourseId: r.CourseId,
            GroupId: r.GroupId,
            TeacherId: r.TeacherId,
            AllowOnDaysOff: r.AllowOnDaysOff,
            Days: r.Days,
            AllowIncompleteDrafts: r.AllowIncompleteDrafts,
            PreferredFirstMaxSlotOrderOverride: r.PreferredFirstMaxSlotOrderOverride,
            GroupRoomPreferences: r.GroupRoomPreferences,
            SoftOptions: MapSoftOptions(r.SoftOptions),
            PreflightOnly: r.PreflightOnly,
            RangeStartDate: monthStart,
            RangeEndDate: nextMonth.AddDays(-1)
        );
        // Запускаємо автогенерацію для кожного тижня місяця.
        return await RunAutoGenForWeeks(template, EnumerateWeekStarts(monthStart, week => week < nextMonth), cancellationToken);
    }
    // Генерує чернетки для курсу в заданому діапазоні тижнів.
    public async Task<ActionResult<AutoGenResult>> AutogenCourse(AutogenCourseRequest r, CancellationToken cancellationToken = default)
    {
        // Підготовка шаблону генерації з фільтрами курсу/групи/викладача.
        var template = new DraftAutoGenRequest(
            WeekStart: r.From,
            ClearExisting: true,
            CourseId: r.CourseId,
            GroupId: r.GroupId,
            TeacherId: r.TeacherId,
            AllowOnDaysOff: r.AllowOnDaysOff,
            Days: r.Days,
            AllowIncompleteDrafts: r.AllowIncompleteDrafts,
            PreferredFirstMaxSlotOrderOverride: r.PreferredFirstMaxSlotOrderOverride,
            GroupRoomPreferences: r.GroupRoomPreferences,
            SoftOptions: MapSoftOptions(r.SoftOptions),
            PreflightOnly: r.PreflightOnly,
            RangeStartDate: r.From,
            RangeEndDate: r.To
        );
        // Проганяємо всі тижні в межах діапазону.
        return await RunAutoGenForWeeks(template, EnumerateWeekStarts(r.From, week => week <= r.To), cancellationToken);
    }
    // Генерує стартові дати тижнів у заданому діапазоні.
    private static IEnumerable<DateOnly> EnumerateWeekStarts(DateOnly reference, Func<DateOnly, bool> shouldInclude)
    {
        for (var week = DateHelpers.StartOfWeek(reference); shouldInclude(week); week = week.AddDays(7))
        {
            yield return week;
        }
    }
    // Запускає автогенерацію для набору тижнів та агрегує результат.
    private async Task<ActionResult<AutoGenResult>> RunAutoGenForWeeks(DraftAutoGenRequest template, IEnumerable<DateOnly> weekStarts, CancellationToken cancellationToken = default)
    {
        var weekList = weekStarts.ToList();
        var firstWeekStart = weekList.Count > 0
            ? weekList[0]
            : DateHelpers.StartOfWeek(template.RangeStartDate ?? template.WeekStart);
        var rangeStartDate = template.RangeStartDate ?? firstWeekStart;
        var rangeEndDate = template.RangeEndDate ?? (weekList.Count > 0 ? weekList[^1].AddDays(6) : firstWeekStart.AddDays(6));
        // Запускаємо діапазон одним проходом, щоб автогенерація бачила взаємний вплив усіх тижнів.
        var res = await DraftAutoGen(template with
        {
            WeekStart = firstWeekStart,
            RangeStartDate = rangeStartDate,
            RangeEndDate = rangeEndDate
        }, cancellationToken);
        if (res.Result is OkObjectResult { Value: AutoGenResult ok })
        {
            return Ok(ok);
        }
        if (res.Result is ObjectResult { Value: AutoGenResult failedResult })
        {
            return BadRequest(failedResult);
        }
        if (res.Result is ObjectResult { Value: { } value })
        {
            return BadRequest(new AutoGenResult(0, 0, new() { JsonSerializer.Serialize(value) }));
        }
        return BadRequest(new AutoGenResult(0, 0, new() { "Сервер не повернув результат автогенерації діапазону." }));
    }
    // Створює чернетки на основі правил і доступних даних для заданого тижня.
    public async Task<ActionResult<AutoGenResult>> DraftAutoGen(DraftAutoGenRequest r, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // ЕТАП 1: збираємо типи занять і готуємо довідники правил.
        var types = await _db.LessonTypes.AsNoTracking().ToListAsync();
        if (types.Count == 0)
            return BadRequest(new AutoGenResult(0, 0, new() { "Типи занять відсутні або вимкнені." }));
        // Визначаємо службові типи (перерва/скасування), які не беруть участі у плані.
        var typeBreakId = types.FirstOrDefault(t => t.Code.ToUpper() == "BREAK" && t.IsActive)?.Id;
        var typeCanceledId = types.FirstOrDefault(t => t.Code.ToUpper() == "CANCELED")?.Id;
        var excludedTypeIds = new HashSet<int>(new[] { typeBreakId, typeCanceledId }.Where(x => x != null)!.Select(x => x!.Value));
        // Мапа типів за ідентифікатором для швидких перевірок.
        var typeById = types.ToDictionary(t => t.Id);
        // Евристика для визначення лекційних типів без окремого прапорця у БД.
        bool IsLectureTypeMeta(LessonTypeRef lessonType)
        {
            var code = (lessonType.Code ?? string.Empty).Trim().ToUpperInvariant();
            if (code is "LECTURE" or "LECT" or "LEC")
            {
                return true;
            }
            var name = (lessonType.Name ?? string.Empty).Trim().ToUpperInvariant();
            return name.Contains("LECTURE", StringComparison.Ordinal)
                || name.Contains("ЛЕКЦ", StringComparison.Ordinal)
                || name.Contains("ЛЕКЦІ", StringComparison.Ordinal)
                || name.Contains("ЛЕКЦІЇ", StringComparison.Ordinal);
        }
        var lectureTypeIds = types
            .Where(IsLectureTypeMeta)
            .Select(t => t.Id)
            .ToHashSet();
        // Активні типи, які враховуються у плані (для циклічного підбору).
        var activeStudyTypes = types.Where(t => t.IsActive && t.CountInPlan).OrderBy(t => t.Id).ToList();
        // Тип, який бажано ставити першим у тижні.
        var preferredFirstTypeId = types.FirstOrDefault(t => t.PreferredFirstInWeek)?.Id ?? 0;
        // Індекс для циклічного вибору типів.
        int ltIndex = 0;
        // Циклічний вибір типів занять, якщо немає жорсткої прив’язки до теми.
        int NextCyclicLessonTypeId() =>
            activeStudyTypes.Count > 0 ? activeStudyTypes[ltIndex++ % activeStudyTypes.Count].Id : preferredFirstTypeId;
        // Межі тижня, що планується.
        var weekStart = DateHelpers.StartOfWeek(r.WeekStart);
        var weekEnd = weekStart.AddDays(7);
        var weekEndInclusive = weekEnd.AddDays(-1);
        // Діапазон може охоплювати кілька тижнів, щоб генератор бачив увесь період одним проходом.
        var rangeStartDate = r.RangeStartDate ?? weekStart;
        var rangeEndDate = r.RangeEndDate ?? weekEndInclusive;
        if (rangeEndDate < rangeStartDate)
        {
            return BadRequest(new AutoGenResult(0, 0, new()
            {
                "Невірний діапазон дат автогенерації: дата завершення менша за дату початку."
            }));
        }
        var rangeEndDateExclusive = rangeEndDate.AddDays(1);
        var planningWeekStart = DateHelpers.StartOfWeek(rangeStartDate);
        var planningWeekEndExclusive = DateHelpers.StartOfWeek(rangeEndDate).AddDays(7);
        var rangeWeekCount = Math.Max(1, (planningWeekEndExclusive.DayNumber - planningWeekStart.DayNumber) / 7);
        // Режим "м'якого заповнення" дозволяє послаблювати частину правил.
        var softFill = r.SoftFill;
        var allowIncompleteDrafts = r.AllowIncompleteDrafts;
        var softOptions = r.SoftOptions;
        var hasRequestedModuleHourOverrides = r.ModuleHours?.Any(kv => kv.Value > 0) == true;
        var isShortManualRange = hasRequestedModuleHourOverrides
                                 && rangeEndDateExclusive.DayNumber - rangeStartDate.DayNumber <= 5;
        var usePressureOrderingForRange = isShortManualRange;
        var recentRepeatWindowDays = softOptions?.RecentRepeatWindowDays is int repeatWindow && repeatWindow >= 0
            ? repeatWindow
            : softFill || isShortManualRange ? 0 : 2;
        var maxParallelGroupsPerModuleInSlot = softOptions?.MaxParallelGroupsPerModuleInSlot is int parallelGroups && parallelGroups > 0
            ? parallelGroups
            : softFill ? 3 : 2;
        var preferredFirstPenaltyMultiplier = softOptions?.PreferredFirstPenaltyMultiplier is double preferredFirstMultiplier && preferredFirstMultiplier >= 0
            ? preferredFirstMultiplier
            : 1.0;
        var adjacentRoomChangePenalty = softOptions?.AdjacentRoomChangePenalty;
        var teacherLoadPenaltyWeight = softOptions?.TeacherLoadPenaltyWeight is double teacherLoadWeight && teacherLoadWeight >= 0
            ? teacherLoadWeight
            : softFill ? 0.0 : 0.25;
        var buildingDistancePenaltyWeight = softOptions?.BuildingDistancePenaltyWeight is double buildingDistanceWeight && buildingDistanceWeight >= 0
            ? buildingDistanceWeight
            : softFill ? 0.0 : 1.0;
        // Перевіряємо, чи день тижня дозволений у вибраному пресеті.
        bool DayAllowed(DayOfWeek dow)
        {
            var day = dow == DayOfWeek.Sunday ? 7 : (int)dow;
            return r.Days switch
            {
                WeekPreset.MonSun => day is >= 1 and <= 7,
                WeekPreset.MonSat => day is >= 1 and <= 6,
                _ => day is >= 1 and <= 5
            };
        }
        // Витягуємо календарні винятки для всього діапазону (робочі/вихідні).
        var calendar = await _db.CalendarExceptions
            .Where(c => c.Date >= rangeStartDate && c.Date < rangeEndDateExclusive)
            .ToListAsync();
        // Перевіряємо, чи дата робоча для конкретної групи з урахуванням винятків.
        bool IsWorking(DateOnly d, Group grp)
        {
            if (d < rangeStartDate || d > rangeEndDate) return false;
            var dow = d.ToDateTime(TimeOnly.MinValue).DayOfWeek;
            if (!DayAllowed(dow)) return false;
            var scoped = TeacherDraftsHelpers.ResolveCalendarOverride(calendar, d, grp.CourseId, grp.Id);
            if (scoped.HasValue)
            {
                return scoped.Value || r.AllowOnDaysOff;
            }
            return true;
        }
        // Нормалізуємо фільтри: 0 або null означає "без фільтра".
        int? courseId = (r.CourseId > 0) ? r.CourseId : null;
        int? requestedTeacherId = (r.TeacherId > 0) ? r.TeacherId : null;
        var requestedGroupIds = new HashSet<int>();
        if (r.GroupId is int singleGroupId && singleGroupId > 0)
        {
            requestedGroupIds.Add(singleGroupId);
        }
        if (r.GroupIds is not null)
        {
            foreach (var candidateGroupId in r.GroupIds)
            {
                if (candidateGroupId > 0)
                {
                    requestedGroupIds.Add(candidateGroupId);
                }
            }
        }
        bool hasGroupFilter = requestedGroupIds.Count > 0;
        // Ручні години по модулях (якщо задані в запиті).
        var moduleHoursByModuleId = r.ModuleHours?
            .Where(kv => kv.Value > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value * rangeWeekCount)
            ?? new Dictionary<int, int>();
        bool hasModuleHourOverrides = moduleHoursByModuleId.Count > 0;
        var initWarnings = new List<string>();
        // Без курсу неможливо перевірити валідність модулів.
        if (hasModuleHourOverrides && courseId is null)
        {
            return BadRequest(new
            {
                message = "Для генерації за модулями потрібно обрати курс."
            });
        }
        // Завантажуємо групи з урахуванням фільтрів курсу та груп.
        var groups = await _db.Groups
            .Include(x => x.Course)
            .Where(x => courseId == null || x.CourseId == courseId)
            .Where(x => !hasGroupFilter || requestedGroupIds.Contains(x.Id))
            .ToListAsync();
        if (hasGroupFilter)
        {
            var foundGroupIds = groups.Select(g => g.Id).ToHashSet();
            var skippedGroupIds = requestedGroupIds
                .Where(id => !foundGroupIds.Contains(id))
                .OrderBy(id => id)
                .ToList();
            if (skippedGroupIds.Count > 0)
            {
                initWarnings.Add($"Ігноровано групи, що не належать вибраному курсу або не існують: {string.Join(", ", skippedGroupIds)}.");
            }
        }
        if (groups.Count == 0)
        {
            initWarnings.Add("Групи не знайдено.");
            return Ok(new AutoGenResult(0, 0, initWarnings));
        }
        var selectedGroupsById = groups.ToDictionary(g => g.Id);
        var selectedGroupsByCourse = groups
            .GroupBy(g => g.CourseId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderBy(x => x.StudentsCount)
                    .ThenBy(x => x.Id)
                    .ToList());
        var courseIds = groups.Select(g => g.CourseId).Distinct().ToList();
        // Валідуємо перелік модулів проти вибраного курсу.
        if (hasModuleHourOverrides)
        {
            var cid = courseId!.Value;
            var allowedModuleIds = await _db.Modules
                .AsNoTracking()
                .Where(m => m.CourseId == cid || m.ModuleCourses.Any(mc => mc.CourseId == cid))
                .Select(m => m.Id)
                .ToListAsync();
            var allowedSet = allowedModuleIds.ToHashSet();
            var invalidModuleIds = moduleHoursByModuleId.Keys
                .Where(mid => !allowedSet.Contains(mid))
                .OrderBy(mid => mid)
                .ToList();
            if (invalidModuleIds.Count > 0)
            {
                foreach (var mid in invalidModuleIds)
                {
                    moduleHoursByModuleId.Remove(mid);
                }
                initWarnings.Add($"Ігноровано модулі, що не належать курсу #{cid}: {string.Join(", ", invalidModuleIds)}.");
            }
            if (moduleHoursByModuleId.Count == 0)
            {
                return BadRequest(new
                {
                    message = "Потрібно вибрати хоча б один модуль з годинами > 0."
                });
            }
        }
        await GenerationLock.WaitAsync(cancellationToken);
        try
        {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        // За потреби очищаємо існуючі незаблоковані чернетки тижня.
        if (r.ClearExisting && !softFill)
        {
            var gids = groups.Select(g => g.Id).ToList();
            var clearQuery = _db.TeacherDraftItems
                .Where(x => x.Date >= rangeStartDate && x.Date < rangeEndDateExclusive && gids.Contains(x.GroupId) && !x.IsLocked);
            if (requestedTeacherId is int clearTeacherId)
            {
                clearQuery = clearQuery.Where(x => x.TeacherId == clearTeacherId);
            }
            await clearQuery.ExecuteDeleteAsync(cancellationToken);
        }
        // Конфігурації ліміту слота для типу з прапорцем "Бажано першим у тижні" та список аудиторій для підбору.
        var preferredFirstSlotLimitsAll = await _db.PreferredFirstSlotLimitConfigs.AsNoTracking().ToListAsync();
        var roomsAll = await _db.Rooms.AsNoTracking().ToListAsync();
        var roomBuildingById = roomsAll.ToDictionary(r => r.Id, r => r.BuildingId);
        var availableBuildingIds = roomsAll
            .Select(r => r.BuildingId)
            .Distinct()
            .ToHashSet();
        var groupRoomPreferencesByGroupId = new Dictionary<int, (int? BuildingId, HashSet<int> RoomIds)>();
        if (r.GroupRoomPreferences is { Count: > 0 })
        {
            foreach (var pref in r.GroupRoomPreferences
                         .Where(pref => pref.GroupId > 0)
                         .GroupBy(pref => pref.GroupId)
                         .Select(group => group.Last()))
            {
                if (!selectedGroupsById.TryGetValue(pref.GroupId, out var selectedGroup))
                {
                    initWarnings.Add($"Ігноровано пріоритет корпусу/аудиторій для групи #{pref.GroupId}: групу не вибрано для генерації.");
                    continue;
                }

                int? normalizedBuildingId = null;
                if (pref.BuildingId is int requestedBuildingId)
                {
                    if (availableBuildingIds.Contains(requestedBuildingId))
                    {
                        normalizedBuildingId = requestedBuildingId;
                    }
                    else
                    {
                        initWarnings.Add($"Ігноровано пріоритетний корпус #{requestedBuildingId} для групи {selectedGroup.Name}: у ньому немає доступних аудиторій.");
                    }
                }

                var requestedRoomIds = pref.RoomIds?
                    .Where(roomId => roomId > 0)
                    .Distinct()
                    .ToList() ?? new List<int>();
                var normalizedRoomIds = requestedRoomIds
                    .Where(roomBuildingById.ContainsKey)
                    .ToHashSet();
                var invalidRoomIds = requestedRoomIds
                    .Where(roomId => !roomBuildingById.ContainsKey(roomId))
                    .OrderBy(roomId => roomId)
                    .ToList();
                if (invalidRoomIds.Count > 0)
                {
                    initWarnings.Add($"Ігноровано аудиторії для групи {selectedGroup.Name}, яких не існує: {string.Join(", ", invalidRoomIds)}.");
                }

                if (normalizedBuildingId is int buildingId && normalizedRoomIds.Count > 0)
                {
                    var removedRoomIds = normalizedRoomIds
                        .Where(roomId => roomBuildingById[roomId] != buildingId)
                        .OrderBy(roomId => roomId)
                        .ToList();
                    if (removedRoomIds.Count > 0)
                    {
                        initWarnings.Add($"Ігноровано аудиторії для групи {selectedGroup.Name}, що не належать вибраному корпусу #{buildingId}: {string.Join(", ", removedRoomIds)}.");
                        normalizedRoomIds.RemoveWhere(roomId => roomBuildingById[roomId] != buildingId);
                    }
                }

                if (normalizedBuildingId is null && normalizedRoomIds.Count == 0)
                {
                    continue;
                }

                groupRoomPreferencesByGroupId[pref.GroupId] = (normalizedBuildingId, normalizedRoomIds);
            }
        }
        var travelMinutesByBuildingPair = await _db.BuildingTravels.AsNoTracking()
            .ToDictionaryAsync(k => (k.FromBuildingId, k.ToBuildingId), v => v.Minutes);
        int TravelMinutes(int fromBuildingId, int toBuildingId)
        {
            if (fromBuildingId == toBuildingId)
                return 0;
            if (travelMinutesByBuildingPair.TryGetValue((fromBuildingId, toBuildingId), out var minutes))
                return minutes;
            if (travelMinutesByBuildingPair.TryGetValue((toBuildingId, fromBuildingId), out minutes))
                return minutes;
            return 10;
        }
        // Список модулів для формування допустимих аудиторій/будівель.
        var moduleIdsAll = await _db.Modules.Select(m => m.Id).ToListAsync();
        var allowedRoomsByModule = await _db.ModuleRooms
            .Where(mr => moduleIdsAll.Contains(mr.ModuleId))
            .GroupBy(mr => mr.ModuleId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(x => x.RoomId).ToHashSet());
        var allowedBuildingsByModule = await _db.ModuleBuildings
            .Where(mb => moduleIdsAll.Contains(mb.ModuleId))
            .GroupBy(mb => mb.ModuleId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(x => x.BuildingId).ToHashSet());
        // Історичне вікно для перевірки повторів.
        const int historyMonthsForRepeats = 12;
        var historyStart = rangeStartDate.AddMonths(-historyMonthsForRepeats);
        var lastWeekStart = planningWeekStart.AddDays(-7);
        // Завантажуємо вже зайняті слоти з чернеток і опублікованого розкладу.
        var busyDrafts = await _db.TeacherDraftItems
            .Include(x => x.Room)
            .Where(x => x.Date >= historyStart && x.Date < planningWeekEndExclusive)
            .Select(x => new BusySlot(
                x.GroupId,
                x.TeacherId,
                x.RoomId,
                x.Date,
                x.StartTime,
                x.EndTime,
                x.Room != null ? (int?)x.Room.BuildingId : null,
                x.ModuleId,
                x.LessonTypeId,
                x.ModuleTopicId,
                true))
            .ToListAsync();
        var busySchedule = await _db.ScheduleItems
            .Include(x => x.Room)
            .Where(x => x.Date >= historyStart && x.Date < planningWeekEndExclusive)
            .Select(x => new BusySlot(
                x.GroupId,
                x.TeacherId,
                x.RoomId,
                x.Date,
                x.StartTime,
                x.EndTime,
                x.Room != null ? (int?)x.Room.BuildingId : null,
                x.ModuleId,
                x.LessonTypeId,
                x.ModuleTopicId,
                false))
            .ToListAsync();
        var topicOrderDrafts = await _db.TeacherDraftItems
            .Include(x => x.Room)
            .Where(x => x.ModuleTopicId != null
                        && x.Date >= historyStart
                        && courseIds.Contains(x.Group.CourseId))
            .Select(x => new BusySlot(
                x.GroupId,
                x.TeacherId,
                x.RoomId,
                x.Date,
                x.StartTime,
                x.EndTime,
                x.Room != null ? (int?)x.Room.BuildingId : null,
                x.ModuleId,
                x.LessonTypeId,
                x.ModuleTopicId,
                true))
            .ToListAsync();
        var topicOrderSchedule = await _db.ScheduleItems
            .Include(x => x.Room)
            .Where(x => x.ModuleTopicId != null
                        && x.Date >= historyStart
                        && courseIds.Contains(x.Group.CourseId))
            .Select(x => new BusySlot(
                x.GroupId,
                x.TeacherId,
                x.RoomId,
                x.Date,
                x.StartTime,
                x.EndTime,
                x.Room != null ? (int?)x.Room.BuildingId : null,
                x.ModuleId,
                x.LessonTypeId,
                x.ModuleTopicId,
                false))
            .ToListAsync();
        var busy = busyDrafts
            .Concat(busySchedule)
            .ToList();
        var topicOrderSlots = topicOrderDrafts
            .Concat(topicOrderSchedule)
            .ToList();
        var busyByGroup = busy
            .GroupBy(slot => slot.GroupId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var busyByTeacher = busy
            .Where(slot => slot.TeacherId is int)
            .GroupBy(slot => slot.TeacherId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        var busyByRoom = busy
            .Where(slot => slot.RoomId is int)
            .GroupBy(slot => slot.RoomId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        var busyByDate = busy
            .GroupBy(slot => slot.Date)
            .ToDictionary(group => group.Key, group => group.ToList());
        var busyByGroupDate = busy
            .GroupBy(slot => (slot.GroupId, slot.Date))
            .ToDictionary(group => group.Key, group => group.ToList());
        var busyByTeacherDate = busy
            .Where(slot => slot.TeacherId is int)
            .GroupBy(slot => (slot.TeacherId!.Value, slot.Date))
            .ToDictionary(group => group.Key, group => group.ToList());
        var busyByRoomDate = busy
            .Where(slot => slot.RoomId is int)
            .GroupBy(slot => (slot.RoomId!.Value, slot.Date))
            .ToDictionary(group => group.Key, group => group.ToList());
        var topicOrderByGroup = topicOrderSlots
            .GroupBy(slot => slot.GroupId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var emptyBusySlots = Array.Empty<BusySlot>();
        IReadOnlyList<BusySlot> BusyForGroup(int groupId)
            => busyByGroup.TryGetValue(groupId, out var slotsForGroup) ? slotsForGroup : emptyBusySlots;
        IReadOnlyList<BusySlot> TopicOrderForGroup(int groupId)
            => topicOrderByGroup.TryGetValue(groupId, out var slotsForGroup) ? slotsForGroup : emptyBusySlots;
        IReadOnlyList<BusySlot> BusyForDate(DateOnly date)
            => busyByDate.TryGetValue(date, out var slotsForDate) ? slotsForDate : emptyBusySlots;
        IReadOnlyList<BusySlot> BusyForGroupDate(int groupId, DateOnly date)
            => busyByGroupDate.TryGetValue((groupId, date), out var slotsForGroupDate) ? slotsForGroupDate : emptyBusySlots;
        IReadOnlyList<BusySlot> BusyForTeacherDate(int teacherId, DateOnly date)
            => busyByTeacherDate.TryGetValue((teacherId, date), out var slotsForTeacherDate) ? slotsForTeacherDate : emptyBusySlots;
        IReadOnlyList<BusySlot> BusyForRoomDate(int roomId, DateOnly date)
            => busyByRoomDate.TryGetValue((roomId, date), out var slotsForRoomDate) ? slotsForRoomDate : emptyBusySlots;
        static bool SlotOverlaps(BusySlot slot, DateOnly date, TimeOnly start, TimeOnly end)
            => slot.Date == date && slot.StartTime < end && start < slot.EndTime;
        bool HasGroupOverlap(int groupId, DateOnly date, TimeOnly start, TimeOnly end)
            => BusyForGroupDate(groupId, date).Any(slot => SlotOverlaps(slot, date, start, end));
        bool HasTeacherOverlap(int teacherId, DateOnly date, TimeOnly start, TimeOnly end)
            => BusyForTeacherDate(teacherId, date).Any(slot => SlotOverlaps(slot, date, start, end));
        bool HasRoomOverlap(int roomId, DateOnly date, TimeOnly start, TimeOnly end)
            => BusyForRoomDate(roomId, date).Any(slot => SlotOverlaps(slot, date, start, end));
        static bool SlotMatches(
            BusySlot slot,
            int groupId,
            DateOnly date,
            TimeOnly start,
            TimeOnly end,
            int moduleId,
            int? teacherId,
            int? roomId,
            int? moduleTopicId)
            => slot.GroupId == groupId
               && slot.Date == date
               && slot.StartTime == start
               && slot.EndTime == end
               && slot.ModuleId == moduleId
               && slot.TeacherId == teacherId
               && slot.RoomId == roomId
               && slot.ModuleTopicId == moduleTopicId;
        void AddBusyIndex<TKey>(Dictionary<TKey, List<BusySlot>> index, TKey key, BusySlot slot)
            where TKey : notnull
        {
            if (!index.TryGetValue(key, out var indexedSlots))
            {
                indexedSlots = new List<BusySlot>();
                index[key] = indexedSlots;
            }
            indexedSlots.Add(slot);
        }
        void RemoveBusyIndex<TKey>(Dictionary<TKey, List<BusySlot>> index, TKey key, BusySlot slot)
            where TKey : notnull
        {
            if (!index.TryGetValue(key, out var indexedSlots))
            {
                return;
            }
            indexedSlots.Remove(slot);
            if (indexedSlots.Count == 0)
            {
                index.Remove(key);
            }
        }
        void AddBusySlot(BusySlot slot)
        {
            busy.Add(slot);
            AddBusyIndex(busyByGroup, slot.GroupId, slot);
            AddBusyIndex(busyByDate, slot.Date, slot);
            AddBusyIndex(busyByGroupDate, (slot.GroupId, slot.Date), slot);
            if (slot.TeacherId is int teacherId)
            {
                AddBusyIndex(busyByTeacher, teacherId, slot);
                AddBusyIndex(busyByTeacherDate, (teacherId, slot.Date), slot);
            }
            if (slot.RoomId is int roomId)
            {
                AddBusyIndex(busyByRoom, roomId, slot);
                AddBusyIndex(busyByRoomDate, (roomId, slot.Date), slot);
            }
            if (slot.ModuleTopicId is int)
            {
                topicOrderSlots.Add(slot);
                AddBusyIndex(topicOrderByGroup, slot.GroupId, slot);
            }
        }
        bool RemoveBusySlot(BusySlot slot)
        {
            var removed = busy.Remove(slot);
            if (!removed)
            {
                return false;
            }
            RemoveBusyIndex(busyByGroup, slot.GroupId, slot);
            RemoveBusyIndex(busyByDate, slot.Date, slot);
            RemoveBusyIndex(busyByGroupDate, (slot.GroupId, slot.Date), slot);
            if (slot.TeacherId is int teacherId)
            {
                RemoveBusyIndex(busyByTeacher, teacherId, slot);
                RemoveBusyIndex(busyByTeacherDate, (teacherId, slot.Date), slot);
            }
            if (slot.RoomId is int roomId)
            {
                RemoveBusyIndex(busyByRoom, roomId, slot);
                RemoveBusyIndex(busyByRoomDate, (roomId, slot.Date), slot);
            }
            if (slot.ModuleTopicId is int)
            {
                topicOrderSlots.Remove(slot);
                RemoveBusyIndex(topicOrderByGroup, slot.GroupId, slot);
            }
            return true;
        }
        BusySlot? FindBusySlotForDraft(TeacherDraftItem draft, TimeOnly start, TimeOnly end)
        {
            return BusyForGroup(draft.GroupId)
                .FirstOrDefault(slot => SlotMatches(
                    slot,
                    draft.GroupId,
                    draft.Date,
                    start,
                    end,
                    draft.ModuleId,
                    draft.TeacherId,
                    draft.RoomId,
                    draft.ModuleTopicId));
        }
        IEnumerable<DateOnly> DatesBetween(DateOnly startInclusive, DateOnly endExclusive)
        {
            for (var date = startInclusive; date < endExclusive; date = date.AddDays(1))
            {
                yield return date;
            }
        }
        // Фіксуємо, де вже використовувався пріоритетний тип на тижні.
        var hasPreferred = new HashSet<(int groupId, int moduleId)>(
            DatesBetween(rangeStartDate, rangeEndDateExclusive)
                .SelectMany(BusyForDate)
                .Where(b => preferredFirstTypeId != 0
                            && b.LessonTypeId == preferredFirstTypeId)
                .Select(b => (b.GroupId, b.ModuleId)));
        // Збираємо модулі, що були минулого тижня (для зменшення повторів).
        var lastWeekModulesByGroup = DatesBetween(lastWeekStart, planningWeekStart)
            .SelectMany(BusyForDate)
            .Where(b => !excludedTypeIds.Contains(b.LessonTypeId))
            .GroupBy(b => b.GroupId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ModuleId).Distinct().ToHashSet());
        // Лічильник кількості пар на день для кожної групи.
        var perDayCount = new Dictionary<(int groupId, DateOnly date), int>();
        foreach (var indexedDay in busyByGroupDate)
        {
            var count = indexedDay.Value.Count(b => !excludedTypeIds.Contains(b.LessonTypeId));
            if (count > 0)
            {
                perDayCount[indexedDay.Key] = count;
            }
        }
        // Допоміжні методи для підрахунків навантаження по днях.
        int CountFor(int gid, DateOnly date) => perDayCount.TryGetValue((gid, date), out var c) ? c : 0;
        int CountModuleForDay(int gid, DateOnly date, int moduleId) =>
            BusyForGroupDate(gid, date).Count(x => x.ModuleId == moduleId
                                                   && !excludedTypeIds.Contains(x.LessonTypeId));
        int CountDistinctModulesForDay(int gid, DateOnly date) =>
            BusyForGroupDate(gid, date)
                .Where(x => !excludedTypeIds.Contains(x.LessonTypeId))
                .Select(x => x.ModuleId)
                .Distinct()
                .Count();
        int CountGroupsWithModuleInSlot(int moduleId, DateOnly date, TimeOnly start, TimeOnly end) =>
            BusyForDate(date)
                .Where(x => x.ModuleId == moduleId
                            && !excludedTypeIds.Contains(x.LessonTypeId)
                            && x.StartTime < end
                            && start < x.EndTime)
                .Select(x => x.GroupId)
                .Distinct()
                .Count();
        void Inc(int gid, DateOnly date)
        {
            var key = (gid, date);
            perDayCount[key] = CountFor(gid, date) + 1;
        }
        void Dec(int gid, DateOnly date)
        {
            var key = (gid, date);
            if (!perDayCount.TryGetValue(key, out var existing))
            {
                return;
            }
            if (existing <= 1)
            {
                perDayCount.Remove(key);
                return;
            }
            perDayCount[key] = existing - 1;
        }
        // Перевірки повторів модулів по днях.
        bool HadSameModulePreviousDay(int gid, int mid, DateOnly date)
        {
            var prev = date.AddDays(-1);
            return BusyForGroupDate(gid, prev).Any(x => x.ModuleId == mid
                                                        && !excludedTypeIds.Contains(x.LessonTypeId));
        }
        // Перевіряємо, чи модуль був у "вікні" навколо дати.
        bool HasRecentModule(int gid, int mid, DateOnly date, int? windowDaysOverride = null)
        {
            var windowDays = windowDaysOverride ?? recentRepeatWindowDays;
            var from = date.AddDays(-windowDays);
            var to = date.AddDays(windowDays);
            return BusyForGroup(gid).Any(x => x.ModuleId == mid
                                              && x.Date != date
                                              && x.Date >= from
                                              && x.Date <= to
                                              && !excludedTypeIds.Contains(x.LessonTypeId));
        }
        // Ознака, що модуль був використаний минулого тижня.
        bool UsedLastWeek(int gid, int mid) =>
            lastWeekModulesByGroup.TryGetValue(gid, out var mods) && mods.Contains(mid);
        // Базові зв'язки модуль -> викладачі / керівники самостійної роботи.
        var teachersForModule = await _db.TeacherModules.AsNoTracking().ToListAsync();
        var supervisorsForModule = await _db.ModuleSupervisors.AsNoTracking().ToListAsync();
        if (requestedTeacherId is int teacherFilterId)
        {
            teachersForModule = teachersForModule
                .Where(x => x.TeacherId == teacherFilterId)
                .ToList();
            supervisorsForModule = supervisorsForModule
                .Where(x => x.TeacherId == teacherFilterId)
                .ToList();
        }
        // Метадані викладачів (для підписів і перевірок кафедр).
        var teachersMeta = await _db.Teachers
            .AsNoTracking()
            .Select(t => new
            {
                t.Id,
                Name = string.IsNullOrWhiteSpace(t.FullName) ? $"#{t.Id}" : t.FullName!,
                t.DepartmentId
            })
            .ToListAsync();
        // Мапи для швидкого доступу до імен та кафедр викладачів.
        var teacherNames = teachersMeta.ToDictionary(x => x.Id, x => x.Name);
        var teacherDepartmentById = teachersMeta.ToDictionary(x => x.Id, x => x.DepartmentId);
        // Назви кафедр для повідомлень та перевірок.
        var departmentNames = await _db.Departments
            .AsNoTracking()
            .ToDictionaryAsync(d => d.Id, d => string.IsNullOrWhiteSpace(d.Name) ? $"#{d.Id}" : d.Name!);
        // Робочі години викладачів (обмеження часу).
        var teacherWorkingHours = await _db.TeacherWorkingHours.AsNoTracking()
            .Select(w => new { w.TeacherId, w.DayOfWeek, w.Start, w.End })
            .ToListAsync();
        // Групуємо робочі години по викладачах та днях тижня.
        var workingHoursByTeacher = teacherWorkingHours
            .GroupBy(w => w.TeacherId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(x => x.DayOfWeek)
                    .ToDictionary(
                        d => d.Key,
                        d => d.Select(x => (x.Start, x.End)).ToList()
                    )
            );
        // Перевіряє, чи вкладається заняття в дозволені години викладача.
        bool TeacherFitsWorkingHours(int teacherId, DateOnly date, TimeOnly start, TimeOnly end)
        {
            if (!workingHoursByTeacher.TryGetValue(teacherId, out var dayMap) || dayMap.Count == 0)
                return true;
            var dow = date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
            if (!dayMap.TryGetValue(dow, out var windows) || windows.Count == 0)
                return false;
            return windows.Any(w => w.Start <= start && end <= w.End);
        }
        // Навантаження викладачів у вже опублікованому розкладі.
        var teacherLoadSchedule = await _db.ScheduleItems
            .Include(si => si.Group)
            .Where(si => si.TeacherId != null && courseIds.Contains(si.Group.CourseId))
            .GroupBy(si => new { si.TeacherId, si.Group.CourseId })
            .Select(g => new { TeacherId = g.Key.TeacherId!.Value, g.Key.CourseId, C = g.Count() })
            .ToListAsync();
        // Навантаження викладачів у поточних чернетках.
        var teacherLoadDrafts = await _db.TeacherDraftItems
            .Include(di => di.Group)
            .Where(di => di.TeacherId != null && courseIds.Contains(di.Group.CourseId))
            .GroupBy(di => new { di.TeacherId, di.Group.CourseId })
            .Select(g => new { TeacherId = g.Key.TeacherId!.Value, g.Key.CourseId, C = g.Count() })
            .ToListAsync();
        // Об'єднана карта навантажень для балансу.
        var teacherLoadMap = teacherLoadSchedule
            .Concat(teacherLoadDrafts)
            .GroupBy(x => (x.TeacherId, x.CourseId))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.C));
        // Активні плани модулів по курсах.
        var activePlans = await _db.ModulePlans.Where(p => courseIds.Contains(p.CourseId) && p.IsActive).ToListAsync();
        // Базова метрика навантаження викладача в межах курсу.
        int TeacherLoadScore(int teacherId, int courseId)
        {
            if (teacherLoadMap.TryGetValue((teacherId, courseId), out var count))
            {
                return count;
            }
            return 0;
        }
        // Список модулів, які потрібно врахувати для планів та тем.
        var moduleIdsForPlans = activePlans.Select(p => p.ModuleId).Distinct().ToList();
        if (hasModuleHourOverrides)
        {
            moduleIdsForPlans = moduleIdsForPlans
                .Concat(moduleHoursByModuleId.Keys)
                .Distinct()
                .ToList();
        }
        // Назви модулів для повідомлень та логіки вибору.
        var moduleTitles = await _db.Modules.AsNoTracking()
            .Where(m => moduleIdsForPlans.Contains(m.Id))
            .ToDictionaryAsync(
                m => m.Id,
                m => string.IsNullOrWhiteSpace(m.Title) ? $"#{m.Id}" : m.Title.Trim());
        // Формуємо зручний ярлик модуля.
        string ModuleTitleLabel(int moduleId) =>
            moduleTitles.TryGetValue(moduleId, out var title) ? title : $"#{moduleId}";
        // Теми модулів для підбору тем і самостійної роботи.
        var topicsAll = await _db.ModuleTopics
            .Where(t => moduleIdsForPlans.Contains(t.ModuleId))
            .ToListAsync();
        // Сортуємо теми за явним порядком із БД, а код використовуємо лише як стабільний fallback.
        topicsAll.Sort((a, b) =>
        {
            var orderDiff = a.Order.CompareTo(b.Order);
            return orderDiff != 0
                ? orderDiff
                : TeacherDraftsHelpers.CompareTopicCodes(a.TopicCode, b.TopicCode);
        });
        // Модулі, де всі теми міжзборові (їх пропускаємо у розкладі).
        var interAssemblyOnlyModules = topicsAll
            .GroupBy(t => t.ModuleId)
            .Where(g => g.Any() && g.All(x => x.IsInterAssembly))
            .Select(g => g.Key)
            .ToHashSet();
        // Робочий набір тем без міжзборових.
        var topicsRaw = topicsAll
            .Where(t => !t.IsInterAssembly)
            .ToList();
        // Дозволені теми для підстановки.
        var allowedTopicIds = topicsRaw.Select(t => t.Id).ToHashSet();
        // Теми самостійної роботи під керівництвом викладача.
        var flaggedSelfStudyTopicIds = topicsRaw
            .Where(t => t.SelfStudyBySupervisor && t.SelfStudyHours > 0)
            .Select(t => t.Id)
            .ToHashSet();
        // Групування тем за модулями.
        var topicsByModule = topicsRaw
            .GroupBy(t => t.ModuleId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var topicById = topicsRaw.ToDictionary(t => t.Id);
        // Теми самостійної роботи для кожного модуля.
        var selfStudyTopicsByModule = topicsByModule
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value
                    .Where(t => t.SelfStudyBySupervisor && t.SelfStudyHours > 0)
                    .ToList());
        // Ліміти використання тем по аудиторних годинах.
        var topicUsageLimitById = topicsRaw
            .ToDictionary(t => t.Id, t => Math.Max(0, t.AuditoriumHours));
        // Сумарні години по модулю (аудиторні та самостійні).
        var moduleAuditoriumHours = topicsByModule
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Sum(t => Math.Max(0, t.AuditoriumHours)));
        // Сумарні години самостійної роботи, які мають бути заплановані.
        var moduleSelfStudyHours = topicsByModule
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value
                    .Where(t => t.SelfStudyBySupervisor)
                    .Sum(t => Math.Max(0, t.SelfStudyHours)));
        // Самостійні заняття, вже призначені у чернетках.
        var selfStudyAssignmentsDraft = await _db.TeacherDraftItems
            .Where(di => di.IsSelfStudy
                         && courseIds.Contains(di.Group.CourseId)
                         && (di.ModuleTopicId == null || flaggedSelfStudyTopicIds.Contains(di.ModuleTopicId.Value)))
            .Select(di => new { di.GroupId, di.ModuleId })
            .ToListAsync();
        // Самостійні заняття, вже опубліковані у розкладі.
        var selfStudyAssignmentsSchedule = await _db.ScheduleItems
            .Where(si => si.IsSelfStudy
                         && courseIds.Contains(si.Group.CourseId)
                         && (si.ModuleTopicId == null || flaggedSelfStudyTopicIds.Contains(si.ModuleTopicId.Value)))
            .Select(si => new { si.GroupId, si.ModuleId })
            .ToListAsync();
        // Самостійні заняття по конкретних темах (чернетки).
        var selfStudyTopicAssignmentsDraft = await _db.TeacherDraftItems
            .Where(di => di.IsSelfStudy
                         && di.ModuleTopicId != null
                         && flaggedSelfStudyTopicIds.Contains(di.ModuleTopicId!.Value)
                         && courseIds.Contains(di.Group.CourseId))
            .GroupBy(di => new { di.GroupId, di.ModuleId, TopicId = di.ModuleTopicId!.Value })
            .Select(g => new { g.Key.GroupId, g.Key.ModuleId, g.Key.TopicId, C = g.Count() })
            .ToListAsync();
        // Самостійні заняття по конкретних темах (розклад).
        var selfStudyTopicAssignmentsSchedule = await _db.ScheduleItems
            .Where(si => si.IsSelfStudy
                         && si.ModuleTopicId != null
                         && flaggedSelfStudyTopicIds.Contains(si.ModuleTopicId!.Value)
                         && courseIds.Contains(si.Group.CourseId))
            .GroupBy(si => new { si.GroupId, si.ModuleId, TopicId = si.ModuleTopicId!.Value })
            .Select(g => new { g.Key.GroupId, g.Key.ModuleId, g.Key.TopicId, C = g.Count() })
            .ToListAsync();
        // Зведений лічильник самостійних занять по модулю/групі.
        var selfStudyAssignments = selfStudyAssignmentsDraft
            .Concat(selfStudyAssignmentsSchedule)
            .GroupBy(x => (x.GroupId, x.ModuleId))
            .ToDictionary(g => g.Key, g => g.Count());
        // Зведений лічильник самостійних занять по темі.
        var selfStudyTopicAssignments = selfStudyTopicAssignmentsDraft
            .Concat(selfStudyTopicAssignmentsSchedule)
            .GroupBy(x => (x.GroupId, x.ModuleId, x.TopicId))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.C));
        // Призначені теми по групі/модулю (чернетки).
        var topicAssignmentsDraft = await _db.TeacherDraftItems
            .Where(di => di.ModuleTopicId != null
                         && courseIds.Contains(di.Group.CourseId)
                         && allowedTopicIds.Contains(di.ModuleTopicId!.Value))
            .Select(di => new { di.GroupId, di.ModuleId, TopicId = di.ModuleTopicId!.Value })
            .ToListAsync();
        // Призначені теми по групі/модулю (розклад).
        var topicAssignmentsSchedule = await _db.ScheduleItems
            .Where(si => si.ModuleTopicId != null
                         && courseIds.Contains(si.Group.CourseId)
                         && allowedTopicIds.Contains(si.ModuleTopicId!.Value))
            .Select(si => new { si.GroupId, si.ModuleId, TopicId = si.ModuleTopicId!.Value })
            .ToListAsync();
        // Сховище використаних тем по групі/модулю.
        var topicAssignments = new Dictionary<(int GroupId, int ModuleId), Dictionary<int, int>>();
        // Збільшує лічильник використаної теми.
        void SeedTopicAssignment(int groupId, int moduleId, int topicId)
        {
            var key = (groupId, moduleId);
            if (!topicAssignments.TryGetValue(key, out var assigned))
            {
                assigned = new Dictionary<int, int>();
                topicAssignments[key] = assigned;
            }
            assigned.TryGetValue(topicId, out var usedCount);
            assigned[topicId] = usedCount + 1;
        }
        foreach (var entry in topicAssignmentsDraft.Concat(topicAssignmentsSchedule))
        {
            SeedTopicAssignment(entry.GroupId, entry.ModuleId, entry.TopicId);
        }
        // Ліміт використання теми у розкладі.
        int GetTopicUsageLimit(ModuleTopic topic)
            => topicUsageLimitById.TryGetValue(topic.Id, out var limit) ? limit : Math.Max(0, topic.AuditoriumHours);
        // Вибір наступної теми строго за порядком коду.
        ModuleTopic? SelectNextTopicInOrder(int groupId, int moduleId)
        {
            if (!topicsByModule.TryGetValue(moduleId, out var list) || list.Count == 0)
                return null;
            topicAssignments.TryGetValue((groupId, moduleId), out var assigned);
            // Вибираємо наступну тему строго за порядком коду і не "перестрибуємо" вперед.
            foreach (var t in list)
            {
                var limit = GetTopicUsageLimit(t);
                if (limit <= 0) continue;
                var usedByGroup = (assigned != null && assigned.TryGetValue(t.Id, out var usedGroupVal)) ? usedGroupVal : 0;
                if (usedByGroup < limit)
                {
                    return t;
                }
            }
            return null;
        }
        // Тримаємо прапорці, щоб не дублювати попередження.
        var topicsExhaustedNotified = new HashSet<(int GroupId, int ModuleId)>();
        var overflowTopicNotified = new HashSet<(int GroupId, int ModuleId, int TopicId)>();
        var missingModulesNotified = new HashSet<int>();
        int created = 0, skipped = 0;
        var allCreatedDrafts = new List<TeacherDraftItem>();
        var selectedGroupIdSet = groups.Select(g => g.Id).ToHashSet();
        var movableDrafts = await _db.TeacherDraftItems
            .Where(item => item.Date >= rangeStartDate
                           && item.Date < rangeEndDateExclusive
                           && selectedGroupIdSet.Contains(item.GroupId)
                           && item.Status == DraftStatus.Draft)
            .ToListAsync(cancellationToken);
        const int maxEmergencySingletonSharedLectures = 0;
        const int latestLectureSlotBeforeLongBreak = 6;
        const int earliestEmergencyLectureSlotOrder = 9;
        var emergencySingletonSharedLecturesCreated = 0;
        int incompleteDraftsCreated = 0, incompleteMissingTeacherCount = 0, incompleteMissingRoomCount = 0, incompleteMissingBothCount = 0;
        // Збираємо попередження та деталі прогалин.
        var warnings = new List<string>();
        if (initWarnings.Count > 0)
        {
            warnings.AddRange(initWarnings);
        }
        var gapDetails = new List<AutoGenGapDetail>();
        var gapWarnings = new HashSet<(int GroupId, DateOnly Date, TimeOnly Start, TimeOnly End)>();
        var slotFailureReasons = new Dictionary<(int GroupId, DateOnly Date, TimeOnly Start, TimeOnly End), HashSet<string>>();
        // Перевірка валідності типу заняття.
        bool TypeAllowed(int lessonTypeId)
        {
            return typeById.TryGetValue(lessonTypeId, out var lt)
                   && lt.IsActive
                   && lt.CountInPlan
                   && !excludedTypeIds.Contains(lessonTypeId);
        }
        int MaxSharedLectureGroupsForCourse(int courseId)
            => selectedGroupsByCourse.TryGetValue(courseId, out var sameCourseGroups) && sameCourseGroups.Count > 0
                ? sameCourseGroups.Count
                : 1;
        // Перевірка, чи тип заняття трактуємо як лекцію для об'єднання груп.
        bool IsLectureType(int lessonTypeId) => lectureTypeIds.Contains(lessonTypeId);
        var preferredFirstTypeIds = types
            .Where(t => t.PreferredFirstInWeek)
            .Select(t => t.Id)
            .ToHashSet();
        // Спільний потік дозволений для лекційних типів і типів із прапорцем "Бажано першим".
        bool CanShareAcrossGroups(int lessonTypeId)
            => IsLectureType(lessonTypeId)
               || preferredFirstTypeIds.Contains(lessonTypeId);
        // Після 6-ї години лекції не ставимо у перехідні слоти 7-8: там замало часу на зміну корпусу.
        int RegularLectureMaxSlotOrder(int? configuredMaxSlotOrder = null)
            => configuredMaxSlotOrder is int configured && configured > 0
                ? Math.Min(configured, latestLectureSlotBeforeLongBreak)
                : latestLectureSlotBeforeLongBreak;
        bool IsBlockedLateLectureSlot(int lessonTypeId, int slotOrder, int? configuredMaxSlotOrder = null)
        {
            if (!CanShareAcrossGroups(lessonTypeId) || slotOrder <= 0)
            {
                return false;
            }

            return slotOrder > RegularLectureMaxSlotOrder(configuredMaxSlotOrder);
        }
        bool IsEmergencyLateLectureSlot(int lessonTypeId, int slotOrder, int? configuredMaxSlotOrder = null)
            => CanShareAcrossGroups(lessonTypeId)
               && slotOrder > RegularLectureMaxSlotOrder(configuredMaxSlotOrder)
               && slotOrder >= earliestEmergencyLectureSlotOrder;
        int SlotGroupLimitForPlacement(int courseId, int lessonTypeId, bool isSelfStudyPlacement)
            => !isSelfStudyPlacement && CanShareAcrossGroups(lessonTypeId)
                ? MaxSharedLectureGroupsForCourse(courseId)
                : maxParallelGroupsPerModuleInSlot;
        // Знаходимо вже створений спільний потік, до якого можна безпечно приєднати групу.
        IReadOnlyList<int> FindExistingSharedLectureGroupIds(
            int courseId,
            int moduleId,
            int lessonTypeId,
            int? moduleTopicId,
            int teacherId,
            int roomId,
            DateOnly date,
            TimeOnly start,
            TimeOnly end)
        {
            if (!CanShareAcrossGroups(lessonTypeId))
            {
                return Array.Empty<int>();
            }
            return busy
                .Where(x => x.Date == date
                            && x.StartTime == start
                            && x.EndTime == end
                            && x.ModuleId == moduleId
                            && x.LessonTypeId == lessonTypeId
                            && x.ModuleTopicId == moduleTopicId
                            && x.JoinableDraft
                            && x.TeacherId == teacherId
                            && x.RoomId == roomId
                            && selectedGroupsById.TryGetValue(x.GroupId, out var existingGroup)
                            && existingGroup.CourseId == courseId)
                .Select(x => x.GroupId)
                .Distinct()
                .ToList();
        }
        // Перевіряємо, що зайнятий слот належить саме тому спільному потоку, до якого приєднуємося.
        bool IsSameSharedLectureCluster(
            BusySlot slot,
            IReadOnlySet<int> existingSharedLectureGroupIds,
            int moduleId,
            int lessonTypeId,
            int? moduleTopicId,
            int teacherId,
            int roomId,
            DateOnly date,
            TimeOnly start,
            TimeOnly end)
        {
            return existingSharedLectureGroupIds.Contains(slot.GroupId)
                   && slot.Date == date
                   && slot.StartTime == start
                   && slot.EndTime == end
                   && slot.ModuleId == moduleId
                   && slot.LessonTypeId == lessonTypeId
                   && slot.ModuleTopicId == moduleTopicId
                   && slot.TeacherId == teacherId
                   && slot.RoomId == roomId;
        }
        string BusyGroupLabel(int groupId)
            => selectedGroupsById.TryGetValue(groupId, out var group) ? group.Name : $"#{groupId}";
        string BusyTeacherLabel(int teacherId)
            => teacherNames.TryGetValue(teacherId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : $"#{teacherId}";
        static string BusyRoomLabel(Room room)
            => string.IsNullOrWhiteSpace(room.Name) ? $"#{room.Id}" : room.Name!;
        bool BlocksTeacherSlot(int lessonTypeId)
            => typeById.TryGetValue(lessonTypeId, out var lessonType) && lessonType.BlocksTeacher;
        bool BlocksRoomSlot(int lessonTypeId)
            => typeById.TryGetValue(lessonTypeId, out var lessonType) && lessonType.BlocksRoom;
        bool IsSameShareableBusySlot(
            BusySlot slot,
            int moduleId,
            int lessonTypeId,
            int? moduleTopicId,
            int? teacherId,
            int? roomId,
            DateOnly date,
            TimeOnly start,
            TimeOnly end)
        {
            return CanShareAcrossGroups(lessonTypeId)
                   && slot.Date == date
                   && slot.StartTime == start
                   && slot.EndTime == end
                   && slot.ModuleId == moduleId
                   && slot.LessonTypeId == lessonTypeId
                   && slot.ModuleTopicId == moduleTopicId
                   && slot.TeacherId == teacherId
                   && slot.RoomId == roomId;
        }
        bool TryValidatePlacementAgainstBusy(
            int moduleId,
            int lessonTypeId,
            int? moduleTopicId,
            int? teacherId,
            Room? room,
            DateOnly date,
            TimeOnly start,
            TimeOnly end,
            IReadOnlyCollection<int> newGroupIds,
            out string reason)
        {
            reason = string.Empty;
            var newGroupSet = newGroupIds
                .Where(selectedGroupsById.ContainsKey)
                .Distinct()
                .ToHashSet();
            if (newGroupSet.Count == 0)
            {
                reason = $"Немає вибраних груп для розміщення у слоті {date:yyyy-MM-dd} {start:HH\\:mm}-{end:HH\\:mm}.";
                return false;
            }

            foreach (var groupId in newGroupSet)
            {
                if (BusyForGroupDate(groupId, date).Any(slot => SlotOverlaps(slot, date, start, end)))
                {
                    reason = $"Група {BusyGroupLabel(groupId)} вже зайнята у слоті {date:yyyy-MM-dd} {start:HH\\:mm}-{end:HH\\:mm}.";
                    return false;
                }
            }

            var slotLabel = $"{date:yyyy-MM-dd} {start:HH\\:mm}-{end:HH\\:mm}";
            if (teacherId is int busyTeacherId && BlocksTeacherSlot(lessonTypeId))
            {
                foreach (var busySlot in BusyForTeacherDate(busyTeacherId, date)
                             .Where(slot => SlotOverlaps(slot, date, start, end) && BlocksTeacherSlot(slot.LessonTypeId)))
                {
                    if (IsSameShareableBusySlot(busySlot, moduleId, lessonTypeId, moduleTopicId, teacherId, room?.Id, date, start, end))
                    {
                        continue;
                    }

                    reason = $"Викладач {BusyTeacherLabel(busyTeacherId)} вже зайнятий у слоті {slotLabel}.";
                    return false;
                }
            }

            if (room is null || !BlocksRoomSlot(lessonTypeId))
            {
                return true;
            }

            var capacityGroupIds = new HashSet<int>(newGroupSet);
            foreach (var busySlot in BusyForRoomDate(room.Id, date)
                         .Where(slot => SlotOverlaps(slot, date, start, end) && BlocksRoomSlot(slot.LessonTypeId)))
            {
                if (!IsSameShareableBusySlot(busySlot, moduleId, lessonTypeId, moduleTopicId, teacherId, room.Id, date, start, end))
                {
                    reason = $"Аудиторія {BusyRoomLabel(room)} вже зайнята у слоті {slotLabel}.";
                    return false;
                }

                capacityGroupIds.Add(busySlot.GroupId);
            }

            var totalStudents = 0;
            foreach (var groupId in capacityGroupIds)
            {
                if (!selectedGroupsById.TryGetValue(groupId, out var group))
                {
                    reason = $"Неможливо перевірити місткість аудиторії {BusyRoomLabel(room)} у слоті {slotLabel} для групи #{groupId}.";
                    return false;
                }

                totalStudents += group.StudentsCount;
            }

            if (totalStudents > room.Capacity)
            {
                reason = $"Аудиторія {BusyRoomLabel(room)} має {room.Capacity} місць для {totalStudents} слухачів у слоті {slotLabel}.";
                return false;
            }

            return true;
        }
        async Task<string?> FindPersistedPlacementConflictAsync(
            int moduleId,
            int lessonTypeId,
            int? moduleTopicId,
            int? teacherId,
            Room? room,
            DateOnly date,
            TimeOnly start,
            TimeOnly end,
            IReadOnlyCollection<int> newGroupIds)
        {
            var newGroupSet = newGroupIds
                .Where(selectedGroupsById.ContainsKey)
                .Distinct()
                .ToHashSet();
            if (newGroupSet.Count == 0)
            {
                return $"Немає вибраних груп для розміщення у слоті {date:yyyy-MM-dd} {start:HH\\:mm}-{end:HH\\:mm}.";
            }

            var roomId = room?.Id;
            var persistedDrafts = await _db.TeacherDraftItems
                .AsNoTracking()
                .Where(draft => draft.Status == DraftStatus.Draft
                                && draft.Date == date
                                && draft.StartTime < end
                                && start < draft.EndTime
                                && (newGroupSet.Contains(draft.GroupId)
                                    || (teacherId != null && draft.TeacherId == teacherId)
                                    || (roomId != null && draft.RoomId == roomId)))
                .Select(draft => new
                {
                    draft.GroupId,
                    draft.ModuleId,
                    draft.LessonTypeId,
                    draft.ModuleTopicId,
                    draft.TeacherId,
                    draft.RoomId,
                    draft.StartTime,
                    draft.EndTime
                })
                .ToListAsync(cancellationToken);
            var slotLabel = $"{date:yyyy-MM-dd} {start:HH\\:mm}-{end:HH\\:mm}";
            foreach (var draft in persistedDrafts)
            {
                var sameShareable = CanShareAcrossGroups(lessonTypeId)
                                    && draft.StartTime == start
                                    && draft.EndTime == end
                                    && draft.ModuleId == moduleId
                                    && draft.LessonTypeId == lessonTypeId
                                    && draft.ModuleTopicId == moduleTopicId
                                    && draft.TeacherId == teacherId
                                    && draft.RoomId == roomId;
                if (newGroupSet.Contains(draft.GroupId) && !sameShareable)
                {
                    return $"Група {BusyGroupLabel(draft.GroupId)} вже має збережену чернетку у слоті {slotLabel}.";
                }
                if (teacherId is int busyTeacherId
                    && draft.TeacherId == busyTeacherId
                    && BlocksTeacherSlot(lessonTypeId)
                    && BlocksTeacherSlot(draft.LessonTypeId)
                    && !sameShareable)
                {
                    return $"Викладач {BusyTeacherLabel(busyTeacherId)} вже має збережену чернетку у слоті {slotLabel}.";
                }
                if (room is not null
                    && draft.RoomId == room.Id
                    && BlocksRoomSlot(lessonTypeId)
                    && BlocksRoomSlot(draft.LessonTypeId)
                    && !sameShareable)
                {
                    return $"Аудиторія {BusyRoomLabel(room)} вже має збережену чернетку у слоті {slotLabel}.";
                }
            }

            if (room is not null && BlocksRoomSlot(lessonTypeId))
            {
                var roomSlotGroupIds = persistedDrafts
                    .Where(draft => draft.RoomId == room.Id
                                    && draft.StartTime == start
                                    && draft.EndTime == end
                                    && BlocksRoomSlot(draft.LessonTypeId))
                    .Select(draft => draft.GroupId)
                    .Concat(newGroupSet)
                    .Distinct()
                    .ToList();
                var totalStudents = 0;
                foreach (var groupId in roomSlotGroupIds)
                {
                    if (!selectedGroupsById.TryGetValue(groupId, out var group))
                    {
                        return $"Неможливо перевірити місткість аудиторії {BusyRoomLabel(room)} у слоті {slotLabel} для групи #{groupId}.";
                    }
                    totalStudents += group.StudentsCount;
                }
                if (totalStudents > room.Capacity)
                {
                    return $"Аудиторія {BusyRoomLabel(room)} має {room.Capacity} місць для {totalStudents} слухачів у слоті {slotLabel}.";
                }
            }

            return null;
        }
        // Чим більше додаткових груп в одному потоці, тим сильніше пріоритет кандидата.
        int SharedLectureCandidatePriority(PlacementCandidate candidate)
            => candidate.IsSelfStudy || !CanShareAcrossGroups(candidate.LessonTypeId)
                ? 0
                : Math.Max(0, candidate.TotalSharedGroupCount - 1);
        // Перевірка доступності конкретної теми для групи з урахуванням лімітів використання.
        bool CanAssignSpecificTopic(int groupIdCheck, int moduleIdCheck, ModuleTopic topic)
        {
            var nextTopic = SelectNextTopicInOrder(groupIdCheck, moduleIdCheck);
            return nextTopic is not null && nextTopic.Id == topic.Id;
        }
        // Позначає тему як використану для конкретної групи.
        void MarkTopicUsed(int groupId, int moduleId, ModuleTopic topic)
        {
            var key = (groupId, moduleId);
            if (!topicAssignments.TryGetValue(key, out var assigned))
            {
                assigned = new Dictionary<int, int>();
                topicAssignments[key] = assigned;
            }
            assigned.TryGetValue(topic.Id, out var used);
            assigned[topic.Id] = used + 1;
        }
        // Відкочує використання теми після пробної перебудови дня.
        void UnmarkTopicUsed(int groupId, int moduleId, int topicId)
        {
            var key = (groupId, moduleId);
            if (!topicAssignments.TryGetValue(key, out var assigned)
                || !assigned.TryGetValue(topicId, out var used))
            {
                return;
            }
            if (used <= 1)
            {
                assigned.Remove(topicId);
                if (assigned.Count == 0)
                {
                    topicAssignments.Remove(key);
                }
                return;
            }
            assigned[topicId] = used - 1;
        }
        static int CompareSlotPosition(
            DateOnly leftDate,
            TimeOnly leftStart,
            TimeOnly leftEnd,
            DateOnly rightDate,
            TimeOnly rightStart,
            TimeOnly rightEnd)
        {
            var dateDiff = leftDate.DayNumber.CompareTo(rightDate.DayNumber);
            if (dateDiff != 0)
            {
                return dateDiff;
            }
            var startDiff = leftStart.CompareTo(rightStart);
            if (startDiff != 0)
            {
                return startDiff;
            }
            return leftEnd.CompareTo(rightEnd);
        }
        bool ViolatesTopicCalendarOrder(
            int groupIdCheck,
            int moduleIdCheck,
            ModuleTopic topic,
            DateOnly date,
            TimeOnly start,
            TimeOnly end)
        {
            var candidateCode = string.IsNullOrWhiteSpace(topic.TopicCode) ? null : topic.TopicCode.Trim();
            if (candidateCode is null && topic.Order <= 0)
            {
                return false;
            }
            foreach (var slot in TopicOrderForGroup(groupIdCheck).Where(x =>
                         x.ModuleId == moduleIdCheck
                         && x.ModuleTopicId is int))
            {
                if (slot.ModuleTopicId is not int existingTopicId)
                {
                    continue;
                }
                if (!topicById.TryGetValue(existingTopicId, out var existingTopic))
                {
                    continue;
                }
                var existingCode = string.IsNullOrWhiteSpace(existingTopic.TopicCode) ? null : existingTopic.TopicCode.Trim();
                if (existingCode is null && existingTopic.Order <= 0)
                {
                    continue;
                }
                var orderComparison = topic.Order.CompareTo(existingTopic.Order);
                if (orderComparison == 0 && candidateCode is not null && existingCode is not null)
                {
                    orderComparison = TeacherDraftsHelpers.CompareTopicCodes(candidateCode, existingCode);
                }
                var slotPosition = CompareSlotPosition(
                    slot.Date,
                    slot.StartTime,
                    slot.EndTime,
                    date,
                    start,
                    end);
                if (slotPosition < 0 && orderComparison < 0)
                {
                    return true;
                }
                if (slotPosition > 0 && orderComparison > 0)
                {
                    return true;
                }
            }
            return false;
        }
        // Перевіряє, чи вичерпано всі теми модуля для групи.
        bool TopicsDepleted(int groupIdCheck, int moduleIdCheck)
        {
            if (!topicsByModule.TryGetValue(moduleIdCheck, out var list) || list.Count == 0)
                return false;
            var key = (groupIdCheck, moduleIdCheck);
            topicAssignments.TryGetValue(key, out var assigned);
            return list.All(topic =>
            {
                var limit = GetTopicUsageLimit(topic);
                if (limit <= 0) return true;
                var usedCount = assigned != null && assigned.TryGetValue(topic.Id, out var count) ? count : 0;
                return usedCount >= limit;
            });
        }
        // Чи був той самий тип заняття вчора для цього модуля.
        ModuleTopic? SelectOverflowTopicInOrder(int groupIdCheck, int moduleIdCheck)
        {
            if (!softFill || !topicsByModule.TryGetValue(moduleIdCheck, out var list) || list.Count == 0)
                return null;
            var usableTopics = list
                .Where(topic => GetTopicUsageLimit(topic) > 0 && TypeAllowed(topic.LessonTypeId))
                .ToList();
            if (usableTopics.Count == 0)
                return null;
            topicAssignments.TryGetValue((groupIdCheck, moduleIdCheck), out var assigned);
            if (assigned is not null)
            {
                var latestUsed = usableTopics
                    .Where(topic => assigned.TryGetValue(topic.Id, out var usedCount) && usedCount > 0)
                    .LastOrDefault();
                if (latestUsed is not null)
                {
                    return latestUsed;
                }
            }
            return usableTopics.Last();
        }
        bool IsOverflowTopicUse(int groupIdCheck, int moduleIdCheck, ModuleTopic topic)
        {
            var limit = GetTopicUsageLimit(topic);
            if (limit <= 0)
                return false;
            return topicAssignments.TryGetValue((groupIdCheck, moduleIdCheck), out var assigned)
                   && assigned.TryGetValue(topic.Id, out var usedCount)
                   && usedCount >= limit;
        }
        ModuleTopic? SelectCalendarSafeOverflowTopic(
            int groupIdCheck,
            int moduleIdCheck,
            DateOnly date,
            TimeOnly start,
            TimeOnly end)
        {
            if (!softFill || !topicsByModule.TryGetValue(moduleIdCheck, out var list) || list.Count == 0)
            {
                return null;
            }

            return list
                .Where(topic => GetTopicUsageLimit(topic) > 0
                                && TypeAllowed(topic.LessonTypeId)
                                && !ViolatesTopicCalendarOrder(groupIdCheck, moduleIdCheck, topic, date, start, end))
                .LastOrDefault();
        }
        bool ModuleHasUsableTopics(int moduleIdCheck)
            => topicsByModule.TryGetValue(moduleIdCheck, out var moduleTopicsForCheck)
               && moduleTopicsForCheck.Any(topic => GetTopicUsageLimit(topic) > 0);
        bool HadSameLessonTypePreviousDay(int gid, int mid, int lessonTypeId, DateOnly date)
        {
            var prev = date.AddDays(-1);
            return BusyForGroupDate(gid, prev).Any(x => x.ModuleId == mid
                                              && x.LessonTypeId == lessonTypeId
                                              && !excludedTypeIds.Contains(x.LessonTypeId));
        }
        // Обирає тип заняття і тему (якщо є) з урахуванням правил.
        (int LessonTypeId, ModuleTopic? Topic) PickLessonType(int groupIdPick, int courseIdPick, int moduleIdPick, DateOnly date)
        {
            var topicCandidate = SelectNextTopicInOrder(groupIdPick, moduleIdPick);
            if (topicCandidate is not null && TypeAllowed(topicCandidate.LessonTypeId))
            {
                return (topicCandidate.LessonTypeId, topicCandidate);
            }
            if (ModuleHasUsableTopics(moduleIdPick))
            {
                var overflowTopic = SelectOverflowTopicInOrder(groupIdPick, moduleIdPick);
                return overflowTopic is not null
                    ? (overflowTopic.LessonTypeId, overflowTopic)
                    : (0, null);
            }
            if (!hasPreferred.Contains((groupIdPick, moduleIdPick))
                && preferredFirstTypeId != 0
                && TypeAllowed(preferredFirstTypeId)
                && !HadSameLessonTypePreviousDay(groupIdPick, moduleIdPick, preferredFirstTypeId, date))
            {
                return (preferredFirstTypeId, null);
            }
            var cycleCount = activeStudyTypes.Count == 0 ? 1 : activeStudyTypes.Count;
            for (int attempt = 0; attempt < cycleCount; attempt++)
            {
                var candidate = NextCyclicLessonTypeId();
                if (!TypeAllowed(candidate)) continue;
                if (HadSameLessonTypePreviousDay(groupIdPick, moduleIdPick, candidate, date) && cycleCount > 1)
                    continue;
                return (candidate, null);
            }
            var fallbackType = activeStudyTypes.FirstOrDefault()?.Id ?? preferredFirstTypeId;
            if (fallbackType != 0 && TypeAllowed(fallbackType))
            {
                return (fallbackType, null);
            }
            return (types.First().Id, null);
        }
        int PeekLessonTypeForDate(int groupIdPick, int courseIdPick, int moduleIdPick, DateOnly date)
        {
            var savedLtIndex = ltIndex;
            try
            {
                return PickLessonType(groupIdPick, courseIdPick, moduleIdPick, date).LessonTypeId;
            }
            finally
            {
                ltIndex = savedLtIndex;
            }
        }
        // Послідовність модулів для основного порядку в курсі.
        var sequenceItems = await _db.ModuleSequenceItems
            .Where(x => courseIds.Contains(x.CourseId))
            .OrderBy(x => x.Order)
            .Select(x => new SequenceItem(x.CourseId, x.ModuleId, x.GroupOrder, x.Order))
            .ToListAsync();
        // Мапа курс -> основна послідовність модулів.
        var mainSequenceByCourse = sequenceItems
            .GroupBy(x => x.CourseId)
            .ToDictionary(g => g.Key, g => g.ToList());
        // Мапа курс -> модулі-наповнювачі (filler).
        var fillerByCourse = await _db.ModuleFillers
            .Where(x => courseIds.Contains(x.CourseId))
            .GroupBy(x => x.CourseId)
            .ToDictionaryAsync(g => g.Key, g => g.Select(x => x.ModuleId).ToHashSet());
        // Формуємо порядок модулів з урахуванням основної послідовності та fillers.
        List<int> BuildCourseModuleOrder(int courseId, List<int> planModules)
        {
            var ordered = new List<int>();
            var seen = new HashSet<int>();
            var planSet = new HashSet<int>(planModules);
        if (mainSequenceByCourse.TryGetValue(courseId, out var mainSequence))
        {
            foreach (var entry in mainSequence)
            {
                var mid = entry.ModuleId;
                if (planSet.Contains(mid) && seen.Add(mid))
                {
                    ordered.Add(mid);
                }
            }
        }
            if (fillerByCourse.TryGetValue(courseId, out var fillerSet) && fillerSet.Count > 0)
            {
                foreach (var mid in planModules)
                {
                    if (fillerSet.Contains(mid) && seen.Add(mid))
                    {
                        ordered.Add(mid);
                    }
                }
            }
            foreach (var mid in planModules)
            {
                if (seen.Add(mid))
                {
                    ordered.Add(mid);
                }
            }
            return ordered;
        }
        // Кількість годин, які ще потрібно запланувати по групі/модулю.
        var remainingByGroupModule = new Dictionary<(int GroupId, int ModuleId), int>();
        // Групи по курсах, відсортовані для стабільних результатів.
        var allGroupsByCourse = await _db.Groups
            .Where(g => courseIds.Contains(g.CourseId))
            .GroupBy(g => g.CourseId)
            .ToDictionaryAsync(g => g.Key, g => g.OrderBy(x => x.Id).ToList());
        // Факт по модулях у чернетках (без службових типів).
        var factByGroupModule = await _db.TeacherDraftItems
            .Where(si => !excludedTypeIds.Contains(si.LessonTypeId) && courseIds.Contains(si.Group.CourseId))
            .GroupBy(si => new { si.GroupId, si.ModuleId })
            .Select(g => new { g.Key.GroupId, g.Key.ModuleId, C = g.Count() })
            .ToListAsync();
        // Перетворюємо факт у словник для швидких доступів.
        var factMap = factByGroupModule.ToDictionary(k => (k.GroupId, k.ModuleId), v => v.C);
        // Факт по модулях у вже опублікованому розкладі.
        var scheduleByGroupModule = await _db.ScheduleItems
            .Where(si => !excludedTypeIds.Contains(si.LessonTypeId) && courseIds.Contains(si.Group.CourseId))
            .GroupBy(si => new { si.GroupId, si.ModuleId })
            .Select(g => new { g.Key.GroupId, g.Key.ModuleId, C = g.Count() })
            .ToListAsync();
        foreach (var item in scheduleByGroupModule)
        {
            var key = (item.GroupId, item.ModuleId);
            if (factMap.TryGetValue(key, out var existing))
            {
                factMap[key] = existing + item.C;
            }
            else
            {
                factMap[key] = item.C;
            }
        }
        // Факт поточного діапазону потрібен для дозаповнення, щоб не перевищувати ручні ліміти модулів.
        var rangeFactByGroupModule = await _db.TeacherDraftItems
            .Where(si => !excludedTypeIds.Contains(si.LessonTypeId)
                         && si.Date >= rangeStartDate
                         && si.Date < rangeEndDateExclusive
                         && courseIds.Contains(si.Group.CourseId))
            .GroupBy(si => new { si.GroupId, si.ModuleId })
            .Select(g => new { g.Key.GroupId, g.Key.ModuleId, C = g.Count() })
            .ToListAsync();
        var rangeFactMap = rangeFactByGroupModule.ToDictionary(k => (k.GroupId, k.ModuleId), v => v.C);
        var rangeScheduleByGroupModule = await _db.ScheduleItems
            .Where(si => !excludedTypeIds.Contains(si.LessonTypeId)
                         && si.Date >= rangeStartDate
                         && si.Date < rangeEndDateExclusive
                         && courseIds.Contains(si.Group.CourseId))
            .GroupBy(si => new { si.GroupId, si.ModuleId })
            .Select(g => new { g.Key.GroupId, g.Key.ModuleId, C = g.Count() })
            .ToListAsync();
        foreach (var item in rangeScheduleByGroupModule)
        {
            var key = (item.GroupId, item.ModuleId);
            if (rangeFactMap.TryGetValue(key, out var existing))
            {
                rangeFactMap[key] = existing + item.C;
            }
            else
            {
                rangeFactMap[key] = item.C;
            }
        }
        int CurrentRangeFactFor(int gid, int mid) =>
            rangeFactMap.TryGetValue((gid, mid), out var used) ? used : 0;
        void AddCurrentRangeFact(int gid, int mid)
        {
            var key = (gid, mid);
            rangeFactMap[key] = CurrentRangeFactFor(gid, mid) + 1;
        }
        void RemoveCurrentRangeFact(int gid, int mid)
        {
            var key = (gid, mid);
            var current = CurrentRangeFactFor(gid, mid);
            if (current <= 1)
            {
                rangeFactMap.Remove(key);
                return;
            }
            rangeFactMap[key] = current - 1;
        }
        // Залишки самостійної роботи по групі/модулю та по конкретних темах.
        var selfStudyRemainingByGroupModule = new Dictionary<(int GroupId, int ModuleId), int>();
        var selfStudyTopicRemaining = new Dictionary<(int GroupId, int ModuleId, int TopicId), int>();
        // Якщо задано ручні години по модулях - беремо їх як базу.
        if (hasModuleHourOverrides)
        {
            // Стабільний порядок модулів та груп для рівномірного розподілу.
            var orderedSelectedModules = moduleHoursByModuleId.Keys.OrderBy(mid => mid).ToList();
            var selectedGroups = groups.OrderBy(g => g.Id).ToList();
            foreach (var moduleId in orderedSelectedModules)
            {
                if (interAssemblyOnlyModules.Contains(moduleId))
                {
                    foreach (var grpRow in selectedGroups)
                    {
                        remainingByGroupModule[(grpRow.Id, moduleId)] = 0;
                    }
                    var moduleLabel = ModuleTitleLabel(moduleId);
                    var groupList = string.Join(", ", selectedGroups.Select(g => g.Name));
                    warnings.Add($"Модуль \"{moduleLabel}\" містить лише міжзборові теми, тому автогенерація пропускає його для груп: {groupList}.");
                    continue;
                }
                // Розподіляємо однакову кількість годин по всіх вибраних групах.
                var hours = moduleHoursByModuleId[moduleId];
                foreach (var grpRow in selectedGroups)
                {
                    var currentRangeFact = CurrentRangeFactFor(grpRow.Id, moduleId);
                    remainingByGroupModule[(grpRow.Id, moduleId)] = Math.Max(0, hours - currentRangeFact);
                }
                if (!selfStudyTopicsByModule.TryGetValue(moduleId, out var ssTopics) || ssTopics.Count == 0)
                    continue;
                foreach (var grpRow in selectedGroups)
                {
                    var total = 0;
                    foreach (var topic in ssTopics)
                    {
                        var key = (grpRow.Id, moduleId, topic.Id);
                        var factPerTopic = selfStudyTopicAssignments.TryGetValue(key, out var used) ? used : 0;
                        var remaining = Math.Max(0, topic.SelfStudyHours - factPerTopic);
                        if (remaining > 0)
                        {
                            selfStudyTopicRemaining[key] = remaining;
                            total += remaining;
                        }
                    }
                    if (total > 0)
                    {
                        selfStudyRemainingByGroupModule[(grpRow.Id, moduleId)] = total;
                    }
                }
            }
        }
        else
        {
            // Розподіляємо години згідно з активними планами модулів.
            foreach (var plan in activePlans)
        {
            if (!allGroupsByCourse.TryGetValue(plan.CourseId, out var courseGroups) || courseGroups.Count == 0)
                continue;
            if (interAssemblyOnlyModules.Contains(plan.ModuleId))
            {
                foreach (var grpRow in courseGroups)
                {
                    remainingByGroupModule[(grpRow.Id, plan.ModuleId)] = 0;
                }
                var moduleLabel = ModuleTitleLabel(plan.ModuleId);
                var groupList = string.Join(", ", courseGroups.Select(g => g.Name));
                warnings.Add($"Модуль \"{moduleLabel}\" містить лише міжзборові теми, тому автогенерація пропускає його для груп: {groupList}.");
                continue;
            }
            // Вираховуємо години, що підуть у самостійне навчання без викладача.
            var excludedSelfStudy = topicsByModule.TryGetValue(plan.ModuleId, out var tlist)
                ? tlist.Where(t => t.SelfStudyHours > 0 && !t.SelfStudyBySupervisor).Sum(t => Math.Max(0, t.SelfStudyHours))
                : 0;
            // Чисті години, які потрібно запланувати в аудиторіях або під керівництвом.
            var effectiveTargetHours = Math.Max(0, plan.TargetHours - excludedSelfStudy);
            int n = courseGroups.Count;
            // Рівномірно розподіляємо години між групами, залишок - по перших групах.
            int baseShare = effectiveTargetHours / n;
            int extra = effectiveTargetHours % n;
            var moduleMinHours =
                (moduleAuditoriumHours.TryGetValue(plan.ModuleId, out var minHours) ? minHours : 0)
                + (moduleSelfStudyHours.TryGetValue(plan.ModuleId, out var ssHours) ? ssHours : 0);
            for (int i = 0; i < n; i++)
            {
                int gid = courseGroups[i].Id;
                int planShare = baseShare + (i < extra ? 1 : 0);
                int target = Math.Max(planShare, moduleMinHours);
                int fact = factMap.TryGetValue((gid, plan.ModuleId), out var c) ? c : 0;
                remainingByGroupModule[(gid, plan.ModuleId)] = Math.Max(0, target - fact);
            }
            if (!selfStudyTopicsByModule.TryGetValue(plan.ModuleId, out var ssTopics) || ssTopics.Count == 0)
                continue;
            foreach (var grpRow in courseGroups)
            {
                var total = 0;
                foreach (var topic in ssTopics)
                {
                    var key = (grpRow.Id, plan.ModuleId, topic.Id);
                    var factPerTopic = selfStudyTopicAssignments.TryGetValue(key, out var used) ? used : 0;
                    var remaining = Math.Max(0, topic.SelfStudyHours - factPerTopic);
                    if (remaining > 0)
                    {
                        selfStudyTopicRemaining[key] = remaining;
                        total += remaining;
                    }
                }
                if (total > 0)
                {
                    selfStudyRemainingByGroupModule[(grpRow.Id, plan.ModuleId)] = total;
                }
            }
        }
        }
        // Доступ до залишків годин по групі/модулю.
        int RemainingFor(int gid, int mid) =>
            remainingByGroupModule.TryGetValue((gid, mid), out var left) ? left : 0;
        int ManualRangeRemainingFor(int gid, int mid) =>
            hasModuleHourOverrides && moduleHoursByModuleId.TryGetValue(mid, out var hours)
                ? Math.Max(0, hours - CurrentRangeFactFor(gid, mid))
                : RemainingFor(gid, mid);
        int PlacementRemainingFor(int gid, int mid) =>
            RemainingFor(gid, mid);
        // Доступ до залишків самостійної роботи.
        int SelfStudyRemaining(int gid, int mid) =>
            selfStudyRemainingByGroupModule.TryGetValue((gid, mid), out var left) ? left : 0;
        // Обирає тему самостійної роботи з доступним лімітом.
        ModuleTopic? PeekSelfStudyTopic(int gid, int mid)
        {
            if (!selfStudyTopicsByModule.TryGetValue(mid, out var list) || list.Count == 0)
                return null;
            foreach (var t in list)
            {
                var key = (gid, mid, t.Id);
                if (selfStudyTopicRemaining.TryGetValue(key, out var left) && left > 0)
                {
                    return t;
                }
            }
            return null;
        }
        var sharedTimeSlots = await _db.TimeSlots.AsNoTracking()
            .Where(s => s.IsActive && (s.CourseId == null || courseIds.Contains(s.CourseId.Value)))
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Start)
            .ToListAsync();
        var sharedSlotsByCourse = courseIds.ToDictionary(
            cid => cid,
            cid => TimeSlotsResolver.ResolveForWeek(sharedTimeSlots, cid));

        bool RoomMatchesGroupPreferenceFor(int groupId, Room room)
        {
            if (!groupRoomPreferencesByGroupId.TryGetValue(groupId, out var pref))
            {
                return true;
            }
            if (pref.BuildingId is int preferredBuildingId && room.BuildingId != preferredBuildingId)
            {
                return false;
            }
            return pref.RoomIds.Count == 0 || pref.RoomIds.Contains(room.Id);
        }

        bool ViolatesSharedLectureTravel(Func<BusySlot, bool> ownerMatch, Room room, DateOnly date, TimeOnly start, TimeOnly end)
        {
            if (room.BuildingId == 0)
            {
                return false;
            }
            foreach (var existing in BusyForDate(date).Where(existing =>
                         existing.RoomId != null
                         && existing.BuildingId.HasValue
                         && ownerMatch(existing)))
            {
                var sourceBuildingId = existing.BuildingId!.Value;
                if (sourceBuildingId == room.BuildingId)
                {
                    continue;
                }
                var needMinutes = TravelMinutes(sourceBuildingId, room.BuildingId);
                var gapBefore = (start.ToTimeSpan() - existing.EndTime.ToTimeSpan()).TotalMinutes;
                var gapAfter = (existing.StartTime.ToTimeSpan() - end.ToTimeSpan()).TotalMinutes;
                if (existing.EndTime <= start && gapBefore < needMinutes)
                {
                    return true;
                }
                if (end <= existing.StartTime && gapAfter < needMinutes)
                {
                    return true;
                }
            }
            return false;
        }

        IReadOnlyList<TimeSlot> SharedSlotsForDate(int courseIdValue, DateOnly date)
        {
            var dayOfWeek = date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
            return sharedSlotsByCourse.TryGetValue(courseIdValue, out var resolvedByDay)
                   && resolvedByDay.TryGetValue(dayOfWeek, out var resolved)
                ? resolved.Slots
                : Array.Empty<TimeSlot>();
        }

        List<Room> CandidateRoomsForPreflight(int groupId, int moduleId, int requiredCapacity)
        {
            allowedRoomsByModule.TryGetValue(moduleId, out var allowedRooms);
            allowedBuildingsByModule.TryGetValue(moduleId, out var allowedBuildings);
            return roomsAll
                .Where(room => (allowedBuildings == null || allowedBuildings.Count == 0 || allowedBuildings.Contains(room.BuildingId))
                               && (allowedRooms == null || allowedRooms.Count == 0 || allowedRooms.Contains(room.Id))
                               && RoomMatchesGroupPreferenceFor(groupId, room)
                               && room.Capacity >= requiredCapacity)
                .OrderBy(room => room.Capacity)
                .ThenBy(room => room.Id)
                .ToList();
        }

        string FormatPreflightIds(IEnumerable<int> ids)
        {
            var values = ids
                .Distinct()
                .OrderBy(id => id)
                .Take(5)
                .Select(id => $"#{id}")
                .ToList();
            return values.Count == 0 ? "немає" : string.Join(", ", values);
        }

        string BuildRoomPreflightContext(int groupId, int moduleId, int requiredCapacity)
        {
            var details = new List<string>();
            if (allowedBuildingsByModule.TryGetValue(moduleId, out var allowedBuildings) && allowedBuildings.Count > 0)
            {
                details.Add($"дозволені корпуси модуля: {FormatPreflightIds(allowedBuildings)}");
            }
            if (allowedRoomsByModule.TryGetValue(moduleId, out var allowedRooms) && allowedRooms.Count > 0)
            {
                details.Add($"дозволені аудиторії модуля: {FormatPreflightIds(allowedRooms)}");
            }
            if (groupRoomPreferencesByGroupId.TryGetValue(groupId, out var pref))
            {
                if (pref.BuildingId is int preferredBuildingId)
                {
                    details.Add($"пріоритетний корпус групи: #{preferredBuildingId}");
                }
                if (pref.RoomIds.Count > 0)
                {
                    details.Add($"пріоритетні аудиторії групи: {FormatPreflightIds(pref.RoomIds)}");
                }
            }
            var unrestrictedCapacityCount = roomsAll.Count(room => room.Capacity >= requiredCapacity);
            if (unrestrictedCapacityCount > 0)
            {
                details.Add($"аудиторій потрібної місткості без цих обмежень: {unrestrictedCapacityCount}");
            }
            return details.Count == 0 ? string.Empty : $" Контекст: {string.Join("; ", details)}.";
        }

        List<AutoGenPreflightItem> BuildAutoGenPreflight()
        {
            var items = new List<AutoGenPreflightItem>();
            void AddItem(string code, string title, int count, string recommendation, string example)
            {
                items.Add(new AutoGenPreflightItem(
                    code,
                    title,
                    Math.Max(1, count),
                    recommendation,
                    string.IsNullOrWhiteSpace(example) ? new List<string>() : new List<string> { example }));
            }

            var rangeDates = Enumerable.Range(0, Math.Max(0, rangeEndDateExclusive.DayNumber - rangeStartDate.DayNumber))
                .Select(offset => rangeStartDate.AddDays(offset))
                .ToList();
            foreach (var entry in remainingByGroupModule
                         .Where(kv => kv.Value > 0)
                         .OrderBy(kv => kv.Key.GroupId)
                         .ThenBy(kv => kv.Key.ModuleId))
            {
                if (!selectedGroupsById.TryGetValue(entry.Key.GroupId, out var group))
                {
                    continue;
                }
                var moduleId = entry.Key.ModuleId;
                var required = entry.Value;
                var moduleLabel = ModuleTitleLabel(moduleId);
                var examplePrefix = $"{group.Name}, {moduleLabel}";
                var workingDates = rangeDates
                    .Where(date => IsWorking(date, group))
                    .ToList();
                var openSlotCount = workingDates
                    .SelectMany(date => SharedSlotsForDate(group.CourseId, date).Select(slot => (date, slot)))
                    .Count(pair => !HasGroupOverlap(group.Id, pair.date, pair.slot.Start, pair.slot.End));
                if (openSlotCount < required)
                {
                    var missingSlots = required - openSlotCount;
                    AddItem(
                        "slot",
                        "Недостатньо відкритих слотів",
                        missingSlots,
                        $"Для групи {group.Name} бракує {missingSlots} вільних пар у вибраному діапазоні. Додайте навчальний час або звільніть уже зайняті пари цієї групи.",
                        $"{examplePrefix}: потрібно {required}, відкрито {openSlotCount}, бракує {missingSlots}.");
                }

                var nextTopic = SelectNextTopicInOrder(group.Id, moduleId);
                if (topicsByModule.TryGetValue(moduleId, out var moduleTopics) && moduleTopics.Count > 0)
                {
                    topicAssignments.TryGetValue((group.Id, moduleId), out var assignedTopics);
                    var topicCapacity = moduleTopics
                        .Where(topic => GetTopicUsageLimit(topic) > 0 && TypeAllowed(topic.LessonTypeId))
                        .Sum(topic =>
                        {
                            var used = assignedTopics is not null && assignedTopics.TryGetValue(topic.Id, out var usedCount)
                                ? usedCount
                                : 0;
                            return Math.Max(0, GetTopicUsageLimit(topic) - used);
                        });
                    if (topicCapacity <= 0)
                    {
                        AddItem(
                            "topic-order",
                            "Немає доступних тем модуля",
                            required,
                            $"Додайте аудиторні теми для модуля <{moduleLabel}> або перевірте порядок/типи тем: зараз жодна тема не дає слота для групи {group.Name}.",
                            $"{examplePrefix}: усі теми вичерпані або недоступні.");
                    }
                    else if (topicCapacity < required && !softFill)
                    {
                        var missingTopicHours = required - topicCapacity;
                        AddItem(
                            "topic-order",
                            "Тем модуля менше, ніж потрібно годин",
                            missingTopicHours,
                            $"Для модуля <{moduleLabel}> бракує тем на {missingTopicHours} аудиторних годин. Додайте теми потрібного типу або зменште години модуля в цьому діапазоні.",
                            $"{examplePrefix}: потрібно {required}, тем вистачає на {topicCapacity}, бракує {missingTopicHours}.");
                    }
                }

                var teacherIds = teachersForModule
                    .Where(link => link.ModuleId == moduleId)
                    .Select(link => link.TeacherId)
                    .Concat(supervisorsForModule
                        .Where(link => link.ModuleId == moduleId)
                        .Select(link => link.TeacherId))
                    .Distinct()
                    .ToList();
                if (nextTopic?.DepartmentId is int departmentId && departmentId > 0)
                {
                    teacherIds = teacherIds
                        .Where(teacherId => teacherDepartmentById.TryGetValue(teacherId, out var teacherDepartmentId)
                                            && teacherDepartmentId == departmentId)
                        .ToList();
                }
                if (teacherIds.Count == 0)
                {
                    var departmentHint = nextTopic?.DepartmentId is int topicDepartmentId
                        && departmentNames.TryGetValue(topicDepartmentId, out var departmentName)
                        ? $" кафедри <{departmentName}>"
                        : string.Empty;
                    AddItem(
                        "teacher",
                        "Немає викладачів для модуля",
                        required,
                        $"Додайте викладача{departmentHint} до модуля <{moduleLabel}>, перевірте кафедру теми або призначте керівника самостійної роботи.",
                        $"{examplePrefix}: потрібно {required} викладацьких слотів, не знайдено жодного викладача.");
                }
                else
                {
                    var teacherSlotCapacity = 0;
                    foreach (var date in workingDates)
                    {
                        foreach (var slot in SharedSlotsForDate(group.CourseId, date))
                        {
                            foreach (var teacherId in teacherIds)
                            {
                                if (!TeacherFitsWorkingHours(teacherId, date, slot.Start, slot.End))
                                {
                                    continue;
                                }
                                if (HasTeacherOverlap(teacherId, date, slot.Start, slot.End))
                                {
                                    continue;
                                }
                                teacherSlotCapacity++;
                                break;
                            }
                            if (teacherSlotCapacity >= required)
                            {
                                break;
                            }
                        }
                        if (teacherSlotCapacity >= required)
                        {
                            break;
                        }
                    }
                    if (teacherSlotCapacity < required)
                    {
                        var missingTeacherSlots = required - teacherSlotCapacity;
                        AddItem(
                            "teacher",
                            "Мало вільних слотів викладачів",
                            missingTeacherSlots,
                            $"Для модуля <{moduleLabel}> у групі {group.Name} бракує {missingTeacherSlots} вільних викладацьких пар. Перевірте робочі години, зайнятість і прив'язку викладачів.",
                            $"{examplePrefix}: потрібно {required}, знайдено {teacherSlotCapacity}, бракує {missingTeacherSlots} викладацьких слотів.");
                    }
                }

                var roomCandidates = CandidateRoomsForPreflight(group.Id, moduleId, group.StudentsCount);
                if (roomCandidates.Count == 0)
                {
                    var code = groupRoomPreferencesByGroupId.ContainsKey(group.Id)
                        || allowedBuildingsByModule.TryGetValue(moduleId, out var allowedBuildingsForModule) && allowedBuildingsForModule.Count > 0
                        ? "building"
                        : "room";
                    AddItem(
                        code,
                        code == "building" ? "Корпуси або пріоритети не дають аудиторій" : "Немає аудиторій потрібної місткості",
                        required,
                        code == "building"
                            ? $"Розширте дозволені корпуси/пріоритети для групи {group.Name} або додайте аудиторії місткістю від {group.StudentsCount} місць у дозволений корпус."
                            : $"Додайте аудиторію місткістю від {group.StudentsCount} місць або зменште обмеження аудиторій для модуля <{moduleLabel}>.",
                        $"{examplePrefix}: потрібно місць {group.StudentsCount}.{BuildRoomPreflightContext(group.Id, moduleId, group.StudentsCount)}");
                }
                else
                {
                    var roomSlotCapacity = 0;
                    var roomSlotCapacityBeforeTravel = 0;
                    foreach (var date in workingDates)
                    {
                        foreach (var slot in SharedSlotsForDate(group.CourseId, date))
                        {
                            var hasRoomBeforeTravel = false;
                            var hasReachableRoom = false;
                            foreach (var room in roomCandidates)
                            {
                                if (HasRoomOverlap(room.Id, date, slot.Start, slot.End))
                                {
                                    continue;
                                }
                                hasRoomBeforeTravel = true;
                                if (ViolatesSharedLectureTravel(existing => existing.GroupId == group.Id, room, date, slot.Start, slot.End))
                                {
                                    continue;
                                }
                                hasReachableRoom = true;
                                break;
                            }
                            if (hasRoomBeforeTravel)
                            {
                                roomSlotCapacityBeforeTravel++;
                            }
                            if (hasReachableRoom)
                            {
                                roomSlotCapacity++;
                            }
                            if (roomSlotCapacity >= required)
                            {
                                break;
                            }
                        }
                        if (roomSlotCapacity >= required)
                        {
                            break;
                        }
                    }
                    if (roomSlotCapacityBeforeTravel >= required && roomSlotCapacity < required)
                    {
                        var missingReachableRoomSlots = required - roomSlotCapacity;
                        var travelFilteredSlots = Math.Max(0, roomSlotCapacityBeforeTravel - roomSlotCapacity);
                        var candidateBuildings = roomCandidates.Select(room => room.BuildingId).Where(buildingId => buildingId != 0);
                        AddItem(
                            "travel",
                            "Переходи між корпусами звужують вибір",
                            missingReachableRoomSlots,
                            $"Бракує {missingReachableRoomSlots} досяжних аудиторних слотів: додайте аудиторії в корпусах {FormatPreflightIds(candidateBuildings)}, збільшіть перерви між корпусами або рознесіть сусідні заняття.",
                            $"{examplePrefix}: до переходів {roomSlotCapacityBeforeTravel}, після переходів {roomSlotCapacity}, переходи відсіяли {travelFilteredSlots} варіантів.");
                    }
                    else if (roomSlotCapacity < required)
                    {
                        var missingRoomSlots = required - roomSlotCapacity;
                        AddItem(
                            "room",
                            "Мало вільних аудиторних слотів",
                            missingRoomSlots,
                            $"Для модуля <{moduleLabel}> бракує {missingRoomSlots} вільних аудиторних пар. Потрібні аудиторії від {group.StudentsCount} місць.",
                            $"{examplePrefix}: потрібно {required}, знайдено {roomSlotCapacity}, бракує {missingRoomSlots} аудиторних слотів.");
                    }
                }
            }

            return MergeAutoGenPreflight(items);
        }

        bool IsTopicStillPendingForGroup(int groupIdCheck, int moduleIdCheck, ModuleTopic topic)
        {
            if (!topicsByModule.TryGetValue(moduleIdCheck, out var moduleTopics)
                || !moduleTopics.Any(candidate => candidate.Id == topic.Id))
            {
                return false;
            }
            topicAssignments.TryGetValue((groupIdCheck, moduleIdCheck), out var assigned);
            var usedCount = assigned is not null && assigned.TryGetValue(topic.Id, out var used) ? used : 0;
            return usedCount < GetTopicUsageLimit(topic);
        }

        bool HasRoomForPendingSharedLecturePack(int moduleId, IReadOnlyList<Group> pendingGroups)
        {
            if (pendingGroups.Count < 2)
            {
                return false;
            }

            allowedRoomsByModule.TryGetValue(moduleId, out var allowedRooms);
            allowedBuildingsByModule.TryGetValue(moduleId, out var allowedBuildings);
            foreach (var room in roomsAll.OrderByDescending(room => room.Capacity).ThenBy(room => room.Id))
            {
                if (allowedBuildings is { Count: > 0 } && !allowedBuildings.Contains(room.BuildingId))
                {
                    continue;
                }
                if (allowedRooms is { Count: > 0 } && !allowedRooms.Contains(room.Id))
                {
                    continue;
                }

                var totalStudents = 0;
                var packedCount = 0;
                foreach (var group in pendingGroups.OrderBy(group => group.StudentsCount).ThenBy(group => group.Id))
                {
                    if (!RoomMatchesGroupPreferenceFor(group.Id, room))
                    {
                        continue;
                    }
                    if (totalStudents + group.StudentsCount > room.Capacity)
                    {
                        continue;
                    }

                    totalStudents += group.StudentsCount;
                    packedCount++;
                    if (packedCount >= 2)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        bool HasPendingSharedLectureCatchUpBeforeTopic(
            int groupIdCheck,
            int courseIdCheck,
            int moduleIdCheck,
            ModuleTopic candidateTopic,
            out ModuleTopic? pendingTopic)
        {
            pendingTopic = null;
            if (CanShareAcrossGroups(candidateTopic.LessonTypeId)
                || !topicsByModule.TryGetValue(moduleIdCheck, out var moduleTopics)
                || !selectedGroupsByCourse.TryGetValue(courseIdCheck, out var sameCourseGroups)
                || sameCourseGroups.Count <= 1)
            {
                return false;
            }

            var candidateIndex = moduleTopics.FindIndex(topic => topic.Id == candidateTopic.Id);
            if (candidateIndex <= 0)
            {
                return false;
            }

            var previousShareableTopic = moduleTopics
                .Take(candidateIndex)
                .Reverse()
                .FirstOrDefault(topic => GetTopicUsageLimit(topic) > 0 && CanShareAcrossGroups(topic.LessonTypeId));
            if (previousShareableTopic is not null)
            {
                var pendingGroups = sameCourseGroups
                    .Where(group => group.Id != groupIdCheck
                                    && PlacementRemainingFor(group.Id, moduleIdCheck) > 0
                                    && IsTopicStillPendingForGroup(group.Id, moduleIdCheck, previousShareableTopic))
                    .OrderBy(group => group.StudentsCount)
                    .ThenBy(group => group.Id)
                    .ToList();
                if (HasRoomForPendingSharedLecturePack(moduleIdCheck, pendingGroups))
                {
                    pendingTopic = previousShareableTopic;
                    return true;
                }
            }

            return false;
        }

        bool HasUnreadySelectedGroupForShareableTopic(int courseIdCheck, int moduleIdCheck, ModuleTopic topic)
        {
            if (!CanShareAcrossGroups(topic.LessonTypeId)
                || !selectedGroupsByCourse.TryGetValue(courseIdCheck, out var sameCourseGroups)
                || sameCourseGroups.Count <= 1)
            {
                return false;
            }

            foreach (var group in sameCourseGroups)
            {
                if (PlacementRemainingFor(group.Id, moduleIdCheck) <= 0)
                {
                    continue;
                }

                if (!IsTopicStillPendingForGroup(group.Id, moduleIdCheck, topic))
                {
                    continue;
                }

                if (!CanAssignSpecificTopic(group.Id, moduleIdCheck, topic))
                {
                    return true;
                }
            }

            return false;
        }

        bool ShouldHoldShareableTopicForMissingPendingGroups(
            int courseIdCheck,
            int moduleIdCheck,
            ModuleTopic topic,
            IReadOnlyCollection<int> currentGroupIds)
        {
            if (!CanShareAcrossGroups(topic.LessonTypeId)
                || !selectedGroupsByCourse.TryGetValue(courseIdCheck, out var sameCourseGroups)
                || sameCourseGroups.Count <= 1)
            {
                return false;
            }

            var currentGroupSet = currentGroupIds.ToHashSet();
            var pendingGroups = sameCourseGroups
                .Where(group => PlacementRemainingFor(group.Id, moduleIdCheck) > 0)
                .Where(group => IsTopicStillPendingForGroup(group.Id, moduleIdCheck, topic))
                .ToList();
            if (pendingGroups.Count <= currentGroupSet.Count)
            {
                return false;
            }

            var pendingStudents = pendingGroups.Sum(group => group.StudentsCount);
            allowedRoomsByModule.TryGetValue(moduleIdCheck, out var allowedRooms);
            allowedBuildingsByModule.TryGetValue(moduleIdCheck, out var allowedBuildings);
            var largestCompatibleRoomCapacity = roomsAll
                .Where(candidateRoom =>
                    (allowedBuildings == null || allowedBuildings.Count == 0 || allowedBuildings.Contains(candidateRoom.BuildingId))
                    && (allowedRooms == null || allowedRooms.Count == 0 || allowedRooms.Contains(candidateRoom.Id))
                    && pendingGroups.All(group => RoomMatchesGroupPreferenceFor(group.Id, candidateRoom)))
                .Select(candidateRoom => candidateRoom.Capacity)
                .DefaultIfEmpty(0)
                .Max();
            return pendingStudents <= largestCompatibleRoomCapacity;
        }

        List<int> BuildPotentialSharedLectureGroupPack(
            IReadOnlyList<Group> courseGroups,
            int moduleId,
            ModuleTopic topic,
            Room room,
            DateOnly date,
            TimeSlot slot)
        {
            var result = new List<int>();
            var totalStudents = 0;
            foreach (var group in courseGroups.OrderBy(g => g.StudentsCount).ThenBy(g => g.Id))
            {
                if (!IsWorking(date, group))
                {
                    continue;
                }
                var slotsForDate = SharedSlotsForDate(group.CourseId, date);
                if (slotsForDate.Count == 0 || CountFor(group.Id, date) >= slotsForDate.Count)
                {
                    continue;
                }
                if (PlacementRemainingFor(group.Id, moduleId) <= 0)
                {
                    continue;
                }
                if (!IsTopicStillPendingForGroup(group.Id, moduleId, topic))
                {
                    continue;
                }
                if (!RoomMatchesGroupPreferenceFor(group.Id, room))
                {
                    continue;
                }
                if (totalStudents + group.StudentsCount > room.Capacity)
                {
                    continue;
                }
                var groupBusy = HasGroupOverlap(group.Id, date, slot.Start, slot.End);
                if (groupBusy)
                {
                    continue;
                }
                if (ViolatesSharedLectureTravel(x => x.GroupId == group.Id, room, date, slot.Start, slot.End))
                {
                    continue;
                }
                result.Add(group.Id);
                totalStudents += group.StudentsCount;
            }
            return result;
        }

        List<int> BuildSharedLectureGroupPack(
            IReadOnlyList<Group> courseGroups,
            int moduleId,
            ModuleTopic topic,
            Room room,
            DateOnly date,
            TimeSlot slot)
        {
            var result = new List<int>();
            var totalStudents = 0;
            foreach (var group in courseGroups.OrderBy(g => g.StudentsCount).ThenBy(g => g.Id))
            {
                if (!IsWorking(date, group))
                {
                    continue;
                }
                var slotsForDate = SharedSlotsForDate(group.CourseId, date);
                if (slotsForDate.Count == 0 || CountFor(group.Id, date) >= slotsForDate.Count)
                {
                    continue;
                }
                if (PlacementRemainingFor(group.Id, moduleId) <= 0)
                {
                    continue;
                }
                if (!softFill && TopicsDepleted(group.Id, moduleId))
                {
                    continue;
                }
                if (!CanAssignSpecificTopic(group.Id, moduleId, topic))
                {
                    continue;
                }
                if (ViolatesTopicCalendarOrder(group.Id, moduleId, topic, date, slot.Start, slot.End))
                {
                    continue;
                }
                if (!RoomMatchesGroupPreferenceFor(group.Id, room))
                {
                    continue;
                }
                if (totalStudents + group.StudentsCount > room.Capacity)
                {
                    continue;
                }
                var groupBusy = HasGroupOverlap(group.Id, date, slot.Start, slot.End);
                if (groupBusy)
                {
                    continue;
                }
                if (ViolatesSharedLectureTravel(x => x.GroupId == group.Id, room, date, slot.Start, slot.End))
                {
                    continue;
                }
                result.Add(group.Id);
                totalStudents += group.StudentsCount;
            }
            return result;
        }

        bool TryPreplaceSharedLectureTopic(int courseIdValue, int moduleId, ModuleTopic topic)
        {
            if (!TypeAllowed(topic.LessonTypeId)
                || !CanShareAcrossGroups(topic.LessonTypeId)
                || !selectedGroupsByCourse.TryGetValue(courseIdValue, out var courseGroups)
                || courseGroups.Count <= 1)
            {
                return false;
            }

            var lessonType = typeById[topic.LessonTypeId];
            if (!lessonType.RequiresRoom)
            {
                return false;
            }

            if (HasUnreadySelectedGroupForShareableTopic(courseIdValue, moduleId, topic))
            {
                return false;
            }

            var teacherIds = lessonType.RequiresTeacher
                ? teachersForModule
                    .Where(x => x.ModuleId == moduleId)
                    .Select(x => x.TeacherId)
                    .Distinct()
                    .Where(tid => topic.DepartmentId is not int departmentId
                                  || departmentId <= 0
                                  || (teacherDepartmentById.TryGetValue(tid, out var teacherDepartment) && teacherDepartment == departmentId))
                    .OrderBy(tid => TeacherLoadScore(tid, courseIdValue))
                    .ThenBy(tid => tid)
                    .Cast<int?>()
                    .ToList()
                : new List<int?> { null };
            if (teacherIds.Count == 0)
            {
                return false;
            }
            allowedRoomsByModule.TryGetValue(moduleId, out var allowedRooms);
            allowedBuildingsByModule.TryGetValue(moduleId, out var allowedBuildings);
            var roomCandidates = roomsAll
                .Where(room => (allowedBuildings == null || allowedBuildings.Count == 0 || allowedBuildings.Contains(room.BuildingId))
                               && (allowedRooms == null || allowedRooms.Count == 0 || allowedRooms.Contains(room.Id)))
                .OrderByDescending(room => room.Capacity)
                .ThenBy(room => room.Id)
                .ToList();
            if (roomCandidates.Count == 0)
            {
                return false;
            }

            var coursePreferredFirstSlotLimit = preferredFirstSlotLimitsAll.FirstOrDefault(x => x.CourseId == courseIdValue)
                                               ?? preferredFirstSlotLimitsAll.FirstOrDefault(x => x.CourseId == null);
            int? coursePreferredFirstMaxSlotOrder = r.PreferredFirstMaxSlotOrderOverride is int preferredFirstOverride
                ? preferredFirstOverride > 0 ? preferredFirstOverride : null
                : coursePreferredFirstSlotLimit is not null && coursePreferredFirstSlotLimit.MaxSlotOrder > 0
                    ? coursePreferredFirstSlotLimit.MaxSlotOrder
                    : preferredFirstTypeId != 0 ? 6 : null;

            int GetSharedSlotOrder(IReadOnlyList<TimeSlot> slotsForDate, TimeSlot slot)
            {
                var index = slotsForDate
                    .Select((candidate, candidateIndex) => new { candidate, candidateIndex })
                    .FirstOrDefault(x => x.candidate.Start == slot.Start && x.candidate.End == slot.End)
                    ?.candidateIndex;
                return index is int value ? value + 1 : int.MaxValue;
            }

            int PreferredFirstLectureLoadForCourseDate(DateOnly date)
            {
                if (preferredFirstTypeId == 0 || topic.LessonTypeId != preferredFirstTypeId)
                {
                    return 0;
                }
                return busy
                    .Where(slot => slot.Date == date
                                   && slot.LessonTypeId == preferredFirstTypeId
                                   && selectedGroupsById.TryGetValue(slot.GroupId, out var slotGroup)
                                   && slotGroup.CourseId == courseIdValue)
                    .GroupBy(slot => new { slot.ModuleId, slot.ModuleTopicId, slot.StartTime, slot.EndTime })
                    .Count();
            }
            bool JoinsExistingTopicOccurrence(DateOnly date, TimeSlot slot)
            {
                return BusyForDate(date).Any(existing => existing.StartTime == slot.Start
                                                        && existing.EndTime == slot.End
                                                        && existing.ModuleId == moduleId
                                                        && existing.ModuleTopicId == topic.Id
                                                        && selectedGroupsById.TryGetValue(existing.GroupId, out var existingGroup)
                                                        && existingGroup.CourseId == courseIdValue);
            }
            // Оцінює, наскільки слот продовжує вже розпочату тему без розриву іншими заняттями.
            int TopicContinuationDistance(DateOnly date, TimeSlot slot, IReadOnlyList<TimeSlot> slotsForDate)
            {
                var existingTopicSlots = busy
                    .Where(existing => existing.ModuleId == moduleId
                                       && existing.ModuleTopicId == topic.Id
                                       && selectedGroupsById.TryGetValue(existing.GroupId, out var existingGroup)
                                       && existingGroup.CourseId == courseIdValue)
                    .GroupBy(existing => new { existing.Date, existing.StartTime, existing.EndTime })
                    .Select(group => group.Key)
                    .ToList();
                if (existingTopicSlots.Count == 0)
                {
                    return 0;
                }

                var candidateIndex = slotsForDate
                    .Select((candidate, index) => new { candidate, index })
                    .FirstOrDefault(x => x.candidate.Start == slot.Start && x.candidate.End == slot.End)
                    ?.index;
                if (candidateIndex is int currentIndex)
                {
                    var sameDayDistances = existingTopicSlots
                        .Where(existing => existing.Date == date)
                        .Select(existing =>
                            slotsForDate
                                .Select((candidate, index) => new { candidate, index })
                                .FirstOrDefault(x => x.candidate.Start == existing.StartTime && x.candidate.End == existing.EndTime)
                                ?.index)
                        .Where(index => index.HasValue)
                        .Select(index => Math.Abs(currentIndex - index!.Value) - 1)
                        .ToList();
                    if (sameDayDistances.Count > 0)
                    {
                        return Math.Max(0, sameDayDistances.Min());
                    }
                }

                var nearestDayDistance = existingTopicSlots
                    .Select(existing => Math.Abs(existing.Date.DayNumber - date.DayNumber))
                    .DefaultIfEmpty(7)
                    .Min();
                return 100 + nearestDayDistance;
            }
            int SharedLectureGapTrapPenalty(DateOnly date, TimeSlot slot, IReadOnlyList<TimeSlot> slotsForDate, Room room, IReadOnlyList<int> groupIds)
            {
                if (room.BuildingId == 0 || groupIds.Count == 0)
                {
                    return 0;
                }
                var slotIndex = slotsForDate
                    .Select((candidate, index) => new { candidate, index })
                    .FirstOrDefault(x => x.candidate.Start == slot.Start && x.candidate.End == slot.End)
                    ?.index;
                if (slotIndex is not int currentIndex)
                {
                    return 0;
                }

                int CountTrappedGroups(TimeSlot adjacentSlot, bool adjacentBefore)
                {
                    var gapMinutes = adjacentBefore
                        ? (slot.Start.ToTimeSpan() - adjacentSlot.End.ToTimeSpan()).TotalMinutes
                        : (adjacentSlot.Start.ToTimeSpan() - slot.End.ToTimeSpan()).TotalMinutes;
                    var trapped = 0;
                    foreach (var groupId in groupIds)
                    {
                        if (HasGroupOverlap(groupId, date, adjacentSlot.Start, adjacentSlot.End))
                        {
                            continue;
                        }
                        if (!remainingByGroupModule.Any(kv => kv.Key.GroupId == groupId && kv.Value > 0))
                        {
                            continue;
                        }
                        var reachableBuildingCount = roomsAll
                            .Where(candidateRoom => candidateRoom.BuildingId != 0
                                                    && RoomMatchesGroupPreferenceFor(groupId, candidateRoom)
                                                    && !HasRoomOverlap(candidateRoom.Id, date, adjacentSlot.Start, adjacentSlot.End))
                            .Select(candidateRoom => candidateRoom.BuildingId)
                            .Distinct()
                            .Count(buildingId =>
                            {
                                var needMinutes = adjacentBefore
                                    ? TravelMinutes(buildingId, room.BuildingId)
                                    : TravelMinutes(room.BuildingId, buildingId);
                                return gapMinutes >= needMinutes;
                            });
                        if (reachableBuildingCount <= 1)
                        {
                            trapped++;
                        }
                    }
                    return trapped;
                }

                var penalty = 0;
                if (currentIndex > 0)
                {
                    penalty += CountTrappedGroups(slotsForDate[currentIndex - 1], adjacentBefore: true);
                }
                if (currentIndex + 1 < slotsForDate.Count)
                {
                    penalty += CountTrappedGroups(slotsForDate[currentIndex + 1], adjacentBefore: false);
                }
                return penalty;
            }
            int SharedLectureOrderConflictCount(DateOnly date, TimeSlot slot, IReadOnlyList<int> groupIds)
                => groupIds.Count(groupId => BusyForGroupDate(groupId, date).Any(existing =>
                    existing.StartTime < slot.Start
                    && !CanShareAcrossGroups(existing.LessonTypeId)
                    && !excludedTypeIds.Contains(existing.LessonTypeId)));

            bool ViolatesSharedModuleBlockSplit(DateOnly date, TimeSlot slot, IReadOnlyList<TimeSlot> slotsForDate, IReadOnlyList<int> groupIds)
            {
                foreach (var groupId in groupIds)
                {
                    var orderedLessons = BusyForGroupDate(groupId, date)
                        .Where(existing => !excludedTypeIds.Contains(existing.LessonTypeId))
                        .Select(existing => (Start: existing.StartTime, End: existing.EndTime, ModuleId: existing.ModuleId))
                        .Append((Start: slot.Start, End: slot.End, ModuleId: moduleId))
                        .Distinct()
                        .OrderBy(lesson => lesson.Start)
                        .ThenBy(lesson => lesson.End)
                        .ToList();
                    var segments = 0;
                    var inSegment = false;
                    foreach (var lesson in orderedLessons)
                    {
                        if (lesson.ModuleId == moduleId)
                        {
                            if (!inSegment)
                            {
                                segments++;
                                inSegment = true;
                                if (segments > 1)
                                {
                                    return true;
                                }
                            }
                        }
                        else
                        {
                            inSegment = false;
                        }
                    }
                }

                return false;
            }

            (DateOnly Date, TimeSlot Slot, int? TeacherId, Room Room, List<int> GroupIds, int TopicContinuationDistance, int PreferredFirstLoad, int GapTrapPenalty, int LectureOrderConflict, bool JoinsExistingOccurrence, bool BeyondPreferredLimit, bool EmergencyLateLecture)? best = null;
            foreach (var date in DatesBetween(rangeStartDate, rangeEndDateExclusive))
            {
                var slotsForDate = SharedSlotsForDate(courseIdValue, date);
                if (slotsForDate.Count == 0)
                {
                    continue;
                }
                foreach (var slot in slotsForDate)
                {
                    var sharedSlotOrder = GetSharedSlotOrder(slotsForDate, slot);
                    if (IsBlockedLateLectureSlot(topic.LessonTypeId, sharedSlotOrder, coursePreferredFirstMaxSlotOrder))
                    {
                        continue;
                    }
                    var beyondPreferredLimit = preferredFirstTypeId != 0
                                               && topic.LessonTypeId == preferredFirstTypeId
                                               && coursePreferredFirstMaxSlotOrder is int maxPreferredSlot
                                               && sharedSlotOrder > maxPreferredSlot;
                    var emergencyLateLecture = IsEmergencyLateLectureSlot(topic.LessonTypeId, sharedSlotOrder, coursePreferredFirstMaxSlotOrder);
                    foreach (var teacherId in teacherIds)
                    {
                        if (teacherId is int tid)
                        {
                            if (!TeacherFitsWorkingHours(tid, date, slot.Start, slot.End))
                            {
                                continue;
                            }
                            var teacherBusy = HasTeacherOverlap(tid, date, slot.Start, slot.End);
                            if (teacherBusy)
                            {
                                continue;
                            }
                        }
                        foreach (var room in roomCandidates)
                        {
                            var roomBusy = HasRoomOverlap(room.Id, date, slot.Start, slot.End);
                            if (roomBusy)
                            {
                                continue;
                            }
                            if (teacherId is int tidForTravel
                                && ViolatesSharedLectureTravel(x => x.TeacherId == tidForTravel, room, date, slot.Start, slot.End))
                            {
                                continue;
                            }
                            var pack = BuildSharedLectureGroupPack(courseGroups, moduleId, topic, room, date, slot);
                            if (pack.Count < 2)
                            {
                                continue;
                            }
                            var potentialPack = BuildPotentialSharedLectureGroupPack(courseGroups, moduleId, topic, room, date, slot);
                            if (pack.Count < potentialPack.Count)
                            {
                                continue;
                            }
                            if (ShouldHoldShareableTopicForMissingPendingGroups(courseIdValue, moduleId, topic, pack))
                            {
                                continue;
                            }
                            if (ViolatesSharedModuleBlockSplit(date, slot, slotsForDate, pack))
                            {
                                continue;
                            }
                            var preferredFirstLoad = PreferredFirstLectureLoadForCourseDate(date);
                            var joinsExistingOccurrence = JoinsExistingTopicOccurrence(date, slot);
                            var topicContinuationDistance = TopicContinuationDistance(date, slot, slotsForDate);
                            var gapTrapPenalty = SharedLectureGapTrapPenalty(date, slot, slotsForDate, room, pack);
                            var lectureOrderConflict = SharedLectureOrderConflictCount(date, slot, pack);
                            var slotComparison = best is null
                                ? 0
                                : CompareSlotPosition(date, slot.Start, slot.End, best.Value.Date, best.Value.Slot.Start, best.Value.Slot.End);
                            var preferEarliestPreferredFirst = preferredFirstTypeId != 0 && topic.LessonTypeId == preferredFirstTypeId;
                            var betterCandidate = best is null;
                            if (!betterCandidate && best is not null)
                            {
                                if (emergencyLateLecture != best.Value.EmergencyLateLecture)
                                {
                                    betterCandidate = !emergencyLateLecture;
                                }
                                else if (lectureOrderConflict != best.Value.LectureOrderConflict)
                                {
                                    betterCandidate = lectureOrderConflict < best.Value.LectureOrderConflict;
                                }
                                else if (pack.Count > best.Value.GroupIds.Count)
                                {
                                    betterCandidate = true;
                                }
                            }
                            if (!betterCandidate
                                && best is not null
                                && pack.Count == best.Value.GroupIds.Count)
                            {
                                if (joinsExistingOccurrence != best.Value.JoinsExistingOccurrence)
                                {
                                    betterCandidate = joinsExistingOccurrence;
                                }
                                else if (preferEarliestPreferredFirst)
                                {
                                    betterCandidate = beyondPreferredLimit != best.Value.BeyondPreferredLimit
                                                      ? !beyondPreferredLimit
                                                      : gapTrapPenalty < best.Value.GapTrapPenalty
                                                        || (gapTrapPenalty == best.Value.GapTrapPenalty && slotComparison < 0)
                                                        || (gapTrapPenalty == best.Value.GapTrapPenalty
                                                            && slotComparison == 0
                                                            && topicContinuationDistance < best.Value.TopicContinuationDistance)
                                                        || (gapTrapPenalty == best.Value.GapTrapPenalty
                                                            && slotComparison == 0
                                                            && topicContinuationDistance == best.Value.TopicContinuationDistance
                                                            && room.Capacity > best.Value.Room.Capacity);
                                }
                                else
                                {
                                    betterCandidate = gapTrapPenalty < best.Value.GapTrapPenalty
                                                      || (gapTrapPenalty == best.Value.GapTrapPenalty
                                                          && topicContinuationDistance < best.Value.TopicContinuationDistance)
                                                      || (gapTrapPenalty == best.Value.GapTrapPenalty
                                                          && topicContinuationDistance == best.Value.TopicContinuationDistance
                                                          && preferredFirstLoad < best.Value.PreferredFirstLoad)
                                                      || (gapTrapPenalty == best.Value.GapTrapPenalty
                                                          && topicContinuationDistance == best.Value.TopicContinuationDistance
                                                          && preferredFirstLoad == best.Value.PreferredFirstLoad
                                                          && slotComparison < 0)
                                                      || (gapTrapPenalty == best.Value.GapTrapPenalty
                                                          && topicContinuationDistance == best.Value.TopicContinuationDistance
                                                          && preferredFirstLoad == best.Value.PreferredFirstLoad
                                                          && slotComparison == 0
                                                          && room.Capacity > best.Value.Room.Capacity);
                                }
                            }
                            if (betterCandidate)
                            {
                                best = (date, slot, teacherId, room, pack, topicContinuationDistance, preferredFirstLoad, gapTrapPenalty, lectureOrderConflict, joinsExistingOccurrence, beyondPreferredLimit, emergencyLateLecture);
                            }
                        }
                    }
                }
            }

            if (best is null)
            {
                return false;
            }
            if (!TryValidatePlacementAgainstBusy(
                    moduleId,
                    topic.LessonTypeId,
                    topic.Id,
                    best.Value.TeacherId,
                    best.Value.Room,
                    best.Value.Date,
                    best.Value.Slot.Start,
                    best.Value.Slot.End,
                    best.Value.GroupIds,
                    out _))
            {
                return false;
            }

            foreach (var groupId in best.Value.GroupIds)
            {
                if (!selectedGroupsById.TryGetValue(groupId, out var group))
                {
                    continue;
                }
                var item = new TeacherDraftItem
                {
                    Date = best.Value.Date,
                    DayOfWeek = best.Value.Date.ToDateTime(TimeOnly.MinValue).DayOfWeek,
                    StartTime = best.Value.Slot.Start,
                    EndTime = best.Value.Slot.End,
                    GroupId = groupId,
                    ModuleId = moduleId,
                    RoomId = best.Value.Room.Id,
                    TeacherId = best.Value.TeacherId,
                    ModuleTopicId = topic.Id,
                    LessonTypeId = topic.LessonTypeId,
                    Status = DraftStatus.Draft,
                    IsLocked = false,
                    IsSelfStudy = false
                };
                _db.TeacherDraftItems.Add(item);
                allCreatedDrafts.Add(item);
                movableDrafts.Add(item);
                AddCurrentRangeFact(groupId, moduleId);
                MarkTopicUsed(groupId, moduleId, topic);
                AddBusySlot(new BusySlot(
                    groupId,
                    best.Value.TeacherId,
                    best.Value.Room.Id,
                    best.Value.Date,
                    best.Value.Slot.Start,
                    best.Value.Slot.End,
                    best.Value.Room.BuildingId,
                    moduleId,
                    topic.LessonTypeId,
                    topic.Id,
                    true));
                created++;
                Inc(groupId, best.Value.Date);
                if (preferredFirstTypeId != 0 && topic.LessonTypeId == preferredFirstTypeId)
                {
                    hasPreferred.Add((groupId, moduleId));
                }
                var remainingKey = (groupId, moduleId);
                if (remainingByGroupModule.TryGetValue(remainingKey, out var leftRemaining) && leftRemaining > 0)
                {
                    remainingByGroupModule[remainingKey] = Math.Max(0, leftRemaining - 1);
                }
            }

            return true;
        }

        int PreplaceAvailableSharedLectureTopics(int? onlyModuleId = null)
        {
            var placed = 0;
            foreach (var courseEntry in selectedGroupsByCourse.OrderBy(entry => entry.Key))
            {
                var moduleIdsForCourse = remainingByGroupModule
                    .Where(kv => kv.Value > 0
                                 && selectedGroupsById.TryGetValue(kv.Key.GroupId, out var group)
                                 && group.CourseId == courseEntry.Key)
                    .Select(kv => kv.Key.ModuleId)
                    .Where(moduleId => onlyModuleId is null || moduleId == onlyModuleId.Value)
                    .Distinct()
                    .ToList();
                foreach (var moduleId in BuildCourseModuleOrder(courseEntry.Key, moduleIdsForCourse))
                {
                    if (!topicsByModule.TryGetValue(moduleId, out var moduleTopics))
                    {
                        continue;
                    }
                    foreach (var topic in moduleTopics.Where(t => Math.Max(0, t.AuditoriumHours) > 0))
                    {
                        while (TryPreplaceSharedLectureTopic(courseEntry.Key, moduleId, topic))
                        {
                            placed++;
                        }
                    }
                }
            }

            return placed;
        }

        var preflightItems = BuildAutoGenPreflight();
        if (preflightItems.Count > 0)
        {
            foreach (var item in preflightItems.Take(6))
            {
                var examples = item.Examples.Count == 0
                    ? string.Empty
                    : $" Приклади: {string.Join("; ", item.Examples.Take(3))}.";
                warnings.Add($"Попередня перевірка ресурсів: {item.Title} — {item.Count}. {item.Recommendation}{examples}");
            }
        }
        else if (r.PreflightOnly)
        {
            warnings.Add("Попередня перевірка ресурсів не знайшла явних дефіцитів. Далі виконується пробна генерація без збереження, щоб показати реальні порожні слоти.");
        }

        PreplaceAvailableSharedLectureTopics();
        // Основний цикл генерації: обходимо всі групи.
        void RemoveDraftEntity(TeacherDraftItem draft)
        {
            var entry = _db.Entry(draft);
            if (entry.State == EntityState.Detached)
            {
                return;
            }
            if (entry.State == EntityState.Added
                || entry.Properties.Any(property => property.Metadata.IsPrimaryKey() && property.IsTemporary))
            {
                entry.State = EntityState.Detached;
                return;
            }
            _db.TeacherDraftItems.Remove(draft);
        }
        foreach (var grp in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Обідня перерва для курсу або глобальна (якщо не задано для курсу).
            var preferredFirstSlotLimit = preferredFirstSlotLimitsAll.FirstOrDefault(x => x.CourseId == grp.CourseId)
                                       ?? preferredFirstSlotLimitsAll.FirstOrDefault(x => x.CourseId == null);
            int? preferredFirstMaxSlotOrder = r.PreferredFirstMaxSlotOrderOverride is int preferredFirstOverride
                ? preferredFirstOverride > 0 ? preferredFirstOverride : null
                : preferredFirstSlotLimit is not null && preferredFirstSlotLimit.MaxSlotOrder > 0
                    ? preferredFirstSlotLimit.MaxSlotOrder
                    : preferredFirstTypeId != 0 ? 6 : null;
            var allSlots = await _db.TimeSlots.AsNoTracking()
                .Where(s => s.IsActive && (s.CourseId == grp.CourseId || s.CourseId == null))
                .OrderBy(s => s.SortOrder).ThenBy(s => s.Start)
                .ToListAsync();
            if (allSlots.Count == 0)
            {
                warnings.Add($"Не знайдено жодного слоту розкладу (глобального або для курсу {grp.Course.Name}). Група {grp.Name} пропущена.");
                continue;
            }
            var resolvedByDay = TimeSlotsResolver.ResolveForWeek(allSlots, grp.CourseId);
            List<TimeSlot> slots = new();
            Dictionary<(TimeOnly Start, TimeOnly End), int> slotIndexByTime = new();
            // Один модуль у межах дня може мати довгий блок, але без розриву іншими модулями.
            const int maxModuleSegmentsPerDay = 1;
            void ApplySlotsForDate(DateOnly date)
            {
                var dayOfWeek = date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
                if (!resolvedByDay.TryGetValue(dayOfWeek, out var resolved) || resolved.Slots.Count == 0)
                {
                    slots = new List<TimeSlot>();
                    slotIndexByTime = new Dictionary<(TimeOnly, TimeOnly), int>();
                    return;
                }
                slots = resolved.Slots;
                slotIndexByTime = slots
                    .Select((slot, index) => new { slot, index })
                    .ToDictionary(x => (x.slot.Start, x.slot.End), x => x.index);
            }
            // Перевіряє, чи слот вже зайнятий у групи.
            int GetSlotOrder(TimeOnly start, TimeOnly end)
                => slotIndexByTime.TryGetValue((start, end), out var idx) ? idx + 1 : 0;
            bool IsPreferredFirstProtectedSlot(TimeOnly start, TimeOnly end)
            {
                if (preferredFirstMaxSlotOrder is not int maxSlot || maxSlot <= 0)
                    return false;
                var slotOrder = GetSlotOrder(start, end);
                return slotOrder > 0 && slotOrder <= maxSlot;
            }
            int? EarliestPreferredFirstSlotOrderForDate(DateOnly date)
            {
                if (preferredFirstTypeId == 0) return null;
                var preferredOrders = BusyForGroup(grp.Id)
                    .Where(b => b.Date == date
                                && b.LessonTypeId == preferredFirstTypeId)
                    .Select(b => GetSlotOrder(b.StartTime, b.EndTime))
                    .Where(order => order > 0)
                    .OrderBy(order => order)
                        .ToList();
                return preferredOrders.Count == 0 ? null : preferredOrders[0];
            }
            bool IsLectureFirstProtectedSlot(TimeOnly start, TimeOnly end)
            {
                var slotOrder = GetSlotOrder(start, end);
                return slotOrder > 0 && slotOrder <= RegularLectureMaxSlotOrder(preferredFirstMaxSlotOrder);
            }
            int CountEarlierNonLectureSlots(DateOnly date, int beforeSlotOrder)
            {
                if (beforeSlotOrder <= 1)
                {
                    return 0;
                }

                return BusyForGroup(grp.Id)
                    .Where(b => b.Date == date
                                && !CanShareAcrossGroups(b.LessonTypeId)
                                && !excludedTypeIds.Contains(b.LessonTypeId))
                    .Select(b => GetSlotOrder(b.StartTime, b.EndTime))
                    .Count(order => order > 0 && order < beforeSlotOrder);
            }
            bool HasLaterLectureFirstPlacement(DateOnly date, TimeOnly afterStart)
                => BusyForGroupDate(grp.Id, date)
                    .Any(b => b.StartTime > afterStart
                              && CanShareAcrossGroups(b.LessonTypeId)
                              && !excludedTypeIds.Contains(b.LessonTypeId));
            bool SlotFilledForGroup(int groupId, DateOnly date, TimeSlot slot) =>
                BusyForGroupDate(groupId, date).Any(b => b.StartTime == slot.Start && b.EndTime == slot.End);
            bool SlotFilled(DateOnly date, TimeSlot slot) =>
                SlotFilledForGroup(grp.Id, date, slot);
            // Повертає викладача, який веде цей модуль у сусідньому слоті.
            int? GetAdjacentSameModuleTeacherId(DateOnly date, TimeSlot slot, int moduleId)
            {
                if (!slotIndexByTime.TryGetValue((slot.Start, slot.End), out var slotIndex))
                    return null;
                int? prevTeacherId = null;
                if (slotIndex > 0)
                {
                    var prevSlot = slots[slotIndex - 1];
                    prevTeacherId = busy
                        .Where(b => b.GroupId == grp.Id
                                    && b.Date == date
                                    && b.ModuleId == moduleId
                                    && b.StartTime == prevSlot.Start
                                    && b.EndTime == prevSlot.End
                                    && b.TeacherId != null
                                    && !excludedTypeIds.Contains(b.LessonTypeId))
                        .Select(b => b.TeacherId)
                        .FirstOrDefault();
                }
                int? nextTeacherId = null;
                if (slotIndex + 1 < slots.Count)
                {
                    var nextSlot = slots[slotIndex + 1];
                    nextTeacherId = busy
                        .Where(b => b.GroupId == grp.Id
                                    && b.Date == date
                                    && b.ModuleId == moduleId
                                    && b.StartTime == nextSlot.Start
                                    && b.EndTime == nextSlot.End
                                    && b.TeacherId != null
                                    && !excludedTypeIds.Contains(b.LessonTypeId))
                        .Select(b => b.TeacherId)
                        .FirstOrDefault();
                }
                if (prevTeacherId is int pt && nextTeacherId is int nt && pt == nt)
                    return pt;
                return prevTeacherId ?? nextTeacherId;
            }
            // Повертає аудиторію суміжного слота для того самого модуля (щоб тримати одну аудиторію в блоці).
            int? GetAdjacentSameModuleRoomId(DateOnly date, TimeSlot slot, int moduleId)
            {
                if (!slotIndexByTime.TryGetValue((slot.Start, slot.End), out var slotIndex))
                    return null;
                int? prevRoomId = null;
                if (slotIndex > 0)
                {
                    var prevSlot = slots[slotIndex - 1];
                    prevRoomId = busy
                        .Where(b => b.GroupId == grp.Id
                                    && b.Date == date
                                    && b.ModuleId == moduleId
                                    && b.StartTime == prevSlot.Start
                                    && b.EndTime == prevSlot.End
                                    && b.RoomId != null
                                    && !excludedTypeIds.Contains(b.LessonTypeId))
                        .Select(b => b.RoomId)
                        .FirstOrDefault();
                }
                int? nextRoomId = null;
                if (slotIndex + 1 < slots.Count)
                {
                    var nextSlot = slots[slotIndex + 1];
                    nextRoomId = busy
                        .Where(b => b.GroupId == grp.Id
                                    && b.Date == date
                                    && b.ModuleId == moduleId
                                    && b.StartTime == nextSlot.Start
                                    && b.EndTime == nextSlot.End
                                    && b.RoomId != null
                                    && !excludedTypeIds.Contains(b.LessonTypeId))
                        .Select(b => b.RoomId)
                        .FirstOrDefault();
                }
                if (prevRoomId is int pr && nextRoomId is int nr && pr == nr)
                    return pr;
                return prevRoomId ?? nextRoomId;
            }
            // Рахує відстань до вже поставлених годин цієї самої теми для групи.
            int TopicContinuationDistanceForGroup(DateOnly date, TimeSlot slot, int moduleId, int? topicId)
            {
                if (topicId is not int selectedTopicId)
                {
                    return 0;
                }
                var existingTopicSlots = busy
                    .Where(existing => existing.GroupId == grp.Id
                                       && existing.ModuleId == moduleId
                                       && existing.ModuleTopicId == selectedTopicId
                                       && !excludedTypeIds.Contains(existing.LessonTypeId))
                    .Select(existing => new { existing.Date, existing.StartTime, existing.EndTime })
                    .Distinct()
                    .ToList();
                if (existingTopicSlots.Count == 0)
                {
                    return 0;
                }
                if (slotIndexByTime.TryGetValue((slot.Start, slot.End), out var currentIndex))
                {
                    var sameDayDistances = existingTopicSlots
                        .Where(existing => existing.Date == date)
                        .Select(existing => slotIndexByTime.TryGetValue((existing.StartTime, existing.EndTime), out var existingIndex)
                            ? (int?)Math.Max(0, Math.Abs(currentIndex - existingIndex) - 1)
                            : null)
                        .Where(distance => distance.HasValue)
                        .Select(distance => distance!.Value)
                        .ToList();
                    if (sameDayDistances.Count > 0)
                    {
                        return sameDayDistances.Min();
                    }
                }
                var nearestDayDistance = existingTopicSlots
                    .Select(existing => Math.Abs(existing.Date.DayNumber - date.DayNumber))
                    .DefaultIfEmpty(7)
                    .Min();
                return 100 + nearestDayDistance;
            }
            // Рахує кількість окремих сегментів модуля в межах дня.
            int CountModuleSegments(IReadOnlyList<int> orderedIndexes)
            {
                if (orderedIndexes.Count == 0)
                {
                    return 0;
                }
                int segments = 1;
                for (var i = 1; i < orderedIndexes.Count; i++)
                {
                    if (orderedIndexes[i] != orderedIndexes[i - 1] + 1)
                    {
                        segments++;
                    }
                }
                return segments;
            }
            // Формує текст причини, коли модуль надто розривається протягом дня.
            string ModuleSegmentLimitReason(int moduleId, int maxSegmentsAllowed)
            {
                return maxSegmentsAllowed <= 1
                    ? $"Модуль <{ModuleTitleLabel(moduleId)}> у межах дня ставимо суцільним блоком без повернення після перемикання на інший модуль."
                    : $"Модуль <{ModuleTitleLabel(moduleId)}> під час дозаповнення можна розбивати не більш ніж на {maxSegmentsAllowed} сегменти в межах дня.";
            }
            bool ViolatesAnyModuleDayBlock(int groupIdCheck, DateOnly date, int candidateModuleId, TimeOnly candidateStart, TimeOnly candidateEnd, out string reason, int maxSegmentsAllowed = 1)
            {
                var dayLessons = busy
                    .Where(b => b.GroupId == groupIdCheck
                                && b.Date == date
                                && !excludedTypeIds.Contains(b.LessonTypeId))
                    .Select(b => (Start: b.StartTime, End: b.EndTime, ModuleId: b.ModuleId))
                    .Distinct()
                    .ToList();
                if (!dayLessons.Any(x => x.Start == candidateStart && x.End == candidateEnd))
                {
                    dayLessons.Add((candidateStart, candidateEnd, candidateModuleId));
                }
                var orderedLessons = dayLessons
                    .OrderBy(x => x.Start)
                    .ThenBy(x => x.End)
                    .ToList();
                foreach (var moduleGroup in orderedLessons.Select(x => x.ModuleId).Distinct())
                {
                    int moduleSegments = 0;
                    bool inModuleSegment = false;
                    foreach (var lesson in orderedLessons)
                    {
                        if (lesson.ModuleId == moduleGroup)
                        {
                            if (!inModuleSegment)
                            {
                                moduleSegments++;
                                inModuleSegment = true;
                                if (moduleSegments > maxSegmentsAllowed)
                                {
                                    reason = ModuleSegmentLimitReason(moduleGroup, maxSegmentsAllowed);
                                    return true;
                                }
                            }
                        }
                        else
                        {
                            inModuleSegment = false;
                        }
                    }
                }
                reason = string.Empty;
                return false;
            }
            bool InsertionWouldSplitExistingModuleBlock(
                int groupIdCheck,
                DateOnly date,
                int candidateModuleId,
                TimeOnly candidateStart,
                TimeOnly candidateEnd,
                out string reason,
                int maxSegmentsAllowed = 1)
            {
                return ViolatesAnyModuleDayBlock(
                    groupIdCheck,
                    date,
                    candidateModuleId,
                    candidateStart,
                    candidateEnd,
                    out reason,
                    maxSegmentsAllowed);
            }
            // Перевіряє, чи є порожні слоти у день.
            bool DayHasGaps(DateOnly date, out TimeSlot? firstGap)
            {
                foreach (var slot in slots)
                {
                    if (!SlotFilled(date, slot))
                    {
                        firstGap = slot;
                        return true;
                    }
                }
                firstGap = null;
                return false;
            }
            // Збираємо створені чернетки для подальших зсувів і аналізу.
            var createdDrafts = new List<TeacherDraftItem>();
            // Формуємо підпис викладача для повідомлень.
            string TeacherLabel(int teacherId) =>
                teacherNames.TryGetValue(teacherId, out var name) && !string.IsNullOrWhiteSpace(name)
                    ? name
                    : $"#{teacherId}";
            // Фіксує причину, чому слот не був заповнений.
            void RecordSlotFailureReason(DateOnly date, TimeSlot slot, string reason)
            {
                var key = (grp.Id, date, slot.Start, slot.End);
                if (!slotFailureReasons.TryGetValue(key, out var reasons))
                {
                    reasons = new HashSet<string>();
                    slotFailureReasons[key] = reasons;
                }
                reasons.Add(reason);
            }
            // Додає причину для всіх слотів дня (масове повідомлення).
            void RecordSlotFailureReasonForAllSlots(DateOnly date, string reason)
            {
                foreach (var slot in slots)
                {
                    RecordSlotFailureReason(date, slot, reason);
                }
            }
            // Формує зрозуміле пояснення для прогалини.
            string? ComposeGapReason(DateOnly date, TimeSlot slot)
            {
                var key = (grp.Id, date, slot.Start, slot.End);
                if (slotFailureReasons.TryGetValue(key, out var reasons) && reasons.Count > 0)
                {
                    return string.Join("; ", reasons);
                }
                if (!remainingByGroupModule.Any(entry => entry.Key.GroupId == grp.Id && entry.Value > 0))
                {
                    return $"Для групи {grp.Name} більше не лишилось модулів із невикористаними годинами.";
                }
                return "Причину не вдалося визначити автоматично. Перевірте доступність викладачів, аудиторій, обмеження за типом заняття та дозволені повторення.";
            }
            // Додає попередження щодо порожнього слоту.
            void WarnGap(DateOnly date, TimeSlot gap)
            {
                var label = $"{gap.Start:HH\\:mm}-{gap.End:HH\\:mm}";
                var key = (grp.Id, date, gap.Start, gap.End);
                if (gapWarnings.Add(key))
                {
                    if (!slotFailureReasons.ContainsKey(key))
                    {
                        var modulesWithHours = remainingByGroupModule
                            .Where(kv => kv.Key.GroupId == grp.Id && kv.Value > 0)
                            .Select(kv => kv.Key.ModuleId)
                            .Distinct()
                            .ToList();
                        if (modulesWithHours.Count == 0)
                        {
                            RecordSlotFailureReason(date, gap, $"Для групи {grp.Name} не лишилось модулів із невикористаними годинами.");
                        }
                        else
                        {
                            var noTeachers = modulesWithHours
                                .Where(mid => !teachersForModule.Any(t => t.ModuleId == mid))
                                .Select(mid => $"#{mid}")
                                .ToList();
                            var noRooms = modulesWithHours
                                .Where(mid => CandidateRoomsFor(mid).Count == 0)
                                .Select(mid => $"#{mid}")
                                .ToList();
                            if (noTeachers.Count > 0)
                            {
                                RecordSlotFailureReason(date, gap, $"Немає викладачів для модулів: {string.Join(", ", noTeachers)}.");
                            }
                            else if (noRooms.Count > 0)
                            {
                                RecordSlotFailureReason(date, gap, $"Немає доступних аудиторій для модулів: {string.Join(", ", noRooms)}.");
                            }
                            else if (CountFor(grp.Id, date) >= slots.Count)
                            {
                                RecordSlotFailureReason(date, gap, $"Досягнуто максимум пар на день для групи {grp.Name} ({slots.Count}).");
                            }
                            else
                            {
                                RecordSlotFailureReason(date, gap, $"Не вдалося підібрати комбінацію модуль/викладач/аудиторія для слоту {label}: усі варіанти зайняті або заборонені правилами.");
                            }
                        }
                    }
                    var gapModuleIds = remainingByGroupModule
                        .Where(kv => kv.Key.GroupId == grp.Id && kv.Value > 0)
                        .Select(kv => kv.Key.ModuleId)
                        .Distinct()
                        .ToList();
                    int? reportModuleId = gapModuleIds.Count == 1 ? gapModuleIds[0] : null;
                    string? reportModuleName = reportModuleId is int moduleId
                        ? ModuleTitleLabel(moduleId)
                        : gapModuleIds.Count > 1
                            ? $"Кілька модулів: {string.Join(", ", gapModuleIds.Take(3).Select(ModuleTitleLabel))}"
                            : null;
                    var reason = ComposeGapReason(date, gap);
                    var reasonSuffix = string.IsNullOrWhiteSpace(reason) ? string.Empty : $" Причина: {reason}";
                    warnings.Add($"Автогенерація не заповнила слот {label} для групи {grp.Name} на {date:yyyy-MM-dd}.{reasonSuffix}");
                    gapDetails.Add(new AutoGenGapDetail(
                        GroupId: grp.Id,
                        GroupName: grp.Name,
                        Date: date,
                        Start: gap.Start,
                        End: gap.End,
                        SlotLabel: label,
                        Reason: reason,
                        ModuleId: reportModuleId,
                        ModuleName: reportModuleName));
                }
            }
            void WarnRemainingGaps(DateOnly date)
            {
                foreach (var gap in slots.Where(slot => !SlotFilled(date, slot)).ToList())
                {
                    WarnGap(date, gap);
                }
            }
            // Спроба прибрати прогалини шляхом пересування занять у межах дня.
            bool TryShiftGaps(DateOnly date)
            {
                bool moved = false;
                var attempted = new HashSet<TeacherDraftItem>();
                while (DayHasGaps(date, out var gap) && gap is not null)
                {
                    if (HasLaterLectureFirstPlacement(date, gap.Start))
                    {
                        break;
                    }

                    var candidate = createdDrafts
                        .Where(cd => cd.GroupId == grp.Id
                                     && cd.Date == date
                                     && !cd.IsLocked
                                     && !CanShareAcrossGroups(cd.LessonTypeId)
                                     && cd.StartTime > gap.Start
                                     && !attempted.Contains(cd))
                        .OrderBy(cd => cd.StartTime)
                        .FirstOrDefault();
                    if (candidate is null)
                    {
                        break;
                    }
                    attempted.Add(candidate);
                    if (typeBreakId.HasValue && candidate.LessonTypeId == typeBreakId.Value)
                    {
                        continue;
                    }
                    var s = gap.Start;
                    var e = gap.End;
                    bool TryShiftTravelCheck(Func<BusySlot, bool> ownerMatch, string subjectLabel, out string reason)
                    {
                        reason = string.Empty;
                        if (candidate.RoomId is not int candidateRoomId)
                        {
                            return false;
                        }
                        if (!roomBuildingById.TryGetValue(candidateRoomId, out var candidateBuildingId) || candidateBuildingId == 0)
                        {
                            return false;
                        }
                        foreach (var existing in BusyForDate(date).Where(x =>
                                     x.RoomId != null
                                     && x.BuildingId.HasValue
                                     && ownerMatch(x)
                                     && !(x.GroupId == candidate.GroupId
                                          && x.TeacherId == candidate.TeacherId
                                          && x.RoomId == candidate.RoomId
                                          && x.ModuleId == candidate.ModuleId
                                          && x.StartTime == candidate.StartTime
                                          && x.EndTime == candidate.EndTime)))
                        {
                            var sourceBuildingId = existing.BuildingId!.Value;
                            if (sourceBuildingId == candidateBuildingId)
                            {
                                continue;
                            }
                            var needMinutes = TravelMinutes(sourceBuildingId, candidateBuildingId);
                            var gapBefore = (s.ToTimeSpan() - existing.EndTime.ToTimeSpan()).TotalMinutes;
                            var gapAfter = (existing.StartTime.ToTimeSpan() - e.ToTimeSpan()).TotalMinutes;
                            if (existing.EndTime <= s && gapBefore < needMinutes)
                            {
                                reason = $"Для {subjectLabel} недостатньо часу на перехід до корпусу #{candidateBuildingId} після заняття в корпусі #{sourceBuildingId}: доступно {gapBefore:N0} хв, потрібно {needMinutes} хв.";
                                return true;
                            }
                            if (e <= existing.StartTime && gapAfter < needMinutes)
                            {
                                reason = $"Для {subjectLabel} недостатньо часу на перехід від корпусу #{sourceBuildingId} до корпусу #{candidateBuildingId} перед заняттям: доступно {gapAfter:N0} хв, потрібно {needMinutes} хв.";
                                return true;
                            }
                        }
                        return false;
                    }
                    if (TryShiftTravelCheck(x => x.GroupId == grp.Id, $"групи {grp.Name}", out var shiftGroupTravelReason))
                    {
                        RecordSlotFailureReason(date, gap, shiftGroupTravelReason);
                        continue;
                    }
                    if (candidate.TeacherId is int shiftTeacherId
                        && TryShiftTravelCheck(x => x.TeacherId == shiftTeacherId, $"викладача #{shiftTeacherId}", out var shiftTeacherTravelReason))
                    {
                        RecordSlotFailureReason(date, gap, shiftTeacherTravelReason);
                        continue;
                    }
                    if (!slotIndexByTime.TryGetValue((candidate.StartTime, candidate.EndTime), out var candidateOldSlotIndex)
                        || !slotIndexByTime.TryGetValue((s, e), out var candidateNewSlotIndex))
                    {
                        continue;
                    }
                    var moduleIndexesAfterShift = BusyForGroupDate(grp.Id, date)
                        .Where(b => b.GroupId == grp.Id
                                    && b.ModuleId == candidate.ModuleId
                                    && !excludedTypeIds.Contains(b.LessonTypeId))
                        .Select(b => slotIndexByTime.TryGetValue((b.StartTime, b.EndTime), out var idx) ? idx : -1)
                        .Where(idx => idx >= 0 && idx != candidateOldSlotIndex)
                        .Append(candidateNewSlotIndex)
                        .Distinct()
                        .OrderBy(idx => idx)
                        .ToList();
                    var keepsContiguousBlock = moduleIndexesAfterShift.Count == 0
                        || moduleIndexesAfterShift[^1] - moduleIndexesAfterShift[0] + 1 == moduleIndexesAfterShift.Count;
                    if (!keepsContiguousBlock)
                    {
                        RecordSlotFailureReason(
                            date,
                            gap,
                            $"Модуль <{ModuleTitleLabel(candidate.ModuleId)}> у межах дня ставимо суцільним блоком без повернення після перемикання на інший модуль.");
                        continue;
                    }
                    if (InsertionWouldSplitExistingModuleBlock(grp.Id, date, candidate.ModuleId, s, e, out var shiftSplitReason))
                    {
                        RecordSlotFailureReason(date, gap, shiftSplitReason);
                        continue;
                    }
                    if (candidate.ModuleTopicId is int shiftedTopicId
                        && topicById.TryGetValue(shiftedTopicId, out var shiftedTopic)
                        && ViolatesTopicCalendarOrder(candidate.GroupId, candidate.ModuleId, shiftedTopic, date, s, e))
                    {
                        RecordSlotFailureReason(
                            date,
                            gap,
                            $"Для групи {grp.Name} порушується хронологічний порядок тем модуля <{ModuleTitleLabel(candidate.ModuleId)}> після перестановки.");
                        continue;
                    }
                    int? tidCandidate = candidate.TeacherId;
                    if (tidCandidate is int tidVal && !TeacherFitsWorkingHours(tidVal, date, s, e))
                    {
                        continue;
                    }
                    var shiftedSlotGroupLimit = SlotGroupLimitForPlacement(
                        grp.CourseId,
                        candidate.LessonTypeId,
                        candidate.IsSelfStudy);
                    if (CountGroupsWithModuleInSlot(candidate.ModuleId, date, s, e) >= shiftedSlotGroupLimit)
                    {
                        continue;
                    }
                    bool peopleBusy = BusyForDate(date).Any(x =>
                        (x.GroupId == grp.Id || (tidCandidate is int t && x.TeacherId == t))
                        && !(x.GroupId == candidate.GroupId
                             && x.StartTime == candidate.StartTime
                             && x.EndTime == candidate.EndTime
                             && x.ModuleId == candidate.ModuleId
                             && x.TeacherId == candidate.TeacherId
                             && x.RoomId == candidate.RoomId)
                        && x.StartTime < e && s < x.EndTime);
                    if (peopleBusy)
                    {
                        continue;
                    }
                    bool requiresRoom = typeById.TryGetValue(candidate.LessonTypeId, out var ltMeta)
                        ? ltMeta.RequiresRoom
                        : true;
                    if (requiresRoom && candidate.RoomId is int rid)
                    {
                        bool roomBusy = BusyForRoomDate(rid, date).Any(x =>
                            !(x.GroupId == candidate.GroupId
                              && x.StartTime == candidate.StartTime
                              && x.EndTime == candidate.EndTime
                              && x.ModuleId == candidate.ModuleId
                              && x.RoomId == candidate.RoomId)
                            && x.StartTime < e && s < x.EndTime);
                        if (roomBusy)
                        {
                            continue;
                        }
                    }
                    var oldStart = candidate.StartTime;
                    var oldEnd = candidate.EndTime;
                    var oldBusySlot = FindBusySlotForDraft(candidate, oldStart, oldEnd);
                    if (oldBusySlot is null || !RemoveBusySlot(oldBusySlot))
                    {
                        continue;
                    }
                    var buildingId = candidate.RoomId.HasValue
                        ? roomsAll.FirstOrDefault(r => r.Id == candidate.RoomId)?.BuildingId
                        : null;
                    AddBusySlot(new BusySlot(
                        candidate.GroupId,
                        candidate.TeacherId,
                        candidate.RoomId,
                        date,
                        s,
                        e,
                        buildingId,
                        candidate.ModuleId,
                        candidate.LessonTypeId,
                        candidate.ModuleTopicId,
                        true));
                    candidate.StartTime = s;
                    candidate.EndTime = e;
                    candidate.DayOfWeek = date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
                    InvalidateGapResourceCaches();
                    warnings.Add($"[{date:yyyy-MM-dd} {s:HH\\:mm}-{e:HH\\:mm}] {grp.Name}: заняття пересунуто, щоб прибрати розрив.");
                    moved = true;
                }
                return moved;
            }
            // Формуємо список модулів для генерації у цій групі.
            var modules = hasModuleHourOverrides
                ? remainingByGroupModule
                    .Where(kv => kv.Key.GroupId == grp.Id && kv.Value > 0)
                    .Select(kv => kv.Key.ModuleId)
                    .Distinct()
                    .ToList()
                : activePlans.Where(p => p.CourseId == grp.CourseId).Select(p => p.ModuleId).Distinct().ToList();
            // Упорядковуємо модулі відповідно до послідовності курсу.
            var orderedModules = BuildCourseModuleOrder(grp.CourseId, modules);
            // Набір наповнювачів (другорядні модулі).
            fillerByCourse.TryGetValue(grp.CourseId, out var fillerSetRaw);
            var fillerLookup = fillerSetRaw is not null
                ? new HashSet<int>(fillerSetRaw)
                : new HashSet<int>();
            var fillerModulesOrdered = fillerLookup.OrderBy(x => x).ToList();
            var mainModulesOrdered = orderedModules.Where(mid => !fillerLookup.Contains(mid)).ToList();
            bool HasPendingLectureFirstPlacementForDate(DateOnly date)
            {
                var savedLtIndex = ltIndex;
                try
                {
                    foreach (var mid in orderedModules.Concat(fillerModulesOrdered).Distinct())
                    {
                        if (RemainingFor(grp.Id, mid) <= 0)
                        {
                            continue;
                        }
                        if (!softFill && TopicsDepleted(grp.Id, mid))
                        {
                            continue;
                        }
                        if (CanShareAcrossGroups(PickLessonType(grp.Id, grp.CourseId, mid, date).LessonTypeId))
                        {
                            return true;
                        }
                    }
                }
                finally
                {
                    ltIndex = savedLtIndex;
                }

                return false;
            }
            var mainModuleSet = new HashSet<int>(mainModulesOrdered);
            var groupOrderByModule = new Dictionary<int, int>();
            int maxGroupOrder = 0;
            if (mainSequenceByCourse.TryGetValue(grp.CourseId, out var mainSequence))
            {
                foreach (var entry in mainSequence)
                {
                    if (!mainModuleSet.Contains(entry.ModuleId))
                    {
                        continue;
                    }
                    if (groupOrderByModule.TryAdd(entry.ModuleId, entry.GroupOrder))
                    {
                        if (entry.GroupOrder > maxGroupOrder)
                        {
                            maxGroupOrder = entry.GroupOrder;
                        }
                    }
                }
            }
            foreach (var mid in mainModulesOrdered)
            {
                if (!groupOrderByModule.ContainsKey(mid))
                {
                    maxGroupOrder++;
                    groupOrderByModule[mid] = maxGroupOrder;
                }
            }
            var mainGroupsOrdered = mainModulesOrdered
                .GroupBy(mid => groupOrderByModule[mid])
                .OrderBy(g => g.Key)
                .Select(g => new MainModuleGroup(g.Key, g.ToList()))
                .ToList();
            var firstMainModuleId = mainGroupsOrdered.Count > 0 && mainGroupsOrdered[0].ModuleIds.Count > 0
                ? mainGroupsOrdered[0].ModuleIds[0]
                : 0;
            var hasCompletedMainModules = mainModulesOrdered.Any(mid =>
                factMap.TryGetValue((grp.Id, mid), out var completed) && completed > 0);
            // Для першої генерації курсу фіксуємо старт із першого головного модуля.
            // Для першої генерації намагаємось стартувати з першого головного модуля.
            bool forceFirstMainModule = mainGroupsOrdered.Count > 0
                && mainGroupsOrdered[0].ModuleIds.Count == 1
                && firstMainModuleId != 0
                && !hasCompletedMainModules
                && RemainingFor(grp.Id, firstMainModuleId) > 0;
            bool firstMainPlaced = !forceFirstMainModule;
            DateOnly? firstMainDate = null;
            TimeOnly? firstMainStart = null;
            if (forceFirstMainModule)
            {
                var existingFirstMain = busy
                    .Where(b => b.GroupId == grp.Id
                                && b.ModuleId == firstMainModuleId
                                && !excludedTypeIds.Contains(b.LessonTypeId))
                    .OrderBy(b => b.Date)
                    .ThenBy(b => b.StartTime)
                    .FirstOrDefault();
                if (existingFirstMain is not null)
                {
                    firstMainPlaced = true;
                    firstMainDate = existingFirstMain.Date;
                    firstMainStart = existingFirstMain.StartTime;
                }
            }
            // Перевірка для заборони слотів до першого головного модуля.
            bool IsBeforeFirstMain(DateOnly date, TimeOnly start)
            {
                if (!firstMainDate.HasValue || !firstMainStart.HasValue) return false;
                if (date < firstMainDate.Value) return true;
                return date == firstMainDate.Value && start < firstMainStart.Value;
            }
            // Детемінований генератор для стабільного випадкового вибору.
            var groupRandom = new Random(StableSeed(weekStart.DayNumber, grp.Id, grp.CourseId));
            var sequenceRandom = new Random(StableSeed(weekStart.DayNumber, grp.Id, grp.CourseId, 17));
            int? lastPrimaryModuleId = null;
            // Лічильник використання аудиторій поточною групою.
            var groupRoomUsage = busy
                .Where(b => b.GroupId == grp.Id && b.Date >= rangeStartDate && b.Date < rangeEndDateExclusive && b.RoomId != null)
                .GroupBy(b => b.RoomId!.Value)
                .ToDictionary(g => g.Key, g => g.Count());
            var teacherDaySlackCache = new Dictionary<(DateOnly Date, int TeacherId), int>();
            var roomDaySlackCache = new Dictionary<(DateOnly Date, int RoomId), int>();
            var neighborGapBuildingPenaltyCache = new Dictionary<(DateOnly Date, TimeOnly Start, TimeOnly End, int BuildingId, int GapBudget), double>();
            // Ваги штрафів для вибору найкращого кандидата у слоті.
            // 0 вимикає вплив конкретного правила.
            const double penaltySameModulePrevDay = 10.0; // Повтор того ж модуля у сусідній день.
            const double penaltyExtraSameDay = 6.0; // Третя і наступні пари модуля за день.
            const double penaltySameSlotPattern = 1.5; // Повтор однакового StartTime для модуля в інші дні.
            const double maxExtraPenaltyPreferSameTeacherForConsecutiveModule = 6.0; // Перевага одного викладача на суміжні слоти модуля.
            const double bonusExpandContiguousModuleBlock = 48.0;
            const double bonusReachThirdSlotInModuleBlock = 18.0;
            const double penaltyStartFourthDistinctModule = 75.0;
            const double penaltyTopicContinuationGap = 85.0; // Розрив повтору тієї самої теми іншим заняттям.
            var penaltyPreferredFirstTypeLateSlot = 2.4 * preferredFirstPenaltyMultiplier; // Пізній слот для типу "бажано першим".
            var penaltyNonPreferredEarlySlotWhilePreferredPending = 18.0 * preferredFirstPenaltyMultiplier; // Ранній непріоритетний тип, поки пріоритетний ще не поставлено.
            var penaltyPreferredFirstBeyondLimitSlot = 28.0 * preferredFirstPenaltyMultiplier; // Вихід пріоритетного типу за ліміт номера слоту.
            var penaltyNonPreferredBeforeFirstPreferred = 26.0 * preferredFirstPenaltyMultiplier; // Непріоритетний тип перед першим пріоритетним у межах дня.
            var bonusPreferredFirstInProtectedSlot = 8.0 * preferredFirstPenaltyMultiplier; // Додатковий бонус для пріоритетного типу в межах ранніх слотів.
            const double penaltyLectureFirstTypeLateSlot = 18.0; // Пізній слот для лекційного або потокового типу.
            const double penaltyLectureAfterNonLecture = 220.0; // Лекційний тип після не-лекційного заняття в межах дня.
            const double penaltyNonLectureEarlySlotWhileLecturePending = 140.0; // Ранній слот зайнято не-лекційним типом, поки лекцію ще не поставлено.
            const double bonusLectureFirstProtectedSlot = 18.0; // Бонус за лекційний тип у ранньому захищеному слоті.
            const double penaltyEmergencyLateLectureSlot = 360.0; // Аварійно пізню лекцію дозволяємо тільки як останній варіант.
            // Штраф за загальне навантаження викладача на курсі.
            double TeacherLoadPenalty(int teacherId) =>
                TeacherLoadScore(teacherId, grp.CourseId) * teacherLoadPenaltyWeight;
            // Найближча наступна будівля групи після поточного слоту.
            int? NextGroupBuilding(DateOnly date, TimeOnly end)
            {
                return BusyForGroupDate(grp.Id, date)
                    .Where(b => b.StartTime >= end && b.RoomId != null)
                    .OrderBy(b => b.StartTime)
                    .FirstOrDefault()?.BuildingId;
            }
            // Найближча наступна будівля викладача після поточного слоту.
            int? NextTeacherBuilding(int teacherId, DateOnly date, TimeOnly end)
            {
                return BusyForTeacherDate(teacherId, date)
                    .Where(b => b.StartTime >= end && b.RoomId != null)
                    .OrderBy(b => b.StartTime)
                    .FirstOrDefault()?.BuildingId;
            }
            // Корпус, який група вже найчастіше використовує в межах дня.
            int? PreferredGroupBuildingForDay(DateOnly date)
            {
                return BusyForGroupDate(grp.Id, date)
                    .Where(b => b.BuildingId.HasValue && b.RoomId != null)
                    .GroupBy(b => b.BuildingId!.Value)
                    .OrderByDescending(g => g.Count())
                    .ThenByDescending(g => g.Max(x => x.EndTime))
                    .Select(g => (int?)g.Key)
                    .FirstOrDefault();
            }
            // Кількість занять групи в конкретному корпусі за день.
            int CountGroupBuildingUsageForDay(DateOnly date, int buildingId)
            {
                return BusyForGroupDate(grp.Id, date).Count(b => b.RoomId != null
                                                                && b.BuildingId == buildingId);
            }
            // Штраф за зміну будівлі для групи або викладача.
            double BuildingDistancePenalty(int teacherId, Room? room, DateOnly date, TimeOnly start, TimeOnly end)
            {
                if (room is null || room.BuildingId == 0)
                    return 2.0 * buildingDistancePenaltyWeight;
                double score = 0;
                var targetBuildingId = room.BuildingId;
                var groupPrev = LastGroupBuilding(date, start);
                var groupNext = NextGroupBuilding(date, end);
                var teacherPrev = LastTeacherBuilding(teacherId, date, start);
                var teacherNext = NextTeacherBuilding(teacherId, date, end);
                var preferredGroupBuilding = PreferredGroupBuildingForDay(date);
                var groupBuildingUsage = CountGroupBuildingUsageForDay(date, targetBuildingId);
                if (groupPrev is int gb && gb != targetBuildingId) score += 1.8;
                if (groupNext is int gn && gn != targetBuildingId) score += 1.6;
                if (preferredGroupBuilding is int preferredBuilding
                    && preferredBuilding != targetBuildingId
                    && groupBuildingUsage == 0)
                {
                    score += 1.4;
                }
                if (teacherPrev is int tb && tb != targetBuildingId) score += 1.0;
                if (teacherNext is int tn && tn != targetBuildingId) score += 0.6;
                return score * buildingDistancePenaltyWeight;
            }
            // Перевіряє, чи є альтернатива для слоту, якщо модуль не вдається поставити.
            bool HasAvailableAlternativeForSlot(int currentModuleId, DateOnly date, TimeOnly start, TimeOnly end)
            {
                foreach (var altModuleId in orderedModules)
                {
                    if (altModuleId == currentModuleId) continue;
                    if (RemainingFor(grp.Id, altModuleId) <= 0) continue;
                    if (HasRecentModule(grp.Id, altModuleId, date)) continue;
                    bool altSelfStudy = SelfStudyRemaining(grp.Id, altModuleId) > 0;
                    var altTids = (altSelfStudy
                            ? supervisorsForModule.Where(x => x.ModuleId == altModuleId).Select(x => x.TeacherId)
                            : teachersForModule.Where(x => x.ModuleId == altModuleId).Select(x => x.TeacherId))
                        .Distinct()
                        .OrderBy(id => TeacherLoadPenalty(id))
                        .ThenBy(id => id)
                        .ToList();
                    if (altTids.Count == 0) continue;
                    var altRooms = CandidateRoomsFor(altModuleId);
                    foreach (var tid in altTids)
                    {
                        if (!TeacherFitsWorkingHours(tid, date, start, end))
                            continue;
                        bool peopleBusy = BusyForDate(date).Any(x =>
                            (x.GroupId == grp.Id || x.TeacherId == tid)
                            && x.StartTime < end && start < x.EndTime);
                        if (peopleBusy) continue;
                        ModuleTopic? altTopic = null;
                        int altLtId;
                        if (altSelfStudy)
                        {
                            altTopic = PeekSelfStudyTopic(grp.Id, altModuleId);
                            altLtId = altTopic?.LessonTypeId ?? PickLessonType(grp.Id, grp.CourseId, altModuleId, date).LessonTypeId;
                        }
                        else
                        {
                            var pick = PickLessonType(grp.Id, grp.CourseId, altModuleId, date);
                            altLtId = pick.LessonTypeId;
                            altTopic = pick.Topic;
                        }
                        if (altTopic?.DepartmentId is int altDepId
                            && altDepId > 0
                            && (!teacherDepartmentById.TryGetValue(tid, out var teacherDep) || teacherDep != altDepId))
                        {
                            continue;
                        }
                        if (!TypeAllowed(altLtId)) continue;
                        var altSlotGroupLimit = SlotGroupLimitForPlacement(grp.CourseId, altLtId, altSelfStudy);
                        if (CountGroupsWithModuleInSlot(altModuleId, date, start, end) >= altSlotGroupLimit) continue;
                        var requiresRoom = (typeById.TryGetValue(altLtId, out var ltMetaAlt) ? ltMetaAlt.RequiresRoom : (bool?)null) ?? true;
                        if (requiresRoom)
                        {
                            if (altRooms.Count == 0) continue;
                            foreach (var rm in altRooms)
                            {
                                bool roomBusy = HasRoomOverlap(rm.Id, date, start, end);
                                if (roomBusy) continue;
                                return true;
                            }
                        }
                        else
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
            // Сортуємо модулі так, щоб уникати повторів з минулого тижня.
            IEnumerable<int> PreferNotUsedLastWeek(IEnumerable<int> modules)
            {
                var list = modules.ToList();
                var preferred = list.Where(mid => !UsedLastWeek(grp.Id, mid) && RemainingFor(grp.Id, mid) > 0).ToList();
                var rest = list.Where(mid => !preferred.Contains(mid)).ToList();
                return preferred.Concat(rest);
            }
            List<int> BuildOrderedModulesForDay(DateOnly date)
            {
                var rng = new Random(StableSeed(weekStart.DayNumber, grp.Id, grp.CourseId, date.DayNumber, 23));
                var ordered = new List<int>();
                var seen = new HashSet<int>();
                foreach (var group in mainGroupsOrdered)
                {
                    var groupModules = group.ModuleIds.ToList();
                    for (var i = groupModules.Count - 1; i > 0; i--)
                    {
                        var j = rng.Next(i + 1);
                        (groupModules[i], groupModules[j]) = (groupModules[j], groupModules[i]);
                    }
                    foreach (var mid in groupModules)
                    {
                        if (seen.Add(mid))
                        {
                            ordered.Add(mid);
                        }
                    }
                }
                foreach (var mid in fillerModulesOrdered)
                {
                    if (seen.Add(mid))
                    {
                        ordered.Add(mid);
                    }
                }
                foreach (var mid in orderedModules)
                {
                    if (seen.Add(mid))
                    {
                        ordered.Add(mid);
                    }
                }
                return ordered;
            }
            // Підбирає аудиторії, які підходять під обмеження модуля і місткість групи.
            List<Room> CandidateRoomsForGroup(int groupId, int mid, int requiredCapacity = -1, bool ignoreGroupPreference = false)
            {
                allowedRoomsByModule.TryGetValue(mid, out var allowedRooms);
                allowedBuildingsByModule.TryGetValue(mid, out var allowedBuildings);
                var minCapacity = requiredCapacity > 0 ? requiredCapacity : grp.StudentsCount;
                return roomsAll
                    .Where(rm => (allowedBuildings == null || allowedBuildings.Count == 0 || allowedBuildings.Contains(rm.BuildingId))
                                 && (allowedRooms == null || allowedRooms.Count == 0 || allowedRooms.Contains(rm.Id))
                                 && (ignoreGroupPreference || RoomMatchesGroupPreference(groupId, rm))
                                 && rm.Capacity >= minCapacity)
                    .OrderBy(rm => groupRoomUsage.TryGetValue(rm.Id, out var used) ? used : 0)
                    .ThenBy(rm => rm.Capacity)
                    .ThenBy(rm => rm.Id)
                    .ToList();
            }
            List<Room> CandidateRoomsFor(int mid, int requiredCapacity = -1, bool ignoreGroupPreference = false)
                => CandidateRoomsForGroup(grp.Id, mid, requiredCapacity, ignoreGroupPreference);
            // Впорядковує аудиторії під конкретний тип розміщення.
            IReadOnlyList<Room> OrderCandidateRoomsForPlacement(IReadOnlyList<Room> candidateRooms, bool isShareableLecturePlacement)
            {
                return isShareableLecturePlacement
                    ? candidateRooms
                        .OrderBy(rm => groupRoomUsage.TryGetValue(rm.Id, out var used) ? used : 0)
                        .ThenByDescending(rm => rm.Capacity)
                        .ThenBy(rm => rm.Id)
                        .ToList()
                    : candidateRooms;
            }
            // Зміна аудиторії в суміжному блоці є небажаною, але не має створювати неповну чернетку, якщо є вільна аудиторія.
            double AdjacentRoomSwitchPenalty()
            {
                if (adjacentRoomChangePenalty is double overridePenalty && overridePenalty >= 0)
                {
                    return overridePenalty;
                }
                return softFill ? 4.0 : 12.0;
            }
            // Оцінює, скільки додаткових груп ще може вмістити спільна лекція в обраній аудиторії.
            int AdditionalSharedLectureGroupCapacity(Room room, int sharedStudents, int totalSharedGroupCount)
            {
                if (room.Capacity <= sharedStudents)
                {
                    return 0;
                }
                if (!selectedGroupsByCourse.TryGetValue(grp.CourseId, out var sameCourseGroups) || sameCourseGroups.Count == 0)
                {
                    return 0;
                }
                var averageGroupSize = Math.Max(1, (int)Math.Ceiling(sameCourseGroups.Average(x => x.StudentsCount)));
                var remainingCapacity = Math.Max(0, room.Capacity - sharedStudents);
                var remainingGroupSlots = Math.Max(0, sameCourseGroups.Count - totalSharedGroupCount);
                return Math.Min(remainingGroupSlots, remainingCapacity / averageGroupSize);
            }
            // Додає дефіцитний пріоритет до аудиторій:
            // великі аудиторії бережемо під спільні лекції, а для спільних лекцій навпаки даємо бонус за запас.
            double RoomScarcityPenalty(
                Room room,
                IReadOnlyList<Room> candidateRooms,
                int requiredCapacity,
                bool isLecturePlacement,
                bool isShareableLecturePlacement,
                int totalSharedStudents,
                int totalSharedGroupCount)
            {
                var capacityReserve = Math.Max(0, room.Capacity - totalSharedStudents);
                if (isShareableLecturePlacement)
                {
                    var futureGroupHeadroom = AdditionalSharedLectureGroupCapacity(room, totalSharedStudents, totalSharedGroupCount);
                    return capacityReserve * 0.04 - futureGroupHeadroom * 8.0;
                }
                var tighterAlternatives = candidateRooms.Count(other =>
                    other.Id != room.Id
                    && other.Capacity >= requiredCapacity
                    && other.Capacity < room.Capacity);
                if (!isLecturePlacement || tighterAlternatives == 0 || capacityReserve < 30)
                {
                    return 0;
                }
                return Math.Min(10.0, tighterAlternatives * 1.3 + capacityReserve * 0.02);
            }
            // Остання будівля групи до поточного слоту.
            int? LastGroupBuilding(DateOnly date, TimeOnly start)
            {
                return BusyForGroupDate(grp.Id, date)
                    .Where(b => b.EndTime <= start && b.RoomId != null)
                    .OrderBy(b => b.EndTime)
                    .LastOrDefault()?.BuildingId;
            }
            // Остання будівля викладача до поточного слоту.
            int? LastTeacherBuilding(int teacherId, DateOnly date, TimeOnly start)
            {
                return BusyForTeacherDate(teacherId, date)
                    .Where(b => b.EndTime <= start && b.RoomId != null)
                    .OrderBy(b => b.EndTime)
                    .LastOrDefault()?.BuildingId;
            }
            // Повертає набір груп для спільної лекції в одному слоті.
            bool RoomMatchesGroupPreference(int groupId, Room room)
            {
                if (!groupRoomPreferencesByGroupId.TryGetValue(groupId, out var pref))
                {
                    return true;
                }
                if (pref.BuildingId is int preferredBuildingId && room.BuildingId != preferredBuildingId)
                {
                    return false;
                }
                return pref.RoomIds.Count == 0 || pref.RoomIds.Contains(room.Id);
            }
            bool ViolatesTravelFeasibility(
                Func<BusySlot, bool> ownerMatch,
                Room? room,
                DateOnly date,
                TimeOnly start,
                TimeOnly end,
                string subjectLabel,
                out string reason)
            {
                reason = string.Empty;
                if (room is null || room.BuildingId == 0)
                {
                    return false;
                }
                var targetBuildingId = room.BuildingId;
                foreach (var existing in BusyForDate(date).Where(existing =>
                             existing.RoomId != null
                             && existing.BuildingId.HasValue
                             && ownerMatch(existing)))
                {
                    var sourceBuildingId = existing.BuildingId!.Value;
                    if (sourceBuildingId == targetBuildingId)
                    {
                        continue;
                    }
                    var needMinutes = TravelMinutes(sourceBuildingId, targetBuildingId);
                    var gapBefore = (start.ToTimeSpan() - existing.EndTime.ToTimeSpan()).TotalMinutes;
                    var gapAfter = (existing.StartTime.ToTimeSpan() - end.ToTimeSpan()).TotalMinutes;
                    if (existing.EndTime <= start && gapBefore < needMinutes)
                    {
                        reason = $"Для {subjectLabel} недостатньо часу на перехід до корпусу #{targetBuildingId} після заняття в корпусі #{sourceBuildingId}: доступно {gapBefore:N0} хв, потрібно {needMinutes} хв.";
                        return true;
                    }
                    if (end <= existing.StartTime && gapAfter < needMinutes)
                    {
                        reason = $"Для {subjectLabel} недостатньо часу на перехід від корпусу #{sourceBuildingId} до корпусу #{targetBuildingId} перед заняттям: доступно {gapAfter:N0} хв, потрібно {needMinutes} хв.";
                        return true;
                    }
                }
                return false;
            }
            IReadOnlyList<int> ResolveSharedLectureGroups(
                int moduleId,
                int lessonTypeId,
                ModuleTopic? topic,
                bool isSelfStudyPlacement,
                IReadOnlyCollection<int> alreadySharedGroupIds,
                int maxModuleSegmentsAllowed,
                DateOnly date,
                TimeOnly start,
                TimeOnly end,
                Room room)
            {
                var sharedGroupIds = new List<int> { grp.Id };
                if (isSelfStudyPlacement || !CanShareAcrossGroups(lessonTypeId))
                {
                    return sharedGroupIds;
                }
                if (!selectedGroupsByCourse.TryGetValue(grp.CourseId, out var sameCourseGroups) || sameCourseGroups.Count <= 1)
                {
                    return sharedGroupIds;
                }
                var alreadySharedGroupSet = alreadySharedGroupIds.ToHashSet();
                int totalStudents = SharedStudentsCount(alreadySharedGroupIds.ToList()) + grp.StudentsCount;
                foreach (var otherGroup in sameCourseGroups)
                {
                    if (sharedGroupIds.Count + alreadySharedGroupIds.Count >= sameCourseGroups.Count)
                    {
                        break;
                    }
                    if (otherGroup.Id == grp.Id || alreadySharedGroupSet.Contains(otherGroup.Id))
                    {
                        continue;
                    }
                    if (!IsWorking(date, otherGroup))
                    {
                        continue;
                    }
                    if (PlacementRemainingFor(otherGroup.Id, moduleId) <= 0)
                    {
                        continue;
                    }
                    if (CountFor(otherGroup.Id, date) >= slots.Count)
                    {
                        continue;
                    }
                    if (!softFill && TopicsDepleted(otherGroup.Id, moduleId))
                    {
                        continue;
                    }
                    if (topic is not null && !CanAssignSpecificTopic(otherGroup.Id, moduleId, topic))
                    {
                        continue;
                    }
                    if (topic is not null && ViolatesTopicCalendarOrder(otherGroup.Id, moduleId, topic, date, start, end))
                    {
                        continue;
                    }
                    if (!RoomMatchesGroupPreference(otherGroup.Id, room))
                    {
                        continue;
                    }
                    if (totalStudents + otherGroup.StudentsCount > room.Capacity)
                    {
                        continue;
                    }
                    bool groupBusy = HasGroupOverlap(otherGroup.Id, date, start, end);
                    if (groupBusy)
                    {
                        continue;
                    }
                    if (ViolatesAnyModuleDayBlock(otherGroup.Id, date, moduleId, start, end, out _, maxModuleSegmentsAllowed))
                    {
                        continue;
                    }
                    if (InsertionWouldSplitExistingModuleBlock(otherGroup.Id, date, moduleId, start, end, out _, maxModuleSegmentsAllowed))
                    {
                        continue;
                    }
                    sharedGroupIds.Add(otherGroup.Id);
                    totalStudents += otherGroup.StudentsCount;
                }
                return sharedGroupIds;
            }
            bool HasFeasiblePendingShareablePartner(int moduleId, ModuleTopic? topic)
            {
                if (!selectedGroupsByCourse.TryGetValue(grp.CourseId, out var sameCourseGroups) || sameCourseGroups.Count <= 1)
                {
                    return false;
                }
                allowedRoomsByModule.TryGetValue(moduleId, out var allowedRooms);
                allowedBuildingsByModule.TryGetValue(moduleId, out var allowedBuildings);
                foreach (var otherGroup in sameCourseGroups)
                {
                    if (otherGroup.Id == grp.Id)
                    {
                        continue;
                    }
                    if (PlacementRemainingFor(otherGroup.Id, moduleId) <= 0)
                    {
                        continue;
                    }
                    if (!softFill && TopicsDepleted(otherGroup.Id, moduleId))
                    {
                        continue;
                    }
                    if (topic is not null && !CanAssignSpecificTopic(otherGroup.Id, moduleId, topic))
                    {
                        continue;
                    }
                    var combinedStudents = grp.StudentsCount + otherGroup.StudentsCount;
                    var roomExists = roomsAll.Any(room =>
                        room.Capacity >= combinedStudents
                        && (allowedBuildings == null || allowedBuildings.Count == 0 || allowedBuildings.Contains(room.BuildingId))
                        && (allowedRooms == null || allowedRooms.Count == 0 || allowedRooms.Contains(room.Id))
                        && RoomMatchesGroupPreference(grp.Id, room)
                        && RoomMatchesGroupPreference(otherGroup.Id, room));
                    if (roomExists)
                    {
                        return true;
                    }
                }
                return false;
            }
            // Рахує сумарну кількість слухачів для груп у спільній парі.
            bool HasFuturePendingShareablePartner(int moduleId, ModuleTopic? topic)
            {
                if (topic is null
                    || !selectedGroupsByCourse.TryGetValue(grp.CourseId, out var sameCourseGroups)
                    || sameCourseGroups.Count <= 1)
                {
                    return false;
                }
                allowedRoomsByModule.TryGetValue(moduleId, out var allowedRooms);
                allowedBuildingsByModule.TryGetValue(moduleId, out var allowedBuildings);
                foreach (var otherGroup in sameCourseGroups)
                {
                    if (otherGroup.Id == grp.Id)
                    {
                        continue;
                    }
                    if (PlacementRemainingFor(otherGroup.Id, moduleId) <= 0)
                    {
                        continue;
                    }
                    if (!IsTopicStillPendingForGroup(otherGroup.Id, moduleId, topic))
                    {
                        continue;
                    }
                    var combinedStudents = grp.StudentsCount + otherGroup.StudentsCount;
                    var roomExists = roomsAll.Any(room =>
                        room.Capacity >= combinedStudents
                        && (allowedBuildings == null || allowedBuildings.Count == 0 || allowedBuildings.Contains(room.BuildingId))
                        && (allowedRooms == null || allowedRooms.Count == 0 || allowedRooms.Contains(room.Id))
                        && RoomMatchesGroupPreference(grp.Id, room)
                        && RoomMatchesGroupPreference(otherGroup.Id, room));
                    if (roomExists)
                    {
                        return true;
                    }
                }
                return false;
            }
            bool HasJoinableFutureShareableGroupOutside(
                int moduleId,
                ModuleTopic? topic,
                Room room,
                DateOnly date,
                TimeOnly start,
                TimeOnly end,
                IReadOnlyCollection<int> currentGroupIds)
            {
                if (topic is null
                    || !selectedGroupsByCourse.TryGetValue(grp.CourseId, out var sameCourseGroups)
                    || sameCourseGroups.Count <= 1)
                {
                    return false;
                }
                var currentGroupSet = currentGroupIds.ToHashSet();
                var totalStudents = SharedStudentsCount(currentGroupIds.ToList());
                foreach (var otherGroup in sameCourseGroups)
                {
                    if (currentGroupSet.Contains(otherGroup.Id))
                    {
                        continue;
                    }
                    if (PlacementRemainingFor(otherGroup.Id, moduleId) <= 0)
                    {
                        continue;
                    }
                    if (!CanAssignSpecificTopic(otherGroup.Id, moduleId, topic))
                    {
                        continue;
                    }
                    if (!RoomMatchesGroupPreference(otherGroup.Id, room))
                    {
                        continue;
                    }
                    if (totalStudents + otherGroup.StudentsCount > room.Capacity)
                    {
                        continue;
                    }
                    var groupBusy = HasGroupOverlap(otherGroup.Id, date, start, end);
                    if (groupBusy)
                    {
                        continue;
                    }
                    if (ViolatesTravelFeasibility(
                            existing => existing.GroupId == otherGroup.Id,
                            room,
                            date,
                            start,
                            end,
                            $"групи {otherGroup.Name}",
                            out _))
                    {
                        continue;
                    }
                    return true;
                }
                return false;
            }
            int SharedStudentsCount(IReadOnlyList<int> groupIds)
            {
                int total = 0;
                foreach (var gid in groupIds)
                {
                    if (selectedGroupsById.TryGetValue(gid, out var g))
                    {
                        total += g.StudentsCount;
                    }
                }
                return total;
            }
            // Скидає кеші дефіцитності, коли після нового розміщення змінюється карта зайнятості.
            void InvalidateGapResourceCaches()
            {
                teacherDaySlackCache.Clear();
                roomDaySlackCache.Clear();
                neighborGapBuildingPenaltyCache.Clear();
            }
            // Визначає, наскільки агресивно треба берегти рідкісні ресурси для інших gap-слотів.
            double GapResourcePreservationWeight(int? forcedGapVariantBudget)
            {
                if (!softFill || forcedGapVariantBudget is null)
                {
                    return 0;
                }
                var baseWeight = forcedGapVariantBudget.Value switch
                {
                    <= 1 => 0.10,
                    2 => 0.22,
                    3 => 0.45,
                    4 => 0.80,
                    _ => 1.15
                };
                return baseWeight * 1.75;
            }
            // Рахує, у скількох слотах дня викладач узагалі залишається вільним.
            int CountTeacherFreeSlotsForDay(DateOnly day, int teacherId)
            {
                var cacheKey = (day, teacherId);
                if (teacherDaySlackCache.TryGetValue(cacheKey, out var cachedCount))
                {
                    return cachedCount;
                }
                var count = 0;
                foreach (var slot in slots)
                {
                    if (!TeacherFitsWorkingHours(teacherId, day, slot.Start, slot.End))
                    {
                        continue;
                    }
                    var isBusy = HasTeacherOverlap(teacherId, day, slot.Start, slot.End);
                    if (!isBusy)
                    {
                        count++;
                    }
                }
                teacherDaySlackCache[cacheKey] = count;
                return count;
            }
            // Рахує, у скількох слотах дня аудиторія ще досяжна для поточної групи без порушення переходів.
            int CountRoomReachableSlotsForDay(DateOnly day, Room room)
            {
                var cacheKey = (day, room.Id);
                if (roomDaySlackCache.TryGetValue(cacheKey, out var cachedCount))
                {
                    return cachedCount;
                }
                var count = 0;
                foreach (var slot in slots)
                {
                    var roomBusy = HasRoomOverlap(room.Id, day, slot.Start, slot.End);
                    if (roomBusy)
                    {
                        continue;
                    }
                    if (ViolatesTravelFeasibility(
                            existing => existing.GroupId == grp.Id,
                            room,
                            day,
                            slot.Start,
                            slot.End,
                            $"групи {grp.Name}",
                            out _))
                    {
                        continue;
                    }
                    count++;
                }
                roomDaySlackCache[cacheKey] = count;
                return count;
            }
            // Рахує, скільки корпусів ще досяжні для сусіднього порожнього слоту після вибору поточного корпусу.
            int CountReachableBuildingsForAdjacentGap(DateOnly day, TimeSlot currentSlot, TimeSlot adjacentGap, int currentBuildingId)
            {
                var reachableBuildings = new HashSet<int>();
                var adjacentGapBeforeCurrent = adjacentGap.End <= currentSlot.Start;
                foreach (var room in roomsAll)
                {
                    if (room.BuildingId == 0 || !RoomMatchesGroupPreference(grp.Id, room))
                    {
                        continue;
                    }
                    var roomBusy = HasRoomOverlap(room.Id, day, adjacentGap.Start, adjacentGap.End);
                    if (roomBusy)
                    {
                        continue;
                    }
                    var availableMinutes = adjacentGapBeforeCurrent
                        ? (currentSlot.Start.ToTimeSpan() - adjacentGap.End.ToTimeSpan()).TotalMinutes
                        : (adjacentGap.Start.ToTimeSpan() - currentSlot.End.ToTimeSpan()).TotalMinutes;
                    var needMinutes = adjacentGapBeforeCurrent
                        ? TravelMinutes(room.BuildingId, currentBuildingId)
                        : TravelMinutes(currentBuildingId, room.BuildingId);
                    if (availableMinutes < needMinutes)
                    {
                        continue;
                    }
                    reachableBuildings.Add(room.BuildingId);
                    if (reachableBuildings.Count >= 4)
                    {
                        break;
                    }
                }
                return reachableBuildings.Count;
            }
            // Штрафує корпус, який занадто сильно обмежує наступні або попередні порожні слоти.
            double NeighborGapBuildingPreservationPenalty(Room room, DateOnly day, TimeSlot currentSlot, int? forcedGapVariantBudget, bool isShareableLecturePlacement)
            {
                var preservationWeight = GapResourcePreservationWeight(forcedGapVariantBudget);
                if (preservationWeight <= 0 && isShareableLecturePlacement)
                {
                    preservationWeight = 18.0;
                }
                if (preservationWeight <= 0 || room.BuildingId == 0)
                {
                    return 0;
                }
                var gapBudget = forcedGapVariantBudget ?? 9;
                var cacheKey = (day, currentSlot.Start, currentSlot.End, room.BuildingId, gapBudget);
                if (neighborGapBuildingPenaltyCache.TryGetValue(cacheKey, out var cachedPenalty))
                {
                    return cachedPenalty;
                }
                var penalty = 0.0;
                if (slotIndexByTime.TryGetValue((currentSlot.Start, currentSlot.End), out var currentSlotIndex))
                {
                    foreach (var neighborIndex in new[] { currentSlotIndex - 1, currentSlotIndex + 1 })
                    {
                        if (neighborIndex < 0 || neighborIndex >= slots.Count)
                        {
                            continue;
                        }
                        var adjacentGap = slots[neighborIndex];
                        if (SlotFilled(day, adjacentGap))
                        {
                            continue;
                        }
                        var reachableBuildingCount = CountReachableBuildingsForAdjacentGap(day, currentSlot, adjacentGap, room.BuildingId);
                        penalty += reachableBuildingCount switch
                        {
                            <= 0 => 8.5,
                            1 => 5.5,
                            2 => 2.5,
                            _ => 0
                        };
                    }
                }
                penalty *= preservationWeight;
                neighborGapBuildingPenaltyCache[cacheKey] = penalty;
                return penalty;
            }
            // Бережемо викладачів, у яких лишається занадто мало вільних слотів на день.
            double TeacherScarcityReservationPenalty(int teacherId, DateOnly day, int? forcedGapVariantBudget)
            {
                var preservationWeight = GapResourcePreservationWeight(forcedGapVariantBudget);
                if (preservationWeight <= 0)
                {
                    return 0;
                }
                var freeSlots = CountTeacherFreeSlotsForDay(day, teacherId);
                var basePenalty = freeSlots switch
                {
                    <= 1 => 8.0,
                    2 => 5.0,
                    3 => 2.8,
                    4 => 1.2,
                    _ => 0
                };
                return basePenalty * preservationWeight;
            }
            // Бережемо аудиторії, які для групи лишаються досяжними лише в небагатьох слотах дня.
            double RoomReachabilityReservationPenalty(Room room, DateOnly day, int? forcedGapVariantBudget, bool isShareableLecturePlacement)
            {
                var preservationWeight = GapResourcePreservationWeight(forcedGapVariantBudget);
                if (preservationWeight <= 0)
                {
                    return 0;
                }
                var reachableSlots = CountRoomReachableSlotsForDay(day, room);
                var basePenalty = reachableSlots switch
                {
                    <= 1 => 7.0,
                    2 => 4.5,
                    3 => 2.5,
                    4 => 1.0,
                    _ => 0
                };
                if (isShareableLecturePlacement)
                {
                    basePenalty *= 0.45;
                }
                return basePenalty * preservationWeight;
            }
            // Визначає, який модуль вважаємо пріоритетним на поточний день.
            int? ResolvePrimaryModule(Func<IEnumerable<int>, IEnumerable<int>>? orderCandidates = null)
            {
                if (mainGroupsOrdered.Count == 0) return null;
                if (forceFirstMainModule && !firstMainPlaced && RemainingFor(grp.Id, firstMainModuleId) > 0)
                {
                    return firstMainModuleId;
                }
                var currentGroup = mainGroupsOrdered
                    .FirstOrDefault(g => g.ModuleIds.Any(mid => RemainingFor(grp.Id, mid) > 0));
                if (currentGroup is null)
                {
                    return null;
                }
                var candidates = currentGroup.ModuleIds
                    .Where(mid => RemainingFor(grp.Id, mid) > 0)
                    .ToList();
                if (candidates.Count == 0)
                {
                    return null;
                }
                var preferred = candidates
                    .Where(mid => !UsedLastWeek(grp.Id, mid))
                    .ToList();
                if (preferred.Count > 0)
                {
                    candidates = preferred;
                }
                if (lastPrimaryModuleId is int last && candidates.Count > 1)
                {
                    var filtered = candidates.Where(mid => mid != last).ToList();
                    if (filtered.Count > 0)
                    {
                        candidates = filtered;
                    }
                }
                if (orderCandidates is not null)
                {
                    var orderedCandidates = orderCandidates(candidates).ToList();
                    if (orderedCandidates.Count > 0)
                    {
                        return orderedCandidates[0];
                    }
                }
                return candidates[sequenceRandom.Next(candidates.Count)];
            }
            var generationDates = DatesBetween(rangeStartDate, rangeEndDateExclusive)
                .Where(date => IsWorking(date, grp))
                .ToList();
            int CountPendingPreferredFirstPlacements()
            {
                if (preferredFirstTypeId == 0 || !TypeAllowed(preferredFirstTypeId))
                {
                    return 0;
                }
                var total = 0;
                foreach (var moduleId in orderedModules.Concat(fillerModulesOrdered).Distinct())
                {
                    var remaining = PlacementRemainingFor(grp.Id, moduleId);
                    if (remaining <= 0)
                    {
                        continue;
                    }
                    var topicLeft = 0;
                    if (topicsByModule.TryGetValue(moduleId, out var topicList))
                    {
                        topicAssignments.TryGetValue((grp.Id, moduleId), out var assignedTopics);
                        foreach (var topic in topicList.Where(t => t.LessonTypeId == preferredFirstTypeId))
                        {
                            var limit = GetTopicUsageLimit(topic);
                            var used = assignedTopics is not null && assignedTopics.TryGetValue(topic.Id, out var usedCount)
                                ? usedCount
                                : 0;
                            topicLeft += Math.Max(0, limit - used);
                        }
                    }
                    if (topicLeft > 0)
                    {
                        total += Math.Min(remaining, topicLeft);
                    }
                    else if (!hasPreferred.Contains((grp.Id, moduleId)))
                    {
                        total++;
                    }
                }
                return total;
            }
            bool ShouldPrioritizePreferredFirstOnDate(DateOnly date)
            {
                if (preferredFirstTypeId == 0 || generationDates.Count <= 1)
                {
                    return true;
                }
                var dateIndex = generationDates.FindIndex(x => x == date);
                if (dateIndex < 0)
                {
                    return true;
                }
                var placedBeforeDate = DatesBetween(rangeStartDate, date)
                    .Sum(day => BusyForGroupDate(grp.Id, day).Count(b => b.LessonTypeId == preferredFirstTypeId));
                var estimatedTotal = placedBeforeDate + CountPendingPreferredFirstPlacements();
                if (estimatedTotal <= 1)
                {
                    return true;
                }
                var allowedBeforeDate = (int)Math.Floor(estimatedTotal * dateIndex / (double)generationDates.Count);
                return placedBeforeDate <= allowedBeforeDate;
            }
            bool PreferredFirstWouldExceedDateBudget(DateOnly date)
            {
                if (preferredFirstTypeId == 0 || generationDates.Count <= 1)
                {
                    return false;
                }
                var dateIndex = generationDates.FindIndex(x => x == date);
                if (dateIndex < 0)
                {
                    return false;
                }
                var placedThroughDate = DatesBetween(rangeStartDate, date.AddDays(1))
                    .Sum(day => BusyForGroupDate(grp.Id, day).Count(b => b.LessonTypeId == preferredFirstTypeId));
                var estimatedTotal = placedThroughDate + CountPendingPreferredFirstPlacements();
                if (estimatedTotal <= 1)
                {
                    return false;
                }
                var allowedThroughDate = (int)Math.Ceiling(estimatedTotal * (dateIndex + 1) / (double)generationDates.Count);
                return placedThroughDate + 1 > allowedThroughDate;
            }
            bool HasNonPreferredAlternativeForDate(int currentModuleId, DateOnly date)
            {
                if (preferredFirstTypeId == 0)
                {
                    return false;
                }
                var savedLtIndex = ltIndex;
                try
                {
                    foreach (var alternativeModuleId in orderedModules.Concat(fillerModulesOrdered).Distinct())
                    {
                        if (alternativeModuleId == currentModuleId)
                        {
                            continue;
                        }
                        if (RemainingFor(grp.Id, alternativeModuleId) <= 0)
                        {
                            continue;
                        }
                        if (!softFill && TopicsDepleted(grp.Id, alternativeModuleId))
                        {
                            continue;
                        }
                        if (PeekLessonTypeForDate(grp.Id, grp.CourseId, alternativeModuleId, date) != preferredFirstTypeId)
                        {
                            return true;
                        }
                    }
                }
                finally
                {
                    ltIndex = savedLtIndex;
                }
                return false;
            }
            // Основна спроба розмістити модуль у межах конкретного дня.
            async Task<bool> TryPlaceModuleAsync(int moduleId, DateOnly date, bool isPrimary, bool allowRepeatPreviousDay = false, bool allowExtraSameDay = false, bool relaxed = false, bool preferEarliestSlot = true, TimeSlot? forcedSlot = null, int? forcedGapVariantBudget = null, bool bypassCatchUpHold = false, int? maxModuleSegmentsOverride = null)
            {
                // Якщо день вже заповнений або порушуємо правило першого головного модуля — виходимо.
                if (CountFor(grp.Id, date) >= slots.Count)
                    return false;
                if (forceFirstMainModule && !firstMainPlaced && moduleId != firstMainModuleId && !relaxed)
                    return false;
                // Ключ для залишків по групі/модулю.
                var remainingKey = (grp.Id, moduleId);
                // Ознака, що модуль належить до fillers.
                bool isFiller = fillerLookup.Contains(moduleId);
                string? moduleTitle = null;
                // Чи використовуємо пріоритетний тип занять.
                bool preferredFirstEnabled = preferredFirstTypeId != 0 && TypeAllowed(preferredFirstTypeId);
                bool preferredFirstPendingToday = false;
                var lectureFirstPendingToday = HasPendingLectureFirstPlacementForDate(date);
                if (preferredFirstEnabled
                    && ShouldPrioritizePreferredFirstOnDate(date)
                    && (penaltyPreferredFirstTypeLateSlot > 0 || penaltyNonPreferredEarlySlotWhilePreferredPending > 0))
                {
                    // Визначаємо, чи є сьогодні ще теми з пріоритетним типом.
                    foreach (var mid in orderedModules)
                    {
                        if (RemainingFor(grp.Id, mid) <= 0) continue;
                        if (PeekLessonTypeForDate(grp.Id, grp.CourseId, mid, date) == preferredFirstTypeId)
                        {
                            preferredFirstPendingToday = true;
                            break;
                        }
                    }
                }
                // Ледачо завантажуємо назву модуля для повідомлень.
                async Task<bool> EnsureModuleTitleAsync()
                {
                    if (moduleTitle is not null)
                    {
                        return true;
                    }
                    moduleTitle = await _db.Modules
                        .Where(m => m.Id == moduleId)
                        .Select(m => m.Title)
                        .FirstOrDefaultAsync();
                    if (moduleTitle is null)
                    {
                        if (missingModulesNotified.Add(moduleId))
                        {
                            warnings.Add($"В базі даних відсутній модуль із ідентифікатором {moduleId}. Заняття для нього не створено.");
                        }
                        skipped++;
                        return false;
                    }
                    return true;
                }
                // Лейбл модуля для текстів попереджень.
                string ModuleLabel() => string.IsNullOrWhiteSpace(moduleTitle) ? $"#{moduleId}" : moduleTitle!;
                // За ручного плану залишок годин є жорсткою верхньою межею і для filler-модулів.
                if ((hasModuleHourOverrides || !isFiller) && PlacementRemainingFor(grp.Id, moduleId) <= 0)
                {
                    return false;
                }
                // Якщо модуль відсутній у БД — пропускаємо.
                if (!await EnsureModuleTitleAsync())
                {
                    return false;
                }
                // Якщо теми вичерпано — прибираємо модуль з плану.
                if (!softFill && TopicsDepleted(grp.Id, moduleId))
                {
                    if (remainingByGroupModule.ContainsKey(remainingKey))
                    {
                        remainingByGroupModule[remainingKey] = 0;
                    }
                    if (topicsExhaustedNotified.Add(remainingKey))
                    {
                        warnings.Add($"Для модуля <{ModuleLabel()}> у групи {grp.Name} вичерпано теми. Пропустили розкладення.");
                    }
                    var topicReason = $"Для модуля <{ModuleLabel()}> у групи {grp.Name} вичерпано теми для цього тижня.";
                    RecordSlotFailureReasonForAllSlots(date, topicReason);
                    return false;
                }
                // Визначаємо, чи ставимо самостійну роботу.
                bool placeSelfStudy = SelfStudyRemaining(grp.Id, moduleId) > 0;
                // Підбираємо кандидатів-викладачів/керівників.
                var tids = (placeSelfStudy
                        ? supervisorsForModule.Where(x => x.ModuleId == moduleId).Select(x => x.TeacherId)
                        : teachersForModule.Where(x => x.ModuleId == moduleId).Select(x => x.TeacherId))
                    .Distinct()
                    .OrderBy(id => TeacherLoadPenalty(id))
                    .ThenBy(id => id)
                    .ToList();
                // Якщо немає викладачів — фіксуємо причину і пропускаємо.
                if (tids.Count == 0)
                {
                    var teacherReason = placeSelfStudy
                        ? $"Не знайдено керівників для модуля <{ModuleLabel()}> (група {grp.Name}). Самостійну годину не створено."
                        : $"Не знайдено викладачів для модуля <{ModuleLabel()}> (група {grp.Name}).";
                    RecordSlotFailureReasonForAllSlots(date, teacherReason);
                    warnings.Add(teacherReason);
                    if (placeSelfStudy && !allowIncompleteDrafts)
                    {
                        var key = (grp.Id, moduleId);
                        if (selfStudyRemainingByGroupModule.ContainsKey(key))
                            selfStudyRemainingByGroupModule[key] = 0;
                    }
                    if (!allowIncompleteDrafts)
                    {
                        skipped++;
                        return false;
                    }
                }
                // Кандидатні аудиторії для модуля.
                var candidateRooms = CandidateRoomsFor(moduleId, ignoreGroupPreference: softFill);
                var maxModuleSegmentsAllowed = Math.Max(maxModuleSegmentsPerDay, maxModuleSegmentsOverride ?? maxModuleSegmentsPerDay);
                var allowEmergencyTopicOrderRelaxation = softFill && forcedSlot is not null && bypassCatchUpHold;
                var targetDistinctModulesForPlacement = Math.Min(3, Math.Min(slots.Count, orderedModules.Concat(fillerModulesOrdered).Distinct().Count(mid => RemainingFor(grp.Id, mid) > 0 || CountModuleForDay(grp.Id, date, mid) > 0)));
                PlacementCandidate? best = null;
                double bestEffectivePenalty = double.MaxValue;
                IncompletePlacementCandidate? bestIncomplete = null;
                double bestIncompleteEffectivePenalty = double.MaxValue;
                int PickEmergencyUnthemedLessonType(int currentLessonTypeId)
                {
                    var fallback = activeStudyTypes
                        .Select(t => t.Id)
                        .Where(TypeAllowed)
                        .Where(id => !CanShareAcrossGroups(id))
                        .FirstOrDefault();
                    if (fallback != 0)
                    {
                        return fallback;
                    }
                    return TypeAllowed(currentLessonTypeId) && !CanShareAcrossGroups(currentLessonTypeId)
                        ? currentLessonTypeId
                        : 0;
                }
                // Перебираємо слоти дня та шукаємо найкращого кандидата.
                var slotsToEvaluate = forcedSlot is null
                    ? slots
                    : slots.Where(sl => sl.Start == forcedSlot.Start && sl.End == forcedSlot.End);
                foreach (var sl in slotsToEvaluate)
                {
                    if (CountFor(grp.Id, date) >= slots.Count) break;
                    var s = sl.Start;
                    var e = sl.End;
                    var slotLabel = $"{s:HH\\:mm}-{e:HH\\:mm}";
                    // Тимчасові колекції причин та кандидатів для цього слоту.
                    var slotIssues = new HashSet<string>();
                    var slotCandidates = new List<PlacementCandidate>();
                    int slotIndex = slotIndexByTime.TryGetValue((s, e), out var si) ? si : 0;
                    var sameModuleIndexes = busy
                        .Where(b => b.GroupId == grp.Id
                                    && b.Date == date
                                    && b.ModuleId == moduleId
                                    && !excludedTypeIds.Contains(b.LessonTypeId))
                        .Select(b => slotIndexByTime.TryGetValue((b.StartTime, b.EndTime), out var existingIndex) ? existingIndex : -1)
                        .Where(idx => idx >= 0)
                        .Distinct()
                        .OrderBy(idx => idx)
                        .ToList();
                    var sameDayModuleCountBefore = sameModuleIndexes.Count;
                    var distinctModulesBeforePlacement = CountDistinctModulesForDay(grp.Id, date);
                    var startsNewDistinctModule = sameDayModuleCountBefore == 0;
                    var expandsContiguousModuleBlock = sameModuleIndexes.Any(index => Math.Abs(index - slotIndex) == 1);
                    // Викладач у сусідньому слоті (для пріоритету суміжних пар).
                    var preferredAdjacentTeacherId = GetAdjacentSameModuleTeacherId(date, sl, moduleId);
                    // Аудиторія у сусідньому слоті (для фіксації однієї аудиторії в блоці модуля).
                    var preferredAdjacentRoomId = GetAdjacentSameModuleRoomId(date, sl, moduleId);
                    // Не дозволяємо ставити інші модулі до першого головного.
                    if (forceFirstMainModule && firstMainPlaced && moduleId != firstMainModuleId && IsBeforeFirstMain(date, s))
                    {
                        RecordSlotFailureReason(date, sl, "Слот до першого головного модуля заблоковано.");
                        continue;
                    }
                    // Слот зайнятий перервою (BREAK).
                    bool slotBreak = BusyForGroupDate(grp.Id, date).Any(b =>
                        b.LessonTypeId == typeBreakId && b.StartTime == s && b.EndTime == e);
                    if (slotBreak)
                    {
                        RecordSlotFailureReason(date, sl, $"Слот {slotLabel} зайнятий перервою (BREAK).");
                        continue;
                    }
                    if (ViolatesAnyModuleDayBlock(grp.Id, date, moduleId, s, e, out var hardRuleReason, maxModuleSegmentsAllowed))
                    {
                        RecordSlotFailureReason(date, sl, hardRuleReason);
                        continue;
                    }
                    if (InsertionWouldSplitExistingModuleBlock(grp.Id, date, moduleId, s, e, out var insertionSplitReason, maxModuleSegmentsAllowed))
                    {
                        RecordSlotFailureReason(date, sl, insertionSplitReason);
                        continue;
                    }
                    bool hasRecent = HasRecentModule(grp.Id, moduleId, date);
                    if (!allowRepeatPreviousDay && hasRecent && HasAvailableAlternativeForSlot(moduleId, date, s, e))
                    {
                        RecordSlotFailureReason(date, sl, $"Модуль <{ModuleLabel()}> не ставимо у вікні ±2 дні, поки є інші модулі з годинами.");
                        continue;
                    }
                    // Спроба розмістити самостійну роботу, якщо вона ще лишилась.
                    var isSelfStudyPlacement = placeSelfStudy && SelfStudyRemaining(grp.Id, moduleId) > 0;
                    ModuleTopic? topicSelection = null;
                    int ltypeId = 0;
                    if (isSelfStudyPlacement)
                    {
                        topicSelection = PeekSelfStudyTopic(grp.Id, moduleId);
                        if (topicSelection is null)
                        {
                            selfStudyRemainingByGroupModule[remainingKey] = 0;
                            isSelfStudyPlacement = false;
                        }
                        else
                        {
                            ltypeId = topicSelection.LessonTypeId;
                        }
                    }
                    // Якщо самостійна робота не підходить — обираємо звичайний тип/тему.
                    if (!isSelfStudyPlacement)
                    {
                        var pickResult = PickLessonType(grp.Id, grp.CourseId, moduleId, date);
                        ltypeId = pickResult.LessonTypeId;
                        topicSelection = pickResult.Topic;
                    }
                    bool emergencyUnthemedTopic = false;
                    void TryUseEmergencyUnthemedTopic(string note)
                    {
                        if (!allowEmergencyTopicOrderRelaxation
                            || isSelfStudyPlacement
                            || topicSelection is null
                            || ModuleHasUsableTopics(moduleId))
                        {
                            return;
                        }
                        var fallbackLessonTypeId = PickEmergencyUnthemedLessonType(ltypeId);
                        if (fallbackLessonTypeId == 0)
                        {
                            return;
                        }
                        ltypeId = fallbackLessonTypeId;
                        topicSelection = null;
                        emergencyUnthemedTopic = true;
                        slotIssues.Add(note);
                    }
                    var emergencySlotOrder = GetSlotOrder(sl.Start, sl.End);
                    if (IsBlockedLateLectureSlot(ltypeId, emergencySlotOrder, preferredFirstMaxSlotOrder))
                    {
                        TryUseEmergencyUnthemedTopic("Аварійне дозаповнення замінило тему на заняття без коду теми, щоб не блокувати слот лекційним типом.");
                    }
                    if (topicSelection is not null
                        && ViolatesTopicCalendarOrder(grp.Id, moduleId, topicSelection, date, s, e))
                    {
                        var safeOverflowTopic = SelectCalendarSafeOverflowTopic(grp.Id, moduleId, date, s, e);
                        if (safeOverflowTopic is not null)
                        {
                            topicSelection = safeOverflowTopic;
                            ltypeId = safeOverflowTopic.LessonTypeId;
                        }
                        else
                        {
                            TryUseEmergencyUnthemedTopic("Аварійне дозаповнення замінило тему на заняття без коду теми, щоб не ламати хронологію тем.");
                        }
                    }
                    if (!TypeAllowed(ltypeId))
                    {
                        string reason;
                        if (!typeById.TryGetValue(ltypeId, out var ltInfo))
                        {
                            reason = $"Тип заняття #{ltypeId} не знайдено, тому слот {slotLabel} не створено.";
                        }
                        else if (!ltInfo.IsActive)
                        {
                            reason = $"Тип заняття \"{ltInfo.Name}\" неактивний, тому слот {slotLabel} не створено.";
                        }
                        else if (!ltInfo.CountInPlan)
                        {
                            reason = $"Тип заняття \"{ltInfo.Name}\" не враховується у плані (CountInPlan=false), тому слот {slotLabel} не створено.";
                        }
                        else if (excludedTypeIds.Contains(ltypeId))
                        {
                            reason = $"Тип заняття \"{ltInfo.Name}\" виключено з автогенерації, тому слот {slotLabel} не створено.";
                        }
                        else
                        {
                            reason = $"Тип заняття #{ltypeId} недоступний для автогенерації, тому слот {slotLabel} не створено.";
                        }
                        RecordSlotFailureReason(date, sl, reason);
                        continue;
                    }
                    var preferredFirstWeekBalancePenalty = 0.0;
                    var preferredFirstWeekBalanceNote = string.Empty;
                    var currentSlotOrder = GetSlotOrder(sl.Start, sl.End);
                    if (IsBlockedLateLectureSlot(ltypeId, currentSlotOrder, preferredFirstMaxSlotOrder))
                    {
                        RecordSlotFailureReason(
                            date,
                            sl,
                            $"Лекційний тип не можна ставити у слот №{currentSlotOrder}: після 6-ї години замалий перехід між корпусами, а аварійний резерв починається з 9-ї.");
                        continue;
                    }
                    if (preferredFirstEnabled
                        && ltypeId == preferredFirstTypeId
                        && !CanShareAcrossGroups(ltypeId)
                        && PreferredFirstWouldExceedDateBudget(date)
                        && HasNonPreferredAlternativeForDate(moduleId, date)
                        && forcedSlot is null
                        && !relaxed)
                    {
                        preferredFirstWeekBalancePenalty = 34.0 * preferredFirstPenaltyMultiplier;
                        preferredFirstWeekBalanceNote = "Тип з прапорцем \"Бажано першим у тижні\" поставлено раніше тижневого балансу, щоб не лишати порожній слот";
                    }
                    // Лекційні потоки лімітуємо кількістю вибраних груп курсу, а не фіксованою стелею.
                    if (!isSelfStudyPlacement
                        && topicSelection is not null
                        && CanShareAcrossGroups(ltypeId)
                        && HasUnreadySelectedGroupForShareableTopic(grp.CourseId, moduleId, topicSelection))
                    {
                        RecordSlotFailureReason(
                            date,
                            sl,
                            $"Спільну лекційну тему {topicSelection.TopicCode} модуля <{ModuleLabel()}> відкладено, доки всі вибрані групи курсу дійдуть до неї за порядком тем.");
                        continue;
                    }
                    var preselectedSlotGroupLimit = SlotGroupLimitForPlacement(grp.CourseId, ltypeId, isSelfStudyPlacement);
                    if (CountGroupsWithModuleInSlot(moduleId, date, s, e) >= preselectedSlotGroupLimit)
                    {
                        continue;
                    }
                    if (!isSelfStudyPlacement
                        && topicSelection is not null
                        && ViolatesTopicCalendarOrder(grp.Id, moduleId, topicSelection, date, s, e))
                    {
                        RecordSlotFailureReason(
                            date,
                            sl,
                            $"Для групи {grp.Name} порушується хронологічний порядок тем модуля <{ModuleLabel()}> у слоті {slotLabel}.");
                        continue;
                    }
                    var catchUpHoldBypassed = false;
                    ModuleTopic? pendingSharedTopic = null;
                    if (!isSelfStudyPlacement
                        && topicSelection is not null
                        && HasPendingSharedLectureCatchUpBeforeTopic(grp.Id, grp.CourseId, moduleId, topicSelection, out pendingSharedTopic))
                    {
                        catchUpHoldBypassed = softFill;
                    }
                    // Дуже сильно штрафуємо інші типи в ранніх слотах до першого заняття з прапорцем "Бажано першим у тижні", але не блокуємо жорстко.
                    string? nonPreferredBeforeFirstPreferredNote = null;
                    double nonPreferredBeforeFirstPreferredPenalty = 0;
                    if (preferredFirstEnabled
                        && penaltyNonPreferredBeforeFirstPreferred > 0
                        && ltypeId != preferredFirstTypeId
                        && IsPreferredFirstProtectedSlot(sl.Start, sl.End))
                    {
                        var slotOrder = GetSlotOrder(sl.Start, sl.End);
                        var earliestPreferredOrder = EarliestPreferredFirstSlotOrderForDate(date);
                        if (earliestPreferredOrder is int placedPreferredOrder && slotOrder > 0 && slotOrder < placedPreferredOrder)
                        {
                            nonPreferredBeforeFirstPreferredPenalty = Math.Max(1, placedPreferredOrder - slotOrder + 1) * penaltyNonPreferredBeforeFirstPreferred;
                            nonPreferredBeforeFirstPreferredNote = $"Ранній слот перед першим заняттям з прапорцем \"Бажано першим у тижні\" (воно вже у слоті №{placedPreferredOrder})";
                        }
                        if (earliestPreferredOrder is null && preferredFirstPendingToday)
                        {
                            var reserveWeight = preferredFirstMaxSlotOrder is int maxReservedSlot
                                ? Math.Max(1, maxReservedSlot - Math.Max(1, slotOrder) + 1)
                                : 1;
                            nonPreferredBeforeFirstPreferredPenalty = reserveWeight * penaltyNonPreferredBeforeFirstPreferred;
                            nonPreferredBeforeFirstPreferredNote = "Ранній слот бажано віддати під перше заняття з прапорцем \"Бажано першим у тижні\"";
                        }
                    }
                    // За потреби фільтруємо викладачів за кафедрою теми.
                    var filteredTeacherIds = tids;
                    if (topicSelection?.DepartmentId is int departmentId && departmentId > 0)
                    {
                        filteredTeacherIds = tids
                            .Where(tid => teacherDepartmentById.TryGetValue(tid, out var depId) && depId == departmentId)
                            .ToList();
                        if (filteredTeacherIds.Count == 0)
                        {
                            var depName = departmentNames.TryGetValue(departmentId, out var dn) ? dn : $"#{departmentId}";
                            var topicCode = string.IsNullOrWhiteSpace(topicSelection.TopicCode) ? null : topicSelection.TopicCode.Trim();
                            var reason = topicCode is null
                                ? $"Для модуля <{ModuleLabel()}> обрано кафедру \"{depName}\", але немає доступних викладачів цієї кафедри."
                                : $"Для модуля <{ModuleLabel()}> (тема {topicCode}) обрано кафедру \"{depName}\", але немає доступних викладачів цієї кафедри.";
                            RecordSlotFailureReason(date, sl, reason);
                            if (!allowIncompleteDrafts)
                            {
                                continue;
                            }
                        }
                    }
                    // Перебір кандидатів-викладачів для цього слоту.
                    var requiresTeacher = (typeById.TryGetValue(ltypeId, out var ltMetaForFallback) ? ltMetaForFallback.RequiresTeacher : (bool?)null) ?? true;
                    var requiresRoomForFallback = (typeById.TryGetValue(ltypeId, out var ltMetaRoomFallback) ? ltMetaRoomFallback.RequiresRoom : (bool?)null) ?? true;
                    var isShareableLecturePlacementForFallback = !isSelfStudyPlacement && CanShareAcrossGroups(ltypeId);
                    var orderedCandidateRoomsForFallback = requiresRoomForFallback
                        ? OrderCandidateRoomsForPlacement(candidateRooms, isShareableLecturePlacementForFallback)
                        : Array.Empty<Room>();
                    var slotGroupBusy = HasGroupOverlap(grp.Id, date, s, e);
                    int? FindTeacherForIncompleteDraft()
                    {
                        if (!requiresTeacher)
                        {
                            return null;
                        }
                        foreach (var teacherId in filteredTeacherIds)
                        {
                            if (!TeacherFitsWorkingHours(teacherId, date, s, e))
                            {
                                continue;
                            }
                            var teacherBusyForSlot = HasTeacherOverlap(teacherId, date, s, e);
                            if (!teacherBusyForSlot)
                            {
                                return teacherId;
                            }
                        }
                        return null;
                    }
                    Room? FindRoomForIncompleteDraft()
                    {
                        if (!requiresRoomForFallback)
                        {
                            return null;
                        }
                        foreach (var roomCandidate in orderedCandidateRoomsForFallback)
                        {
                            var roomBusyForSlot = HasRoomOverlap(roomCandidate.Id, date, s, e);
                            if (roomBusyForSlot)
                            {
                                continue;
                            }
                            if (ViolatesTravelFeasibility(
                                    existing => existing.GroupId == grp.Id,
                                    roomCandidate,
                                    date,
                                    s,
                                    e,
                                    $"групи {grp.Name}",
                                    out _))
                            {
                                continue;
                            }
                            return roomCandidate;
                        }
                        return null;
                    }
                    TeacherDraftItem? FindDraftForBusySlot(BusySlot busySlot)
                        => allCreatedDrafts.FirstOrDefault(draft =>
                            SlotMatches(
                                busySlot,
                                draft.GroupId,
                                draft.Date,
                                draft.StartTime,
                                draft.EndTime,
                                draft.ModuleId,
                                draft.TeacherId,
                                draft.RoomId,
                                draft.ModuleTopicId));
                    bool TryReassignSingleRoomOccupant(
                        Room targetRoom,
                        IReadOnlySet<int> ignoredSharedGroupIds,
                        int currentTeacherId,
                        out string note)
                    {
                        note = string.Empty;
                        if (!softFill || !relaxed || forcedSlot is null)
                        {
                            return false;
                        }
                        var conflictingSlots = BusyForRoomDate(targetRoom.Id, date)
                            .Where(slot => slot.StartTime < e && s < slot.EndTime)
                            .Where(slot => !IsSameSharedLectureCluster(
                                slot,
                                ignoredSharedGroupIds,
                                moduleId,
                                ltypeId,
                                topicSelection?.Id,
                                currentTeacherId,
                                targetRoom.Id,
                                date,
                                s,
                                e))
                            .ToList();
                        if (conflictingSlots.Count != 1)
                        {
                            return false;
                        }
                        var occupiedSlot = conflictingSlots[0];
                        var occupiedDraft = FindDraftForBusySlot(occupiedSlot);
                        if (occupiedDraft is null
                            || occupiedDraft.IsLocked
                            || occupiedDraft.Status != DraftStatus.Draft
                            || occupiedDraft.RoomId != targetRoom.Id
                            || CanShareAcrossGroups(occupiedDraft.LessonTypeId)
                            || excludedTypeIds.Contains(occupiedDraft.LessonTypeId)
                            || !selectedGroupsById.TryGetValue(occupiedDraft.GroupId, out var occupiedGroup))
                        {
                            return false;
                        }
                        var alternativeRooms = CandidateRoomsForGroup(
                                occupiedDraft.GroupId,
                                occupiedDraft.ModuleId,
                                occupiedGroup.StudentsCount,
                                ignoreGroupPreference: true)
                            .Where(room => room.Id != targetRoom.Id)
                            .OrderBy(room => room.Capacity)
                            .ThenBy(room => room.Id)
                            .ToList();
                        foreach (var alternativeRoom in alternativeRooms)
                        {
                            if (BusyForRoomDate(alternativeRoom.Id, date).Any(slot => slot.StartTime < e && s < slot.EndTime))
                            {
                                continue;
                            }
                            if (ViolatesTravelFeasibility(
                                    existing => existing.GroupId == occupiedDraft.GroupId,
                                    alternativeRoom,
                                    date,
                                    s,
                                    e,
                                    $"групи {occupiedGroup.Name}",
                                    out _))
                            {
                                continue;
                            }
                            if (occupiedDraft.TeacherId is int occupiedTeacherId
                                && ViolatesTravelFeasibility(
                                    existing => existing.TeacherId == occupiedTeacherId,
                                    alternativeRoom,
                                    date,
                                    s,
                                    e,
                                    $"викладача {TeacherLabel(occupiedTeacherId)}",
                                    out _))
                            {
                                continue;
                            }
                            var oldBusySlot = FindBusySlotForDraft(occupiedDraft, occupiedDraft.StartTime, occupiedDraft.EndTime);
                            if (oldBusySlot is null || !RemoveBusySlot(oldBusySlot))
                            {
                                return false;
                            }
                            occupiedDraft.RoomId = alternativeRoom.Id;
                            AddBusySlot(new BusySlot(
                                occupiedDraft.GroupId,
                                occupiedDraft.TeacherId,
                                occupiedDraft.RoomId,
                                date,
                                occupiedDraft.StartTime,
                                occupiedDraft.EndTime,
                                alternativeRoom.BuildingId,
                                occupiedDraft.ModuleId,
                                occupiedDraft.LessonTypeId,
                                occupiedDraft.ModuleTopicId,
                                true));
                            InvalidateGapResourceCaches();
                            note = $"Аудиторію #{targetRoom.Id} звільнено пересадкою групи {occupiedGroup.Name} в аудиторію #{alternativeRoom.Id}.";
                            warnings.Add($"[{date:yyyy-MM-dd} {s:HH\\:mm}-{e:HH\\:mm}] {note}");
                            return true;
                        }
                        return false;
                    }
                    bool TryMoveDraftToSlotPreservingResources(TeacherDraftItem draft, TimeSlot targetSlot, out string note)
                    {
                        note = string.Empty;
                        if (draft.IsLocked
                            || draft.Status != DraftStatus.Draft
                            || draft.Date != date
                            || CanShareAcrossGroups(draft.LessonTypeId)
                            || excludedTypeIds.Contains(draft.LessonTypeId)
                            || (draft.StartTime == targetSlot.Start && draft.EndTime == targetSlot.End))
                        {
                            return false;
                        }
                        if (draft.TeacherId is not int draftTeacherId
                            || draft.RoomId is not int draftRoomId
                            || !selectedGroupsById.TryGetValue(draft.GroupId, out var draftGroup))
                        {
                            return false;
                        }
                        var draftRoom = roomsAll.FirstOrDefault(room => room.Id == draftRoomId);
                        if (draftRoom is null)
                        {
                            return false;
                        }
                        if (!slotIndexByTime.TryGetValue((draft.StartTime, draft.EndTime), out var oldSlotIndex)
                            || !slotIndexByTime.TryGetValue((targetSlot.Start, targetSlot.End), out var targetSlotIndex))
                        {
                            return false;
                        }
                        var maxSegmentsAllowed = softFill ? 2 : maxModuleSegmentsPerDay;
                        var moduleIndexesAfterMove = BusyForGroupDate(draft.GroupId, date)
                            .Where(slot => slot.ModuleId == draft.ModuleId
                                           && !excludedTypeIds.Contains(slot.LessonTypeId))
                            .Select(slot => slotIndexByTime.TryGetValue((slot.StartTime, slot.EndTime), out var idx) ? idx : -1)
                            .Where(idx => idx >= 0 && idx != oldSlotIndex)
                            .Append(targetSlotIndex)
                            .Distinct()
                            .OrderBy(idx => idx)
                            .ToList();
                        if (CountModuleSegments(moduleIndexesAfterMove) > maxSegmentsAllowed
                            || InsertionWouldSplitExistingModuleBlock(draft.GroupId, date, draft.ModuleId, targetSlot.Start, targetSlot.End, out _, maxSegmentsAllowed))
                        {
                            return false;
                        }
                        var oldBusySlot = FindBusySlotForDraft(draft, draft.StartTime, draft.EndTime);
                        if (oldBusySlot is null || !RemoveBusySlot(oldBusySlot))
                        {
                            return false;
                        }
                        var oldStart = draft.StartTime;
                        var oldEnd = draft.EndTime;
                        try
                        {
                            if (!TeacherFitsWorkingHours(draftTeacherId, date, targetSlot.Start, targetSlot.End)
                                || HasGroupOverlap(draft.GroupId, date, targetSlot.Start, targetSlot.End)
                                || HasTeacherOverlap(draftTeacherId, date, targetSlot.Start, targetSlot.End)
                                || HasRoomOverlap(draftRoomId, date, targetSlot.Start, targetSlot.End))
                            {
                                return false;
                            }
                            if (draft.ModuleTopicId is int draftTopicId
                                && topicById.TryGetValue(draftTopicId, out var draftTopic)
                                && ViolatesTopicCalendarOrder(draft.GroupId, draft.ModuleId, draftTopic, date, targetSlot.Start, targetSlot.End))
                            {
                                return false;
                            }
                            if (ViolatesTravelFeasibility(
                                    existing => existing.GroupId == draft.GroupId,
                                    draftRoom,
                                    date,
                                    targetSlot.Start,
                                    targetSlot.End,
                                    $"групи {draftGroup.Name}",
                                    out _))
                            {
                                return false;
                            }
                            if (ViolatesTravelFeasibility(
                                    existing => existing.TeacherId == draftTeacherId,
                                    draftRoom,
                                    date,
                                    targetSlot.Start,
                                    targetSlot.End,
                                    $"викладача {TeacherLabel(draftTeacherId)}",
                                    out _))
                            {
                                return false;
                            }
                            draft.StartTime = targetSlot.Start;
                            draft.EndTime = targetSlot.End;
                            draft.DayOfWeek = date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
                            AddBusySlot(new BusySlot(
                                draft.GroupId,
                                draft.TeacherId,
                                draft.RoomId,
                                date,
                                targetSlot.Start,
                                targetSlot.End,
                                draftRoom.BuildingId,
                                draft.ModuleId,
                                draft.LessonTypeId,
                                draft.ModuleTopicId,
                                true));
                            InvalidateGapResourceCaches();
                            note = $"Заняття групи {draftGroup.Name} перенесено з {oldStart:HH\\:mm}-{oldEnd:HH\\:mm} у {targetSlot.Start:HH\\:mm}-{targetSlot.End:HH\\:mm}, щоб звільнити дефіцитний ресурс.";
                            warnings.Add($"[{date:yyyy-MM-dd} {s:HH\\:mm}-{e:HH\\:mm}] {note}");
                            return true;
                        }
                        finally
                        {
                            if (draft.StartTime == oldStart && draft.EndTime == oldEnd)
                            {
                                AddBusySlot(oldBusySlot);
                                InvalidateGapResourceCaches();
                            }
                        }
                    }
                    bool TryDisplaceSingleTeacherOccupant(
                        int targetTeacherId,
                        IReadOnlySet<int> ignoredSharedGroupIds,
                        int currentRoomId,
                        out string note)
                    {
                        note = string.Empty;
                        if (!softFill || !relaxed || forcedSlot is null)
                        {
                            return false;
                        }
                        var conflictingSlots = BusyForTeacherDate(targetTeacherId, date)
                            .Where(slot => slot.StartTime < e && s < slot.EndTime)
                            .Where(slot => !IsSameSharedLectureCluster(
                                slot,
                                ignoredSharedGroupIds,
                                moduleId,
                                ltypeId,
                                topicSelection?.Id,
                                targetTeacherId,
                                currentRoomId,
                                date,
                                s,
                                e))
                            .ToList();
                        if (conflictingSlots.Count != 1)
                        {
                            return false;
                        }
                        var occupiedDraft = FindDraftForBusySlot(conflictingSlots[0]);
                        if (occupiedDraft is null
                            || occupiedDraft.GroupId == grp.Id
                            || occupiedDraft.TeacherId != targetTeacherId
                            || occupiedDraft.RoomId is null)
                        {
                            return false;
                        }
                        var targetSlots = slots
                            .Where(slot => !(slot.Start == s && slot.End == e))
                            .OrderBy(slot => slotIndexByTime.TryGetValue((slot.Start, slot.End), out var idx) ? Math.Abs(idx - slotIndex) : int.MaxValue)
                            .ThenBy(slot => slot.Start)
                            .ToList();
                        foreach (var targetSlot in targetSlots)
                        {
                            if (TryMoveDraftToSlotPreservingResources(occupiedDraft, targetSlot, out note))
                            {
                                return true;
                            }
                        }
                        return false;
                    }
                    foreach (var tidCandidate in filteredTeacherIds)
                    {
                        if (!TeacherFitsWorkingHours(tidCandidate, date, s, e))
                        {
                            slotIssues.Add($"Викладач {TeacherLabel(tidCandidate)} не працює у слоті {slotLabel}.");
                            continue;
                        }
                        // Перевірка конфліктів поточної групи у слоті.
                        bool groupBusy = HasGroupOverlap(grp.Id, date, s, e);
                        if (groupBusy)
                        {
                            slotIssues.Add($"Група {grp.Name} зайнята у слоті {slotLabel}.");
                            continue;
                        }
                        // Накопичуємо штрафи та їх пояснення.
                        var penalties = new List<string>();
                        double penaltyScore = 0;
                        if (catchUpHoldBypassed)
                        {
                            penaltyScore += 120.0;
                            penalties.Add("Тему після спільної лекції дозволено лише для аварійного дозаповнення");
                        }
                        if (emergencyUnthemedTopic)
                        {
                            penaltyScore += 65.0;
                            penalties.Add("Аварійне дозаповнення створило заняття без коду теми");
                        }
                        if (!string.IsNullOrWhiteSpace(nonPreferredBeforeFirstPreferredNote))
                        {
                            penaltyScore += nonPreferredBeforeFirstPreferredPenalty;
                            penalties.Add(nonPreferredBeforeFirstPreferredNote);
                        }
                        if (preferredFirstWeekBalancePenalty > 0)
                        {
                            penaltyScore += preferredFirstWeekBalancePenalty;
                            penalties.Add(preferredFirstWeekBalanceNote);
                        }
                        if (preferredFirstEnabled)
                        {
                            if (ltypeId == preferredFirstTypeId && penaltyPreferredFirstTypeLateSlot > 0)
                            {
                                penaltyScore += slotIndex * penaltyPreferredFirstTypeLateSlot;
                                if (bonusPreferredFirstInProtectedSlot > 0
                                    && IsPreferredFirstProtectedSlot(s, e))
                                {
                                    var slotOrder = GetSlotOrder(s, e);
                                    var reserveWeight = preferredFirstMaxSlotOrder is int maxReservedSlot
                                        ? Math.Max(1, maxReservedSlot - Math.Max(1, slotOrder) + 1)
                                        : 1;
                                    penaltyScore -= reserveWeight * bonusPreferredFirstInProtectedSlot;
                                    penalties.Add("Ранній слот підсилює тип з прапорцем \"Бажано першим у тижні\"");
                                }
                            }
                            else if (preferredFirstPendingToday
                                     && penaltyNonPreferredEarlySlotWhilePreferredPending > 0
                                     && IsPreferredFirstProtectedSlot(s, e))
                            {
                                var slotOrder = GetSlotOrder(s, e);
                                var reserveWeight = preferredFirstMaxSlotOrder is int maxReservedSlot
                                    ? Math.Max(1, maxReservedSlot - slotOrder + 1)
                                    : 1;
                                penaltyScore += reserveWeight * penaltyNonPreferredEarlySlotWhilePreferredPending;
                                penalties.Add("Ранній слот зарезервовано під тип з прапорцем \"Бажано першим у тижні\"");
                            }
                        }
                        var isLectureFirstPlacement = !isSelfStudyPlacement && CanShareAcrossGroups(ltypeId);
                        var lectureSlotOrder = GetSlotOrder(s, e);
                        if (isLectureFirstPlacement)
                        {
                            penaltyScore += slotIndex * penaltyLectureFirstTypeLateSlot;
                            if (IsLectureFirstProtectedSlot(s, e))
                            {
                                var reserveWeight = Math.Max(1, RegularLectureMaxSlotOrder(preferredFirstMaxSlotOrder) - Math.Max(1, lectureSlotOrder) + 1);
                                penaltyScore -= reserveWeight * bonusLectureFirstProtectedSlot;
                                penalties.Add("Лекційний тип поставлено в ранній захищений слот");
                            }
                            var earlierNonLectureCount = CountEarlierNonLectureSlots(date, lectureSlotOrder);
                            if (earlierNonLectureCount > 0)
                            {
                                penaltyScore += earlierNonLectureCount * penaltyLectureAfterNonLecture;
                                penalties.Add("Лекційний тип отримав штраф за розміщення після не-лекційних занять");
                            }
                            if (IsEmergencyLateLectureSlot(ltypeId, lectureSlotOrder, preferredFirstMaxSlotOrder))
                            {
                                penaltyScore += penaltyEmergencyLateLectureSlot;
                                penalties.Add("Аварійно пізній слот для лекційного типу");
                            }
                        }
                        else if ((lectureFirstPendingToday || HasLaterLectureFirstPlacement(date, s))
                                 && IsLectureFirstProtectedSlot(s, e))
                        {
                            var reserveWeight = Math.Max(1, RegularLectureMaxSlotOrder(preferredFirstMaxSlotOrder) - Math.Max(1, lectureSlotOrder) + 1);
                            penaltyScore += reserveWeight * penaltyNonLectureEarlySlotWhileLecturePending * 2.0;
                            penalties.Add("Ранній слот збережено під лекційний або потоковий тип");
                        }
                        // Штрафуємо повтори модуля в один день.
                        var sameDayCount = CountModuleForDay(grp.Id, date, moduleId);
                        if (expandsContiguousModuleBlock)
                        {
                            penaltyScore -= bonusExpandContiguousModuleBlock;
                            penalties.Add("Продовжено суцільний блок модуля");
                            if (sameDayModuleCountBefore == 2)
                            {
                                penaltyScore -= bonusReachThirdSlotInModuleBlock;
                                penalties.Add("Третю пару модуля збережено в одному блоці");
                            }
                        }
                        if (startsNewDistinctModule && distinctModulesBeforePlacement >= targetDistinctModulesForPlacement)
                        {
                            penaltyScore += penaltyStartFourthDistinctModule;
                            penalties.Add("Новий модуль після цільової різноманітності дня має додатковий штраф");
                        }
                        if (sameDayCount >= 2)
                        {
                            var multiplier = allowExtraSameDay ? 0.5 : 1.0;
                            penaltyScore += penaltyExtraSameDay * (sameDayCount - 1) * multiplier;
                            penalties.Add("Дозволено більше двох пар модуля в один день");
                        }
                        // Штрафуємо повтори модуля у сусідні дні.
                        if (HadSameModulePreviousDay(grp.Id, moduleId, date))
                        {
                            if (allowRepeatPreviousDay)
                            {
                                penaltyScore += penaltySameModulePrevDay * 0.5;
                                penalties.Add("Повтор модуля у сусідні дні дозволено для балансу");
                            }
                            else
                            {
                                penaltyScore += penaltySameModulePrevDay;
                                penalties.Add("Дозволено повторення модуля у сусідні дні");
                            }
                        }
                        // Чи потрібна аудиторія для цього типу заняття.
                        var requiresRoom = (typeById.TryGetValue(ltypeId, out var ltMeta) ? ltMeta.RequiresRoom : (bool?)null) ?? true;
                        var isLecturePlacement = IsLectureType(ltypeId);
                        var isShareableLecturePlacement = !isSelfStudyPlacement && CanShareAcrossGroups(ltypeId);
                        var allowJoinExistingSharedLecture = softFill && forcedSlot is not null && isShareableLecturePlacement;
                        if (softFill
                        && hasModuleHourOverrides
                        && ManualRangeRemainingFor(grp.Id, moduleId) <= 0)
                    {
                        penaltyScore += 35.0;
                        penalties.Add("Модуль вже набрав ручний ліміт у поточному діапазоні");
                    }
                        var orderedCandidateRooms = requiresRoom
                            ? OrderCandidateRoomsForPlacement(candidateRooms, isShareableLecturePlacement)
                            : Array.Empty<Room>();
                        // Додаємо штраф за навантаження викладача.
                        penaltyScore += TeacherLoadPenalty(tidCandidate);
                        var teacherReservationPenalty = TeacherScarcityReservationPenalty(tidCandidate, date, forcedGapVariantBudget);
                        if (teacherReservationPenalty > 0)
                        {
                            penaltyScore += teacherReservationPenalty;
                            penalties.Add("Рідкісні вільні слоти викладача збережено для складніших прогалин");
                        }
                        // Штрафуємо повтор однакового часу для модуля в інший день.
                        bool sameSlotPattern = BusyForGroup(grp.Id).Any(b =>
                            b.ModuleId == moduleId
                            && b.Date != date
                            && b.StartTime == s
                            && !excludedTypeIds.Contains(b.LessonTypeId));
                        if (sameSlotPattern)
                        {
                            penaltyScore += penaltySameSlotPattern;
                            penalties.Add("Повтор того ж часу в інші дні");
                        }
                        var topicContinuationDistance = TopicContinuationDistanceForGroup(date, sl, moduleId, topicSelection?.Id);
                        if (topicContinuationDistance > 0)
                        {
                            penaltyScore += topicContinuationDistance * penaltyTopicContinuationGap;
                            penalties.Add("Повтор тієї самої теми поставлено не суміжним блоком");
                        }
                        // Підбір аудиторій (якщо потрібні).
                        if (requiresRoom)
                        {
                            if (candidateRooms.Count == 0)
                            {
                                var roomReason = groupRoomPreferencesByGroupId.ContainsKey(grp.Id)
                                    ? $"Не знайдено аудиторій для модуля <{ModuleLabel()}> (група {grp.Name}) у слоті {slotLabel} з урахуванням вибраних пріоритетів корпусу/аудиторій."
                                    : $"Не знайдено аудиторій для модуля <{ModuleLabel()}> (група {grp.Name}) у слоті {slotLabel}.";
                                RecordSlotFailureReason(date, sl, roomReason);
                                warnings.Add(roomReason);
                                continue;
                            }
                            // Перевіряємо кожну аудиторію на зайнятість.
                            foreach (var rm in orderedCandidateRooms)
                            {
                                var roomSwitchPenalty = 0.0;
                                if (preferredAdjacentRoomId is int preferredRoomId && rm.Id != preferredRoomId)
                                {
                                    roomSwitchPenalty = AdjacentRoomSwitchPenalty();
                                }
                                var topicId = topicSelection?.Id;
                                var existingSharedLectureGroupIds = allowJoinExistingSharedLecture
                                    ? FindExistingSharedLectureGroupIds(
                                        grp.CourseId,
                                        moduleId,
                                        ltypeId,
                                        topicId,
                                        tidCandidate,
                                        rm.Id,
                                        date,
                                        s,
                                        e)
                                    : Array.Empty<int>();
                                var existingSharedLectureGroupSet = existingSharedLectureGroupIds.ToHashSet();
                                bool teacherBusy = BusyForTeacherDate(tidCandidate, date).Any(x =>
                                                                 x.StartTime < e && s < x.EndTime
                                                                 && !IsSameSharedLectureCluster(
                                                                     x,
                                                                     existingSharedLectureGroupSet,
                                                                     moduleId,
                                                                     ltypeId,
                                                                     topicId,
                                                                     tidCandidate,
                                                                     rm.Id,
                                                                     date,
                                                                     s,
                                                                     e));
                                if (teacherBusy && !TryDisplaceSingleTeacherOccupant(tidCandidate, existingSharedLectureGroupSet, rm.Id, out _))
                                {
                                    slotIssues.Add($"Викладач {TeacherLabel(tidCandidate)} зайнятий у слоті {slotLabel}.");
                                    continue;
                                }
                                bool roomBusy = BusyForRoomDate(rm.Id, date).Any(x =>
                                                              x.StartTime < e && s < x.EndTime
                                                              && !IsSameSharedLectureCluster(
                                                                  x,
                                                                  existingSharedLectureGroupSet,
                                                                  moduleId,
                                                                  ltypeId,
                                                                  topicId,
                                                                  tidCandidate,
                                                                  rm.Id,
                                                                  date,
                                                                  s,
                                                                  e));
                                if (roomBusy && !TryReassignSingleRoomOccupant(rm, existingSharedLectureGroupSet, tidCandidate, out _))
                                {
                                    slotIssues.Add($"Усі аудиторії для модуля <{ModuleLabel()}> зайняті у слоті {slotLabel}.");
                                    continue;
                                }
                                var sharedGroupIds = ResolveSharedLectureGroups(
                                    moduleId,
                                    ltypeId,
                                    topicSelection,
                                    isSelfStudyPlacement,
                                    existingSharedLectureGroupIds,
                                    maxModuleSegmentsAllowed,
                                    date,
                                    s,
                                    e,
                                    rm);
                                var allSharedGroupIds = existingSharedLectureGroupIds
                                    .Concat(sharedGroupIds)
                                    .Distinct()
                                    .ToList();
                                var newSharedGroupIds = allSharedGroupIds
                                    .Except(existingSharedLectureGroupIds)
                                    .ToList();
                                if (isShareableLecturePlacement
                                    && topicSelection is not null
                                    && ShouldHoldShareableTopicForMissingPendingGroups(grp.CourseId, moduleId, topicSelection, allSharedGroupIds))
                                {
                                    continue;
                                }
                                if (isShareableLecturePlacement
                                    && allSharedGroupIds.Count <= 1
                                    && (!softFill
                                        || !bypassCatchUpHold
                                        || emergencySingletonSharedLecturesCreated >= maxEmergencySingletonSharedLectures))
                                {
                                    continue;
                                }
                                if (isShareableLecturePlacement
                                    && allSharedGroupIds.Count > 0
                                    && (HasJoinableFutureShareableGroupOutside(moduleId, topicSelection, rm, date, s, e, allSharedGroupIds)
                                        || (allSharedGroupIds.Count == 1 && HasFuturePendingShareablePartner(moduleId, topicSelection))
                                        || (!relaxed && HasFeasiblePendingShareablePartner(moduleId, topicSelection))))
                                {
                                    continue;
                                }
                                var groupsWithModuleInSlot = CountGroupsWithModuleInSlot(moduleId, date, s, e);
                                if (allowJoinExistingSharedLecture
                                    && groupsWithModuleInSlot + newSharedGroupIds.Count > MaxSharedLectureGroupsForCourse(grp.CourseId))
                                {
                                    continue;
                                }
                                var travelViolation = false;
                                string? travelReason = null;
                                foreach (var sharedGroupId in newSharedGroupIds)
                                {
                                    if (!selectedGroupsById.TryGetValue(sharedGroupId, out var sharedGroup))
                                    {
                                        continue;
                                    }
                                    if (ViolatesTravelFeasibility(
                                            existing => existing.GroupId == sharedGroupId,
                                            rm,
                                            date,
                                            s,
                                            e,
                                            $"групи {sharedGroup.Name}",
                                            out var groupTravelReason))
                                    {
                                        travelViolation = true;
                                        travelReason = groupTravelReason;
                                        break;
                                    }
                                }
                                if (!travelViolation && ViolatesTravelFeasibility(
                                        existing => existing.TeacherId == tidCandidate,
                                        rm,
                                        date,
                                        s,
                                        e,
                                        $"викладача {TeacherLabel(tidCandidate)}",
                                        out var teacherTravelReason))
                                {
                                    travelViolation = true;
                                    travelReason = teacherTravelReason;
                                }
                                if (travelViolation)
                                {
                                    if (!string.IsNullOrWhiteSpace(travelReason))
                                    {
                                        slotIssues.Add(travelReason);
                                    }
                                    continue;
                                }
                                var sharedStudents = SharedStudentsCount(allSharedGroupIds);
                                if (sharedStudents <= 0 || sharedStudents > rm.Capacity)
                                {
                                    continue;
                                }
                                if (!TryValidatePlacementAgainstBusy(
                                        moduleId,
                                        ltypeId,
                                        topicSelection?.Id,
                                        tidCandidate,
                                        rm,
                                        date,
                                        s,
                                        e,
                                        newSharedGroupIds,
                                        out var placementBusyReason))
                                {
                                    if (!string.IsNullOrWhiteSpace(placementBusyReason))
                                    {
                                        slotIssues.Add(placementBusyReason);
                                    }
                                    continue;
                                }
                                var capacityReserve = Math.Max(0, rm.Capacity - sharedStudents);
                                var capacityPenalty = isShareableLecturePlacement
                                    ? capacityReserve * 0.04
                                    : isLecturePlacement
                                        ? capacityReserve * 0.08
                                        : capacityReserve * 0.25;
                                var roomScarcityPenalty = RoomScarcityPenalty(
                                    rm,
                                    orderedCandidateRooms,
                                    sharedStudents,
                                    isLecturePlacement,
                                    isShareableLecturePlacement,
                                    sharedStudents,
                                    allSharedGroupIds.Count);
                                var roomReachabilityReservationPenalty = RoomReachabilityReservationPenalty(
                                    rm,
                                    date,
                                    forcedGapVariantBudget,
                                    isShareableLecturePlacement);
                                var neighborGapBuildingPenalty = NeighborGapBuildingPreservationPenalty(
                                    rm,
                                    date,
                                    sl,
                                    forcedGapVariantBudget,
                                    isShareableLecturePlacement);
                                var sharedLectureBonus = isShareableLecturePlacement
                                    ? Math.Max(0, allSharedGroupIds.Count - 1) * 18.0
                                    : 0;
                                var singleSharedLecturePenalty = isShareableLecturePlacement
                                                                 && allSharedGroupIds.Count <= 1
                                                                 && softFill
                                                                 && bypassCatchUpHold
                                    ? 180.0
                                    : 0.0;
                                var totalPenalty = penaltyScore
                                    + roomSwitchPenalty
                                    + BuildingDistancePenalty(tidCandidate, rm, date, s, e)
                                    + capacityPenalty
                                    + roomScarcityPenalty
                                    + roomReachabilityReservationPenalty
                                    + neighborGapBuildingPenalty
                                    + singleSharedLecturePenalty
                                    - sharedLectureBonus;
                                var notes = new List<string>(penalties);
                                if (roomSwitchPenalty > 0)
                                {
                                    notes.Add("Змінено аудиторію всередині суміжного блоку модуля");
                                }
                                if (roomScarcityPenalty < 0)
                                {
                                    notes.Add("Обрано аудиторію із запасом для розширення спільної лекції");
                                }
                                else if (roomScarcityPenalty > 0)
                                {
                                    notes.Add("Велика аудиторія збережена для дефіцитних спільних потоків");
                                }
                                if (roomReachabilityReservationPenalty > 0)
                                {
                                    notes.Add("Дефіцитну аудиторію збережено для складніших прогалин дня");
                                }
                                if (neighborGapBuildingPenalty > 0)
                                {
                                    notes.Add("Корпус ізолює сусідні порожні слоти, тому має додатковий штраф");
                                }
                                if (singleSharedLecturePenalty > 0)
                                {
                                    notes.Add("Одиночну спільну лекцію дозволено лише для аварійного дозаповнення");
                                }
                                var candidate = new PlacementCandidate(
                                    sl,
                                    tidCandidate,
                                    rm,
                                    ltypeId,
                                    topicSelection,
                                    isSelfStudyPlacement,
                                    newSharedGroupIds,
                                    allSharedGroupIds.Count,
                                    startsNewDistinctModule,
                                    expandsContiguousModuleBlock,
                                    totalPenalty,
                                    notes);
                                slotCandidates.Add(candidate);
                            }
                        }
                        else
                        {
                            bool teacherBusy = HasTeacherOverlap(tidCandidate, date, s, e);
                            if (teacherBusy)
                            {
                                slotIssues.Add($"Викладач {TeacherLabel(tidCandidate)} зайнятий у слоті {slotLabel}.");
                                continue;
                            }
                            if (!TryValidatePlacementAgainstBusy(
                                    moduleId,
                                    ltypeId,
                                    topicSelection?.Id,
                                    tidCandidate,
                                    null,
                                    date,
                                    s,
                                    e,
                                    new[] { grp.Id },
                                    out var placementBusyReason))
                            {
                                if (!string.IsNullOrWhiteSpace(placementBusyReason))
                                {
                                    slotIssues.Add(placementBusyReason);
                                }
                                continue;
                            }
                            var notes = new List<string>(penalties);
                            var candidate = new PlacementCandidate(
                                sl,
                                tidCandidate,
                                null,
                                ltypeId,
                                topicSelection,
                                isSelfStudyPlacement,
                                new[] { grp.Id },
                                1,
                                startsNewDistinctModule,
                                expandsContiguousModuleBlock,
                                penaltyScore,
                                notes);
                            slotCandidates.Add(candidate);
                        }
                    }
                    // Якщо є кандидати — обираємо найкращого за штрафами.
                    if (allowIncompleteDrafts
                        && slotCandidates.Count == 0
                        && !slotGroupBusy
                        && (isSelfStudyPlacement
                            || !CanShareAcrossGroups(ltypeId)
                            || (softFill
                                && bypassCatchUpHold
                                && emergencySingletonSharedLecturesCreated < maxEmergencySingletonSharedLectures)))
                    {
                        var fallbackTeacherId = FindTeacherForIncompleteDraft();
                        var fallbackRoom = FindRoomForIncompleteDraft();
                        var missingTeacher = requiresTeacher && fallbackTeacherId is null;
                        var missingRoom = requiresRoomForFallback && fallbackRoom is null;
                        if (missingTeacher || missingRoom)
                        {
                            int? assignedTeacherId = missingRoom && !missingTeacher ? fallbackTeacherId : null;
                            var assignedRoom = missingTeacher && !missingRoom ? fallbackRoom : null;
                            var fallbackPenalty = (missingTeacher ? 40.0 : 0.0) + (missingRoom ? 40.0 : 0.0);
                            if (preferEarliestSlot)
                            {
                                fallbackPenalty += slotIndex * 0.20;
                            }
                            var notes = new List<string>();
                            if (missingTeacher)
                            {
                                notes.Add("Чернетку створено без викладача");
                            }
                            if (missingRoom)
                            {
                                notes.Add("Чернетку створено без аудиторії");
                            }
                            var incompleteCandidate = new IncompletePlacementCandidate(
                                sl,
                                assignedTeacherId,
                                assignedRoom,
                                ltypeId,
                                topicSelection,
                                isSelfStudyPlacement,
                                fallbackPenalty,
                                missingTeacher,
                                missingRoom,
                                notes);
                            if (bestIncomplete is null || incompleteCandidate.Penalty < bestIncompleteEffectivePenalty)
                            {
                                bestIncomplete = incompleteCandidate;
                                bestIncompleteEffectivePenalty = incompleteCandidate.Penalty;
                            }
                        }
                    }
                    if (slotCandidates.Count > 0)
                    {
                        var bestAny = slotCandidates
                            .OrderBy(c => IsEmergencyLateLectureSlot(c.LessonTypeId, GetSlotOrder(c.Slot.Start, c.Slot.End), preferredFirstMaxSlotOrder))
                            .ThenByDescending(c => c.StartsNewDistinctModule && CountDistinctModulesForDay(grp.Id, date) < targetDistinctModulesForPlacement)
                            .ThenByDescending(c => c.ExpandsContiguousBlock)
                            .ThenBy(c => c.StartsNewDistinctModule && CountDistinctModulesForDay(grp.Id, date) >= targetDistinctModulesForPlacement)
                            .ThenByDescending(SharedLectureCandidatePriority)
                            .ThenByDescending(c => c.TotalSharedGroupCount)
                            .ThenBy(c => c.Penalty)
                            .ThenBy(c => groupRandom.Next())
                            .First();
                        var localBest = bestAny;
                        if (preferredAdjacentTeacherId is int pt && maxExtraPenaltyPreferSameTeacherForConsecutiveModule > 0)
                        {
                            var bestPreferred = slotCandidates
                                .Where(c => c.TeacherId == pt)
                                .OrderBy(c => IsEmergencyLateLectureSlot(c.LessonTypeId, GetSlotOrder(c.Slot.Start, c.Slot.End), preferredFirstMaxSlotOrder))
                                .ThenByDescending(c => c.StartsNewDistinctModule && CountDistinctModulesForDay(grp.Id, date) < targetDistinctModulesForPlacement)
                                .ThenByDescending(c => c.ExpandsContiguousBlock)
                                .ThenBy(c => c.StartsNewDistinctModule && CountDistinctModulesForDay(grp.Id, date) >= targetDistinctModulesForPlacement)
                                .ThenByDescending(SharedLectureCandidatePriority)
                                .ThenByDescending(c => c.TotalSharedGroupCount)
                                .ThenBy(c => c.Penalty)
                                .ThenBy(c => groupRandom.Next())
                                .FirstOrDefault();
                            if (bestPreferred is not null
                                && bestPreferred.Penalty <= bestAny.Penalty + maxExtraPenaltyPreferSameTeacherForConsecutiveModule)
                            {
                                localBest = bestPreferred;
                            }
                        }
                        var effectivePenalty = localBest.Penalty;
                        var localSharedLecturePriority = SharedLectureCandidatePriority(localBest);
                        var localSharedGroupCount = localBest.TotalSharedGroupCount;
                        var localEmergencyLateLecture = IsEmergencyLateLectureSlot(localBest.LessonTypeId, GetSlotOrder(localBest.Slot.Start, localBest.Slot.End), preferredFirstMaxSlotOrder);
                        if (preferEarliestSlot)
                        {
                            effectivePenalty += slotIndex * 0.20;
                        }
                        var bestSharedLecturePriority = best is null ? -1 : SharedLectureCandidatePriority(best);
                        var bestSharedGroupCount = best?.TotalSharedGroupCount ?? 0;
                        var bestEmergencyLateLecture = best is not null && IsEmergencyLateLectureSlot(best.LessonTypeId, GetSlotOrder(best.Slot.Start, best.Slot.End), preferredFirstMaxSlotOrder);
                        if (best is null
                            || (!localEmergencyLateLecture && bestEmergencyLateLecture)
                            || (localEmergencyLateLecture == bestEmergencyLateLecture
                                && (localSharedLecturePriority > bestSharedLecturePriority
                                    || (localSharedLecturePriority == bestSharedLecturePriority && localSharedGroupCount > bestSharedGroupCount)
                                    || (localSharedLecturePriority == bestSharedLecturePriority
                                        && localSharedGroupCount == bestSharedGroupCount
                                        && effectivePenalty < bestEffectivePenalty))))
                        {
                            best = localBest;
                            bestEffectivePenalty = effectivePenalty;
                        }
                    }
                    // Якщо кандидатів немає, фіксуємо причини відмов.
                    else if (slotIssues.Count > 0)
                    {
                        foreach (var reason in slotIssues)
                        {
                            RecordSlotFailureReason(date, sl, reason);
                        }
                    }
                    else
                    {
                        var key = (grp.Id, date, sl.Start, sl.End);
                        if (!slotFailureReasons.ContainsKey(key))
                        {
                            RecordSlotFailureReason(date, sl, $"Не знайдено вільної комбінації викладачів/аудиторій для модуля <{ModuleLabel()}> у слоті {slotLabel} (м'які правила повторів/тем/робочих годин).");
                        }
                    }
                }
                if (best is null)
                {
                    if (bestIncomplete is null)
                    {
                        return false;
                    }
                }
                // Фіксуємо обраний варіант та створюємо чернетку.
                var selectedIncomplete = best is null ? bestIncomplete : null;
                var selectedSlot = selectedIncomplete is not null ? selectedIncomplete.Slot : best!.Slot;
                var selectedRoom = selectedIncomplete is not null ? selectedIncomplete.Room : best!.Room;
                var selectedTeacher = selectedIncomplete is not null ? selectedIncomplete.TeacherId : best!.TeacherId;
                var selectedTopic = selectedIncomplete is not null ? selectedIncomplete.Topic : best!.Topic;
                var selectedLessonTypeId = selectedIncomplete is not null ? selectedIncomplete.LessonTypeId : best!.LessonTypeId;
                var selectedIsSelfStudy = selectedIncomplete is not null ? selectedIncomplete.IsSelfStudy : best!.IsSelfStudy;
                var selectedNotes = selectedIncomplete is not null ? selectedIncomplete.Notes : best!.Notes;
                var selectedValidationWarnings = selectedIncomplete is not null
                    ? BuildIncompleteDraftWarningJson(selectedIncomplete.MissingTeacher, selectedIncomplete.MissingRoom)
                    : null;
                var startTime = selectedSlot.Start;
                var endTime = selectedSlot.End;
                var placedGroupIds = (selectedIncomplete is null
                    ? (best!.SharedGroupIds.Count > 0 ? best.SharedGroupIds : new[] { grp.Id })
                    : new[] { grp.Id })
                    .Distinct()
                    .ToList();
                var writablePlacedGroupIds = placedGroupIds
                    .Where(selectedGroupsById.ContainsKey)
                    .ToList();
                var selectedIsEmergencySingletonSharedLecture = !selectedIsSelfStudy
                                                                 && CanShareAcrossGroups(selectedLessonTypeId)
                                                                 && writablePlacedGroupIds.Count <= 1
                                                                 && softFill
                                                                 && bypassCatchUpHold;
                if (!selectedIsSelfStudy
                    && selectedTopic is not null
                    && CanShareAcrossGroups(selectedLessonTypeId)
                    && writablePlacedGroupIds.Count <= 1)
                {
                    RecordSlotFailureReason(date, selectedSlot, $"Спільну лекційну тему модуля <{ModuleLabel()}> не створено одиночним потоком.");
                    return false;
                }
                if (!selectedIsSelfStudy && selectedTopic is null && ModuleHasUsableTopics(moduleId))
                {
                    RecordSlotFailureReason(
                        date,
                        selectedSlot,
                        $"Для модуля <{ModuleLabel()}> є планові теми, тому заняття без коду теми не створюється.");
                    return false;
                }
                if (!selectedIsSelfStudy && selectedTopic is not null)
                {
                    var selectedViolatesTopicOrder = writablePlacedGroupIds
                        .Any(sharedGroupId => ViolatesTopicCalendarOrder(sharedGroupId, moduleId, selectedTopic, date, startTime, endTime));
                    if (selectedViolatesTopicOrder && allowEmergencyTopicOrderRelaxation && selectedIncomplete is null)
                    {
                        RecordSlotFailureReason(
                            date,
                            selectedSlot,
                            $"Аварійне дозаповнення не створило заняття модуля <{ModuleLabel()}> без коду теми, бо тема порушує хронологічний порядок.");
                        return false;
                    }
                    foreach (var sharedGroupId in writablePlacedGroupIds)
                    {
                        if (selectedTopic is not null
                            && ViolatesTopicCalendarOrder(sharedGroupId, moduleId, selectedTopic, date, startTime, endTime))
                        {
                            var groupLabel = selectedGroupsById.TryGetValue(sharedGroupId, out var sharedGroupInfo)
                                ? sharedGroupInfo.Name
                                : $"#{sharedGroupId}";
                            RecordSlotFailureReason(
                                date,
                                selectedSlot,
                                $"Для групи {groupLabel} порушується хронологічний порядок тем модуля <{ModuleLabel()}> у слоті {startTime:HH\\:mm}-{endTime:HH\\:mm}.");
                            return false;
                        }
                    }
                }
                if (!TryValidatePlacementAgainstBusy(
                        moduleId,
                        selectedLessonTypeId,
                        selectedTopic?.Id,
                        selectedTeacher,
                        selectedRoom,
                        date,
                        startTime,
                        endTime,
                        writablePlacedGroupIds,
                        out var selectedBusyReason))
                {
                    RecordSlotFailureReason(date, selectedSlot, selectedBusyReason);
                    return false;
                }
                var persistedConflictReason = await FindPersistedPlacementConflictAsync(
                    moduleId,
                    selectedLessonTypeId,
                    selectedTopic?.Id,
                    selectedTeacher,
                    selectedRoom,
                    date,
                    startTime,
                    endTime,
                    writablePlacedGroupIds);
                if (!string.IsNullOrWhiteSpace(persistedConflictReason))
                {
                    RecordSlotFailureReason(date, selectedSlot, persistedConflictReason);
                    return false;
                }
                if (selectedIncomplete is not null)
                {
                    incompleteDraftsCreated++;
                    if (selectedIncomplete.MissingTeacher)
                    {
                        incompleteMissingTeacherCount++;
                    }
                    if (selectedIncomplete.MissingRoom)
                    {
                        incompleteMissingRoomCount++;
                    }
                    if (selectedIncomplete.MissingTeacher && selectedIncomplete.MissingRoom)
                    {
                        incompleteMissingBothCount++;
                    }
                }
                bool currentGroupPlaced = false;
                foreach (var sharedGroupId in writablePlacedGroupIds)
                {
                    if (!selectedGroupsById.TryGetValue(sharedGroupId, out var sharedGroup))
                    {
                        continue;
                    }
                    var item = new TeacherDraftItem
                    {
                        Date = date,
                        DayOfWeek = date.ToDateTime(TimeOnly.MinValue).DayOfWeek,
                        StartTime = startTime,
                        EndTime = endTime,
                        GroupId = sharedGroupId,
                        ModuleId = moduleId,
                        RoomId = selectedRoom?.Id,
                        TeacherId = selectedTeacher,
                        ModuleTopicId = selectedTopic?.Id,
                        LessonTypeId = selectedLessonTypeId,
                        Status = DraftStatus.Draft,
                        IsLocked = false,
                        IsSelfStudy = selectedIsSelfStudy,
                        ValidationWarnings = selectedValidationWarnings
                    };
                    _db.TeacherDraftItems.Add(item);
                    allCreatedDrafts.Add(item);
                    movableDrafts.Add(item);
                    AddCurrentRangeFact(sharedGroupId, moduleId);
                    if (sharedGroupId == grp.Id)
                    {
                        createdDrafts.Add(item);
                        currentGroupPlaced = true;
                    }
                    // Позначаємо тему як використану (для звичайних занять).
                    if (selectedTopic is not null && !selectedIsSelfStudy)
                    {
                        if (IsOverflowTopicUse(sharedGroupId, moduleId, selectedTopic)
                            && overflowTopicNotified.Add((sharedGroupId, moduleId, selectedTopic.Id)))
                        {
                            warnings.Add($"Для модуля <{ModuleLabel()}> у групі {sharedGroup.Name} повторно використано тему {selectedTopic.TopicCode}, щоб заповнити слот без порушення жорстких правил.");
                        }
                        MarkTopicUsed(sharedGroupId, moduleId, selectedTopic);
                    }
                    // Додаємо слот у список зайнятих.
                    AddBusySlot(new BusySlot(
                        sharedGroupId,
                        selectedTeacher,
                        selectedRoom?.Id,
                        date,
                        startTime,
                        endTime,
                        selectedRoom?.BuildingId,
                        moduleId,
                        selectedLessonTypeId,
                        selectedTopic?.Id,
                        true));
                    // Збільшуємо лічильники створених записів і зайнятих слотів.
                    created++;
                    Inc(sharedGroupId, date);
                    // Фіксуємо, що пріоритетний тип вже використано для модуля.
                    if (preferredFirstTypeId != 0 && selectedLessonTypeId == preferredFirstTypeId)
                    {
                        hasPreferred.Add((sharedGroupId, moduleId));
                    }
                    // Зменшуємо залишки самостійної роботи.
                    var sharedRemainingKey = (sharedGroupId, moduleId);
                    if (selectedIsSelfStudy && selfStudyRemainingByGroupModule.TryGetValue(sharedRemainingKey, out var ssLeft) && ssLeft > 0)
                    {
                        selfStudyRemainingByGroupModule[sharedRemainingKey] = Math.Max(0, ssLeft - 1);
                    }
                    if (selectedIsSelfStudy && selectedTopic is not null)
                    {
                        var topicKey = (sharedGroupId, moduleId, selectedTopic.Id);
                        if (selfStudyTopicRemaining.TryGetValue(topicKey, out var leftByTopic) && leftByTopic > 0)
                        {
                            selfStudyTopicRemaining[topicKey] = Math.Max(0, leftByTopic - 1);
                        }
                    }
                    // Зменшуємо загальні залишки годин по модулю.
                    if (remainingByGroupModule.TryGetValue(sharedRemainingKey, out var leftRemaining) && leftRemaining > 0)
                    {
                        leftRemaining--;
                        remainingByGroupModule[sharedRemainingKey] = Math.Max(0, leftRemaining);
                    }
                    if (sharedGroupId == grp.Id && forceFirstMainModule && !firstMainPlaced && moduleId == firstMainModuleId)
                    {
                        firstMainPlaced = true;
                        firstMainDate = date;
                        firstMainStart = startTime;
                    }
                }
                if (!currentGroupPlaced)
                {
                    return false;
                }
                if (selectedIsEmergencySingletonSharedLecture)
                {
                    emergencySingletonSharedLecturesCreated++;
                }
                // Оновлюємо статистику використання аудиторій.
                if (selectedRoom?.Id is int ridSelected)
                {
                    groupRoomUsage[ridSelected] = groupRoomUsage.TryGetValue(ridSelected, out var usedRoom)
                        ? usedRoom + 1
                        : 1;
                }
                InvalidateGapResourceCaches();
                if (isPrimary)
                {
                    lastPrimaryModuleId = moduleId;
                }
                // Додаємо нотатки з причинами штрафів.
                if (selectedNotes.Count > 0)
                {
                    var noteText = string.Join("; ", selectedNotes);
                    warnings.Add($"[{date:yyyy-MM-dd} {startTime:HH\\:mm}-{endTime:HH\\:mm}] {grp.Name}: {noteText}");
                }
                var nextShareableTopicUnlocked = !softFill
                    && !selectedIsSelfStudy
                    && selectedTopic is not null
                    && placedGroupIds.Any(sharedGroupId =>
                        PlacementRemainingFor(sharedGroupId, moduleId) > 0
                        && SelectNextTopicInOrder(sharedGroupId, moduleId) is { } nextTopic
                        && CanShareAcrossGroups(nextTopic.LessonTypeId));
                if (nextShareableTopicUnlocked)
                {
                    PreplaceAvailableSharedLectureTopics(moduleId);
                }
                return true;
            }
            foreach (var date in generationDates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplySlotsForDate(date);
                int maxPerDay = slots.Count;
                if (maxPerDay == 0) continue;
                var modulesAttemptedToday = new HashSet<int>();
                var orderedModulesForDay = BuildOrderedModulesForDay(date);
                var lectureFirstModulesForDay = orderedModulesForDay
                    .Where(mid => RemainingFor(grp.Id, mid) > 0)
                    .Where(mid => CanShareAcrossGroups(PeekLessonTypeForDate(grp.Id, grp.CourseId, mid, date)))
                    .ToList();
                if (lectureFirstModulesForDay.Count > 0)
                {
                    var lectureFirstModuleSet = lectureFirstModulesForDay.ToHashSet();
                    orderedModulesForDay = lectureFirstModulesForDay
                        .Concat(orderedModulesForDay.Where(mid => !lectureFirstModuleSet.Contains(mid)))
                        .ToList();
                }
                int preferredMaxDistinctModulesPerDay = softOptions?.PreferredMaxDistinctModulesPerDay is int preferredDistinctLimit && preferredDistinctLimit > 0
                    ? Math.Min(preferredDistinctLimit, maxPerDay)
                    : softFill ? Math.Min(5, maxPerDay) : (maxPerDay >= 8 ? 3 : 2);
                int maxDistinctModulesPerDay = softOptions?.MaxDistinctModulesPerDay is int distinctLimit && distinctLimit > 0
                    ? Math.Min(distinctLimit, maxPerDay)
                    : softFill ? Math.Min(6, maxPerDay) : Math.Min(5, maxPerDay);
                preferredMaxDistinctModulesPerDay = Math.Min(preferredMaxDistinctModulesPerDay, maxDistinctModulesPerDay);
                var targetMinDistinctModulesPerDay = Math.Min(3, Math.Min(maxPerDay, orderedModulesForDay.Count(mid => RemainingFor(grp.Id, mid) > 0)));
                bool CanIntroduceModuleToday(int moduleId, bool bypassPreferredLimit = false)
                {
                    if (CountModuleForDay(grp.Id, date, moduleId) > 0)
                    {
                        return true;
                    }
                    var distinctToday = CountDistinctModulesForDay(grp.Id, date);
                    if (bypassPreferredLimit)
                    {
                        return true;
                    }
                    if (distinctToday >= maxDistinctModulesPerDay)
                    {
                        return false;
                    }
                    if (distinctToday < preferredMaxDistinctModulesPerDay)
                    {
                        return true;
                    }
                    var hasRemainingUsedModule = orderedModulesForDay.Any(mid =>
                        CountModuleForDay(grp.Id, date, mid) > 0
                        && RemainingFor(grp.Id, mid) > 0);
                    return !hasRemainingUsedModule;
                }
                bool ModuleHasPendingSharedLectureCatchUp(int moduleId)
                {
                    var nextTopic = SelectNextTopicInOrder(grp.Id, moduleId);
                    return nextTopic is not null
                           && HasPendingSharedLectureCatchUpBeforeTopic(grp.Id, grp.CourseId, moduleId, nextTopic, out _);
                }
                // Ранні проходи мають брати модулі з найменшою реальною місткістю, щоб прості модулі не забивали їхні вікна.
                IEnumerable<int> OrderModulesByCurrentPressure(IEnumerable<int> moduleIds, bool deferCatchUpModules)
                {
                    var candidates = moduleIds
                        .Where(mid => PlacementRemainingFor(grp.Id, mid) > 0)
                        .Distinct()
                        .ToList();
                    if (!usePressureOrderingForRange || candidates.Count <= 1)
                    {
                        return candidates;
                    }

                    var moduleBudgetCache = new Dictionary<(TimeOnly Start, TimeOnly End, int ModuleId, bool BypassDistinctLimit), int>();
                    var pressureByModule = new Dictionary<int, (int Budget, double Score)>();

                    (int Budget, double Score) MeasurePressure(int moduleId)
                    {
                        if (pressureByModule.TryGetValue(moduleId, out var cached))
                        {
                            return cached;
                        }

                        var remaining = Math.Max(1, PlacementRemainingFor(grp.Id, moduleId));
                        var budget = 0;
                        foreach (var gap in slots.Where(slot => !SlotFilled(date, slot)))
                        {
                            var slotBudget = EstimateModuleGapPlacementBudget(
                                gap,
                                moduleId,
                                bypassDistinctLimit: true,
                                maxModuleSegmentsAllowed: maxModuleSegmentsPerDay,
                                moduleBudgetCache);
                            if (slotBudget > 0)
                            {
                                budget += Math.Min(3, slotBudget);
                            }
                        }

                        var result = budget <= 0
                            ? (Budget: 0, Score: double.MaxValue)
                            : (Budget: budget, Score: budget / (double)remaining);
                        pressureByModule[moduleId] = result;
                        return result;
                    }

                    return candidates
                        .OrderBy(mid => deferCatchUpModules && ModuleHasPendingSharedLectureCatchUp(mid) ? 1 : 0)
                        .ThenBy(mid => MeasurePressure(mid).Budget <= 0 ? 1 : 0)
                        .ThenBy(mid => MeasurePressure(mid).Score)
                        .ThenBy(mid => MeasurePressure(mid).Budget)
                        .ThenByDescending(mid => PlacementRemainingFor(grp.Id, mid))
                        .ThenBy(mid => mid)
                        .ToList();
                }
                // Допоміжний прохід: заповнення залишків з різними рівнями послаблень.
                async Task FillWithRemainingModulesAsync(bool allowRepeatPreviousDay = false, bool allowExtraSameDay = false, bool relaxed = false)
                {
                    bool tryAnotherCycle;
                    do
                    {
                        tryAnotherCycle = false;
                        foreach (var moduleId in OrderModulesByCurrentPressure(
                                     PreferNotUsedLastWeek(orderedModulesForDay),
                                     deferCatchUpModules: true))
                        {
                            if (CountFor(grp.Id, date) >= maxPerDay)
                            {
                                break;
                            }
                            if (!allowExtraSameDay && modulesAttemptedToday.Contains(moduleId))
                            {
                                continue;
                            }
                            if (RemainingFor(grp.Id, moduleId) <= 0)
                            {
                                continue;
                            }
                            if (!CanIntroduceModuleToday(moduleId))
                            {
                                continue;
                            }
                            modulesAttemptedToday.Add(moduleId);
                            var preGapReservationBudget = EstimatePreGapReservationBudget();
                            var placed = await TryPlaceModuleAsync(
                                moduleId,
                                date,
                                isPrimary: false,
                                allowRepeatPreviousDay: allowRepeatPreviousDay,
                                allowExtraSameDay: allowExtraSameDay,
                                relaxed: relaxed,
                                forcedGapVariantBudget: preGapReservationBudget);
                            if (placed && allowExtraSameDay && CountFor(grp.Id, date) < maxPerDay)
                            {
                                tryAnotherCycle = true;
                            }
                        }
                    } while (allowExtraSameDay && tryAnotherCycle && CountFor(grp.Id, date) < maxPerDay);
                }
                async Task<bool> TryPlaceDistinctModulesUntilAsync(int targetDistinctCount, int primaryModuleIdValue)
                {
                    var placedAny = false;
                    bool progressed;
                    do
                    {
                        progressed = false;
                        if (CountDistinctModulesForDay(grp.Id, date) >= targetDistinctCount || CountFor(grp.Id, date) >= maxPerDay)
                        {
                            break;
                        }
                        foreach (var moduleId in OrderModulesByCurrentPressure(
                                     PreferNotUsedLastWeek(orderedModulesForDay)
                                         .Where(mid => mid != primaryModuleIdValue || CountDistinctModulesForDay(grp.Id, date) >= targetDistinctCount)
                                         .Where(mid => CountModuleForDay(grp.Id, date, mid) == 0),
                                     deferCatchUpModules: true))
                        {
                            if (RemainingFor(grp.Id, moduleId) <= 0)
                            {
                                continue;
                            }
                            if (!CanIntroduceModuleToday(moduleId))
                            {
                                continue;
                            }
                            modulesAttemptedToday.Add(moduleId);
                            var preGapReservationBudget = EstimatePreGapReservationBudget();
                            var placed = await TryPlaceModuleAsync(
                                moduleId,
                                date,
                                isPrimary: false,
                                allowRepeatPreviousDay: softFill,
                                allowExtraSameDay: softFill,
                                relaxed: softFill,
                                forcedGapVariantBudget: preGapReservationBudget);
                            if (placed)
                            {
                                placedAny = true;
                                progressed = true;
                                break;
                            }
                        }
                    } while (progressed);
                    return placedAny;
                }
                IEnumerable<int> BuildGapCandidateModules()
                {
                    var modulesWithRemaining = remainingByGroupModule
                        .Where(kv => kv.Key.GroupId == grp.Id && kv.Value > 0)
                        .Select(kv => kv.Key.ModuleId);
                    return OrderModulesByCurrentPressure(
                        PreferNotUsedLastWeek(
                            orderedModulesForDay
                                .Concat(fillerModulesOrdered)
                                .Concat(modulesWithRemaining)
                                .Distinct()),
                        deferCatchUpModules: true);
                }
                // Без побічних ефектів підглядає тип і тему, які модуль спробує взяти у gap-fill.
                bool TryPeekGapPlacementBlueprint(int moduleId, DateOnly placementDate, out bool isSelfStudyPlacement, out int lessonTypeId, out ModuleTopic? topicSelection)
                {
                    isSelfStudyPlacement = SelfStudyRemaining(grp.Id, moduleId) > 0;
                    topicSelection = null;
                    lessonTypeId = 0;
                    if (isSelfStudyPlacement)
                    {
                        topicSelection = PeekSelfStudyTopic(grp.Id, moduleId);
                        if (topicSelection is not null)
                        {
                            lessonTypeId = topicSelection.LessonTypeId;
                            return true;
                        }
                        isSelfStudyPlacement = false;
                    }
                    var savedLtIndex = ltIndex;
                    try
                    {
                        var pick = PickLessonType(grp.Id, grp.CourseId, moduleId, placementDate);
                        lessonTypeId = pick.LessonTypeId;
                        topicSelection = pick.Topic;
                    }
                    finally
                    {
                        ltIndex = savedLtIndex;
                    }
                    return lessonTypeId != 0;
                }
                // Перевіряє відповідність кафедри теми викладачу.
                bool TeacherMatchesTopicDepartment(ModuleTopic? topicSelection, int teacherId)
                {
                    return topicSelection?.DepartmentId is not int departmentId
                           || departmentId <= 0
                           || (teacherDepartmentById.TryGetValue(teacherId, out var teacherDepartment) && teacherDepartment == departmentId);
                }
                // Грубо оцінює, скільки викладачів реально доступні для модуля в конкретному порожньому слоті.
                int CountFeasibleTeacherOptionsForGap(TimeSlot gap, int moduleId, bool isSelfStudyPlacement, ModuleTopic? topicSelection, int stopAfter = 4)
                {
                    int count = 0;
                    var teacherIds = (isSelfStudyPlacement
                            ? supervisorsForModule.Where(x => x.ModuleId == moduleId).Select(x => x.TeacherId)
                            : teachersForModule.Where(x => x.ModuleId == moduleId).Select(x => x.TeacherId))
                        .Distinct()
                        .OrderBy(id => TeacherLoadPenalty(id))
                        .ThenBy(id => id);
                    foreach (var teacherId in teacherIds)
                    {
                        if (!TeacherMatchesTopicDepartment(topicSelection, teacherId))
                        {
                            continue;
                        }
                        if (!TeacherFitsWorkingHours(teacherId, date, gap.Start, gap.End))
                        {
                            continue;
                        }
                        var teacherBusy = HasTeacherOverlap(teacherId, date, gap.Start, gap.End);
                        if (teacherBusy)
                        {
                            continue;
                        }
                        count++;
                        if (count >= stopAfter)
                        {
                            break;
                        }
                    }
                    return count;
                }
                // Грубо оцінює, скільки аудиторій реально доступні для модуля в конкретному порожньому слоті.
                int CountFeasibleRoomOptionsForGap(TimeSlot gap, int moduleId, int requiredCapacity, bool ignoreGroupPreference = false, int stopAfter = 4)
                {
                    int count = 0;
                    foreach (var room in CandidateRoomsFor(moduleId, requiredCapacity, ignoreGroupPreference))
                    {
                        var roomBusy = HasRoomOverlap(room.Id, date, gap.Start, gap.End);
                        if (roomBusy)
                        {
                            continue;
                        }
                        if (ViolatesTravelFeasibility(
                                existing => existing.GroupId == grp.Id,
                                room,
                                date,
                                gap.Start,
                                gap.End,
                                $"групи {grp.Name}",
                                out _))
                        {
                            continue;
                        }
                        count++;
                        if (count >= stopAfter)
                        {
                            break;
                        }
                    }
                    return count;
                }
                // Оцінює кількість життєздатних комбінацій для одного модуля у конкретному gap-слоті.
                int EstimateModuleGapPlacementBudget(
                    TimeSlot gap,
                    int moduleId,
                    bool bypassDistinctLimit,
                    int maxModuleSegmentsAllowed,
                    Dictionary<(TimeOnly Start, TimeOnly End, int ModuleId, bool BypassDistinctLimit), int> cache)
                {
                    var cacheKey = (gap.Start, gap.End, moduleId, bypassDistinctLimit);
                    if (cache.TryGetValue(cacheKey, out var cachedBudget))
                    {
                        return cachedBudget;
                    }
                    var budget = 0;
                    if (RemainingFor(grp.Id, moduleId) > 0
                        && CanIntroduceModuleToday(moduleId, bypassPreferredLimit: bypassDistinctLimit)
                        && !ViolatesAnyModuleDayBlock(grp.Id, date, moduleId, gap.Start, gap.End, out _, maxModuleSegmentsAllowed)
                        && !InsertionWouldSplitExistingModuleBlock(grp.Id, date, moduleId, gap.Start, gap.End, out _, maxModuleSegmentsAllowed)
                        && TryPeekGapPlacementBlueprint(moduleId, date, out var isSelfStudyPlacement, out var lessonTypeId, out var topicSelection)
                        && TypeAllowed(lessonTypeId)
                        && CountGroupsWithModuleInSlot(moduleId, date, gap.Start, gap.End) < SlotGroupLimitForPlacement(grp.CourseId, lessonTypeId, isSelfStudyPlacement))
                    {
                        var teacherCount = CountFeasibleTeacherOptionsForGap(gap, moduleId, isSelfStudyPlacement, topicSelection);
                        if (teacherCount > 0)
                        {
                            var requiresRoom = (typeById.TryGetValue(lessonTypeId, out var lessonTypeMeta) ? lessonTypeMeta.RequiresRoom : (bool?)null) ?? true;
                            var roomCount = requiresRoom
                                ? CountFeasibleRoomOptionsForGap(gap, moduleId, grp.StudentsCount, ignoreGroupPreference: softFill && bypassDistinctLimit)
                                : 1;
                            if (roomCount > 0)
                            {
                                budget = Math.Min(9, teacherCount * roomCount);
                            }
                        }
                    }
                    cache[cacheKey] = budget;
                    return budget;
                }
                // Оцінює дефіцитність порожнього слоту через сумарну кількість доступних варіантів.
                (int Budget, int ViableSlots, double Score) MeasureModuleScarcity(
                    int moduleId,
                    bool bypassDistinctLimit,
                    int maxModuleSegmentsAllowed,
                    Dictionary<(TimeOnly Start, TimeOnly End, int ModuleId, bool BypassDistinctLimit), int> moduleBudgetCache)
                {
                    var remaining = Math.Max(0, PlacementRemainingFor(grp.Id, moduleId));
                    if (remaining <= 0)
                    {
                        return (0, 0, double.MaxValue);
                    }

                    var totalBudget = 0;
                    var viableSlots = 0;
                    foreach (var gap in slots.Where(slot => !SlotFilled(date, slot)))
                    {
                        var slotBudget = EstimateModuleGapPlacementBudget(
                            gap,
                            moduleId,
                            bypassDistinctLimit,
                            maxModuleSegmentsAllowed,
                            moduleBudgetCache);
                        if (slotBudget <= 0)
                        {
                            continue;
                        }

                        viableSlots++;
                        totalBudget += Math.Min(3, slotBudget);
                    }

                    if (totalBudget <= 0)
                    {
                        return (0, viableSlots, double.MaxValue);
                    }

                    return (totalBudget, viableSlots, totalBudget / (double)remaining);
                }

                int EstimateGapVariantBudget(
                    TimeSlot gap,
                    bool bypassDistinctLimit,
                    int maxModuleSegmentsAllowed,
                    Dictionary<(TimeOnly Start, TimeOnly End, int ModuleId, bool BypassDistinctLimit), int> moduleBudgetCache)
                {
                    int totalBudget = 0;
                    foreach (var moduleId in BuildGapCandidateModules())
                    {
                        var moduleBudget = EstimateModuleGapPlacementBudget(gap, moduleId, bypassDistinctLimit, maxModuleSegmentsAllowed, moduleBudgetCache);
                        totalBudget += Math.Min(3, moduleBudget);
                        if (totalBudget >= 9)
                        {
                            return 9;
                        }
                    }
                    return totalBudget;
                }
                // Дає раннім проходам сигнал, наскільки вже видно дефіцитні прогалини в поточному дні.
                int? EstimatePreGapReservationBudget()
                {
                    if (!softFill || CountFor(grp.Id, date) <= 0)
                    {
                        return null;
                    }
                    var gaps = slots.Where(sl => !SlotFilled(date, sl)).ToList();
                    if (gaps.Count == 0 || gaps.Count == slots.Count)
                    {
                        return null;
                    }
                    var moduleBudgetCache = new Dictionary<(TimeOnly Start, TimeOnly End, int ModuleId, bool BypassDistinctLimit), int>();
                    var minGapBudget = gaps
                        .Select(gap => EstimateGapVariantBudget(gap, bypassDistinctLimit: false, maxModuleSegmentsAllowed: 1, moduleBudgetCache))
                        .DefaultIfEmpty(9)
                        .Min();
                    return minGapBudget <= 4 ? minGapBudget : null;
                }
                // Для важких слотів пробуємо вузькі модулі першими, для легких — навпаки бережемо scarce-ресурси.
                IReadOnlyList<int> OrderGapCandidateModulesByScarcity(
                    TimeSlot gap,
                    bool bypassDistinctLimit,
                    IReadOnlyDictionary<(TimeOnly Start, TimeOnly End), int>? gapVariantBudgetBySlot,
                    Dictionary<(TimeOnly Start, TimeOnly End, int ModuleId, bool BypassDistinctLimit), int>? moduleBudgetCache)
                {
                    var orderedModules = BuildGapCandidateModules().Distinct().Select((moduleId, index) => (ModuleId: moduleId, Index: index)).ToList();
                    if (gapVariantBudgetBySlot is null || moduleBudgetCache is null)
                    {
                        return orderedModules
                            .OrderBy(entry => ModuleHasPendingSharedLectureCatchUp(entry.ModuleId) ? 1 : 0)
                            .ThenBy(entry => entry.Index)
                            .Select(x => x.ModuleId)
                            .ToList();
                    }
                    var gapKey = (gap.Start, gap.End);
                    var currentGapBudget = gapVariantBudgetBySlot.TryGetValue(gapKey, out var currentBudget) ? currentBudget : 9;
                    var hasScarcerGap = gapVariantBudgetBySlot.Any(entry => entry.Key != gapKey && entry.Value < currentGapBudget);
                    if (currentGapBudget > 3 && !hasScarcerGap)
                    {
                        return orderedModules
                            .OrderBy(entry => ModuleHasPendingSharedLectureCatchUp(entry.ModuleId) ? 1 : 0)
                            .ThenBy(entry => entry.Index)
                            .Select(x => x.ModuleId)
                            .ToList();
                    }
                    var scoredModules = orderedModules
                        .Select(entry => new
                        {
                            entry.ModuleId,
                            entry.Index,
                            CatchUpHold = ModuleHasPendingSharedLectureCatchUp(entry.ModuleId),
                            Budget = EstimateModuleGapPlacementBudget(gap, entry.ModuleId, bypassDistinctLimit, maxModuleSegmentsPerDay, moduleBudgetCache),
                            Scarcity = MeasureModuleScarcity(entry.ModuleId, bypassDistinctLimit, maxModuleSegmentsPerDay, moduleBudgetCache)
                        })
                        .ToList();
                    return currentGapBudget <= 3
                        ? scoredModules
                            .OrderBy(entry => entry.CatchUpHold ? 1 : 0)
                            .ThenBy(entry => entry.Scarcity.Score)
                            .ThenBy(entry => entry.Scarcity.ViableSlots)
                            .ThenBy(entry => entry.Budget <= 0 ? int.MaxValue : entry.Budget)
                            .ThenBy(entry => entry.Index)
                            .Select(entry => entry.ModuleId)
                            .ToList()
                        : scoredModules
                            .OrderBy(entry => entry.CatchUpHold ? 1 : 0)
                            .ThenBy(entry => entry.Scarcity.Score)
                            .ThenBy(entry => entry.Scarcity.ViableSlots)
                            .ThenByDescending(entry => entry.Budget)
                            .ThenBy(entry => entry.Index)
                            .Select(entry => entry.ModuleId)
                            .ToList();
                }
                async Task<bool> TryFillGapWithVariantsAsync(
                    TimeSlot gap,
                    bool allowRepeatPreviousDay,
                    bool allowExtraSameDay,
                    bool relaxed,
                    bool bypassDistinctLimit,
                    int maxModuleSegmentsAllowed,
                    IReadOnlyDictionary<(TimeOnly Start, TimeOnly End), int>? gapVariantBudgetBySlot = null,
                    Dictionary<(TimeOnly Start, TimeOnly End, int ModuleId, bool BypassDistinctLimit), int>? moduleBudgetCache = null)
                {
                    int? forcedGapVariantBudget = gapVariantBudgetBySlot is not null
                        && gapVariantBudgetBySlot.TryGetValue((gap.Start, gap.End), out var gapBudget)
                        ? gapBudget
                        : null;
                    foreach (var moduleId in OrderGapCandidateModulesByScarcity(gap, bypassDistinctLimit, gapVariantBudgetBySlot, moduleBudgetCache))
                    {
                        if (RemainingFor(grp.Id, moduleId) <= 0)
                        {
                            continue;
                        }
                        if (!CanIntroduceModuleToday(moduleId, bypassPreferredLimit: bypassDistinctLimit))
                        {
                            continue;
                        }
                        var placed = await TryPlaceModuleAsync(
                            moduleId,
                            date,
                            isPrimary: false,
                            allowRepeatPreviousDay: allowRepeatPreviousDay,
                            allowExtraSameDay: allowExtraSameDay,
                            relaxed: relaxed,
                            preferEarliestSlot: true,
                            forcedSlot: gap,
                            forcedGapVariantBudget: forcedGapVariantBudget,
                            bypassCatchUpHold: bypassDistinctLimit,
                            maxModuleSegmentsOverride: maxModuleSegmentsAllowed);
                        if (placed)
                        {
                            return true;
                        }
                    }
                    return false;
                }
                async Task<bool> TryExhaustiveGapFillAsync()
                {
                    bool anyPlaced = false;
                    bool progress;
                    int pass = 0;
                    int GapFillSegmentLimit(bool relaxed, bool bypassDistinctLimit)
                        => relaxed || bypassDistinctLimit ? 2 : maxModuleSegmentsPerDay;
                    async Task<bool> TryFillGapStageAsync(
                        bool allowRepeatPreviousDay,
                        bool allowExtraSameDay,
                        bool relaxed,
                        bool bypassDistinctLimit)
                    {
                        var maxModuleSegmentsAllowedForGapFill = GapFillSegmentLimit(relaxed, bypassDistinctLimit);
                        var moduleBudgetCache = new Dictionary<(TimeOnly Start, TimeOnly End, int ModuleId, bool BypassDistinctLimit), int>();
                        var gaps = slots.Where(sl => !SlotFilled(date, sl)).ToList();
                        var gapVariantBudgetBySlot = gaps.ToDictionary(
                            gap => (gap.Start, gap.End),
                            gap => EstimateGapVariantBudget(gap, bypassDistinctLimit, maxModuleSegmentsAllowedForGapFill, moduleBudgetCache));
                        gaps = gaps
                            .OrderBy(gap => gapVariantBudgetBySlot[(gap.Start, gap.End)])
                            // Прогалини дозаповнюємо за ходом дня, щоб раніший слот не отримав пізнішу тему після пізнього слоту.
                            .ThenBy(gap => slotIndexByTime.TryGetValue((gap.Start, gap.End), out var gapIndex) ? gapIndex : 0)
                            .ToList();
                        foreach (var gap in gaps)
                        {
                            if (CountFor(grp.Id, date) >= maxPerDay)
                            {
                                break;
                            }
                            if (SlotFilled(date, gap))
                            {
                                continue;
                            }
                            var placed = await TryFillGapWithVariantsAsync(
                                gap,
                                allowRepeatPreviousDay,
                                allowExtraSameDay,
                                relaxed,
                                bypassDistinctLimit,
                                maxModuleSegmentsAllowedForGapFill,
                                gapVariantBudgetBySlot,
                                moduleBudgetCache);
                            if (placed)
                            {
                                return true;
                            }
                        }
                        return false;
                    }
                    do
                    {
                        progress = false;
                        pass++;
                        var placed = await TryFillGapStageAsync(
                                         allowRepeatPreviousDay: false,
                                         allowExtraSameDay: false,
                                         relaxed: false,
                                         bypassDistinctLimit: false)
                                     || await TryFillGapStageAsync(
                                         allowRepeatPreviousDay: false,
                                         allowExtraSameDay: true,
                                         relaxed: false,
                                         bypassDistinctLimit: false)
                                     || await TryFillGapStageAsync(
                                         allowRepeatPreviousDay: true,
                                         allowExtraSameDay: true,
                                         relaxed: true,
                                         bypassDistinctLimit: false)
                                     || await TryFillGapStageAsync(
                                         allowRepeatPreviousDay: true,
                                         allowExtraSameDay: true,
                                         relaxed: true,
                                         bypassDistinctLimit: true);
                        if (placed)
                        {
                            progress = true;
                            anyPlaced = true;
                        }
                    } while (progress && pass < Math.Max(1, slots.Count * 2) && CountFor(grp.Id, date) < maxPerDay);
                    return anyPlaced;
                }
                bool IsSameDraftBusySlot(TeacherDraftItem draft, BusySlot slot, TimeOnly start, TimeOnly end)
                    => SlotMatches(
                        slot,
                        draft.GroupId,
                        draft.Date,
                        start,
                        end,
                        draft.ModuleId,
                        draft.TeacherId,
                        draft.RoomId,
                        draft.ModuleTopicId);
                bool ViolatesMoveTravel(
                    TeacherDraftItem candidate,
                    TimeSlot targetSlot,
                    Func<BusySlot, bool> ownerMatch,
                    string subjectLabel,
                    out string reason)
                {
                    reason = string.Empty;
                    if (candidate.RoomId is not int roomId
                        || !roomBuildingById.TryGetValue(roomId, out var targetBuildingId)
                        || targetBuildingId == 0)
                    {
                        return false;
                    }
                    foreach (var existing in BusyForDate(date).Where(slot =>
                                 slot.RoomId != null
                                 && slot.BuildingId.HasValue
                                 && ownerMatch(slot)
                                 && !IsSameDraftBusySlot(candidate, slot, candidate.StartTime, candidate.EndTime)))
                    {
                        var sourceBuildingId = existing.BuildingId!.Value;
                        if (sourceBuildingId == targetBuildingId)
                        {
                            continue;
                        }
                        var needMinutes = TravelMinutes(sourceBuildingId, targetBuildingId);
                        var gapBefore = (targetSlot.Start.ToTimeSpan() - existing.EndTime.ToTimeSpan()).TotalMinutes;
                        var gapAfter = (existing.StartTime.ToTimeSpan() - targetSlot.End.ToTimeSpan()).TotalMinutes;
                        if (existing.EndTime <= targetSlot.Start && gapBefore < needMinutes)
                        {
                            reason = $"Для {subjectLabel} недостатньо часу на перехід до корпусу #{targetBuildingId} після заняття в корпусі #{sourceBuildingId}: доступно {gapBefore:N0} хв, потрібно {needMinutes} хв.";
                            return true;
                        }
                        if (targetSlot.End <= existing.StartTime && gapAfter < needMinutes)
                        {
                            reason = $"Для {subjectLabel} недостатньо часу на перехід від корпусу #{sourceBuildingId} до корпусу #{targetBuildingId} перед заняттям: доступно {gapAfter:N0} хв, потрібно {needMinutes} хв.";
                            return true;
                        }
                    }
                    return false;
                }
                bool CanMoveDraftToGap(TeacherDraftItem candidate, TimeSlot targetGap, out string reason)
                {
                    reason = string.Empty;
                    var candidateGroup = selectedGroupsById.TryGetValue(candidate.GroupId, out var resolvedCandidateGroup)
                        ? resolvedCandidateGroup
                        : grp;
                    if (SlotFilledForGroup(candidate.GroupId, date, targetGap)
                        || candidate.Date != date
                        || candidate.IsLocked
                        || CanShareAcrossGroups(candidate.LessonTypeId)
                        || excludedTypeIds.Contains(candidate.LessonTypeId))
                    {
                        return false;
                    }
                    if (!slotIndexByTime.TryGetValue((candidate.StartTime, candidate.EndTime), out var oldSlotIndex)
                        || !slotIndexByTime.TryGetValue((targetGap.Start, targetGap.End), out var targetSlotIndex))
                    {
                        return false;
                    }
                    var maxSegmentsAllowed = softFill ? 2 : maxModuleSegmentsPerDay;
                    var moduleIndexesAfterMove = BusyForGroupDate(grp.Id, date)
                        .Where(slot => slot.ModuleId == candidate.ModuleId
                                       && !excludedTypeIds.Contains(slot.LessonTypeId))
                        .Select(slot => slotIndexByTime.TryGetValue((slot.StartTime, slot.EndTime), out var idx) ? idx : -1)
                        .Where(idx => idx >= 0 && idx != oldSlotIndex)
                        .Append(targetSlotIndex)
                        .Distinct()
                        .OrderBy(idx => idx)
                        .ToList();
                    if (CountModuleSegments(moduleIndexesAfterMove) > maxSegmentsAllowed)
                    {
                        reason = ModuleSegmentLimitReason(candidate.ModuleId, maxSegmentsAllowed);
                        return false;
                    }
                    if (InsertionWouldSplitExistingModuleBlock(candidate.GroupId, date, candidate.ModuleId, targetGap.Start, targetGap.End, out var insertionSplitReason, maxSegmentsAllowed))
                    {
                        reason = insertionSplitReason;
                        return false;
                    }
                    if (candidate.ModuleTopicId is int topicId
                        && topicById.TryGetValue(topicId, out var topic)
                        && ViolatesTopicCalendarOrder(candidate.GroupId, candidate.ModuleId, topic, date, targetGap.Start, targetGap.End))
                    {
                        reason = $"Для групи {grp.Name} порушується хронологічний порядок тем модуля <{ModuleTitleLabel(candidate.ModuleId)}> після перестановки.";
                        return false;
                    }
                    if (candidate.TeacherId is int teacherId)
                    {
                        if (!TeacherFitsWorkingHours(teacherId, date, targetGap.Start, targetGap.End))
                        {
                            reason = $"Викладач {TeacherLabel(teacherId)} не працює у слоті {targetGap.Start:HH\\:mm}-{targetGap.End:HH\\:mm}.";
                            return false;
                        }
                        if (BusyForTeacherDate(teacherId, date).Any(slot =>
                                SlotOverlaps(slot, date, targetGap.Start, targetGap.End)
                                && !IsSameDraftBusySlot(candidate, slot, candidate.StartTime, candidate.EndTime)))
                        {
                            reason = $"Викладач {TeacherLabel(teacherId)} зайнятий у слоті {targetGap.Start:HH\\:mm}-{targetGap.End:HH\\:mm}.";
                            return false;
                        }
                    }
                    if (BusyForGroupDate(candidate.GroupId, date).Any(slot =>
                            SlotOverlaps(slot, date, targetGap.Start, targetGap.End)
                            && !IsSameDraftBusySlot(candidate, slot, candidate.StartTime, candidate.EndTime)))
                    {
                        reason = $"Група {candidateGroup.Name} зайнята у слоті {targetGap.Start:HH\\:mm}-{targetGap.End:HH\\:mm}.";
                        return false;
                    }
                    if (candidate.RoomId is int roomId)
                    {
                        if (BusyForRoomDate(roomId, date).Any(slot =>
                                SlotOverlaps(slot, date, targetGap.Start, targetGap.End)
                                && !IsSameDraftBusySlot(candidate, slot, candidate.StartTime, candidate.EndTime)))
                        {
                            reason = $"Аудиторія #{roomId} зайнята у слоті {targetGap.Start:HH\\:mm}-{targetGap.End:HH\\:mm}.";
                            return false;
                        }
                    }
                    var shiftedSlotGroupLimit = SlotGroupLimitForPlacement(
                        candidateGroup.CourseId,
                        candidate.LessonTypeId,
                        candidate.IsSelfStudy);
                    if (CountGroupsWithModuleInSlot(candidate.ModuleId, date, targetGap.Start, targetGap.End) >= shiftedSlotGroupLimit)
                    {
                        reason = $"Досягнуто ліміт паралельних груп модуля <{ModuleTitleLabel(candidate.ModuleId)}> у слоті.";
                        return false;
                    }
                    if (ViolatesMoveTravel(candidate, targetGap, slot => slot.GroupId == candidate.GroupId, $"групи {candidateGroup.Name}", out var groupTravelReason))
                    {
                        reason = groupTravelReason;
                        return false;
                    }
                    if (candidate.TeacherId is int teacherForTravel
                        && ViolatesMoveTravel(candidate, targetGap, slot => slot.TeacherId == teacherForTravel, $"викладача {TeacherLabel(teacherForTravel)}", out var teacherTravelReason))
                    {
                        reason = teacherTravelReason;
                        return false;
                    }
                    return true;
                }
                bool TryApplyDraftMove(
                    TeacherDraftItem candidate,
                    TimeSlot targetGap,
                    out TimeOnly oldStart,
                    out TimeOnly oldEnd,
                    out BusySlot? oldBusySlot,
                    out BusySlot newBusySlot)
                {
                    oldStart = candidate.StartTime;
                    oldEnd = candidate.EndTime;
                    oldBusySlot = FindBusySlotForDraft(candidate, oldStart, oldEnd);
                    var buildingId = candidate.RoomId is int roomId && roomBuildingById.TryGetValue(roomId, out var candidateBuildingId)
                        ? candidateBuildingId
                        : (int?)null;
                    newBusySlot = new BusySlot(
                        candidate.GroupId,
                        candidate.TeacherId,
                        candidate.RoomId,
                        date,
                        targetGap.Start,
                        targetGap.End,
                        buildingId,
                        candidate.ModuleId,
                        candidate.LessonTypeId,
                        candidate.ModuleTopicId,
                        true);
                    if (oldBusySlot is null || !RemoveBusySlot(oldBusySlot))
                    {
                        return false;
                    }
                    candidate.StartTime = targetGap.Start;
                    candidate.EndTime = targetGap.End;
                    candidate.DayOfWeek = date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
                    AddBusySlot(newBusySlot);
                    InvalidateGapResourceCaches();
                    return true;
                }
                void RollbackDraftMove(TeacherDraftItem candidate, TimeOnly oldStart, TimeOnly oldEnd, BusySlot oldBusySlot, BusySlot newBusySlot)
                {
                    RemoveBusySlot(newBusySlot);
                    candidate.StartTime = oldStart;
                    candidate.EndTime = oldEnd;
                    candidate.DayOfWeek = date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
                    AddBusySlot(oldBusySlot);
                    InvalidateGapResourceCaches();
                }
                async Task<bool> TryTargetedRepairGapAsync(TimeSlot targetGap)
                {
                    if (!softFill || SlotFilled(date, targetGap))
                    {
                        return false;
                    }
                    var targetIndex = slotIndexByTime.TryGetValue((targetGap.Start, targetGap.End), out var idx)
                        ? idx
                        : 0;
                    var candidates = movableDrafts
                        .Where(candidate => candidate.GroupId == grp.Id
                                            && candidate.Date == date
                                            && !candidate.IsLocked
                                            && !CanShareAcrossGroups(candidate.LessonTypeId)
                                            && !excludedTypeIds.Contains(candidate.LessonTypeId)
                                            && !(candidate.StartTime == targetGap.Start && candidate.EndTime == targetGap.End))
                        .Select(candidate => new
                        {
                            Draft = candidate,
                            OldSlot = slots.FirstOrDefault(slot => slot.Start == candidate.StartTime && slot.End == candidate.EndTime),
                            OldIndex = slotIndexByTime.TryGetValue((candidate.StartTime, candidate.EndTime), out var oldIndex) ? oldIndex : int.MaxValue
                        })
                        .Where(entry => entry.OldSlot is not null)
                        .OrderBy(entry => Math.Abs(entry.OldIndex - targetIndex))
                        .ThenBy(entry => entry.OldIndex)
                        .Take(12)
                        .ToList();
                    foreach (var candidateEntry in candidates)
                    {
                        var oldSlot = candidateEntry.OldSlot!;
                        var moduleBudgetCache = new Dictionary<(TimeOnly Start, TimeOnly End, int ModuleId, bool BypassDistinctLimit), int>();
                        var repairSegmentLimit = softFill ? 2 : maxModuleSegmentsPerDay;
                        var deficitModulesForOldSlot = BuildGapCandidateModules()
                            .Where(moduleId => moduleId != candidateEntry.Draft.ModuleId
                                               && RemainingFor(grp.Id, moduleId) > 0
                                               && RemainingFor(grp.Id, moduleId) >= RemainingFor(grp.Id, candidateEntry.Draft.ModuleId)
                                               && EstimateModuleGapPlacementBudget(
                                                   oldSlot,
                                                   moduleId,
                                                   bypassDistinctLimit: true,
                                                   maxModuleSegmentsAllowed: repairSegmentLimit,
                                                   moduleBudgetCache) > 0)
                            .OrderByDescending(moduleId => RemainingFor(grp.Id, moduleId))
                            .ThenBy(moduleId => moduleId)
                            .ToList();
                        if (deficitModulesForOldSlot.Count == 0)
                        {
                            continue;
                        }
                        if (!CanMoveDraftToGap(candidateEntry.Draft, targetGap, out var moveReason))
                        {
                            if (!string.IsNullOrWhiteSpace(moveReason))
                            {
                                RecordSlotFailureReason(date, targetGap, moveReason);
                            }
                            continue;
                        }
                        if (!TryApplyDraftMove(
                                candidateEntry.Draft,
                                targetGap,
                                out var oldStart,
                                out var oldEnd,
                                out var oldBusySlot,
                                out var newBusySlot)
                            || oldBusySlot is null)
                        {
                            continue;
                        }
                        var filledOldSlot = false;
                        foreach (var deficitModuleId in deficitModulesForOldSlot)
                        {
                            filledOldSlot = await TryPlaceModuleAsync(
                                deficitModuleId,
                                date,
                                isPrimary: false,
                                allowRepeatPreviousDay: true,
                                allowExtraSameDay: true,
                                relaxed: true,
                                preferEarliestSlot: true,
                                forcedSlot: oldSlot,
                                bypassCatchUpHold: true,
                                maxModuleSegmentsOverride: softFill ? 2 : maxModuleSegmentsPerDay);
                            if (filledOldSlot)
                            {
                                break;
                            }
                        }
                        if (filledOldSlot)
                        {
                            warnings.Add($"[{date:yyyy-MM-dd}] {grp.Name}: цільовий repair-pass пересунув заняття {candidateEntry.Draft.ModuleId} у слот {targetGap.Start:HH\\:mm}-{targetGap.End:HH\\:mm} і дозаповнив звільнений слот {oldStart:HH\\:mm}-{oldEnd:HH\\:mm}.");
                            return true;
                        }
                        RollbackDraftMove(candidateEntry.Draft, oldStart, oldEnd, oldBusySlot, newBusySlot);
                    }
                    return false;
                }
                TeacherDraftItem? FindCreatedDraftForBusySlot(BusySlot busySlot)
                    => allCreatedDrafts.FirstOrDefault(draft =>
                        SlotMatches(
                            busySlot,
                            draft.GroupId,
                            draft.Date,
                            draft.StartTime,
                            draft.EndTime,
                            draft.ModuleId,
                            draft.TeacherId,
                            draft.RoomId,
                            draft.ModuleTopicId));
                async Task<bool> TryReleaseExternalBlockerAndFillGapAsync(TimeSlot targetGap)
                {
                    if (!softFill || SlotFilled(date, targetGap) || CountFor(grp.Id, date) >= maxPerDay)
                    {
                        return false;
                    }

                    var targetIndex = slotIndexByTime.TryGetValue((targetGap.Start, targetGap.End), out var idx)
                        ? idx
                        : 0;
                    var blockers = BusyForDate(date)
                        .Where(slot => slot.GroupId != grp.Id
                                       && slot.StartTime == targetGap.Start
                                       && slot.EndTime == targetGap.End)
                        .Select(FindCreatedDraftForBusySlot)
                        .Where(draft => draft is not null
                                        && draft.Date == date
                                        && !draft.IsLocked
                                        && draft.Status == DraftStatus.Draft
                                        && !CanShareAcrossGroups(draft.LessonTypeId)
                                        && !excludedTypeIds.Contains(draft.LessonTypeId)
                                        && selectedGroupsById.TryGetValue(draft.GroupId, out var blockerGroup)
                                        && blockerGroup.CourseId == grp.CourseId)
                        .Select(draft => draft!)
                        .Distinct()
                        .Select(draft => new
                        {
                            Draft = draft,
                            OldIndex = slotIndexByTime.TryGetValue((draft.StartTime, draft.EndTime), out var oldIndex)
                                ? oldIndex
                                : int.MaxValue
                        })
                        .OrderBy(entry => Math.Abs(entry.OldIndex - targetIndex))
                        .ThenBy(entry => entry.OldIndex)
                        .Take(8)
                        .ToList();
                    foreach (var blocker in blockers)
                    {
                        var blockerTargetSlots = slots
                            .Where(slot => !(slot.Start == targetGap.Start && slot.End == targetGap.End))
                            .Where(slot => !SlotFilledForGroup(blocker.Draft.GroupId, date, slot))
                            .OrderBy(slot => slotIndexByTime.TryGetValue((slot.Start, slot.End), out var slotIndex)
                                ? Math.Abs(slotIndex - blocker.OldIndex)
                                : int.MaxValue)
                            .ThenBy(slot => slot.Start)
                            .Take(6)
                            .ToList();
                        foreach (var blockerTargetSlot in blockerTargetSlots)
                        {
                            if (!CanMoveDraftToGap(blocker.Draft, blockerTargetSlot, out _))
                            {
                                continue;
                            }
                            if (!TryApplyDraftMove(
                                    blocker.Draft,
                                    blockerTargetSlot,
                                    out var oldStart,
                                    out var oldEnd,
                                    out var oldBusySlot,
                                    out var newBusySlot)
                                || oldBusySlot is null)
                            {
                                continue;
                            }

                            var filledTargetGap = await TryFillGapWithVariantsAsync(
                                targetGap,
                                allowRepeatPreviousDay: true,
                                allowExtraSameDay: true,
                                relaxed: true,
                                bypassDistinctLimit: true,
                                maxModuleSegmentsAllowed: softFill ? 2 : maxModuleSegmentsPerDay);
                            if (filledTargetGap)
                            {
                                var blockerGroupName = selectedGroupsById.TryGetValue(blocker.Draft.GroupId, out var blockerGroup)
                                    ? blockerGroup.Name
                                    : $"#{blocker.Draft.GroupId}";
                                warnings.Add($"[{date:yyyy-MM-dd}] {grp.Name}: ресурсний repair-pass пересунув заняття групи {blockerGroupName} з {oldStart:HH\\:mm}-{oldEnd:HH\\:mm} у {blockerTargetSlot.Start:HH\\:mm}-{blockerTargetSlot.End:HH\\:mm} і дозаповнив слот {targetGap.Start:HH\\:mm}-{targetGap.End:HH\\:mm}.");
                                return true;
                            }

                            RollbackDraftMove(blocker.Draft, oldStart, oldEnd, oldBusySlot, newBusySlot);
                        }
                    }

                    return false;
                }
                async Task<bool> TryTargetedRepairGapWithMoveChainAsync(TimeSlot targetGap)
                {
                    if (!softFill || SlotFilled(date, targetGap))
                    {
                        return false;
                    }

                    const int maxDepth = 3;
                    const int beamWidth = 10;
                    const int trialLimit = 120;
                    var gapCountBefore = CountDayGaps();
                    var trialCount = 0;
                    var movedDrafts = new HashSet<TeacherDraftItem>();
                    var visitedGaps = new HashSet<(TimeOnly Start, TimeOnly End)>
                    {
                        (targetGap.Start, targetGap.End)
                    };

                    async Task<bool> TryRepairGapByMoveChainAsync(TimeSlot currentGap, int depth)
                    {
                        if (depth <= 0 || SlotFilled(date, currentGap) || trialCount >= trialLimit)
                        {
                            return false;
                        }

                        var targetIndex = slotIndexByTime.TryGetValue((currentGap.Start, currentGap.End), out var idx)
                            ? idx
                            : 0;
                        var moduleBudgetCache = new Dictionary<(TimeOnly Start, TimeOnly End, int ModuleId, bool BypassDistinctLimit), int>();
                        var repairSegmentLimit = softFill ? 2 : maxModuleSegmentsPerDay;
                        var candidates = movableDrafts
                            .Where(candidate => candidate.GroupId == grp.Id
                                                && candidate.Date == date
                                                && !candidate.IsLocked
                                                && !movedDrafts.Contains(candidate)
                                                && !CanShareAcrossGroups(candidate.LessonTypeId)
                                                && !excludedTypeIds.Contains(candidate.LessonTypeId)
                                                && !(candidate.StartTime == currentGap.Start && candidate.EndTime == currentGap.End))
                            .Select(candidate => new
                            {
                                Draft = candidate,
                                OldSlot = slots.FirstOrDefault(slot => slot.Start == candidate.StartTime && slot.End == candidate.EndTime),
                                OldIndex = slotIndexByTime.TryGetValue((candidate.StartTime, candidate.EndTime), out var oldIndex) ? oldIndex : int.MaxValue
                            })
                            .Where(entry => entry.OldSlot is not null
                                            && !visitedGaps.Contains((entry.OldSlot!.Start, entry.OldSlot.End)))
                            .Select(entry => new
                            {
                                entry.Draft,
                                OldSlot = entry.OldSlot!,
                                entry.OldIndex,
                                DirectBudget = EstimateGapVariantBudget(
                                    entry.OldSlot!,
                                    bypassDistinctLimit: true,
                                    maxModuleSegmentsAllowed: repairSegmentLimit,
                                    moduleBudgetCache)
                            })
                            .OrderByDescending(entry => entry.DirectBudget > 0)
                            .ThenBy(entry => Math.Abs(entry.OldIndex - targetIndex))
                            .ThenByDescending(entry => CountModuleForDay(grp.Id, date, entry.Draft.ModuleId))
                            .ThenBy(entry => entry.OldIndex)
                            .Take(beamWidth)
                            .ToList();

                        foreach (var candidateEntry in candidates)
                        {
                            if (trialCount++ >= trialLimit)
                            {
                                break;
                            }

                            var oldSlot = candidateEntry.OldSlot;
                            var oldSlotKey = (oldSlot.Start, oldSlot.End);
                            if (visitedGaps.Contains(oldSlotKey))
                            {
                                continue;
                            }

                            if (!CanMoveDraftToGap(candidateEntry.Draft, currentGap, out var moveReason))
                            {
                                if (!string.IsNullOrWhiteSpace(moveReason))
                                {
                                    RecordSlotFailureReason(date, currentGap, moveReason);
                                }
                                continue;
                            }

                            if (!TryApplyDraftMove(
                                    candidateEntry.Draft,
                                    currentGap,
                                    out var oldStart,
                                    out var oldEnd,
                                    out var oldBusySlot,
                                    out var newBusySlot)
                                || oldBusySlot is null)
                            {
                                continue;
                            }

                            movedDrafts.Add(candidateEntry.Draft);
                            visitedGaps.Add(oldSlotKey);
                            var repaired = false;
                            if (CountFor(grp.Id, date) < maxPerDay)
                            {
                                repaired = await TryFillGapWithVariantsAsync(
                                    oldSlot,
                                    allowRepeatPreviousDay: true,
                                    allowExtraSameDay: true,
                                    relaxed: true,
                                    bypassDistinctLimit: true,
                                    maxModuleSegmentsAllowed: softFill ? 2 : maxModuleSegmentsPerDay);
                            }
                            if (!repaired && depth > 1)
                            {
                                repaired = await TryRepairGapByMoveChainAsync(oldSlot, depth - 1);
                            }
                            if (repaired && CountDayGaps() < gapCountBefore)
                            {
                                return true;
                            }

                            movedDrafts.Remove(candidateEntry.Draft);
                            visitedGaps.Remove(oldSlotKey);
                            RollbackDraftMove(candidateEntry.Draft, oldStart, oldEnd, oldBusySlot, newBusySlot);
                        }

                        return false;
                    }

                    return await TryRepairGapByMoveChainAsync(targetGap, maxDepth)
                           || await TryTargetedRepairGapAsync(targetGap)
                           || await TryReleaseExternalBlockerAndFillGapAsync(targetGap);
                }
                bool TryRepairLectureOrder()
                {
                    var lectureSlots = BusyForGroup(grp.Id)
                        .Where(slot => slot.Date == date
                                       && CanShareAcrossGroups(slot.LessonTypeId)
                                       && !excludedTypeIds.Contains(slot.LessonTypeId))
                        .Select(slot => new
                        {
                            slot.StartTime,
                            slot.EndTime,
                            Order = GetSlotOrder(slot.StartTime, slot.EndTime)
                        })
                        .Where(slot => slot.Order > 0)
                        .OrderBy(slot => slot.Order)
                        .ToList();
                    if (lectureSlots.Count == 0)
                    {
                        return false;
                    }

                    var firstLectureOrder = lectureSlots[0].Order;
                    var lastLectureEnd = lectureSlots
                        .OrderByDescending(slot => slot.Order)
                        .First()
                        .EndTime;
                    var earlyNonLectures = movableDrafts
                        .Where(candidate => candidate.GroupId == grp.Id
                                            && candidate.Date == date
                                            && candidate.Status == DraftStatus.Draft
                                            && !candidate.IsLocked
                                            && !CanShareAcrossGroups(candidate.LessonTypeId)
                                            && !excludedTypeIds.Contains(candidate.LessonTypeId))
                        .Select(candidate => new
                        {
                            Draft = candidate,
                            Order = GetSlotOrder(candidate.StartTime, candidate.EndTime)
                        })
                        .Where(entry => entry.Order > 0 && entry.Order < firstLectureOrder)
                        .OrderByDescending(entry => entry.Order)
                        .ThenBy(entry => entry.Draft.StartTime)
                        .ToList();
                    if (earlyNonLectures.Count == 0)
                    {
                        return false;
                    }

                    var targetGaps = slots
                        .Where(gap => gap.Start >= lastLectureEnd && !SlotFilled(date, gap))
                        .OrderBy(gap => slotIndexByTime.TryGetValue((gap.Start, gap.End), out var gapIndex) ? gapIndex : int.MaxValue)
                        .ToList();
                    if (targetGaps.Count == 0)
                    {
                        return false;
                    }

                    foreach (var earlyEntry in earlyNonLectures)
                    {
                        foreach (var targetGap in targetGaps)
                        {
                            if (SlotFilled(date, targetGap)
                                || !CanMoveDraftToGap(earlyEntry.Draft, targetGap, out _))
                            {
                                continue;
                            }

                            if (TryApplyDraftMove(
                                    earlyEntry.Draft,
                                    targetGap,
                                    out var oldStart,
                                    out var oldEnd,
                                    out var oldBusySlot,
                                    out _)
                                && oldBusySlot is not null)
                            {
                                warnings.Add($"[{date:yyyy-MM-dd}] {grp.Name}: лекційний repair-pass пересунув заняття {earlyEntry.Draft.ModuleId} зі слоту {oldStart:HH\\:mm}-{oldEnd:HH\\:mm} після лекційного блоку у слот {targetGap.Start:HH\\:mm}-{targetGap.End:HH\\:mm}.");
                                return true;
                            }
                        }

                        var removedStart = earlyEntry.Draft.StartTime;
                        var removedEnd = earlyEntry.Draft.EndTime;
                        var removedBusySlot = FindBusySlotForDraft(earlyEntry.Draft, removedStart, removedEnd);
                        if (removedBusySlot is not null)
                        {
                            RemoveBusySlot(removedBusySlot);
                        }
                        movableDrafts.Remove(earlyEntry.Draft);
                        createdDrafts.Remove(earlyEntry.Draft);
                        if (allCreatedDrafts.Remove(earlyEntry.Draft))
                        {
                            if (created > 0)
                            {
                                created--;
                            }
                            Dec(earlyEntry.Draft.GroupId, earlyEntry.Draft.Date);
                        }
                        RemoveDraftEntity(earlyEntry.Draft);
                        InvalidateGapResourceCaches();
                        warnings.Add($"[{date:yyyy-MM-dd}] {grp.Name}: лекційний repair-pass прибрав заняття {earlyEntry.Draft.ModuleId} зі слоту {removedStart:HH\\:mm}-{removedEnd:HH\\:mm}, бо його не вдалося безпечно пересунути після лекційного блоку.");
                        return true;
                    }

                    return false;
                }
                async Task<bool> TryTargetedRepairAnyGapAsync()
                {
                    if (!softFill || CountFor(grp.Id, date) >= maxPerDay)
                    {
                        return false;
                    }
                    var moduleBudgetCache = new Dictionary<(TimeOnly Start, TimeOnly End, int ModuleId, bool BypassDistinctLimit), int>();
                    var gaps = slots
                        .Where(gap => !SlotFilled(date, gap))
                        .ToList();
                    if (gaps.Count == 0)
                    {
                        return false;
                    }
                    var gapVariantBudgetBySlot = gaps.ToDictionary(
                        gap => (gap.Start, gap.End),
                        gap => EstimateGapVariantBudget(
                            gap,
                            bypassDistinctLimit: true,
                            maxModuleSegmentsAllowed: softFill ? 2 : maxModuleSegmentsPerDay,
                            moduleBudgetCache));
                    foreach (var gap in gaps
                                 .OrderBy(gap => gapVariantBudgetBySlot[(gap.Start, gap.End)])
                                 .ThenBy(gap => slotIndexByTime.TryGetValue((gap.Start, gap.End), out var gapIndex) ? gapIndex : 0))
                    {
                        if (SlotFilled(date, gap))
                        {
                            continue;
                        }
                        if (await TryTargetedRepairGapWithMoveChainAsync(gap))
                        {
                            return true;
                        }
                    }
                    return false;
                }
                int CountDayGaps()
                    => slots.Count(slot => !SlotFilled(date, slot));
                async Task<bool> RunFinalGapOptimizationAsync()
                {
                    var initialGaps = CountDayGaps();
                    if (initialGaps == 0)
                    {
                        return false;
                    }

                    var bestGapCount = initialGaps;
                    var improved = false;
                    var optimizationStartedAt = Stopwatch.GetTimestamp();
                    var optimizationBudget = TimeSpan.FromSeconds(softFill ? 8 : 2);
                    bool HasOptimizationBudget()
                        => Stopwatch.GetElapsedTime(optimizationStartedAt) < optimizationBudget;
                    for (var cycle = 0; cycle < Math.Max(1, slots.Count) && HasOptimizationBudget(); cycle++)
                    {
                        var beforeGaps = CountDayGaps();
                        if (beforeGaps == 0)
                        {
                            break;
                        }

                        bool progressed = false;
                        if (CountFor(grp.Id, date) < maxPerDay && await TryTargetedRepairAnyGapAsync())
                        {
                            progressed = true;
                        }
                        if (CountFor(grp.Id, date) < maxPerDay && await TryExhaustiveGapFillAsync())
                        {
                            progressed = true;
                        }
                        if (TryShiftGaps(date))
                        {
                            progressed = true;
                        }
                        if (CountFor(grp.Id, date) < maxPerDay && await TryExhaustiveGapFillAsync())
                        {
                            progressed = true;
                        }
                        for (var lectureCycle = 0; lectureCycle < slots.Count; lectureCycle++)
                        {
                            if (!TryRepairLectureOrder())
                            {
                                break;
                            }
                            progressed = true;
                        }

                        var afterGaps = CountDayGaps();
                        if (afterGaps < bestGapCount)
                        {
                            bestGapCount = afterGaps;
                            improved = true;
                        }
                        if (!progressed || afterGaps >= beforeGaps)
                        {
                            break;
                        }
                    }

                    if (improved)
                    {
                        warnings.Add($"[{date:yyyy-MM-dd}] {grp.Name}: фінальний repair-pass зменшив кількість порожніх слотів з {initialGaps} до {bestGapCount}.");
                    }
                    return improved;
                }
                // Короткий цикл ущільнення: зсув прогалин і повторне дозаповнення.
                async Task<bool> RunGapCompactionCycleAsync()
                {
                    bool progressed = false;
                    if (CountFor(grp.Id, date) < maxPerDay && await TryExhaustiveGapFillAsync())
                    {
                        progressed = true;
                    }
                    if (TryShiftGaps(date))
                    {
                        progressed = true;
                    }
                    if (CountFor(grp.Id, date) < maxPerDay && await TryExhaustiveGapFillAsync())
                    {
                        progressed = true;
                    }
                    if (CountFor(grp.Id, date) < maxPerDay
                        && await TryTargetedRepairAnyGapAsync())
                    {
                        progressed = true;
                    }
                    return progressed;
                }

                IReadOnlyList<int> OrderModulesForDayPass(IEnumerable<int> moduleIds, int passMode, bool deferCatchUpModules)
                {
                    var distinctModuleIds = moduleIds.Distinct().ToList();
                    var pressureOrdered = OrderModulesByCurrentPressure(distinctModuleIds, deferCatchUpModules).ToList();
                    if (passMode == 1)
                    {
                        var moduleBudgetCache = new Dictionary<(TimeOnly Start, TimeOnly End, int ModuleId, bool BypassDistinctLimit), int>();
                        return pressureOrdered
                            .Select((moduleId, index) => new
                            {
                                ModuleId = moduleId,
                                Index = index,
                                Scarcity = MeasureModuleScarcity(
                                    moduleId,
                                    bypassDistinctLimit: softFill,
                                    maxModuleSegmentsAllowed: softFill ? 2 : maxModuleSegmentsPerDay,
                                    moduleBudgetCache)
                            })
                            .OrderBy(entry => entry.Scarcity.Score)
                            .ThenBy(entry => entry.Scarcity.ViableSlots == 0 ? int.MaxValue : entry.Scarcity.ViableSlots)
                            .ThenByDescending(entry => PlacementRemainingFor(grp.Id, entry.ModuleId))
                            .ThenBy(entry => entry.Index)
                            .Select(entry => entry.ModuleId)
                            .ToList();
                    }
                    if (passMode == 2)
                    {
                        return pressureOrdered
                            .OrderByDescending(moduleId => PlacementRemainingFor(grp.Id, moduleId))
                            .ThenBy(moduleId => CountModuleForDay(grp.Id, date, moduleId))
                            .ThenBy(moduleId => moduleId)
                            .ToList();
                    }
                    if (passMode == 3)
                    {
                        return pressureOrdered
                            .OrderBy(moduleId => CountModuleForDay(grp.Id, date, moduleId) > 0 ? 1 : 0)
                            .ThenByDescending(moduleId => PlacementRemainingFor(grp.Id, moduleId))
                            .ThenBy(moduleId => ModuleHasPendingSharedLectureCatchUp(moduleId) ? 1 : 0)
                            .ThenBy(moduleId => moduleId)
                            .ToList();
                    }
                    return pressureOrdered;
                }

                void RestoreFirstMainMarker()
                {
                    if (!forceFirstMainModule)
                    {
                        firstMainPlaced = true;
                        firstMainDate = null;
                        firstMainStart = null;
                        return;
                    }

                    var existingFirstMain = busy
                        .Where(b => b.GroupId == grp.Id
                                    && b.ModuleId == firstMainModuleId
                                    && !excludedTypeIds.Contains(b.LessonTypeId))
                        .OrderBy(b => b.Date)
                        .ThenBy(b => b.StartTime)
                        .FirstOrDefault();
                    firstMainPlaced = existingFirstMain is not null;
                    firstMainDate = existingFirstMain?.Date;
                    firstMainStart = existingFirstMain?.StartTime;
                }

                void UndoCreatedDraft(TeacherDraftItem item)
                {
                    var busySlot = FindBusySlotForDraft(item, item.StartTime, item.EndTime);
                    if (busySlot is not null)
                    {
                        RemoveBusySlot(busySlot);
                    }

                    RemoveDraftEntity(item);
                    allCreatedDrafts.Remove(item);
                    movableDrafts.Remove(item);
                    createdDrafts.Remove(item);
                    if (!excludedTypeIds.Contains(item.LessonTypeId))
                    {
                        Dec(item.GroupId, item.Date);
                        RemoveCurrentRangeFact(item.GroupId, item.ModuleId);
                        var remainingKey = (item.GroupId, item.ModuleId);
                        if (remainingByGroupModule.ContainsKey(remainingKey))
                        {
                            remainingByGroupModule[remainingKey]++;
                        }
                        if (item.IsSelfStudy)
                        {
                            selfStudyRemainingByGroupModule[remainingKey] = SelfStudyRemaining(item.GroupId, item.ModuleId) + 1;
                            if (item.ModuleTopicId is int selfStudyTopicId)
                            {
                                var topicKey = (item.GroupId, item.ModuleId, selfStudyTopicId);
                                selfStudyTopicRemaining[topicKey] = selfStudyTopicRemaining.TryGetValue(topicKey, out var topicLeft)
                                    ? topicLeft + 1
                                    : 1;
                            }
                        }
                        else if (item.ModuleTopicId is int topicId)
                        {
                            UnmarkTopicUsed(item.GroupId, item.ModuleId, topicId);
                        }
                    }

                    if (item.RoomId is int roomId && groupRoomUsage.TryGetValue(roomId, out var usedRoomCount))
                    {
                        if (usedRoomCount <= 1)
                        {
                            groupRoomUsage.Remove(roomId);
                        }
                        else
                        {
                            groupRoomUsage[roomId] = usedRoomCount - 1;
                        }
                    }

                    if (preferredFirstTypeId != 0 && item.LessonTypeId == preferredFirstTypeId)
                    {
                        if (!BusyForGroup(item.GroupId).Any(slot =>
                                slot.ModuleId == item.ModuleId
                                && slot.LessonTypeId == preferredFirstTypeId
                                && !excludedTypeIds.Contains(slot.LessonTypeId)))
                        {
                            hasPreferred.Remove((item.GroupId, item.ModuleId));
                        }
                    }

                    RestoreFirstMainMarker();
                    InvalidateGapResourceCaches();
                }

                HashSet<(int GroupId, DateOnly Date, TimeOnly Start, TimeOnly End)> CopyGapWarnings()
                    => gapWarnings.ToHashSet();

                Dictionary<(int GroupId, DateOnly Date, TimeOnly Start, TimeOnly End), HashSet<string>> CopySlotFailureReasons()
                    => slotFailureReasons.ToDictionary(entry => entry.Key, entry => entry.Value.ToHashSet());

                int CountModuleTransitionsForDay()
                {
                    var dayModules = BusyForGroupDate(grp.Id, date)
                        .Where(slot => !excludedTypeIds.Contains(slot.LessonTypeId))
                        .OrderBy(slot => slot.StartTime)
                        .ThenBy(slot => slot.EndTime)
                        .Select(slot => slot.ModuleId)
                        .ToList();
                    var transitions = 0;
                    for (var i = 1; i < dayModules.Count; i++)
                    {
                        if (dayModules[i] != dayModules[i - 1])
                        {
                            transitions++;
                        }
                    }
                    return transitions;
                }

                double ScoreCurrentDay()
                {
                    var filledSlots = CountFor(grp.Id, date);
                    var gapCount = CountDayGaps();
                    var remainingNeed = remainingByGroupModule
                        .Where(entry => entry.Key.GroupId == grp.Id)
                        .Sum(entry => Math.Max(0, entry.Value));
                    var moduleSegments = BusyForGroupDate(grp.Id, date)
                        .Where(slot => !excludedTypeIds.Contains(slot.LessonTypeId))
                        .GroupBy(slot => slot.ModuleId)
                        .Sum(group =>
                        {
                            var indexes = group
                                .Select(slot => slotIndexByTime.TryGetValue((slot.StartTime, slot.EndTime), out var idx) ? idx : -1)
                                .Where(idx => idx >= 0)
                                .Distinct()
                                .OrderBy(idx => idx)
                                .ToList();
                            return CountModuleSegments(indexes);
                        });
                    var transitions = CountModuleTransitionsForDay();
                    var distinctShortfall = Math.Max(0, targetMinDistinctModulesPerDay - CountDistinctModulesForDay(grp.Id, date));
                    var pendingSharedCatchUpNeed = orderedModulesForDay
                        .Where(moduleId => PlacementRemainingFor(grp.Id, moduleId) > 0)
                        .Where(ModuleHasPendingSharedLectureCatchUp)
                        .Sum(moduleId => Math.Max(1, PlacementRemainingFor(grp.Id, moduleId)));
                    return filledSlots * 10000
                           - gapCount * 5000
                           - remainingNeed * 260
                           - pendingSharedCatchUpNeed * 1300
                           - distinctShortfall * 900
                           - Math.Max(0, moduleSegments - CountDistinctModulesForDay(grp.Id, date)) * 180
                           - transitions * 40;
                }

                void RestoreTrialState(
                    HashSet<TeacherDraftItem> createdSnapshot,
                    int warningsCount,
                    int gapDetailsCount,
                    HashSet<(int GroupId, DateOnly Date, TimeOnly Start, TimeOnly End)> gapWarningSnapshot,
                    Dictionary<(int GroupId, DateOnly Date, TimeOnly Start, TimeOnly End), HashSet<string>> slotFailureSnapshot,
                    HashSet<int> attemptedModulesSnapshot,
                    int createdSnapshotCount,
                    int skippedSnapshotCount,
                    int incompleteDraftsSnapshot,
                    int incompleteMissingTeacherSnapshot,
                    int incompleteMissingRoomSnapshot,
                    int incompleteMissingBothSnapshot,
                    int emergencySingletonSnapshot,
                    int? lastPrimarySnapshot)
                {
                    foreach (var item in allCreatedDrafts
                                 .Where(item => !createdSnapshot.Contains(item) && !item.IsLocked)
                                 .ToList())
                    {
                        UndoCreatedDraft(item);
                    }

                    while (warnings.Count > warningsCount)
                    {
                        warnings.RemoveAt(warnings.Count - 1);
                    }
                    while (gapDetails.Count > gapDetailsCount)
                    {
                        gapDetails.RemoveAt(gapDetails.Count - 1);
                    }
                    gapWarnings.Clear();
                    foreach (var warningKey in gapWarningSnapshot)
                    {
                        gapWarnings.Add(warningKey);
                    }
                    slotFailureReasons.Clear();
                    foreach (var entry in slotFailureSnapshot)
                    {
                        slotFailureReasons[entry.Key] = entry.Value.ToHashSet();
                    }
                    modulesAttemptedToday.Clear();
                    foreach (var moduleId in attemptedModulesSnapshot)
                    {
                        modulesAttemptedToday.Add(moduleId);
                    }
                    created = createdSnapshotCount;
                    skipped = skippedSnapshotCount;
                    incompleteDraftsCreated = incompleteDraftsSnapshot;
                    incompleteMissingTeacherCount = incompleteMissingTeacherSnapshot;
                    incompleteMissingRoomCount = incompleteMissingRoomSnapshot;
                    incompleteMissingBothCount = incompleteMissingBothSnapshot;
                    emergencySingletonSharedLecturesCreated = emergencySingletonSnapshot;
                    lastPrimaryModuleId = lastPrimarySnapshot;
                    RestoreFirstMainMarker();
                    InvalidateGapResourceCaches();
                }

                async Task RunDayGenerationPassAsync(int passMode)
                {
                    // Основний модуль дня (пріоритетний у логіці курсу).
                    var primaryModuleId = ResolvePrimaryModule(moduleIds =>
                        OrderModulesForDayPass(moduleIds, passMode, deferCatchUpModules: false));
                    bool placedPrimary = false;
                    if (primaryModuleId.HasValue)
                    {
                        modulesAttemptedToday.Add(primaryModuleId.Value);
                        placedPrimary = await TryPlaceModuleAsync(
                            primaryModuleId.Value,
                            date,
                            isPrimary: true,
                            allowRepeatPreviousDay: softFill,
                            allowExtraSameDay: softFill,
                            relaxed: softFill,
                            preferEarliestSlot: true);
                        if (placedPrimary
                            && CountFor(grp.Id, date) < maxPerDay
                            && CountDistinctModulesForDay(grp.Id, date) < targetMinDistinctModulesPerDay)
                        {
                            await TryPlaceDistinctModulesUntilAsync(targetMinDistinctModulesPerDay, primaryModuleId.Value);
                        }
                        if (placedPrimary
                            && RemainingFor(grp.Id, primaryModuleId.Value) > 0
                            && CountFor(grp.Id, date) < maxPerDay)
                        {
                            var preGapReservationBudget = EstimatePreGapReservationBudget();
                            await TryPlaceModuleAsync(
                                primaryModuleId.Value,
                                date,
                                isPrimary: true,
                                allowRepeatPreviousDay: softFill,
                                allowExtraSameDay: softFill,
                                relaxed: softFill,
                                forcedGapVariantBudget: preGapReservationBudget);
                        }
                    }

                    // Черга filler-модулів, відсортована за залишками.
                    Queue<int> BuildFillerQueueForDay()
                    {
                        if (fillerModulesOrdered.Count == 0)
                            return new Queue<int>();
                        var needsMoreDistinctModules = CountDistinctModulesForDay(grp.Id, date) < targetMinDistinctModulesPerDay;
                        var pressureOrdered = OrderModulesForDayPass(fillerModulesOrdered, passMode, deferCatchUpModules: true).ToList();
                        var pressureRank = pressureOrdered
                            .Select((mid, index) => new { mid, index })
                            .ToDictionary(x => x.mid, x => x.index);
                        int PressureRank(int moduleId)
                            => pressureRank.TryGetValue(moduleId, out var rank) ? rank : int.MaxValue;
                        var ordered = fillerModulesOrdered
                            .OrderBy(mid => needsMoreDistinctModules && CountModuleForDay(grp.Id, date, mid) > 0 ? 1 : 0)
                            .ThenBy(PressureRank)
                            .ThenByDescending(mid => RemainingFor(grp.Id, mid))
                            .ThenBy(mid => mid)
                            .ToList();
                        return new Queue<int>(ordered);
                    }

                    // Заповнюємо день filler-модулями, якщо залишився простір.
                    if (fillerModulesOrdered.Count > 0)
                    {
                        var fillerQueue = BuildFillerQueueForDay();
                        int fillerAttempts = 0;
                        while (CountFor(grp.Id, date) < maxPerDay)
                        {
                            if (fillerQueue.Count == 0) break;
                            var fillerModuleId = fillerQueue.Dequeue();
                            if (RemainingFor(grp.Id, fillerModuleId) <= 0)
                            {
                                fillerAttempts++;
                                if (fillerAttempts >= fillerModulesOrdered.Count)
                                {
                                    break;
                                }
                                if (fillerQueue.Count == 0)
                                {
                                    fillerQueue = BuildFillerQueueForDay();
                                }
                                continue;
                            }
                            if (!CanIntroduceModuleToday(fillerModuleId))
                            {
                                fillerAttempts++;
                                if (fillerAttempts >= fillerModulesOrdered.Count)
                                {
                                    break;
                                }
                                if (fillerQueue.Count == 0)
                                {
                                    fillerQueue = BuildFillerQueueForDay();
                                }
                                continue;
                            }
                            modulesAttemptedToday.Add(fillerModuleId);
                            var preGapReservationBudget = EstimatePreGapReservationBudget();
                            var placedFiller = await TryPlaceModuleAsync(
                                fillerModuleId,
                                date,
                                isPrimary: false,
                                allowRepeatPreviousDay: softFill,
                                allowExtraSameDay: softFill,
                                relaxed: softFill,
                                forcedGapVariantBudget: preGapReservationBudget);
                            if (!placedFiller)
                            {
                                fillerAttempts++;
                                if (fillerAttempts >= fillerModulesOrdered.Count)
                                {
                                    break;
                                }
                                if (fillerQueue.Count == 0)
                                {
                                    fillerQueue = BuildFillerQueueForDay();
                                }
                                continue;
                            }
                            fillerAttempts = 0;
                            if (fillerQueue.Count == 0 && CountFor(grp.Id, date) < maxPerDay)
                            {
                                fillerQueue = BuildFillerQueueForDay();
                            }
                        }
                    }

                    // Додаткові проходи для заповнення (softFill дозволяє більше повторів).
                    if (softFill)
                    {
                        if (CountFor(grp.Id, date) < maxPerDay)
                        {
                            await FillWithRemainingModulesAsync(allowRepeatPreviousDay: true, allowExtraSameDay: true, relaxed: true);
                        }
                    }
                    else
                    {
                        if (CountFor(grp.Id, date) < maxPerDay)
                        {
                            await FillWithRemainingModulesAsync();
                        }
                        if (CountFor(grp.Id, date) < maxPerDay)
                        {
                            await FillWithRemainingModulesAsync(allowRepeatPreviousDay: false, allowExtraSameDay: true);
                        }
                        if (CountFor(grp.Id, date) < maxPerDay)
                        {
                            await FillWithRemainingModulesAsync(allowRepeatPreviousDay: true, allowExtraSameDay: true, relaxed: true);
                        }
                    }
                    if (CountFor(grp.Id, date) < maxPerDay)
                    {
                        await TryExhaustiveGapFillAsync();
                    }
                    // Після заповнення пробуємо вирівняти прогалини.
                    var actualDistinctModulesForDay = CountDistinctModulesForDay(grp.Id, date);
                    if (CountFor(grp.Id, date) > 0 && actualDistinctModulesForDay < targetMinDistinctModulesPerDay)
                    {
                        warnings.Add($"[{date:yyyy-MM-dd}] {grp.Name}: вдалося розмістити {actualDistinctModulesForDay} різн. модулі(в) із цільових {targetMinDistinctModulesPerDay}; перевірте доступність викладачів, аудиторій та залишки годин інших модулів.");
                    }
                    var maxGapCompactionCycles = 3;
                    for (var cycle = 0; cycle < maxGapCompactionCycles; cycle++)
                    {
                        var progressed = await RunGapCompactionCycleAsync();
                        if (!progressed || CountFor(grp.Id, date) >= maxPerDay || !DayHasGaps(date, out _))
                        {
                            break;
                        }
                    }
                    for (var repairCycle = 0; repairCycle < slots.Count; repairCycle++)
                    {
                        if (!TryRepairLectureOrder())
                        {
                            break;
                        }
                    }
                    await RunFinalGapOptimizationAsync();
                }

                var trialCreatedSnapshot = allCreatedDrafts.ToHashSet();
                var trialWarningsCount = warnings.Count;
                var trialGapDetailsCount = gapDetails.Count;
                var trialGapWarnings = CopyGapWarnings();
                var trialSlotFailureReasons = CopySlotFailureReasons();
                var trialAttemptedModules = modulesAttemptedToday.ToHashSet();
                var trialCreatedCount = created;
                var trialSkippedCount = skipped;
                var trialIncompleteDrafts = incompleteDraftsCreated;
                var trialIncompleteMissingTeacher = incompleteMissingTeacherCount;
                var trialIncompleteMissingRoom = incompleteMissingRoomCount;
                var trialIncompleteMissingBoth = incompleteMissingBothCount;
                var trialEmergencySingleton = emergencySingletonSharedLecturesCreated;
                var trialLastPrimary = lastPrimaryModuleId;

                var enableDayRollbackOptimizer = softFill || hasModuleHourOverrides;
                var passModes = enableDayRollbackOptimizer
                    ? new[] { 0, 1, 2, 3 }
                    : new[] { 0 };
                var bestMode = 0;
                var bestScore = double.NegativeInfinity;
                var bestGapCount = int.MaxValue;
                var bestFilledSlots = -1;
                var bestRemainingNeed = int.MaxValue;
                foreach (var passMode in passModes)
                {
                    await RunDayGenerationPassAsync(passMode);
                    var score = ScoreCurrentDay();
                    var gapCount = CountDayGaps();
                    var filledSlots = CountFor(grp.Id, date);
                    var remainingNeed = remainingByGroupModule
                        .Where(entry => entry.Key.GroupId == grp.Id)
                        .Sum(entry => Math.Max(0, entry.Value));
                    var isBetterPass = gapCount < bestGapCount
                                       || (gapCount == bestGapCount && filledSlots > bestFilledSlots)
                                       || (gapCount == bestGapCount && filledSlots == bestFilledSlots && remainingNeed < bestRemainingNeed)
                                       || (gapCount == bestGapCount && filledSlots == bestFilledSlots && remainingNeed == bestRemainingNeed && score > bestScore);
                    if (isBetterPass)
                    {
                        bestScore = score;
                        bestMode = passMode;
                        bestGapCount = gapCount;
                        bestFilledSlots = filledSlots;
                        bestRemainingNeed = remainingNeed;
                    }
                    RestoreTrialState(
                        trialCreatedSnapshot,
                        trialWarningsCount,
                        trialGapDetailsCount,
                        trialGapWarnings,
                        trialSlotFailureReasons,
                        trialAttemptedModules,
                        trialCreatedCount,
                        trialSkippedCount,
                        trialIncompleteDrafts,
                        trialIncompleteMissingTeacher,
                        trialIncompleteMissingRoom,
                        trialIncompleteMissingBoth,
                        trialEmergencySingleton,
                        trialLastPrimary);
                    if (bestGapCount == 0 && bestScore > 0)
                    {
                        break;
                    }
                }

                await RunDayGenerationPassAsync(bestMode);
                if (bestMode != 0)
                {
                    warnings.Add($"[{date:yyyy-MM-dd}] {grp.Name}: оптимізатор перебудував день зі стратегією #{bestMode}, щоб зменшити прогалини та дефіцит модулів.");
                }
                WarnRemainingGaps(date);
            }
        }
        int RunFinalLectureOrderCleanup()
        {
            var removedCount = 0;
            var safetyBudget = Math.Max(1, movableDrafts.Count);
            while (safetyBudget-- > 0)
            {
                TeacherDraftItem? conflict = null;
                foreach (var dayGroup in movableDrafts
                             .Where(draft => draft.Status == DraftStatus.Draft
                                             && !draft.IsLocked
                                             && selectedGroupIdSet.Contains(draft.GroupId)
                                             && draft.Date >= rangeStartDate
                                             && draft.Date < rangeEndDateExclusive)
                             .GroupBy(draft => new { draft.GroupId, draft.Date }))
                {
                    var latestLectureStart = BusyForGroupDate(dayGroup.Key.GroupId, dayGroup.Key.Date)
                        .Where(slot => CanShareAcrossGroups(slot.LessonTypeId)
                                       && !excludedTypeIds.Contains(slot.LessonTypeId))
                        .Select(slot => (TimeOnly?)slot.StartTime)
                        .OrderByDescending(start => start)
                        .FirstOrDefault();
                    if (latestLectureStart is null)
                    {
                        continue;
                    }

                    conflict = dayGroup
                        .Where(draft => draft.StartTime < latestLectureStart.Value
                                        && !CanShareAcrossGroups(draft.LessonTypeId)
                                        && !excludedTypeIds.Contains(draft.LessonTypeId))
                        .OrderBy(draft => draft.StartTime)
                        .FirstOrDefault();
                    if (conflict is not null)
                    {
                        break;
                    }
                }

                if (conflict is null)
                {
                    break;
                }

                var removedStart = conflict.StartTime;
                var removedEnd = conflict.EndTime;
                var removedBusySlot = FindBusySlotForDraft(conflict, removedStart, removedEnd);
                if (removedBusySlot is not null)
                {
                    RemoveBusySlot(removedBusySlot);
                }
                movableDrafts.Remove(conflict);
                if (allCreatedDrafts.Remove(conflict))
                {
                    if (created > 0)
                    {
                        created--;
                    }
                    Dec(conflict.GroupId, conflict.Date);
                }
                RemoveDraftEntity(conflict);
                removedCount++;
                var groupLabel = selectedGroupsById.TryGetValue(conflict.GroupId, out var conflictGroup)
                    ? conflictGroup.Name
                    : $"#{conflict.GroupId}";
                warnings.Add($"[{conflict.Date:yyyy-MM-dd}] {groupLabel}: фінальний лекційний cleanup прибрав заняття {conflict.ModuleId} зі слоту {removedStart:HH\\:mm}-{removedEnd:HH\\:mm}, бо воно лишалося перед лекційним блоком.");
            }

            return removedCount;
        }
        int RunFinalResourceConflictCleanup()
        {
            var removedCount = 0;
            var safetyBudget = Math.Max(1, allCreatedDrafts.Count);
            while (safetyBudget-- > 0)
            {
                TeacherDraftItem? conflict = null;
                string conflictReason = string.Empty;
                foreach (var draft in allCreatedDrafts
                             .Where(draft => draft.Status == DraftStatus.Draft && !draft.IsLocked)
                             .OrderBy(draft => draft.Date)
                             .ThenBy(draft => draft.StartTime)
                             .ThenBy(draft => draft.GroupId)
                             .ToList())
                {
                    bool SameDraftBusySlot(BusySlot slot)
                        => SlotMatches(
                            slot,
                            draft.GroupId,
                            draft.Date,
                            draft.StartTime,
                            draft.EndTime,
                            draft.ModuleId,
                            draft.TeacherId,
                            draft.RoomId,
                            draft.ModuleTopicId);

                    bool SameShareableBusySlot(BusySlot slot)
                        => IsSameShareableBusySlot(
                            slot,
                            draft.ModuleId,
                            draft.LessonTypeId,
                            draft.ModuleTopicId,
                            draft.TeacherId,
                            draft.RoomId,
                            draft.Date,
                            draft.StartTime,
                            draft.EndTime);

                    var slotLabel = $"{draft.Date:yyyy-MM-dd} {draft.StartTime:HH\\:mm}-{draft.EndTime:HH\\:mm}";
                    if (BusyForGroupDate(draft.GroupId, draft.Date).Any(slot =>
                            SlotOverlaps(slot, draft.Date, draft.StartTime, draft.EndTime)
                            && !SameDraftBusySlot(slot)))
                    {
                        conflict = draft;
                        conflictReason = $"Група {BusyGroupLabel(draft.GroupId)} вже зайнята у слоті {slotLabel}.";
                        break;
                    }

                    if (draft.TeacherId is int teacherId && BlocksTeacherSlot(draft.LessonTypeId))
                    {
                        var teacherBusy = BusyForTeacherDate(teacherId, draft.Date).Any(slot =>
                            SlotOverlaps(slot, draft.Date, draft.StartTime, draft.EndTime)
                            && BlocksTeacherSlot(slot.LessonTypeId)
                            && !SameDraftBusySlot(slot)
                            && !SameShareableBusySlot(slot));
                        if (teacherBusy)
                        {
                            conflict = draft;
                            conflictReason = $"Викладач {BusyTeacherLabel(teacherId)} вже зайнятий у слоті {slotLabel}.";
                            break;
                        }
                    }

                    if (draft.RoomId is int roomId && BlocksRoomSlot(draft.LessonTypeId))
                    {
                        var roomBusy = BusyForRoomDate(roomId, draft.Date).Any(slot =>
                            SlotOverlaps(slot, draft.Date, draft.StartTime, draft.EndTime)
                            && BlocksRoomSlot(slot.LessonTypeId)
                            && !SameDraftBusySlot(slot)
                            && !SameShareableBusySlot(slot));
                        if (roomBusy)
                        {
                            conflict = draft;
                            conflictReason = $"Аудиторія #{roomId} вже зайнята у слоті {slotLabel}.";
                            break;
                        }

                        var room = roomsAll.FirstOrDefault(item => item.Id == roomId);
                        if (room is not null)
                        {
                            var roomSlotGroupIds = BusyForRoomDate(roomId, draft.Date)
                                .Where(slot => slot.StartTime == draft.StartTime
                                               && slot.EndTime == draft.EndTime
                                               && BlocksRoomSlot(slot.LessonTypeId))
                                .Select(slot => slot.GroupId)
                                .Distinct()
                                .ToList();
                            var totalStudents = roomSlotGroupIds.Sum(groupId =>
                                selectedGroupsById.TryGetValue(groupId, out var group) ? group.StudentsCount : 0);
                            if (totalStudents > room.Capacity)
                            {
                                conflict = draft;
                                conflictReason = $"Аудиторія {BusyRoomLabel(room)} має {room.Capacity} місць для {totalStudents} слухачів у слоті {slotLabel}.";
                                break;
                            }
                        }
                    }
                }

                if (conflict is null)
                {
                    break;
                }

                var removedBusySlot = FindBusySlotForDraft(conflict, conflict.StartTime, conflict.EndTime);
                if (removedBusySlot is not null)
                {
                    RemoveBusySlot(removedBusySlot);
                }

                movableDrafts.Remove(conflict);
                if (allCreatedDrafts.Remove(conflict))
                {
                    if (created > 0)
                    {
                        created--;
                    }
                    Dec(conflict.GroupId, conflict.Date);
                }
                RemoveDraftEntity(conflict);
                removedCount++;
                var removedGroupName = selectedGroupsById.TryGetValue(conflict.GroupId, out var removedGroup)
                    ? removedGroup.Name
                    : $"#{conflict.GroupId}";
                var removedReason = string.IsNullOrWhiteSpace(conflictReason)
                    ? "Фінальна перевірка ресурсів відхилила небезпечний варіант."
                    : conflictReason;
                warnings.Add($"[{conflict.Date:yyyy-MM-dd} {conflict.StartTime:HH\\:mm}-{conflict.EndTime:HH\\:mm}] {removedGroupName}: чернетку прибрано перед збереженням. {removedReason}");
                gapDetails.Add(new AutoGenGapDetail(
                    GroupId: conflict.GroupId,
                    GroupName: removedGroupName,
                    Date: conflict.Date,
                    Start: conflict.StartTime,
                    End: conflict.EndTime,
                    SlotLabel: $"{conflict.StartTime:HH\\:mm}-{conflict.EndTime:HH\\:mm}",
                    Reason: removedReason,
                    ModuleId: conflict.ModuleId,
                    ModuleName: ModuleTitleLabel(conflict.ModuleId)));
            }

            return removedCount;
        }
        RunFinalLectureOrderCleanup();
        var removedUnsafeDrafts = RunFinalResourceConflictCleanup();
        if (removedUnsafeDrafts > 0)
        {
            warnings.Add($"Фінальна перевірка ресурсів прибрала {removedUnsafeDrafts} небезпечні чернетки перед збереженням.");
        }
        var modifiedDraftsForValidation = _db.ChangeTracker
            .Entries<TeacherDraftItem>()
            .Where(entry => entry.State == EntityState.Modified)
            .Select(entry => entry.Entity)
            .Where(draft => draft.Status == DraftStatus.Draft
                            && draft.Date >= rangeStartDate
                            && draft.Date < rangeEndDateExclusive)
            .ToList();
        var modifiedDraftIdsForValidation = modifiedDraftsForValidation
            .Where(draft => draft.Id > 0)
            .Select(draft => draft.Id)
            .Distinct()
            .ToList();
        var changedDraftsForValidation = new List<TeacherDraftItem>();
        var changedDraftIdsForValidation = new HashSet<int>();
        void AddChangedDraftForValidation(TeacherDraftItem draft)
        {
            if (draft.Id > 0)
            {
                if (changedDraftIdsForValidation.Add(draft.Id))
                {
                    changedDraftsForValidation.Add(draft);
                }
                return;
            }

            if (changedDraftsForValidation.All(existing => !ReferenceEquals(existing, draft)))
            {
                changedDraftsForValidation.Add(draft);
            }
        }
        foreach (var draft in allCreatedDrafts)
        {
            AddChangedDraftForValidation(draft);
        }
        foreach (var draft in modifiedDraftsForValidation)
        {
            AddChangedDraftForValidation(draft);
        }
        async Task<List<string>> ValidateGeneratedDraftsAsync()
        {
            var errors = new List<string>();
            if (changedDraftsForValidation.Count == 0)
            {
                return errors;
            }

            static bool Overlaps(TimeOnly leftStart, TimeOnly leftEnd, TimeOnly rightStart, TimeOnly rightEnd)
                => leftStart < rightEnd && rightStart < leftEnd;

            static bool SlotRangeAllowed(TimeOnly start, TimeOnly end, List<(TimeOnly Start, TimeOnly End)> daySlots)
            {
                if (daySlots.Count == 0) return true;
                for (var i = 0; i < daySlots.Count; i++)
                {
                    if (daySlots[i].Start != start) continue;
                    for (var j = i; j < daySlots.Count; j++)
                    {
                        if (j > i && daySlots[j - 1].End != daySlots[j].Start) break;
                        if (daySlots[j].End == end) return true;
                    }
                }
                return false;
            }
            bool BlocksTeacher(int lessonTypeId)
                => typeById.TryGetValue(lessonTypeId, out var lessonType) && lessonType.BlocksTeacher;
            bool BlocksRoom(int lessonTypeId)
                => typeById.TryGetValue(lessonTypeId, out var lessonType) && lessonType.BlocksRoom;
            bool SameShareableOccurrence(
                int leftGroupId,
                int leftModuleId,
                int leftLessonTypeId,
                int? leftModuleTopicId,
                int? leftTeacherId,
                int? leftRoomId,
                DateOnly leftDate,
                TimeOnly leftStart,
                TimeOnly leftEnd,
                int rightGroupId,
                int rightModuleId,
                int rightLessonTypeId,
                int? rightModuleTopicId,
                int? rightTeacherId,
                int? rightRoomId,
                DateOnly rightDate,
                TimeOnly rightStart,
                TimeOnly rightEnd)
            {
                if (leftGroupId == rightGroupId || !CanShareAcrossGroups(leftLessonTypeId))
                {
                    return false;
                }
                return leftDate == rightDate
                       && leftStart == rightStart
                       && leftEnd == rightEnd
                       && leftModuleId == rightModuleId
                       && leftLessonTypeId == rightLessonTypeId
                       && leftModuleTopicId == rightModuleTopicId
                       && leftTeacherId == rightTeacherId
                       && leftRoomId == rightRoomId;
            }

            var slotCache = new Dictionary<(int CourseId, DayOfWeek Day), List<(TimeOnly Start, TimeOnly End)>>();
            async Task<List<(TimeOnly Start, TimeOnly End)>> GetSlotsAsync(int courseIdValue, DayOfWeek day)
            {
                var key = (courseIdValue, day);
                if (!slotCache.TryGetValue(key, out var daySlots))
                {
                    var resolved = await TimeSlotsResolver.ResolveForDayAsync(_db, courseIdValue, day);
                    daySlots = resolved.Slots
                        .Select(s => (s.Start, s.End))
                        .ToList();
                    slotCache[key] = daySlots;
                }
                return daySlots;
            }

            foreach (var draft in changedDraftsForValidation)
            {
                var groupLabel = selectedGroupsById.TryGetValue(draft.GroupId, out var group)
                    ? group.Name
                    : $"#{draft.GroupId}";
                if (draft.Date < rangeStartDate || draft.Date > rangeEndDate)
                {
                    errors.Add($"Фінальна перевірка: чернетка групи {groupLabel} виходить за діапазон генерації ({draft.Date:yyyy-MM-dd}).");
                }
                if (draft.EndTime <= draft.StartTime)
                {
                    errors.Add($"Фінальна перевірка: некоректний час {draft.StartTime:HH\\:mm}-{draft.EndTime:HH\\:mm} для групи {groupLabel}.");
                }
                if (typeById.TryGetValue(draft.LessonTypeId, out var lessonType))
                {
                    if (!allowIncompleteDrafts && lessonType.RequiresTeacher && draft.TeacherId is null)
                    {
                        errors.Add($"Фінальна перевірка: для групи {groupLabel} у слоті {draft.Date:yyyy-MM-dd} {draft.StartTime:HH\\:mm}-{draft.EndTime:HH\\:mm} відсутній викладач.");
                    }
                    if (!allowIncompleteDrafts && lessonType.RequiresRoom && draft.RoomId is null)
                    {
                        errors.Add($"Фінальна перевірка: для групи {groupLabel} у слоті {draft.Date:yyyy-MM-dd} {draft.StartTime:HH\\:mm}-{draft.EndTime:HH\\:mm} відсутня аудиторія.");
                    }
                }
                else
                {
                    errors.Add($"Фінальна перевірка: невідомий тип заняття #{draft.LessonTypeId} для групи {groupLabel}.");
                }
                if (draft.ModuleTopicId is int draftTopicId)
                {
                    if (!topicById.TryGetValue(draftTopicId, out var draftTopic))
                    {
                        errors.Add($"Фінальна перевірка: тема #{draftTopicId} для групи {groupLabel} не знайдена у плані модуля.");
                    }
                    else
                    {
                        if (draftTopic.ModuleId != draft.ModuleId)
                        {
                            errors.Add($"Фінальна перевірка: тема #{draftTopicId} не належить модулю #{draft.ModuleId} для групи {groupLabel}.");
                        }

                        if (!draft.IsSelfStudy && draftTopic.LessonTypeId != draft.LessonTypeId)
                        {
                            errors.Add($"Фінальна перевірка: тип заняття #{draft.LessonTypeId} не відповідає типу теми #{draftTopicId} для групи {groupLabel}.");
                        }
                    }
                }
                else if (!draft.IsSelfStudy && ModuleHasUsableTopics(draft.ModuleId))
                {
                    errors.Add($"Фінальна перевірка: модуль #{draft.ModuleId} має планові теми, але заняття для групи {groupLabel} створено без теми.");
                }
                if (selectedGroupsById.TryGetValue(draft.GroupId, out var selectedGroup))
                {
                    var daySlots = await GetSlotsAsync(selectedGroup.CourseId, draft.DayOfWeek);
                    if (!SlotRangeAllowed(draft.StartTime, draft.EndTime, daySlots))
                    {
                        errors.Add($"Фінальна перевірка: слот {draft.Date:yyyy-MM-dd} {draft.StartTime:HH\\:mm}-{draft.EndTime:HH\\:mm} для групи {groupLabel} не входить у налаштовані слоти.");
                    }
                }
            }

            for (var i = 0; i < changedDraftsForValidation.Count; i++)
            {
                var left = changedDraftsForValidation[i];
                for (var j = i + 1; j < changedDraftsForValidation.Count; j++)
                {
                    var right = changedDraftsForValidation[j];
                    if (left.Date != right.Date || !Overlaps(left.StartTime, left.EndTime, right.StartTime, right.EndTime))
                    {
                        continue;
                    }
                    var label = $"{left.Date:yyyy-MM-dd} {left.StartTime:HH\\:mm}-{left.EndTime:HH\\:mm}";
                    if (left.GroupId == right.GroupId)
                    {
                        errors.Add($"Фінальна перевірка: група #{left.GroupId} має перетин чернеток у слоті {label}.");
                    }
                    var sameShareableOccurrence = SameShareableOccurrence(
                        left.GroupId,
                        left.ModuleId,
                        left.LessonTypeId,
                        left.ModuleTopicId,
                        left.TeacherId,
                        left.RoomId,
                        left.Date,
                        left.StartTime,
                        left.EndTime,
                        right.GroupId,
                        right.ModuleId,
                        right.LessonTypeId,
                        right.ModuleTopicId,
                        right.TeacherId,
                        right.RoomId,
                        right.Date,
                        right.StartTime,
                        right.EndTime);
                    if (left.TeacherId is int leftTeacher
                        && right.TeacherId == leftTeacher
                        && BlocksTeacher(left.LessonTypeId)
                        && BlocksTeacher(right.LessonTypeId)
                        && !sameShareableOccurrence)
                    {
                        errors.Add($"Фінальна перевірка: викладач #{leftTeacher} має перетин чернеток у слоті {label}.");
                    }
                    if (left.RoomId is int leftRoom
                        && right.RoomId == leftRoom
                        && BlocksRoom(left.LessonTypeId)
                        && BlocksRoom(right.LessonTypeId)
                        && !sameShareableOccurrence)
                    {
                        errors.Add($"Фінальна перевірка: аудиторія #{leftRoom} має перетин чернеток у слоті {label}.");
                    }
                }
            }

            var groupIdsForValidation = changedDraftsForValidation.Select(d => d.GroupId).Distinct().ToList();
            var teacherIdsForValidation = changedDraftsForValidation.Where(d => d.TeacherId != null).Select(d => d.TeacherId!.Value).Distinct().ToList();
            var roomIdsForValidation = changedDraftsForValidation.Where(d => d.RoomId != null).Select(d => d.RoomId!.Value).Distinct().ToList();

            var existingDrafts = await _db.TeacherDraftItems.AsNoTracking()
                .Where(d => d.Date >= rangeStartDate
                            && d.Date < rangeEndDateExclusive
                            && !modifiedDraftIdsForValidation.Contains(d.Id)
                            && (groupIdsForValidation.Contains(d.GroupId)
                                || (d.TeacherId != null && teacherIdsForValidation.Contains(d.TeacherId.Value))
                                || (d.RoomId != null && roomIdsForValidation.Contains(d.RoomId.Value))))
                .Select(d => new { d.Date, d.StartTime, d.EndTime, d.GroupId, d.ModuleId, d.ModuleTopicId, d.TeacherId, d.RoomId, d.LessonTypeId })
                .ToListAsync();
            var existingSchedule = await _db.ScheduleItems.AsNoTracking()
                .Where(d => d.Date >= rangeStartDate
                            && d.Date < rangeEndDateExclusive
                            && (groupIdsForValidation.Contains(d.GroupId)
                                || (d.TeacherId != null && teacherIdsForValidation.Contains(d.TeacherId.Value))
                                || (d.RoomId != null && roomIdsForValidation.Contains(d.RoomId.Value))))
                .Select(d => new { d.Date, d.StartTime, d.EndTime, d.GroupId, d.ModuleId, d.ModuleTopicId, d.TeacherId, d.RoomId, d.LessonTypeId })
                .ToListAsync();

            foreach (var draft in changedDraftsForValidation)
            {
                foreach (var existing in existingDrafts.Concat(existingSchedule))
                {
                    if (draft.Date != existing.Date || !Overlaps(draft.StartTime, draft.EndTime, existing.StartTime, existing.EndTime))
                    {
                        continue;
                    }
                    var label = $"{draft.Date:yyyy-MM-dd} {draft.StartTime:HH\\:mm}-{draft.EndTime:HH\\:mm}";
                    if (draft.GroupId == existing.GroupId)
                    {
                        errors.Add($"Фінальна перевірка: група #{draft.GroupId} перетинається з наявним заняттям у слоті {label}.");
                    }
                    var sameShareableOccurrence = SameShareableOccurrence(
                        draft.GroupId,
                        draft.ModuleId,
                        draft.LessonTypeId,
                        draft.ModuleTopicId,
                        draft.TeacherId,
                        draft.RoomId,
                        draft.Date,
                        draft.StartTime,
                        draft.EndTime,
                        existing.GroupId,
                        existing.ModuleId,
                        existing.LessonTypeId,
                        existing.ModuleTopicId,
                        existing.TeacherId,
                        existing.RoomId,
                        existing.Date,
                        existing.StartTime,
                        existing.EndTime);
                    if (!typeById.ContainsKey(existing.LessonTypeId))
                    {
                        errors.Add($"Фінальна перевірка: наявне заняття у слоті {label} має невідомий тип #{existing.LessonTypeId}.");
                    }
                    if (draft.TeacherId is int draftTeacher
                        && existing.TeacherId == draftTeacher
                        && BlocksTeacher(draft.LessonTypeId)
                        && BlocksTeacher(existing.LessonTypeId)
                        && !sameShareableOccurrence)
                    {
                        errors.Add($"Фінальна перевірка: викладач #{draftTeacher} перетинається з наявним заняттям у слоті {label}.");
                    }
                    if (draft.RoomId is int draftRoom
                        && existing.RoomId == draftRoom
                        && BlocksRoom(draft.LessonTypeId)
                        && BlocksRoom(existing.LessonTypeId)
                        && !sameShareableOccurrence)
                    {
                        errors.Add($"Фінальна перевірка: аудиторія #{draftRoom} перетинається з наявним заняттям у слоті {label}.");
                    }
                }
            }

            return errors
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        if (gapDetails.Count > 0)
        {
            var reasonSummary = gapDetails
                .Select(g => string.IsNullOrWhiteSpace(g.Reason) ? "Причину не визначено" : g.Reason!)
                .GroupBy(reason => reason)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(8)
                .Select(g => $"{g.Count()} x {g.Key}");
            warnings.Add($"Зведення причин незаповнених слотів: {string.Join(" | ", reasonSummary)}.");
            warnings.AddRange(BuildAutoGenRepairSuggestions(gapDetails));
        }
        // Зберігаємо створені чернетки та повертаємо результат.
        if (incompleteDraftsCreated > 0)
        {
            warnings.Add(
                $"Створено {incompleteDraftsCreated} неповних чернеток: без викладача — {incompleteMissingTeacherCount}, без аудиторії — {incompleteMissingRoomCount}, без обох призначень — {incompleteMissingBothCount}.");
        }
        var finalValidationErrors = await ValidateGeneratedDraftsAsync();
        var pendingDraftsForSharedValidation = changedDraftsForValidation
            .Select(draft => new TeacherDraftsAutogenPendingDraft(
                draft.Date,
                draft.StartTime,
                draft.EndTime,
                draft.GroupId,
                draft.ModuleId,
                draft.LessonTypeId,
                draft.ModuleTopicId,
                draft.TeacherId,
                draft.RoomId,
                draft.IsSelfStudy))
            .ToList();
        var sharedHardRuleValidator = new TeacherDraftsAutogenHardRuleValidator(_db);
        foreach (var courseGroup in selectedGroupsByCourse)
        {
            var courseGroupIds = courseGroup.Value.Select(group => group.Id).ToList();
            var coursePendingDrafts = pendingDraftsForSharedValidation
                .Where(draft => courseGroupIds.Contains(draft.GroupId))
                .ToList();
            var sharedHardRuleValidation = await sharedHardRuleValidator.ValidateAsync(
                new TeacherDraftsAutogenHardRuleValidationRequest(
                    courseGroup.Key,
                    courseGroupIds,
                    rangeStartDate,
                    rangeEndDate,
                    r.Days,
                    allowIncompleteDrafts,
                    PendingDrafts: coursePendingDrafts,
                    ExcludedDraftIds: modifiedDraftIdsForValidation),
                cancellationToken);
            finalValidationErrors.AddRange(sharedHardRuleValidation.Violations
                .Select(error => $"Фінальна перевірка: {error}"));
        }
        finalValidationErrors = finalValidationErrors
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (finalValidationErrors.Count > 0)
        {
            warnings.AddRange(finalValidationErrors.Take(50));
            if (finalValidationErrors.Count > 50)
            {
                warnings.Add($"Фінальна перевірка знайшла ще {finalValidationErrors.Count - 50} проблем.");
            }
            return BadRequest(new AutoGenResult(created, skipped, warnings, gapDetails, BuildAutoGenGapSummary(gapDetails), preflightItems));
        }
        if (r.PreflightOnly)
        {
            warnings.Add("Пробну генерацію завершено без збереження чернеток.");
            await tx.RollbackAsync(cancellationToken);
            return Ok(new AutoGenResult(0, skipped, warnings, gapDetails, BuildAutoGenGapSummary(gapDetails), preflightItems));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return Ok(new AutoGenResult(created, skipped, warnings, gapDetails, BuildAutoGenGapSummary(gapDetails), preflightItems));
        }
        finally
        {
            GenerationLock.Release();
        }
    }

}
