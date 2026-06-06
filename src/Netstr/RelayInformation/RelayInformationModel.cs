using System.Text.Json.Serialization;

namespace Netstr.RelayInformation
{
    public record RelayInformationModel
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("description")]
        public required string Description { get; init; }

        [JsonPropertyName("contact")]
        public string? Contact { get; init; }
        
        [JsonPropertyName("pubkey")]
        public string? PublicKey { get; init; }
        
        [JsonPropertyName("supported_nips")]
        public required int[] SupportedNips { get; init; }

        [JsonPropertyName("version")]
        public string? SoftwareVersion { get; init; }

        [JsonPropertyName("software")]
        public string? Software { get; init; }

        [JsonPropertyName("limitation")]
        public required RelayInformationLimits Limits { get; init; }

        [JsonPropertyName("blossom")]
        public BlossomInfo? Blossom { get; init; }
    }

    public record BlossomInfo
    {
        [JsonPropertyName("enabled")]
        public required bool Enabled { get; init; }

        [JsonPropertyName("max_upload_size")]
        public required long MaxUploadSize { get; init; }

        [JsonPropertyName("max_per_user")]
        public required long MaxPerUser { get; init; }

        [JsonPropertyName("max_total")]
        public required long MaxTotal { get; init; }

        [JsonPropertyName("allowed_types")]
        public required string[] AllowedTypes { get; init; }
    }
}
