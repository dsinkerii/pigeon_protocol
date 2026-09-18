namespace Netstr.Data
{
    public enum AppealStatus
    {
        Pending = 0,
        Approved = 1,
        Rejected = 2
    }

    public class ModerationAppealEntity
    {
        public int Id { get; set; }

        public required string AppellantPubkey { get; set; }

        public string? TargetEventId { get; set; }

        public required string AppealMessage { get; set; }

        public AppealStatus Status { get; set; } = AppealStatus.Pending;

        public string? ModeratorComment { get; set; }

        public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? ReviewedAt { get; set; }
    }
}
