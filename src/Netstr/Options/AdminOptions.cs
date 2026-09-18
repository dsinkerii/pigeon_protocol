namespace Netstr.Options
{
    public record AdminOptions
    {
        public bool Enabled { get; init; } = true;

        public string Path { get; init; } = "/admin";

        public int Port { get; init; } = 2053;
    }
}
