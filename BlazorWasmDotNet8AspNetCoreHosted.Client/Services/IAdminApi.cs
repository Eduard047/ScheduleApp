using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Components.Forms;

namespace BlazorWasmDotNet8AspNetCoreHosted.Client.Services
{
    // Контракт адміністративного API клієнта
    public interface IAdminApi
    {
        // Метадані для клієнта.
        Task<MetaResponseDto> GetMeta();
        // Календар винятків.
        Task<List<CalendarExceptionEditDto>> GetCalendar();
        Task<int> UpsertCalendar(CalendarExceptionEditDto dto);
        Task DeleteCalendar(int id);
        // Налаштування обіду.
        Task<List<LunchConfigEditDto>> GetLunch();
        Task<int> UpsertLunch(LunchConfigEditDto dto);
        Task DeleteLunch(int id);
        // Викладачі.
        Task<List<TeacherViewDto>> GetTeachers();
        Task<TeacherEditDto?> GetTeacher(int id);
        Task<int> UpsertTeacher(TeacherEditDto dto);
        Task DeleteTeacher(int id);
        // Групи.
        Task<List<GroupEditDto>> GetGroups();
        Task<int> UpsertGroup(GroupEditDto dto);
        Task DeleteGroup(int id, bool force = false);
        // Модулі та теми.
        Task<List<ModuleEditDto>> GetModules();
        Task<int> UpsertModule(ModuleEditDto dto);
        Task DeleteModule(int id);
        Task<List<ModuleTopicViewDto>> GetModuleTopics(int moduleId);
        Task<int> UpsertModuleTopic(int moduleId, ModuleTopicDto dto);
        Task DeleteModuleTopic(int moduleId, int topicId);
        // Аудиторії.
        Task<List<RoomEditDto>> GetRooms();
        Task<int> UpsertRoom(RoomEditDto dto);
        Task DeleteRoom(int id);
        // Корпуси та переходи.
        Task<List<BuildingEditDto>> GetBuildings();
        Task<List<BuildingTravelEditDto>> GetBuildingTravels();
        Task<int> UpsertBuilding(BuildingEditDto dto);
        Task DeleteBuilding(int id);
        Task UpsertBuildingTravel(BuildingTravelEditDto dto);
        Task DeleteBuildingTravel(int fromId, int toId);
        // Курси.
        Task<List<CourseEditDto>> GetCourses();
        Task<int> UpsertCourse(CourseEditDto dto);
        Task DeleteCourse(int id, bool force = false);
        // Типи занять.
        Task<List<LessonTypeEditDto>> GetLessonTypes();
        Task UpsertLessonType(LessonTypeEditDto dto);
        Task DeleteLessonType(int id);
        // Палітра кольорів типів занять.
        Task<List<LessonColorDto>> GetLessonColorPalette();
        // Плани модулів.
        Task<List<CourseModulePlanDto>> GetModulePlans(int moduleId, int? courseId = null);
        Task UpsertModulePlans(int moduleId, int? courseId, List<SaveCourseModulePlanDto> rows);
        Task<CourseModulePlanDto> GetCourseModulePlan(int moduleId, int courseId);
        Task UpsertCourseModulePlan(int moduleId, int courseId, SaveCourseModulePlanDto dto);
        // Послідовність модулів.
        Task<ModuleSequenceConfigDto?> GetModuleSequence(int courseId);
        Task SaveModuleSequence(ModuleSequenceSaveRequestDto dto);
        // Імпорт з DOCX та очищення модулів.
        Task<DocxImportResultDto> ImportModulesFromDocx(IBrowserFile file, bool apply, CancellationToken ct = default);
        Task ClearModulesAndPlans();
    }
}
