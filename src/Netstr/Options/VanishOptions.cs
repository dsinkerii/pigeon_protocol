namespace Netstr.Options
{
    public record VanishOptions
    {
        public int CancelWindowDays { get; init; } = 3;
        public int BanAtMinDays { get; init; } = 1;
        public int BanAtMaxDays { get; init; } = 30;
        public int ExecutionIntervalSeconds { get; init; } = 60;
    }
}
