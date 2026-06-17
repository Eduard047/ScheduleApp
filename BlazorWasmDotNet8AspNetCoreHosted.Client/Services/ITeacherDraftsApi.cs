using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// Контракт клієнта для роботи з чернетками викладачів
public interface ITeacherDraftsApi
{
    // Завантажує чернетки тижня.
    Task<List<TeacherDraftItemDto>> GetWeek(DateOnly weekStart, int? teacherId);
    // Експортує чернетки у файл.
    Task<byte[]> ExportWeek(DateOnly weekStart, int? teacherId, int? groupId, int? roomId);
    // Створює або оновлює чернетку.
    Task<int> Upsert(DraftUpsertRequest req);
    // Видаляє чернетку з підтвердженням.
    Task Delete(int id, bool confirm = false, bool unrestricted = false);
    // Автогенерація на тиждень.
    Task<AutoGenResult> AutogenWeek(AutoGenRequest req);
    // Попередня перевірка ресурсів автогенерації без створення чернеток.
    Task<AutoGenResult> AutogenPreflightWeek(AutoGenRequest req);
    Task<AutoGenJobStartResult> StartAutogenJob(AutoGenJobRequest req);
    Task<AutoGenJobStatus> GetAutogenJob(string jobId);
    Task<AutoGenJobStatus> CancelAutogenJob(string jobId);
    // Очищає чернетки тижня.
    Task<int> ClearWeek(ClearWeekRequest req);
    // Автогенерація на місяць.
    Task<AutoGenResult> AutogenMonth(AutogenMonthRequest req);
    // Автогенерація для курсу за діапазон.
    Task<AutoGenResult> AutogenCourse(AutogenCourseRequest req);
    // Публікація чернеток тижня.
    Task PublishWeek(PublishWeekRequest req);
}
