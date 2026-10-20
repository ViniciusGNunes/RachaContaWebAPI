using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RachaConta.Api.Migrations
{
    /// <inheritdoc />
    public partial class MemberDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemberDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemberDevices_Members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MemberLinkCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberLinkCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemberLinkCodes_Members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemberDevices_MemberId",
                table: "MemberDevices",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_MemberDevices_TokenHash",
                table: "MemberDevices",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemberLinkCodes_CodeHash",
                table: "MemberLinkCodes",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemberLinkCodes_MemberId",
                table: "MemberLinkCodes",
                column: "MemberId");

            // Existing members keep working: their single token becomes their first device.
            var newId = migrationBuilder.ActiveProvider.Contains("Npgsql")
                ? "gen_random_uuid()"
                : "lower(hex(randomblob(16)))";
            migrationBuilder.Sql($@"
                INSERT INTO ""MemberDevices"" (""Id"", ""MemberId"", ""TokenHash"", ""CreatedAt"")
                SELECT {newId}, ""Id"", ""TokenHash"", COALESCE(""ClaimedAt"", ""CreatedAt"")
                FROM ""Members""
                WHERE ""TokenHash"" IS NOT NULL;");

            migrationBuilder.DropIndex(
                name: "IX_Members_TokenHash",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "TokenHash",
                table: "Members");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TokenHash",
                table: "Members",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Members_TokenHash",
                table: "Members",
                column: "TokenHash");

            // Only one token fits a member again: keep the oldest device's.
            migrationBuilder.Sql(@"
                UPDATE ""Members"" SET ""TokenHash"" = (
                    SELECT d.""TokenHash"" FROM ""MemberDevices"" d
                    WHERE d.""MemberId"" = ""Members"".""Id""
                    ORDER BY d.""CreatedAt"" LIMIT 1);");

            migrationBuilder.DropTable(
                name: "MemberDevices");

            migrationBuilder.DropTable(
                name: "MemberLinkCodes");
        }
    }
}
