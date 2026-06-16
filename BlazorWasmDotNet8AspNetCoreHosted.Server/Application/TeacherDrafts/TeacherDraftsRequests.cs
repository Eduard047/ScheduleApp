using System;
using System.Collections.Generic;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application.TeacherDrafts;

public sealed record DraftAutoGenSoftOptions(
    int? MaxParallelGroupsPerModuleInSlot = null, // М'який ліміт паралельних груп одного модуля в тому самому слоті.
    int? RecentRepeatWindowDays = null, // Розмір вікна днів для близьких повторів модуля.
    int? PreferredMaxDistinctModulesPerDay = null, // Бажана кількість різних модулів у межах дня.
    int? MaxDistinctModulesPerDay = null, // Максимальна кількість різних модулів у межах дня.
    double? PreferredFirstPenaltyMultiplier = null, // Множник суворості для евристики "бажано першим у тижні".
    double? AdjacentRoomChangePenalty = null, // Штраф за зміну аудиторії в суміжному блоці одного модуля.
    double? TeacherLoadPenaltyWeight = null, // Вага штрафу за загальне навантаження викладача.
    double? BuildingDistancePenaltyWeight = null // Вага м'якого штрафу за зміну корпусу.
);

public sealed record DraftAutoGenRequest(
    DateOnly WeekStart, // Дата старту тижня для генерації.
    bool ClearExisting = true, // Очищати наявні чернетки перед генерацією.
    int? CourseId = null, // Фільтр за курсом.
    int? GroupId = null, // Фільтр за групою.
    List<int>? GroupIds = null, // Фільтр за переліком груп.
    int? TeacherId = null, // Фільтр за викладачем.
    bool AllowOnDaysOff = false, // Дозволяти генерацію у вихідні.
    WeekPreset Days = WeekPreset.MonFri, // Набір днів для генерації.
    Dictionary<int, int>? ModuleHours = null, // Ручні години по модулях.
    bool SoftFill = false, // Дозволити заповнення за м'якими правилами.
    bool AllowIncompleteDrafts = false, // Дозволити створювати чернетки без викладача/аудиторії, якщо повний варіант не знайдено.
    DateOnly? RangeStartDate = null, // Опційний початок діапазону в межах тижня.
    DateOnly? RangeEndDate = null, // Опційне завершення діапазону в межах тижня.
    int? PreferredFirstMaxSlotOrderOverride = null, // Явний override ліміту для типу "бажано першим у тижні"; 0 вимикає збережений ліміт.
    List<GroupRoomPreferenceDto>? GroupRoomPreferences = null, // Пріоритетні корпуси та аудиторії для конкретних груп.
    DraftAutoGenSoftOptions? SoftOptions = null, // Додаткові м'які параметри для дослідного пошуку профілів.
    bool PreflightOnly = false // Лише перевірити ресурси без збереження чернеток.
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
