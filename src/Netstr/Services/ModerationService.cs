using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Netstr.Data;
using Netstr.Messaging;

namespace Netstr.Services
{
    public interface IModerationService
    {
        Task<List<ModerationReportEntity>> GetReportsAsync(ReportStatus? status = null, int limit = 50, CancellationToken ct = default);

        Task<bool> ResolveReportAsync(int reportId, ReportStatus status, string? notes, string adminName, CancellationToken ct = default);

        Task<List<PubkeyRuleEntity>> GetPubkeyRulesAsync(CancellationToken ct = default);

        Task<bool> SetPubkeyRuleAsync(string pubkey, PubkeyRuleStatus status, string? reason, long? quotaBytes, string adminName, CancellationToken ct = default);

        Task<bool> RemovePubkeyRuleAsync(string pubkey, string adminName, CancellationToken ct = default);

        Task<List<BannedEventEntity>> GetBannedEventsAsync(int limit = 50, CancellationToken ct = default);

        Task<bool> BanEventAsync(string eventId, string? reason, string adminName, CancellationToken ct = default);

        Task<bool> UnbanEventAsync(string eventId, string adminName, CancellationToken ct = default);

        Task<List<ModerationAppealEntity>> GetAppealsAsync(AppealStatus? status = null, int limit = 50, CancellationToken ct = default);

        Task<bool> SubmitAppealAsync(string appellantPubkey, string? targetEventId, string appealMessage, CancellationToken ct = default);

        Task<bool> ReviewAppealAsync(int appealId, AppealStatus status, string? comment, string adminName, CancellationToken ct = default);

        Task<List<ModerationAuditLogEntity>> GetAuditLogsAsync(int limit = 100, CancellationToken ct = default);
    }

    public class ModerationService : IModerationService
    {
        private readonly IDbContextFactory<NetstrDbContext> dbFactory;
        private readonly IModerationCache moderationCache;
        private readonly ILogger<ModerationService> logger;

        public ModerationService(
            IDbContextFactory<NetstrDbContext> dbFactory,
            IModerationCache moderationCache,
            ILogger<ModerationService> logger)
        {
            this.dbFactory = dbFactory;
            this.moderationCache = moderationCache;
            this.logger = logger;
        }

        public async Task<List<ModerationReportEntity>> GetReportsAsync(ReportStatus? status = null, int limit = 50, CancellationToken ct = default)
        {
            using var db = this.dbFactory.CreateDbContext();
            var query = db.ModerationReports.AsNoTracking();

            if (status.HasValue)
            {
                query = query.Where(r => r.Status == status.Value);
            }

            return await query.OrderByDescending(r => r.CreatedAt).Take(limit).ToListAsync(ct);
        }

        public async Task<bool> ResolveReportAsync(int reportId, ReportStatus status, string? notes, string adminName, CancellationToken ct = default)
        {
            using var db = this.dbFactory.CreateDbContext();
            var report = await db.ModerationReports.FirstOrDefaultAsync(r => r.Id == reportId, ct);
            if (report == null)
            {
                return false;
            }

            report.Status = status;
            report.ResolutionNotes = notes;
            report.ResolvedByAdmin = adminName;
            report.ResolvedAt = DateTimeOffset.UtcNow;

            db.ModerationAuditLogs.Add(new ModerationAuditLogEntity
            {
                AdminIdentifier = adminName,
                ActionType = $"ResolveReport:{status}",
                TargetIdentifier = $"Report#{reportId} (Target: {report.TargetPubkey})",
                DetailsJson = JsonSerializer.Serialize(new { reportId, status = status.ToString(), notes }),
                Timestamp = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);
            return true;
        }

        public async Task<List<PubkeyRuleEntity>> GetPubkeyRulesAsync(CancellationToken ct = default)
        {
            using var db = this.dbFactory.CreateDbContext();
            return await db.PubkeyRules.AsNoTracking().OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        }

        public async Task<bool> SetPubkeyRuleAsync(string pubkey, PubkeyRuleStatus status, string? reason, long? quotaBytes, string adminName, CancellationToken ct = default)
        {
            pubkey = pubkey.Trim().ToLowerInvariant();
            using var db = this.dbFactory.CreateDbContext();
            var existing = await db.PubkeyRules.FirstOrDefaultAsync(r => r.PublicKey.ToLower() == pubkey, ct);

            if (existing == null)
            {
                existing = new PubkeyRuleEntity
                {
                    PublicKey = pubkey,
                    Status = status,
                    BanReason = reason,
                    CustomStorageQuotaBytes = quotaBytes,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.PubkeyRules.Add(existing);
            }
            else
            {
                existing.Status = status;
                existing.BanReason = reason;
                existing.CustomStorageQuotaBytes = quotaBytes;
            }

            db.ModerationAuditLogs.Add(new ModerationAuditLogEntity
            {
                AdminIdentifier = adminName,
                ActionType = $"SetPubkeyStatus:{status}",
                TargetIdentifier = pubkey,
                DetailsJson = JsonSerializer.Serialize(new { status = status.ToString(), reason, quotaBytes }),
                Timestamp = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);

            // Update in-memory cache
            this.moderationCache.SetPubkeyRule(pubkey, status, reason);
            this.moderationCache.SetCustomStorageQuota(pubkey, quotaBytes);
            this.logger.LogInformation($"Pubkey rule for {pubkey} updated to {status} by {adminName}");

            return true;
        }

        public async Task<bool> RemovePubkeyRuleAsync(string pubkey, string adminName, CancellationToken ct = default)
        {
            pubkey = pubkey.Trim().ToLowerInvariant();
            using var db = this.dbFactory.CreateDbContext();
            var existing = await db.PubkeyRules.FirstOrDefaultAsync(r => r.PublicKey.ToLower() == pubkey, ct);
            if (existing != null)
            {
                db.PubkeyRules.Remove(existing);

                db.ModerationAuditLogs.Add(new ModerationAuditLogEntity
                {
                    AdminIdentifier = adminName,
                    ActionType = "RemovePubkeyRule",
                    TargetIdentifier = pubkey,
                    Timestamp = DateTimeOffset.UtcNow
                });

                await db.SaveChangesAsync(ct);
            }

            this.moderationCache.RemovePubkeyRule(pubkey);
            return true;
        }

        public async Task<List<BannedEventEntity>> GetBannedEventsAsync(int limit = 50, CancellationToken ct = default)
        {
            using var db = this.dbFactory.CreateDbContext();
            return await db.BannedEvents.AsNoTracking().OrderByDescending(b => b.BannedAt).Take(limit).ToListAsync(ct);
        }

        public async Task<bool> BanEventAsync(string eventId, string? reason, string adminName, CancellationToken ct = default)
        {
            eventId = eventId.Trim().ToLowerInvariant();
            using var db = this.dbFactory.CreateDbContext();
            var existing = await db.BannedEvents.FirstOrDefaultAsync(b => b.EventId.ToLower() == eventId, ct);

            if (existing == null)
            {
                db.BannedEvents.Add(new BannedEventEntity
                {
                    EventId = eventId,
                    Reason = reason,
                    BannedAt = DateTimeOffset.UtcNow
                });

                // Also delete event from DB if present
                var storedEvent = await db.Events.FirstOrDefaultAsync(e => e.EventId.ToLower() == eventId, ct);
                if (storedEvent != null)
                {
                    db.Events.Remove(storedEvent);
                }

                db.ModerationAuditLogs.Add(new ModerationAuditLogEntity
                {
                    AdminIdentifier = adminName,
                    ActionType = "BanEvent",
                    TargetIdentifier = eventId,
                    DetailsJson = JsonSerializer.Serialize(new { reason, eventExisted = storedEvent != null }),
                    Timestamp = DateTimeOffset.UtcNow
                });

                await db.SaveChangesAsync(ct);
            }

            this.moderationCache.SetEventBanned(eventId, reason);
            this.logger.LogInformation($"Event {eventId} banned by {adminName}");
            return true;
        }

        public async Task<bool> UnbanEventAsync(string eventId, string adminName, CancellationToken ct = default)
        {
            eventId = eventId.Trim().ToLowerInvariant();
            using var db = this.dbFactory.CreateDbContext();
            var existing = await db.BannedEvents.FirstOrDefaultAsync(b => b.EventId.ToLower() == eventId, ct);
            if (existing != null)
            {
                db.BannedEvents.Remove(existing);

                db.ModerationAuditLogs.Add(new ModerationAuditLogEntity
                {
                    AdminIdentifier = adminName,
                    ActionType = "UnbanEvent",
                    TargetIdentifier = eventId,
                    Timestamp = DateTimeOffset.UtcNow
                });

                await db.SaveChangesAsync(ct);
            }

            this.moderationCache.RemoveEventBanned(eventId);
            return true;
        }

        public async Task<List<ModerationAppealEntity>> GetAppealsAsync(AppealStatus? status = null, int limit = 50, CancellationToken ct = default)
        {
            using var db = this.dbFactory.CreateDbContext();
            var query = db.ModerationAppeals.AsNoTracking();

            if (status.HasValue)
            {
                query = query.Where(a => a.Status == status.Value);
            }

            return await query.OrderByDescending(a => a.SubmittedAt).Take(limit).ToListAsync(ct);
        }

        public async Task<bool> SubmitAppealAsync(string appellantPubkey, string? targetEventId, string appealMessage, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(appellantPubkey) || string.IsNullOrWhiteSpace(appealMessage))
            {
                return false;
            }

            using var db = this.dbFactory.CreateDbContext();
            db.ModerationAppeals.Add(new ModerationAppealEntity
            {
                AppellantPubkey = appellantPubkey.Trim(),
                TargetEventId = targetEventId?.Trim(),
                AppealMessage = appealMessage.Trim(),
                Status = AppealStatus.Pending,
                SubmittedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);
            return true;
        }

        public async Task<bool> ReviewAppealAsync(int appealId, AppealStatus status, string? comment, string adminName, CancellationToken ct = default)
        {
            using var db = this.dbFactory.CreateDbContext();
            var appeal = await db.ModerationAppeals.FirstOrDefaultAsync(a => a.Id == appealId, ct);
            if (appeal == null)
            {
                return false;
            }

            appeal.Status = status;
            appeal.ModeratorComment = comment;
            appeal.ReviewedAt = DateTimeOffset.UtcNow;

            // If approved, automatically unban pubkey if requested
            if (status == AppealStatus.Approved)
            {
                var rule = await db.PubkeyRules.FirstOrDefaultAsync(r => r.PublicKey.ToLower() == appeal.AppellantPubkey.ToLower(), ct);
                if (rule != null && rule.Status == PubkeyRuleStatus.Banned)
                {
                    db.PubkeyRules.Remove(rule);
                    this.moderationCache.RemovePubkeyRule(appeal.AppellantPubkey);
                }
            }

            db.ModerationAuditLogs.Add(new ModerationAuditLogEntity
            {
                AdminIdentifier = adminName,
                ActionType = $"ReviewAppeal:{status}",
                TargetIdentifier = $"Appeal#{appealId} ({appeal.AppellantPubkey})",
                DetailsJson = JsonSerializer.Serialize(new { appealId, status = status.ToString(), comment }),
                Timestamp = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);
            return true;
        }

        public async Task<List<ModerationAuditLogEntity>> GetAuditLogsAsync(int limit = 100, CancellationToken ct = default)
        {
            using var db = this.dbFactory.CreateDbContext();
            return await db.ModerationAuditLogs.AsNoTracking().OrderByDescending(l => l.Timestamp).Take(limit).ToListAsync(ct);
        }
    }
}
