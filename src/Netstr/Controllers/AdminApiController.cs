using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Netstr.Data;
using Netstr.Messaging;
using Netstr.Services;

namespace Netstr.Controllers
{
    [ApiController]
    [Route("api/admin")]
    public class AdminApiController : ControllerBase
    {
        private readonly ITelemetryService telemetryService;
        private readonly IAdminAuthService authService;
        private readonly IModerationService moderationService;
        private readonly IBlossomManagerService blossomManager;
        private readonly IWebSocketAdapterCollection webSocketCollection;
        private readonly IHostEnvironment environment;
        private readonly ILogger<AdminApiController> logger;

        public AdminApiController(
            ITelemetryService telemetryService,
            IAdminAuthService authService,
            IModerationService moderationService,
            IBlossomManagerService blossomManager,
            IWebSocketAdapterCollection webSocketCollection,
            IHostEnvironment environment,
            ILogger<AdminApiController> logger)
        {
            this.telemetryService = telemetryService;
            this.authService = authService;
            this.moderationService = moderationService;
            this.blossomManager = blossomManager;
            this.webSocketCollection = webSocketCollection;
            this.environment = environment;
            this.logger = logger;
        }

        private bool IsAuthorized(out string username)
        {
            username = "admin";
            var authHeader = Request.Headers["Authorization"].FirstOrDefault();
            string? token = null;

            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = authHeader.Substring("Bearer ".Length).Trim();
            }

            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            return this.authService.ValidateToken(token, out username!);
        }

        // ==========================================
        // 1. AUTHENTICATION
        // ==========================================

        [HttpPost("auth/login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var (success, token, error) = await this.authService.AuthenticateAsync(request.Username, request.Password, HttpContext.RequestAborted);
            if (!success)
            {
                this.logger.LogWarning("Admin login failed for user '{Username}' from IP {IpAddress}", request.Username, clientIp);
                return Unauthorized(new { error = error ?? "Invalid credentials" });
            }

            this.logger.LogInformation("Admin login successful for user '{Username}' from IP {IpAddress}", request.Username, clientIp);
            return Ok(new { token, username = request.Username });
        }

        [HttpPost("auth/logout")]
        public IActionResult Logout()
        {
            if (IsAuthorized(out _))
            {
                var authHeader = Request.Headers["Authorization"].FirstOrDefault();
                if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    this.authService.RevokeToken(authHeader.Substring(7).Trim());
                }
            }
            return Ok(new { success = true });
        }

        [HttpGet("auth/nostr-challenge")]
        public IActionResult GetNostrChallenge()
        {
            var challenge = this.authService.GenerateNostrChallenge();
            return Ok(new { challenge });
        }

        [HttpPost("auth/nostr-login")]
        public async Task<IActionResult> NostrLogin([FromBody] NostrLoginRequest request)
        {
            var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var (success, token, error) = await this.authService.AuthenticateNostrAsync(
                request.PublicKey, request.Challenge, request.Signature, HttpContext.RequestAborted);

            if (!success)
            {
                this.logger.LogWarning("Admin Nostr login failed for pubkey '{Pubkey}' from IP {IpAddress}: {Error}", request.PublicKey, clientIp, error);
                return Unauthorized(new { error });
            }

            this.logger.LogInformation("Admin Nostr login successful for pubkey '{Pubkey}' from IP {IpAddress}", request.PublicKey, clientIp);
            return Ok(new { token, pubkey = request.PublicKey });
        }

        [HttpPost("auth/reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
        {
            if (!IsAuthorized(out var username))
            {
                return Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 10)
            {
                return BadRequest(new { error = "Password must be at least 10 characters." });
            }

            await this.authService.ResetPasswordAsync(username, request.NewPassword, HttpContext.RequestAborted);
            return Ok(new { success = true, message = "Password updated successfully." });
        }

        // ==========================================
        // 2. TELEMETRY & LIVE STATS
        // ==========================================

        [HttpGet("telemetry")]
        public async Task<IActionResult> GetTelemetry()
        {
            if (!IsAuthorized(out _))
            {
                return Unauthorized();
            }

            var snapshot = await this.telemetryService.GetSnapshotAsync(HttpContext.RequestAborted);
            return Ok(snapshot);
        }

        // ==========================================
        // 3. CONNECTED CLIENTS MANAGEMENT
        // ==========================================

        [HttpGet("clients")]
        public IActionResult GetConnectedClients()
        {
            if (!IsAuthorized(out _))
            {
                return Unauthorized();
            }

            var now = DateTimeOffset.UtcNow;
            var clients = this.webSocketCollection.GetAll().Select(adapter => new
            {
                clientId = adapter.Context.ClientId,
                ipAddress = adapter.Context.IpAddress,
                publicKey = adapter.Context.PublicKey,
                isAuthenticated = adapter.Context.IsAuthenticated(),
                connectedAt = adapter.Context.ConnectedAt,
                durationSeconds = (long)(now - adapter.Context.ConnectedAt).TotalSeconds,
                subscriptionCount = adapter.Subscriptions?.GetAll()?.Count() ?? 0
            }).ToArray();

            return Ok(clients);
        }

        [HttpPost("clients/{clientId}/disconnect")]
        public async Task<IActionResult> DisconnectClient(string clientId, [FromBody] DisconnectRequest? request)
        {
            if (!IsAuthorized(out _))
            {
                return Unauthorized();
            }

            var adapter = this.webSocketCollection.GetById(clientId);
            if (adapter == null)
            {
                return NotFound(new { error = "Client not found or already disconnected." });
            }

            var reason = request?.Reason ?? "Disconnected by administrator";
            await adapter.CloseAsync(reason);
            this.webSocketCollection.Remove(clientId);

            return Ok(new { success = true, message = $"Client {clientId} disconnected." });
        }

        [HttpGet("clients/{clientId}/subscriptions")]
        public IActionResult GetClientSubscriptions(string clientId)
        {
            if (!IsAuthorized(out _))
            {
                return Unauthorized();
            }

            var adapter = this.webSocketCollection.GetById(clientId);
            if (adapter == null)
            {
                return NotFound(new { error = "Client not found or disconnected." });
            }

            var subs = adapter.Subscriptions?.GetAll().Select(kv => new
            {
                id = kv.Key,
                createdAt = kv.Value.CreatedAt,
                lastActivityAt = kv.Value.LastActivityAt,
                eventsSent = kv.Value.EventsSentCount,
                filters = kv.Value.Filters.Select(f => new
                {
                    ids = f.Ids,
                    authors = f.Authors,
                    kinds = f.Kinds,
                    since = f.Since?.ToUnixTimeSeconds(),
                    until = f.Until?.ToUnixTimeSeconds(),
                    limit = f.Limit
                })
            }).ToArray();

            return Ok(subs ?? Array.Empty<object>());
        }

        [HttpPost("clients/{clientId}/subscriptions/{subscriptionId}/close")]
        public IActionResult CloseClientSubscription(string clientId, string subscriptionId)
        {
            if (!IsAuthorized(out _))
            {
                return Unauthorized();
            }

            var adapter = this.webSocketCollection.GetById(clientId);
            if (adapter == null)
            {
                return NotFound(new { error = "Client not found or disconnected." });
            }

            adapter.Subscriptions.RemoveById(subscriptionId);
            adapter.SendClosed(subscriptionId, "closed by relay administrator");

            return Ok(new { success = true, message = $"Subscription {subscriptionId} closed." });
        }

        // ==========================================
        // 4. PUBKEYS & BANS MANAGEMENT
        // ==========================================

        [HttpGet("pubkeys")]
        public async Task<IActionResult> GetPubkeys()
        {
            if (!IsAuthorized(out _))
            {
                return Unauthorized();
            }

            var rules = await this.moderationService.GetPubkeyRulesAsync(HttpContext.RequestAborted);
            return Ok(rules);
        }

        [HttpPost("pubkeys/rule")]
        public async Task<IActionResult> SetPubkeyRule([FromBody] SetPubkeyRuleRequest request)
        {
            if (!IsAuthorized(out var adminName))
            {
                return Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.PublicKey))
            {
                return BadRequest(new { error = "PublicKey is required." });
            }

            await this.moderationService.SetPubkeyRuleAsync(
                request.PublicKey, request.Status, request.Reason, request.CustomStorageQuotaBytes, adminName, HttpContext.RequestAborted);

            return Ok(new { success = true });
        }

        [HttpDelete("pubkeys/{pubkey}")]
        public async Task<IActionResult> DeletePubkeyRule(string pubkey)
        {
            if (!IsAuthorized(out var adminName))
            {
                return Unauthorized();
            }

            await this.moderationService.RemovePubkeyRuleAsync(pubkey, adminName, HttpContext.RequestAborted);
            return Ok(new { success = true });
        }

        // ==========================================
        // 5. BANNED EVENTS (TOMBSTONING)
        // ==========================================

        [HttpGet("banned-events")]
        public async Task<IActionResult> GetBannedEvents([FromQuery] int limit = 50)
        {
            if (!IsAuthorized(out _))
            {
                return Unauthorized();
            }

            var banned = await this.moderationService.GetBannedEventsAsync(limit, HttpContext.RequestAborted);
            return Ok(banned);
        }

        [HttpPost("banned-events")]
        public async Task<IActionResult> BanEvent([FromBody] BanEventRequest request)
        {
            if (!IsAuthorized(out var adminName))
            {
                return Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.EventId))
            {
                return BadRequest(new { error = "EventId is required." });
            }

            await this.moderationService.BanEventAsync(request.EventId, request.Reason, adminName, HttpContext.RequestAborted);
            return Ok(new { success = true });
        }

        [HttpDelete("banned-events/{eventId}")]
        public async Task<IActionResult> UnbanEvent(string eventId)
        {
            if (!IsAuthorized(out var adminName))
            {
                return Unauthorized();
            }

            await this.moderationService.UnbanEventAsync(eventId, adminName, HttpContext.RequestAborted);
            return Ok(new { success = true });
        }

        // ==========================================
        // 6. REPORTS & APPEALS
        // ==========================================

        [HttpGet("reports")]
        public async Task<IActionResult> GetReports([FromQuery] ReportStatus? status, [FromQuery] int limit = 50)
        {
            if (!IsAuthorized(out _))
            {
                return Unauthorized();
            }

            var reports = await this.moderationService.GetReportsAsync(status, limit, HttpContext.RequestAborted);
            return Ok(reports);
        }

        [HttpPost("reports/{id}/resolve")]
        public async Task<IActionResult> ResolveReport(int id, [FromBody] ResolveReportRequest request)
        {
            if (!IsAuthorized(out var adminName))
            {
                return Unauthorized();
            }

            var success = await this.moderationService.ResolveReportAsync(id, request.Status, request.Notes, adminName, HttpContext.RequestAborted);
            if (!success)
            {
                return NotFound();
            }

            return Ok(new { success = true });
        }

        [HttpGet("appeals")]
        public async Task<IActionResult> GetAppeals([FromQuery] AppealStatus? status, [FromQuery] int limit = 50)
        {
            if (!IsAuthorized(out _))
            {
                return Unauthorized();
            }

            var appeals = await this.moderationService.GetAppealsAsync(status, limit, HttpContext.RequestAborted);
            return Ok(appeals);
        }

        [HttpPost("appeals/{id}/review")]
        public async Task<IActionResult> ReviewAppeal(int id, [FromBody] ReviewAppealRequest request)
        {
            if (!IsAuthorized(out var adminName))
            {
                return Unauthorized();
            }

            var success = await this.moderationService.ReviewAppealAsync(id, request.Status, request.Comment, adminName, HttpContext.RequestAborted);
            if (!success)
            {
                return NotFound();
            }

            return Ok(new { success = true });
        }

        [HttpGet("audit-logs")]
        public async Task<IActionResult> GetAuditLogs([FromQuery] int limit = 100)
        {
            if (!IsAuthorized(out _))
            {
                return Unauthorized();
            }

            var logs = await this.moderationService.GetAuditLogsAsync(limit, HttpContext.RequestAborted);
            return Ok(logs);
        }

        // ==========================================
        // 7. RELAY CONFIGURATION (appsettings.json)
        // ==========================================

        [HttpGet("config")]
        public IActionResult GetConfig()
        {
            if (!IsAuthorized(out _))
            {
                return Unauthorized();
            }

            var configPath = Path.Combine(this.environment.ContentRootPath, "appsettings.json");
            if (!System.IO.File.Exists(configPath))
            {
                return NotFound(new { error = "appsettings.json not found." });
            }

            var jsonContent = System.IO.File.ReadAllText(configPath);
            return Content(jsonContent, "application/json");
        }

        [HttpPost("config")]
        public IActionResult SaveConfig([FromBody] JsonElement newConfigJson)
        {
            if (!IsAuthorized(out var adminName))
            {
                return Unauthorized();
            }

            var configPath = Path.Combine(this.environment.ContentRootPath, "appsettings.json");
            var backupPath = Path.Combine(this.environment.ContentRootPath, $"appsettings.backup_{DateTime.UtcNow:yyyyMMddHHmmss}.json");

            try
            {
                // Create backup first
                if (System.IO.File.Exists(configPath))
                {
                    System.IO.File.Copy(configPath, backupPath, true);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var formatted = JsonSerializer.Serialize(newConfigJson, options);
                System.IO.File.WriteAllText(configPath, formatted);

                this.logger.LogInformation($"Configuration updated via Admin Panel by {adminName}");
                return Ok(new { success = true, message = "Configuration saved successfully." });
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Failed to save configuration");
                return StatusCode(500, new { error = "Failed to save configuration: " + ex.Message });
            }
        }

        // ==========================================
        // 8. LOGS
        // ==========================================

        [HttpGet("logs")]
        public IActionResult GetLogs([FromQuery] int lines = 100)
        {
            if (!IsAuthorized(out _))
            {
                return Unauthorized();
            }

            var logsDir = Path.Combine(this.environment.ContentRootPath, "logs");
            if (!Directory.Exists(logsDir))
            {
                return Ok(new string[] { "Log directory does not exist yet." });
            }

            var latestLog = Directory.GetFiles(logsDir, "*.txt")
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();

            if (latestLog == null)
            {
                return Ok(new string[] { "No log files found." });
            }

            try
            {
                using var fs = new FileStream(latestLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                var allLines = new List<string>();
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    allLines.Add(line);
                }

                var output = allLines.TakeLast(lines).ToArray();
                return Ok(output);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Failed to read logs: " + ex.Message });
            }
        }

        // ==========================================
        // 9. BLOSSOM MANAGEMENT
        // ==========================================

        [HttpGet("blossom/stats")]
        public async Task<IActionResult> GetBlossomStats(CancellationToken ct)
        {
            if (!IsAuthorized(out _))
            {
                return Unauthorized();
            }

            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var stats = await this.blossomManager.GetStatsAsync(baseUrl, ct);
            return Ok(stats);
        }

        [HttpPost("blossom/killswitch")]
        public IActionResult SetBlossomKillswitch([FromBody] BlossomKillswitchRequest request)
        {
            if (!IsAuthorized(out var adminName))
            {
                return Unauthorized();
            }

            this.blossomManager.SetKillswitch(request.UploadsEnabled, request.AccessMode);
            this.logger.LogInformation($"Admin '{adminName}' set Blossom killswitch: UploadsEnabled={request.UploadsEnabled}, AccessMode={request.AccessMode ?? "unchanged"}");

            return Ok(new
            {
                success = true,
                uploadsEnabled = this.blossomManager.UploadsEnabled,
                accessMode = this.blossomManager.AccessMode
            });
        }

        [HttpGet("blossom/blobs")]
        public async Task<IActionResult> QueryBlossomBlobs(
            [FromQuery] string? search = null,
            [FromQuery] string? pubkey = null,
            [FromQuery] string? category = null,
            [FromQuery] long? minBytes = null,
            [FromQuery] string? sortBy = null,
            [FromQuery] bool sortDesc = true,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            CancellationToken ct = default)
        {
            if (!IsAuthorized(out _))
            {
                return Unauthorized();
            }

            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var (blobs, total) = await this.blossomManager.QueryBlobsAsync(
                search, pubkey, category, minBytes, sortBy, sortDesc, page, pageSize, baseUrl, ct);

            return Ok(new { blobs, total, page, pageSize });
        }

        [HttpDelete("blossom/blobs/{sha256}")]
        public async Task<IActionResult> DeleteBlossomBlob(string sha256, CancellationToken ct)
        {
            if (!IsAuthorized(out var adminName))
            {
                return Unauthorized();
            }

            var success = await this.blossomManager.DeleteBlobAsync(sha256, adminName, ct);
            if (!success)
            {
                return NotFound(new { error = "Blob not found" });
            }

            return Ok(new { success = true });
        }

        [HttpPost("blossom/blobs/batch-delete")]
        public async Task<IActionResult> BatchDeleteBlossomBlobs([FromBody] BlossomBatchDeleteRequest request, CancellationToken ct)
        {
            if (!IsAuthorized(out var adminName))
            {
                return Unauthorized();
            }

            if (request.Hashes == null || request.Hashes.Count == 0)
            {
                return BadRequest(new { error = "No hashes provided" });
            }

            var count = await this.blossomManager.BatchDeleteBlobsAsync(request.Hashes, adminName, ct);
            return Ok(new { success = true, deletedCount = count });
        }

        [HttpGet("blossom/users")]
        public async Task<IActionResult> QueryBlossomUsers(
            [FromQuery] string? search = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            CancellationToken ct = default)
        {
            if (!IsAuthorized(out _))
            {
                return Unauthorized();
            }

            var (users, total) = await this.blossomManager.QueryUsersAsync(search, page, pageSize, ct);
            return Ok(new { users, total, page, pageSize });
        }

        [HttpPost("blossom/users/{pubkey}/quota")]
        public async Task<IActionResult> SetBlossomUserQuota(string pubkey, [FromBody] BlossomSetQuotaRequest request, CancellationToken ct)
        {
            if (!IsAuthorized(out var adminName))
            {
                return Unauthorized();
            }

            var success = await this.blossomManager.SetUserQuotaAsync(pubkey, request.QuotaBytes, adminName, ct);
            return Ok(new { success });
        }

        [HttpPost("blossom/users/{pubkey}/ban-uploads")]
        public async Task<IActionResult> SetBlossomUserUploadBanned(string pubkey, [FromBody] BlossomBanUploadsRequest request, CancellationToken ct)
        {
            if (!IsAuthorized(out var adminName))
            {
                return Unauthorized();
            }

            var success = await this.blossomManager.SetUserUploadBannedAsync(pubkey, request.Banned, request.Reason, adminName, ct);
            return Ok(new { success });
        }

        [HttpDelete("blossom/users/{pubkey}/blobs")]
        public async Task<IActionResult> PurgeBlossomUserBlobs(string pubkey, CancellationToken ct)
        {
            if (!IsAuthorized(out var adminName))
            {
                return Unauthorized();
            }

            var count = await this.blossomManager.PurgeUserBlobsAsync(pubkey, adminName, ct);
            return Ok(new { success = true, deletedCount = count });
        }

        [HttpGet("blossom/reconcile")]
        public async Task<IActionResult> ReconcileBlossomStorage(CancellationToken ct)
        {
            if (!IsAuthorized(out _))
            {
                return Unauthorized();
            }

            var report = await this.blossomManager.ReconcileStorageAsync(ct);
            return Ok(report);
        }

        [HttpPost("blossom/reconcile/clean")]
        public async Task<IActionResult> CleanBlossomOrphanedFiles(CancellationToken ct)
        {
            if (!IsAuthorized(out var adminName))
            {
                return Unauthorized();
            }

            var cleaned = await this.blossomManager.CleanOrphanedFilesAsync(adminName, ct);
            return Ok(new { success = true, cleanedCount = cleaned });
        }
    }

    public record LoginRequest(string Username, string Password);
    public record NostrLoginRequest(string PublicKey, string Challenge, string Signature);
    public record ResetPasswordRequest(string NewPassword);
    public record DisconnectRequest(string? Reason);
    public record SetPubkeyRuleRequest(string PublicKey, PubkeyRuleStatus Status, string? Reason, long? CustomStorageQuotaBytes);
    public record BanEventRequest(string EventId, string? Reason);
    public record ResolveReportRequest(ReportStatus Status, string? Notes);
    public record ReviewAppealRequest(AppealStatus Status, string? Comment);

    public record BlossomKillswitchRequest(bool UploadsEnabled, string? AccessMode);
    public record BlossomBatchDeleteRequest(List<string> Hashes);
    public record BlossomSetQuotaRequest(long? QuotaBytes);
    public record BlossomBanUploadsRequest(bool Banned, string? Reason);
}
