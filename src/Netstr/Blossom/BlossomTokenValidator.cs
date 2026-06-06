using Microsoft.Extensions.Options;
using Netstr.Json;
using Netstr.Messaging.Models;
using Netstr.Options;
using System.Security.Cryptography;
using System.Text.Json;

namespace Netstr.Blossom
{
    public record BlossomAuthResult
    {
        public bool IsValid { get; init; }
        public string? Error { get; init; }
        public string? PublicKey { get; init; }
        public string? Action { get; init; }
        public string? BlobHash { get; init; }
    }

    public class BlossomTokenValidator
    {
        private static JsonSerializerOptions serializerOptions = new JsonSerializerOptions
        {
            Encoder = new NostrJsonEncoder()
        };

        private readonly BlossomOptions options;
        private readonly ILogger<BlossomTokenValidator> logger;

        public BlossomTokenValidator(IOptions<BlossomOptions> options, ILogger<BlossomTokenValidator> logger)
        {
            this.options = options.Value;
            this.logger = logger;
        }

        public BlossomAuthResult Validate(string? authorizationHeader, string expectedAction, string? expectedBlobHash = null)
        {
            if (string.IsNullOrEmpty(authorizationHeader))
            {
                if (!options.AuthRequired)
                {
                    return new BlossomAuthResult { IsValid = true };
                }

                return new BlossomAuthResult { IsValid = false, Error = "Authorization header required" };
            }

            if (!authorizationHeader.StartsWith("Nostr ", StringComparison.OrdinalIgnoreCase))
            {
                return new BlossomAuthResult { IsValid = false, Error = "Invalid authorization scheme, expected 'Nostr'" };
            }

            var base64Token = authorizationHeader["Nostr ".Length..].Trim();

            Event? tokenEvent;
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(DecodeBase64Url(base64Token)));
                tokenEvent = JsonSerializer.Deserialize<Event>(json);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to decode Blossom token");
                return new BlossomAuthResult { IsValid = false, Error = "Invalid token encoding" };
            }

            if (tokenEvent == null)
            {
                return new BlossomAuthResult { IsValid = false, Error = "Invalid token event" };
            }

            // 1. verify kind == 24242
            if (tokenEvent.Kind != EventKind.BlossomToken)
            {
                return new BlossomAuthResult { IsValid = false, Error = $"Invalid token kind {tokenEvent.Kind}, expected {EventKind.BlossomToken}" };
            }

            // 2. verify event hash
            if (!VerifyEventHash(tokenEvent))
            {
                return new BlossomAuthResult { IsValid = false, Error = "Invalid event hash" };
            }

            // 3. verify signature
            if (!VerifySignature(tokenEvent))
            {
                return new BlossomAuthResult { IsValid = false, Error = "Invalid signature" };
            }

            // 4. verify created_at is in the past
            if (tokenEvent.CreatedAt > DateTimeOffset.UtcNow)
            {
                return new BlossomAuthResult { IsValid = false, Error = "Token created_at is in the future" };
            }

            // 5. verify expiration tag
            var expirationValue = tokenEvent.GetExpirationValue();
            if (expirationValue == null)
            {
                return new BlossomAuthResult { IsValid = false, Error = "Missing expiration tag" };
            }

            if (expirationValue < DateTimeOffset.UtcNow)
            {
                return new BlossomAuthResult { IsValid = false, Error = "Token expired" };
            }

            // 6. verify t tag matches expected action
            var action = tokenEvent.GetTagValue(EventTag.BlossomAction);
            if (string.IsNullOrEmpty(action) || action != expectedAction)
            {
                return new BlossomAuthResult { IsValid = false, Error = $"Invalid action '{action}', expected '{expectedAction}'" };
            }

            // 7. verify x tag for upload/delete
            var blobHash = tokenEvent.GetTagValue(EventTag.BlossomBlobHash);

            if (expectedAction is "upload" or "delete" or "media")
            {
                if (string.IsNullOrEmpty(blobHash))
                {
                    return new BlossomAuthResult { IsValid = false, Error = "Missing 'x' tag for this action" };
                }

                if (expectedBlobHash != null && !blobHash.Equals(expectedBlobHash, StringComparison.OrdinalIgnoreCase))
                {
                    return new BlossomAuthResult { IsValid = false, Error = $"Blob hash mismatch: token has '{blobHash}', expected '{expectedBlobHash}'" };
                }
            }

            // 8. verify server tag if present
            var serverTags = tokenEvent.GetTagValues(EventTag.BlossomServer).ToList();
            if (serverTags.Count > 0)
            {
                // TODO
            }

            this.logger.LogDebug("Validated Blossom token for pubkey {Pubkey}, action {Action}", tokenEvent.PublicKey, action);

            return new BlossomAuthResult
            {
                IsValid = true,
                PublicKey = tokenEvent.PublicKey,
                Action = action,
                BlobHash = blobHash
            };
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

            var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(obj, serializerOptions)));
            return hash.Equals(e.Id);
        }

        private static bool VerifySignature(Event e)
        {
            try
            {
                var pubkey = Convert.FromHexString(e.PublicKey);
                var sig = Convert.FromHexString(e.Signature);
                var id = Convert.FromHexString(e.Id);

                if (!NBitcoin.Secp256k1.SecpSchnorrSignature.TryCreate(sig, out var signature))
                {
                    return false;
                }

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
    }
}
