using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alteva.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPageCrawlState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "DomainLinkRatio",
                table: "Pages",
                type: "float",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "float");

            migrationBuilder.AddColumn<int>(
                name: "Depth",
                table: "Pages",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                table: "Pages",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "Pages",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Pages",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                // Pages persisted before this migration were only ever written once fully crawled.
                defaultValue: "Done");

            migrationBuilder.AddColumn<int>(
                name: "ClaimedPages",
                table: "Jobs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                "UPDATE j SET ClaimedPages = (SELECT COUNT(*) FROM Pages p WHERE p.JobId = j.Id) FROM Jobs j;");

            migrationBuilder.CreateIndex(
                name: "IX_Pages_JobId_Status",
                table: "Pages",
                columns: new[] { "JobId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Pages_JobId_Status",
                table: "Pages");

            migrationBuilder.DropColumn(
                name: "Depth",
                table: "Pages");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                table: "Pages");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "Pages");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Pages");

            migrationBuilder.DropColumn(
                name: "ClaimedPages",
                table: "Jobs");

            migrationBuilder.AlterColumn<double>(
                name: "DomainLinkRatio",
                table: "Pages",
                type: "float",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "float",
                oldNullable: true);
        }
    }
}
