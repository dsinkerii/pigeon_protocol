namespace Netstr.Data
{
    public class BlobEntity
    {
        public int Id { get; set; }

        public required string Sha256 { get; set; }

        public required string ContentType { get; set; }

        public required long Size { get; set; }

        public required string OwnerPubkey { get; set; }

        public required DateTimeOffset UploadedAt { get; set; }
    }
}
