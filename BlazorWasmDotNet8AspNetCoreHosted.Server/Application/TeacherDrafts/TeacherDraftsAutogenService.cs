using System;
using System.Collections.Generic;
using System.Linq;
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
        int LessonTypeId // Тип заняття (для обмежень).
    );
    private sealed record PlacementCandidate(
        TimeSlot Slot, // Слот розкладу для можливого розміщення.
        int TeacherId, // Обраний викладач.
        Room? Room, // Обрана аудиторія (може бути null).
        int LessonTypeId, // Обраний тип заняття.
        ModuleTopic? Topic, // Обрана тема модуля (може бути null).
        bool IsSelfStudy, // Ознака самостійної роботи.
        IReadOnlyList<int> SharedGroupIds, // Групи, для яких формуємо спільне заняття.
        double Penalty, // Сумарний штраф за правилами.
        List<string> Notes); // Пояснення нарахованих штрафів.
    private sealed record SequenceItem(int CourseId, int ModuleId, int GroupOrder, int Order);
    private sealed record MainModuleGroup(int GroupOrder, List<int> ModuleIds);
    // Уніфіковані відповіді для API.
    private static ActionResult<AutoGenResult> Ok(AutoGenResult value) => new OkObjectResult(value);
    private static ActionResult<AutoGenResult> BadRequest(object value) => new BadRequestObjectResult(value);
    // Викликає автогенерацію чернеток для одного тижня.
    public Task<ActionResult<AutoGenResult>> DraftAutoGenWeek(DraftAutoGenRequest r)
        => DraftAutoGen(r);
    // Автоматично генерує чернетки для кожного тижня в межах місяця.
    public async Task<ActionResult<AutoGenResult>> AutogenMonth(AutogenMonthRequest r)
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
            Days: r.Days
        );
        // Запускаємо автогенерацію для кожного тижня місяця.
        return await RunAutoGenForWeeks(template, EnumerateWeekStarts(monthStart, week => week < nextMonth));
    }
    // Генерує чернетки для курсу в заданому діапазоні тижнів.
    public async Task<ActionResult<AutoGenResult>> AutogenCourse(AutogenCourseRequest r)
    {
        // Підготовка шаблону генерації з фільтрами курсу/групи/викладача.
        var template = new DraftAutoGenRequest(
            WeekStart: r.From,
            ClearExisting: true,
            CourseId: r.CourseId,
            GroupId: r.GroupId,
            TeacherId: r.TeacherId,
            AllowOnDaysOff: r.AllowOnDaysOff,
            Days: r.Days
        );
        // Проганяємо всі тижні в межах діапазону.
        return await RunAutoGenForWeeks(template, EnumerateWeekStarts(r.From, week => week <= r.To));
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
    private async Task<ActionResult<AutoGenResult>> RunAutoGenForWeeks(DraftAutoGenRequest template, IEnumerable<DateOnly> weekStarts)
    {
        int created = 0, skipped = 0;
        var warnings = new List<string>();
        var gapDetails = new List<AutoGenGapDetail>();
        foreach (var weekStart in weekStarts)
        {
            // Генеруємо тиждень окремо, щоб не зупиняти весь процес при часткових збоях.
            var res = await DraftAutoGen(template with { WeekStart = weekStart });
            if (res.Result is not OkObjectResult { Value: AutoGenResult ok }) continue;
            created += ok.Created;
            skipped += ok.Skipped;
            warnings.AddRange(ok.Warnings);
            if (ok.GapDetails is not null)
            {
                gapDetails.AddRange(ok.GapDetails);
            }
        }
        // Підсумовуємо статистику по всіх тижнях.
        return Ok(new AutoGenResult(created, skipped, warnings, gapDetails));
    }
    // Створює чернетки на основі правил і доступних даних для заданого тижня.
    public async Task<ActionResult<AutoGenResult>> DraftAutoGen(DraftAutoGenRequest r)
    {
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
        var weekStart = r.WeekStart;
        var weekEnd = weekStart.AddDays(7);
        var weekEndInclusive = weekEnd.AddDays(-1);
        // Опційно обрізаємо генерацію межами діапазону в межах тижня.
        var rangeStartDate = r.RangeStartDate ?? weekStart;
        var rangeEndDate = r.RangeEndDate ?? weekEndInclusive;
        if (rangeStartDate < weekStart) rangeStartDate = weekStart;
        if (rangeEndDate > weekEndInclusive) rangeEndDate = weekEndInclusive;
        if (rangeEndDate < rangeStartDate)
        {
            return BadRequest(new AutoGenResult(0, 0, new()
            {
                "Невірний діапазон дат автогенерації: дата завершення менша за дату початку."
            }));
        }
        var rangeEndDateExclusive = rangeEndDate.AddDays(1);
        // Режим "м'якого заповнення" дозволяє послаблювати частину правил.
        var softFill = r.SoftFill;
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
        // Витягуємо календарні винятки для тижня (робочі/вихідні).
        var calendar = await _db.CalendarExceptions
            .Where(c => c.Date >= weekStart && c.Date < weekEnd)
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
        int? groupId = (r.GroupId > 0) ? r.GroupId : null;
        // Ручні години по модулях (якщо задані в запиті).
        var moduleHoursByModuleId = r.ModuleHours?
            .Where(kv => kv.Value > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value)
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
        // Завантажуємо групи з урахуванням фільтрів курсу та групи.
        var groups = await _db.Groups
            .Include(x => x.Course)
            .Where(x => courseId == null || x.CourseId == courseId)
            .Where(x => groupId == null || x.Id == groupId)
            .ToListAsync();
        if (groups.Count == 0)
            return Ok(new AutoGenResult(0, 0, new() { "Групи не знайдено." }));
        var selectedGroupsById = groups.ToDictionary(g => g.Id);
        var selectedGroupsByCourse = groups
            .GroupBy(g => g.CourseId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderBy(x => x.StudentsCount)
                    .ThenBy(x => x.Id)
                    .ToList());
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
        // За потреби очищаємо існуючі незаблоковані чернетки тижня.
        if (r.ClearExisting && !softFill)
        {
            var gids = groups.Select(g => g.Id).ToList();
            await _db.TeacherDraftItems
                .Where(x => x.Date >= rangeStartDate && x.Date < rangeEndDateExclusive && gids.Contains(x.GroupId) && !x.IsLocked)
                .ExecuteDeleteAsync();
        }
        // Конфігурації ліміту слота для типу з прапорцем "Бажано першим у тижні" та список аудиторій для підбору.
        var preferredFirstSlotLimitsAll = await _db.PreferredFirstSlotLimitConfigs.AsNoTracking().ToListAsync();
        var roomsAll = await _db.Rooms.AsNoTracking().ToListAsync();
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
        const int maxParallelGroupsPerModuleInSlot = 2;
        var historyStart = weekStart.AddMonths(-historyMonthsForRepeats);
        var lastWeekStart = weekStart.AddDays(-7);
        // Завантажуємо вже зайняті слоти з чернеток і опублікованого розкладу.
        var busyDrafts = await _db.TeacherDraftItems
            .Include(x => x.Room)
            .Where(x => x.Date >= historyStart && x.Date < weekEnd)
            .Select(x => new BusySlot(
                x.GroupId,
                x.TeacherId,
                x.RoomId,
                x.Date,
                x.StartTime,
                x.EndTime,
                x.Room != null ? (int?)x.Room.BuildingId : null,
                x.ModuleId,
                x.LessonTypeId))
            .ToListAsync();
        var busySchedule = await _db.ScheduleItems
            .Include(x => x.Room)
            .Where(x => x.Date >= historyStart && x.Date < weekEnd)
            .Select(x => new BusySlot(
                x.GroupId,
                x.TeacherId,
                x.RoomId,
                x.Date,
                x.StartTime,
                x.EndTime,
                x.Room != null ? (int?)x.Room.BuildingId : null,
                x.ModuleId,
                x.LessonTypeId))
            .ToListAsync();
        var busy = busyDrafts
            .Concat(busySchedule)
            .ToList();
        // Фіксуємо, де вже використовувався пріоритетний тип на тижні.
        var hasPreferred = new HashSet<(int groupId, int moduleId)>(
            busy.Where(b => preferredFirstTypeId != 0 && b.LessonTypeId == preferredFirstTypeId)
                .Select(b => (b.GroupId, b.ModuleId)));
        // Збираємо модулі, що були минулого тижня (для зменшення повторів).
        var lastWeekModulesByGroup = busy
            .Where(b => b.Date >= lastWeekStart
                        && b.Date < weekStart
                        && !excludedTypeIds.Contains(b.LessonTypeId))
            .GroupBy(b => b.GroupId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ModuleId).Distinct().ToHashSet());
        // Лічильник кількості пар на день для кожної групи.
        var perDayCount = new Dictionary<(int groupId, DateOnly date), int>();
        foreach (var b in busy.Where(b => !excludedTypeIds.Contains(b.LessonTypeId)))
        {
            var key = (b.GroupId, b.Date);
            perDayCount[key] = perDayCount.TryGetValue(key, out var existing) ? existing + 1 : 1;
        }
        // Допоміжні методи для підрахунків навантаження по днях.
        int CountFor(int gid, DateOnly date) => perDayCount.TryGetValue((gid, date), out var c) ? c : 0;
        int CountModuleForDay(int gid, DateOnly date, int moduleId) =>
            busy.Count(x => x.GroupId == gid
                            && x.Date == date
                            && x.ModuleId == moduleId
                            && !excludedTypeIds.Contains(x.LessonTypeId));
        int CountDistinctModulesForDay(int gid, DateOnly date) =>
            busy.Where(x => x.GroupId == gid
                            && x.Date == date
                            && !excludedTypeIds.Contains(x.LessonTypeId))
                .Select(x => x.ModuleId)
                .Distinct()
                .Count();
        int CountGroupsWithModuleInSlot(int moduleId, DateOnly date, TimeOnly start, TimeOnly end) =>
            busy.Where(x => x.Date == date
                            && x.ModuleId == moduleId
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
        // Перевірки повторів модулів по днях.
        bool HadSameModulePreviousDay(int gid, int mid, DateOnly date)
        {
            var prev = date.AddDays(-1);
            return busy.Any(x => x.GroupId == gid
                                 && x.ModuleId == mid
                                 && x.Date == prev
                                 && !excludedTypeIds.Contains(x.LessonTypeId));
        }
        // Перевіряємо, чи модуль був у "вікні" навколо дати.
        bool HasRecentModule(int gid, int mid, DateOnly date, int windowDays = 2)
        {
            var from = date.AddDays(-windowDays);
            var to = date.AddDays(windowDays);
            return busy.Any(x => x.GroupId == gid
                                 && x.ModuleId == mid
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
        // Перелік курсів, задіяних у генерації.
        var courseIds = groups.Select(g => g.CourseId).Distinct().ToList();
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
        // Сортуємо теми за кодом, щоб автоген відповідав порядку у плані модуля.
        topicsAll.Sort((a, b) => TeacherDraftsHelpers.CompareTopicCodes(a.TopicCode, b.TopicCode));
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
        var missingModulesNotified = new HashSet<int>();
        int created = 0, skipped = 0;
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
        // Перевірка, чи тип заняття трактуємо як лекцію для об'єднання груп.
        bool IsLectureType(int lessonTypeId) => lectureTypeIds.Contains(lessonTypeId);
        // Перевірка доступності конкретної теми для групи з урахуванням лімітів використання.
        bool CanAssignSpecificTopic(int groupIdCheck, int moduleIdCheck, ModuleTopic topic)
        {
            var limit = GetTopicUsageLimit(topic);
            if (limit <= 0)
            {
                return false;
            }
            var key = (groupIdCheck, moduleIdCheck);
            topicAssignments.TryGetValue(key, out var assigned);
            var usedCount = assigned != null && assigned.TryGetValue(topic.Id, out var count) ? count : 0;
            return usedCount < limit;
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
        bool HadSameLessonTypePreviousDay(int gid, int mid, int lessonTypeId, DateOnly date)
        {
            var prev = date.AddDays(-1);
            return busy.Any(x => x.GroupId == gid
                                 && x.ModuleId == mid
                                 && x.Date == prev
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
                    remainingByGroupModule[(grpRow.Id, moduleId)] = Math.Max(0, hours);
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
        // Основний цикл генерації: обходимо всі групи.
        foreach (var grp in groups)
        {
            // Обідня перерва для курсу або глобальна (якщо не задано для курсу).
            var preferredFirstSlotLimit = preferredFirstSlotLimitsAll.FirstOrDefault(x => x.CourseId == grp.CourseId)
                                       ?? preferredFirstSlotLimitsAll.FirstOrDefault(x => x.CourseId == null);
            int? preferredFirstMaxSlotOrder = preferredFirstSlotLimit is not null && preferredFirstSlotLimit.MaxSlotOrder > 0
                ? preferredFirstSlotLimit.MaxSlotOrder
                : null;
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
            // Максимальна кількість пар одного модуля у межах дня.
            const int maxConsecutiveModuleSlotsPerDay = 5;
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
                var preferredOrders = busy
                    .Where(b => b.GroupId == grp.Id
                                && b.Date == date
                                && b.LessonTypeId == preferredFirstTypeId)
                    .Select(b => GetSlotOrder(b.StartTime, b.EndTime))
                    .Where(order => order > 0)
                    .OrderBy(order => order)
                    .ToList();
                return preferredOrders.Count == 0 ? null : preferredOrders[0];
            }
            bool SlotFilled(DateOnly date, TimeSlot slot) =>
                busy.Any(b => b.GroupId == grp.Id && b.Date == date && b.StartTime == slot.Start && b.EndTime == slot.End);
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
            // Жорстка перевірка денних правил для модуля:
            // 1) не більше 5 пар на день;
            // 2) модуль не можна "розривати" іншим модулем і потім повертатися до нього.
            bool ViolatesModuleDayHardRules(int groupIdCheck, DateOnly date, int moduleId, TimeOnly candidateStart, TimeOnly candidateEnd, out string reason)
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
                    dayLessons.Add((candidateStart, candidateEnd, moduleId));
                }
                var modulePerDayCount = dayLessons.Count(x => x.ModuleId == moduleId);
                if (modulePerDayCount > maxConsecutiveModuleSlotsPerDay)
                {
                    reason = $"Модуль <{ModuleTitleLabel(moduleId)}> не можна ставити більш ніж {maxConsecutiveModuleSlotsPerDay} пар у межах дня.";
                    return true;
                }
                var orderedLessons = dayLessons
                    .OrderBy(x => x.Start)
                    .ThenBy(x => x.End)
                    .ToList();
                int moduleSegments = 0;
                bool inModuleSegment = false;
                foreach (var lesson in orderedLessons)
                {
                    if (lesson.ModuleId == moduleId)
                    {
                        if (!inModuleSegment)
                        {
                            moduleSegments++;
                            inModuleSegment = true;
                            if (moduleSegments > 1)
                            {
                                reason = $"Модуль <{ModuleTitleLabel(moduleId)}> у межах дня ставимо суцільним блоком без повернення після перемикання на інший модуль.";
                                return true;
                            }
                        }
                    }
                    else
                    {
                        inModuleSegment = false;
                    }
                }
                reason = string.Empty;
                return false;
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
                        Reason: reason));
                }
            }
            // Спроба прибрати прогалини шляхом пересування занять у межах дня.
            bool TryShiftGaps(DateOnly date)
            {
                bool moved = false;
                var attempted = new HashSet<TeacherDraftItem>();
                while (DayHasGaps(date, out var gap) && gap is not null)
                {
                    var candidate = createdDrafts
                        .Where(cd => cd.GroupId == grp.Id
                                     && cd.Date == date
                                     && !cd.IsLocked
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
                    if (!slotIndexByTime.TryGetValue((candidate.StartTime, candidate.EndTime), out var candidateOldSlotIndex)
                        || !slotIndexByTime.TryGetValue((s, e), out var candidateNewSlotIndex))
                    {
                        continue;
                    }
                    var moduleIndexesAfterShift = busy
                        .Where(b => b.GroupId == grp.Id
                                    && b.Date == date
                                    && b.ModuleId == candidate.ModuleId
                                    && !excludedTypeIds.Contains(b.LessonTypeId))
                        .Select(b => slotIndexByTime.TryGetValue((b.StartTime, b.EndTime), out var idx) ? idx : -1)
                        .Where(idx => idx >= 0 && idx != candidateOldSlotIndex)
                        .Append(candidateNewSlotIndex)
                        .Distinct()
                        .OrderBy(idx => idx)
                        .ToList();
                    if (moduleIndexesAfterShift.Count > maxConsecutiveModuleSlotsPerDay)
                    {
                        RecordSlotFailureReason(
                            date,
                            gap,
                            $"Модуль <{ModuleTitleLabel(candidate.ModuleId)}> не можна ставити більш ніж {maxConsecutiveModuleSlotsPerDay} пари підряд у межах дня.");
                        continue;
                    }
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
                    int? tidCandidate = candidate.TeacherId;
                    if (tidCandidate is int tidVal && !TeacherFitsWorkingHours(tidVal, date, s, e))
                    {
                        continue;
                    }
                    if (CountGroupsWithModuleInSlot(candidate.ModuleId, date, s, e) >= maxParallelGroupsPerModuleInSlot)
                    {
                        continue;
                    }
                    bool peopleBusy = busy.Any(x => x.Date == date
                                                    && (x.GroupId == grp.Id || (tidCandidate is int t && x.TeacherId == t))
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
                        bool roomBusy = busy.Any(x => x.Date == date
                                                      && x.RoomId == rid
                                                      && !(x.GroupId == candidate.GroupId
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
                    var idx = busy.FindIndex(x =>
                        x.GroupId == candidate.GroupId
                        && x.Date == date
                        && x.StartTime == oldStart
                        && x.EndTime == oldEnd
                        && x.ModuleId == candidate.ModuleId
                        && x.TeacherId == candidate.TeacherId
                        && x.RoomId == candidate.RoomId);
                    if (idx >= 0)
                    {
                        busy.RemoveAt(idx);
                    }
                    var buildingId = candidate.RoomId.HasValue
                        ? roomsAll.FirstOrDefault(r => r.Id == candidate.RoomId)?.BuildingId
                        : null;
                    busy.Add(new BusySlot(
                        candidate.GroupId,
                        candidate.TeacherId,
                        candidate.RoomId,
                        date,
                        s,
                        e,
                        buildingId,
                        candidate.ModuleId,
                        candidate.LessonTypeId));
                    candidate.StartTime = s;
                    candidate.EndTime = e;
                    candidate.DayOfWeek = date.ToDateTime(TimeOnly.MinValue).DayOfWeek;
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
            var groupRandom = new Random(HashCode.Combine(weekStart.DayNumber, grp.Id, grp.CourseId));
            var sequenceRandom = new Random(HashCode.Combine(weekStart.DayNumber, grp.Id, grp.CourseId, 17));
            int? lastPrimaryModuleId = null;
            // Лічильник використання аудиторій поточною групою.
            var groupRoomUsage = busy
                .Where(b => b.GroupId == grp.Id && b.Date >= weekStart && b.Date < weekEnd && b.RoomId != null)
                .GroupBy(b => b.RoomId!.Value)
                .ToDictionary(g => g.Key, g => g.Count());
            // Ваги штрафів для вибору найкращого кандидата у слоті.
            // 0 вимикає вплив конкретного правила.
            const double penaltySameModulePrevDay = 10.0; // Повтор того ж модуля у сусідній день.
            const double penaltyExtraSameDay = 6.0; // Третя і наступні пари модуля за день.
            const double penaltySameSlotPattern = 1.5; // Повтор однакового StartTime для модуля в інші дні.
            const double maxExtraPenaltyPreferSameTeacherForConsecutiveModule = 6.0; // Перевага одного викладача на суміжні слоти модуля.
            const double penaltyPreferredFirstTypeLateSlot = 1.5; // Пізній слот для типу "бажано першим".
            const double penaltyNonPreferredEarlySlotWhilePreferredPending = 12.0; // Ранній непріоритетний тип, поки пріоритетний ще не поставлено.
            const double penaltyPreferredFirstBeyondLimitSlot = 20.0; // Вихід пріоритетного типу за ліміт номера слоту.
            const double penaltyNonPreferredBeforeFirstPreferred = 18.0; // Непріоритетний тип перед першим пріоритетним у межах дня.
            // Штраф за загальне навантаження викладача на курсі.
            double TeacherLoadPenalty(int teacherId) =>
                TeacherLoadScore(teacherId, grp.CourseId) * 0.25;
            // Штраф за зміну будівлі для групи або викладача.
            double BuildingDistancePenalty(int teacherId, Room? room, DateOnly date, TimeOnly start)
            {
                if (room is null || room.BuildingId == 0)
                    return 2.0;
                double score = 0;
                var groupPrev = LastGroupBuilding(date, start);
                var teacherPrev = LastTeacherBuilding(teacherId, date, start);
                if (groupPrev is int gb && gb != room.BuildingId) score += 1.0;
                if (teacherPrev is int tb && tb != room.BuildingId) score += 1.0;
                return score;
            }
            // Перевіряє, чи є альтернатива для слоту, якщо модуль не вдається поставити.
            bool HasAvailableAlternativeForSlot(int currentModuleId, DateOnly date, TimeOnly start, TimeOnly end)
            {
                foreach (var altModuleId in orderedModules)
                {
                    if (altModuleId == currentModuleId) continue;
                    if (RemainingFor(grp.Id, altModuleId) <= 0) continue;
                    if (HasRecentModule(grp.Id, altModuleId, date, windowDays: 2)) continue;
                    if (CountGroupsWithModuleInSlot(altModuleId, date, start, end) >= maxParallelGroupsPerModuleInSlot) continue;
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
                        bool peopleBusy = busy.Any(x => x.Date == date
                                                        && (x.GroupId == grp.Id || x.TeacherId == tid)
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
                        var requiresRoom = (typeById.TryGetValue(altLtId, out var ltMetaAlt) ? ltMetaAlt.RequiresRoom : (bool?)null) ?? true;
                        if (requiresRoom)
                        {
                            if (altRooms.Count == 0) continue;
                            foreach (var rm in altRooms)
                            {
                                bool roomBusy = busy.Any(x => x.Date == date
                                                              && x.RoomId == rm.Id
                                                              && x.StartTime < end && start < x.EndTime);
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
                var rng = new Random(HashCode.Combine(weekStart.DayNumber, grp.Id, grp.CourseId, date.DayNumber, 23));
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
            List<Room> CandidateRoomsFor(int mid, int requiredCapacity = -1)
            {
                allowedRoomsByModule.TryGetValue(mid, out var allowedRooms);
                allowedBuildingsByModule.TryGetValue(mid, out var allowedBuildings);
                var minCapacity = requiredCapacity > 0 ? requiredCapacity : grp.StudentsCount;
                return roomsAll
                    .Where(rm => (allowedBuildings == null || allowedBuildings.Count == 0 || allowedBuildings.Contains(rm.BuildingId))
                                 && (allowedRooms == null || allowedRooms.Count == 0 || allowedRooms.Contains(rm.Id))
                                 && rm.Capacity >= minCapacity)
                    .OrderBy(rm => groupRoomUsage.TryGetValue(rm.Id, out var used) ? used : 0)
                    .ThenBy(rm => rm.Capacity)
                    .ThenBy(rm => rm.Id)
                    .ToList();
            }
            // Остання будівля групи до поточного слоту.
            int? LastGroupBuilding(DateOnly date, TimeOnly start)
            {
                return busy
                    .Where(b => b.GroupId == grp.Id && b.Date == date && b.EndTime <= start && b.RoomId != null)
                    .OrderBy(b => b.EndTime)
                    .LastOrDefault()?.BuildingId;
            }
            // Остання будівля викладача до поточного слоту.
            int? LastTeacherBuilding(int teacherId, DateOnly date, TimeOnly start)
            {
                return busy
                    .Where(b => b.TeacherId == teacherId && b.Date == date && b.EndTime <= start && b.RoomId != null)
                    .OrderBy(b => b.EndTime)
                    .LastOrDefault()?.BuildingId;
            }
            // Повертає набір груп для спільної лекції в одному слоті.
            IReadOnlyList<int> ResolveSharedLectureGroups(
                int moduleId,
                int lessonTypeId,
                ModuleTopic? topic,
                bool isSelfStudyPlacement,
                DateOnly date,
                TimeOnly start,
                TimeOnly end,
                Room room)
            {
                var sharedGroupIds = new List<int> { grp.Id };
                if (isSelfStudyPlacement || !IsLectureType(lessonTypeId))
                {
                    return sharedGroupIds;
                }
                if (!selectedGroupsByCourse.TryGetValue(grp.CourseId, out var sameCourseGroups) || sameCourseGroups.Count <= 1)
                {
                    return sharedGroupIds;
                }
                int totalStudents = grp.StudentsCount;
                foreach (var otherGroup in sameCourseGroups)
                {
                    if (otherGroup.Id == grp.Id)
                    {
                        continue;
                    }
                    if (!IsWorking(date, otherGroup))
                    {
                        continue;
                    }
                    if (RemainingFor(otherGroup.Id, moduleId) <= 0)
                    {
                        continue;
                    }
                    if (CountFor(otherGroup.Id, date) >= slots.Count)
                    {
                        continue;
                    }
                    if (TopicsDepleted(otherGroup.Id, moduleId))
                    {
                        continue;
                    }
                    if (topic is not null && !CanAssignSpecificTopic(otherGroup.Id, moduleId, topic))
                    {
                        continue;
                    }
                    if (totalStudents + otherGroup.StudentsCount > room.Capacity)
                    {
                        continue;
                    }
                    bool groupBusy = busy.Any(x => x.GroupId == otherGroup.Id
                                                   && x.Date == date
                                                   && x.StartTime < end
                                                   && start < x.EndTime);
                    if (groupBusy)
                    {
                        continue;
                    }
                    if (ViolatesModuleDayHardRules(otherGroup.Id, date, moduleId, start, end, out _))
                    {
                        continue;
                    }
                    sharedGroupIds.Add(otherGroup.Id);
                    totalStudents += otherGroup.StudentsCount;
                }
                return sharedGroupIds;
            }
            // Рахує сумарну кількість слухачів для груп у спільній парі.
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
            // Визначає, який модуль вважаємо пріоритетним на поточний день.
            int? ResolvePrimaryModule()
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
                return candidates[sequenceRandom.Next(candidates.Count)];
            }
            // Основна спроба розмістити модуль у межах конкретного дня.
            async Task<bool> TryPlaceModuleAsync(int moduleId, DateOnly date, bool isPrimary, bool allowRepeatPreviousDay = false, bool allowExtraSameDay = false, bool relaxed = false, bool preferEarliestSlot = true, TimeSlot? forcedSlot = null)
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
                if (preferredFirstEnabled && (penaltyPreferredFirstTypeLateSlot > 0 || penaltyNonPreferredEarlySlotWhilePreferredPending > 0))
                {
                    // Визначаємо, чи є сьогодні ще теми з пріоритетним типом.
                    foreach (var mid in orderedModules)
                    {
                        if (RemainingFor(grp.Id, mid) <= 0) continue;
                        var savedLtIndex = ltIndex;
                        try
                        {
                            if (PickLessonType(grp.Id, grp.CourseId, mid, date).LessonTypeId == preferredFirstTypeId)
                            {
                                preferredFirstPendingToday = true;
                                break;
                            }
                        }
                        finally
                        {
                            ltIndex = savedLtIndex;
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
                            warnings.Add($"В базі даних відсутній модуль із ідентифікатором {moduleId}. Автогенерацію для нього пропущено.");
                        }
                        skipped++;
                        return false;
                    }
                    return true;
                }
                // Лейбл модуля для текстів попереджень.
                string ModuleLabel() => string.IsNullOrWhiteSpace(moduleTitle) ? $"#{moduleId}" : moduleTitle!;
                // Для основних модулів — перевіряємо залишок годин.
                if (!isFiller && RemainingFor(grp.Id, moduleId) <= 0)
                {
                    return false;
                }
                // Якщо модуль відсутній у БД — пропускаємо.
                if (!await EnsureModuleTitleAsync())
                {
                    return false;
                }
                // Якщо теми вичерпано — прибираємо модуль з плану.
                if (TopicsDepleted(grp.Id, moduleId))
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
                        ? $"Не знайдено керівників для модуля <{ModuleLabel()}> (група {grp.Name}). Самостійну годину пропущено."
                        : $"Не знайдено викладачів для модуля <{ModuleLabel()}> (група {grp.Name}).";
                    RecordSlotFailureReasonForAllSlots(date, teacherReason);
                    warnings.Add(teacherReason);
                    if (placeSelfStudy)
                    {
                        var key = (grp.Id, moduleId);
                        if (selfStudyRemainingByGroupModule.ContainsKey(key))
                            selfStudyRemainingByGroupModule[key] = 0;
                    }
                    skipped++;
                    return false;
                }
                // Кандидатні аудиторії для модуля.
                var candidateRooms = CandidateRoomsFor(moduleId);
                PlacementCandidate? best = null;
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
                    if (sameModuleIndexes.Count > 0)
                    {
                        var indexesWithCandidate = sameModuleIndexes
                            .Append(slotIndex)
                            .Distinct()
                            .OrderBy(idx => idx)
                            .ToList();
                        var isContiguousBlock = indexesWithCandidate[^1] - indexesWithCandidate[0] + 1 == indexesWithCandidate.Count;
                        if (!isContiguousBlock)
                        {
                            RecordSlotFailureReason(date, sl, $"Модуль <{ModuleLabel()}> у межах дня ставимо суцільним блоком без повернення після перемикання на інший модуль.");
                            continue;
                        }
                        if (indexesWithCandidate.Count > maxConsecutiveModuleSlotsPerDay)
                        {
                            RecordSlotFailureReason(date, sl, $"Модуль <{ModuleLabel()}> не можна ставити більш ніж {maxConsecutiveModuleSlotsPerDay} пари підряд у межах дня.");
                            continue;
                        }
                    }
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
                    bool slotBreak = busy.Any(b => b.GroupId == grp.Id && b.Date == date
                                                   && b.LessonTypeId == typeBreakId && b.StartTime == s && b.EndTime == e);
                    if (slotBreak)
                    {
                        RecordSlotFailureReason(date, sl, $"Слот {slotLabel} зайнятий перервою (BREAK).");
                        continue;
                    }
                    if (ViolatesModuleDayHardRules(grp.Id, date, moduleId, s, e, out var hardRuleReason))
                    {
                        RecordSlotFailureReason(date, sl, hardRuleReason);
                        continue;
                    }
                    // Уникаємо повторів модуля у вузькому часовому вікні.
                    if (CountGroupsWithModuleInSlot(moduleId, date, s, e) >= maxParallelGroupsPerModuleInSlot)
                    {
                        continue;
                    }
                    bool hasRecent = HasRecentModule(grp.Id, moduleId, date, windowDays: 2);
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
                    if (!TypeAllowed(ltypeId))
                    {
                        string reason;
                        if (!typeById.TryGetValue(ltypeId, out var ltInfo))
                        {
                            reason = $"Тип заняття #{ltypeId} не знайдено, тому слот {slotLabel} пропущено.";
                        }
                        else if (!ltInfo.IsActive)
                        {
                            reason = $"Тип заняття \"{ltInfo.Name}\" неактивний, тому слот {slotLabel} пропущено.";
                        }
                        else if (!ltInfo.CountInPlan)
                        {
                            reason = $"Тип заняття \"{ltInfo.Name}\" не враховується у плані (CountInPlan=false), тому слот {slotLabel} пропущено.";
                        }
                        else if (excludedTypeIds.Contains(ltypeId))
                        {
                            reason = $"Тип заняття \"{ltInfo.Name}\" виключено з автогенерації, тому слот {slotLabel} пропущено.";
                        }
                        else
                        {
                            reason = $"Тип заняття #{ltypeId} недоступний для автогенерації, тому слот {slotLabel} пропущено.";
                        }
                        RecordSlotFailureReason(date, sl, reason);
                        continue;
                    }
                    // Дуже сильно штрафуємо тип з прапорцем "Бажано першим у тижні" за вихід за межу слота, але не блокуємо жорстко.
                    string? preferredFirstAfterLimitNote = null;
                    double preferredFirstAfterLimitPenalty = 0;
                    if (preferredFirstEnabled
                        && preferredFirstMaxSlotOrder is int maxPreferredSlot
                        && ltypeId == preferredFirstTypeId
                        && GetSlotOrder(sl.Start, sl.End) > maxPreferredSlot)
                    {
                        var slotOrder = GetSlotOrder(sl.Start, sl.End);
                        var overLimitBy = Math.Max(1, slotOrder - maxPreferredSlot);
                        preferredFirstAfterLimitPenalty = overLimitBy * penaltyPreferredFirstBeyondLimitSlot;
                        preferredFirstAfterLimitNote = $"Тип з прапорцем \"Бажано першим у тижні\" поставлено після ліміту (слот №{slotOrder}, ліміт №{maxPreferredSlot})";
                    }
                    // Дуже сильно штрафуємо інші типи в ранніх слотах до першого заняття з прапорцем "Бажано першим у тижні", але не блокуємо жорстко.
                    string? nonPreferredBeforeFirstPreferredNote = null;
                    double nonPreferredBeforeFirstPreferredPenalty = 0;
                    if (preferredFirstEnabled
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
                            continue;
                        }
                    }
                    // Перебір кандидатів-викладачів для цього слоту.
                    foreach (var tidCandidate in filteredTeacherIds)
                    {
                        if (!TeacherFitsWorkingHours(tidCandidate, date, s, e))
                        {
                            slotIssues.Add($"Викладач {TeacherLabel(tidCandidate)} не працює у слоті {slotLabel}.");
                            continue;
                        }
                        // Перевірка конфліктів по групі або викладачу.
                        bool peopleBusy = busy.Any(x => x.Date == date
                                                        && (x.GroupId == grp.Id || x.TeacherId == tidCandidate)
                                                        && x.StartTime < e && s < x.EndTime);
                        if (peopleBusy)
                        {
                            slotIssues.Add($"Група {grp.Name} або викладач {TeacherLabel(tidCandidate)} зайняті у слоті {slotLabel}.");
                            continue;
                        }
                        // Накопичуємо штрафи та їх пояснення.
                        var penalties = new List<string>();
                        double penaltyScore = 0;
                        if (!string.IsNullOrWhiteSpace(preferredFirstAfterLimitNote))
                        {
                            penaltyScore += preferredFirstAfterLimitPenalty;
                            penalties.Add(preferredFirstAfterLimitNote);
                        }
                        if (!string.IsNullOrWhiteSpace(nonPreferredBeforeFirstPreferredNote))
                        {
                            penaltyScore += nonPreferredBeforeFirstPreferredPenalty;
                            penalties.Add(nonPreferredBeforeFirstPreferredNote);
                        }
                        if (preferredFirstEnabled)
                        {
                            if (ltypeId == preferredFirstTypeId && penaltyPreferredFirstTypeLateSlot > 0)
                            {
                                penaltyScore += slotIndex * penaltyPreferredFirstTypeLateSlot;
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
                        // Штрафуємо повтори модуля в один день.
                        var sameDayCount = CountModuleForDay(grp.Id, date, moduleId);
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
                        // Додаємо штраф за навантаження викладача.
                        penaltyScore += TeacherLoadPenalty(tidCandidate);
                        // Штрафуємо повтор однакового часу для модуля в інший день.
                        bool sameSlotPattern = busy.Any(b =>
                            b.GroupId == grp.Id
                            && b.ModuleId == moduleId
                            && b.Date != date
                            && b.StartTime == s
                            && !excludedTypeIds.Contains(b.LessonTypeId));
                        if (sameSlotPattern)
                        {
                            penaltyScore += penaltySameSlotPattern;
                            penalties.Add("Повтор того ж часу в інші дні");
                        }
                        // Підбір аудиторій (якщо потрібні).
                        if (requiresRoom)
                        {
                            if (candidateRooms.Count == 0)
                            {
                                var roomReason = $"Не знайдено аудиторій для модуля <{ModuleLabel()}> (група {grp.Name}) у слоті {slotLabel}.";
                                RecordSlotFailureReason(date, sl, roomReason);
                                warnings.Add(roomReason);
                                continue;
                            }
                            // Перевіряємо кожну аудиторію на зайнятість.
                            foreach (var rm in candidateRooms)
                            {
                                if (preferredAdjacentRoomId is int preferredRoomId && rm.Id != preferredRoomId)
                                {
                                    continue;
                                }
                                bool roomBusy = busy.Any(x => x.Date == date
                                                              && x.RoomId == rm.Id
                                                              && x.StartTime < e && s < x.EndTime);
                                if (roomBusy)
                                {
                                    slotIssues.Add($"Усі аудиторії для модуля <{ModuleLabel()}> зайняті у слоті {slotLabel}.");
                                    continue;
                                }
                                var sharedGroupIds = ResolveSharedLectureGroups(
                                    moduleId,
                                    ltypeId,
                                    topicSelection,
                                    isSelfStudyPlacement,
                                    date,
                                    s,
                                    e,
                                    rm);
                                var sharedStudents = SharedStudentsCount(sharedGroupIds);
                                if (sharedStudents <= 0 || sharedStudents > rm.Capacity)
                                {
                                    continue;
                                }
                                var capacityReserve = Math.Max(0, rm.Capacity - sharedStudents);
                                var capacityPenalty = isLecturePlacement
                                    ? capacityReserve * 0.08
                                    : capacityReserve * 0.25;
                                var sharedLectureBonus = isLecturePlacement
                                    ? Math.Max(0, sharedGroupIds.Count - 1) * 18.0
                                    : 0;
                                var totalPenalty = penaltyScore
                                    + BuildingDistancePenalty(tidCandidate, rm, date, s)
                                    + capacityPenalty
                                    - sharedLectureBonus;
                                var notes = new List<string>(penalties);
                                var candidate = new PlacementCandidate(sl, tidCandidate, rm, ltypeId, topicSelection, isSelfStudyPlacement, sharedGroupIds, totalPenalty, notes);
                                slotCandidates.Add(candidate);
                            }
                        }
                        else
                        {
                            var notes = new List<string>(penalties);
                            var candidate = new PlacementCandidate(sl, tidCandidate, null, ltypeId, topicSelection, isSelfStudyPlacement, new[] { grp.Id }, penaltyScore, notes);
                            slotCandidates.Add(candidate);
                        }
                    }
                    // Якщо є кандидати — обираємо найкращого за штрафами.
                    if (slotCandidates.Count > 0)
                    {
                        var bestAny = slotCandidates
                            .OrderBy(c => c.Penalty)
                            .ThenBy(c => groupRandom.Next())
                            .First();
                        var localBest = bestAny;
                        if (preferredAdjacentTeacherId is int pt && maxExtraPenaltyPreferSameTeacherForConsecutiveModule > 0)
                        {
                            var bestPreferred = slotCandidates
                                .Where(c => c.TeacherId == pt)
                                .OrderBy(c => c.Penalty)
                                .ThenBy(c => groupRandom.Next())
                                .FirstOrDefault();
                            if (bestPreferred is not null
                                && bestPreferred.Penalty <= bestAny.Penalty + maxExtraPenaltyPreferSameTeacherForConsecutiveModule)
                            {
                                localBest = bestPreferred;
                            }
                        }
                        if (preferEarliestSlot)
                        {
                            best = localBest;
                            break;
                        }
                        if (best is null || localBest.Penalty < best.Penalty)
                        {
                            best = localBest;
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
                // Якщо не знайдено жодного слоту — виходимо.
                if (best is null)
                {
                    return false;
                }
                // Фіксуємо обраний варіант та створюємо чернетку.
                var selectedSlot = best.Slot;
                var selectedRoom = best.Room;
                var selectedTeacher = best.TeacherId;
                var selectedTopic = best.Topic;
                var selectedLessonTypeId = best.LessonTypeId;
                var startTime = selectedSlot.Start;
                var endTime = selectedSlot.End;
                var placedGroupIds = best.SharedGroupIds.Count > 0
                    ? best.SharedGroupIds
                    : new[] { grp.Id };
                bool currentGroupPlaced = false;
                foreach (var sharedGroupId in placedGroupIds.Distinct())
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
                        IsSelfStudy = best.IsSelfStudy
                    };
                    _db.TeacherDraftItems.Add(item);
                    if (sharedGroupId == grp.Id)
                    {
                        createdDrafts.Add(item);
                        currentGroupPlaced = true;
                    }
                    // Позначаємо тему як використану (для звичайних занять).
                    if (selectedTopic is not null && !best.IsSelfStudy)
                    {
                        MarkTopicUsed(sharedGroupId, moduleId, selectedTopic);
                    }
                    // Додаємо слот у список зайнятих.
                    busy.Add(new BusySlot(
                        sharedGroupId,
                        selectedTeacher,
                        selectedRoom?.Id,
                        date,
                        startTime,
                        endTime,
                        selectedRoom?.BuildingId,
                        moduleId,
                        selectedLessonTypeId));
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
                    if (best.IsSelfStudy && selfStudyRemainingByGroupModule.TryGetValue(sharedRemainingKey, out var ssLeft) && ssLeft > 0)
                    {
                        selfStudyRemainingByGroupModule[sharedRemainingKey] = Math.Max(0, ssLeft - 1);
                    }
                    if (best.IsSelfStudy && selectedTopic is not null)
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
                // Оновлюємо статистику використання аудиторій.
                if (selectedRoom?.Id is int ridSelected)
                {
                    groupRoomUsage[ridSelected] = groupRoomUsage.TryGetValue(ridSelected, out var usedRoom)
                        ? usedRoom + 1
                        : 1;
                }
                if (isPrimary)
                {
                    lastPrimaryModuleId = moduleId;
                }
                // Додаємо нотатки з причинами штрафів.
                if (best.Notes.Count > 0)
                {
                    var noteText = string.Join("; ", best.Notes);
                    warnings.Add($"[{date:yyyy-MM-dd} {startTime:HH\\:mm}-{endTime:HH\\:mm}] {grp.Name}: {noteText}");
                }
                return true;
            }
            // Генеруємо розклад по днях тижня для поточної групи.
            for (int d = 0; d < 7; d++)
            {
                var date = weekStart.AddDays(d);
                if (!IsWorking(date, grp)) continue;
                ApplySlotsForDate(date);
                int maxPerDay = slots.Count;
                if (maxPerDay == 0) continue;
                var modulesAttemptedToday = new HashSet<int>();
                var orderedModulesForDay = BuildOrderedModulesForDay(date);
                int preferredMaxDistinctModulesPerDay = maxPerDay >= 8 ? 3 : 2;
                int maxDistinctModulesPerDay = Math.Min(5, maxPerDay);
                bool CanIntroduceModuleToday(int moduleId, bool bypassPreferredLimit = false)
                {
                    if (CountModuleForDay(grp.Id, date, moduleId) > 0)
                    {
                        return true;
                    }
                    var distinctToday = CountDistinctModulesForDay(grp.Id, date);
                    if (distinctToday >= maxDistinctModulesPerDay)
                    {
                        return false;
                    }
                    if (bypassPreferredLimit)
                    {
                        return true;
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
                // Допоміжний прохід: заповнення залишків з різними рівнями послаблень.
                async Task FillWithRemainingModulesAsync(bool allowRepeatPreviousDay = false, bool allowExtraSameDay = false, bool relaxed = false)
                {
                    bool tryAnotherCycle;
                    do
                    {
                        tryAnotherCycle = false;
                        foreach (var moduleId in PreferNotUsedLastWeek(orderedModulesForDay))
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
                            var placed = await TryPlaceModuleAsync(
                                moduleId,
                                date,
                                isPrimary: false,
                                allowRepeatPreviousDay: allowRepeatPreviousDay,
                                allowExtraSameDay: allowExtraSameDay,
                                relaxed: relaxed);
                            if (placed && allowExtraSameDay && CountFor(grp.Id, date) < maxPerDay)
                            {
                                tryAnotherCycle = true;
                            }
                        }
                    } while (allowExtraSameDay && tryAnotherCycle && CountFor(grp.Id, date) < maxPerDay);
                }
                async Task<bool> TryPlaceSecondModuleForDayAsync(int primaryModuleIdValue)
                {
                    foreach (var moduleId in PreferNotUsedLastWeek(orderedModulesForDay))
                    {
                        if (moduleId == primaryModuleIdValue)
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
                        var placed = await TryPlaceModuleAsync(
                            moduleId,
                            date,
                            isPrimary: false,
                            allowRepeatPreviousDay: softFill,
                            allowExtraSameDay: softFill,
                            relaxed: softFill);
                        if (placed)
                        {
                            return true;
                        }
                    }
                    return false;
                }
                IEnumerable<int> BuildGapCandidateModules()
                {
                    var modulesWithRemaining = remainingByGroupModule
                        .Where(kv => kv.Key.GroupId == grp.Id && kv.Value > 0)
                        .Select(kv => kv.Key.ModuleId);
                    return PreferNotUsedLastWeek(
                        orderedModulesForDay
                            .Concat(fillerModulesOrdered)
                            .Concat(modulesWithRemaining)
                            .Distinct());
                }
                async Task<bool> TryFillGapWithVariantsAsync(TimeSlot gap, bool allowRepeatPreviousDay, bool allowExtraSameDay, bool relaxed, bool bypassDistinctLimit)
                {
                    foreach (var moduleId in BuildGapCandidateModules())
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
                            forcedSlot: gap);
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
                    do
                    {
                        progress = false;
                        pass++;
                        var gaps = slots.Where(sl => !SlotFilled(date, sl)).ToList();
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
                                             allowRepeatPreviousDay: false,
                                             allowExtraSameDay: false,
                                             relaxed: false,
                                             bypassDistinctLimit: false)
                                         || await TryFillGapWithVariantsAsync(
                                             gap,
                                             allowRepeatPreviousDay: false,
                                             allowExtraSameDay: true,
                                             relaxed: false,
                                             bypassDistinctLimit: false)
                                         || await TryFillGapWithVariantsAsync(
                                             gap,
                                             allowRepeatPreviousDay: true,
                                             allowExtraSameDay: true,
                                             relaxed: true,
                                             bypassDistinctLimit: false)
                                         || await TryFillGapWithVariantsAsync(
                                             gap,
                                             allowRepeatPreviousDay: true,
                                             allowExtraSameDay: true,
                                             relaxed: true,
                                             bypassDistinctLimit: true);
                            if (!placed)
                            {
                                continue;
                            }
                            progress = true;
                            anyPlaced = true;
                        }
                    } while (progress && pass < Math.Max(1, slots.Count * 2) && CountFor(grp.Id, date) < maxPerDay);
                    return anyPlaced;
                }
                // Основний модуль дня (пріоритетний у логіці курсу).
                var primaryModuleId = ResolvePrimaryModule();
                bool placedPrimary = false;
                if (primaryModuleId.HasValue)
                {
                    bool secondModulePlaced = false;
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
                        && CountDistinctModulesForDay(grp.Id, date) < 2)
                    {
                        secondModulePlaced = await TryPlaceSecondModuleForDayAsync(primaryModuleId.Value);
                    }
                    if (placedPrimary
                        && !secondModulePlaced
                        && RemainingFor(grp.Id, primaryModuleId.Value) > 0
                        && CountModuleForDay(grp.Id, date, primaryModuleId.Value) < 2
                        && CountFor(grp.Id, date) < maxPerDay)
                    {
                        await TryPlaceModuleAsync(
                            primaryModuleId.Value,
                            date,
                            isPrimary: true,
                            allowRepeatPreviousDay: softFill,
                            allowExtraSameDay: softFill,
                            relaxed: softFill);
                    }
                }
                // Черга filler-модулів, відсортована за залишками.
                Queue<int> BuildFillerQueueForDay()
                {
                    if (fillerModulesOrdered.Count == 0)
                        return new Queue<int>();
                    var ordered = fillerModulesOrdered
                        .OrderByDescending(mid => RemainingFor(grp.Id, mid))
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
                        var placedFiller = await TryPlaceModuleAsync(
                            fillerModuleId,
                            date,
                            isPrimary: false,
                            allowRepeatPreviousDay: softFill,
                            allowExtraSameDay: softFill,
                            relaxed: softFill);
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
                if (CountFor(grp.Id, date) > 0 && CountDistinctModulesForDay(grp.Id, date) == 1)
                {
                    warnings.Add($"[{date:yyyy-MM-dd}] {grp.Name}: вдалося розмістити лише один модуль за день; перевірте доступність викладачів, аудиторій та залишки годин інших модулів.");
                }
                var shifted = TryShiftGaps(date);
                if (shifted && CountFor(grp.Id, date) < maxPerDay)
                {
                    await TryExhaustiveGapFillAsync();
                    TryShiftGaps(date);
                }
                if (DayHasGaps(date, out var remainingGap) && remainingGap is not null)
                {
                    WarnGap(date, remainingGap);
                }
            }
        }
        // Зберігаємо створені чернетки та повертаємо результат.
        await _db.SaveChangesAsync();
        return Ok(new AutoGenResult(created, skipped, warnings, gapDetails));
    }

}
