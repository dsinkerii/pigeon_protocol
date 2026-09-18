namespace Netstr.Data
{
    public enum ReportStatus
    {
        Pending = 0,
        ResolvedDismissed = 1,
        ResolvedActionTaken = 2
    }

    public class ModerationReportEntity
    {
        public int Id { get; set; }

        public string? ReportEventId { get; set; }

        public required string ReporterPubkey { get; set; }

        public required string TargetPubkey { get; set; }

        public string? TargetEventId { get; set; }

        public required string Category { get; set; }

        public string? Content { get; set; }

        public ReportStatus Status { get; set; } = ReportStatus.Pending;

        public string? ResolutionNotes { get; set; }

        public string? ResolvedByAdmin { get; set; }

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? ResolvedAt { get; set; }
    }
}
