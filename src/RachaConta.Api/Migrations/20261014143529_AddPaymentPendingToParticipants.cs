using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RachaConta.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentPendingToParticipants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAt",
                table: "SplitParticipants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PaymentPending",
                table: "SplitParticipants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "SplitParticipants");

            migrationBuilder.DropColumn(
                name: "PaymentPending",
                table: "SplitParticipants");
        }
    }
}
