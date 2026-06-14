using System.Linq;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Forms;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Client.Services
{
    // API-РєР»С–С”РЅС‚ РґР»СЏ Р°РґРјС–РЅС–СЃС‚СЂР°С‚РёРІРЅРёС… РѕРїРµСЂР°С†С–Р№
    public sealed class AdminApi(HttpClient http) : IAdminApi
    {
        private readonly HttpClient _http = http;
        // РџРµСЂРµРІС–СЂСЏС” РІС–РґРїРѕРІС–РґСЊ С– РєРёРґР°С” РІРёРЅСЏС‚РѕРє Р· РїРѕРІС–РґРѕРјР»РµРЅРЅСЏРј СЃРµСЂРІРµСЂР°.
        private static async Task Ensure(HttpResponseMessage resp)
        {
            await resp.EnsureSuccessWithDetailsAsync();
        }
        // РќР°РґСЃРёР»Р°С” DTO РЅР° upsert-endpoint С– РїРѕРІРµСЂС‚Р°С” СЃС‚РІРѕСЂРµРЅРёР№ Р°Р±Рѕ РѕРЅРѕРІР»РµРЅРёР№ id.
        private async Task<int> PostForId<T>(string url, T dto)
        {
            var resp = await _http.PostAsJsonAsync(url, dto);
            await Ensure(resp);
            return (await resp.Content.ReadFromJsonAsync<int>())!;
        }
        // РћС‚СЂРёРјСѓС” РјРµС‚Р°РґР°РЅС– РґР»СЏ РґРѕРІС–РґРЅРёРєС–РІ РєР»С–С”РЅС‚Р°.
        public async Task<MetaResponseDto> GetMeta()
            => await _http.GetFromJsonAsync<MetaResponseDto>("api/meta")
               ?? ApiClientHelpers.EmptyMeta();
        // РљР°Р»РµРЅРґР°СЂ РІРёРЅСЏС‚РєС–РІ (СЃРІСЏС‚Р°, РїРµСЂРµРЅРµСЃРµРЅРЅСЏ).
        public async Task<List<CalendarExceptionEditDto>> GetCalendar()
            => await _http.GetFromJsonAsync<List<CalendarExceptionEditDto>>("api/admin/config/calendar") ?? new();
        // РЎС‚РІРѕСЂСЋС” Р°Р±Рѕ РѕРЅРѕРІР»СЋС” РєР°Р»РµРЅРґР°СЂРЅРёР№ РІРёРЅСЏС‚РѕРє.
        public async Task<int> UpsertCalendar(CalendarExceptionEditDto dto)
            => await PostForId("api/admin/config/calendar/upsert", dto);
        // Р’РёРґР°Р»СЏС” РєР°Р»РµРЅРґР°СЂРЅРёР№ РІРёРЅСЏС‚РѕРє.
        public async Task DeleteCalendar(int id)
            => await Ensure(await _http.DeleteAsync(ApiClientHelpers.WithConfirm($"api/admin/config/calendar/{id}")));
        // РќР°Р»Р°С€С‚СѓРІР°РЅРЅСЏ РѕР±С–РґРЅС–С… РїРµСЂРµСЂРІ.
        public async Task<List<LunchConfigEditDto>> GetLunch()
            => await _http.GetFromJsonAsync<List<LunchConfigEditDto>>("api/admin/config/lunch") ?? new();
        // РЎС‚РІРѕСЂСЋС” Р°Р±Рѕ РѕРЅРѕРІР»СЋС” РѕР±С–РґРЅСЋ РїРµСЂРµСЂРІСѓ.
        public async Task<int> UpsertLunch(LunchConfigEditDto dto)
            => await PostForId("api/admin/config/lunch/upsert", dto);
        // Р’РёРґР°Р»СЏС” РѕР±С–РґРЅСЋ РїРµСЂРµСЂРІСѓ.
        public async Task DeleteLunch(int id)
            => await Ensure(await _http.DeleteAsync(ApiClientHelpers.WithConfirm($"api/admin/config/lunch/{id}")));
        // Р”РѕРІС–РґРЅРёРє РІРёРєР»Р°РґР°С‡С–РІ.
        public async Task<List<TeacherViewDto>> GetTeachers()
            => await _http.GetFromJsonAsync<List<TeacherViewDto>>("api/admin/teachers") ?? new();
        // РћС‚СЂРёРјСѓС” РІРёРєР»Р°РґР°С‡Р° РґР»СЏ СЂРµРґР°РіСѓРІР°РЅРЅСЏ.
        public async Task<TeacherEditDto?> GetTeacher(int id)
            => await _http.GetFromJsonAsync<TeacherEditDto>($"api/admin/teachers/{id}");
        // РЎС‚РІРѕСЂСЋС” Р°Р±Рѕ РѕРЅРѕРІР»СЋС” РІРёРєР»Р°РґР°С‡Р°.
        public async Task<int> UpsertTeacher(TeacherEditDto dto)
            => await PostForId("api/admin/teachers/upsert", dto);
        // Р’РёРґР°Р»СЏС” РІРёРєР»Р°РґР°С‡Р°.
        public async Task DeleteTeacher(int id)
            => await Ensure(await _http.DeleteAsync(ApiClientHelpers.WithConfirm($"api/admin/teachers/{id}")));
        // Р”РѕРІС–РґРЅРёРє РЅР°РІС‡Р°Р»СЊРЅРёС… РіСЂСѓРї.
        public async Task<List<GroupEditDto>> GetGroups()
            => await _http.GetFromJsonAsync<List<GroupEditDto>>("api/admin/groups") ?? new();
        // РЎС‚РІРѕСЂСЋС” Р°Р±Рѕ РѕРЅРѕРІР»СЋС” РіСЂСѓРїСѓ.
        public async Task<int> UpsertGroup(GroupEditDto dto)
            => await PostForId("api/admin/groups/upsert", dto);
        // Р’РёРґР°Р»СЏС” РіСЂСѓРїСѓ Р· РѕРїС†С–Р№РЅРёРј force.
        public async Task DeleteGroup(int id, bool force = false)
        {
            var url = force
                ? ApiClientHelpers.WithConfirm($"api/admin/groups/{id}?force=true")
                : ApiClientHelpers.WithConfirm($"api/admin/groups/{id}");
            await Ensure(await _http.DeleteAsync(url));
        }
        // Р”РѕРІС–РґРЅРёРє РјРѕРґСѓР»С–РІ.
        public async Task<List<ModuleEditDto>> GetModules()
            => await _http.GetFromJsonAsync<List<ModuleEditDto>>("api/admin/modules") ?? new();
        // РЎС‚РІРѕСЂСЋС” Р°Р±Рѕ РѕРЅРѕРІР»СЋС” РјРѕРґСѓР»СЊ.
        public async Task<int> UpsertModule(ModuleEditDto dto)
            => await PostForId("api/admin/modules/upsert", dto);
        // Р’РёРґР°Р»СЏС” РјРѕРґСѓР»СЊ.
        public async Task DeleteModule(int id)
            => await Ensure(await _http.DeleteAsync(ApiClientHelpers.WithConfirm($"api/admin/modules/{id}")));
        // РџРµСЂРµС‚РІРѕСЂСЋС” РјРѕРґСѓР»СЊ РЅР° РѕРєСЂРµРјРёР№ РµРєР·РµРјРїР»СЏСЂ РґР»СЏ РєРѕРЅРєСЂРµС‚РЅРѕРіРѕ РєСѓСЂСЃСѓ.
        public async Task<int> EnsureCourseScopedModule(int moduleId, int courseId)
        {
            var resp = await _http.PostAsync($"api/admin/modules/{moduleId}/ensure-course-scope?courseId={courseId}", null);
            await Ensure(resp);
            return (await resp.Content.ReadFromJsonAsync<int>())!;
        }
        // РўРµРјРё РјРѕРґСѓР»СЏ РґР»СЏ РІРёР±РѕСЂСѓ РІ РЅР°РІС‡Р°Р»СЊРЅРёС… РїР»Р°РЅР°С….
        public async Task<List<ModuleTopicViewDto>> GetModuleTopics(int moduleId)
            => await _http.GetFromJsonAsync<List<ModuleTopicViewDto>>($"api/admin/modules/{moduleId}/topics") ?? new();
        // РЎС‚РІРѕСЂСЋС” Р°Р±Рѕ РѕРЅРѕРІР»СЋС” С‚РµРјСѓ РјРѕРґСѓР»СЏ.
        public async Task<int> UpsertModuleTopic(int moduleId, ModuleTopicDto dto)
            => await PostForId($"api/admin/modules/{moduleId}/topics/upsert", dto);
        // Р’РёРґР°Р»СЏС” С‚РµРјСѓ РјРѕРґСѓР»СЏ.
        public async Task DeleteModuleTopic(int moduleId, int topicId)
            => await Ensure(await _http.DeleteAsync(ApiClientHelpers.WithConfirm($"api/admin/modules/{moduleId}/topics/{topicId}")));
        // Р”РѕРІС–РґРЅРёРє Р°СѓРґРёС‚РѕСЂС–Р№.
        public async Task<List<RoomEditDto>> GetRooms()
            => await _http.GetFromJsonAsync<List<RoomEditDto>>("api/admin/rooms") ?? new();
        // РЎС‚РІРѕСЂСЋС” Р°Р±Рѕ РѕРЅРѕРІР»СЋС” Р°СѓРґРёС‚РѕСЂС–СЋ.
        public async Task<int> UpsertRoom(RoomEditDto dto)
            => await PostForId("api/admin/rooms/upsert", dto);
        // Р’РёРґР°Р»СЏС” Р°СѓРґРёС‚РѕСЂС–СЋ.
        public async Task DeleteRoom(int id)
            => await Ensure(await _http.DeleteAsync(ApiClientHelpers.WithConfirm($"api/admin/rooms/{id}")));
        // РљРѕРјР±С–РЅРѕРІР°РЅР° РјРѕРґРµР»СЊ РґР»СЏ РѕС‚СЂРёРјР°РЅРЅСЏ Р±СѓРґС–РІРµР»СЊ С– РїРµСЂРµС…РѕРґС–РІ.
        private sealed record BuildingsVm(List<BuildingEditDto> buildings, List<BuildingTravelEditDto> travels);
        // Р§РёС‚Р°С” РєРѕСЂРїСѓСЃРё С‚Р° РїРµСЂРµС…РѕРґРё РѕРґРЅРёРј Р·Р°РїРёС‚РѕРј Р±РµР· РєРµС€СѓРІР°РЅРЅСЏ.
        private async Task<BuildingsVm> GetBuildingsVm()
            => await _http.GetFromJsonAsync<BuildingsVm>("api/admin/buildings") ?? new(new(), new());
        // Р”РѕРІС–РґРЅРёРє Р±СѓРґС–РІРµР»СЊ.
        public async Task<List<BuildingEditDto>> GetBuildings()
            => (await GetBuildingsVm()).buildings;
        // РќР°Р»Р°С€С‚СѓРІР°РЅРЅСЏ РїРµСЂРµС…РѕРґС–РІ РјС–Р¶ Р±СѓРґС–РІР»СЏРјРё.
        public async Task<List<BuildingTravelEditDto>> GetBuildingTravels()
            => (await GetBuildingsVm()).travels;
        // РЎС‚РІРѕСЂСЋС” Р°Р±Рѕ РѕРЅРѕРІР»СЋС” Р±СѓРґС–РІР»СЋ.
        public async Task<int> UpsertBuilding(BuildingEditDto dto)
            => await PostForId("api/admin/buildings/upsert", dto);
        // Р’РёРґР°Р»СЏС” Р±СѓРґС–РІР»СЋ.
        public async Task DeleteBuilding(int id)
            => await Ensure(await _http.DeleteAsync(ApiClientHelpers.WithConfirm($"api/admin/buildings/{id}")));
        // РЎС‚РІРѕСЂСЋС” Р°Р±Рѕ РѕРЅРѕРІР»СЋС” РјР°СЂС€СЂСѓС‚ РјС–Р¶ Р±СѓРґС–РІР»СЏРјРё.
        public async Task UpsertBuildingTravel(BuildingTravelEditDto dto)
        {
            var resp = await _http.PostAsJsonAsync("api/admin/buildings/travel/upsert", dto);
            await Ensure(resp);
        }
        // Р’РёРґР°Р»СЏС” РјР°СЂС€СЂСѓС‚ РјС–Р¶ Р±СѓРґС–РІР»СЏРјРё.
        public async Task DeleteBuildingTravel(int fromId, int toId)
            => await Ensure(await _http.PostAsJsonAsync(
                ApiClientHelpers.WithConfirm("api/admin/buildings/travel/delete"),
                new BuildingTravelEditDto(fromId, toId, 0)));
        // Р”РѕРІС–РґРЅРёРє РєСѓСЂСЃС–РІ.
        public async Task<List<CourseEditDto>> GetCourses()
            => await _http.GetFromJsonAsync<List<CourseEditDto>>("api/admin/courses") ?? new();
        // РЎС‚РІРѕСЂСЋС” Р°Р±Рѕ РѕРЅРѕРІР»СЋС” РєСѓСЂСЃ.
        public async Task<int> UpsertCourse(CourseEditDto dto)
            => await PostForId("api/admin/courses/upsert", dto);
        // Р’РёРґР°Р»СЏС” РєСѓСЂСЃ Р· РѕРїС†С–Р№РЅРёРј force.
        public async Task DeleteCourse(int id, bool force = false)
        {
            var url = force
                ? ApiClientHelpers.WithConfirm($"api/admin/courses/{id}?force=true")
                : ApiClientHelpers.WithConfirm($"api/admin/courses/{id}");
            await Ensure(await _http.DeleteAsync(url));
        }
        // Р”РѕРІС–РґРЅРёРє С‚РёРїС–РІ Р·Р°РЅСЏС‚СЊ.
        public async Task<List<LessonTypeEditDto>> GetLessonTypes()
            => await _http.GetFromJsonAsync<List<LessonTypeEditDto>>("api/admin/types/lesson") ?? new();
        // РЎС‚РІРѕСЂСЋС” Р°Р±Рѕ РѕРЅРѕРІР»СЋС” С‚РёРї Р·Р°РЅСЏС‚С‚СЏ.
        public async Task UpsertLessonType(LessonTypeEditDto dto)
            => await Ensure(await _http.PostAsJsonAsync("api/admin/types/lesson/upsert", dto));
        // Р’РёРґР°Р»СЏС” С‚РёРї Р·Р°РЅСЏС‚С‚СЏ.
        public async Task DeleteLessonType(int id)
            => await Ensure(await _http.DeleteAsync(ApiClientHelpers.WithConfirm($"api/admin/types/lesson/{id}")));
        // РџР°Р»С–С‚СЂР° РєРѕР»СЊРѕСЂС–РІ РґР»СЏ С‚РёРїС–РІ Р·Р°РЅСЏС‚СЊ.
        public async Task<List<LessonColorDto>> GetLessonColorPalette()
            => await _http.GetFromJsonAsync<List<LessonColorDto>>("api/admin/types/lesson/palette") ?? new();
        // РџР»Р°РЅСѓРІР°РЅРЅСЏ РјРѕРґСѓР»С–РІ С–Р· РјРѕР¶Р»РёРІРёРј С„С–Р»СЊС‚СЂРѕРј Р·Р° РєСѓСЂСЃРѕРј.
        public async Task<List<CourseModulePlanDto>> GetModulePlans(int moduleId, int? courseId = null)
        {
            var url = courseId is int cid && cid > 0
                ? $"api/admin/plans/module/{moduleId}?courseId={cid}"
                : $"api/admin/plans/module/{moduleId}";
            return await _http.GetFromJsonAsync<List<CourseModulePlanDto>>(url) ?? new();
        }
        // Р—Р±РµСЂС–РіР°С” РїР»Р°РЅРё РјРѕРґСѓР»С–РІ РґР»СЏ РєСѓСЂСЃСѓ.
        public async Task UpsertModulePlans(int moduleId, int? courseId, List<SaveCourseModulePlanDto> rows)
        {
            var url = courseId is int cid && cid > 0
                ? $"api/admin/plans/module/{moduleId}/upsert?courseId={cid}"
                : $"api/admin/plans/module/{moduleId}/upsert";
            await Ensure(await _http.PostAsJsonAsync(url, rows));
        }
        // РџРѕРІРµСЂС‚Р°С” РїР»Р°РЅ РјРѕРґСѓР»СЏ РґР»СЏ РєСѓСЂСЃСѓ Р· РґРµС„РѕР»С‚Р°РјРё.
        public async Task<CourseModulePlanDto> GetCourseModulePlan(int moduleId, int courseId)
        {
            var list = await GetModulePlans(moduleId, courseId);
            return list.FirstOrDefault() ?? new CourseModulePlanDto(
                CourseId: courseId, ModuleId: moduleId, TargetHours: 0, ScheduledHours: 0, IsActive: false);
        }
        // Р—Р±РµСЂС–РіР°С” РїР»Р°РЅ РјРѕРґСѓР»СЏ РґР»СЏ РєСѓСЂСЃСѓ.
        public async Task UpsertCourseModulePlan(int moduleId, int courseId, SaveCourseModulePlanDto dto)
            => await UpsertModulePlans(moduleId, courseId, new List<SaveCourseModulePlanDto> { dto });
        // РџРѕСЃР»С–РґРѕРІРЅС–СЃС‚СЊ РјРѕРґСѓР»С–РІ Сѓ РјРµР¶Р°С… РєСѓСЂСЃСѓ.
        public async Task<ModuleSequenceConfigDto?> GetModuleSequence(int courseId)
            => await _http.GetFromJsonAsync<ModuleSequenceConfigDto>($"api/admin/module-sequence/{courseId}");
        // Р—Р±РµСЂС–РіР°С” РїРѕСЃР»С–РґРѕРІРЅС–СЃС‚СЊ РјРѕРґСѓР»С–РІ.
        public async Task SaveModuleSequence(ModuleSequenceSaveRequestDto dto)
            => await Ensure(await _http.PostAsJsonAsync("api/admin/module-sequence/save", dto));
        // Р†РјРїРѕСЂС‚ РјРѕРґСѓР»С–РІ С–Р· DOCX Р· РѕРїС†С–Р№РЅРёРј Р·Р°СЃС‚РѕСЃСѓРІР°РЅРЅСЏРј Р·РјС–РЅ.
        public async Task<DocxImportResultDto> ImportModulesFromDocx(IBrowserFile file, bool apply, CancellationToken ct = default)
        {
            var url = $"api/admin/modules/import-docx?apply={(apply ? "true" : "false")}";
            var content = new MultipartFormDataContent();
            // Р›С–РјС–С‚ СЃС‚СЂС–РјСѓ С‰РѕР± СѓРЅРёРєРЅСѓС‚Рё Р·Р°РІР°РЅС‚Р°Р¶РµРЅРЅСЏ РЅР°РґРІРµР»РёРєРёС… С„Р°Р№Р»С–РІ Сѓ РїР°РјвЂ™СЏС‚СЊ.
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
                   ?? new DocxImportResultDto(string.Empty, null, false, new(), new(), "РџРѕСЂРѕР¶РЅСЏ РІС–РґРїРѕРІС–РґСЊ СЃРµСЂРІРµСЂР°");
        }
        // РџРѕРІРЅС–СЃС‚СЋ РѕС‡РёС‰Р°С” РјРѕРґСѓР»С– С‚Р° С—С…РЅС– РїР»Р°РЅРё.
        public async Task ClearModulesAndPlans()
        {
            var resp = await _http.PostAsync("api/admin/modules/clear-all", null);
            await Ensure(resp);
        }
    }
}
