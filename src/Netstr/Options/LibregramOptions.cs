namespace Netstr.Options
{
    public record LibregramOptions
    {
        public bool Enabled { get; init; } = true;

        public int ProtocolVersion { get; init; } = 1;

        public string RelayFlavor { get; init; } = "libregram";

        public string[] Commands { get; init; } =
        [
            "lg.capabilities"
        ];
    }
}
