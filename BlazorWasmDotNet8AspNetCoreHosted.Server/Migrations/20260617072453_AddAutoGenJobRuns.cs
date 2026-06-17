using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoGenJobRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutoGenJobRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    JobId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CurrentStage = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RangeStartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RangeEndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TotalWeeks = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    CompletedWeeks = table.Column<int>(type: "int", nullable: false),
                    CurrentWeekNumber = table.Column<int>(type: "int", nullable: false),
                    CurrentWeekStartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CurrentRangeStartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CurrentRangeEndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedCount = table.Column<int>(type: "int", nullable: false),
                    SkippedCount = table.Column<int>(type: "int", nullable: false),
                    WarningCount = table.Column<int>(type: "int", nullable: false),
                    GapCount = table.Column<int>(type: "int", nullable: false),
                    DeficitCount = table.Column<int>(type: "int", nullable: false),
                    Percent = table.Column<int>(type: "int", nullable: false),
                    CancellationRequested = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    LastCompletedMessage = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Error = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequestJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StatusJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ResultJson = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ReportJson = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutoGenJobRuns", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AutoGenJobRuns_CreatedAtUtc",
                table: "AutoGenJobRuns",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AutoGenJobRuns_JobId",
                table: "AutoGenJobRuns",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutoGenJobRuns_State",
                table: "AutoGenJobRuns",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_AutoGenJobRuns_UpdatedAtUtc",
                table: "AutoGenJobRuns",
                column: "UpdatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutoGenJobRuns");
        }
    }
}
