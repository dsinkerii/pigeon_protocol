using Netstr.Messaging.Models;

namespace Netstr.Messaging.Events.Validators
{
    /// <summary>
    /// Ensure older events cannot be republished if user vanished,
    /// and prevent cancel attempts during irreversible vanish stage.
    /// </summary>
    public class UserVanishedValidator : IEventValidator
    {
        private readonly ILogger<UserVanishedValidator> logger;
        private readonly IUserCache userCache;

        public UserVanishedValidator(
            ILogger<UserVanishedValidator> logger,
            IUserCache userCache)
        {
            this.logger = logger;
            this.userCache = userCache;
        }

        public string? Validate(Event e, ClientContext context)
        {
            var user = this.userCache.GetByPublicKey(e.PublicKey);

            if (user == null)
            {
                return null;
            }

            // Existing: reject events older than last vanish
            if (user.LastVanished.HasValue && e.CreatedAt <= user.LastVanished.Value)
            {
                this.logger.LogInformation($"Event {e.Id} is from user who already vanished on {user.LastVanished} (this event is from {e.CreatedAt})");
                return Messages.InvalidDeletedEvent;
            }

            // Stage 2: cancel attempts are rejected
            if (user.VanishIrreversible && e.IsRequestToVanishCancel())
            {
                this.logger.LogInformation($"Event {e.Id} is a cancel attempt from user in irreversible vanish stage");
                return "invalid: cancel window expired";
            }

            return null;
        }
    }
}
