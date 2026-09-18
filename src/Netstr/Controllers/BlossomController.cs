using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Netstr.Blossom;
using Netstr.Messaging;
using Netstr.Options;
using Netstr.Services;
using System.Security.Cryptography;
using System.Text.Json;

namespace Netstr.Controllers
{
    [Route("/")]
    public class BlossomController : Controller
    {
        private readonly IBlobStorageService blobStorage;
        private readonly BlossomTokenValidator tokenValidator;
        private readonly IBlossomManagerService blossomManager;
        private readonly IModerationCache moderationCache;
        private readonly BlossomOptions options;
        private readonly ILogger<BlossomController> logger;

        public BlossomController(
            IBlobStorageService blobStorage,
            BlossomTokenValidator tokenValidator,
            IBlossomManagerService blossomManager,
            IModerationCache moderationCache,
            IOptions<BlossomOptions> options,
            ILogger<BlossomController> logger)
        {
            this.blobStorage = blobStorage;
            this.tokenValidator = tokenValidator;
            this.blossomManager = blossomManager;
            this.moderationCache = moderationCache;
            this.options = options.Value;
            this.logger = logger;
        }

        /// <summary>
        /// BUD-01: GET /{sha256}.ext - Retrieve blob (with file extension)
        /// </summary>
        [HttpGet("{sha256:regex(^[[a-fA-F0-9]]{{64}}$)}.{ext}")]
        public Task<IActionResult> GetBlobWithExt(string sha256) => GetBlobCore(sha256);

        /// <summary>
        /// BUD-01: GET /{sha256} - Retrieve blob
        /// </summary>
        [HttpGet("{sha256:regex(^[[a-fA-F0-9]]{{64}}$)}")]
        public Task<IActionResult> GetBlob(string sha256) => GetBlobCore(sha256);

        private async Task<IActionResult> GetBlobCore(string sha256)
        {
            if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            {
                return NotFound();
            }

            sha256 = sha256.ToLowerInvariant();

            try
            {
                var (stream, contentType, size) = await this.blobStorage.GetBlobAsync(sha256);

                Response.Headers["Content-Type"] = contentType;
                Response.Headers["Content-Length"] = size.ToString();
                Response.Headers["Accept-Ranges"] = "bytes";
                Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                Response.Headers["X-Content-Type-Options"] = "nosniff";

                if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                    contentType.Contains("svg", StringComparison.OrdinalIgnoreCase) ||
                    contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase))
                {
                    Response.Headers["Content-Disposition"] = "attachment";
                }

                return File(stream, contentType);
            }
            catch (FileNotFoundException)
            {
                return NotFound();
            }
        }

        /// <summary>
        /// BUD-01: HEAD /{sha256}.ext - Check blob exists (with file extension)
        /// </summary>
        [HttpHead("{sha256:regex(^[[a-fA-F0-9]]{{64}}$)}.{ext}")]
        public Task<IActionResult> HeadBlobWithExt(string sha256) => HeadBlobCore(sha256);

        /// <summary>
        /// BUD-01: HEAD /{sha256} - Check blob exists
        /// </summary>
        [HttpHead("{sha256:regex(^[[a-fA-F0-9]]{{64}}$)}")]
        public Task<IActionResult> HeadBlob(string sha256) => HeadBlobCore(sha256);

        private async Task<IActionResult> HeadBlobCore(string sha256)
        {
            if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            {
                return NotFound();
            }

            sha256 = sha256.ToLowerInvariant();

            var exists = await this.blobStorage.BlobExistsAsync(sha256);
            if (!exists)
            {
                return NotFound();
            }

            try
            {
                var (_, contentType, size) = await this.blobStorage.GetBlobAsync(sha256);

                Response.Headers["Content-Type"] = contentType;
                Response.Headers["Content-Length"] = size.ToString();
                Response.Headers["Accept-Ranges"] = "bytes";
                Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                Response.Headers["X-Content-Type-Options"] = "nosniff";

                return Ok();
            }
            catch (FileNotFoundException)
            {
                return NotFound();
            }
        }

        /// <summary>
        /// BUD-02: PUT /upload - Upload blob
        /// </summary>
        [HttpPut("upload")]
        public async Task<IActionResult> UploadBlob()
        {
            if (!options.Enabled)
            {
                return StatusCode(503);
            }

            // Emergency killswitch or read-only mode
            if (!this.blossomManager.UploadsEnabled || this.blossomManager.AccessMode == "ReadOnly")
            {
                Response.Headers["X-Reason"] = "Uploads are temporarily paused by relay operator";
                return StatusCode(503);
            }

            var baseUrl = $"{Request.Scheme}://{Request.Host}";

            // Validate auth token
            var authResult = this.tokenValidator.Validate(
                Request.Headers["Authorization"].FirstOrDefault(),
                "upload",
                Request.Headers["X-SHA-256"].FirstOrDefault()?.ToLowerInvariant(),
                baseUrl);

            if (!authResult.IsValid)
            {
                Response.Headers["X-Reason"] = authResult.Error ?? "Unauthorized";
                return Unauthorized();
            }

            var pubkey = authResult.PublicKey!;

            // Moderation check: is pubkey banned from relay?
            if (this.moderationCache.IsPubkeyBanned(pubkey, out var banReason))
            {
                Response.Headers["X-Reason"] = $"Pubkey is banned: {banReason ?? "Restricted"}";
                return StatusCode(403);
            }

            // Moderation check: is pubkey banned from Blossom uploads?
            if (this.moderationCache.IsBlossomUploadBanned(pubkey, out var uploadBanReason))
            {
                Response.Headers["X-Reason"] = $"Blossom uploads forbidden for this pubkey: {uploadBanReason ?? "Restricted"}";
                return StatusCode(403);
            }

            // Whitelist-only access mode check
            if (this.blossomManager.AccessMode == "WhitelistedOnly" && !this.moderationCache.IsPubkeyWhitelisted(pubkey))
            {
                Response.Headers["X-Reason"] = "Blossom access restricted to whitelisted pubkeys";
                return StatusCode(403);
            }

            // check MIME type from request header
            var rawContentType = Request.ContentType ?? "application/octet-stream";
            var mediaType = rawContentType.Split(';')[0].Trim().ToLowerInvariant();

            if (options.BlockedMimeTypes.Length > 0 && options.BlockedMimeTypes.Any(m => mediaType.Equals(m.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                Response.Headers["X-Reason"] = $"MIME type '{mediaType}' is blocked by this server";
                return StatusCode(415);
            }

            if (options.AllowedMimeTypes.Length > 0 && !options.AllowedMimeTypes.Any(m => mediaType.Equals(m.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                Response.Headers["X-Reason"] = $"MIME type '{mediaType}' is not allowed by this server";
                return StatusCode(415);
            }

            // check content length as early hint (enforced on actual bytes read below)
            if (Request.ContentLength.HasValue && Request.ContentLength.Value > options.MaxUploadSizeBytes)
            {
                Response.Headers["X-Reason"] = $"File too large, max {options.MaxUploadSizeBytes} bytes";
                return StatusCode(413);
            }

            // check global storage quota
            if (options.MaxTotalStorageBytes > 0)
            {
                var totalUsed = await this.blobStorage.GetTotalStorageUsedAsync();
                if (totalUsed >= options.MaxTotalStorageBytes)
                {
                    Response.Headers["X-Reason"] = "Server storage full";
                    return StatusCode(507);
                }
            }

            // check per-user storage quota (custom override takes precedence)
            var hasCustomQuota = this.moderationCache.TryGetCustomStorageQuota(pubkey, out var effectiveUserQuota);
            if (!hasCustomQuota)
            {
                effectiveUserQuota = options.MaxStoragePerUserBytes;
            }

            if (hasCustomQuota && effectiveUserQuota == 0)
            {
                Response.Headers["X-Reason"] = "User storage quota is 0 (uploads disabled for this user)";
                return StatusCode(403);
            }

            long userUsed = 0;
            if (effectiveUserQuota > 0)
            {
                userUsed = await this.blobStorage.GetUserStorageUsedAsync(pubkey);
                if (userUsed >= effectiveUserQuota)
                {
                    Response.Headers["X-Reason"] = $"User storage quota exceeded ({userUsed}/{effectiveUserQuota} bytes)";
                    return StatusCode(507);
                }
            }

            // stream body to temp file while computing SHA-256 and enforcing size limit
            var tempPath = Path.Combine(options.StoragePath, $".upload_{Guid.NewGuid():N}.tmp");
            var directory = Path.GetDirectoryName(tempPath)!;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string sha256;
            long totalBytesRead = 0;

            try
            {
                await using (var tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    using var hashStream = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    int bytesRead;

                    while ((bytesRead = await Request.Body.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        totalBytesRead += bytesRead;
                        if (totalBytesRead > options.MaxUploadSizeBytes)
                        {
                            Response.Headers["X-Reason"] = $"File too large, max {options.MaxUploadSizeBytes} bytes";
                            return StatusCode(413);
                        }
                        await tempStream.WriteAsync(buffer, 0, bytesRead);
                        hashStream.AppendData(buffer, 0, bytesRead);
                    }

                    sha256 = Convert.ToHexStringLower(hashStream.GetHashAndReset());
                }

                // verify XSHA256 header if provided
                var declaredSha = Request.Headers["X-SHA-256"].FirstOrDefault()?.ToLowerInvariant();
                if (!string.IsNullOrEmpty(declaredSha) && declaredSha != sha256)
                {
                    Response.Headers["X-Reason"] = $"SHA-256 mismatch: computed {sha256}, declared {declaredSha}";
                    return StatusCode(409);
                }

                // Sniff MIME type from file content if client didn't specify or sent generic octet-stream
                if (mediaType is "application/octet-stream" or "binary/octet-stream" or "")
                {
                    var sniffed = Services.BlossomManagerService.SniffMimeType(tempPath, mediaType);
                    if (sniffed != "application/octet-stream")
                    {
                        mediaType = sniffed;
                    }
                }

                if (options.AllowedMimeTypes.Length > 0 && !options.AllowedMimeTypes.Any(m => mediaType.Equals(m.Trim(), StringComparison.OrdinalIgnoreCase)) && mediaType != "application/octet-stream")
                {
                    Response.Headers["X-Reason"] = $"Detected MIME type '{mediaType}' is not allowed by this server";
                    return StatusCode(415);
                }

                // verify file magic bytes to prevent disguised executable uploads
                var (isValidMagic, magicError) = VerifyBlobMagicBytes(tempPath, mediaType);
                if (!isValidMagic)
                {
                    Response.Headers["X-Reason"] = magicError ?? "Invalid file content for declared MIME type";
                    return StatusCode(415);
                }

                // check if the uploaded size would exceed user or total quota
                if (effectiveUserQuota > 0 && userUsed + totalBytesRead > effectiveUserQuota)
                {
                    Response.Headers["X-Reason"] = $"Upload exceeds user storage quota ({userUsed + totalBytesRead}/{effectiveUserQuota} bytes)";
                    return StatusCode(507);
                }

                if (options.MaxTotalStorageBytes > 0)
                {
                    var totalUsed = await this.blobStorage.GetTotalStorageUsedAsync();
                    if (totalUsed + totalBytesRead > options.MaxTotalStorageBytes)
                    {
                        Response.Headers["X-Reason"] = "Upload exceeds server storage quota";
                        return StatusCode(507);
                    }
                }

                // store blob
                var descriptor = await this.blobStorage.StoreBlobAsync(sha256, tempPath, mediaType, authResult.PublicKey!, baseUrl);

                Response.Headers["X-SHA-256"] = sha256;

                return StatusCode(201, descriptor);
            }
            finally
            {
                if (System.IO.File.Exists(tempPath))
                {
                    System.IO.File.Delete(tempPath);
                }
            }
        }

        /// <summary>
        /// BUD-06: HEAD /upload - Check upload requirements
        /// </summary>
        [HttpHead("upload")]
        public IActionResult HeadUpload()
        {
            if (!options.Enabled || !this.blossomManager.UploadsEnabled || this.blossomManager.AccessMode == "ReadOnly")
            {
                Response.Headers["X-Reason"] = "Uploads are temporarily paused by relay operator";
                return StatusCode(503);
            }

            Response.Headers["X-Max-Size"] = options.MaxUploadSizeBytes.ToString();

            if (options.AllowedMimeTypes.Length > 0)
            {
                Response.Headers["X-Allow"] = string.Join(", ", options.AllowedMimeTypes);
            }

            return Ok();
        }

        /// <summary>
        /// GET /blossom/quota - Get storage quota for authenticated user
        /// </summary>
        [HttpGet("blossom/quota")]
        public async Task<IActionResult> GetQuota()
        {
            if (!options.Enabled)
            {
                return StatusCode(503);
            }

            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var authResult = this.tokenValidator.Validate(
                Request.Headers["Authorization"].FirstOrDefault(),
                "list",
                null,
                baseUrl);

            if (!authResult.IsValid)
            {
                Response.Headers["X-Reason"] = authResult.Error ?? "Unauthorized";
                return Unauthorized();
            }

            var pubkey = authResult.PublicKey!;
            var used = await this.blobStorage.GetUserStorageUsedAsync(pubkey);
            var maxQuota = this.moderationCache.TryGetCustomStorageQuota(pubkey, out var customQuota)
                ? customQuota
                : options.MaxStoragePerUserBytes;

            return Ok(new
            {
                used,
                max = maxQuota,
                upload_banned = this.moderationCache.IsBlossomUploadBanned(pubkey, out _),
                uploads_enabled = this.blossomManager.UploadsEnabled && this.blossomManager.AccessMode != "ReadOnly"
            });
        }

        /// <summary>
        /// BUD-12: DELETE /{sha256} - Delete blob
        /// </summary>
        [HttpDelete("{sha256:regex(^[[a-fA-F0-9]]{{64}}$)}")]
        public async Task<IActionResult> DeleteBlob(string sha256)
        {
            if (!options.Enabled)
            {
                return StatusCode(503);
            }

            sha256 = sha256.ToLowerInvariant();
            var baseUrl = $"{Request.Scheme}://{Request.Host}";

            var authResult = this.tokenValidator.Validate(
                Request.Headers["Authorization"].FirstOrDefault(),
                "delete",
                sha256,
                baseUrl);

            if (!authResult.IsValid)
            {
                Response.Headers["X-Reason"] = authResult.Error ?? "Unauthorized";
                return Unauthorized();
            }

            var deleted = await this.blobStorage.DeleteBlobAsync(sha256, authResult.PublicKey!);

            if (!deleted)
            {
                return NotFound();
            }

            return Ok();
        }

        /// <summary>
        /// GET /list - List blobs for authenticated user
        /// </summary>
        [HttpGet("list")]
        public async Task<IActionResult> ListBlobs([FromQuery] string? cursor = null, [FromQuery] int limit = 100)
        {
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var authResult = this.tokenValidator.Validate(
                Request.Headers["Authorization"].FirstOrDefault(),
                "list",
                null,
                baseUrl);

            if (!authResult.IsValid)
            {
                Response.Headers["X-Reason"] = authResult.Error ?? "Unauthorized";
                return Unauthorized();
            }

            var blobs = await this.blobStorage.ListBlobsAsync(authResult.PublicKey!, cursor, Math.Min(limit, 1000), baseUrl);

            return Ok(blobs);
        }

        /// <summary>
        /// BUD-11: GET /.well-known/blossom - Server info (optional)
        /// </summary>
        [HttpGet(".well-known/blossom")]
        public IActionResult BlossomInfo()
        {
            return Ok(new
            {
                software = "libregram",
                version = "0.0.1",
                max_upload_size = options.MaxUploadSizeBytes,
                allowed_types = options.AllowedMimeTypes,
                blocked_types = options.BlockedMimeTypes,
                auth_required = options.AuthRequired
            });
        }

        private static (bool IsValid, string? Error) VerifyBlobMagicBytes(string filePath, string mediaType)
        {
            try
            {
                var buffer = new byte[16];
                int read;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    read = fs.Read(buffer, 0, buffer.Length);
                }

                if (read >= 2 && buffer[0] == 0x4D && buffer[1] == 0x5A) // MZ (Windows PE / EXE)
                {
                    return (false, "Executable binaries are forbidden");
                }

                if (read >= 4 && buffer[0] == 0x7F && buffer[1] == 0x45 && buffer[2] == 0x4C && buffer[3] == 0x46) // \x7fELF
                {
                    return (false, "Executable binaries are forbidden");
                }

                if (read >= 4 && ((buffer[0] == 0xFE && buffer[1] == 0xED && buffer[2] == 0xFA && buffer[3] == 0xCE) ||
                                  (buffer[0] == 0xFE && buffer[1] == 0xED && buffer[2] == 0xFA && buffer[3] == 0xCF) ||
                                  (buffer[0] == 0xCE && buffer[1] == 0xFA && buffer[2] == 0xED && buffer[3] == 0xFE) ||
                                  (buffer[0] == 0xCF && buffer[1] == 0xFA && buffer[2] == 0xED && buffer[3] == 0xFE))) // Mach-O
                {
                    return (false, "Executable binaries are forbidden");
                }

                if (mediaType == "image/png")
                {
                    if (read < 8 || buffer[0] != 0x89 || buffer[1] != 0x50 || buffer[2] != 0x4E || buffer[3] != 0x47 ||
                        buffer[4] != 0x0D || buffer[5] != 0x0A || buffer[6] != 0x1A || buffer[7] != 0x0A)
                    {
                        return (false, "Content does not match image/png format");
                    }
                }
                else if (mediaType == "image/jpeg")
                {
                    if (read < 3 || buffer[0] != 0xFF || buffer[1] != 0xD8 || buffer[2] != 0xFF)
                    {
                        return (false, "Content does not match image/jpeg format");
                    }
                }
                else if (mediaType == "image/gif")
                {
                    if (read < 4 || buffer[0] != 0x47 || buffer[1] != 0x49 || buffer[2] != 0x46 || buffer[3] != 0x38)
                    {
                        return (false, "Content does not match image/gif format");
                    }
                }
                else if (mediaType == "image/webp")
                {
                    if (read < 12 || buffer[0] != 0x52 || buffer[1] != 0x49 || buffer[2] != 0x46 || buffer[3] != 0x46 ||
                        buffer[8] != 0x57 || buffer[9] != 0x45 || buffer[10] != 0x42 || buffer[11] != 0x50)
                    {
                        return (false, "Content does not match image/webp format");
                    }
                }
                else if (mediaType == "application/pdf")
                {
                    if (read < 5 || buffer[0] != 0x25 || buffer[1] != 0x50 || buffer[2] != 0x44 || buffer[3] != 0x46 || buffer[4] != 0x2D)
                    {
                        return (false, "Content does not match application/pdf format");
                    }
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, "Failed to verify file integrity: " + ex.Message);
            }
        }
    }
}
