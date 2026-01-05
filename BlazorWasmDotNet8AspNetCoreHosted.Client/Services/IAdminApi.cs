using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Components.Forms;

namespace BlazorWasmDotNet8AspNetCoreHosted.Client.Services
{
    // Контракт адміністративного API клієнта
    public interface IAdminApi
    {
        
        Task<MetaResponseDto> GetMeta();

        Task<List<CalendarExceptionEditDto>> GetCalendar();
        Task<int> UpsertCalendar(CalendarExceptionEditDto dto);
        Task DeleteCalendar(int id);

        
        Task<List<LunchConfigEditDto>> GetLunch();
        Task<int> UpsertLunch(LunchConfigEditDto dto);
        Task DeleteLunch(int id);

        
        Task<List<TeacherViewDto>> GetTeachers();
        Task<TeacherEditDto?> GetTeacher(int id);
        Task<int> UpsertTeacher(TeacherEditDto dto);
        Task DeleteTeacher(int id);

        
        Task<List<GroupEditDto>> GetGroups();
        Task<int> UpsertGroup(GroupEditDto dto);
        Task DeleteGroup(int id, bool force = false);

        
        Task<List<ModuleEditDto>> GetModules();
        Task<int> UpsertModule(ModuleEditDto dto);
        Task DeleteModule(int id);
        
        Task<List<ModuleTopicViewDto>> GetModuleTopics(int moduleId);
        Task<int> UpsertModuleTopic(int moduleId, ModuleTopicDto dto);
        Task DeleteModuleTopic(int moduleId, int topicId);

        
        Task<List<RoomEditDto>> GetRooms();
        Task<int> UpsertRoom(RoomEditDto dto);
        Task DeleteRoom(int id);

        
        Task<List<BuildingEditDto>> GetBuildings();
        Task<List<BuildingTravelEditDto>> GetBuildingTravels();
        Task<int> UpsertBuilding(BuildingEditDto dto);
        Task DeleteBuilding(int id);
        Task UpsertBuildingTravel(BuildingTravelEditDto dto);
        Task DeleteBuildingTravel(int fromId, int toId);

        
        Task<List<CourseEditDto>> GetCourses();
        Task<int> UpsertCourse(CourseEditDto dto);
        Task DeleteCourse(int id, bool force = false);

        
        Task<List<LessonTypeEditDto>> GetLessonTypes();
        Task UpsertLessonType(LessonTypeEditDto dto);
        Task DeleteLessonType(int id);

        
        Task<List<LessonColorDto>> GetLessonColorPalette();


        
        Task<List<CourseModulePlanDto>> GetModulePlans(int moduleId, int? courseId = null);
        Task UpsertModulePlans(int moduleId, int? courseId, List<SaveCourseModulePlanDto> rows);
        Task<CourseModulePlanDto> GetCourseModulePlan(int moduleId, int courseId);
        Task UpsertCourseModulePlan(int moduleId, int courseId, SaveCourseModulePlanDto dto);


        Task<ModuleSequenceConfigDto?> GetModuleSequence(int courseId);
        Task SaveModuleSequence(ModuleSequenceSaveRequestDto dto);

        Task<DocxImportResultDto> ImportModulesFromDocx(IBrowserFile file, bool apply, CancellationToken ct = default);
        Task ClearModulesAndPlans();
    }
}


