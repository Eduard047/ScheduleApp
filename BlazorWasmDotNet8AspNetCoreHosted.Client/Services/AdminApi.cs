using System.Linq;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Forms;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Client.Services
{
    // API-клієнт для адміністративних операцій
    public sealed class AdminApi(HttpClient http) : IAdminApi
    {
        private readonly HttpClient _http = http;
        // Додає confirm=true до URL для операцій із підтвердженням.
        private static string WithConfirm(string url)
            => url.Contains('?') ? $"{url}&confirm=true" : $"{url}?confirm=true";
        // Перевіряє відповідь і кидає виняток з повідомленням сервера.
        private static async Task Ensure(HttpResponseMessage resp)
        {
            if (resp.IsSuccessStatusCode) return;
            var body = await resp.Content.ReadAsStringAsync();
            var msg = string.IsNullOrWhiteSpace(body) ? resp.ReasonPhrase : body;
            throw new HttpRequestException(msg ?? "Request failed", null, resp.StatusCode);
        }
        // Отримує метадані для довідників клієнта.
        public async Task<MetaResponseDto> GetMeta()
            => await _http.GetFromJsonAsync<MetaResponseDto>("api/meta")
               ?? new MetaResponseDto(new(), new(), new(), new(), new(), new(), new());
        // Календар винятків (свята, перенесення).
        public async Task<List<CalendarExceptionEditDto>> GetCalendar()
            => await _http.GetFromJsonAsync<List<CalendarExceptionEditDto>>("api/admin/config/calendar") ?? new();
        // Створює або оновлює календарний виняток.
        public async Task<int> UpsertCalendar(CalendarExceptionEditDto dto)
        {
            var resp = await _http.PostAsJsonAsync("api/admin/config/calendar/upsert", dto);
            await Ensure(resp);
            return (await resp.Content.ReadFromJsonAsync<int>())!;
        }
        // Видаляє календарний виняток.
        public async Task DeleteCalendar(int id)
            => await Ensure(await _http.DeleteAsync(WithConfirm($"api/admin/config/calendar/{id}")));
        // Налаштування обідніх перерв.
        public async Task<List<LunchConfigEditDto>> GetLunch()
            => await _http.GetFromJsonAsync<List<LunchConfigEditDto>>("api/admin/config/lunch") ?? new();
        // Створює або оновлює обідню перерву.
        public async Task<int> UpsertLunch(LunchConfigEditDto dto)
        {
            var resp = await _http.PostAsJsonAsync("api/admin/config/lunch/upsert", dto);
            await Ensure(resp);
            return (await resp.Content.ReadFromJsonAsync<int>())!;
        }
        // Видаляє обідню перерву.
        public async Task DeleteLunch(int id)
            => await Ensure(await _http.DeleteAsync(WithConfirm($"api/admin/config/lunch/{id}")));
        // Довідник викладачів.
        public async Task<List<TeacherViewDto>> GetTeachers()
            => await _http.GetFromJsonAsync<List<TeacherViewDto>>("api/admin/teachers") ?? new();
        // Отримує викладача для редагування.
        public async Task<TeacherEditDto?> GetTeacher(int id)
            => await _http.GetFromJsonAsync<TeacherEditDto>($"api/admin/teachers/{id}");
        // Створює або оновлює викладача.
        public async Task<int> UpsertTeacher(TeacherEditDto dto)
        {
            var resp = await _http.PostAsJsonAsync("api/admin/teachers/upsert", dto);
            await Ensure(resp);
            return (await resp.Content.ReadFromJsonAsync<int>())!;
        }
        // Видаляє викладача.
        public async Task DeleteTeacher(int id)
            => await Ensure(await _http.DeleteAsync(WithConfirm($"api/admin/teachers/{id}")));
        // Довідник навчальних груп.
        public async Task<List<GroupEditDto>> GetGroups()
            => await _http.GetFromJsonAsync<List<GroupEditDto>>("api/admin/groups") ?? new();
        // Створює або оновлює групу.
        public async Task<int> UpsertGroup(GroupEditDto dto)
        {
            var resp = await _http.PostAsJsonAsync("api/admin/groups/upsert", dto);
            await Ensure(resp);
            return (await resp.Content.ReadFromJsonAsync<int>())!;
        }
        // Видаляє групу з опційним force.
        public async Task DeleteGroup(int id, bool force = false)
        {
            var url = force
                ? WithConfirm($"api/admin/groups/{id}?force=true")
                : WithConfirm($"api/admin/groups/{id}");
            await Ensure(await _http.DeleteAsync(url));
        }
        // Довідник модулів.
        public async Task<List<ModuleEditDto>> GetModules()
            => await _http.GetFromJsonAsync<List<ModuleEditDto>>("api/admin/modules") ?? new();
        // Створює або оновлює модуль.
        public async Task<int> UpsertModule(ModuleEditDto dto)
        {
            var resp = await _http.PostAsJsonAsync("api/admin/modules/upsert", dto);
            await Ensure(resp);
            return (await resp.Content.ReadFromJsonAsync<int>())!;
        }
        // Видаляє модуль.
        public async Task DeleteModule(int id)
            => await Ensure(await _http.DeleteAsync(WithConfirm($"api/admin/modules/{id}")));
        // Перетворює модуль на окремий екземпляр для конкретного курсу.
        public async Task<int> EnsureCourseScopedModule(int moduleId, int courseId)
        {
            var resp = await _http.PostAsync($"api/admin/modules/{moduleId}/ensure-course-scope?courseId={courseId}", null);
            await Ensure(resp);
            return (await resp.Content.ReadFromJsonAsync<int>())!;
        }
        // Теми модуля для вибору в навчальних планах.
        public async Task<List<ModuleTopicViewDto>> GetModuleTopics(int moduleId)
            => await _http.GetFromJsonAsync<List<ModuleTopicViewDto>>($"api/admin/modules/{moduleId}/topics") ?? new();
        // Створює або оновлює тему модуля.
        public async Task<int> UpsertModuleTopic(int moduleId, ModuleTopicDto dto)
        {
            var resp = await _http.PostAsJsonAsync($"api/admin/modules/{moduleId}/topics/upsert", dto);
            await Ensure(resp);
            return (await resp.Content.ReadFromJsonAsync<int>())!;
        }
        // Видаляє тему модуля.
        public async Task DeleteModuleTopic(int moduleId, int topicId)
            => await Ensure(await _http.DeleteAsync(WithConfirm($"api/admin/modules/{moduleId}/topics/{topicId}")));
        // Довідник аудиторій.
        public async Task<List<RoomEditDto>> GetRooms()
            => await _http.GetFromJsonAsync<List<RoomEditDto>>("api/admin/rooms") ?? new();
        // Створює або оновлює аудиторію.
        public async Task<int> UpsertRoom(RoomEditDto dto)
        {
            var resp = await _http.PostAsJsonAsync("api/admin/rooms/upsert", dto);
            await Ensure(resp);
            return (await resp.Content.ReadFromJsonAsync<int>())!;
        }
        // Видаляє аудиторію.
        public async Task DeleteRoom(int id)
            => await Ensure(await _http.DeleteAsync(WithConfirm($"api/admin/rooms/{id}")));
        // Комбінована модель для отримання будівель і переходів.
        private sealed record BuildingsVm(List<BuildingEditDto> buildings, List<BuildingTravelEditDto> travels);
        // Довідник будівель.
        public async Task<List<BuildingEditDto>> GetBuildings()
            => (await _http.GetFromJsonAsync<BuildingsVm>("api/admin/buildings") ?? new(new(), new())).buildings;
        // Налаштування переходів між будівлями.
        public async Task<List<BuildingTravelEditDto>> GetBuildingTravels()
            => (await _http.GetFromJsonAsync<BuildingsVm>("api/admin/buildings") ?? new(new(), new())).travels;
        // Створює або оновлює будівлю.
        public async Task<int> UpsertBuilding(BuildingEditDto dto)
        {
            var resp = await _http.PostAsJsonAsync("api/admin/buildings/upsert", dto);
            await Ensure(resp);
            return (await resp.Content.ReadFromJsonAsync<int>())!;
        }
        // Видаляє будівлю.
        public async Task DeleteBuilding(int id)
            => await Ensure(await _http.DeleteAsync(WithConfirm($"api/admin/buildings/{id}")));
        // Створює або оновлює маршрут між будівлями.
        public async Task UpsertBuildingTravel(BuildingTravelEditDto dto)
        {
            var resp = await _http.PostAsJsonAsync("api/admin/buildings/travel/upsert", dto);
            await Ensure(resp);
        }
        // Видаляє маршрут між будівлями.
        public async Task DeleteBuildingTravel(int fromId, int toId)
            => await Ensure(await _http.PostAsJsonAsync(
                WithConfirm("api/admin/buildings/travel/delete"),
                new BuildingTravelEditDto(fromId, toId, 0)));
        // Довідник курсів.
        public async Task<List<CourseEditDto>> GetCourses()
            => await _http.GetFromJsonAsync<List<CourseEditDto>>("api/admin/courses") ?? new();
        // Створює або оновлює курс.
        public async Task<int> UpsertCourse(CourseEditDto dto)
        {
            var resp = await _http.PostAsJsonAsync("api/admin/courses/upsert", dto);
            await Ensure(resp);
            return (await resp.Content.ReadFromJsonAsync<int>())!;
        }
        // Видаляє курс з опційним force.
        public async Task DeleteCourse(int id, bool force = false)
        {
            var url = force
                ? WithConfirm($"api/admin/courses/{id}?force=true")
                : WithConfirm($"api/admin/courses/{id}");
            await Ensure(await _http.DeleteAsync(url));
        }
        // Довідник типів занять.
        public async Task<List<LessonTypeEditDto>> GetLessonTypes()
            => await _http.GetFromJsonAsync<List<LessonTypeEditDto>>("api/admin/types/lesson") ?? new();
        // Створює або оновлює тип заняття.
        public async Task UpsertLessonType(LessonTypeEditDto dto)
            => await Ensure(await _http.PostAsJsonAsync("api/admin/types/lesson/upsert", dto));
        // Видаляє тип заняття.
        public async Task DeleteLessonType(int id)
            => await Ensure(await _http.DeleteAsync(WithConfirm($"api/admin/types/lesson/{id}")));
        // Палітра кольорів для типів занять.
        public async Task<List<LessonColorDto>> GetLessonColorPalette()
            => await _http.GetFromJsonAsync<List<LessonColorDto>>("api/admin/types/lesson/palette") ?? new();
        // Планування модулів із можливим фільтром за курсом.
        public async Task<List<CourseModulePlanDto>> GetModulePlans(int moduleId, int? courseId = null)
        {
            var url = courseId is int cid && cid > 0
                ? $"api/admin/plans/module/{moduleId}?courseId={cid}"
                : $"api/admin/plans/module/{moduleId}";
            return await _http.GetFromJsonAsync<List<CourseModulePlanDto>>(url) ?? new();
        }
        // Зберігає плани модулів для курсу.
        public async Task UpsertModulePlans(int moduleId, int? courseId, List<SaveCourseModulePlanDto> rows)
        {
            var url = courseId is int cid && cid > 0
                ? $"api/admin/plans/module/{moduleId}/upsert?courseId={cid}"
                : $"api/admin/plans/module/{moduleId}/upsert";
            await Ensure(await _http.PostAsJsonAsync(url, rows));
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
            => await _http.GetFromJsonAsync<ModuleSequenceConfigDto>($"api/admin/module-sequence/{courseId}");
        // Зберігає послідовність модулів.
        public async Task SaveModuleSequence(ModuleSequenceSaveRequestDto dto)
            => await Ensure(await _http.PostAsJsonAsync("api/admin/module-sequence/save", dto));
        // Імпорт модулів із DOCX з опційним застосуванням змін.
        public async Task<DocxImportResultDto> ImportModulesFromDocx(IBrowserFile file, bool apply, CancellationToken ct = default)
        {
            var url = $"api/admin/modules/import-docx?apply={(apply ? "true" : "false")}";
            var content = new MultipartFormDataContent();
            // Ліміт стріму щоб уникнути завантаження надвеликих файлів у пам’ять.
            var stream = file.OpenReadStream(50 * 1024 * 1024, ct);
            var streamContent = new StreamContent(stream);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");
            content.Add(streamContent, "file", file.Name);
            var resp = await _http.PostAsync(url, content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var msg = string.IsNullOrWhiteSpace(body) ? resp.ReasonPhrase : body;
                throw new HttpRequestException(msg ?? "Import failed", null, resp.StatusCode);
            }
            return await resp.Content.ReadFromJsonAsync<DocxImportResultDto>(cancellationToken: ct)
                   ?? new DocxImportResultDto(string.Empty, null, false, new(), new(), "Порожня відповідь сервера");
        }
        // Повністю очищає модулі та їхні плани.
        public async Task ClearModulesAndPlans()
        {
            var resp = await _http.PostAsync("api/admin/modules/clear-all", null);
            await Ensure(resp);
        }
    }
}
