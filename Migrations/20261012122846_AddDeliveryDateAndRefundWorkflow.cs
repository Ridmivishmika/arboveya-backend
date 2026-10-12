using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arboveya.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryDateAndRefundWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""BankAccountName"" character varying(150);
                ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""BankAccountNumber"" character varying(100);
                ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""BankBranch"" character varying(100);
                ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""BankName"" character varying(150);
                ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""BankRoutingCode"" character varying(100);

                ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""Condition"" character varying(100);
                ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""CountryOfOrigin"" character varying(100);
                ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""EstimatedDeliveryTime"" character varying(100);
                ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""ExpiryDate"" timestamp with time zone;
                ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""HandlingTime"" character varying(100);
                ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""IsFreeShipping"" boolean NOT NULL DEFAULT false;
                ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""ManufactureDate"" timestamp with time zone;
                ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""ReturnPolicy"" text;
                ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""ShippingCost"" numeric(18,2) NOT NULL DEFAULT 0;
                ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""ShippingMethod"" character varying(150);
                ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""ShippingOptions"" text;
                ALTER TABLE ""Products"" ADD COLUMN IF NOT EXISTS ""Specifications"" text;

                ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""EstimatedDeliveryDate"" timestamp with time zone;
                ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""RefundProcessedAt"" timestamp with time zone;
                ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""RefundReason"" character varying(500);
                ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""RefundRequestedAt"" timestamp with time zone;
                ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""RefundStatus"" character varying(50) NOT NULL DEFAULT 'None';
                ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""ShippedAt"" timestamp with time zone;
                ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""ShippingCarrier"" character varying(100);
                ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""ShippingCost"" numeric(18,2) NOT NULL DEFAULT 0;
                ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""ShippingMethod"" character varying(150);
                ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""TrackingNumber"" character varying(150);
                ALTER TABLE ""Orders"" ADD COLUMN IF NOT EXISTS ""TrackingUrl"" character varying(500);
            ");

            migrationBuilder.UpdateData(
                table: "SiteSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "FacebookLink", "WhatsAppNumber" },
                values: new object[] { "https://www.facebook.com/share/1DspKR2vtm/", "0717981355" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BankAccountName",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BankAccountNumber",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BankBranch",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BankName",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BankRoutingCode",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Condition",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "CountryOfOrigin",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "EstimatedDeliveryTime",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ExpiryDate",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "HandlingTime",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "IsFreeShipping",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ManufactureDate",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ReturnPolicy",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ShippingCost",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ShippingMethod",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ShippingOptions",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Specifications",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "EstimatedDeliveryDate",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RefundProcessedAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RefundReason",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RefundRequestedAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RefundStatus",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ShippedAt",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ShippingCarrier",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ShippingCost",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ShippingMethod",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TrackingNumber",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TrackingUrl",
                table: "Orders");

            migrationBuilder.UpdateData(
                table: "SiteSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "FacebookLink", "WhatsAppNumber" },
                values: new object[] { "https://facebook.com/arboveya", "+94771234567" });
        }
    }
}
