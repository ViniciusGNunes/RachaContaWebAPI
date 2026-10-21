using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RachaPix.Api.Migrations
{
    /// <inheritdoc />
    public partial class GroupIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedAt",
                table: "Settlements",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByMemberId",
                table: "Settlements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Settlements",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Confirmed");

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAt",
                table: "Members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAdmin",
                table: "Members",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TokenHash",
                table: "Members",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByMemberId",
                table: "Expenses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Expenses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedByMemberId",
                table: "Expenses",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExpenseConfirmations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpenseId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Comment = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseConfirmations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseConfirmations_Expenses_ExpenseId",
                        column: x => x.ExpenseId,
                        principalTable: "Expenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Members_TokenHash",
                table: "Members",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseConfirmations_ExpenseId_MemberId",
                table: "ExpenseConfirmations",
                columns: new[] { "ExpenseId", "MemberId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExpenseConfirmations");

            migrationBuilder.DropIndex(
                name: "IX_Members_TokenHash",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                table: "Settlements");

            migrationBuilder.DropColumn(
                name: "CreatedByMemberId",
                table: "Settlements");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Settlements");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "IsAdmin",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "TokenHash",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "CreatedByMemberId",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "UpdatedByMemberId",
                table: "Expenses");
        }
    }
}
