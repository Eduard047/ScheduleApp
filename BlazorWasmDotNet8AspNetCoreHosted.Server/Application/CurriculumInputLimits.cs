namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Application;

// Єдині безпечні межі для навчальних довідників незалежно від способу введення даних.
public static class CurriculumInputLimits
{
    public const int CodeMaxLength = 64;
    public const int ModuleTitleMaxLength = 512;
    public const int LessonTypeNameMaxLength = 200;
    public const int PlanHoursMax = 299999;
    public const int ModuleCreditsScale = 2;
    // Максимум із точністю БД до сотих, який після множення на 30 і округлення не перевищує PlanHoursMax.
    public const decimal ModuleCreditsMax = 9999.98m;
    public const int TopicHoursMax = PlanHoursMax;
    public const int ModuleAssociationCountMax = 500;
    public const int ImportModuleCountMax = 500;
    public const int ImportTopicCountMax = 5_000;
    // Одна таблиця модулів і до однієї таблиці тем для кожного дозволеного модуля.
    public const int ImportTableCountMax = ImportModuleCountMax + 1;

    public static bool HasSupportedModuleCreditScale(decimal credits)
        => decimal.Round(credits, ModuleCreditsScale) == credits;
}
