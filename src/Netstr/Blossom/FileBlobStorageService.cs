using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Netstr.Data;
using Netstr.Options;

namespace Netstr.Blossom
{
    public class FileBlobStorageService : IBlobStorageService
    {
        private readonly IDbContextFactory<NetstrDbContext> db;
        private readonly BlossomOptions options;
        private readonly ILogger<FileBlobStorageService> logger;

        public FileBlobStorageService(
            IDbContextFactory<NetstrDbContext> db,
            IOptions<BlossomOptions> options,
            ILogger<FileBlobStorageService> logger)
        {
            this.db = db;
            this.options = options.Value;
            this.logger = logger;
        }

        public async Task<(Stream stream, string contentType, long size)> GetBlobAsync(string sha256)
        {
            var filePath = GetFilePath(sha256);

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Blob {sha256} not found");
            }

            var fileInfo = new FileInfo(filePath);
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);

            using var dbContext = this.db.CreateDbContext();
            var entity = await dbContext.Blobs.FirstOrDefaultAsync(x => x.Sha256 == sha256);
            var contentType = entity?.ContentType ?? "application/octet-stream";

            return (stream, contentType, fileInfo.Length);
        }

        public async Task<bool> BlobExistsAsync(string sha256)
        {
            var filePath = GetFilePath(sha256);
            return File.Exists(filePath) && await this.db.CreateDbContext().Blobs.AnyAsync(x => x.Sha256 == sha256);
        }

        public async Task<BlobDescriptor> StoreBlobAsync(string sha256, string sourceFilePath, string contentType, string ownerPubkey, string? baseUrl = null)
        {
            var filePath = GetFilePath(sha256);
            var directory = Path.GetDirectoryName(filePath)!;

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var alreadyExists = File.Exists(filePath);

            if (!alreadyExists)
            {
                File.Move(sourceFilePath, filePath);
            }

            var size = new FileInfo(filePath).Length;

            using var dbContext = this.db.CreateDbContext();

            var existing = await dbContext.Blobs.FirstOrDefaultAsync(x => x.Sha256 == sha256);
            if (existing == null)
            {
                var entity = new BlobEntity
                {
                    Sha256 = sha256,
                    ContentType = contentType,
                    Size = size,
                    OwnerPubkey = ownerPubkey,
                    UploadedAt = DateTimeOffset.UtcNow
                };

                dbContext.Blobs.Add(entity);
                await dbContext.SaveChangesAsync();
            }

            this.logger.LogInformation("Stored blob {Sha256} ({Size} bytes, {Type}) by {Pubkey}", sha256, size, contentType, ownerPubkey);

            return new BlobDescriptor
            {
                Url = GetBlobUrl(sha256, contentType, baseUrl),
                Sha256 = sha256,
                Size = size,
                Type = contentType,
                Uploaded = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
        }

        public async Task<bool> DeleteBlobAsync(string sha256, string ownerPubkey)
        {
            using var dbContext = this.db.CreateDbContext();

            var entity = await dbContext.Blobs.FirstOrDefaultAsync(x => x.Sha256 == sha256);
            if (entity == null)
            {
                return false;
            }

            if (entity.OwnerPubkey != ownerPubkey)
            {
                return false;
            }

            var filePath = GetFilePath(sha256);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            dbContext.Blobs.Remove(entity);
            await dbContext.SaveChangesAsync();

            this.logger.LogInformation("Deleted blob {Sha256} by {Pubkey}", sha256, ownerPubkey);
            return true;
        }

        public async Task<IReadOnlyList<BlobDescriptor>> ListBlobsAsync(string pubkey, string? cursor = null, int limit = 100, string? baseUrl = null)
        {
            using var dbContext = this.db.CreateDbContext();

            IQueryable<BlobEntity> query = dbContext.Blobs
                .Where(x => x.OwnerPubkey == pubkey)
                .OrderByDescending(x => x.UploadedAt);

            if (!string.IsNullOrEmpty(cursor))
            {
                query = query.Where(x => x.Sha256.CompareTo(cursor) < 0);
            }

            var entities = await query.Take(limit).ToListAsync();

            return entities.Select(x => new BlobDescriptor
            {
                Url = GetBlobUrl(x.Sha256, x.ContentType, baseUrl),
                Sha256 = x.Sha256,
                Size = x.Size,
                Type = x.ContentType,
                Uploaded = x.UploadedAt.ToUnixTimeSeconds()
            }).ToList();
        }

        public string GetBlobUrl(string sha256, string contentType, string? baseUrl = null)
        {
            var extensions = global::Netstr.MimeTypes.GetMimeTypeExtensions(contentType);
            var ext = extensions.FirstOrDefault() ?? "bin";
            if (!ext.StartsWith('.')) ext = "." + ext;
            var path = $"/{sha256}{ext}";
            if (!string.IsNullOrEmpty(baseUrl))
            {
                return $"{baseUrl.TrimEnd('/')}{path}";
            }
            return path;
        }

        private string GetFilePath(string sha256)
        {
            return Path.Combine(options.StoragePath, sha256);
        }

        public async Task<long> GetTotalStorageUsedAsync()
        {
            using var dbContext = this.db.CreateDbContext();
            return await dbContext.Blobs.SumAsync(x => x.Size);
        }

        public async Task<long> GetUserStorageUsedAsync(string pubkey)
        {
            using var dbContext = this.db.CreateDbContext();
            return await dbContext.Blobs.Where(x => x.OwnerPubkey == pubkey).SumAsync(x => x.Size);
        }
    }
}
