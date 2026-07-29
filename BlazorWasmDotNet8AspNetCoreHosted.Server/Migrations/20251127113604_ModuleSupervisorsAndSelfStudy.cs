using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Migrations
{
    /// <inheritdoc />
    public partial class ModuleSupervisorsAndSelfStudy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSelfStudy",
                table: "TeacherDraftItems",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSelfStudy",
                table: "ScheduleItems",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ModuleSupervisors",
                columns: table => new
                {
                    TeacherId = table.Column<int>(type: "int", nullable: false),
                    ModuleId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModuleSupervisors", x => new { x.TeacherId, x.ModuleId });
                    table.ForeignKey(
                        name: "FK_ModuleSupervisors_Modules_ModuleId",
                        column: x => x.ModuleId,
                        principalTable: "Modules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ModuleSupervisors_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalTable: "Teachers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ModuleSupervisors_ModuleId_TeacherId",
                table: "ModuleSupervisors",
                columns: new[] { "ModuleId", "TeacherId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModuleSupervisors");

            migrationBuilder.DropColumn(
                name: "IsSelfStudy",
                table: "TeacherDraftItems");

            migrationBuilder.DropColumn(
                name: "IsSelfStudy",
                table: "ScheduleItems");
        }
    }
}
