using Microsoft.EntityFrameworkCore;
using UnifiedMessaging.Api.Models;

namespace UnifiedMessaging.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<UnifiedAccount> Accounts => Set<UnifiedAccount>();
    public DbSet<UnifiedMessage> Messages => Set<UnifiedMessage>();
    public DbSet<UnifiedAttachment> Attachments => Set<UnifiedAttachment>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<Conversation> Conversations => Set<Conversation>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UnifiedAccount>(e =>
        {
            e.HasIndex(x => new { x.Provider, x.ExternalAccountId });
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(32);
        });

        b.Entity<UnifiedMessage>(e =>
        {
            e.HasOne(x => x.Account)
                .WithMany(x => x.Messages)
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);

            // Dedupe incoming messages by (account, provider message id).
            e.HasIndex(x => new { x.AccountId, x.ProviderMessageId }).IsUnique();
            e.HasIndex(x => new { x.AccountId, x.ChatId, x.Timestamp });
        });

        b.Entity<UnifiedAttachment>(e =>
        {
            e.HasOne<UnifiedMessage>()
                .WithMany(x => x.Attachments)
                .HasForeignKey(x => x.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WebhookSubscription>(e =>
        {
            e.HasOne(x => x.Account)
                .WithMany(x => x.WebhookSubscriptions)
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.AccountId);
        });

        b.Entity<WebhookDelivery>(e =>
        {
            e.HasIndex(x => new { x.Status, x.NextRetryAt });
        });

        b.Entity<Conversation>(e =>
        {
            e.HasOne(x => x.Account)
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AccountId, x.ChatId }).IsUnique();
            e.HasIndex(x => new { x.AccountId, x.LastMessageAt });
        });
    }
}
