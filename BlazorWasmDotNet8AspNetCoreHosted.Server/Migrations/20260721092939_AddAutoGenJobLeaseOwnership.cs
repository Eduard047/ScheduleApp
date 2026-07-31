using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoGenJobLeaseOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempt",
                table: "AutoGenJobRuns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresAtUtc",
                table: "AutoGenJobRuns",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerInstanceId",
                table: "AutoGenJobRuns",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "AutoGenJobRuns",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "AutoGenJobRuns",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_AutoGenJobRuns_State_LeaseExpiresAtUtc",
                table: "AutoGenJobRuns",
                columns: new[] { "State", "LeaseExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AutoGenJobRuns_State_LeaseExpiresAtUtc",
                table: "AutoGenJobRuns");

            migrationBuilder.DropColumn(
                name: "Attempt",
                table: "AutoGenJobRuns");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAtUtc",
                table: "AutoGenJobRuns");

            migrationBuilder.DropColumn(
                name: "OwnerInstanceId",
                table: "AutoGenJobRuns");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "AutoGenJobRuns");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "AutoGenJobRuns");
        }
    }
}
