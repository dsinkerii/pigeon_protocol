using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Netstr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingVanish : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingVanishes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Pubkey = table.Column<string>(type: "text", nullable: false),
                    WillEventId = table.Column<string>(type: "text", nullable: false),
                    WillCreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CancelBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    BanAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    Irreversible = table.Column<bool>(type: "boolean", nullable: false),
                    Executed = table.Column<bool>(type: "boolean", nullable: false),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingVanishes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingVanishes_Pubkey",
                table: "PendingVanishes",
                column: "Pubkey");

            migrationBuilder.CreateIndex(
                name: "IX_PendingVanishes_Pubkey_WillEventId",
                table: "PendingVanishes",
                columns: new[] { "Pubkey", "WillEventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingVanishes");
        }
    }
}
