namespace Netstr.Data
{
    public enum PubkeyRuleStatus
    {
        Allowed = 0,
        Banned = 1,
        Whitelisted = 2
    }

    public class PubkeyRuleEntity
    {
        public int Id { get; set; }

        public required string PublicKey { get; set; }

        public PubkeyRuleStatus Status { get; set; } = PubkeyRuleStatus.Allowed;

        public long? CustomStorageQuotaBytes { get; set; }
 
        public bool BlossomUploadBanned { get; set; } = false;

        public string? BanReason { get; set; }

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? ExpiresAt { get; set; }
    }
}
