using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Planner.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOutlookSubsystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConnectedMicrosoftAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MsalHomeAccountId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    MicrosoftUserId = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EmailAddress = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ConnectionStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    ConnectedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastAuthenticatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectedMicrosoftAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExternalCalendars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConnectedMicrosoftAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalCalendars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalCalendars_ConnectedMicrosoftAccounts_ConnectedMicrosoftAccountId",
                        column: x => x.ConnectedMicrosoftAccountId,
                        principalTable: "ConnectedMicrosoftAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CalendarSyncStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalCalendarId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeltaCursor = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    HorizonStartsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    HorizonEndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSuccessfulSyncAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarSyncStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarSyncStates_ExternalCalendars_ExternalCalendarId",
                        column: x => x.ExternalCalendarId,
                        principalTable: "ExternalCalendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalCalendarEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalCalendarId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ICalUId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsAllDay = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsCancelled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Sensitivity = table.Column<int>(type: "INTEGER", nullable: false),
                    ShowAs = table.Column<int>(type: "INTEGER", nullable: false),
                    Response = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesMasterId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    LastModifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalCalendarEvents", x => x.Id);
                    table.CheckConstraint("CK_ExternalCalendarEvents_EndAfterStart", "EndsAtUtc > StartsAtUtc");
                    table.ForeignKey(
                        name: "FK_ExternalCalendarEvents_ExternalCalendars_ExternalCalendarId",
                        column: x => x.ExternalCalendarId,
                        principalTable: "ExternalCalendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarSyncStates_ExternalCalendarId",
                table: "CalendarSyncStates",
                column: "ExternalCalendarId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectedMicrosoftAccounts_MsalHomeAccountId",
                table: "ConnectedMicrosoftAccounts",
                column: "MsalHomeAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalCalendarEvents_ExternalCalendarId_ExternalId",
                table: "ExternalCalendarEvents",
                columns: new[] { "ExternalCalendarId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalCalendarEvents_StartsAtUtc_EndsAtUtc",
                table: "ExternalCalendarEvents",
                columns: new[] { "StartsAtUtc", "EndsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalCalendars_ConnectedMicrosoftAccountId_ExternalId",
                table: "ExternalCalendars",
                columns: new[] { "ConnectedMicrosoftAccountId", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarSyncStates");

            migrationBuilder.DropTable(
                name: "ExternalCalendarEvents");

            migrationBuilder.DropTable(
                name: "ExternalCalendars");

            migrationBuilder.DropTable(
                name: "ConnectedMicrosoftAccounts");
        }
    }
}
