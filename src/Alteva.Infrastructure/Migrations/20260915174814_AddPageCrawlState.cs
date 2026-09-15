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
            migrationBuilder.DropIndex(
                name: "IX_Pages_JobId_Url",
                table: "Pages");

            migrationBuilder.DropIndex(
                name: "IX_Edges_JobId_ParentUrl_ChildUrl",
                table: "Edges");

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

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Pages",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                // Pages persisted before this migration were only ever written once fully crawled.
                defaultValue: "Done");

            migrationBuilder.AddColumn<byte[]>(
                name: "UrlHash",
                table: "Pages",
                type: "binary(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: false,
                defaultValue: new byte[0]);

            // Backfill before the unique index is created. HASHBYTES over nvarchar hashes the
            // UTF-16LE bytes, matching UrlHasher.Hash in the application.
            migrationBuilder.Sql("UPDATE Pages SET UrlHash = HASHBYTES('SHA2_256', Url);");

            migrationBuilder.CreateIndex(
                name: "IX_Pages_JobId_Status",
                table: "Pages",
                columns: new[] { "JobId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Pages_JobId_UrlHash",
                table: "Pages",
                columns: new[] { "JobId", "UrlHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Edges_JobId",
                table: "Edges",
                column: "JobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Pages_JobId_Status",
                table: "Pages");

            migrationBuilder.DropIndex(
                name: "IX_Pages_JobId_UrlHash",
                table: "Pages");

            migrationBuilder.DropIndex(
                name: "IX_Edges_JobId",
                table: "Edges");

            migrationBuilder.DropColumn(
                name: "Depth",
                table: "Pages");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                table: "Pages");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Pages");

            migrationBuilder.DropColumn(
                name: "UrlHash",
                table: "Pages");

            migrationBuilder.AlterColumn<double>(
                name: "DomainLinkRatio",
                table: "Pages",
                type: "float",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "float",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pages_JobId_Url",
                table: "Pages",
                columns: new[] { "JobId", "Url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Edges_JobId_ParentUrl_ChildUrl",
                table: "Edges",
                columns: new[] { "JobId", "ParentUrl", "ChildUrl" },
                unique: true);
        }
    }
}
