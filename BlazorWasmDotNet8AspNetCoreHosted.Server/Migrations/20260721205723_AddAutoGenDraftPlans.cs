using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoGenDraftPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GenerationJobId",
                table: "TeacherDraftItems",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AutoGenDraftPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PlanId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AutoGenJobRunId = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CourseId = table.Column<int>(type: "int", nullable: false),
                    RangeStartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RangeEndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Days = table.Column<int>(type: "int", nullable: false),
                    AllowIncompleteDrafts = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    GroupIdsJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BeforeScopeRevision = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    InputFingerprint = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AppliedScopeRevision = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    AddCount = table.Column<int>(type: "int", nullable: false),
                    UpdateCount = table.Column<int>(type: "int", nullable: false),
                    DeleteCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    RolledBackAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutoGenDraftPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutoGenDraftPlans_AutoGenJobRuns_AutoGenJobRunId",
                        column: x => x.AutoGenJobRunId,
                        principalTable: "AutoGenJobRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AutoGenDraftPlans_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AutoGenDraftPlanMutations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AutoGenDraftPlanId = table.Column<int>(type: "int", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Operation = table.Column<int>(type: "int", nullable: false),
                    SourceDraftId = table.Column<int>(type: "int", nullable: true),
                    AppliedDraftId = table.Column<int>(type: "int", nullable: true),
                    BeforeRevision = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    AppliedRevision = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    BeforeJson = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AfterJson = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutoGenDraftPlanMutations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutoGenDraftPlanMutations_AutoGenDraftPlans_AutoGenDraftPlan~",
                        column: x => x.AutoGenDraftPlanId,
                        principalTable: "AutoGenDraftPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_TeacherDraftItems_GenerationJobId",
                table: "TeacherDraftItems",
                column: "GenerationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_AutoGenDraftPlanMutations_AppliedDraftId",
                table: "AutoGenDraftPlanMutations",
                column: "AppliedDraftId");

            migrationBuilder.CreateIndex(
                name: "IX_AutoGenDraftPlanMutations_AutoGenDraftPlanId_Ordinal",
                table: "AutoGenDraftPlanMutations",
                columns: new[] { "AutoGenDraftPlanId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutoGenDraftPlans_AutoGenJobRunId",
                table: "AutoGenDraftPlans",
                column: "AutoGenJobRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutoGenDraftPlans_CourseId",
                table: "AutoGenDraftPlans",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_AutoGenDraftPlans_PlanId",
                table: "AutoGenDraftPlans",
                column: "PlanId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutoGenDraftPlans_State_ExpiresAtUtc",
                table: "AutoGenDraftPlans",
                columns: new[] { "State", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutoGenDraftPlanMutations");

            migrationBuilder.DropTable(
                name: "AutoGenDraftPlans");

            migrationBuilder.DropIndex(
                name: "IX_TeacherDraftItems_GenerationJobId",
                table: "TeacherDraftItems");

            migrationBuilder.DropColumn(
                name: "GenerationJobId",
                table: "TeacherDraftItems");
        }
    }
}
