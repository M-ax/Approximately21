using Approximately21.Networking;
using Xunit;

namespace Approximately21.Tests;

public class BlackjackLobbyResolverTests
{
    [Fact]
    public void OnlyVerifiedGameHostDeterminesAuthority()
    {
        var members = new ulong[] { 1, 2, 3 };
        var client = BlackjackLobbyResolver.Resolve(10, 2, false, true, 3, members);
        Assert.Equal(3UL, client.HostPlayerId);
        Assert.False(client.IsHost);
        var host = BlackjackLobbyResolver.Resolve(10, 3, true, true, 0, members);
        Assert.True(host.IsHost);
        Assert.Equal(3UL, host.HostPlayerId);
    }

    [Theory]
    [InlineData(10UL, 2UL, false, true, 0UL)]
    [InlineData(10UL, 2UL, false, true, 2UL)]
    [InlineData(10UL, 2UL, false, true, 4UL)]
    [InlineData(10UL, 2UL, false, false, 1UL)]
    [InlineData(10UL, 2UL, true, false, 0UL)]
    [InlineData(0UL, 2UL, true, true, 0UL)]
    [InlineData(10UL, 0UL, true, true, 0UL)]
    public void UnverifiedOrUnreadySessionsFailClosed(ulong lobby, ulong local, bool host, bool ready, ulong remote)
    {
        Assert.Null(BlackjackLobbyResolver.Resolve(lobby, local, host, ready, remote, new ulong[] { 1, 2, 3 }));
    }

    [Fact]
    public void InvalidMembershipFailsClosed()
    {
        Assert.Null(BlackjackLobbyResolver.Resolve(10, 1, true, true, 0, null));
        Assert.Null(BlackjackLobbyResolver.Resolve(10, 1, true, true, 0, new ulong[] { 2 }));
        Assert.Null(BlackjackLobbyResolver.Resolve(10, 1, true, true, 0, new ulong[] { 0, 1 }));
    }
}