using Netstr.Messaging.Models;

namespace Netstr.Messaging.Events.Validators
{
    public class ModerationEventValidator : IEventValidator
    {
        private readonly IModerationCache moderationCache;

        public ModerationEventValidator(IModerationCache? moderationCache = null)
        {
            this.moderationCache = moderationCache ?? new ModerationCache();
        }

        public string? Validate(Event e, ClientContext context)
        {
            if (this.moderationCache.IsPubkeyBanned(e.PublicKey, out var authorBanReason))
            {
                return string.IsNullOrWhiteSpace(authorBanReason)
                    ? "blocked: author pubkey is banned on this relay"
                    : $"blocked: author pubkey is banned: {authorBanReason}";
            }

            if (this.moderationCache.IsEventBanned(e.Id, out var eventBanReason))
            {
                return string.IsNullOrWhiteSpace(eventBanReason)
                    ? "blocked: event is tombstoned/banned by relay moderation"
                    : $"blocked: event is banned: {eventBanReason}";
            }

            return null;
        }
    }
}
