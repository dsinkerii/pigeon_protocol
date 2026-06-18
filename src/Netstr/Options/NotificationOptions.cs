namespace Netstr.Options
{
    public record NotificationOptions
    {
        public bool Enabled { get; init; } = true;

        public int MaxEvents { get; init; } = 50;

        public int MaxSinceAgeDays { get; init; } = 90;

        public int TokenLifetimeDays { get; init; } = 10;
    }
}