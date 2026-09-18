using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Netstr.Data.Migrations
{
    [DbContext(typeof(NetstrDbContext))]
    [Migration("20260913180000_AddAdminAndModeration")]
    public partial class AddAdminAndModeration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. AdminUsers
            migrationBuilder.CreateTable(
                name: "AdminUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Username = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    Salt = table.Column<string>(type: "text", nullable: false),
                    NostrPublicKey = table.Column<string>(type: "text", nullable: true),
                    Role = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminUsers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminUsers_Username",
                table: "AdminUsers",
                column: "Username",
                unique: true);

            // 2. PubkeyRules
            migrationBuilder.CreateTable(
                name: "PubkeyRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicKey = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CustomStorageQuotaBytes = table.Column<long>(type: "bigint", nullable: true),
                    BanReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PubkeyRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PubkeyRules_PublicKey",
                table: "PubkeyRules",
                column: "PublicKey",
                unique: true);

            // 3. BannedEvents
            migrationBuilder.CreateTable(
                name: "BannedEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    BannedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BannedEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BannedEvents_EventId",
                table: "BannedEvents",
                column: "EventId",
                unique: true);

            // 4. ModerationReports
            migrationBuilder.CreateTable(
                name: "ModerationReports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReportEventId = table.Column<string>(type: "text", nullable: true),
                    ReporterPubkey = table.Column<string>(type: "text", nullable: false),
                    TargetPubkey = table.Column<string>(type: "text", nullable: false),
                    TargetEventId = table.Column<string>(type: "text", nullable: true),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ResolutionNotes = table.Column<string>(type: "text", nullable: true),
                    ResolvedByAdmin = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModerationReports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModerationReports_TargetPubkey",
                table: "ModerationReports",
                column: "TargetPubkey");

            migrationBuilder.CreateIndex(
                name: "IX_ModerationReports_TargetEventId",
                table: "ModerationReports",
                column: "TargetEventId");

            migrationBuilder.CreateIndex(
                name: "IX_ModerationReports_Status",
                table: "ModerationReports",
                column: "Status");

            // 5. ModerationAppeals
            migrationBuilder.CreateTable(
                name: "ModerationAppeals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppellantPubkey = table.Column<string>(type: "text", nullable: false),
                    TargetEventId = table.Column<string>(type: "text", nullable: true),
                    AppealMessage = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ModeratorComment = table.Column<string>(type: "text", nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModerationAppeals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModerationAppeals_AppellantPubkey",
                table: "ModerationAppeals",
                column: "AppellantPubkey");

            migrationBuilder.CreateIndex(
                name: "IX_ModerationAppeals_Status",
                table: "ModerationAppeals",
                column: "Status");

            // 6. ModerationAuditLogs
            migrationBuilder.CreateTable(
                name: "ModerationAuditLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AdminIdentifier = table.Column<string>(type: "text", nullable: false),
                    ActionType = table.Column<string>(type: "text", nullable: false),
                    TargetIdentifier = table.Column<string>(type: "text", nullable: false),
                    DetailsJson = table.Column<string>(type: "text", nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModerationAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModerationAuditLogs_Timestamp",
                table: "ModerationAuditLogs",
                column: "Timestamp");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ModerationAuditLogs");
            migrationBuilder.DropTable(name: "ModerationAppeals");
            migrationBuilder.DropTable(name: "ModerationReports");
            migrationBuilder.DropTable(name: "BannedEvents");
            migrationBuilder.DropTable(name: "PubkeyRules");
            migrationBuilder.DropTable(name: "AdminUsers");
        }
    }
}
