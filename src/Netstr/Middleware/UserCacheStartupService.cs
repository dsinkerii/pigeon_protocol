using Microsoft.EntityFrameworkCore;
using Netstr.Data;
using Netstr.Messaging;
using Netstr.Messaging.Models;

namespace Netstr.Middleware
{
    public class UserCacheStartupService : IHostedService
    {
        private readonly ILogger<UserCacheStartupService> logger;
        private readonly IDbContextFactory<NetstrDbContext> db;
        private readonly IUserCache cache;

        public UserCacheStartupService(
            ILogger<UserCacheStartupService> logger,
            IDbContextFactory<NetstrDbContext> db,
            IUserCache cache)
        {
            this.logger = logger;
            this.db = db;
            this.cache = cache;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            this.logger.LogInformation("Initializing user cache started");

            using var db = this.db.CreateDbContext();
            var now = DateTimeOffset.UtcNow;

            // Load vanished users (existing logic)
            var events = await db.Events
                .AsNoTracking()
                .GroupBy(x => new { x.EventKind, x.EventPublicKey })
                .Where(x => x.Key.EventKind == EventKind.RequestToVanish)
                .Select(x => new { x.Key.EventPublicKey, VanishedAt = x.Max(x => x.EventCreatedAt) })
                .ToArrayAsync(cancellationToken);

            var users = events
                .Select(x => new User { PublicKey = x.EventPublicKey, LastVanished = x.VanishedAt })
                .ToArray();

            this.cache.Initialize(users);

            // Load pending vanishes
            var pending = await db.Set<PendingVanishEntity>()
                .Where(p => !p.Cancelled && !p.Executed)
                .ToListAsync(cancellationToken);

            foreach (var p in pending)
            {
                if (p.Irreversible)
                {
                    this.cache.SetVanishIrreversible(p.Pubkey, p.WillCreatedAt);
                }
                else
                {
                    this.cache.SetPendingVanish(p.Pubkey, p.WillCreatedAt, p.CancelBefore, p.BanAt);
                }
            }

            this.logger.LogInformation($"User cache initialized: {users.Length} vanished, {pending.Count} pending");
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
