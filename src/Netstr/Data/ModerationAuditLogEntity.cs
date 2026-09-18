namespace Netstr.Data
{
    public class ModerationAuditLogEntity
    {
        public int Id { get; set; }

        public required string AdminIdentifier { get; set; }

        public required string ActionType { get; set; }

        public required string TargetIdentifier { get; set; }

        public string? DetailsJson { get; set; }

        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    }
}
