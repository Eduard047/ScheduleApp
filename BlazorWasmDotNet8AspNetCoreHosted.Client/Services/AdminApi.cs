using System.Linq;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Forms;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Client.Services
{
    // API-клієнт для адміністративних операцій
    public sealed class AdminApi(HttpClient http) : IAdminApi
    {
        public const long MaxDocxImportFileSizeBytes = 10 * 1024 * 1024;
        public const string MaxDocxImportFileSizeMessage = "Розмір DOCX-файлу перевищує дозволені 10 МБ";

        private readonly HttpClient _http = http;
        // Перевіряє відповідь і кидає виняток з повідомленням сервера.
        private static async Task Ensure(HttpResponseMessage resp)
        {
            await resp.EnsureSuccessWithDetailsAsync();
        }
        // Перевіряє відповідь операції без тіла та одразу звільняє її ресурси.
        private static async Task EnsureAndDispose(HttpResponseMessage resp)
        {
            using (resp)
            {
                await Ensure(resp);
            }
        }
        // Надсилає DTO на upsert-endpoint і повертає створений або оновлений id.
        private async Task<int> PostForId<T>(string url, T dto)
        {
            using var resp = await _http.PostAsJsonAsync(url, dto);
            await Ensure(resp);
            return (await resp.Content.ReadFromJsonAsync<int>())!;
        }
        // Отримує метадані для довідників клієнта.
        public async Task<MetaResponseDto> GetMeta()
            => await _http.GetFromJsonWithDetailsAsync<MetaResponseDto>("api/meta")
               ?? ApiClientHelpers.EmptyMeta();
        // Календар винятків (свята, перенесення).
        public async Task<List<CalendarExceptionEditDto>> GetCalendar()
            => await _http.GetFromJsonWithDetailsAsync<List<CalendarExceptionEditDto>>("api/admin/config/calendar") ?? new();
        // Створює або оновлює календарний виняток.
        public async Task<int> UpsertCalendar(CalendarExceptionEditDto dto)
            => await PostForId("api/admin/config/calendar/upsert", dto);
        // Видаляє календарний виняток.
        public async Task DeleteCalendar(int id)
            => await EnsureAndDispose(await _http.DeleteAsync(ApiClientHelpers.WithConfirm($"api/admin/config/calendar/{id}")));
        // Налаштування обідніх перерв.
        public async Task<List<LunchConfigEditDto>> GetLunch()
            => await _http.GetFromJsonWithDetailsAsync<List<LunchConfigEditDto>>("api/admin/config/lunch") ?? new();
        // Створює або оновлює обідню перерву.
        public async Task<int> UpsertLunch(LunchConfigEditDto dto)
            => await PostForId("api/admin/config/lunch/upsert", dto);
        // Видаляє обідню перерву.
        public async Task DeleteLunch(int id)
            => await EnsureAndDispose(await _http.DeleteAsync(ApiClientHelpers.WithConfirm($"api/admin/config/lunch/{id}")));
        // Довідник викладачів.
        public async Task<List<TeacherViewDto>> GetTeachers()
            => await _http.GetFromJsonWithDetailsAsync<List<TeacherViewDto>>("api/admin/teachers") ?? new();
        // Отримує викладача для редагування.
        public async Task<TeacherEditDto?> GetTeacher(int id)
            => await _http.GetFromJsonWithDetailsAsync<TeacherEditDto>($"api/admin/teachers/{id}");
        // Створює або оновлює викладача.
        public async Task<int> UpsertTeacher(TeacherEditDto dto)
            => await PostForId("api/admin/teachers/upsert", dto);
        // Видаляє викладача.
        public async Task DeleteTeacher(int id)
            => await EnsureAndDispose(await _http.DeleteAsync(ApiClientHelpers.WithConfirm($"api/admin/teachers/{id}")));
        // Довідник навчальних груп.
        public async Task<List<GroupEditDto>> GetGroups()
            => await _http.GetFromJsonWithDetailsAsync<List<GroupEditDto>>("api/admin/groups") ?? new();
        // Створює або оновлює групу.
        public async Task<int> UpsertGroup(GroupEditDto dto)
            => await PostForId("api/admin/groups/upsert", dto);
        // Видаляє групу з опційним force.
        public async Task DeleteGroup(int id, bool force = false)
        {
            var url = force
                ? ApiClientHelpers.WithConfirm($"api/admin/groups/{id}?force=true")
                : ApiClientHelpers.WithConfirm($"api/admin/groups/{id}");
            await EnsureAndDispose(await _http.DeleteAsync(url));
        }
        // Довідник модулів.
        public async Task<List<ModuleEditDto>> GetModules()
            => await _http.GetFromJsonWithDetailsAsync<List<ModuleEditDto>>("api/admin/modules") ?? new();
        // Створює або оновлює модуль.
        public async Task<int> UpsertModule(ModuleEditDto dto)
            => await PostForId("api/admin/modules/upsert", dto);
        // Видаляє модуль.
        public async Task DeleteModule(int id)
            => await EnsureAndDispose(await _http.DeleteAsync(ApiClientHelpers.WithConfirm($"api/admin/modules/{id}")));
        // Перетворює модуль на окремий екземпляр для конкретного курсу.
        public async Task<int> EnsureCourseScopedModule(int moduleId, int courseId)
        {
            using var resp = await _http.PostAsync($"api/admin/modules/{moduleId}/ensure-course-scope?courseId={courseId}", null);
            await Ensure(resp);
            return (await resp.Content.ReadFromJsonAsync<int>())!;
        }
        // Теми модуля для вибору в навчальних планах.
        public async Task<List<ModuleTopicViewDto>> GetModuleTopics(int moduleId)
            => await _http.GetFromJsonWithDetailsAsync<List<ModuleTopicViewDto>>($"api/admin/modules/{moduleId}/topics") ?? new();
        // Створює або оновлює тему модуля.
        public async Task<int> UpsertModuleTopic(int moduleId, ModuleTopicDto dto)
            => await PostForId($"api/admin/modules/{moduleId}/topics/upsert", dto);
        // Видаляє тему модуля.
        public async Task DeleteModuleTopic(int moduleId, int topicId)
            => await EnsureAndDispose(await _http.DeleteAsync(ApiClientHelpers.WithConfirm($"api/admin/modules/{moduleId}/topics/{topicId}")));
        // Довідник аудиторій.
        public async Task<List<RoomEditDto>> GetRooms()
            => await _http.GetFromJsonWithDetailsAsync<List<RoomEditDto>>("api/admin/rooms") ?? new();
        // Створює або оновлює аудиторію.
        public async Task<int> UpsertRoom(RoomEditDto dto)
            => await PostForId("api/admin/rooms/upsert", dto);
        // Видаляє аудиторію.
        public async Task DeleteRoom(int id)
            => await EnsureAndDispose(await _http.DeleteAsync(ApiClientHelpers.WithConfirm($"api/admin/rooms/{id}")));
        // Читає узгоджений знімок корпусів і переходів одним запитом без кешування.
        public async Task<BuildingCatalogDto> GetBuildingCatalog()
            => await _http.GetFromJsonWithDetailsAsync<BuildingCatalogDto>("api/admin/buildings") ?? new(new(), new());
        // Довідник будівель.
        public async Task<List<BuildingEditDto>> GetBuildings()
            => (await GetBuildingCatalog()).Buildings;
        // Налаштування переходів між будівлями.
        public async Task<List<BuildingTravelEditDto>> GetBuildingTravels()
            => (await GetBuildingCatalog()).Travels;
        // Створює або оновлює будівлю.
        public async Task<int> UpsertBuilding(BuildingEditDto dto)
            => await PostForId("api/admin/buildings/upsert", dto);
        // Видаляє будівлю.
        public async Task DeleteBuilding(int id)
            => await EnsureAndDispose(await _http.DeleteAsync(ApiClientHelpers.WithConfirm($"api/admin/buildings/{id}")));
        // Створює або оновлює маршрут між будівлями.
        public async Task UpsertBuildingTravel(BuildingTravelEditDto dto)
        {
            await EnsureAndDispose(await _http.PostAsJsonAsync("api/admin/buildings/travel/upsert", dto));
        }
        // Видаляє маршрут між будівлями.
        public async Task DeleteBuildingTravel(int fromId, int toId)
            => await EnsureAndDispose(await _http.PostAsJsonAsync(
                ApiClientHelpers.WithConfirm("api/admin/buildings/travel/delete"),
                new BuildingTravelEditDto(fromId, toId, 0)));
        // Довідник курсів.
        public async Task<List<CourseEditDto>> GetCourses()
            => await _http.GetFromJsonWithDetailsAsync<List<CourseEditDto>>("api/admin/courses") ?? new();
        // Створює або оновлює курс.
        public async Task<int> UpsertCourse(CourseEditDto dto)
            => await PostForId("api/admin/courses/upsert", dto);
        // Видаляє курс з опційним force.
        public async Task DeleteCourse(int id, bool force = false)
        {
            var url = force
                ? ApiClientHelpers.WithConfirm($"api/admin/courses/{id}?force=true")
                : ApiClientHelpers.WithConfirm($"api/admin/courses/{id}");
            await EnsureAndDispose(await _http.DeleteAsync(url));
        }
        // Довідник типів занять.
        public async Task<List<LessonTypeEditDto>> GetLessonTypes()
            => await _http.GetFromJsonWithDetailsAsync<List<LessonTypeEditDto>>("api/admin/types/lesson") ?? new();
        // Створює або оновлює тип заняття.
        public async Task UpsertLessonType(LessonTypeEditDto dto)
            => await EnsureAndDispose(await _http.PostAsJsonAsync("api/admin/types/lesson/upsert", dto));
        // Видаляє тип заняття.
        public async Task DeleteLessonType(int id)
            => await EnsureAndDispose(await _http.DeleteAsync(ApiClientHelpers.WithConfirm($"api/admin/types/lesson/{id}")));
        // Палітра кольорів для типів занять.
        public async Task<List<LessonColorDto>> GetLessonColorPalette()
            => await _http.GetFromJsonWithDetailsAsync<List<LessonColorDto>>("api/admin/types/lesson/palette") ?? new();
        // Планування модулів із можливим фільтром за курсом.
        public async Task<List<CourseModulePlanDto>> GetModulePlans(int moduleId, int? courseId = null)
        {
            var url = courseId is int cid && cid > 0
                ? $"api/admin/plans/module/{moduleId}?courseId={cid}"
                : $"api/admin/plans/module/{moduleId}";
            return await _http.GetFromJsonWithDetailsAsync<List<CourseModulePlanDto>>(url) ?? new();
        }
        // Зберігає плани модулів для курсу.
        public async Task UpsertModulePlans(int moduleId, int? courseId, List<SaveCourseModulePlanDto> rows)
        {
            var url = courseId is int cid && cid > 0
                ? $"api/admin/plans/module/{moduleId}/upsert?courseId={cid}"
                : $"api/admin/plans/module/{moduleId}/upsert";
            await EnsureAndDispose(await _http.PostAsJsonAsync(url, rows));
        }
        // Повертає план модуля для курсу з дефолтами.
        public async Task<CourseModulePlanDto> GetCourseModulePlan(int moduleId, int courseId)
        {
            var list = await GetModulePlans(moduleId, courseId);
            return list.FirstOrDefault() ?? new CourseModulePlanDto(
                CourseId: courseId, ModuleId: moduleId, TargetHours: 0, ScheduledHours: 0, IsActive: false);
        }
        // Зберігає план модуля для курсу.
        public async Task UpsertCourseModulePlan(int moduleId, int courseId, SaveCourseModulePlanDto dto)
            => await UpsertModulePlans(moduleId, courseId, new List<SaveCourseModulePlanDto> { dto });
        // Послідовність модулів у межах курсу.
        public async Task<ModuleSequenceConfigDto?> GetModuleSequence(int courseId)
            => await _http.GetFromJsonWithDetailsAsync<ModuleSequenceConfigDto>($"api/admin/module-sequence/{courseId}");
        // Зберігає послідовність модулів.
        public async Task SaveModuleSequence(ModuleSequenceSaveRequestDto dto)
            => await EnsureAndDispose(await _http.PostAsJsonAsync("api/admin/module-sequence/save", dto));
        // Імпорт модулів із DOCX з опційним застосуванням змін.
        public async Task<DocxImportResultDto> ImportModulesFromDocx(IBrowserFile file, bool apply, CancellationToken ct = default)
        {
            if (file.Size > MaxDocxImportFileSizeBytes)
            {
                throw new InvalidOperationException(MaxDocxImportFileSizeMessage);
            }

            var url = $"api/admin/modules/import-docx?apply={(apply ? "true" : "false")}";
            using var content = new MultipartFormDataContent();
            // Ліміт стріму щоб уникнути завантаження надвеликих файлів у пам’ять.
            await using var stream = file.OpenReadStream(MaxDocxImportFileSizeBytes, ct);
            var streamContent = new StreamContent(stream);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");
            content.Add(streamContent, "file", file.Name);
            using var resp = await _http.PostAsync(url, content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var msg = string.IsNullOrWhiteSpace(body) ? resp.ReasonPhrase : body;
                throw new HttpRequestException(msg ?? "Не вдалося імпортувати документ.", null, resp.StatusCode);
            }
            return await resp.Content.ReadFromJsonAsync<DocxImportResultDto>(cancellationToken: ct)
                   ?? new DocxImportResultDto(string.Empty, null, false, new(), new(), "Порожня відповідь сервера.");
        }
        // Повністю очищає модулі та їхні плани.
        public async Task ClearModulesAndPlans()
        {
            await EnsureAndDispose(await _http.PostAsync(
                ApiClientHelpers.WithConfirm("api/admin/modules/clear-all"),
                null));
        }
    }
}
