using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LegalBasisAndSnapshotLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_designation_snapshot_id",
                table: "designation",
                column: "snapshot_id");

            migrationBuilder.AddForeignKey(
                name: "fk_designation_source_snapshot_snapshot_id",
                table: "designation",
                column: "snapshot_id",
                principalTable: "source_snapshot",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_designation_source_snapshot_snapshot_id",
                table: "designation");

            migrationBuilder.DropIndex(
                name: "ix_designation_snapshot_id",
                table: "designation");
        }
    }
}
