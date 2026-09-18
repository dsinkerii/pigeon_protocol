using System.Collections.Concurrent;
using Netstr.Data;

namespace Netstr.Messaging
{
    public interface IModerationCache
    {
        void Initialize(IEnumerable<PubkeyRuleEntity> pubkeyRules, IEnumerable<BannedEventEntity> bannedEvents);

        bool IsPubkeyBanned(string publicKey, out string? reason);

        bool IsEventBanned(string eventId, out string? reason);

        bool IsPubkeyWhitelisted(string publicKey);

        bool IsBlossomUploadBanned(string publicKey, out string? reason);

        bool TryGetCustomStorageQuota(string publicKey, out long quotaBytes);

        void SetPubkeyRule(string publicKey, PubkeyRuleStatus status, string? reason);

        void RemovePubkeyRule(string publicKey);

        void SetBlossomUploadBanned(string publicKey, bool banned, string? reason);

        void SetCustomStorageQuota(string publicKey, long? quotaBytes);

        void SetEventBanned(string eventId, string? reason);

        void RemoveEventBanned(string eventId);
    }

    public class ModerationCache : IModerationCache
    {
        private readonly ConcurrentDictionary<string, string?> bannedPubkeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> whitelistedPubkeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string?> bannedEvents = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string?> blossomUploadBanned = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, long> customStorageQuotas = new(StringComparer.OrdinalIgnoreCase);

        public void Initialize(IEnumerable<PubkeyRuleEntity> pubkeyRules, IEnumerable<BannedEventEntity> bannedEventsList)
        {
            this.bannedPubkeys.Clear();
            this.whitelistedPubkeys.Clear();
            this.bannedEvents.Clear();
            this.blossomUploadBanned.Clear();
            this.customStorageQuotas.Clear();

            foreach (var rule in pubkeyRules)
            {
                if (rule.ExpiresAt.HasValue && rule.ExpiresAt.Value <= DateTimeOffset.UtcNow)
                {
                    continue;
                }

                if (rule.Status == PubkeyRuleStatus.Banned)
                {
                    this.bannedPubkeys[rule.PublicKey] = rule.BanReason;
                }
                else if (rule.Status == PubkeyRuleStatus.Whitelisted)
                {
                    this.whitelistedPubkeys[rule.PublicKey] = 0;
                }

                if (rule.BlossomUploadBanned)
                {
                    this.blossomUploadBanned[rule.PublicKey] = rule.BanReason ?? "Uploads banned by admin";
                }

                if (rule.CustomStorageQuotaBytes.HasValue)
                {
                    this.customStorageQuotas[rule.PublicKey] = rule.CustomStorageQuotaBytes.Value;
                }
            }

            foreach (var ev in bannedEventsList)
            {
                this.bannedEvents[ev.EventId] = ev.Reason;
            }
        }

        public bool IsPubkeyBanned(string publicKey, out string? reason)
        {
            return this.bannedPubkeys.TryGetValue(publicKey, out reason);
        }

        public bool IsEventBanned(string eventId, out string? reason)
        {
            return this.bannedEvents.TryGetValue(eventId, out reason);
        }

        public bool IsPubkeyWhitelisted(string publicKey)
        {
            return this.whitelistedPubkeys.ContainsKey(publicKey);
        }

        public bool IsBlossomUploadBanned(string publicKey, out string? reason)
        {
            if (this.bannedPubkeys.TryGetValue(publicKey, out reason))
            {
                return true;
            }
            if (this.blossomUploadBanned.TryGetValue(publicKey, out reason))
            {
                return true;
            }
            if (this.customStorageQuotas.TryGetValue(publicKey, out var quota) && quota == 0)
            {
                reason = "User storage quota is 0 MB";
                return true;
            }
            return false;
        }

        public bool TryGetCustomStorageQuota(string publicKey, out long quotaBytes)
        {
            return this.customStorageQuotas.TryGetValue(publicKey, out quotaBytes);
        }

        public void SetPubkeyRule(string publicKey, PubkeyRuleStatus status, string? reason)
        {
            if (status == PubkeyRuleStatus.Banned)
            {
                this.whitelistedPubkeys.TryRemove(publicKey, out _);
                this.bannedPubkeys[publicKey] = reason;
            }
            else if (status == PubkeyRuleStatus.Whitelisted)
            {
                this.bannedPubkeys.TryRemove(publicKey, out _);
                this.whitelistedPubkeys[publicKey] = 0;
            }
            else
            {
                RemovePubkeyRule(publicKey);
            }
        }

        public void RemovePubkeyRule(string publicKey)
        {
            this.bannedPubkeys.TryRemove(publicKey, out _);
            this.whitelistedPubkeys.TryRemove(publicKey, out _);
            this.blossomUploadBanned.TryRemove(publicKey, out _);
            this.customStorageQuotas.TryRemove(publicKey, out _);
        }

        public void SetBlossomUploadBanned(string publicKey, bool banned, string? reason)
        {
            if (banned)
            {
                this.blossomUploadBanned[publicKey] = reason ?? "Uploads banned by admin";
            }
            else
            {
                this.blossomUploadBanned.TryRemove(publicKey, out _);
            }
        }

        public void SetCustomStorageQuota(string publicKey, long? quotaBytes)
        {
            if (quotaBytes.HasValue)
            {
                this.customStorageQuotas[publicKey] = quotaBytes.Value;
            }
            else
            {
                this.customStorageQuotas.TryRemove(publicKey, out _);
            }
        }

        public void SetEventBanned(string eventId, string? reason)
        {
            this.bannedEvents[eventId] = reason;
        }

        public void RemoveEventBanned(string eventId)
        {
            this.bannedEvents.TryRemove(eventId, out _);
        }
    }
}
