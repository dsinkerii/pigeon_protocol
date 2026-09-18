using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Netstr.Data;
using Netstr.Messaging;

namespace Netstr.Services
{
    public record SystemInfoMetrics(
        double CpuUsagePercent,
        long MemoryWorkingSetBytes,
        long MemoryGcHeapBytes,
        long TotalSystemMemoryBytes,
        long UptimeSeconds,
        string OsDescription,
        DateTimeOffset ProcessStartTime,
        string DotNetVersion
    );

    public record NetworkMetrics(
        long TotalInboundBytes,
        long TotalOutboundBytes,
        double InboundBytesPerSecond,
        double OutboundBytesPerSecond,
        long TotalInboundMessages,
        long TotalOutboundMessages
    );

    public record ConnectionMetrics(
        int ActiveWebSockets,
        int AuthenticatedWebSockets,
        int ActiveSubscriptions
    );

    public record StorageMetrics(
        long DatabaseSizeBytes,
        long TotalEventsCount,
        long BlossomBlobsCount,
        long BlossomStorageSizeBytes
    );

    public record ModerationSummary(
        int PendingReportsCount,
        int PendingAppealsCount,
        int BannedPubkeysCount,
        int BannedEventsCount
    );

    public record TelemetrySnapshot(
        SystemInfoMetrics System,
        NetworkMetrics Network,
        ConnectionMetrics Connections,
        StorageMetrics Storage,
        ModerationSummary Moderation,
        DateTimeOffset Timestamp
    );

    public interface ITelemetryService
    {
        Task<TelemetrySnapshot> GetSnapshotAsync(CancellationToken ct = default);
    }

    public class TelemetryService : ITelemetryService
    {
        private readonly IDbContextFactory<NetstrDbContext> dbFactory;
        private readonly IWebSocketAdapterCollection webSocketCollection;
        private readonly ITrafficTracker trafficTracker;
        private readonly IModerationCache moderationCache;

        private TimeSpan lastCpuTime;
        private DateTimeOffset lastCpuSampleTime = DateTimeOffset.UtcNow;
        private double lastCpuPercent;
        private readonly object cpuLock = new();

        public TelemetryService(
            IDbContextFactory<NetstrDbContext> dbFactory,
            IWebSocketAdapterCollection webSocketCollection,
            ITrafficTracker trafficTracker,
            IModerationCache moderationCache)
        {
            this.dbFactory = dbFactory;
            this.webSocketCollection = webSocketCollection;
            this.trafficTracker = trafficTracker;
            this.moderationCache = moderationCache;

            try
            {
                using var proc = Process.GetCurrentProcess();
                this.lastCpuTime = proc.TotalProcessorTime;
            }
            catch
            {
                this.lastCpuTime = TimeSpan.Zero;
            }
        }

        public async Task<TelemetrySnapshot> GetSnapshotAsync(CancellationToken ct = default)
        {
            var sysInfo = GetSystemInfo();
            var netInfo = GetNetworkMetrics();
            var connInfo = GetConnectionMetrics();
            var storageInfo = await GetStorageMetricsAsync(ct);
            var modInfo = await GetModerationSummaryAsync(ct);

            return new TelemetrySnapshot(
                sysInfo,
                netInfo,
                connInfo,
                storageInfo,
                modInfo,
                DateTimeOffset.UtcNow
            );
        }

        private SystemInfoMetrics GetSystemInfo()
        {
            using var proc = Process.GetCurrentProcess();
            var cpu = CalculateCpuUsage(proc);
            var workingSet = proc.WorkingSet64;
            var gcHeap = GC.GetTotalMemory(false);
            var totalMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            var uptime = (DateTimeOffset.UtcNow - proc.StartTime).TotalSeconds;

            return new SystemInfoMetrics(
                CpuUsagePercent: Math.Round(cpu, 1),
                MemoryWorkingSetBytes: workingSet,
                MemoryGcHeapBytes: gcHeap,
                TotalSystemMemoryBytes: totalMem,
                UptimeSeconds: (long)Math.Max(0, uptime),
                OsDescription: RuntimeInformation.OSDescription,
                ProcessStartTime: proc.StartTime,
                DotNetVersion: RuntimeInformation.FrameworkDescription
            );
        }

        private double CalculateCpuUsage(Process proc)
        {
            var now = DateTimeOffset.UtcNow;
            lock (this.cpuLock)
            {
                var elapsed = (now - this.lastCpuSampleTime).TotalMilliseconds;
                if (elapsed >= 500)
                {
                    try
                    {
                        var currentCpuTime = proc.TotalProcessorTime;
                        var cpuUsedMs = (currentCpuTime - this.lastCpuTime).TotalMilliseconds;
                        var totalPossibleMs = elapsed * Environment.ProcessorCount;

                        this.lastCpuPercent = Math.Clamp((cpuUsedMs / totalPossibleMs) * 100.0, 0.0, 100.0);
                        this.lastCpuTime = currentCpuTime;
                        this.lastCpuSampleTime = now;
                    }
                    catch
                    {
                        this.lastCpuPercent = 0.0;
                    }
                }
                return this.lastCpuPercent;
            }
        }

        private NetworkMetrics GetNetworkMetrics()
        {
            return new NetworkMetrics(
                TotalInboundBytes: this.trafficTracker.TotalInboundBytes,
                TotalOutboundBytes: this.trafficTracker.TotalOutboundBytes,
                InboundBytesPerSecond: Math.Round(this.trafficTracker.InboundBytesPerSecond, 1),
                OutboundBytesPerSecond: Math.Round(this.trafficTracker.OutboundBytesPerSecond, 1),
                TotalInboundMessages: this.trafficTracker.TotalInboundMessages,
                TotalOutboundMessages: this.trafficTracker.TotalOutboundMessages
            );
        }

        private ConnectionMetrics GetConnectionMetrics()
        {
            var adapters = this.webSocketCollection.GetAll().ToArray();
            var total = adapters.Length;
            var authenticated = adapters.Count(a => a.Context.IsAuthenticated());
            var totalSubs = adapters.Sum(a => a.Subscriptions?.GetAll()?.Count() ?? 0);

            return new ConnectionMetrics(
                ActiveWebSockets: total,
                AuthenticatedWebSockets: authenticated,
                ActiveSubscriptions: totalSubs
            );
        }

        private async Task<StorageMetrics> GetStorageMetricsAsync(CancellationToken ct)
        {
            try
            {
                using var db = this.dbFactory.CreateDbContext();

                var eventCount = await db.Events.LongCountAsync(ct);
                var blobCount = await db.Blobs.LongCountAsync(ct);
                var blobSize = await db.Blobs.SumAsync(x => (long?)x.Size, ct) ?? 0;

                long dbSize = 0;
                try
                {
                    using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT pg_database_size(current_database());";
                    await db.Database.OpenConnectionAsync(ct);
                    var result = await command.ExecuteScalarAsync(ct);
                    if (result != null && long.TryParse(result.ToString(), out var size))
                    {
                        dbSize = size;
                    }
                }
                catch
                {
                    // Fallback if not postgres
                }

                return new StorageMetrics(
                    DatabaseSizeBytes: dbSize,
                    TotalEventsCount: eventCount,
                    BlossomBlobsCount: blobCount,
                    BlossomStorageSizeBytes: blobSize
                );
            }
            catch
            {
                return new StorageMetrics(0, 0, 0, 0);
            }
        }

        private async Task<ModerationSummary> GetModerationSummaryAsync(CancellationToken ct)
        {
            try
            {
                using var db = this.dbFactory.CreateDbContext();
                var pendingReports = await db.ModerationReports.CountAsync(r => r.Status == ReportStatus.Pending, ct);
                var pendingAppeals = await db.ModerationAppeals.CountAsync(a => a.Status == AppealStatus.Pending, ct);
                var bannedPubkeys = await db.PubkeyRules.CountAsync(p => p.Status == PubkeyRuleStatus.Banned, ct);
                var bannedEvents = await db.BannedEvents.CountAsync(ct);

                return new ModerationSummary(
                    PendingReportsCount: pendingReports,
                    PendingAppealsCount: pendingAppeals,
                    BannedPubkeysCount: bannedPubkeys,
                    BannedEventsCount: bannedEvents
                );
            }
            catch
            {
                return new ModerationSummary(0, 0, 0, 0);
            }
        }
    }
}
