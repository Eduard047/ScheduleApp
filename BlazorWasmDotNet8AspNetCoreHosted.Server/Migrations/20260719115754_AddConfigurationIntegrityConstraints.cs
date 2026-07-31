using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigurationIntegrityConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TEMPORARY TABLE `_ConfigurationFunctionalIndexProbe` (`ScopeId` int NULL);
                CREATE UNIQUE INDEX `_UX_ConfigurationFunctionalIndexProbe`
                    ON `_ConfigurationFunctionalIndexProbe` ((COALESCE(`ScopeId`, 0)));
                DROP TEMPORARY TABLE `_ConfigurationFunctionalIndexProbe`;
                """);

            migrationBuilder.Sql(
                "UPDATE `LessonTypes` SET `Code` = UPPER(TRIM(`Code`)), `CssKey` = NULLIF(TRIM(`CssKey`), '');");
            migrationBuilder.Sql(
                "UPDATE `LessonTypes` SET `CssKey` = NULL WHERE `Code` IN ('NONE', 'EXAM', 'CREDIT') AND `CssKey` IN ('exam', 'credit');");
            migrationBuilder.Sql(
                "UPDATE `Modules` SET `Code` = UPPER(TRIM(`Code`));");
            migrationBuilder.Sql(
                "UPDATE `LessonTypes` SET `Code` = CONCAT('TYPE-', `Id`), `Name` = COALESCE(NULLIF(TRIM(`Name`), ''), CONCAT('Тип заняття ', `Id`)) WHERE `Code` = '';");
            migrationBuilder.Sql(
                "UPDATE `Modules` SET `Code` = CONCAT('MODULE-', `Id`) WHERE `Code` = '';");
            migrationBuilder.Sql(
                """
                CREATE TEMPORARY TABLE `_ConfigurationIntegrityMigrationGuard`
                (
                    `Id` int NOT NULL,
                    PRIMARY KEY (`Id`)
                );
                INSERT INTO `_ConfigurationIntegrityMigrationGuard` (`Id`) VALUES (1);
                INSERT INTO `_ConfigurationIntegrityMigrationGuard` (`Id`)
                SELECT 1
                WHERE EXISTS (SELECT 1 FROM `LessonTypes` WHERE `Code` = '' OR CHAR_LENGTH(`Code`) > 64)
                   OR EXISTS (SELECT 1 FROM `LessonTypes` WHERE `CssKey` IS NOT NULL AND CHAR_LENGTH(`CssKey`) > 32)
                   OR EXISTS (SELECT 1 FROM `LessonTypes` GROUP BY `Code` HAVING COUNT(*) > 1)
                   OR EXISTS (SELECT 1 FROM `LessonTypes` WHERE `CssKey` IS NOT NULL GROUP BY `CssKey` HAVING COUNT(*) > 1)
                   OR EXISTS (SELECT 1 FROM `Modules` WHERE `Code` = '' OR CHAR_LENGTH(`Code`) > 64)
                   OR EXISTS (SELECT 1 FROM `Modules` GROUP BY `CourseId`, `Code` HAVING COUNT(*) > 1)
                   OR EXISTS (SELECT 1 FROM `CalendarExceptions` GROUP BY `Date`, COALESCE(`CourseId`, 0), COALESCE(`GroupId`, 0) HAVING COUNT(*) > 1)
                   OR EXISTS (SELECT 1 FROM `LunchConfigs` GROUP BY COALESCE(`CourseId`, 0) HAVING COUNT(*) > 1)
                   OR EXISTS (SELECT 1 FROM `PreferredFirstSlotLimitConfigs` GROUP BY COALESCE(`CourseId`, 0) HAVING COUNT(*) > 1)
                   OR EXISTS (SELECT 1 FROM `TimeSlots` GROUP BY COALESCE(`CourseId`, 0), COALESCE(`DayOfWeek`, -1), `SortOrder` HAVING COUNT(*) > 1)
                   OR EXISTS (SELECT 1 FROM `LunchConfigs` lunch LEFT JOIN `Courses` course ON course.`Id` = lunch.`CourseId` WHERE lunch.`CourseId` IS NOT NULL AND course.`Id` IS NULL);
                DROP TEMPORARY TABLE `_ConfigurationIntegrityMigrationGuard`;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "Modules",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "longtext")
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "CssKey",
                table: "LessonTypes",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "longtext",
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "LessonTypes",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "longtext")
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Modules_CourseId_Code",
                table: "Modules",
                columns: new[] { "CourseId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LunchConfigs_CourseId",
                table: "LunchConfigs",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_LessonTypes_Code",
                table: "LessonTypes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LessonTypes_CssKey",
                table: "LessonTypes",
                column: "CssKey",
                unique: true);

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX `UX_TimeSlots_NormalizedScope` ON `TimeSlots` ((COALESCE(`CourseId`, 0)), (COALESCE(`DayOfWeek`, -1)), `SortOrder`);");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX `UX_CalendarExceptions_NormalizedScope` ON `CalendarExceptions` (`Date`, (COALESCE(`CourseId`, 0)), (COALESCE(`GroupId`, 0)));");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX `UX_LunchConfigs_NormalizedScope` ON `LunchConfigs` ((COALESCE(`CourseId`, 0)));");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX `UX_PreferredFirstSlotLimitConfigs_NormalizedScope` ON `PreferredFirstSlotLimitConfigs` ((COALESCE(`CourseId`, 0)));");

            migrationBuilder.AddForeignKey(
                name: "FK_LunchConfigs_Courses_CourseId",
                table: "LunchConfigs",
                column: "CourseId",
                principalTable: "Courses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX `UX_TimeSlots_NormalizedScope` ON `TimeSlots`;");
            migrationBuilder.Sql("DROP INDEX `UX_CalendarExceptions_NormalizedScope` ON `CalendarExceptions`;");
            migrationBuilder.Sql("DROP INDEX `UX_LunchConfigs_NormalizedScope` ON `LunchConfigs`;");
            migrationBuilder.Sql("DROP INDEX `UX_PreferredFirstSlotLimitConfigs_NormalizedScope` ON `PreferredFirstSlotLimitConfigs`;");

            migrationBuilder.DropForeignKey(
                name: "FK_LunchConfigs_Courses_CourseId",
                table: "LunchConfigs");

            migrationBuilder.DropIndex(
                name: "IX_Modules_CourseId_Code",
                table: "Modules");

            migrationBuilder.DropIndex(
                name: "IX_LunchConfigs_CourseId",
                table: "LunchConfigs");

            migrationBuilder.DropIndex(
                name: "IX_LessonTypes_Code",
                table: "LessonTypes");

            migrationBuilder.DropIndex(
                name: "IX_LessonTypes_CssKey",
                table: "LessonTypes");

            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "Modules",
                type: "longtext",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldMaxLength: 64)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "CssKey",
                table: "LessonTypes",
                type: "longtext",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(32)",
                oldMaxLength: 32,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "LessonTypes",
                type: "longtext",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(64)",
                oldMaxLength: 64)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

        }
    }
}
