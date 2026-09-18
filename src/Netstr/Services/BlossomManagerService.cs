using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Netstr.Data;
using Netstr.Messaging;
using Netstr.Options;

namespace Netstr.Services
{
    public record BlossomStatsDto
    {
        public bool Enabled { get; init; }
        public bool UploadsEnabled { get; init; }
        public string AccessMode { get; init; } = "Public"; // Public, WhitelistedOnly, ReadOnly, Disabled
        public long TotalBlobs { get; init; }
        public long TotalBytesUsed { get; init; }
        public long MaxTotalStorageBytes { get; init; }
        public long DefaultUserQuotaBytes { get; init; }
        public int DistinctUsersCount { get; init; }
        public BlossomMimeCategoryStats Categories { get; init; } = new();
    }

    public record BlossomMimeCategoryStats
    {
        public long ImageBytes { get; set; }
        public int ImageCount { get; set; }
        public long AudioBytes { get; set; }
        public int AudioCount { get; set; }
        public long VideoBytes { get; set; }
        public int VideoCount { get; set; }
        public long DocumentBytes { get; set; }
        public int DocumentCount { get; set; }
        public long OtherBytes { get; set; }
        public int OtherCount { get; set; }
    }

    public record BlossomBlobDto
    {
        public int Id { get; init; }
        public required string Sha256 { get; init; }
        public required string ContentType { get; init; }
        public required long Size { get; init; }
        public required string OwnerPubkey { get; init; }
        public required DateTimeOffset UploadedAt { get; init; }
        public required string Url { get; init; }
        public bool ExistsOnDisk { get; init; }
        public string Category { get; init; } = "other";
    }

    public record BlossomUserStatDto
    {
        public required string PublicKey { get; init; }
        public long TotalBytesUsed { get; init; }
        public int BlobCount { get; init; }
        public long EffectiveQuotaBytes { get; init; }
        public bool HasCustomQuota { get; init; }
        public bool IsUploadBanned { get; init; }
        public bool IsWhitelisted { get; init; }
        public string? BanReason { get; init; }
    }

    public record BlossomReconcileDto
    {
        public int OrphanedFilesCount { get; init; }
        public long OrphanedBytesTotal { get; init; }
        public List<string> SampleOrphanedFiles { get; init; } = [];
        public int MissingFilesCount { get; init; }
        public List<string> SampleMissingBlobs { get; init; } = [];
    }

    public interface IBlossomManagerService
    {
        bool UploadsEnabled { get; }
        string AccessMode { get; }

        void SetKillswitch(bool uploadsEnabled, string? accessMode = null);

        Task<BlossomStatsDto> GetStatsAsync(string? baseUrl = null, CancellationToken ct = default);

        Task<(List<BlossomBlobDto> Blobs, int TotalCount)> QueryBlobsAsync(
            string? search = null,
            string? pubkey = null,
            string? category = null,
            long? minBytes = null,
            string? sortBy = null,
            bool sortDesc = true,
            int page = 1,
            int pageSize = 50,
            string? baseUrl = null,
            CancellationToken ct = default);

        Task<bool> DeleteBlobAsync(string sha256, string adminName, CancellationToken ct = default);

        Task<int> BatchDeleteBlobsAsync(IEnumerable<string> sha256List, string adminName, CancellationToken ct = default);

        Task<int> PurgeUserBlobsAsync(string pubkey, string adminName, CancellationToken ct = default);

        Task<(List<BlossomUserStatDto> Users, int TotalCount)> QueryUsersAsync(
            string? searchPubkey = null,
            int page = 1,
            int pageSize = 50,
            CancellationToken ct = default);

        Task<bool> SetUserQuotaAsync(string pubkey, long? quotaBytes, string adminName, CancellationToken ct = default);

        Task<bool> SetUserUploadBannedAsync(string pubkey, bool banned, string? reason, string adminName, CancellationToken ct = default);

        Task<BlossomReconcileDto> ReconcileStorageAsync(CancellationToken ct = default);

        Task<int> CleanOrphanedFilesAsync(string adminName, CancellationToken ct = default);
    }

    public class BlossomManagerService : IBlossomManagerService
    {
        private readonly IDbContextFactory<NetstrDbContext> dbFactory;
        private readonly IOptions<BlossomOptions> options;
        private readonly IModerationCache moderationCache;
        private readonly ILogger<BlossomManagerService> logger;

        private volatile bool uploadsEnabled = true;
        private volatile string accessMode = "Public";

        public BlossomManagerService(
            IDbContextFactory<NetstrDbContext> dbFactory,
            IOptions<BlossomOptions> options,
            IModerationCache moderationCache,
            ILogger<BlossomManagerService> logger)
        {
            this.dbFactory = dbFactory;
            this.options = options;
            this.moderationCache = moderationCache;
            this.logger = logger;
        }

        public bool UploadsEnabled => this.uploadsEnabled;
        public string AccessMode => this.accessMode;

        public void SetKillswitch(bool uploadsEnabled, string? accessMode = null)
        {
            this.uploadsEnabled = uploadsEnabled;
            if (!string.IsNullOrEmpty(accessMode))
            {
                this.accessMode = accessMode;
            }
            this.logger.LogInformation("Blossom killswitch updated: UploadsEnabled={UploadsEnabled}, AccessMode={AccessMode}", this.uploadsEnabled, this.accessMode);
        }

        public static string CategorizeMime(string? contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return "other";
            var ct = contentType.ToLowerInvariant();
            if (ct.StartsWith("image/")) return "image";
            if (ct.StartsWith("audio/")) return "audio";
            if (ct.StartsWith("video/")) return "video";
            if (ct.StartsWith("text/") || ct.Contains("pdf") || ct.Contains("json") || ct.Contains("document") || ct.Contains("sheet") || ct.Contains("presentation") || ct.Contains("zip") || ct.Contains("tar") || ct.Contains("xml")) return "document";
            return "other";
        }

        public static string SniffMimeType(string filePath, string fallback = "application/octet-stream")
        {
            try
            {
                if (!File.Exists(filePath)) return fallback;

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buffer = new byte[32];
                var read = fs.Read(buffer, 0, buffer.Length);
                if (read < 2) return fallback;

                // PNG: 89 50 4E 47 0D 0A 1A 0A
                if (read >= 8 && buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47 &&
                    buffer[4] == 0x0D && buffer[5] == 0x0A && buffer[6] == 0x1A && buffer[7] == 0x0A)
                    return "image/png";

                // JPEG: FF D8 FF
                if (read >= 3 && buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF)
                    return "image/jpeg";

                // GIF: GIF87a or GIF89a
                if (read >= 4 && buffer[0] == 0x47 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x38)
                    return "image/gif";

                // WEBP: RIFF....WEBP
                if (read >= 12 && buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46 &&
                    buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50)
                    return "image/webp";

                // MP4 / MOV: ....ftyp
                if (read >= 8 && buffer[4] == 0x66 && buffer[5] == 0x74 && buffer[6] == 0x79 && buffer[7] == 0x70)
                    return "video/mp4";

                // Matroska / WebM: 1A 45 DF A3
                if (read >= 4 && buffer[0] == 0x1A && buffer[1] == 0x45 && buffer[2] == 0xDF && buffer[3] == 0xA3)
                    return "video/webm";

                // MP3 with ID3 tag: ID3
                if (read >= 3 && buffer[0] == 0x49 && buffer[1] == 0x44 && buffer[2] == 0x33)
                    return "audio/mpeg";

                // MP3 raw frame sync: FF FB or FF F3 or FF F2
                if (read >= 2 && buffer[0] == 0xFF && (buffer[1] & 0xFE) == 0xFA)
                    return "audio/mpeg";

                // OGG: OggS
                if (read >= 4 && buffer[0] == 0x4F && buffer[1] == 0x67 && buffer[2] == 0x67 && buffer[3] == 0x53)
                    return "audio/ogg";

                // FLAC: fLaC
                if (read >= 4 && buffer[0] == 0x66 && buffer[1] == 0x4C && buffer[2] == 0x61 && buffer[3] == 0x43)
                    return "audio/flac";

                // WAV: RIFF....WAVE
                if (read >= 12 && buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46 &&
                    buffer[8] == 0x57 && buffer[9] == 0x41 && buffer[10] == 0x56 && buffer[11] == 0x45)
                    return "audio/wav";

                // PDF: %PDF-
                if (read >= 5 && buffer[0] == 0x25 && buffer[1] == 0x50 && buffer[2] == 0x44 && buffer[3] == 0x46 && buffer[4] == 0x2D)
                    return "application/pdf";

                // ZIP: PK\x03\x04
                if (read >= 4 && buffer[0] == 0x50 && buffer[1] == 0x4B && buffer[2] == 0x03 && buffer[3] == 0x04)
                    return "application/zip";

                // JSON: { or [
                if (read >= 1 && (buffer[0] == (byte)'{' || buffer[0] == (byte)'['))
                    return "application/json";
            }
            catch
            {
            }

            return fallback;
        }

        public static string GetExtensionForMimeType(string contentType)
        {
            var ct = contentType.ToLowerInvariant().Split(';')[0].Trim();
            switch (ct)
            {
                case "image/jpeg": return ".jpg";
                case "image/png": return ".png";
                case "image/gif": return ".gif";
                case "image/webp": return ".webp";
                case "image/svg+xml": return ".svg";
                case "image/jxl": return ".jxl";
                case "image/avif": return ".avif";
                case "image/bmp": return ".bmp";
                case "audio/mpeg": return ".mp3";
                case "audio/ogg": return ".ogg";
                case "audio/wav":
                case "audio/x-wav": return ".wav";
                case "audio/flac": return ".flac";
                case "audio/aac": return ".aac";
                case "audio/webm": return ".weba";
                case "video/mp4": return ".mp4";
                case "video/webm": return ".webm";
                case "video/ogg": return ".ogv";
                case "video/quicktime": return ".mov";
                case "video/x-matroska": return ".mkv";
                case "application/pdf": return ".pdf";
                case "application/json": return ".json";
                case "application/zip": return ".zip";
                case "application/x-tar": return ".tar";
                case "text/plain": return ".txt";
                case "text/html": return ".html";
                case "text/markdown": return ".md";
                default:
                    var exts = global::Netstr.MimeTypes.GetMimeTypeExtensions(ct);
                    var e = exts.FirstOrDefault();
                    return string.IsNullOrEmpty(e) ? ".bin" : (e.StartsWith('.') ? e : "." + e);
            }
        }

        public async Task<BlossomStatsDto> GetStatsAsync(string? baseUrl = null, CancellationToken ct = default)
        {
            using var db = this.dbFactory.CreateDbContext();

            var totalBlobs = await db.Blobs.CountAsync(ct);
            var totalBytes = totalBlobs > 0 ? await db.Blobs.SumAsync(x => x.Size, ct) : 0L;
            var distinctUsers = await db.Blobs.Select(x => x.OwnerPubkey).Distinct().CountAsync(ct);

            var catStats = new BlossomMimeCategoryStats();
            var mimeGroups = await db.Blobs
                .GroupBy(x => x.ContentType)
                .Select(g => new { ContentType = g.Key, Count = g.Count(), Bytes = g.Sum(x => x.Size) })
                .ToListAsync(ct);

            foreach (var g in mimeGroups)
            {
                var cat = CategorizeMime(g.ContentType);
                switch (cat)
                {
                    case "image":
                        catStats.ImageBytes += g.Bytes;
                        catStats.ImageCount += g.Count;
                        break;
                    case "audio":
                        catStats.AudioBytes += g.Bytes;
                        catStats.AudioCount += g.Count;
                        break;
                    case "video":
                        catStats.VideoBytes += g.Bytes;
                        catStats.VideoCount += g.Count;
                        break;
                    case "document":
                        catStats.DocumentBytes += g.Bytes;
                        catStats.DocumentCount += g.Count;
                        break;
                    default:
                        catStats.OtherBytes += g.Bytes;
                        catStats.OtherCount += g.Count;
                        break;
                }
            }

            return new BlossomStatsDto
            {
                Enabled = this.options.Value.Enabled,
                UploadsEnabled = this.uploadsEnabled,
                AccessMode = this.accessMode,
                TotalBlobs = totalBlobs,
                TotalBytesUsed = totalBytes,
                MaxTotalStorageBytes = this.options.Value.MaxTotalStorageBytes,
                DefaultUserQuotaBytes = this.options.Value.MaxStoragePerUserBytes,
                DistinctUsersCount = distinctUsers,
                Categories = catStats
            };
        }

        public async Task<(List<BlossomBlobDto> Blobs, int TotalCount)> QueryBlobsAsync(
            string? search = null,
            string? pubkey = null,
            string? category = null,
            long? minBytes = null,
            string? sortBy = null,
            bool sortDesc = true,
            int page = 1,
            int pageSize = 50,
            string? baseUrl = null,
            CancellationToken ct = default)
        {
            using var db = this.dbFactory.CreateDbContext();
            var query = db.Blobs.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim().ToLowerInvariant();
                query = query.Where(x => x.Sha256.ToLower().Contains(s) || x.OwnerPubkey.ToLower().Contains(s));
            }

            if (!string.IsNullOrWhiteSpace(pubkey))
            {
                var p = pubkey.Trim().ToLowerInvariant();
                query = query.Where(x => x.OwnerPubkey.ToLower() == p);
            }

            if (!string.IsNullOrWhiteSpace(category) && category != "all")
            {
                var cat = category.ToLowerInvariant();
                if (cat == "image") query = query.Where(x => x.ContentType.ToLower().StartsWith("image/"));
                else if (cat == "audio") query = query.Where(x => x.ContentType.ToLower().StartsWith("audio/"));
                else if (cat == "video") query = query.Where(x => x.ContentType.ToLower().StartsWith("video/"));
                else if (cat == "document") query = query.Where(x => x.ContentType.ToLower().StartsWith("text/") || x.ContentType.ToLower().Contains("pdf") || x.ContentType.ToLower().Contains("json"));
            }

            if (minBytes.HasValue && minBytes.Value > 0)
            {
                query = query.Where(x => x.Size >= minBytes.Value);
            }

            var totalCount = await query.CountAsync(ct);

            // Sorting
            query = (sortBy?.ToLowerInvariant()) switch
            {
                "size" => sortDesc ? query.OrderByDescending(x => x.Size) : query.OrderBy(x => x.Size),
                "type" => sortDesc ? query.OrderByDescending(x => x.ContentType) : query.OrderBy(x => x.ContentType),
                _ => sortDesc ? query.OrderByDescending(x => x.UploadedAt) : query.OrderBy(x => x.UploadedAt)
            };

            var paged = await query
                .Skip((Math.Max(1, page) - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            var storagePath = this.options.Value.StoragePath;
            var dtos = paged.Select(x =>
            {
                var filePath = Path.Combine(storagePath, x.Sha256);
                var contentType = x.ContentType;
                if ((contentType is "application/octet-stream" or "binary/octet-stream" or "") && File.Exists(filePath))
                {
                    var sniffed = SniffMimeType(filePath, contentType);
                    if (sniffed != "application/octet-stream")
                    {
                        contentType = sniffed;
                    }
                }

                var ext = GetExtensionForMimeType(contentType);
                var urlPath = $"/{x.Sha256}{ext}";
                var fullUrl = !string.IsNullOrEmpty(baseUrl) ? $"{baseUrl.TrimEnd('/')}{urlPath}" : urlPath;
                var onDisk = File.Exists(filePath);

                return new BlossomBlobDto
                {
                    Id = x.Id,
                    Sha256 = x.Sha256,
                    ContentType = contentType,
                    Size = x.Size,
                    OwnerPubkey = x.OwnerPubkey,
                    UploadedAt = x.UploadedAt,
                    Url = fullUrl,
                    ExistsOnDisk = onDisk,
                    Category = CategorizeMime(contentType)
                };
            }).ToList();

            return (dtos, totalCount);
        }

        public async Task<bool> DeleteBlobAsync(string sha256, string adminName, CancellationToken ct = default)
        {
            using var db = this.dbFactory.CreateDbContext();
            var normalizedSha = sha256.Trim().ToLowerInvariant();
            var entity = await db.Blobs.FirstOrDefaultAsync(x => x.Sha256 == normalizedSha, ct);
            if (entity == null)
            {
                return false;
            }

            var filePath = Path.Combine(this.options.Value.StoragePath, normalizedSha);
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to delete physical file {FilePath}", filePath);
            }

            db.Blobs.Remove(entity);

            db.ModerationAuditLogs.Add(new ModerationAuditLogEntity
            {
                AdminIdentifier = adminName,
                ActionType = "BLOSSOM_DELETE_BLOB",
                TargetIdentifier = normalizedSha,
                DetailsJson = JsonSerializer.Serialize(new { size = entity.Size, type = entity.ContentType, owner = entity.OwnerPubkey }),
                Timestamp = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);
            this.logger.LogInformation("Blob {Sha256} deleted by admin {Admin}", normalizedSha, adminName);
            return true;
        }

        public async Task<int> BatchDeleteBlobsAsync(IEnumerable<string> sha256List, string adminName, CancellationToken ct = default)
        {
            var count = 0;
            foreach (var hash in sha256List)
            {
                if (await DeleteBlobAsync(hash, adminName, ct))
                {
                    count++;
                }
            }
            return count;
        }

        public async Task<int> PurgeUserBlobsAsync(string pubkey, string adminName, CancellationToken ct = default)
        {
            using var db = this.dbFactory.CreateDbContext();
            var normalizedPubkey = pubkey.Trim().ToLowerInvariant();
            var blobs = await db.Blobs.Where(x => x.OwnerPubkey == normalizedPubkey).ToListAsync(ct);
            if (blobs.Count == 0)
            {
                return 0;
            }

            var storagePath = this.options.Value.StoragePath;
            long totalBytes = 0;
            foreach (var b in blobs)
            {
                totalBytes += b.Size;
                var filePath = Path.Combine(storagePath, b.Sha256);
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
                catch {}
            }

            db.Blobs.RemoveRange(blobs);

            db.ModerationAuditLogs.Add(new ModerationAuditLogEntity
            {
                AdminIdentifier = adminName,
                ActionType = "BLOSSOM_PURGE_USER_BLOBS",
                TargetIdentifier = normalizedPubkey,
                DetailsJson = JsonSerializer.Serialize(new { deletedCount = blobs.Count, totalBytes }),
                Timestamp = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);
            this.logger.LogInformation("Purged {Count} blobs ({Bytes} bytes) for user {Pubkey} by admin {Admin}", blobs.Count, totalBytes, normalizedPubkey, adminName);
            return blobs.Count;
        }

        public async Task<(List<BlossomUserStatDto> Users, int TotalCount)> QueryUsersAsync(
            string? searchPubkey = null,
            int page = 1,
            int pageSize = 50,
            CancellationToken ct = default)
        {
            using var db = this.dbFactory.CreateDbContext();

            var query = db.Blobs.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(searchPubkey))
            {
                var s = searchPubkey.Trim().ToLowerInvariant();
                query = query.Where(x => x.OwnerPubkey.ToLower().Contains(s));
            }

            var grouped = query
                .GroupBy(x => x.OwnerPubkey)
                .Select(g => new
                {
                    Pubkey = g.Key,
                    TotalBytes = g.Sum(x => x.Size),
                    BlobCount = g.Count()
                })
                .OrderByDescending(x => x.TotalBytes);

            var totalCount = await grouped.CountAsync(ct);

            var paged = await grouped
                .Skip((Math.Max(1, page) - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            var pubkeys = paged.Select(x => x.Pubkey).ToList();
            var rules = await db.PubkeyRules.AsNoTracking()
                .Where(r => pubkeys.Contains(r.PublicKey))
                .ToDictionaryAsync(r => r.PublicKey, ct);

            var defaultQuota = this.options.Value.MaxStoragePerUserBytes;

            var result = paged.Select(x =>
            {
                rules.TryGetValue(x.Pubkey, out var rule);
                var hasCustom = rule?.CustomStorageQuotaBytes.HasValue ?? false;
                var effectiveQuota = hasCustom ? rule!.CustomStorageQuotaBytes!.Value : defaultQuota;
                var isUploadBanned = (rule?.BlossomUploadBanned ?? false) || (rule?.Status == PubkeyRuleStatus.Banned) || (hasCustom && effectiveQuota == 0);
                var isWhitelisted = rule?.Status == PubkeyRuleStatus.Whitelisted;

                return new BlossomUserStatDto
                {
                    PublicKey = x.Pubkey,
                    TotalBytesUsed = x.TotalBytes,
                    BlobCount = x.BlobCount,
                    EffectiveQuotaBytes = effectiveQuota,
                    HasCustomQuota = hasCustom,
                    IsUploadBanned = isUploadBanned,
                    IsWhitelisted = isWhitelisted,
                    BanReason = rule?.BanReason
                };
            }).ToList();

            return (result, totalCount);
        }

        public async Task<bool> SetUserQuotaAsync(string pubkey, long? quotaBytes, string adminName, CancellationToken ct = default)
        {
            using var db = this.dbFactory.CreateDbContext();
            var normalizedPubkey = pubkey.Trim().ToLowerInvariant();

            var rule = await db.PubkeyRules.FirstOrDefaultAsync(x => x.PublicKey == normalizedPubkey, ct);
            if (rule == null)
            {
                rule = new PubkeyRuleEntity
                {
                    PublicKey = normalizedPubkey,
                    Status = PubkeyRuleStatus.Allowed,
                    CustomStorageQuotaBytes = quotaBytes,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.PubkeyRules.Add(rule);
            }
            else
            {
                rule.CustomStorageQuotaBytes = quotaBytes;
            }

            db.ModerationAuditLogs.Add(new ModerationAuditLogEntity
            {
                AdminIdentifier = adminName,
                ActionType = "BLOSSOM_SET_USER_QUOTA",
                TargetIdentifier = normalizedPubkey,
                DetailsJson = JsonSerializer.Serialize(new { quotaBytes }),
                Timestamp = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);
            this.moderationCache.SetCustomStorageQuota(normalizedPubkey, quotaBytes);
            this.logger.LogInformation("Updated storage quota for {Pubkey} to {Quota} bytes by admin {Admin}", normalizedPubkey, quotaBytes, adminName);
            return true;
        }

        public async Task<bool> SetUserUploadBannedAsync(string pubkey, bool banned, string? reason, string adminName, CancellationToken ct = default)
        {
            using var db = this.dbFactory.CreateDbContext();
            var normalizedPubkey = pubkey.Trim().ToLowerInvariant();

            var rule = await db.PubkeyRules.FirstOrDefaultAsync(x => x.PublicKey == normalizedPubkey, ct);
            if (rule == null)
            {
                rule = new PubkeyRuleEntity
                {
                    PublicKey = normalizedPubkey,
                    Status = PubkeyRuleStatus.Allowed,
                    BlossomUploadBanned = banned,
                    BanReason = reason,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.PubkeyRules.Add(rule);
            }
            else
            {
                rule.BlossomUploadBanned = banned;
                if (!string.IsNullOrEmpty(reason)) rule.BanReason = reason;
            }

            db.ModerationAuditLogs.Add(new ModerationAuditLogEntity
            {
                AdminIdentifier = adminName,
                ActionType = banned ? "BLOSSOM_BAN_USER_UPLOADS" : "BLOSSOM_UNBAN_USER_UPLOADS",
                TargetIdentifier = normalizedPubkey,
                DetailsJson = JsonSerializer.Serialize(new { banned, reason }),
                Timestamp = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);
            this.moderationCache.SetBlossomUploadBanned(normalizedPubkey, banned, reason);
            this.logger.LogInformation("Blossom upload banned={Banned} for {Pubkey} by admin {Admin}", banned, normalizedPubkey, adminName);
            return true;
        }

        public async Task<BlossomReconcileDto> ReconcileStorageAsync(CancellationToken ct = default)
        {
            using var db = this.dbFactory.CreateDbContext();
            var dbHashes = await db.Blobs.AsNoTracking().Select(x => x.Sha256).ToListAsync(ct);
            var hashSet = new HashSet<string>(dbHashes, StringComparer.OrdinalIgnoreCase);

            var storagePath = this.options.Value.StoragePath;
            var orphanedFiles = new List<string>();
            long orphanedBytes = 0;
            var missingBlobs = new List<string>();

            if (Directory.Exists(storagePath))
            {
                var files = Directory.GetFiles(storagePath);
                foreach (var f in files)
                {
                    var name = Path.GetFileName(f);
                    if (name.StartsWith('.') || name.StartsWith('_')) continue;

                    if (!hashSet.Contains(name))
                    {
                        orphanedFiles.Add(name);
                        try
                        {
                            orphanedBytes += new FileInfo(f).Length;
                        }
                        catch {}
                    }
                }
            }

            // Check for missing files
            foreach (var hash in dbHashes.Take(500))
            {
                var expectedPath = Path.Combine(storagePath, hash);
                if (!File.Exists(expectedPath))
                {
                    missingBlobs.Add(hash);
                }
            }

            return new BlossomReconcileDto
            {
                OrphanedFilesCount = orphanedFiles.Count,
                OrphanedBytesTotal = orphanedBytes,
                SampleOrphanedFiles = orphanedFiles.Take(20).ToList(),
                MissingFilesCount = missingBlobs.Count,
                SampleMissingBlobs = missingBlobs.Take(20).ToList()
            };
        }

        public async Task<int> CleanOrphanedFilesAsync(string adminName, CancellationToken ct = default)
        {
            using var db = this.dbFactory.CreateDbContext();
            var dbHashes = await db.Blobs.AsNoTracking().Select(x => x.Sha256).ToListAsync(ct);
            var hashSet = new HashSet<string>(dbHashes, StringComparer.OrdinalIgnoreCase);

            var storagePath = this.options.Value.StoragePath;
            if (!Directory.Exists(storagePath)) return 0;

            var files = Directory.GetFiles(storagePath);
            int cleaned = 0;
            long cleanedBytes = 0;

            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith('.') || name.StartsWith('_')) continue;

                if (!hashSet.Contains(name))
                {
                    try
                    {
                        var len = new FileInfo(f).Length;
                        File.Delete(f);
                        cleaned++;
                        cleanedBytes += len;
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogWarning(ex, "Could not delete orphaned file {File}", f);
                    }
                }
            }

            db.ModerationAuditLogs.Add(new ModerationAuditLogEntity
            {
                AdminIdentifier = adminName,
                ActionType = "BLOSSOM_CLEAN_ORPHANS",
                TargetIdentifier = "storage",
                DetailsJson = JsonSerializer.Serialize(new { cleanedFiles = cleaned, cleanedBytes }),
                Timestamp = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);

            this.logger.LogInformation("Cleaned {Cleaned} orphaned files ({Bytes} bytes) by admin {Admin}", cleaned, cleanedBytes, adminName);
            return cleaned;
        }
    }
}
