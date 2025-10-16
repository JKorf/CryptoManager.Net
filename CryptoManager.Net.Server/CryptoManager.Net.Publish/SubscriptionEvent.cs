namespace CryptoManager.Net.Data
{
    public class SubscriptionEvent
    {
        public string? Exchange { get; set; }
        public StreamStatus Status { get; set; }

        public SubscriptionEvent(StreamStatus status)
        {
            Status = status;
        }
    }

    public enum StreamStatus
    {
        Interrupted,
        Restored,
        Failed
    }
}
