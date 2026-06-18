namespace Netstr.Data
{
    public class NotificationTokenEntity
    {
        public int Id { get; set; }

        public required string Pubkey { get; set; }

        public required string Token { get; set; }

        public required DateTimeOffset IssuedAt { get; set; }

        public required DateTimeOffset LastAuthAt { get; set; }
    }
}
