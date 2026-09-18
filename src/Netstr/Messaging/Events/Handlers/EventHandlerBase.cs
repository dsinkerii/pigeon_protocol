using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Netstr.Data;
using Netstr.Messaging.Models;
using Netstr.Messaging.Subscriptions;
using Netstr.Options;

namespace Netstr.Messaging.Events.Handlers
{
    public abstract class EventHandlerBase : IEventHandler
    {
        protected readonly ILogger<EventHandlerBase> logger;
        protected readonly IOptions<AuthOptions> auth;
        protected readonly IWebSocketAdapterCollection adapters;
        protected readonly IUserCache? userCache;

        protected EventHandlerBase(
            ILogger<EventHandlerBase> logger,
            IOptions<AuthOptions> auth,
            IWebSocketAdapterCollection adapters,
            IUserCache? userCache = null)
        {
            this.logger = logger;
            this.auth = auth;
            this.adapters = adapters;
            this.userCache = userCache;
        }

        public async Task HandleEventAsync(IWebSocketAdapter sender, Event e)
        {
            try
            {
                await HandleEventCoreAsync(sender, e);
            }
            catch (DbUpdateException ex) when (ex.IsUniqueIndexViolation())
            {
                this.logger.LogInformation($"Event {e.ToStringUnique()} already exists, ignoring");
                sender.SendOk(e.Id, Messages.DuplicateEvent);
            }
        }

        public abstract bool CanHandleEvent(Event e);

        protected abstract Task HandleEventCoreAsync(IWebSocketAdapter sender, Event e);

        protected void BroadcastEvent(Event e)
        {
            var adapters = this.adapters.GetAll();

            // Inject vanish fields for kind-0 broadcasts
            var eventToBroadcast = this.userCache != null && e.Kind == 0
                ? VanishProfileHelper.InjectVanishFields(e, this.userCache)
                : e;

            foreach (var adapter in adapters)
            {
                BroadcastEventForAdapterAsync(adapter, eventToBroadcast);
            }
        }

        private void BroadcastEventForAdapterAsync(IWebSocketAdapter adapter, Event e)
        {
            if (
                this.auth.Value.ProtectedKinds.Contains(e.Kind) &&
                this.auth.Value.Mode != AuthMode.Disabled &&
                adapter.Context.PublicKey != e.PublicKey &&
                e.Tags.Any(x => x.Length >= 2 && x[0] == EventTag.PublicKey && x[1] != adapter.Context.PublicKey))
            {
                this.logger.LogInformation($"Not going to broadcast event {e.Id}");

                return;
            }

            var subs = adapter.Subscriptions
                .GetAll()
                .Where(x => x.Value.Filters.IsAnyMatch(e))
                .ToList();

            if (subs.Any())
            {
                this.logger.LogInformation($"Broadcasting event {e.Id} to subscribers");

                foreach (var sub in subs)
                {
                    sub.Value.SendEvent(e);
                };
            }
        }
    }
}
