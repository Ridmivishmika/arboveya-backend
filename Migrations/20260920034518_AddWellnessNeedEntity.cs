using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arboveya.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWellnessNeedEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WellnessNeedId",
                table: "Products",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WellnessNeeds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Icon = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ImageUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WellnessNeeds", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Products_WellnessNeedId",
                table: "Products",
                column: "WellnessNeedId");

            migrationBuilder.CreateIndex(
                name: "IX_WellnessNeeds_Name",
                table: "WellnessNeeds",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Products_WellnessNeeds_WellnessNeedId",
                table: "Products",
                column: "WellnessNeedId",
                principalTable: "WellnessNeeds",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Products_WellnessNeeds_WellnessNeedId",
                table: "Products");

            migrationBuilder.DropTable(
                name: "WellnessNeeds");

            migrationBuilder.DropIndex(
                name: "IX_Products_WellnessNeedId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "WellnessNeedId",
                table: "Products");
        }
    }
}
