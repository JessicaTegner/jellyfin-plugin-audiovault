using Jellyfin.Plugin.AudioVault.Services;

namespace Jellyfin.Plugin.AudioVault.Tests;

public class UndeliveredMessagesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-10T14:14:52Z");
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Fact]
    public void KeepsEachTextOncePerSessionInOrder()
    {
        var waiting = new UndeliveredMessages();

        Assert.True(waiting.Add("s1", "first", Now, Hour));
        Assert.False(waiting.Add("s1", "first", Now.AddSeconds(1), Hour));
        Assert.True(waiting.Add("s1", "second", Now.AddSeconds(2), Hour));
        Assert.True(waiting.Add("s2", "first", Now, Hour));

        Assert.Equal(["first", "second"], waiting.Take("s1", Now.AddMinutes(1)));
        Assert.Empty(waiting.Take("s1", Now.AddMinutes(1)));
        Assert.False(waiting.HasAny("s1", Now.AddMinutes(1)));
        Assert.True(waiting.HasAny("s2", Now.AddMinutes(1)));

        // Still on its way: the same text isn't queued twice.
        Assert.False(waiting.Add("s1", "first", Now.AddMinutes(1), Hour));
    }

    [Fact]
    public void ExpiredMessagesAreDropped()
    {
        var waiting = new UndeliveredMessages();
        waiting.Add("s1", "announcement", Now, TimeSpan.FromMinutes(3));
        waiting.Add("s1", "failure", Now, Hour);

        Assert.Equal(["failure"], waiting.Take("s1", Now.AddMinutes(5)));
        Assert.False(waiting.Add("s1", "never kept", Now, TimeSpan.Zero));
        Assert.False(waiting.HasAny("s1", Now.AddMinutes(5)));
    }

    [Fact]
    public void KeepsOnlyTheNewestFew()
    {
        var waiting = new UndeliveredMessages();
        for (var i = 0; i < UndeliveredMessages.MaxPerSession + 2; i++)
        {
            waiting.Add("s1", $"m{i}", Now, Hour);
        }

        var kept = waiting.Take("s1", Now);
        Assert.Equal(UndeliveredMessages.MaxPerSession, kept.Count);
        Assert.Equal("m2", kept[0]);
    }

    [Fact]
    public async Task AConnectionLostBeforeTheSendKeepsTheMessage()
    {
        // Review round 6, finding 5: a reconnect starts the flush, then the connection drops before the worker sends.
        var waiting = new UndeliveredMessages();
        waiting.Add("s1", "failure", Now, Hour);
        Assert.Equal(["failure"], waiting.Take("s1", Now.AddMinutes(1)));

        var sends = 0;
        Assert.False(await waiting.SendAsync("s1", "failure", () => false, () => { sends++; return Task.CompletedTask; }, () => Now.AddMinutes(1)));
        Assert.Equal(0, sends);

        // Waiting again, with the hour it had from the start.
        Assert.True(waiting.HasAny("s1", Now.AddMinutes(59)));
        Assert.False(waiting.HasAny("s1", Now.AddMinutes(61)));
    }

    [Fact]
    public async Task AFailedSendKeepsTheMessageAndPassesTheErrorOn()
    {
        var waiting = new UndeliveredMessages();
        waiting.Add("s1", "failure", Now, Hour);
        waiting.Take("s1", Now);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            waiting.SendAsync("s1", "failure", () => true, () => throw new InvalidOperationException("socket closed"), () => Now));

        Assert.Equal(["failure"], waiting.Take("s1", Now.AddMinutes(1)));
    }

    [Fact]
    public async Task ACompletedSendRemovesTheMessage()
    {
        var waiting = new UndeliveredMessages();
        waiting.Add("s1", "failure", Now, Hour);
        Assert.True(waiting.TakeOne("s1", "failure", Now));
        Assert.False(waiting.TakeOne("s1", "failure", Now));

        Assert.True(await waiting.SendAsync("s1", "failure", () => true, () => Task.CompletedTask, () => Now));

        Assert.False(waiting.HasAny("s1", Now));
        Assert.True(waiting.Add("s1", "failure", Now, Hour));
    }

    [Fact]
    public async Task ASocketClosedDuringTheSendKeepsTheMessageUntilReconnect()
    {
        // Review round 7, finding 11: Jellyfin completes the send without sending once no socket is open.
        var waiting = new UndeliveredMessages();
        waiting.Add("s1", "failure", Now, Hour);
        Assert.True(waiting.TakeOne("s1", "failure", Now));

        var connected = true;
        var closeBeforeSend = true;
        var shown = new List<string>();
        Task Send()
        {
            connected &= !closeBeforeSend;
            if (connected)
            {
                shown.Add("failure");
            }

            return Task.CompletedTask;
        }

        Assert.False(await waiting.SendAsync("s1", "failure", () => connected, Send, () => Now.AddMinutes(1)));
        Assert.Empty(shown);
        Assert.False(waiting.Add("s1", "failure", Now.AddMinutes(1), Hour));

        // Reconnected: sent again, and then gone.
        connected = true;
        closeBeforeSend = false;
        var retry = waiting.Take("s1", Now.AddMinutes(2));
        Assert.Equal(["failure"], retry);
        Assert.True(await waiting.SendAsync("s1", "failure", () => connected, Send, () => Now.AddMinutes(2)));
        Assert.Equal(["failure"], shown);
        Assert.False(waiting.HasAny("s1", Now.AddMinutes(2)));
        Assert.Empty(waiting.Take("s1", Now.AddMinutes(2)));
    }

    [Fact]
    public async Task ASocketClosedDuringTheSendStillExpiresOnTime()
    {
        var waiting = new UndeliveredMessages();
        waiting.Add("s1", "announcement", Now, TimeSpan.FromMinutes(3));
        waiting.Take("s1", Now);

        var connected = true;
        Assert.False(await waiting.SendAsync("s1", "announcement", () => connected, () => { connected = false; return Task.CompletedTask; }, () => Now.AddMinutes(1)));

        Assert.True(waiting.HasAny("s1", Now.AddMinutes(2)));
        Assert.False(waiting.HasAny("s1", Now.AddMinutes(4)));
    }

    [Fact]
    public async Task ASendWhileConnectedThroughoutIsNotRepeated()
    {
        var waiting = new UndeliveredMessages();
        waiting.Add("s1", "failure", Now, Hour);
        waiting.Take("s1", Now);

        var sends = 0;
        var checks = 0;
        Assert.True(await waiting.SendAsync("s1", "failure", () => { checks++; return true; }, () => { sends++; return Task.CompletedTask; }, () => Now));

        Assert.Equal(1, sends);
        Assert.Equal(2, checks);
        Assert.False(waiting.HasAny("s1", Now));
        Assert.Empty(waiting.Take("s1", Now.AddMinutes(1)));
    }

    [Fact]
    public void MessagesOnTheirWayAreNotPushedOut()
    {
        var waiting = new UndeliveredMessages();
        waiting.Add("s1", "on its way", Now, Hour);
        waiting.Take("s1", Now);
        for (var i = 0; i < UndeliveredMessages.MaxPerSession + 2; i++)
        {
            waiting.Add("s1", $"m{i}", Now, Hour);
        }

        waiting.Failed("s1", "on its way", Now);
        Assert.Contains("on its way", waiting.Take("s1", Now));
    }
}
