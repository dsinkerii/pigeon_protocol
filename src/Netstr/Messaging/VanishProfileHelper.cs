using System.Text.Json;
using Netstr.Messaging.Models;

namespace Netstr.Messaging
{
    public static class VanishProfileHelper
    {
        public static Event InjectVanishFields(Event e, IUserCache userCache)
        {
            if (e.Kind != 0)
            {
                return e;
            }

            var user = userCache.GetByPublicKey(e.PublicKey);
            if (user == null)
            {
                return e;
            }

            int cancelationPeriod;
            long vanishAt;

            if (user.LastVanished.HasValue)
            {
                cancelationPeriod = 2;
                vanishAt = user.LastVanished.Value.ToUnixTimeSeconds();
            }
            else if (user.VanishIrreversible)
            {
                cancelationPeriod = 2;
                vanishAt = user.PendingBanAt?.ToUnixTimeSeconds() ?? -1;
            }
            else if (user.PendingVanishAt.HasValue)
            {
                cancelationPeriod = 1;
                vanishAt = user.PendingBanAt?.ToUnixTimeSeconds() ?? -1;
            }
            else
            {
                cancelationPeriod = 0;
                vanishAt = -1;
            }

            // Only inject if there's something to inject
            if (cancelationPeriod == 0 && vanishAt == -1)
            {
                return e;
            }

            try
            {
                var content = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(e.Content)
                    ?? new Dictionary<string, JsonElement>();

                content["cancelation_period"] = JsonSerializer.SerializeToElement(cancelationPeriod);
                content["vanish_at"] = JsonSerializer.SerializeToElement(vanishAt);

                var newContent = JsonSerializer.Serialize(content);

                return e with { Content = newContent };
            }
            catch
            {
                return e;
            }
        }
    }
}
