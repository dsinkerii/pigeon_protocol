using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Netstr.Data;
using Netstr.Json;
using Netstr.Messaging.Models;
using Netstr.Options;
using System.Security.Cryptography;
using System.Text.Json;

namespace Netstr.Controllers
{
    [Route("/")]
    public class NotificationController : Controller
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = new NostrJsonEncoder()
        };

        private const string NotifyScheme = "Notify ";
        private const string NostrScheme = "Nostr ";

        private readonly IDbContextFactory<NetstrDbContext> dbFactory;
        private readonly IOptions<NotificationOptions> options;
        private readonly ILogger<NotificationController> logger;

        public NotificationController(
            IDbContextFactory<NetstrDbContext> dbFactory,
            IOptions<NotificationOptions> options,
            ILogger<NotificationController> logger)
        {
            this.dbFactory = dbFactory;
            this.options = options;
            this.logger = logger;
        }

        [HttpPost("notifications/token")]
        public async Task<IActionResult> CreateToken()
        {
            var opts = this.options.Value;
            if (!opts.Enabled) return StatusCode(503);

            var authResult = ValidateNostrAuth(expectedAction: "notifications");
            if (!authResult.IsValid)
            {
                Response.Headers["X-Reason"] = authResult.Error ?? "Unauthorized";
                return Unauthorized();
            }

            var pubkey = authResult.PublicKey!;
            var tokenBytes = RandomNumberGenerator.GetBytes(16);
            var token = "nt-" + Convert.ToHexStringLower(tokenBytes);

            await using var db = await this.dbFactory.CreateDbContextAsync();

            var existing = await db.NotificationTokens.FirstOrDefaultAsync(t => t.Pubkey == pubkey);
            if (existing != null)
            {
                existing.Token = token;
                existing.LastAuthAt = DateTimeOffset.UtcNow;
            }
            else
            {
                db.NotificationTokens.Add(new NotificationTokenEntity
                {
                    Pubkey = pubkey,
                    Token = token,
                    IssuedAt = DateTimeOffset.UtcNow,
                    LastAuthAt = DateTimeOffset.UtcNow
                });
            }

            await db.SaveChangesAsync();

            return Ok(new { token });
        }

        [HttpDelete("notifications/token")]
        public async Task<IActionResult> DeleteToken()
        {
            var opts = this.options.Value;
            if (!opts.Enabled) return StatusCode(503);

            var authResult = ValidateNostrAuth(expectedAction: "notifications");
            if (!authResult.IsValid)
            {
                Response.Headers["X-Reason"] = authResult.Error ?? "Unauthorized";
                return Unauthorized();
            }

            var pubkey = authResult.PublicKey!;

            await using var db = await this.dbFactory.CreateDbContextAsync();

            var existing = await db.NotificationTokens.FirstOrDefaultAsync(t => t.Pubkey == pubkey);
            if (existing != null)
            {
                db.NotificationTokens.Remove(existing);
                await db.SaveChangesAsync();
            }

            return Ok();
        }

        [HttpGet("notifications")]
        public async Task<IActionResult> GetNotifications(
            [FromQuery] long since,
            [FromQuery] bool content = false)
        {
            var opts = this.options.Value;
            if (!opts.Enabled) return StatusCode(503);

            var pubkey = await ResolvePubkeyAsync();
            if (pubkey == null)
            {
                Response.Headers["X-Reason"] = "Unauthorized";
                return Unauthorized();
            }

            var sinceDate = DateTimeOffset.FromUnixTimeSeconds(since);
            var maxSince = DateTimeOffset.UtcNow.AddDays(-opts.MaxSinceAgeDays);
            if (sinceDate < maxSince) sinceDate = maxSince;

            var isTokenAuth = Request.Headers["Authorization"].FirstOrDefault()?.StartsWith(NotifyScheme, StringComparison.OrdinalIgnoreCase) == true;
            var exposeContent = content && !isTokenAuth;

            await using var db = await this.dbFactory.CreateDbContextAsync();

            var entities = await db.Events
                .Include(e => e.Tags)
                .AsNoTracking()
                .Where(e =>
                    !e.DeletedAt.HasValue &&
                    e.EventCreatedAt >= sinceDate &&
                    e.Tags.Any(t => t.Name == EventTag.PublicKey && t.Value == pubkey))
                .OrderByDescending(e => e.EventCreatedAt)
                .Take(opts.MaxEvents)
                .ToListAsync();

            var events = entities.Select(e => new
            {
                id = e.EventId,
                pubkey = e.EventPublicKey,
                kind = e.EventKind,
                created_at = e.EventCreatedAt.ToUnixTimeSeconds(),
                content = exposeContent ? e.EventContent : null,
                tags = e.Tags.Select(t =>
                {
                    var tag = new List<string>(t.OtherValues.Length + 2) { t.Name };
                    if (t.Value != null) tag.Add(t.Value);
                    tag.AddRange(t.OtherValues);
                    return tag.ToArray();
                }).ToArray()
            }).ToList();

            var until = events.Count > 0 ? events.Max(e => e.created_at) : since;

            return Ok(new { events, since, until });
        }

        private async Task<string?> ResolvePubkeyAsync()
        {
            var authHeader = Request.Headers["Authorization"].FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader)) return null;

            if (authHeader.StartsWith(NostrScheme, StringComparison.OrdinalIgnoreCase))
            {
                var result = ValidateNostrAuth(expectedAction: "notifications");
                return result.IsValid ? result.PublicKey : null;
            }

            if (authHeader.StartsWith(NotifyScheme, StringComparison.OrdinalIgnoreCase))
            {
                var token = authHeader[NotifyScheme.Length..].Trim();
                return await ValidateNotifyToken(token);
            }

            return null;
        }

        private async Task<string?> ValidateNotifyToken(string token)
        {
            await using var db = await this.dbFactory.CreateDbContextAsync();

            var tokenEntity = await db.NotificationTokens
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Token == token);

            if (tokenEntity == null) return null;

            var cutoff = DateTimeOffset.UtcNow.AddDays(-this.options.Value.TokenLifetimeDays);
            if (tokenEntity.LastAuthAt < cutoff) return null;

            return tokenEntity.Pubkey;
        }

        private NotificationAuthResult ValidateNostrAuth(string expectedAction)
        {
            var authHeader = Request.Headers["Authorization"].FirstOrDefault();

            if (string.IsNullOrEmpty(authHeader))
            {
                return NotificationAuthResult.Invalid("Authorization header required");
            }

            if (!authHeader.StartsWith(NostrScheme, StringComparison.OrdinalIgnoreCase))
            {
                return NotificationAuthResult.Invalid("Invalid authorization scheme, expected 'Nostr'");
            }

            var base64Token = authHeader[NostrScheme.Length..].Trim();

            Event? tokenEvent;
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(DecodeBase64Url(base64Token)));
                tokenEvent = JsonSerializer.Deserialize<Event>(json);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to decode notification auth token");
                return NotificationAuthResult.Invalid("Invalid token encoding");
            }

            if (tokenEvent == null) return NotificationAuthResult.Invalid("Invalid token event");

            if (tokenEvent.Kind != EventKind.BlossomToken)
                return NotificationAuthResult.Invalid($"Invalid token kind {tokenEvent.Kind}, expected {EventKind.BlossomToken}");

            if (!VerifyEventHash(tokenEvent)) return NotificationAuthResult.Invalid("Invalid event hash");
            if (!VerifySignature(tokenEvent)) return NotificationAuthResult.Invalid("Invalid signature");
            if (tokenEvent.CreatedAt > DateTimeOffset.UtcNow) return NotificationAuthResult.Invalid("Token created_at is in the future");

            var expirationValue = tokenEvent.GetExpirationValue();
            if (expirationValue == null) return NotificationAuthResult.Invalid("Missing expiration tag");
            if (expirationValue < DateTimeOffset.UtcNow) return NotificationAuthResult.Invalid("Token expired");

            var action = tokenEvent.GetTagValue(EventTag.BlossomAction);
            if (string.IsNullOrEmpty(action) || action != expectedAction)
                return NotificationAuthResult.Invalid($"Invalid action '{action}', expected '{expectedAction}'");

            this.logger.LogDebug("Validated Nostr auth for pubkey {Pubkey}", tokenEvent.PublicKey);

            return NotificationAuthResult.Valid(tokenEvent.PublicKey);
        }

        private static bool VerifyEventHash(Event e)
        {
            var obj = (object[])[
                0,
                e.PublicKey,
                e.CreatedAt.ToUnixTimeSeconds(),
                e.Kind,
                e.Tags,
                e.Content
            ];

            var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(obj, JsonOptions)));
            return hash.Equals(e.Id);
        }

        private static bool VerifySignature(Event e)
        {
            try
            {
                var pubkey = Convert.FromHexString(e.PublicKey);
                var sig = Convert.FromHexString(e.Signature);
                var id = Convert.FromHexString(e.Id);

                if (!NBitcoin.Secp256k1.SecpSchnorrSignature.TryCreate(sig, out var signature)) return false;

                return NBitcoin.Secp256k1.Context.Instance.CreateXOnlyPubKey(pubkey).SigVerifyBIP340(signature, id);
            }
            catch
            {
                return false;
            }
        }

        private static string DecodeBase64Url(string base64)
        {
            base64 = base64.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }
            return base64;
        }

        private record NotificationAuthResult
        {
            public bool IsValid { get; init; }
            public string? Error { get; init; }
            public string? PublicKey { get; init; }

            public static NotificationAuthResult Valid(string pubkey) => new() { IsValid = true, PublicKey = pubkey };
            public static NotificationAuthResult Invalid(string error) => new() { IsValid = false, Error = error };
        }
    }
}