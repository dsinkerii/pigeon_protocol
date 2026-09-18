using Microsoft.EntityFrameworkCore;
using Netstr.Data;
using Netstr.Messaging;
using Netstr.Services;

namespace Netstr.Middleware
{
    public class ModerationStartupService : IHostedService
    {
        private readonly ILogger<ModerationStartupService> logger;
        private readonly IDbContextFactory<NetstrDbContext> dbFactory;
        private readonly IModerationCache moderationCache;
        private readonly IAdminAuthService adminAuthService;

        public ModerationStartupService(
            ILogger<ModerationStartupService> logger,
            IDbContextFactory<NetstrDbContext> dbFactory,
            IModerationCache moderationCache,
            IAdminAuthService adminAuthService)
        {
            this.logger = logger;
            this.dbFactory = dbFactory;
            this.moderationCache = moderationCache;
            this.adminAuthService = adminAuthService;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            this.logger.LogInformation("Initializing moderation engine and admin accounts...");

            try
            {
                using var db = this.dbFactory.CreateDbContext();

                // 1. Seed default admin if needed
                await this.adminAuthService.EnsureDefaultAdminAsync(cancellationToken);

                // 2. Load active pubkey rules and banned events into in-memory cache
                var rules = await db.PubkeyRules.AsNoTracking().ToListAsync(cancellationToken);
                var bannedEvents = await db.BannedEvents.AsNoTracking().ToListAsync(cancellationToken);

                this.moderationCache.Initialize(rules, bannedEvents);

                this.logger.LogInformation($"Moderation cache initialized: {rules.Count} pubkey rules, {bannedEvents.Count} banned events.");
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Failed to initialize moderation cache or admin account.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
