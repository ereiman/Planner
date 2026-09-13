using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Planner.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class CorrectStagesZeroToFour : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BoardRules_BoardId",
                table: "BoardRules");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                table: "Tasks",
                newName: "UpdatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "Tasks",
                newName: "CreatedAtUtc");

            migrationBuilder.RenameColumn(
                name: "CompletedAt",
                table: "Tasks",
                newName: "CompletedAtUtc");

            migrationBuilder.RenameColumn(
                name: "IsArchived",
                table: "Projects",
                newName: "Version");

            migrationBuilder.RenameColumn(
                name: "IsRuleManaged",
                table: "BoardPlacements",
                newName: "IsRuleMatch");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAtUtc",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "DueTime",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DueDate",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAtUtc",
                table: "Projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAtUtc",
                table: "Projects",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc",
                table: "Projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAtUtc",
                table: "Projects",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAtUtc",
                table: "Boards",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAtUtc",
                table: "Boards",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc",
                table: "Boards",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAtUtc",
                table: "Boards",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Boards",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Operator",
                table: "BoardRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsManual",
                table: "BoardPlacements",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSuppressed",
                table: "BoardPlacements",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RemovedAtUtc",
                table: "BoardPlacements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "BoardColumns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE Tasks SET ArchivedAtUtc = UpdatedAtUtc WHERE State = 2");
            migrationBuilder.Sql("UPDATE Tasks SET State = CASE State WHEN 0 THEN 1 WHEN 1 THEN 2 WHEN 2 THEN 1 ELSE State END, Version = 1");
            migrationBuilder.Sql("UPDATE Tasks SET DueDate = (SELECT substr(DueAt, 1, 10) FROM Deadlines WHERE Deadlines.TaskId = Tasks.Id), DueTime = (SELECT CASE WHEN HasTime = 1 THEN substr(DueAt, 12, 8) ELSE NULL END FROM Deadlines WHERE Deadlines.TaskId = Tasks.Id) WHERE EXISTS (SELECT 1 FROM Deadlines WHERE Deadlines.TaskId = Tasks.Id)");
            migrationBuilder.Sql("UPDATE Projects SET ArchivedAtUtc = CASE WHEN Version = 1 THEN CURRENT_TIMESTAMP ELSE NULL END, Version = 1, CreatedAtUtc = CURRENT_TIMESTAMP, UpdatedAtUtc = CURRENT_TIMESTAMP");
            migrationBuilder.Sql("UPDATE Boards SET Version = 1, CreatedAtUtc = CURRENT_TIMESTAMP, UpdatedAtUtc = CURRENT_TIMESTAMP");
            migrationBuilder.Sql("UPDATE BoardPlacements SET IsManual = CASE WHEN IsRuleMatch = 1 THEN 0 ELSE 1 END");
            migrationBuilder.Sql("UPDATE BoardColumns SET IsDefault = 1 WHERE Id IN (SELECT Id FROM BoardColumns AS candidate WHERE candidate.BoardId = BoardColumns.BoardId ORDER BY candidate.[Order], candidate.Id LIMIT 1)");

            migrationBuilder.DropTable(
                name: "Deadlines");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tasks_DueTimeRequiresDueDate",
                table: "Tasks",
                sql: "DueTime IS NULL OR DueDate IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BoardRules_BoardId_Field_Operator_Value",
                table: "BoardRules",
                columns: new[] { "BoardId", "Field", "Operator", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BoardColumns_BoardId",
                table: "BoardColumns",
                column: "BoardId",
                unique: true,
                filter: "IsDefault = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Projects SET Version = CASE WHEN ArchivedAtUtc IS NULL THEN 0 ELSE 1 END");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tasks_DueTimeRequiresDueDate",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_BoardRules_BoardId_Field_Operator_Value",
                table: "BoardRules");

            migrationBuilder.DropIndex(
                name: "IX_BoardColumns_BoardId",
                table: "BoardColumns");

            migrationBuilder.DropColumn(
                name: "ArchivedAtUtc",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "DueTime",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "DueDate",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ArchivedAtUtc",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ArchivedAtUtc",
                table: "Boards");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "Boards");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Boards");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "Boards");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Boards");

            migrationBuilder.DropColumn(
                name: "Operator",
                table: "BoardRules");

            migrationBuilder.DropColumn(
                name: "IsManual",
                table: "BoardPlacements");

            migrationBuilder.DropColumn(
                name: "IsSuppressed",
                table: "BoardPlacements");

            migrationBuilder.DropColumn(
                name: "RemovedAtUtc",
                table: "BoardPlacements");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "BoardColumns");

            migrationBuilder.RenameColumn(
                name: "UpdatedAtUtc",
                table: "Tasks",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "CompletedAtUtc",
                table: "Tasks",
                newName: "CompletedAt");

            migrationBuilder.RenameColumn(
                name: "CreatedAtUtc",
                table: "Tasks",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "Version",
                table: "Projects",
                newName: "IsArchived");

            migrationBuilder.RenameColumn(
                name: "IsRuleMatch",
                table: "BoardPlacements",
                newName: "IsRuleManaged");

            migrationBuilder.CreateTable(
                name: "Deadlines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    HasTime = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deadlines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Deadlines_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BoardRules_BoardId",
                table: "BoardRules",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_Deadlines_TaskId",
                table: "Deadlines",
                column: "TaskId",
                unique: true);
        }
    }
}
