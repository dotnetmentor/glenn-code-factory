using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace api.Migrations
{
    /// <inheritdoc />
    public partial class AddSubdomainAssignmentFkToBranches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SubdomainAssignments_AssignedBranchId",
                table: "SubdomainAssignments",
                column: "AssignedBranchId",
                unique: true,
                filter: "\"AssignedBranchId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_SubdomainAssignments_ProjectBranches_AssignedBranchId",
                table: "SubdomainAssignments",
                column: "AssignedBranchId",
                principalTable: "ProjectBranches",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SubdomainAssignments_ProjectBranches_AssignedBranchId",
                table: "SubdomainAssignments");

            migrationBuilder.DropIndex(
                name: "IX_SubdomainAssignments_AssignedBranchId",
                table: "SubdomainAssignments");
        }
    }
}
