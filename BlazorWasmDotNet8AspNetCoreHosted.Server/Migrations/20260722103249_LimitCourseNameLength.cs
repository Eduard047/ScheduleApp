using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Migrations
{
    /// <inheritdoc />
    public partial class LimitCourseNameLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TEMPORARY TABLE IF EXISTS `_CourseNameLengthMigrationGuard`;
                CREATE TEMPORARY TABLE `_CourseNameLengthMigrationGuard`
                (
                    `Id` int NOT NULL,
                    PRIMARY KEY (`Id`)
                );
                INSERT INTO `_CourseNameLengthMigrationGuard` (`Id`) VALUES (1);
                INSERT INTO `_CourseNameLengthMigrationGuard` (`Id`)
                SELECT 1
                WHERE EXISTS (SELECT 1 FROM `Courses` WHERE CHAR_LENGTH(`Name`) > 256);
                DROP TEMPORARY TABLE `_CourseNameLengthMigrationGuard`;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Courses",
                type: "varchar(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "longtext")
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Courses",
                type: "longtext",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(256)",
                oldMaxLength: 256)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }
    }
}
