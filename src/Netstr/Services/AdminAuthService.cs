using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NBitcoin.Secp256k1;
using Netstr.Data;

namespace Netstr.Services
{
    public interface IAdminAuthService
    {
        Task EnsureDefaultAdminAsync(CancellationToken ct = default);

        Task<(bool Success, string? Token, string? Error)> AuthenticateAsync(string username, string password, CancellationToken ct = default);

        bool ValidateToken(string token, out string? username);

        void RevokeToken(string token);

        string GenerateNostrChallenge();

        Task<(bool Success, string? Token, string? Error)> AuthenticateNostrAsync(string pubkey, string challenge, string signatureHex, CancellationToken ct = default);

        Task<bool> ResetPasswordAsync(string username, string newPassword, CancellationToken ct = default);
    }

    public class AdminAuthService : IAdminAuthService
    {
        private readonly IDbContextFactory<NetstrDbContext> dbFactory;
        private readonly ILogger<AdminAuthService> logger;

        // In-memory active tokens mapped to username and expiration
        private readonly ConcurrentDictionary<string, (string Username, DateTimeOffset ExpiresAt)> activeTokens = new();
        private readonly ConcurrentDictionary<string, DateTimeOffset> pendingNostrChallenges = new();

        public AdminAuthService(
            IDbContextFactory<NetstrDbContext> dbFactory,
            ILogger<AdminAuthService> logger)
        {
            this.dbFactory = dbFactory;
            this.logger = logger;
        }

        public async Task EnsureDefaultAdminAsync(CancellationToken ct = default)
        {
            try
            {
                using var db = this.dbFactory.CreateDbContext();
                if (!await db.AdminUsers.AnyAsync(ct))
                {
                    var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
                    var initialPassword = "adm_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
                    var hash = HashPassword(initialPassword, salt);

                    db.AdminUsers.Add(new AdminUserEntity
                    {
                        Username = "admin",
                        PasswordHash = hash,
                        Salt = salt,
                        Role = "Admin",
                        CreatedAt = DateTimeOffset.UtcNow
                    });

                    await db.SaveChangesAsync(ct);
                    this.logger.LogWarning("==================================================================");
                    this.logger.LogWarning("Unitaz-UI: Initial admin account created: 'admin' / '{InitialPassword}'", initialPassword);
                    this.logger.LogWarning("Please change your password immediately via panel or CLI!");
                    this.logger.LogWarning("==================================================================");
                }
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Failed to check or seed default admin user");
            }
        }

        public async Task<(bool Success, string? Token, string? Error)> AuthenticateAsync(string username, string password, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                return (false, null, "Username and password are required.");
            }

            using var db = this.dbFactory.CreateDbContext();
            var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), ct);

            if (user == null)
            {
                return (false, null, "Invalid username or password.");
            }

            var computedHash = HashPassword(password, user.Salt);
            if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computedHash),
                Encoding.UTF8.GetBytes(user.PasswordHash)))
            {
                return (false, null, "Invalid username or password.");
            }

            user.LastLoginAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var token = GenerateSecureToken();
            this.activeTokens[token] = (user.Username, DateTimeOffset.UtcNow.AddDays(7));

            return (true, token, null);
        }

        public bool ValidateToken(string token, out string? username)
        {
            username = null;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            if (this.activeTokens.TryGetValue(token, out var session))
            {
                if (session.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    username = session.Username;
                    return true;
                }
                this.activeTokens.TryRemove(token, out _);
            }

            return false;
        }

        public void RevokeToken(string token)
        {
            this.activeTokens.TryRemove(token, out _);
        }

        public string GenerateNostrChallenge()
        {
            var challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            this.pendingNostrChallenges[challenge] = DateTimeOffset.UtcNow.AddMinutes(5);
            return challenge;
        }

        public async Task<(bool Success, string? Token, string? Error)> AuthenticateNostrAsync(
            string pubkey, string challenge, string signatureHex, CancellationToken ct = default)
        {
            if (!this.pendingNostrChallenges.TryRemove(challenge, out var expiresAt) || expiresAt < DateTimeOffset.UtcNow)
            {
                return (false, null, "Challenge has expired or is invalid.");
            }

            // Verify Schnorr signature using NBitcoin.Secp256k1
            try
            {
                var pubkeyBytes = Convert.FromHexString(pubkey);
                var sigBytes = Convert.FromHexString(signatureHex);
                var hashBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(challenge));

                if (!Context.Instance.TryCreateXOnlyPubKey(pubkeyBytes, out var xonlyPubkey) || xonlyPubkey == null)
                {
                    return (false, null, "Invalid Nostr public key format.");
                }

                if (!SecpSchnorrSignature.TryCreate(sigBytes, out var signature) || signature == null)
                {
                    return (false, null, "Invalid signature format.");
                }

                if (!xonlyPubkey.SigVerifyBIP340(signature, hashBytes))
                {
                    return (false, null, "Nostr signature verification failed.");
                }

                using var db = this.dbFactory.CreateDbContext();
                var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.NostrPublicKey != null && u.NostrPublicKey.ToLower() == pubkey.ToLower(), ct);

                if (user == null)
                {
                    return (false, null, "No admin account associated with this Nostr public key.");
                }

                user.LastLoginAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);

                var token = GenerateSecureToken();
                this.activeTokens[token] = (user.Username, DateTimeOffset.UtcNow.AddDays(7));

                return (true, token, null);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Error verifying Nostr authentication");
                return (false, null, "Authentication verification error.");
            }
        }

        public async Task<bool> ResetPasswordAsync(string username, string newPassword, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 10)
            {
                throw new ArgumentException("Password must be at least 10 characters long.");
            }

            using var db = this.dbFactory.CreateDbContext();
            var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), ct);
            if (user == null)
            {
                var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
                db.AdminUsers.Add(new AdminUserEntity
                {
                    Username = username,
                    PasswordHash = HashPassword(newPassword, salt),
                    Salt = salt,
                    Role = "Admin"
                });
            }
            else
            {
                user.Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
                user.PasswordHash = HashPassword(newPassword, user.Salt);
            }

            await db.SaveChangesAsync(ct);
            return true;
        }

        private static string HashPassword(string password, string salt)
        {
            var saltBytes = Convert.FromBase64String(salt);
            var hash = Rfc2898DeriveBytes.Pbkdf2(
                password: Encoding.UTF8.GetBytes(password),
                salt: saltBytes,
                iterations: 100_000,
                hashAlgorithm: HashAlgorithmName.SHA256,
                outputLength: 32
            );
            return Convert.ToBase64String(hash);
        }

        private static string GenerateSecureToken()
        {
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        }
    }
}
