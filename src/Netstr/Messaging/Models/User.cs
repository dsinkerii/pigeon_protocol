namespace Netstr.Messaging.Models
{
    public record User
    {
        public required string PublicKey { get; init; }

        public string? EventId { get; init; }

        public DateTimeOffset? LastVanished { get; init; }

        public DateTimeOffset? PendingVanishAt { get; init; }

        public DateTimeOffset? PendingCancelBefore { get; init; }

        public DateTimeOffset? PendingBanAt { get; init; }

        public bool VanishIrreversible { get; init; }
    }
}
