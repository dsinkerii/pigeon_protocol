using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Netstr.Blossom;
using Netstr.Options;
using System.Security.Cryptography;
using System.Text.Json;

namespace Netstr.Controllers
{
    [Route("/")]
    public class BlossomController : Controller
    {
        private readonly IBlobStorageService blobStorage;
        private readonly BlossomTokenValidator tokenValidator;
        private readonly BlossomOptions options;
        private readonly ILogger<BlossomController> logger;

        public BlossomController(
            IBlobStorageService blobStorage,
            BlossomTokenValidator tokenValidator,
            IOptions<BlossomOptions> options,
            ILogger<BlossomController> logger)
        {
            this.blobStorage = blobStorage;
            this.tokenValidator = tokenValidator;
            this.options = options.Value;
            this.logger = logger;
        }

        /// <summary>
        /// BUD-01: GET /{sha256}.ext - Retrieve blob (with file extension)
        /// </summary>
        [HttpGet("{sha256}.{ext}")]
        public Task<IActionResult> GetBlobWithExt(string sha256) => GetBlobCore(sha256);

        /// <summary>
        /// BUD-01: GET /{sha256} - Retrieve blob
        /// </summary>
        [HttpGet("{sha256:regex(^[[a-fA-F0-9]]{{64}}$)}")]
        public Task<IActionResult> GetBlob(string sha256) => GetBlobCore(sha256);

        private async Task<IActionResult> GetBlobCore(string sha256)
        {
            sha256 = sha256.ToLowerInvariant();

            try
            {
                var (stream, contentType, size) = await this.blobStorage.GetBlobAsync(sha256);

                Response.Headers["Content-Type"] = contentType;
                Response.Headers["Content-Length"] = size.ToString();
                Response.Headers["Accept-Ranges"] = "bytes";
                Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";

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
        [HttpHead("{sha256}.{ext}")]
        public Task<IActionResult> HeadBlobWithExt(string sha256) => HeadBlobCore(sha256);

        /// <summary>
        /// BUD-01: HEAD /{sha256} - Check blob exists
        /// </summary>
        [HttpHead("{sha256:regex(^[[a-fA-F0-9]]{{64}}$)}")]
        public Task<IActionResult> HeadBlob(string sha256) => HeadBlobCore(sha256);

        private async Task<IActionResult> HeadBlobCore(string sha256)
        {
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

            // Validate auth token
            var authResult = this.tokenValidator.Validate(
                Request.Headers["Authorization"].FirstOrDefault(),
                "upload",
                Request.Headers["X-SHA-256"].FirstOrDefault()?.ToLowerInvariant());

            if (!authResult.IsValid)
            {
                Response.Headers["X-Reason"] = authResult.Error ?? "Unauthorized";
                return Unauthorized();
            }

            // check content length as early hint (but we enforce on actual bytes read!)
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

            // check per-user storage quota
            if (options.MaxStoragePerUserBytes > 0)
            {
                var userUsed = await this.blobStorage.GetUserStorageUsedAsync(authResult.PublicKey!);
                if (userUsed >= options.MaxStoragePerUserBytes)
                {
                    Response.Headers["X-Reason"] = "User storage quota exceeded";
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

                // MIME type from client header (used for metadata only, not enforced server-side)
                var contentType = Request.ContentType ?? "application/octet-stream";

                // store blob
                var baseUrl = $"{Request.Scheme}://{Request.Host}";
                var descriptor = await this.blobStorage.StoreBlobAsync(sha256, tempPath, contentType, authResult.PublicKey!, baseUrl);

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
            if (!options.Enabled)
            {
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

            var authResult = this.tokenValidator.Validate(
                Request.Headers["Authorization"].FirstOrDefault(),
                "list");

            if (!authResult.IsValid)
            {
                Response.Headers["X-Reason"] = authResult.Error ?? "Unauthorized";
                return Unauthorized();
            }

            var used = await this.blobStorage.GetUserStorageUsedAsync(authResult.PublicKey!);

            return Ok(new
            {
                used,
                max = options.MaxStoragePerUserBytes
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

            var authResult = this.tokenValidator.Validate(
                Request.Headers["Authorization"].FirstOrDefault(),
                "delete",
                sha256);

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
            var authResult = this.tokenValidator.Validate(
                Request.Headers["Authorization"].FirstOrDefault(),
                "list");

            if (!authResult.IsValid)
            {
                Response.Headers["X-Reason"] = authResult.Error ?? "Unauthorized";
                return Unauthorized();
            }

            var baseUrl = $"{Request.Scheme}://{Request.Host}";
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
    }
}
