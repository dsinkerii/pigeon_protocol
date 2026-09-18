namespace Netstr.Messaging
{
    public interface ITrafficTracker
    {
        void TrackInbound(long bytes);

        void TrackOutbound(long bytes);

        long TotalInboundBytes { get; }

        long TotalOutboundBytes { get; }

        long TotalInboundMessages { get; }

        long TotalOutboundMessages { get; }

        double InboundBytesPerSecond { get; }

        double OutboundBytesPerSecond { get; }
    }

    public class TrafficTracker : ITrafficTracker
    {
        private long totalInboundBytes;
        private long totalOutboundBytes;
        private long totalInboundMessages;
        private long totalOutboundMessages;

        private long lastInboundBytes;
        private long lastOutboundBytes;
        private DateTimeOffset lastSampleTime = DateTimeOffset.UtcNow;
        private double inboundRate;
        private double outboundRate;
        private readonly object sampleLock = new();

        public long TotalInboundBytes => Interlocked.Read(ref this.totalInboundBytes);

        public long TotalOutboundBytes => Interlocked.Read(ref this.totalOutboundBytes);

        public long TotalInboundMessages => Interlocked.Read(ref this.totalInboundMessages);

        public long TotalOutboundMessages => Interlocked.Read(ref this.totalOutboundMessages);

        public double InboundBytesPerSecond
        {
            get
            {
                UpdateRates();
                return this.inboundRate;
            }
        }

        public double OutboundBytesPerSecond
        {
            get
            {
                UpdateRates();
                return this.outboundRate;
            }
        }

        public void TrackInbound(long bytes)
        {
            Interlocked.Add(ref this.totalInboundBytes, bytes);
            Interlocked.Increment(ref this.totalInboundMessages);
        }

        public void TrackOutbound(long bytes)
        {
            Interlocked.Add(ref this.totalOutboundBytes, bytes);
            Interlocked.Increment(ref this.totalOutboundMessages);
        }

        private void UpdateRates()
        {
            var now = DateTimeOffset.UtcNow;
            lock (this.sampleLock)
            {
                var elapsed = (now - this.lastSampleTime).TotalSeconds;
                if (elapsed >= 1.0)
                {
                    var currentIn = Interlocked.Read(ref this.totalInboundBytes);
                    var currentOut = Interlocked.Read(ref this.totalOutboundBytes);

                    this.inboundRate = Math.Max(0, (currentIn - this.lastInboundBytes) / elapsed);
                    this.outboundRate = Math.Max(0, (currentOut - this.lastOutboundBytes) / elapsed);

                    this.lastInboundBytes = currentIn;
                    this.lastOutboundBytes = currentOut;
                    this.lastSampleTime = now;
                }
            }
        }
    }
}
