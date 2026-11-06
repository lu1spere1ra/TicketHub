namespace TicketHub.BuildingBlocks.Messaging;

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>Topic exchange shared by every service; routing key is the message type name.</summary>
    public string Exchange { get; set; } = "tickethub.events";

    /// <summary>Single queue collecting messages that exhausted their retries, from every service.</summary>
    public string DeadLetterQueue { get; set; } = "tickethub.dead-letters";

    /// <summary>
    /// Prepended to every consumer queue name. Empty in real deployments; tests set it so several hosts can share
    /// one broker without consuming each other's messages.
    /// </summary>
    public string QueuePrefix { get; set; } = string.Empty;

    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public int OutboxBatchSize { get; set; } = 100;
    public TimeSpan OutboxRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long a failed message waits in the retry queue before being redelivered.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Total delivery attempts before a message is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 5;

    public ushort PrefetchCount { get; set; } = 16;
}
