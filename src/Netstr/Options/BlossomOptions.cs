namespace Netstr.Options
{
    public record BlossomOptions
    {
        public bool Enabled { get; init; }

        public string StoragePath { get; init; } = "blossom_data";

        public bool AuthRequired { get; init; } = true;

        public long MaxUploadSizeBytes { get; init; } = 10_485_760;

        public long MaxTotalStorageBytes { get; init; } = 0;

        public long MaxStoragePerUserBytes { get; init; } = 1_073_741_824;

        public string[] AllowedMimeTypes { get; init; } = [];

        public string[] BlockedMimeTypes { get; init; } = [];

        public int MaxUploadsPerMinute { get; init; } = 30;
    }
}
