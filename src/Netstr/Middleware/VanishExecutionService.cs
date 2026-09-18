using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Netstr.Data;
using Netstr.Messaging;
using Netstr.Messaging.Models;
using Netstr.Options;

namespace Netstr.Middleware
{
    public class VanishExecutionService : BackgroundService
    {
        private readonly ILogger<VanishExecutionService> logger;
        private readonly IDbContextFactory<NetstrDbContext> db;
        private readonly IUserCache userCache;
        private readonly IOptions<VanishOptions> options;

        public VanishExecutionService(
            ILogger<VanishExecutionService> logger,
            IDbContextFactory<NetstrDbContext> db,
            IUserCache userCache,
            IOptions<VanishOptions> options)
        {
            this.logger = logger;
            this.db = db;
            this.userCache = userCache;
            this.options = options;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(10, this.options.Value.ExecutionIntervalSeconds));

            this.logger.LogInformation($"Vanish execution service started, checking every {interval.TotalSeconds}s");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessVanishAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, "Error processing vanish");
                }

                await Task.Delay(interval, stoppingToken);
            }
        }

        private async Task ProcessVanishAsync(CancellationToken ct)
        {
            using var db = this.db.CreateDbContext();
            var now = DateTimeOffset.UtcNow;

            // Step 1: Mark expired cancel windows as irreversible
            var expiredCancels = await db.Set<PendingVanishEntity>()
                .Where(p => !p.Cancelled && !p.Irreversible && !p.Executed && p.CancelBefore <= now)
                .ToListAsync(ct);

            foreach (var pending in expiredCancels)
            {
                this.logger.LogInformation($"Vanish cancel window expired for {pending.Pubkey}, making irreversible");
                pending.Irreversible = true;
                this.userCache.SetVanishIrreversible(pending.Pubkey, pending.WillCreatedAt);
            }

            if (expiredCancels.Count > 0)
            {
                await db.SaveChangesAsync(ct);
            }

            // Step 2: Execute irreversible vanishes past ban_at
            var dueForExecution = await db.Set<PendingVanishEntity>()
                .Where(p => !p.Cancelled && p.Irreversible && !p.Executed && p.BanAt <= now)
                .ToListAsync(ct);

            foreach (var pending in dueForExecution)
            {
                try
                {
                    await ExecuteVanishAsync(db, pending, ct);
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, $"Failed to execute vanish for {pending.Pubkey}");
                }
            }

            if (dueForExecution.Count > 0)
            {
                await db.SaveChangesAsync(ct);
            }
        }

        private async Task ExecuteVanishAsync(NetstrDbContext db, PendingVanishEntity pending, CancellationToken ct)
        {
            this.logger.LogInformation($"Executing vanish for {pending.Pubkey}");

            using var tx = await db.Database.BeginTransactionAsync(ct);

            // Delete all user's events up to will created_at (same as original vanish)
            await db.Events
                .Include(x => x.Tags)
                .Where(x =>
                    (x.EventPublicKey == pending.Pubkey ||
                    (x.EventKind == EventKind.GiftWrap && x.Tags.Any(t => t.Name == EventTag.PublicKey && t.Value == pending.Pubkey))) &&
                    x.EventCreatedAt <= pending.WillCreatedAt)
                .ExecuteDeleteAsync(ct);

            // Mark executed
            pending.Executed = true;
            pending.ExecutedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Update cache
            this.userCache.Vanish(pending.Pubkey, pending.BanAt);

            this.logger.LogInformation($"Vanish executed for {pending.Pubkey}");
        }
    }
}
