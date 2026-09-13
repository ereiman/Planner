using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Planner.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateAccentColor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "TaskTypes",
                keyColumn: "Id",
                keyValue: new Guid("b953ebc8-41c1-4480-9ba5-c017f3f329c1"),
                column: "Color",
                value: "#03A7E1");

            migrationBuilder.Sql("UPDATE Projects SET Color = '#03A7E1' WHERE lower(Color) = '#6750a4'");
            migrationBuilder.Sql("UPDATE BoardColumns SET Color = '#03A7E1' WHERE lower(Color) = '#6750a4'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "TaskTypes",
                keyColumn: "Id",
                keyValue: new Guid("b953ebc8-41c1-4480-9ba5-c017f3f329c1"),
                column: "Color",
                value: "#6750A4");

            migrationBuilder.Sql("UPDATE Projects SET Color = '#6750A4' WHERE lower(Color) = '#03a7e1'");
            migrationBuilder.Sql("UPDATE BoardColumns SET Color = '#6750A4' WHERE lower(Color) = '#03a7e1'");
        }
    }
}
