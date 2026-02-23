using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPreferredFirstSlotLimitConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PreferredFirstSlotLimitConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    CourseId = table.Column<int>(type: "int", nullable: true),
                    MaxSlotOrder = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreferredFirstSlotLimitConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PreferredFirstSlotLimitConfigs_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_PreferredFirstSlotLimitConfigs_CourseId",
                table: "PreferredFirstSlotLimitConfigs",
                column: "CourseId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PreferredFirstSlotLimitConfigs");
        }
    }
}
