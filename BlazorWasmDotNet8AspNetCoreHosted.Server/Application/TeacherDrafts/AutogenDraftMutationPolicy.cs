using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public static class AutogenDraftMutationPolicy
{
    // Дозволяє неатомарний repair лише для звичайної незахищеної чернетки.
    public static bool CanMutateInRepair(TeacherDraftItem draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return draft.Status == DraftStatus.Draft
               && !draft.IsLocked
               && string.IsNullOrWhiteSpace(draft.BatchKey);
    }

    // Обмежує фінальну синхронізацію незахищеними чернетками поточного діапазону.
    public static bool CanSynchronizeMovedDraft(
        TeacherDraftItem draft,
        IReadOnlySet<int> selectedGroupIds,
        DateOnly rangeStart,
        DateOnly rangeEndExclusive)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(selectedGroupIds);

        return CanMutateInRepair(draft)
               && selectedGroupIds.Contains(draft.GroupId)
               && draft.Date >= rangeStart
               && draft.Date < rangeEndExclusive;
    }
}
