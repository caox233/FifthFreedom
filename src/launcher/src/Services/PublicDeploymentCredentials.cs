using System.Text.Json;

namespace SplinterCellCNLauncher.Services;

/// <summary>Explicit credential namespace; a Public configuration is never inferred from an address.</summary>
public sealed class CredentialEnvironmentContext : IEquatable<CredentialEnvironmentContext>
{
    public static CredentialEnvironmentContext Lan { get; } = new(null);
    public static CredentialEnvironmentContext UnconfiguredPublic { get; } = new(null, true);
    private readonly PublicDeploymentProfile? _deployment;
    private readonly bool unconfiguredPublic;
    public NetworkEnvironmentKind Environment => _deployment is null && !unconfiguredPublic ? NetworkEnvironmentKind.Lan : NetworkEnvironmentKind.Public;
    private CredentialEnvironmentContext(PublicDeploymentProfile? deployment, bool unconfigured = false)
        => (_deployment, unconfiguredPublic) = (deployment, unconfigured);
    public static CredentialEnvironmentContext ForPublic(PublicDeploymentProfile deployment)
        => new(deployment ?? throw new ArgumentException("PUBLIC_CREDENTIAL_DEPLOYMENT_REQUIRED"));

    public CredentialIdentity CreateIdentity(string server, string username)
    {
        if (unconfiguredPublic) throw new ArgumentException("PUBLIC_CREDENTIAL_DEPLOYMENT_REQUIRED");
        if (_deployment is null) return new CredentialIdentity(Environment, CredentialStore.Scope(server, username));
        if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || username.Any(char.IsControl) || !username.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            throw new ArgumentException("PUBLIC_CREDENTIAL_IDENTITY_INVALID");
        string endpoint = _deployment.CanonicalNativeEndpoint(server);
        string scope = "FIFTH-FREEDOM-PUBLIC-DPAPI-v1\n" + JsonSerializer.Serialize(new[] {
            _deployment.CredentialNamespace, endpoint, username.Trim() });
        return new CredentialIdentity(Environment, scope);
    }
    public bool Equals(CredentialEnvironmentContext? other)
        => other is not null && Environment == other.Environment && _deployment?.CredentialNamespace == other._deployment?.CredentialNamespace;
    public override bool Equals(object? obj) => obj is CredentialEnvironmentContext other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Environment, _deployment?.CredentialNamespace);
    public override string ToString() => "CredentialEnvironmentContext (" + Environment + ")";
}

/// <summary>No public raw scope/password/endpoint rendering; only this factory can construct a namespace.</summary>
public sealed class CredentialIdentity : IEquatable<CredentialIdentity>
{
    public NetworkEnvironmentKind Environment { get; }
    internal string ScopeValue { get; }
    internal CredentialIdentity(NetworkEnvironmentKind environment, string scope) => (Environment, ScopeValue) = (environment, scope);
    public bool Equals(CredentialIdentity? other) => other is not null && Environment == other.Environment && ScopeValue == other.ScopeValue;
    public override bool Equals(object? obj) => obj is CredentialIdentity other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Environment, ScopeValue);
    public override string ToString() => "CredentialIdentity (" + Environment + ")";
}
