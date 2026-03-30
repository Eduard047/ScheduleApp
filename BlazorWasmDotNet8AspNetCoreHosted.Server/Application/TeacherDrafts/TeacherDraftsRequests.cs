using System;
using System.Collections.Generic;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed record DraftAutoGenRequest(
    DateOnly WeekStart, // Дата старту тижня для генерації.
    bool ClearExisting = true, // Очищати існуючі чернетки перед генерацією.
    int? CourseId = null, // Фільтр за курсом.
    int? GroupId = null, // Фільтр за групою.
    List<int>? GroupIds = null, // Фільтр за переліком груп.
    int? TeacherId = null, // Фільтр за викладачем.
    bool AllowOnDaysOff = false, // Дозволяти генерацію у вихідні.
    WeekPreset Days = WeekPreset.MonFri, // Набір днів для генерації.
    Dictionary<int, int>? ModuleHours = null, // Ручні години по модулях (модуль -> години).
    bool SoftFill = false, // Дозволити заповнення за м'якими правилами.
    DateOnly? RangeStartDate = null, // Опційний початок діапазону в межах тижня.
    DateOnly? RangeEndDate = null // Опційне завершення діапазону в межах тижня.
);

public sealed record ApproveWeekRequest(
    DateOnly WeekStart, // Дата старту тижня.
    int TeacherId // Ідентифікатор викладача.
);

public sealed record PublishWeekResults(
    int Created, // Кількість створених записів.
    int Skipped, // Кількість пропущених записів.
    List<string> Warnings // Попередження під час публікації.
);
