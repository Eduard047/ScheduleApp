using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;

/// <summary>
/// Контекст даних, що інкапсулює доступ до сутностей розкладу.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Module> Modules => Set<Module>();
    public DbSet<Teacher> Teachers => Set<Teacher>();
    public DbSet<TeacherModule> TeacherModules => Set<TeacherModule>();
    public DbSet<ModuleSupervisor> ModuleSupervisors => Set<ModuleSupervisor>();

    public DbSet<LessonTypeRef> LessonTypes => Set<LessonTypeRef>();

    public DbSet<Building> Buildings => Set<Building>();
    public DbSet<BuildingTravel> BuildingTravels => Set<BuildingTravel>();

    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<ModuleRoom> ModuleRooms => Set<ModuleRoom>();
    public DbSet<ModuleBuilding> ModuleBuildings => Set<ModuleBuilding>();

    public DbSet<ModulePlan> ModulePlans => Set<ModulePlan>();
    public DbSet<ScheduleItem> ScheduleItems => Set<ScheduleItem>();

    public DbSet<TeacherCourseLoad> TeacherCourseLoads => Set<TeacherCourseLoad>();
    public DbSet<TeacherWorkingHour> TeacherWorkingHours => Set<TeacherWorkingHour>();

    public DbSet<ModuleTopic> ModuleTopics => Set<ModuleTopic>();
    public DbSet<ModuleCourse> ModuleCourses => Set<ModuleCourse>();

    public DbSet<LunchConfig> LunchConfigs => Set<LunchConfig>();
    public DbSet<CalendarException> CalendarExceptions => Set<CalendarException>();
    public DbSet<ModuleSequenceItem> ModuleSequenceItems => Set<ModuleSequenceItem>();
    public DbSet<ModuleFiller> ModuleFillers => Set<ModuleFiller>();
    public DbSet<TeacherDraftItem> TeacherDraftItems => Set<TeacherDraftItem>();
    public DbSet<TimeSlot> TimeSlots => Set<TimeSlot>();

    /// <summary>
    /// Налаштовує зв'язки, обмеження та індекси для сутностей.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder b)
    {
        // Фіксуємо таблицю довідника типів занять.
        b.Entity<LessonTypeRef>().ToTable("LessonTypes");

        // Створюємо складені ключі для таблиць зі зв'язками багато-до-багатьох.
        b.Entity<TeacherModule>().HasKey(x => new { x.TeacherId, x.ModuleId });
        b.Entity<ModuleSupervisor>().HasKey(x => new { x.TeacherId, x.ModuleId });
        b.Entity<ModuleRoom>().HasKey(x => new { x.ModuleId, x.RoomId });
        b.Entity<ModuleBuilding>().HasKey(x => new { x.ModuleId, x.BuildingId });
        b.Entity<ModuleCourse>().HasKey(x => new { x.ModuleId, x.CourseId });

        // Забороняємо каскадне видалення курсу при видаленні групи.
        b.Entity<Group>()
            .HasOne(g => g.Course)
            .WithMany(c => c.Groups)
            .HasForeignKey(g => g.CourseId)
            .OnDelete(DeleteBehavior.Restrict);

        // Забезпечуємо залежність модулів від курсу та налаштовуємо числові поля.
        b.Entity<Module>(e =>
        {
            e.HasOne(m => m.Course)
                .WithMany(c => c.Modules)
                .HasForeignKey(m => m.CourseId)
                .OnDelete(DeleteBehavior.Cascade);

            e.Property(m => m.Credits)
                .HasColumnType("decimal(6,2)")
                .HasDefaultValue(0m);
        });

        b.Entity<ModuleSupervisor>(e =>
        {
            e.HasOne(x => x.Module)
                .WithMany(m => m.ModuleSupervisors)
                .HasForeignKey(x => x.ModuleId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Teacher)
                .WithMany(t => t.ModuleSupervisions)
                .HasForeignKey(x => x.TeacherId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.ModuleId, x.TeacherId }).IsUnique();
        });

        // Визначаємо зв'язки між модулями та курсами з унікальністю комбінацій.
        b.Entity<ModuleCourse>(e =>
        {
            e.HasOne(x => x.Module)
                .WithMany(m => m.ModuleCourses)
                .HasForeignKey(x => x.ModuleId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Course)
                .WithMany(c => c.ModuleCourses)
                .HasForeignKey(x => x.CourseId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.CourseId, x.ModuleId }).IsUnique();
        });

        // Підтримуємо планові години модулів та запобігаємо дублюванню записів.
        b.Entity<ModulePlan>(e =>
        {
            e.HasKey(x => x.Id);

            e.HasOne(x => x.Course)
                .WithMany()
                .HasForeignKey(x => x.CourseId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Module)
                .WithMany()
                .HasForeignKey(x => x.ModuleId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.CourseId, x.ModuleId }).IsUnique();

            e.Property(x => x.TargetHours).HasDefaultValue(0);
            e.Property(x => x.ScheduledHours).HasDefaultValue(0);
        });

        // Прив'язуємо аудиторії до будівель.
        b.Entity<Room>()
            .HasOne(r => r.Building).WithMany().HasForeignKey(r => r.BuildingId);

        // Зберігаємо маршрути між будівлями та запобігаємо дублям.
        b.Entity<BuildingTravel>(e =>
        {
            e.HasIndex(x => new { x.FromBuildingId, x.ToBuildingId }).IsUnique();
            e.HasOne(x => x.From).WithMany().HasForeignKey(x => x.FromBuildingId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.To).WithMany().HasForeignKey(x => x.ToBuildingId).OnDelete(DeleteBehavior.Restrict);
        });

        // Обмежуємо графік роботи викладачів посиланням на сутності.
        b.Entity<TeacherWorkingHour>(e =>
        {
            e.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId);
        });

        // Зберігаємо навантаження викладача для курсу.
        b.Entity<TeacherCourseLoad>(e =>
        {
            e.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId);
            e.HasOne(x => x.Course).WithMany().HasForeignKey(x => x.CourseId);
        });

        // Детально описуємо позиції розкладу та їх залежності.
        b.Entity<ScheduleItem>(e =>
        {
            e.HasOne(si => si.Teacher).WithMany()
                .HasForeignKey(si => si.TeacherId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(si => si.Room).WithMany()
                .HasForeignKey(si => si.RoomId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(si => si.Group).WithMany()
                .HasForeignKey(si => si.GroupId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(si => si.Module).WithMany()
                .HasForeignKey(si => si.ModuleId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(si => si.LessonType).WithMany()
                .HasForeignKey(si => si.LessonTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(si => si.ModuleTopic).WithMany()
                .HasForeignKey(si => si.ModuleTopicId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => new { x.Date, x.GroupId });
            e.HasIndex(x => new { x.Date, x.TeacherId });
            e.HasIndex(x => new { x.Date, x.RoomId });

            e.Property(x => x.IsSelfStudy).HasDefaultValue(false);
        });

        // Унікалізуємо винятки у календарі за датою.
        b.Entity<CalendarException>(e =>
        {
            e.HasIndex(x => new { x.Date, x.CourseId, x.GroupId }).IsUnique();
            e.HasOne(x => x.Course)
                .WithMany()
                .HasForeignKey(x => x.CourseId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Group)
                .WithMany()
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Вказуємо послідовність модулів у курсі.
        b.Entity<ModuleSequenceItem>(e =>
        {
            e.HasOne(x => x.Course).WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Module).WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CourseId, x.ModuleId }).IsUnique();
            e.HasIndex(x => new { x.CourseId, x.Order }).IsUnique();
            e.Property(x => x.Order).HasDefaultValue(0);
        });

        // Фіксуємо наповнювачі модулів для курсу без дублювання.
        b.Entity<ModuleFiller>(e =>
        {
            e.HasOne(x => x.Course).WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Module).WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CourseId, x.ModuleId }).IsUnique();
        });

        // Додаємо налаштування для часових слотів.
        b.Entity<TimeSlot>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Course)
                .WithMany()
                .HasForeignKey(x => x.CourseId)
                .OnDelete(DeleteBehavior.Cascade);

            e.Property(x => x.SortOrder).HasDefaultValue(0);
            e.Property(x => x.IsActive).HasDefaultValue(true);

            e.HasIndex(x => new { x.CourseId, x.SortOrder }).IsUnique();
        });

        // Контролюємо чернетки викладачів і їхні залежності.
        b.Entity<TeacherDraftItem>(e =>
        {
            e.HasKey(x => x.Id);

            e.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Room).WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Module).WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.LessonType).WithMany().HasForeignKey(x => x.LessonTypeId).OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.ModuleTopic).WithMany().HasForeignKey(x => x.ModuleTopicId).OnDelete(DeleteBehavior.SetNull);

            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.BatchKey).HasMaxLength(64);
            e.Property(x => x.IsSelfStudy).HasDefaultValue(false);

            e.HasIndex(x => new { x.Date, x.GroupId });
            e.HasIndex(x => new { x.Date, x.TeacherId });
            e.HasIndex(x => new { x.Date, x.RoomId });
        });

        // Забезпечуємо роботу з тематичним наповненням модулів.
        b.Entity<ModuleTopic>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Module)
                .WithMany(m => m.Topics)
                .HasForeignKey(x => x.ModuleId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.LessonType)
                .WithMany()
                .HasForeignKey(x => x.LessonTypeId)
                .OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Order).HasDefaultValue(0);
            e.Property(x => x.TopicCode).HasMaxLength(64).IsRequired();
            e.Property(x => x.IsInterAssembly).HasDefaultValue(false);
            e.Property(x => x.SelfStudyBySupervisor).HasDefaultValue(false);
            e.HasIndex(x => new { x.ModuleId, x.Order }).IsUnique();
            e.HasIndex(x => new { x.ModuleId, x.TopicCode }).IsUnique();
        });
    }
}
