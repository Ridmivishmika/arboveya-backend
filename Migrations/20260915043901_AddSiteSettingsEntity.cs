using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arboveya.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSiteSettingsEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SiteSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    HomePageHeroText = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AboutUsContent = table.Column<string>(type: "text", nullable: true),
                    FacebookLink = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    WhatsAppNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "SiteSettings",
                columns: new[] { "Id", "AboutUsContent", "FacebookLink", "HomePageHeroText", "UpdatedAt", "WhatsAppNumber" },
                values: new object[] { 1, "Arboveya is dedicated to bringing vibrant, healthy plants, botanical care accessories, and sustainable greenery into your homes and workplaces.", "https://facebook.com/arboveya", "Bring Nature Indoors with Arboveya's Premium Botanical Collection", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "+94771234567" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiteSettings");
        }
    }
}
