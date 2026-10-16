using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <summary>
    /// The in-memory semantic index now asks, before every search, for the newest vector
    /// under its model to learn whether another process has written since it loaded.
    /// (model_id, created_at) answers that off the end of the index; the old index on
    /// model_id alone made it a 20 ms scan, and the new one still serves the load.
    /// </summary>
    public partial class SemanticIndexFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_designation_name_embedding_model_id",
                table: "designation_name_embedding");

            migrationBuilder.CreateIndex(
                name: "ix_designation_name_embedding_model_id_created_at",
                table: "designation_name_embedding",
                columns: new[] { "model_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_designation_name_embedding_model_id_created_at",
                table: "designation_name_embedding");

            migrationBuilder.CreateIndex(
                name: "ix_designation_name_embedding_model_id",
                table: "designation_name_embedding",
                column: "model_id");
        }
    }
}
