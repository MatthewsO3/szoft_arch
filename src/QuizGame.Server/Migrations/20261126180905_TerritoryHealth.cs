using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuizGame.Server.Migrations
{
    /// <inheritdoc />
    public partial class TerritoryHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Health",
                table: "Territories",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Health",
                table: "Territories");
        }
    }
}
