using Netstr.Messaging.Models;
using System.Collections.Concurrent;

namespace Netstr.Messaging
{
    public interface IUserCache
    {
        void Initialize(IEnumerable<User> users);
        
        User SetFromEvent(Event e);
        
        User? GetByPublicKey(string publicKey);
        
        User Vanish(string publicKey, DateTimeOffset timestamp);

        User SetPendingVanish(string publicKey, DateTimeOffset pendingAt, DateTimeOffset cancelBefore, DateTimeOffset banAt);

        User ClearPendingVanish(string publicKey);

        User SetVanishIrreversible(string publicKey, DateTimeOffset willCreatedAt);
    }

    public class UserCache : IUserCache
    {
        private readonly ConcurrentDictionary<string, User> users = new();

        public User? GetByPublicKey(string publicKey)
        {
            this.users.TryGetValue(publicKey, out var user);

            return user;
        }

        public void Initialize(IEnumerable<User> users)
        {
            foreach (var user in users)
            {
                this.users.TryAdd(user.PublicKey, user);
            }
        }

        public User SetFromEvent(Event e)
        {
            return this.users.AddOrUpdate(
                e.PublicKey,
                key => new User { PublicKey = key, EventId = e.Id },
                (key, user) => user with { EventId = e.Id });
        }

        public User Vanish(string publicKey, DateTimeOffset timestamp)
        {
            return this.users.AddOrUpdate(
                publicKey,
                key => new User { PublicKey = key, LastVanished = timestamp },
                (key, user) => user with { LastVanished = timestamp, PendingVanishAt = null, VanishIrreversible = false });
        }

        public User SetPendingVanish(string publicKey, DateTimeOffset pendingAt, DateTimeOffset cancelBefore, DateTimeOffset banAt)
        {
            return this.users.AddOrUpdate(
                publicKey,
                key => new User
                {
                    PublicKey = key,
                    PendingVanishAt = pendingAt,
                    PendingCancelBefore = cancelBefore,
                    PendingBanAt = banAt,
                    VanishIrreversible = false
                },
                (key, user) => user with
                {
                    PendingVanishAt = pendingAt,
                    PendingCancelBefore = cancelBefore,
                    PendingBanAt = banAt,
                    VanishIrreversible = false
                });
        }

        public User ClearPendingVanish(string publicKey)
        {
            return this.users.AddOrUpdate(
                publicKey,
                key => new User { PublicKey = key },
                (key, user) => user with
                {
                    PendingVanishAt = null,
                    PendingCancelBefore = null,
                    PendingBanAt = null,
                    VanishIrreversible = false
                });
        }

        public User SetVanishIrreversible(string publicKey, DateTimeOffset willCreatedAt)
        {
            return this.users.AddOrUpdate(
                publicKey,
                key => new User
                {
                    PublicKey = key,
                    LastVanished = willCreatedAt,
                    PendingVanishAt = null,
                    PendingCancelBefore = null,
                    PendingBanAt = null,
                    VanishIrreversible = false
                },
                (key, user) => user with
                {
                    LastVanished = willCreatedAt,
                    PendingVanishAt = null,
                    PendingCancelBefore = null,
                    PendingBanAt = null,
                    VanishIrreversible = false
                });
        }
    }
}