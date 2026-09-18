namespace Netstr.Data
{
    public class BannedEventEntity
    {
        public int Id { get; set; }

        public required string EventId { get; set; }

        public string? Reason { get; set; }

        public DateTimeOffset BannedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
