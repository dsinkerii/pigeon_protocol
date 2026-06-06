namespace Netstr.Blossom
{
    public record BlobDescriptor
    {
        public required string Url { get; init; }
        public required string Sha256 { get; init; }
        public required long Size { get; init; }
        public required string Type { get; init; }
        public required long Uploaded { get; init; }
    }

    public interface IBlobStorageService
    {
        Task<(Stream stream, string contentType, long size)> GetBlobAsync(string sha256);

        Task<bool> BlobExistsAsync(string sha256);

        Task<BlobDescriptor> StoreBlobAsync(string sha256, string sourceFilePath, string contentType, string ownerPubkey, string? baseUrl = null);

        Task<bool> DeleteBlobAsync(string sha256, string ownerPubkey);

        Task<IReadOnlyList<BlobDescriptor>> ListBlobsAsync(string pubkey, string? cursor = null, int limit = 100, string? baseUrl = null);

        string GetBlobUrl(string sha256, string contentType, string? baseUrl = null);

        Task<long> GetTotalStorageUsedAsync();

        Task<long> GetUserStorageUsedAsync(string pubkey);
    }
}
