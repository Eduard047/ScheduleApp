using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Migrations
{
    /// <inheritdoc />
    public partial class CalendarExceptionScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CalendarExceptions_Date",
                table: "CalendarExceptions");

            migrationBuilder.AddColumn<int>(
                name: "CourseId",
                table: "CalendarExceptions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "CalendarExceptions",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarExceptions_CourseId",
                table: "CalendarExceptions",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarExceptions_Date_CourseId_GroupId",
                table: "CalendarExceptions",
                columns: new[] { "Date", "CourseId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarExceptions_GroupId",
                table: "CalendarExceptions",
                column: "GroupId");

            migrationBuilder.AddForeignKey(
                name: "FK_CalendarExceptions_Courses_CourseId",
                table: "CalendarExceptions",
                column: "CourseId",
                principalTable: "Courses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_CalendarExceptions_Groups_GroupId",
                table: "CalendarExceptions",
                column: "GroupId",
                principalTable: "Groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CalendarExceptions_Courses_CourseId",
                table: "CalendarExceptions");

            migrationBuilder.DropForeignKey(
                name: "FK_CalendarExceptions_Groups_GroupId",
                table: "CalendarExceptions");

            migrationBuilder.DropIndex(
                name: "IX_CalendarExceptions_CourseId",
                table: "CalendarExceptions");

            migrationBuilder.DropIndex(
                name: "IX_CalendarExceptions_Date_CourseId_GroupId",
                table: "CalendarExceptions");

            migrationBuilder.DropIndex(
                name: "IX_CalendarExceptions_GroupId",
                table: "CalendarExceptions");

            migrationBuilder.DropColumn(
                name: "CourseId",
                table: "CalendarExceptions");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "CalendarExceptions");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarExceptions_Date",
                table: "CalendarExceptions",
                column: "Date",
                unique: true);
        }
    }
}
