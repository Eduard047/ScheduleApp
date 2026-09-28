namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

// Перевіряє, що перед не лекційною темою проведено всі потрібні аудиторні години лекцій.
public static class LecturePrerequisitePolicy
{
    public sealed record Topic(
        int Id,
        int ModuleId,
        int Order,
        bool IsLecture,
        int RequiredHours);

    public sealed record Placement(
        int GroupId,
        int ModuleId,
        int TopicId,
        DateOnly Date,
        TimeOnly Start,
        TimeOnly End,
        bool IsSelfStudy);

    public sealed record Violation(
        Placement Target,
        Topic Prerequisite,
        int RequiredHours,
        int CompletedHours,
        int MissingHours);

    // Узгоджує визначення лекції для адаптерів генератора та валідатора.
    public static bool IsLecture(string? lessonTypeCode, string? lessonTypeName = null)
    {
        var code = lessonTypeCode?.Trim();
        if (string.Equals(code, "LECTURE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "LECT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(code, "LEC", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = lessonTypeName?.Trim();
        return !string.IsNullOrWhiteSpace(name)
               && (name.Contains("ЛЕКЦ", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("LECTURE", StringComparison.OrdinalIgnoreCase));
    }

    // Повертає окрему проблему для кожної відсутньої попередньої лекційної теми.
    public static IReadOnlyList<Violation> FindViolations(
        IEnumerable<Topic> topics,
        IEnumerable<Placement> placements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentNullException.ThrowIfNull(placements);
        cancellationToken.ThrowIfCancellationRequested();

        var topicList = new List<Topic>();
        foreach (var topic in topics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            topicList.Add(topic);
        }

        var placementList = new List<Placement>();
        foreach (var placement in placements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            placementList.Add(placement);
        }

        var topicByKey = topicList
            .GroupBy(topic => (topic.ModuleId, topic.Id))
            .ToDictionary(group => group.Key, group => group.First());
        var lecturePrerequisitesByModule = topicList
            .Where(topic => topic.IsLecture && topic.RequiredHours > 0)
            .GroupBy(topic => topic.ModuleId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(topic => topic.Order)
                    .ThenBy(topic => topic.Id)
                    .ToList());
        var completedSlotsByGroupModuleTopic = placementList
            .Where(placement => !placement.IsSelfStudy && placement.End > placement.Start)
            .GroupBy(placement => (placement.GroupId, placement.ModuleId, placement.TopicId))
            .ToDictionary(
                group => group.Key,
                group => BuildCompletedSlots(group, cancellationToken));

        var violations = new List<Violation>();
        foreach (var target in placementList
                     .Where(placement => !placement.IsSelfStudy)
                     .Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!topicByKey.TryGetValue((target.ModuleId, target.TopicId), out var targetTopic)
                || targetTopic.IsLecture
                || !lecturePrerequisitesByModule.TryGetValue(target.ModuleId, out var lectureTopics))
            {
                continue;
            }

            foreach (var prerequisite in lectureTopics
                         .Where(topic => topic.Order < targetTopic.Order))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completedHours = completedSlotsByGroupModuleTopic.TryGetValue(
                    (target.GroupId, prerequisite.ModuleId, prerequisite.Id),
                    out var completedSlots)
                    ? CountCompletedBefore(completedSlots, target.Date, target.Start)
                    : 0;
                if (completedHours >= prerequisite.RequiredHours)
                {
                    continue;
                }

                violations.Add(new Violation(
                    target,
                    prerequisite,
                    prerequisite.RequiredHours,
                    completedHours,
                    prerequisite.RequiredHours - completedHours));
            }
        }

        return violations;
    }

    private static CompletedSlot[] BuildCompletedSlots(
        IEnumerable<Placement> placements,
        CancellationToken cancellationToken)
    {
        var intervals = new HashSet<Slot>();
        foreach (var placement in placements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            intervals.Add(new Slot(placement.Date, placement.Start, placement.End));
        }

        var completedSlots = new List<CompletedSlot>(intervals.Count);
        DateOnly? lastDate = null;
        var lastEnd = default(TimeOnly);
        foreach (var interval in intervals
                     .OrderBy(slot => slot.Date)
                     .ThenBy(slot => slot.End)
                     .ThenBy(slot => slot.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lastDate == interval.Date && interval.Start < lastEnd)
            {
                continue;
            }

            completedSlots.Add(new CompletedSlot(interval.Date, interval.End));
            lastDate = interval.Date;
            lastEnd = interval.End;
        }

        return completedSlots.ToArray();
    }

    private static int CountCompletedBefore(
        IReadOnlyList<CompletedSlot> completedSlots,
        DateOnly date,
        TimeOnly start)
    {
        var low = 0;
        var high = completedSlots.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            var slot = completedSlots[middle];
            if (slot.Date < date || (slot.Date == date && slot.End <= start))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private readonly record struct Slot(DateOnly Date, TimeOnly Start, TimeOnly End);
    private readonly record struct CompletedSlot(DateOnly Date, TimeOnly End);
}
