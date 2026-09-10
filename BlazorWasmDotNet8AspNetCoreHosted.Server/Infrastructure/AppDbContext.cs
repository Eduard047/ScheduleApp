using Microsoft.EntityFrameworkCore;
using BlazorWasmDotNet8AspNetCoreHosted.Server.Domain.Entities;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Infrastructure;

// Контекст даних, що інкапсулює доступ до сутностей розкладу.
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    // Базові довідники та структури навчального процесу.
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Module> Modules => Set<Module>();
    public DbSet<Department> Departments => Set<Department>();
    // Викладачі та їх зв'язки з модулями.
    public DbSet<Teacher> Teachers => Set<Teacher>();
    public DbSet<TeacherModule> TeacherModules => Set<TeacherModule>();
    public DbSet<ModuleSupervisor> ModuleSupervisors => Set<ModuleSupervisor>();
    // Довідник типів занять.
    public DbSet<LessonTypeRef> LessonTypes => Set<LessonTypeRef>();
    // Інфраструктура: будівлі та маршрути між ними.
    public DbSet<Building> Buildings => Set<Building>();
    public DbSet<BuildingTravel> BuildingTravels => Set<BuildingTravel>();
    // Аудиторії та їх відповідність модулям.
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<ModuleRoom> ModuleRooms => Set<ModuleRoom>();
    public DbSet<ModuleBuilding> ModuleBuildings => Set<ModuleBuilding>();
    // Планування навчального процесу та розклад.
    public DbSet<ModulePlan> ModulePlans => Set<ModulePlan>();
    public DbSet<ScheduleItem> ScheduleItems => Set<ScheduleItem>();
    // Навантаження та робочі години викладачів.
    public DbSet<TeacherCourseLoad> TeacherCourseLoads => Set<TeacherCourseLoad>();
    public DbSet<TeacherWorkingHour> TeacherWorkingHours => Set<TeacherWorkingHour>();
    // Теми модулів та їх зв'язки з курсами.
    public DbSet<ModuleTopic> ModuleTopics => Set<ModuleTopic>();
    public DbSet<ModuleCourse> ModuleCourses => Set<ModuleCourse>();
    // Додаткові конфігурації та чернетки викладачів.
    public DbSet<LunchConfig> LunchConfigs => Set<LunchConfig>();
    public DbSet<PreferredFirstSlotLimitConfig> PreferredFirstSlotLimitConfigs => Set<PreferredFirstSlotLimitConfig>();
    public DbSet<CalendarException> CalendarExceptions => Set<CalendarException>();
    public DbSet<ModuleSequenceItem> ModuleSequenceItems => Set<ModuleSequenceItem>();
    public DbSet<ModuleFiller> ModuleFillers => Set<ModuleFiller>();
    public DbSet<TeacherDraftItem> TeacherDraftItems => Set<TeacherDraftItem>();
    public DbSet<TimeSlot> TimeSlots => Set<TimeSlot>();
    public DbSet<AutoGenJobRun> AutoGenJobRuns => Set<AutoGenJobRun>();
    public DbSet<AutoGenDraftPlan> AutoGenDraftPlans => Set<AutoGenDraftPlan>();
    public DbSet<AutoGenDraftPlanMutation> AutoGenDraftPlanMutations => Set<AutoGenDraftPlanMutation>();
    // Налаштовує зв'язки, обмеження та індекси для сутностей.
    protected override void OnModelCreating(ModelBuilder b)
    {
        // Фіксуємо таблицю довідника типів занять та унікальність системних кодів.
        b.Entity<LessonTypeRef>(e =>
        {
            e.ToTable("LessonTypes");
            e.Property(x => x.Code).HasMaxLength(64).IsRequired();
            e.Property(x => x.CssKey).HasMaxLength(32);
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.CssKey).IsUnique();
        });
        // Довідник кафедр.
        b.Entity<Department>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.HasIndex(x => x.Name).IsUnique();
        });
        // Створюємо складені ключі для таблиць зі зв'язками багато-до-багатьох.
        b.Entity<TeacherModule>().HasKey(x => new { x.TeacherId, x.ModuleId });
        b.Entity<ModuleSupervisor>().HasKey(x => new { x.TeacherId, x.ModuleId });
        b.Entity<ModuleRoom>().HasKey(x => new { x.ModuleId, x.RoomId });
        b.Entity<ModuleBuilding>().HasKey(x => new { x.ModuleId, x.BuildingId });
        b.Entity<ModuleCourse>().HasKey(x => new { x.ModuleId, x.CourseId });
        // Зберігаємо явну нижню межу поточного навчального періоду без обов'язкового значення.
        b.Entity<Course>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(CourseEditDto.NameMaxLength).IsRequired();
            e.Property(x => x.AcademicPeriodStartDate).HasColumnType("date");
        });
        // Забороняємо каскадне видалення курсу при видаленні групи.
        b.Entity<Group>()
            .HasOne(g => g.Course)
            .WithMany(c => c.Groups)
            .HasForeignKey(g => g.CourseId)
            .OnDelete(DeleteBehavior.Restrict);
        // Прив'язка викладачів до кафедри.
        b.Entity<Teacher>(e =>
        {
            e.HasOne(t => t.Department)
                .WithMany(d => d.Teachers)
                .HasForeignKey(t => t.DepartmentId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(t => t.DepartmentId);
        });
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
            e.Property(m => m.Code).HasMaxLength(64).IsRequired();
            e.HasIndex(m => new { m.CourseId, m.Code }).IsUnique();
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
            var revision = e.Property(x => x.Revision)
                .HasColumnType("char(36)")
                .IsConcurrencyToken();
            if (Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) == true)
            {
                revision.HasDefaultValueSql("(UUID())");
            }
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
            e.Property(x => x.BatchKey).HasMaxLength(64);
            e.HasIndex(x => x.BatchKey);
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
            e.Property(x => x.GroupOrder).HasDefaultValue(0);
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
            e.HasIndex(x => new { x.CourseId, x.DayOfWeek, x.SortOrder }).IsUnique();
        });
        // Забороняємо кілька обідніх конфігурацій для тієї самої області дії.
        b.Entity<LunchConfig>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Course>()
                .WithMany()
                .HasForeignKey(x => x.CourseId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        // Контролюємо чернетки викладачів і їхні залежності.
        b.Entity<PreferredFirstSlotLimitConfig>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Course)
                .WithMany()
                .HasForeignKey(x => x.CourseId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.MaxSlotOrder).HasDefaultValue(0);
            e.HasIndex(x => x.CourseId).IsUnique();
        });
        b.Entity<TeacherDraftItem>(e =>
        {
            e.HasKey(x => x.Id);
            var revision = e.Property(x => x.Revision)
                .HasColumnType("char(36)")
                .IsConcurrencyToken();
            if (Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) == true)
            {
                revision.HasDefaultValueSql("(UUID())");
            }
            e.HasOne(x => x.Teacher).WithMany().HasForeignKey(x => x.TeacherId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Room).WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Module).WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.LessonType).WithMany().HasForeignKey(x => x.LessonTypeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ModuleTopic).WithMany().HasForeignKey(x => x.ModuleTopicId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.BatchKey).HasMaxLength(64);
            e.Property(x => x.GenerationJobId).HasMaxLength(64);
            e.Property(x => x.IsSelfStudy).HasDefaultValue(false);
            e.HasIndex(x => new { x.Date, x.GroupId });
            e.HasIndex(x => new { x.Date, x.TeacherId });
            e.HasIndex(x => new { x.Date, x.RoomId });
            e.HasIndex(x => x.BatchKey);
            e.HasIndex(x => x.GenerationJobId);
        });
        // Зберігаємо перебіг автогенерації окремо від чернеток, щоб статус можна було переглянути після перезапуску.
        b.Entity<AutoGenJobRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.JobId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ClientPartitionKey).HasMaxLength(64).HasDefaultValue("legacy").IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.OwnerInstanceId).HasMaxLength(64);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.Title).HasMaxLength(256).IsRequired();
            e.Property(x => x.CurrentStage).HasMaxLength(512).IsRequired();
            e.Property(x => x.LastCompletedMessage).HasMaxLength(1024);
            e.Property(x => x.Error).HasColumnType("longtext");
            e.Property(x => x.RequestJson).HasColumnType("longtext").IsRequired();
            e.Property(x => x.StatusJson).HasColumnType("longtext").IsRequired();
            e.Property(x => x.ResultJson).HasColumnType("longtext");
            e.Property(x => x.ReportJson).HasColumnType("longtext");
            e.Property(x => x.TotalWeeks).HasDefaultValue(1);
            e.HasIndex(x => x.JobId).IsUnique();
            e.HasIndex(x => x.State);
            e.HasIndex(x => new { x.ClientPartitionKey, x.State });
            e.HasIndex(x => new { x.State, x.LeaseExpiresAtUtc });
            e.HasIndex(x => x.CreatedAtUtc);
            e.HasIndex(x => x.UpdatedAtUtc);
        });
        // Зберігаємо попередній план окремо від робочих чернеток, щоб перегляд не впливав на розклад.
        b.Entity<AutoGenDraftPlan>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.PlanId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.GroupIdsJson).HasColumnType("longtext").IsRequired();
            e.Property(x => x.BeforeScopeRevision).HasColumnType("char(36)");
            e.Property(x => x.InputFingerprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.AppliedScopeRevision).HasColumnType("char(36)");
            e.HasOne(x => x.AutoGenJobRun)
                .WithMany()
                .HasForeignKey(x => x.AutoGenJobRunId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Course)
                .WithMany()
                .HasForeignKey(x => x.CourseId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.PlanId).IsUnique();
            e.HasIndex(x => x.AutoGenJobRunId).IsUnique();
            e.HasIndex(x => new { x.State, x.ExpiresAtUtc });
        });
        // Зберігаємо точні знімки до та після кожної зміни для безпечного застосування й відкоту.
        b.Entity<AutoGenDraftPlanMutation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.BeforeRevision).HasColumnType("char(36)");
            e.Property(x => x.AppliedRevision).HasColumnType("char(36)");
            e.Property(x => x.BeforeJson).HasColumnType("longtext");
            e.Property(x => x.AfterJson).HasColumnType("longtext");
            e.HasOne(x => x.Plan)
                .WithMany(x => x.Mutations)
                .HasForeignKey(x => x.AutoGenDraftPlanId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AutoGenDraftPlanId, x.Ordinal }).IsUnique();
            e.HasIndex(x => x.AppliedDraftId);
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
            e.HasOne(x => x.Department)
                .WithMany(d => d.ModuleTopics)
                .HasForeignKey(x => x.DepartmentId)
                .OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Order).HasDefaultValue(0);
            e.Property(x => x.TopicCode).HasMaxLength(64).IsRequired();
            e.Property(x => x.IsInterAssembly).HasDefaultValue(false);
            e.Property(x => x.SelfStudyBySupervisor).HasDefaultValue(false);
            e.HasIndex(x => new { x.ModuleId, x.Order }).IsUnique();
            e.HasIndex(x => new { x.ModuleId, x.TopicCode }).IsUnique();
            e.HasIndex(x => x.DepartmentId);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareRevisionTokens();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        PrepareRevisionTokens();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // Оновлює маркер версії кожного зміненого рядка, щоб сервер міг виявити застаріле редагування.
    private void PrepareRevisionTokens()
    {
        ChangeTracker.DetectChanges();
        RefreshRevisionTokens(ChangeTracker.Entries<ScheduleItem>());
        RefreshRevisionTokens(ChangeTracker.Entries<TeacherDraftItem>());
    }

    private static void RefreshRevisionTokens<TEntity>(IEnumerable<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity>> entries)
        where TEntity : class
    {
        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added)
            {
                var revision = entry.Property(nameof(ScheduleItem.Revision));
                if ((Guid)revision.CurrentValue! == Guid.Empty)
                {
                    revision.CurrentValue = Guid.NewGuid();
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Property(nameof(ScheduleItem.Revision)).CurrentValue = Guid.NewGuid();
            }
        }
    }
}
