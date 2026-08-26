namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

// Мінімальна проєкція рядка розкладу для підрахунку навчальних годин.
public sealed record CurriculumScheduleRow(
    int Id,
    int CourseId,
    string? BatchKey,
    DateOnly Date,
    TimeOnly StartTime,
    TimeOnly EndTime,
    int GroupId,
    int ModuleId,
    int LessonTypeId,
    int? ModuleTopicId,
    int? TeacherId,
    int? RoomId,
    bool IsSelfStudy);

// Узгоджено схлопує технічні рядки одного логічного заняття для навчальних агрегатів.
public static class CurriculumScheduleAggregation
{
    // Для плану модуля теми й співвикладачі одного заняття не створюють додаткових годин.
    public static IReadOnlyList<CurriculumScheduleRow> CollapseForPlan(
        IEnumerable<CurriculumScheduleRow> rows)
        => Collapse(rows, preserveTopic: false);

    // Для статистики тем співвикладачі схлопуються, але кожна окрема тема зберігається.
    public static IReadOnlyList<CurriculumScheduleRow> CollapseForTopics(
        IEnumerable<CurriculumScheduleRow> rows)
        => Collapse(rows, preserveTopic: true);

    // Для навантаження кожен викладач отримує тривалість логічного заняття один раз,
    // навіть якщо його рядок повторено для кількох тем тієї самої події.
    public static IReadOnlyList<CurriculumScheduleRow> CollapseForTeacherLoad(
        IEnumerable<CurriculumScheduleRow> rows)
    {
        var indexedRows = rows
            .Select((row, index) => new IndexedRow(index, row))
            .ToList();
        var collapsed = indexedRows
            .Where(item => !string.IsNullOrWhiteSpace(item.Row.BatchKey))
            .GroupBy(item => new TeacherLoadBatchLogicalEventKey(
                item.Row.CourseId,
                item.Row.BatchKey!,
                item.Row.Date,
                item.Row.StartTime,
                item.Row.EndTime,
                item.Row.GroupId,
                item.Row.ModuleId,
                item.Row.LessonTypeId,
                item.Row.TeacherId))
            .Select(group => group.First())
            .ToList();

        foreach (var legacyGroup in indexedRows
                     .Where(item => string.IsNullOrWhiteSpace(item.Row.BatchKey))
                     .GroupBy(item => BuildLegacyKey(item.Row)))
        {
            var legacyRows = legacyGroup.ToList();
            var isLogicalEvent = legacyRows.Count > 1
                                 && legacyRows
                                     .Select(item => (item.Row.ModuleTopicId, item.Row.TeacherId))
                                     .Distinct()
                                     .Skip(1)
                                     .Any();
            if (!isLogicalEvent)
            {
                collapsed.AddRange(legacyRows);
                continue;
            }

            collapsed.AddRange(legacyRows
                .GroupBy(item => item.Row.TeacherId)
                .Select(group => group.First()));
        }

        return collapsed
            .OrderBy(item => item.Index)
            .Select(item => item.Row)
            .ToList();
    }

    // Перетворює тривалість заняття на ту саму цілу кількість годин, яку зберігають агрегати.
    public static int ScheduledHours(TimeOnly start, TimeOnly end)
    {
        var hours = (end.ToTimeSpan() - start.ToTimeSpan()).TotalHours;
        return Math.Max(1, (int)Math.Ceiling(hours));
    }

    private static IReadOnlyList<CurriculumScheduleRow> Collapse(
        IEnumerable<CurriculumScheduleRow> rows,
        bool preserveTopic)
    {
        var indexedRows = rows
            .Select((row, index) => new IndexedRow(index, row))
            .ToList();
        var collapsed = indexedRows
            .Where(item => !string.IsNullOrWhiteSpace(item.Row.BatchKey))
            .GroupBy(item => BuildBatchKey(item.Row, preserveTopic))
            .Select(group => group.First())
            .ToList();

        foreach (var legacyGroup in indexedRows
                     .Where(item => string.IsNullOrWhiteSpace(item.Row.BatchKey))
                     .GroupBy(item => BuildLegacyKey(item.Row)))
        {
            var distinctTopicTeachers = legacyGroup
                .Select(item => (item.Row.ModuleTopicId, item.Row.TeacherId))
                .Distinct()
                .Take(2)
                .Count();
            if (legacyGroup.Count() <= 1 || distinctTopicTeachers <= 1)
            {
                collapsed.AddRange(legacyGroup);
                continue;
            }

            if (preserveTopic)
            {
                collapsed.AddRange(legacyGroup
                    .GroupBy(item => item.Row.ModuleTopicId)
                    .Select(group => group.First()));
            }
            else
            {
                collapsed.Add(legacyGroup.First());
            }
        }

        return collapsed
            .OrderBy(item => item.Index)
            .Select(item => item.Row)
            .ToList();
    }

    private static BatchLogicalEventKey BuildBatchKey(
        CurriculumScheduleRow row,
        bool preserveTopic)
        => new(
            row.CourseId,
            row.BatchKey!,
            row.Date,
            row.StartTime,
            row.EndTime,
            row.GroupId,
            row.ModuleId,
            row.LessonTypeId,
            preserveTopic ? row.ModuleTopicId : null);

    private static LegacyLogicalEventKey BuildLegacyKey(CurriculumScheduleRow row)
        => new(
            row.CourseId,
            row.Date,
            row.StartTime,
            row.EndTime,
            row.GroupId,
            row.ModuleId,
            row.LessonTypeId,
            row.RoomId,
            row.IsSelfStudy);

    private readonly record struct IndexedRow(int Index, CurriculumScheduleRow Row);

    private readonly record struct BatchLogicalEventKey(
        int CourseId,
        string BatchKey,
        DateOnly Date,
        TimeOnly StartTime,
        TimeOnly EndTime,
        int GroupId,
        int ModuleId,
        int LessonTypeId,
        int? ModuleTopicId);

    private readonly record struct TeacherLoadBatchLogicalEventKey(
        int CourseId,
        string BatchKey,
        DateOnly Date,
        TimeOnly StartTime,
        TimeOnly EndTime,
        int GroupId,
        int ModuleId,
        int LessonTypeId,
        int? TeacherId);

    private readonly record struct LegacyLogicalEventKey(
        int CourseId,
        DateOnly Date,
        TimeOnly StartTime,
        TimeOnly EndTime,
        int GroupId,
        int ModuleId,
        int LessonTypeId,
        int? RoomId,
        bool IsSelfStudy);
}
