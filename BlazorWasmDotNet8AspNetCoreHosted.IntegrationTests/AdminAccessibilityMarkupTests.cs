namespace BlazorWasmDotNet8AspNetCoreHosted.IntegrationTests;

public sealed class AdminAccessibilityMarkupTests
{
    [Fact]
    public void Application_shell_exposes_accessible_loading_errors_and_skip_navigation()
    {
        var index = ReadClientFile("wwwroot/index.html");
        var layout = ReadClientFile("Layout/MainLayout.razor");
        var styles = ReadClientFile("wwwroot/css/app.css");

        Assert.Contains("role=\"status\" aria-live=\"polite\" aria-atomic=\"true\"", index);
        Assert.Contains("css/app.css?v=20260828-1", index);
        Assert.Contains("js/schedule-app.js?v=20260828-2", index);
        Assert.Contains("<span class=\"visually-hidden\">Завантаження застосунку…</span>", index);
        Assert.Contains("class=\"loading-progress\" aria-hidden=\"true\" focusable=\"false\"", index);
        Assert.Contains("id=\"blazor-error-ui\" role=\"alert\" aria-live=\"assertive\"", index);
        Assert.Contains("<button type=\"button\" class=\"dismiss\" aria-label=\"Закрити повідомлення\">", index);
        Assert.DoesNotContain("<a class=\"dismiss\"", index);
        Assert.Contains("class=\"skip-link\"", layout);
        Assert.Contains("href=\"@SkipLinkHref\"", layout);
        Assert.Contains("@onclick=\"FocusMainContentAsync\"", layout);
        Assert.Contains("@onclick:preventDefault=\"true\"", layout);
        Assert.Contains("return $\"{uriWithoutFragment}#main-content\";", layout);
        Assert.Contains("<main id=\"main-content\" class=\"@MainClass\" tabindex=\"-1\">", layout);
        Assert.Contains(".skip-link:focus", styles);
        var script = ReadClientFile("wwwroot/js/schedule-app.js");
        Assert.Contains("window.scheduleApp.focusMainContent", script);
        Assert.Contains("document.querySelector(\".app-navbar\")", script);
        Assert.Contains("window.scrollTo({ top: targetTop", script);
        Assert.Contains("history.replaceState(history.state", script);
        Assert.Contains("z-index: 1200", styles);
    }

    [Fact]
    public void Teachers_table_is_a_named_keyboard_scroll_region()
    {
        var markup = ReadAdminPage("AdminTeachers.razor");

        Assert.Contains(
            "<div class=\"table-responsive\" role=\"region\" aria-label=\"Список викладачів\" tabindex=\"0\">",
            markup);
        Assert.Contains("@if (listLoadFailed || metaLoadFailed)", markup);
        Assert.Contains("@RetryLoadLabel", markup);
    }

    [Fact]
    public void Schedule_log_toggles_expose_their_state_and_control_targets()
    {
        var markup = ReadAdminPage("AdminScheduleLogs.razor");

        Assert.Contains("aria-pressed=\"@IsActionSelected(filter.Code)\"", markup);
        Assert.Contains("aria-expanded=\"@allDetailsExpanded\"", markup);
        Assert.Contains("aria-controls=\"schedule-log-results\"", markup);
        Assert.Contains("id=\"schedule-log-results\"", markup);
        Assert.Contains("aria-expanded=\"@isExpanded\"", markup);
        Assert.Contains("aria-controls=\"@detailsId\"", markup);
        Assert.Contains("<div id=\"@detailsId\" class=\"log-details\">", markup);
        Assert.Contains("<div id=\"@detailsId\" hidden></div>", markup);
        Assert.DoesNotContain("_expandedAll", markup);
    }

    [Fact]
    public void Time_slot_editor_exposes_visual_sequence_and_keyboard_actions()
    {
        var markup = ReadAdminPage("AdminTimeSlots.razor");

        Assert.Contains("role=\"group\" aria-label=\"Область застосування графіка\"", markup);
        Assert.Contains("aria-pressed=\"@(_targetMode == TimeSlotEditorTargetMode.Course)\"", markup);
        Assert.Contains("aria-pressed=\"@(_targetMode == TimeSlotEditorTargetMode.AllCourses)\"", markup);
        Assert.DoesNotContain("role=\"radio\"", markup);
        Assert.DoesNotContain("aria-checked", markup);
        Assert.Contains("<ol class=\"day-timeline\" aria-label=\"Послідовність пар\">", markup);
        Assert.Contains("aria-label=\"Перемістити пару @(rowIndex + 1) вище\"", markup);
        Assert.Contains("aria-label=\"Перемістити пару @(rowIndex + 1) нижче\"", markup);
        Assert.Contains("aria-label=\"Вставити нову пару після пари @(rowIndex + 1)\"", markup);
        Assert.Contains("aria-label=\"Видалити пару @(rowIndex + 1)\"", markup);
        Assert.Contains("aria-live=\"polite\" aria-atomic=\"true\"", markup);
        Assert.Contains("<NavigationLock ConfirmExternalNavigation=\"@HasUnsavedChanges\"", markup);
        Assert.Contains("Прибрати спільну перерву", markup);
        Assert.Contains("Прибрати власну перерву й успадковувати спільну", markup);
        Assert.Contains("Прибрати виняток і повернути основний графік", markup);
        Assert.Contains("вимкніть «Використовувати в розкладі»", markup);
        Assert.Contains("disabled=\"@(!CanEditSequence || !row.IsActive || inheritedLunch)\"", markup);
        Assert.Contains("Успадковано; оберіть іншу активну пару, щоб змінити.", markup);
        Assert.Contains("Перевірити зміни", markup);
        Assert.Contains("Застосувати графік", markup);
        Assert.DoesNotContain("Перевірити й застосувати", markup);
        Assert.DoesNotContain("<table", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Груп", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("AdminBuildings.razor", 2)]
    [InlineData("AdminRooms.razor", 1)]
    [InlineData("AdminDepartments.razor", 1)]
    [InlineData("AdminGroups.razor", 1)]
    [InlineData("AdminCalendar.razor", 1)]
    public void Failed_post_mutation_refresh_can_be_retried_inside_open_modal(
        string fileName,
        int expectedModalCount)
    {
        var markup = ReadAdminPage(fileName);
        var modalBodies = markup
            .Split("<AdminEditorModal", StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(segment => segment[..segment.IndexOf("</AdminEditorModal>", StringComparison.Ordinal)])
            .ToList();

        Assert.Equal(expectedModalCount, modalBodies.Count);
        foreach (var modalBody in modalBodies)
        {
            var recoveryIndex = modalBody.IndexOf("@if (loadFailed)", StringComparison.Ordinal);
            var disabledFormIndex = modalBody.IndexOf(
                "<fieldset class=\"border-0 p-0 m-0 w-100\" disabled=\"@IsInteractionBlocked\">",
                StringComparison.Ordinal);

            Assert.True(recoveryIndex >= 0, $"{fileName}: у модальному редакторі немає recovery-блоку.");
            Assert.True(
                disabledFormIndex > recoveryIndex,
                $"{fileName}: recovery-кнопка має бути поза заблокованою формою.");
            Assert.Contains("@onclick=\"RetryLoad\"", modalBody);
            Assert.Contains("Повторити лише завантаження", modalBody);
            Assert.Contains("Busy=\"@(loading || mutationInProgress)\"", modalBody);
            Assert.DoesNotContain("Busy=\"@IsInteractionBlocked\"", modalBody);
        }
    }

    [Fact]
    public void Modules_recovery_keeps_both_modal_shells_closable()
    {
        var markup = ReadAdminPage("AdminModules.razor");
        var modalBodies = markup
            .Split("<AdminEditorModal", StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(segment => segment[..segment.IndexOf("</AdminEditorModal>", StringComparison.Ordinal)])
            .ToList();

        Assert.Equal(2, modalBodies.Count);
        foreach (var modalBody in modalBodies)
        {
            Assert.Contains("Busy=\"@IsModalBusy\"", modalBody);
            Assert.DoesNotContain("Busy=\"@IsPageInteractionBlocked\"", modalBody);
        }
    }

    // Шукає checkout від каталогу тестового процесу без локальних абсолютних шляхів.
    private static string ReadAdminPage(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "BlazorWasmDotNet8AspNetCoreHosted.Client",
                "Pages",
                fileName);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Не знайдено Razor-сторінку {fileName} від каталогу тестового процесу.");
    }

    // Читає файл клієнта від checkout без локальних абсолютних шляхів.
    private static string ReadClientFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "BlazorWasmDotNet8AspNetCoreHosted.Client",
                relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Не знайдено клієнтський файл {relativePath} від каталогу тестового процесу.");
    }
}
