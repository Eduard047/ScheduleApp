using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleRevisionTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "Revision",
                table: "TeacherDraftItems",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "Revision",
                table: "ScheduleItems",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.Sql(
                "UPDATE `TeacherDraftItems` SET `Revision` = UUID() " +
                "WHERE `Revision` = '00000000-0000-0000-0000-000000000000';");
            migrationBuilder.Sql(
                "UPDATE `ScheduleItems` SET `Revision` = UUID() " +
                "WHERE `Revision` = '00000000-0000-0000-0000-000000000000';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Revision",
                table: "TeacherDraftItems");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "ScheduleItems");
        }
    }
}
