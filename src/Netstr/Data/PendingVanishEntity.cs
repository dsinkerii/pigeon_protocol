namespace Netstr.Data
{
    public class PendingVanishEntity
    {
        public int Id { get; set; }
        public required string Pubkey { get; set; }
        public required string WillEventId { get; set; }
        public required DateTimeOffset WillCreatedAt { get; set; }
        public required DateTimeOffset CancelBefore { get; set; }
        public required DateTimeOffset BanAt { get; set; }
        public bool Cancelled { get; set; }
        public bool Irreversible { get; set; }
        public bool Executed { get; set; }
        public DateTimeOffset? ExecutedAt { get; set; }
    }
}
