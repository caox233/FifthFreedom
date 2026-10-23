namespace SplinterCellCNLauncher.Services;

public enum NetworkEnvironmentKind { Lan, Public }
public enum LanRole { Join, Host }
public enum PublicTransport { Fast, Compatible }
public enum NetworkEnvironmentStatus
{
    Idle, Switching, Ready, Disconnected, Unavailable, ConnectionFailed, RollbackFailed, CleanupFailed, Cancelled, Closed
}
public enum NetworkEnvironmentChangeOutcome
{
    Applied, Unchanged, RejectedGameActive, RejectedOwnedLaunch, Unavailable,
    ConnectionFailed, Cancelled, Closed
}
public enum NetworkEnvironmentRollback { NotNeeded, Restored, Failed }

/// <summary>Disjoint deployment selection. Public transport is never a LAN role.</summary>
public sealed record NetworkEnvironmentSelection
{
    public NetworkEnvironmentKind Environment { get; }
    public LanRole? LanRole { get; }
    public PublicTransport? PublicTransport { get; }

    private NetworkEnvironmentSelection(NetworkEnvironmentKind environment, LanRole? role, PublicTransport? transport)
        => (Environment, LanRole, PublicTransport) = (environment, role, transport);

    public static NetworkEnvironmentSelection ForLan(LanRole role)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        return new(NetworkEnvironmentKind.Lan, role, null);
    }

    public static NetworkEnvironmentSelection ForPublic(PublicTransport transport)
    {
        if (!Enum.IsDefined(transport)) throw new ArgumentOutOfRangeException(nameof(transport));
        return new(NetworkEnvironmentKind.Public, null, transport);
    }
}

public readonly record struct NetworkEnvironmentActivity(bool GameActive, bool OwnedLaunch);

/// <summary>
/// Presentation-only data: no endpoint, bind address, account, credential, token,
/// arbitrary exception text, process handle or serializable adapter reference.
/// </summary>
public sealed record NetworkEnvironmentSnapshot(
    NetworkEnvironmentSelection? Selection,
    bool IsReady,
    bool IsSwitching,
    bool IsClosed,
    NetworkEnvironmentStatus Status,
    long? LatencyMilliseconds,
    bool GameActive,
    bool OwnedLaunch)
{
    public bool CanSwitch => !IsClosed && !IsSwitching && !GameActive && !OwnedLaunch;
}

public sealed record NetworkEnvironmentChangeResult(
    NetworkEnvironmentChangeOutcome Outcome,
    NetworkEnvironmentRollback Rollback,
    NetworkEnvironmentSelection Requested,
    NetworkEnvironmentSnapshot Snapshot);

/// <summary>
/// Owns only resources created by this connection attempt. Disposal must never
/// terminate an unrelated process, shared tunnel or another controller's host.
/// Status getters must be thread-safe, nonblocking and side-effect-free; readiness
/// may change during the lease lifetime and is sampled for presentation/reconnect.
/// </summary>
public interface INetworkEnvironmentLease : IAsyncDisposable
{
    bool IsReady { get; }
    long? LatencyMilliseconds { get; }
}

/// <summary>
/// An explicitly selected LAN Join role may wait for an address or a compatible
/// server. This permits leaving an owned host while offline; it never grants
/// remote readiness, and is not applicable to Host or Public candidates.
/// </summary>
public interface IConfiguredLanJoinLease : INetworkEnvironmentLease
{
    bool IsConfigured { get; }
}

public interface INetworkEnvironmentAdapter
{
    NetworkEnvironmentKind Environment { get; }
    bool IsAvailable { get; }
    Task<INetworkEnvironmentLease> ConnectAsync(NetworkEnvironmentSelection selection, CancellationToken cancellationToken);
}

/// <summary>UI-02 deliberately does not connect the donor public stack.</summary>
public sealed class UnavailablePublicNetworkEnvironmentAdapter : INetworkEnvironmentAdapter
{
    public NetworkEnvironmentKind Environment => NetworkEnvironmentKind.Public;
    public bool IsAvailable => false;
    public Task<INetworkEnvironmentLease> ConnectAsync(NetworkEnvironmentSelection selection, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The public adapter is not integrated.");
}
