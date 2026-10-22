using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class AuthenticatedSessionStoreTests
{
    [Fact]
    public void Session_is_available_only_for_the_overlay_ip_used_at_login()
    {
        var store = new AuthenticatedSessionStore();

        Assert.True(store.TryUpdate("account-42", "CanonicalPlayer", "opaque-token", "10.66.0.42"));
        Assert.True(store.TryGetForBindIp("10.66.0.42", out AuthenticatedSession session));
        Assert.Equal("account-42", session.AccountId);
        Assert.Equal("CanonicalPlayer", session.CanonicalUsername);
        Assert.Equal("opaque-token", session.SessionToken);
        Assert.True(store.TryGetCurrentBindIp(out string currentBindIp));
        Assert.Equal("10.66.0.42", currentBindIp);
        Assert.False(store.TryGetForBindIp("10.66.0.43", out _));
    }

    [Theory]
    [InlineData("192.168.1.42")]
    [InlineData("127.0.0.1")]
    [InlineData("not-an-ip")]
    public void Non_overlay_bind_ip_is_rejected_and_clears_the_previous_session(string invalidBindIp)
    {
        var store = new AuthenticatedSessionStore();
        Assert.True(store.TryUpdate("account-42", "CanonicalPlayer", "opaque-token", "10.66.0.42"));

        Assert.False(store.TryUpdate("account-43", "OtherPlayer", "other-token", invalidBindIp));
        Assert.False(store.TryGetForBindIp("10.66.0.42", out _));
    }

    [Fact]
    public void Oversized_token_is_rejected_and_clears_the_previous_session()
    {
        var store = new AuthenticatedSessionStore();
        Assert.True(store.TryUpdate("account-42", "CanonicalPlayer", "opaque-token", "10.66.0.42"));

        Assert.False(store.TryUpdate("account-42", "CanonicalPlayer", new string('x', 4097), "10.66.0.42"));
        Assert.False(store.TryGetForBindIp("10.66.0.42", out _));
    }
}
