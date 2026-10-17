using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EFCore.SchemaSync.IntegrationTests.Migrations;

[DbContext(typeof(LibraryContext))]
[Migration("20260101000000_InitialCreate")]
public sealed class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Authors",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_Authors", x => x.Id));

        migrationBuilder.CreateTable(
            name: "Books",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                AuthorId = table.Column<int>(type: "int", nullable: false),
                Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Books", x => x.Id);
                table.ForeignKey("FK_Books_Authors_AuthorId", x => x.AuthorId, "Authors", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(name: "IX_Books_AuthorId", table: "Books", column: "AuthorId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "Books");
        migrationBuilder.DropTable(name: "Authors");
    }
}

[DbContext(typeof(LibraryContext))]
[Migration("20260201000000_AddIsbnAndStock")]
public sealed class AddIsbnAndStock : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "Isbn", table: "Books", type: "nvarchar(20)", maxLength: 20, nullable: true);
        migrationBuilder.AddColumn<bool>(name: "InStock", table: "Books", type: "bit", nullable: false, defaultValue: true);
        migrationBuilder.CreateIndex(name: "IX_Books_Isbn", table: "Books", column: "Isbn", unique: true, filter: "[Isbn] IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Books_Isbn", table: "Books");
        migrationBuilder.DropColumn(name: "InStock", table: "Books");
        migrationBuilder.DropColumn(name: "Isbn", table: "Books");
    }
}
