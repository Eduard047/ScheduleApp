using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Migrations
{
    public partial class TimeSlotsDayOfWeek : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DayOfWeek",
                table: "TimeSlots",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimeSlots_CourseId",
                table: "TimeSlots",
                column: "CourseId");

            migrationBuilder.DropIndex(
                name: "IX_TimeSlots_CourseId_SortOrder",
                table: "TimeSlots");

            migrationBuilder.CreateIndex(
                name: "IX_TimeSlots_CourseId_DayOfWeek_SortOrder",
                table: "TimeSlots",
                columns: new[] { "CourseId", "DayOfWeek", "SortOrder" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TimeSlots_CourseId_DayOfWeek_SortOrder",
                table: "TimeSlots");

            migrationBuilder.DropColumn(
                name: "DayOfWeek",
                table: "TimeSlots");

            migrationBuilder.CreateIndex(
                name: "IX_TimeSlots_CourseId_SortOrder",
                table: "TimeSlots",
                columns: new[] { "CourseId", "SortOrder" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_TimeSlots_CourseId",
                table: "TimeSlots");
        }
    }
}
