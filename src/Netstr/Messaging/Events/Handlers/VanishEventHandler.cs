using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Netstr.Data;
using Netstr.Extensions;
using Netstr.Messaging.Models;
using Netstr.Options;

namespace Netstr.Messaging.Events.Handlers
{
    public class VanishEventHandler : EventHandlerBase
    {
        private readonly IDbContextFactory<NetstrDbContext> db;
        private readonly IHttpContextAccessor http;
        private readonly IOptions<VanishOptions> vanishOptions;

        private readonly static string AllRelaysValue = "ALL_RELAYS";

        public VanishEventHandler(
            ILogger<EventHandlerBase> logger,
            IOptions<AuthOptions> auth,
            IWebSocketAdapterCollection adapters,
            IDbContextFactory<NetstrDbContext> db,
            IUserCache userCache,
            IHttpContextAccessor http,
            IOptions<VanishOptions> vanishOptions)
            : base(logger, auth, adapters, userCache)
        {
            this.db = db;
            this.http = http;
            this.vanishOptions = vanishOptions;
        }

        public override bool CanHandleEvent(Event e) => e.IsRequestToVanish();

        protected override async Task HandleEventCoreAsync(IWebSocketAdapter sender, Event e)
        {
            var ctx = this.http.HttpContext?.Request ?? throw new InvalidOperationException("HttpContext not set");
            var path = ctx.GetNormalizedUrl();
            var relays = e.GetNormalizedRelayValues();

            if (!relays.Any(x => x == path || x == AllRelaysValue))
            {
                throw new EventProcessingException(e, string.Format(Messages.InvalidWrongTagValue, EventTag.Relay));
            }

            using var db = this.db.CreateDbContext();

            // Cancel flow
            if (e.IsRequestToVanishCancel())
            {
                await HandleCancelAsync(sender, e, db);
                return;
            }

            // New vanish flow
            await HandleNewVanishAsync(sender, e, db);
        }

        private async Task HandleCancelAsync(IWebSocketAdapter sender, Event e, NetstrDbContext db)
        {
            var targetEventId = e.GetTagValue(EventTag.Event);
            if (string.IsNullOrEmpty(targetEventId))
            {
                throw new EventProcessingException(e, string.Format(Messages.InvalidWrongTagValue, EventTag.Event));
            }

            var pending = await db.Set<PendingVanishEntity>()
                .Where(p => p.Pubkey == e.PublicKey && p.WillEventId == targetEventId)
                .FirstOrDefaultAsync();

            if (pending == null)
            {
                throw new EventProcessingException(e, Messages.InvalidDeletedEvent);
            }

            if (pending.Cancelled || pending.Executed)
            {
                throw new EventProcessingException(e, Messages.InvalidDeletedEvent);
            }

            if (pending.Irreversible)
            {
                throw new EventProcessingException(e, "invalid: cancel window expired");
            }

            var now = DateTimeOffset.UtcNow;
            if (now > pending.CancelBefore)
            {
                throw new EventProcessingException(e, "invalid: cancel window expired");
            }

            pending.Cancelled = true;
            pending.Irreversible = true;

            db.Add(e.ToEntity(now));
            await db.SaveChangesAsync();

            this.userCache!.ClearPendingVanish(e.PublicKey);

            sender.SendOk(e.Id);
            BroadcastEvent(e);
        }

        private async Task HandleNewVanishAsync(IWebSocketAdapter sender, Event e, NetstrDbContext db)
        {
            var opts = this.vanishOptions.Value;
            var now = DateTimeOffset.UtcNow;

            // Parse ban_at
            var banAtStr = e.GetTagValue("ban_at");
            if (string.IsNullOrEmpty(banAtStr) || !long.TryParse(banAtStr, out var banAtUnix) || banAtUnix <= 0)
            {
                throw new EventProcessingException(e, string.Format(Messages.InvalidWrongTagValue, "ban_at"));
            }

            var banAt = DateTimeOffset.FromUnixTimeSeconds(banAtUnix);

            if (banAt <= now)
            {
                throw new EventProcessingException(e, "invalid: ban_at must be in the future");
            }

            var banAtDays = (banAt - now).TotalDays;
            if (banAtDays < opts.BanAtMinDays)
            {
                throw new EventProcessingException(e, $"invalid: ban_at must be at least {opts.BanAtMinDays} day(s) from now");
            }
            if (banAtDays > opts.BanAtMaxDays)
            {
                throw new EventProcessingException(e, $"invalid: ban_at must be at most {opts.BanAtMaxDays} day(s) from now");
            }

            // Check existing pending vanish
            var existing = await db.Set<PendingVanishEntity>()
                .Where(p => p.Pubkey == e.PublicKey && !p.Cancelled && !p.Executed)
                .FirstOrDefaultAsync();

            if (existing != null)
            {
                throw new EventProcessingException(e, "invalid: already have a pending vanish");
            }

            // Parse cancel_before (optional)
            var cancelBefore = now.AddDays(opts.CancelWindowDays);
            var cancelBeforeStr = e.GetTagValue("cancel_before");
            if (!string.IsNullOrEmpty(cancelBeforeStr) && long.TryParse(cancelBeforeStr, out var cbUnix) && cbUnix > 0)
            {
                var parsed = DateTimeOffset.FromUnixTimeSeconds(cbUnix);
                if (parsed > now && parsed < banAt)
                {
                    cancelBefore = parsed;
                }
            }

            if (cancelBefore - now < TimeSpan.FromHours(1))
            {
                throw new EventProcessingException(e, "invalid: cancel window must be at least 1 hour");
            }

            var entity = new PendingVanishEntity
            {
                Pubkey = e.PublicKey,
                WillEventId = e.Id,
                WillCreatedAt = e.CreatedAt,
                CancelBefore = cancelBefore,
                BanAt = banAt,
                Cancelled = false,
                Irreversible = false,
                Executed = false
            };

            db.Add(entity);
            db.Add(e.ToEntity(now));
            await db.SaveChangesAsync();

            this.userCache!.SetPendingVanish(e.PublicKey, now, cancelBefore, banAt);

            sender.SendOk(e.Id);
            BroadcastEvent(e);
        }
    }
}
