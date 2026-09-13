using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Planner.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "InboxOrder",
                table: "Tasks",
                newName: "SortOrder");

            migrationBuilder.Sql("UPDATE Tasks SET State = 1 WHERE State = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SortOrder",
                table: "Tasks",
                newName: "InboxOrder");
        }
    }
}
