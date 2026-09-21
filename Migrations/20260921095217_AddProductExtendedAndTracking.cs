using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arboveya.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProductExtendedAndTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UserType",
                table: "ContactMessages",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UserType",
                table: "ContactMessages");
        }
    }
}
