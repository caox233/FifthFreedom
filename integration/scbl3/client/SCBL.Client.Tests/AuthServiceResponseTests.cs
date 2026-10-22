using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Network;
using SplinterCellCNLauncher.Services;

namespace SCBL.Client.Tests;

public sealed class AuthServiceResponseTests
{
    [Fact]
    public void Complete_response_creates_a_source_bound_session()
    {
        var store = new AuthenticatedSessionStore();
        var response = new LoginResponse
        {
            User = new User { Id = "account-42", Username = "CanonicalPlayer" },
            Token = "opaque-token"
        };

        LoginResult result = AuthService.InterpretLoginResponse(
            response,
            "player",
            "10.66.0.42",
            store);

        Assert.Equal(LoginStatus.Success, result.Status);
        Assert.True(store.TryGetForBindIp("10.66.0.42", out AuthenticatedSession session));
        Assert.Equal("opaque-token", session.SessionToken);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Incomplete_or_old_response_fails_closed(bool includeUser, bool includeToken)
    {
        var response = new LoginResponse
        {
            User = includeUser ? new User { Id = "account-42", Username = "player" } : null,
            Token = includeToken ? "old-or-incomplete-token" : ""
        };

        LoginResult result = AuthService.InterpretLoginResponse(
            response,
            "player",
            "10.66.0.42",
            new AuthenticatedSessionStore());

        Assert.Equal(LoginStatus.ServerError, result.Status);
        Assert.Contains("认证会话", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_server_error_is_preserved()
    {
        LoginResult result = AuthService.InterpretLoginResponse(
            new LoginResponse { Error = "denied" },
            "player",
            "10.66.0.42",
            new AuthenticatedSessionStore());

        Assert.Equal(LoginStatus.ServerError, result.Status);
        Assert.Equal("denied", result.Message);
    }

    [Fact]
    public void Pre_registration_account_id_is_stable_without_legacy_md5()
    {
        string first = AccountIdService.CreateStableAccountId(" Player ");
        string second = AccountIdService.CreateStableAccountId("player");

        Assert.Equal(first, second);
        Assert.True(Guid.TryParse(first, out _));
        Assert.Equal("5593b5cd-baf3-3929-77fc-0945fb85f118", first);
    }
}
