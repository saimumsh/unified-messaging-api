using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UnifiedMessaging.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RepliesMentionsPollsConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "Mentions",
                table: "Messages",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "PollName",
                table: "Messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "PollOptions",
                table: "Messages",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "ReplyToMessageId",
                table: "Messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplyToSenderId",
                table: "Messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplyToText",
                table: "Messages",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    ChatId = table.Column<string>(type: "text", nullable: false),
                    IsGroup = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastMessagePreview = table.Column<string>(type: "text", nullable: true),
                    LastMessageDirection = table.Column<string>(type: "text", nullable: true),
                    UnreadCount = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversations_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_AccountId_ChatId",
                table: "Conversations",
                columns: new[] { "AccountId", "ChatId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_AccountId_LastMessageAt",
                table: "Conversations",
                columns: new[] { "AccountId", "LastMessageAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropColumn(
                name: "Mentions",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "PollName",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "PollOptions",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ReplyToMessageId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ReplyToSenderId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ReplyToText",
                table: "Messages");
        }
    }
}
