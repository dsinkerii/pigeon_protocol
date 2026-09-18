namespace Netstr.Data
{
    public class AdminUserEntity
    {
        public int Id { get; set; }

        public required string Username { get; set; }

        public required string PasswordHash { get; set; }

        public required string Salt { get; set; }

        public string? NostrPublicKey { get; set; }

        public string Role { get; set; } = "Admin";

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? LastLoginAt { get; set; }
    }
}
