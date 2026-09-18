using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Netstr.Data
{
    public class NetstrDbContext : DbContext
    {
        public const string ReplaceableUniqueIndexName = "ReplaceableEventsIdx";
        public const string EventLookupIndexName = "EventLookupIdx";
        public const string EventIdIndexName = "EventIdIdx";
        public const string TagValueIndexName = "TagNameValueIdx";

        public NetstrDbContext(DbContextOptions<NetstrDbContext> options)
            : base(options)
        {
        }

        public DbSet<EventEntity> Events { get; set; }
        
        public DbSet<TagEntity> Tags { get; set; }

        public DbSet<BlobEntity> Blobs { get; set; }

        public DbSet<NotificationTokenEntity> NotificationTokens { get; set; }

        public DbSet<PendingVanishEntity> PendingVanishes { get; set; }

        public DbSet<AdminUserEntity> AdminUsers { get; set; }

        public DbSet<PubkeyRuleEntity> PubkeyRules { get; set; }

        public DbSet<BannedEventEntity> BannedEvents { get; set; }

        public DbSet<ModerationReportEntity> ModerationReports { get; set; }

        public DbSet<ModerationAppealEntity> ModerationAppeals { get; set; }

        public DbSet<ModerationAuditLogEntity> ModerationAuditLogs { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<EventEntity>(e =>
            {
                var eKind = $"\"{nameof(EventEntity.EventKind)}\"";

                e.HasKey(x => x.Id);
                e.HasMany(x => x.Tags).WithOne(x => x.Event).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(x => x.EventId, EventIdIndexName).IsUnique();
                e.HasIndex(x => new 
                { 
                    x.EventKind,
                    x.EventPublicKey,
                    x.EventCreatedAt
                }, EventLookupIndexName);
                e.HasIndex(x => new
                {
                    x.EventPublicKey,
                    x.EventKind,
                    x.EventDeduplication
                }, ReplaceableUniqueIndexName).HasFilter(@$"
                    ({eKind} = 0) OR 
                    ({eKind} = 3) OR 
                    ({eKind} >= 10000 AND {eKind} < 20000) OR 
                    ({eKind} >= 30000 AND {eKind} < 40000)")
                .IsUnique();
            });

            builder.Entity<TagEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => new { x.Name, x.Value, x.EventId }, TagValueIndexName).IsUnique();
            });

            builder.Entity<BlobEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.Sha256).IsUnique();
                e.HasIndex(x => new { x.OwnerPubkey, x.UploadedAt });
            });

            builder.Entity<NotificationTokenEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.Token).IsUnique();
                e.HasIndex(x => x.Pubkey);
            });

            builder.Entity<PendingVanishEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => new { x.Pubkey, x.WillEventId }).IsUnique();
                e.HasIndex(x => x.Pubkey);
            });

            builder.Entity<AdminUserEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.Username).IsUnique();
            });

            builder.Entity<PubkeyRuleEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.PublicKey).IsUnique();
            });

            builder.Entity<BannedEventEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.EventId).IsUnique();
            });

            builder.Entity<ModerationReportEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.TargetPubkey);
                e.HasIndex(x => x.TargetEventId);
                e.HasIndex(x => x.Status);
            });

            builder.Entity<ModerationAppealEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.AppellantPubkey);
                e.HasIndex(x => x.Status);
            });

            builder.Entity<ModerationAuditLogEntity>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.Timestamp);
            });
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            optionsBuilder.ConfigureWarnings(w => w.Log(RelationalEventId.PendingModelChangesWarning));
        }
    }
}
