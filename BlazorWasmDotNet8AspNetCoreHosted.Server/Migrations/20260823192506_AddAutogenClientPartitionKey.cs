using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddAutogenClientPartitionKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientPartitionKey",
                table: "AutoGenJobRuns",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "legacy")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AutoGenJobRuns_ClientPartitionKey_State",
                table: "AutoGenJobRuns",
                columns: new[] { "ClientPartitionKey", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AutoGenJobRuns_ClientPartitionKey_State",
                table: "AutoGenJobRuns");

            migrationBuilder.DropColumn(
                name: "ClientPartitionKey",
                table: "AutoGenJobRuns");
        }
    }
}
