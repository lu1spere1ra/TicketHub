namespace TicketHub.TestKit;

/// <summary>Polls an assertion until it passes. Asynchronous systems are asserted on outcomes, not on sleeps.</summary>
public static class Eventually
{
    public static async Task AssertAsync(Func<Task> assertion, TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            try
            {
                await assertion();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(interval ?? TimeSpan.FromMilliseconds(100));
            }
        }
    }

    public static Task AssertAsync(Action assertion, TimeSpan? timeout = null) =>
        AssertAsync(() => { assertion(); return Task.CompletedTask; }, timeout);
}
