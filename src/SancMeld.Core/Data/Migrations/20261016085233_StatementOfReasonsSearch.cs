using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace SancMeld.Core.Data.Migrations
{
    /// <summary>
    /// A generated tsvector over a listing's free text (reason, function, title,
    /// remarks) with a GIN index, so a company named only in a statement of reasons can
    /// be reached by its name; and the hash of what a register lookup compared, so a
    /// re-exported list does not send every person back to the register.
    /// </summary>
    public partial class StatementOfReasonsSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "match_input_hash",
                table: "external_identity",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "designation",
                type: "tsvector",
                nullable: true)
                .Annotation("Npgsql:TsVectorConfig", "simple")
                .Annotation("Npgsql:TsVectorProperties", new[] { "reason", "function", "title", "remarks" });

            migrationBuilder.CreateIndex(
                name: "designation_search_ix",
                table: "designation",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "designation_search_ix",
                table: "designation");

            migrationBuilder.DropColumn(
                name: "match_input_hash",
                table: "external_identity");

            migrationBuilder.DropColumn(
                name: "search_vector",
                table: "designation");
        }
    }
}
