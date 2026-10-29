using System.Globalization;
using System.Net;
using System.Text.Json;
using SplinterCellCNLauncher.Models;

namespace SplinterCellCNLauncher.Services;

public enum PublicServerSettingsError { None, EndpointRequired, EndpointInvalid, UpdatePortInvalid }
public sealed record ValidatedPublicServerSettings(string PublicEndpoint, int TunnelPort, int UpdatePort);

/// <summary>Adapted donor server-settings contract: endpoint + independent update port, no connection side effects.</summary>
public static class PublicServerConfiguration
{
    public static bool TryValidate(string? endpointText, string? updatePortText,
        out ValidatedPublicServerSettings? settings, out PublicServerSettingsError error)
    {
        settings = null; error = PublicServerSettingsError.None;
        string endpoint = endpointText?.Trim() ?? "";
        if (endpoint.Length == 0) { error = PublicServerSettingsError.EndpointRequired; return false; }
        foreach (string prefix in new[] { "tcp://", "udp://", "ws://", "wss://", "quic://" })
            if (endpoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { endpoint = endpoint[prefix.Length..]; break; }
        if (endpoint.Length > 512 || endpoint.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            || endpoint.IndexOfAny(['/', '\\', '?', '#', '@', '%']) >= 0
            || !Uri.TryCreate("tcp://" + endpoint, UriKind.Absolute, out var uri)
            || uri.Port is < 1 or > 65535 || uri.UserInfo.Length != 0
            || uri.HostNameType is UriHostNameType.Unknown or UriHostNameType.Basic)
        { error = PublicServerSettingsError.EndpointInvalid; return false; }
        if (!int.TryParse(updatePortText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int updatePort)
            || updatePort is < 1 or > 65535)
        { error = PublicServerSettingsError.UpdatePortInvalid; return false; }
        string host = uri.IdnHost.Trim('[', ']').ToLowerInvariant();
        string normalized = host.Contains(':') ? $"[{host}]:{uri.Port}" : $"{host}:{uri.Port}";
        settings = new(normalized, uri.Port, updatePort);
        return true;
    }

    public static bool IsExample(ValidatedPublicServerSettings settings)
    {
        var uri = new Uri("tcp://" + settings.PublicEndpoint);
        return uri.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("invalid", StringComparison.OrdinalIgnoreCase);
    }
}

public enum PublicReadinessStage
{
    ServerConfigurationRequired, DeploymentConfigurationRequired, DeploymentConfigurationInvalid,
    RuntimeInputsInvalid, OwnedNetworkAdmissionRequired
}

public sealed record PublicReadiness(PublicReadinessStage Stage, bool ServerConfigured, bool DeploymentConfigured,
    bool RuntimeFilesVerified)
{
    // There is no admitted Windows application-level owned network/session provider yet.
    // Configuration and even exact local file hashes cannot manufacture that authority.
    public bool IsReady => false;
    public override string ToString() => $"PublicReadiness ({Stage}; ready=false)";
}

/// <summary>
/// Explicit non-secret deployment input; every field is named and reviewed by the importer.
/// Loading verifies schema/profile and local pinned files only. It grants no TLS identity,
/// WFP coverage, NIC ownership, native bearer or authority to execute the declared files.
/// </summary>
public sealed record PublicDeploymentConfiguration(PublicDeploymentProfile Profile, string RuntimeDirectory)
{
    public static PublicDeploymentConfiguration Load(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw Invalid();
            string full = Path.GetFullPath(path);
            for (string? item = full; item is not null; item = Path.GetDirectoryName(item))
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw Invalid();
            using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > 32768) throw Invalid();
            byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 6 });
            var value = document.RootElement;
            Shape(value, ["schema", "deployment_id", "control_origin", "overlay_cidr", "server_overlay_ip", "transport", "peer_uri", "runtime"]);
            if (value.GetProperty("schema").GetInt32() != 1) throw Invalid();
            var runtime = value.GetProperty("runtime"); Shape(runtime, ["version", "directory", "pins"]);
            string directory = runtime.GetProperty("directory").GetString() ?? throw Invalid();
            if (!Path.IsPathFullyQualified(directory) || Path.GetFullPath(directory) != directory) throw Invalid();
            string transportName = value.GetProperty("transport").GetString() ?? "";
            if (transportName is not ("Fast" or "Compatible")) throw Invalid();
            var pins = new List<PublicRuntimePin>();
            foreach (var row in runtime.GetProperty("pins").EnumerateArray())
            {
                Shape(row, ["role", "bytes", "sha256"]);
                string? roleName = row.GetProperty("role").GetString();
                if (!Enum.TryParse<PublicRuntimeRole>(roleName, out var role)
                    || !Enum.IsDefined(role) || roleName != role.ToString() || pins.Count == 4) throw Invalid();
                pins.Add(new(role, row.GetProperty("bytes").GetInt64(), row.GetProperty("sha256").GetString()!));
            }
            var expected = new PublicDeploymentRuntimePins(runtime.GetProperty("version").GetString()!, pins);
            var profile = new PublicDeploymentProfile(value.GetProperty("deployment_id").GetString()!,
                value.GetProperty("control_origin").GetString()!, value.GetProperty("overlay_cidr").GetString()!,
                value.GetProperty("server_overlay_ip").GetString()!, NetworkEnvironmentPreferences.Transport(transportName),
                value.GetProperty("peer_uri").GetString()!, expected);
            return new(profile, directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException
            or InvalidOperationException or KeyNotFoundException or OverflowException or System.Security.SecurityException)
        { throw Invalid(); }
    }
    private static void Shape(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != fields.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || names.Except(fields, StringComparer.Ordinal).Any()) throw Invalid();
    }
    private static IOException Invalid() => new("PUBLIC_DEPLOYMENT_CONFIGURATION_INVALID");
    public override string ToString() => "PublicDeploymentConfiguration (declared inputs; not runtime admission)";
}

/// <summary>
/// Production configuration adapter. It selects the requested donor transport and
/// verifies explicit deployment inputs. It intentionally does not replace missing
/// owned WFP/NIC/native-session admission with donor unconditional tunnel startup.
/// </summary>
public sealed class PublicNetworkEnvironmentAdapter(Func<LauncherSettings> readSettings) : INetworkEnvironmentAdapter
{
    private PublicReadiness current = new(PublicReadinessStage.ServerConfigurationRequired, false, false, false);
    private long assessmentGeneration;
    public PublicReadiness CurrentReadiness => Volatile.Read(ref current);
    public NetworkEnvironmentKind Environment => NetworkEnvironmentKind.Public;
    public bool IsAvailable => true; // Selection is available; readiness is a separate contract.
    public PublicReadiness Assess()
    {
        long generation = Interlocked.Increment(ref assessmentGeneration);
        var result = AssessInputs(CaptureSettings());
        if (generation == Volatile.Read(ref assessmentGeneration)) Volatile.Write(ref current, result);
        return result;
    }
    public async Task<PublicReadiness> RefreshAsync(CancellationToken token)
    {
        // Capture on the caller's dispatcher before work moves off-thread. Later
        // edits/cancellation cannot publish this older configuration's assessment.
        var settings = CaptureSettings();
        long generation = Interlocked.Increment(ref assessmentGeneration);
        var result = await Task.Run(() => { token.ThrowIfCancellationRequested(); return AssessInputs(settings); }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (generation == Volatile.Read(ref assessmentGeneration)) Volatile.Write(ref current, result);
        return result;
    }
    private LauncherSettings CaptureSettings()
    {
        var settings = readSettings();
        return new LauncherSettings { PublicEndpoint = settings.PublicEndpoint, PublicUpdatePort = settings.PublicUpdatePort,
            NetworkMode = settings.NetworkMode, PublicDeploymentConfigurationPath = settings.PublicDeploymentConfigurationPath };
    }
    private static PublicReadiness AssessInputs(LauncherSettings settings)
    {
        if (!PublicServerConfiguration.TryValidate(settings.PublicEndpoint, settings.PublicUpdatePort.ToString(CultureInfo.InvariantCulture),
                out var endpoint, out _) || PublicServerConfiguration.IsExample(endpoint!))
            return new(PublicReadinessStage.ServerConfigurationRequired, false, false, false);
        if (string.IsNullOrWhiteSpace(settings.PublicDeploymentConfigurationPath))
            return new(PublicReadinessStage.DeploymentConfigurationRequired, true, false, false);
        PublicDeploymentConfiguration configuration;
        try
        {
            configuration = PublicDeploymentConfiguration.Load(settings.PublicDeploymentConfigurationPath);
            var peer = configuration.Profile.PeerUri;
            var declared = new Uri("tcp://" + endpoint!.PublicEndpoint);
            if (configuration.Profile.Transport != NetworkEnvironmentPreferences.Transport(settings.NetworkMode)
                || !peer.IdnHost.Equals(declared.IdnHost, StringComparison.OrdinalIgnoreCase) || peer.Port != declared.Port)
                throw new IOException("PUBLIC_DEPLOYMENT_ENDPOINT_MISMATCH");
        }
        catch (IOException) { return new(PublicReadinessStage.DeploymentConfigurationInvalid, true, false, false); }
        try { configuration.Profile.RuntimePins.VerifyFiles(configuration.RuntimeDirectory); }
        catch (IOException) { return new(PublicReadinessStage.RuntimeInputsInvalid, true, true, false); }
        return new(PublicReadinessStage.OwnedNetworkAdmissionRequired, true, true, true);
    }
    public async Task<INetworkEnvironmentLease> ConnectAsync(NetworkEnvironmentSelection selection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (selection.Environment != NetworkEnvironmentKind.Public || selection.PublicTransport is null)
            throw new ArgumentException("PUBLIC_SELECTION_REQUIRED");
        // The assessment is observable through Assess; it never authenticates a session.
        _ = await RefreshAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return new ConfigurationLease();
    }
    private sealed class ConfigurationLease : IConfiguredPublicLease
    {
        private bool closed;
        public bool IsConfigured => !Volatile.Read(ref closed);
        public bool IsReady => false;
        public long? LatencyMilliseconds => null;
        public ValueTask DisposeAsync() { Volatile.Write(ref closed, true); return ValueTask.CompletedTask; }
    }
}
