namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// Мінімальний опис наявного рядка для планування синхронізації тем і співвикладачів.
public sealed record TeacherDraftEditExistingRow(
    int Id,
    int? ModuleTopicId,
    int? TeacherId);

// Бажаний рядок логічного заняття з ідентифікатором, який можна повторно використати.
public sealed record TeacherDraftEditTargetRow(
    int? ExistingId,
    int? ModuleTopicId,
    int? TeacherId);

// Чистий план атомарного оновлення логічного заняття.
public sealed record TeacherDraftEditMutationPlan(
    IReadOnlyList<TeacherDraftEditTargetRow> TargetRows,
    IReadOnlyList<int> DeleteIds);

// Цільове положення логічного заняття під час перетягування.
public sealed record TeacherDraftRelocationTarget(
    DateOnly Date,
    string TimeStart,
    string TimeEnd,
    int GroupId);

// Планує точну синхронізацію тем без накопичення повторних рядків.
public static class TeacherDraftEditMutationPlanner
{
    // Вибирає лише рядки того самого вихідного логічного заняття.
    public static IReadOnlyList<TeacherDraftItemDto> SelectLogicalEventRows(
        IEnumerable<TeacherDraftItemDto> candidates,
        TeacherDraftItemDto source)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(source.BatchKey))
        {
            return candidates
                .Where(candidate => candidate.Id == source.Id)
                .OrderBy(candidate => candidate.Id)
                .ToList();
        }

        return candidates
            .Where(candidate => string.Equals(candidate.BatchKey, source.BatchKey, StringComparison.Ordinal)
                                && HasSameEventSignature(candidate, source))
            .OrderBy(candidate => candidate.Id)
            .ToList();
    }

    // Перевіряє, що вихідний рядок не був перенесений до іншого логічного заняття паралельною зміною.
    public static bool HasSameOriginalIdentity(TeacherDraftItemDto original, TeacherDraftItemDto current)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(current);

        return original.Id == current.Id
               && original.Revision == current.Revision
               && string.Equals(original.BatchKey, current.BatchKey, StringComparison.Ordinal)
               && HasSameEventSignature(original, current);
    }

    // Переносить усі рядки логічного заняття, не втрачаючи теми, співвикладачів або ознаку самостійної роботи.
    public static IReadOnlyList<DraftUpsertRequest> BuildRelocationRequests(
        IReadOnlyCollection<TeacherDraftItemDto> logicalEventRows,
        TeacherDraftRelocationTarget target,
        bool ignoreValidationErrors)
    {
        ArgumentNullException.ThrowIfNull(logicalEventRows);
        ArgumentNullException.ThrowIfNull(target);
        if (logicalEventRows.Count == 0)
        {
            throw new ArgumentException("Логічне заняття має містити хоча б один рядок.", nameof(logicalEventRows));
        }
        if (logicalEventRows.Any(row => row.Id <= 0)
            || logicalEventRows.Select(row => row.Id).Distinct().Count() != logicalEventRows.Count)
        {
            throw new ArgumentException("Рядки логічного заняття повинні мати унікальні додатні ідентифікатори.", nameof(logicalEventRows));
        }
        if (target.GroupId <= 0
            || string.IsNullOrWhiteSpace(target.TimeStart)
            || string.IsNullOrWhiteSpace(target.TimeEnd))
        {
            throw new ArgumentException("Ціль перенесення має містити групу та часовий інтервал.", nameof(target));
        }

        return logicalEventRows
            .OrderBy(row => row.Id)
            .Select(row => new DraftUpsertRequest(
                Id: row.Id,
                Date: target.Date,
                TimeStart: target.TimeStart,
                TimeEnd: target.TimeEnd,
                GroupId: target.GroupId,
                ModuleId: row.ModuleId,
                ModuleTopicId: row.ModuleTopicId,
                TeacherId: row.TeacherId,
                RoomId: row.RoomId,
                RequiresRoom: row.RequiresRoom,
                LessonTypeId: row.LessonTypeId,
                OverrideNonWorkingDay: false,
                BatchKey: row.BatchKey,
                IsLocked: row.IsLocked,
                IgnoreValidationErrors: ignoreValidationErrors,
                IsSelfStudy: row.IsSelfStudy,
                ExpectedRevision: row.Revision))
            .ToList();
    }

    // Будує бажані комбінації «тема × викладач», повторно використовуючи лише відповідні рядки.
    public static TeacherDraftEditMutationPlan BuildPlan(
        IReadOnlyCollection<TeacherDraftEditExistingRow> existingRows,
        IReadOnlyList<int?> desiredTopicIds,
        int? originalPrimaryTeacherId,
        int? desiredPrimaryTeacherId)
    {
        ArgumentNullException.ThrowIfNull(existingRows);
        ArgumentNullException.ThrowIfNull(desiredTopicIds);

        var orderedExistingRows = existingRows
            .OrderBy(row => row.Id)
            .ToList();
        if (orderedExistingRows.Select(row => row.Id).Distinct().Count() != orderedExistingRows.Count)
        {
            throw new ArgumentException("Ідентифікатори наявних рядків мають бути унікальними.", nameof(existingRows));
        }

        var topics = desiredTopicIds.Count == 0
            ? new List<int?> { null }
            : desiredTopicIds
                .Select(topicId => topicId is > 0 ? topicId : null)
                .Distinct()
                .ToList();
        var teachers = BuildDesiredTeachers(
            orderedExistingRows,
            originalPrimaryTeacherId,
            desiredPrimaryTeacherId);
        var usedIds = new HashSet<int>();
        var targets = new List<TeacherDraftEditTargetRow>(topics.Count * teachers.Count);

        foreach (var topicId in topics)
        {
            foreach (var teacherId in teachers)
            {
                var reusable = FindUnusedRow(
                    orderedExistingRows,
                    usedIds,
                    row => row.ModuleTopicId == topicId && row.TeacherId == teacherId);
                if (reusable is null
                    && teacherId == desiredPrimaryTeacherId
                    && originalPrimaryTeacherId != desiredPrimaryTeacherId)
                {
                    reusable = FindUnusedRow(
                        orderedExistingRows,
                        usedIds,
                        row => row.ModuleTopicId == topicId && row.TeacherId == originalPrimaryTeacherId);
                }
                if (reusable is not null)
                {
                    usedIds.Add(reusable.Id);
                }
                targets.Add(new TeacherDraftEditTargetRow(reusable?.Id, topicId, teacherId));
            }
        }

        var deleteIds = orderedExistingRows
            .Where(row => !usedIds.Contains(row.Id))
            .Select(row => row.Id)
            .ToList();
        return new TeacherDraftEditMutationPlan(targets, deleteIds);
    }

    // Будує точний декартів добуток тем і викладачів для масового редагування цілого логічного заняття.
    public static TeacherDraftEditMutationPlan BuildReplacementPlan(
        IReadOnlyCollection<TeacherDraftEditExistingRow> existingRows,
        IReadOnlyList<int?> desiredTopicIds,
        IReadOnlyList<int?> desiredTeacherIds)
    {
        ArgumentNullException.ThrowIfNull(existingRows);
        ArgumentNullException.ThrowIfNull(desiredTopicIds);
        ArgumentNullException.ThrowIfNull(desiredTeacherIds);
        var orderedExistingRows = existingRows.OrderBy(row => row.Id).ToList();
        if (orderedExistingRows.Count == 0)
        {
            throw new ArgumentException("Логічне заняття має містити хоча б один наявний рядок.", nameof(existingRows));
        }
        if (orderedExistingRows.Select(row => row.Id).Distinct().Count() != orderedExistingRows.Count)
        {
            throw new ArgumentException("Ідентифікатори наявних рядків мають бути унікальними.", nameof(existingRows));
        }
        var topics = (desiredTopicIds.Count == 0 ? new int?[] { null } : desiredTopicIds)
            .Select(topicId => topicId is > 0 ? topicId : null)
            .Distinct()
            .ToList();
        var teachers = (desiredTeacherIds.Count == 0 ? new int?[] { null } : desiredTeacherIds)
            .Select(teacherId => teacherId is > 0 ? teacherId : null)
            .Distinct()
            .ToList();
        var usedIds = new HashSet<int>();
        var targets = new List<TeacherDraftEditTargetRow>(topics.Count * teachers.Count);
        foreach (var topicId in topics)
        {
            foreach (var teacherId in teachers)
            {
                var reusable = FindUnusedRow(
                    orderedExistingRows,
                    usedIds,
                    row => row.ModuleTopicId == topicId && row.TeacherId == teacherId);
                reusable ??= FindUnusedRow(orderedExistingRows, usedIds, _ => true);
                if (reusable is not null)
                {
                    usedIds.Add(reusable.Id);
                }
                targets.Add(new TeacherDraftEditTargetRow(reusable?.Id, topicId, teacherId));
            }
        }
        var deleteIds = orderedExistingRows
            .Where(row => !usedIds.Contains(row.Id))
            .Select(row => row.Id)
            .ToList();
        return new TeacherDraftEditMutationPlan(targets, deleteIds);
    }

    private static bool HasSameEventSignature(TeacherDraftItemDto left, TeacherDraftItemDto right)
        => left.Date == right.Date
           && string.Equals(left.TimeStart, right.TimeStart, StringComparison.Ordinal)
           && string.Equals(left.TimeEnd, right.TimeEnd, StringComparison.Ordinal)
           && left.GroupId == right.GroupId
           && left.ModuleId == right.ModuleId
           && left.LessonTypeId == right.LessonTypeId;

    private static List<int?> BuildDesiredTeachers(
        IReadOnlyList<TeacherDraftEditExistingRow> existingRows,
        int? originalPrimaryTeacherId,
        int? desiredPrimaryTeacherId)
    {
        var teachers = new List<int?> { desiredPrimaryTeacherId };
        foreach (var teacherId in existingRows.Select(row => row.TeacherId))
        {
            var desiredTeacherId = teacherId == originalPrimaryTeacherId
                ? desiredPrimaryTeacherId
                : teacherId;
            if (!teachers.Contains(desiredTeacherId))
            {
                teachers.Add(desiredTeacherId);
            }
        }
        return teachers;
    }

    private static TeacherDraftEditExistingRow? FindUnusedRow(
        IReadOnlyList<TeacherDraftEditExistingRow> existingRows,
        IReadOnlySet<int> usedIds,
        Func<TeacherDraftEditExistingRow, bool> predicate)
        => existingRows.FirstOrDefault(row => !usedIds.Contains(row.Id) && predicate(row));
}
