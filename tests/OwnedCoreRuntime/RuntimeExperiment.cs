using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using SplinterCellCNLauncher.Services;

internal sealed class RuntimeReport
{
    public string Schema { get; } = "ff-owned-core-runtime-v1";
    public string Kind { get; } = "finite-owned-core-nic-runtime";
    public bool Passed { get; set; }
    public string Stage { get; set; } = "INPUT";
    public string? ErrorCode { get; set; }
    public string? CleanupErrorCode { get; set; }
    public string? ProbeId { get; set; }
    public string? Instance { get; set; }
    public string? CoreSha256 { get; set; }
    public long? CoreBytes { get; set; }
    public string? SourceTreeSha256 { get; set; }
    public string? BuildRecipeSha256 { get; set; }
    public string? InertProfileSha256 { get; set; }
    public string? ConfigurationSha256 { get; set; }
    public string? KernelBeforeSha256 { get; set; }
    public string? KernelAfterSha256 { get; set; }
    public bool OwnedProcessStartAttempted { get; set; }
    public bool CoreReadHelloCompleted { get; set; }
    public bool CorrectSourceKernelVerified { get; set; }
    public bool WrongSourceUnavailable { get; set; }
    public bool ReadChannelEnded { get; set; }
    public bool NicAbsentWhileCoreAlive { get; set; }
    public bool ExactOwnedJobDrained { get; set; }
    public bool ForeignKernelIdentitiesEqual { get; set; }
    public bool InputPinsReleased { get; set; }
    public bool ConfigurationRemoved { get; set; }
    public bool PublicAdmission => false;
    public bool PeerAuthenticationExercised => false;
    public bool PeerTrafficExercised => false;
    public bool GameExecuted => false;
    public bool DriverUninstalled => false;
}
internal static class RuntimeExperiment
{
    private static void Require(bool value, string code) { if (!value) throw new RuntimeCheckFailure(code); }
    internal static void Host()
    {
        Require(OperatingSystem.IsWindowsVersionAtLeast(10,0,26100) && !Environment.Is64BitProcess
            && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
            && Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted"
            && Environment.GetEnvironmentVariable("ImageOS") is "win25" or "win25-vs2026", "HOST_REJECTED");
        if (!OperatingSystem.IsWindows()) throw new RuntimeCheckFailure("HOST_REJECTED");
        using var identity = WindowsIdentity.GetCurrent();
        Require(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), "HOST_REJECTED");
    }
    internal static string Failure(Exception error, string stage) => error is RuntimeCheckFailure known ? known.Code : stage switch
    {
        "INPUT" => "INPUT_REJECTED", "HOST" => "HOST_REJECTED", "PREFLIGHT" => "KERNEL_PREFLIGHT_FAILED",
        "CONFIGURATION" => "CONFIGURATION_FAILED", "START" => "CORE_START_FAILED", "OBSERVE" => "CORE_OBSERVATION_FAILED",
        "WRONG_SOURCE" => "WRONG_SOURCE_FAILED", "END_READS" => "NORMAL_CLEANUP_OBSERVATION_FAILED",
        _ => "RUNTIME_FAILED",
    };
    private static void VerifyPinsReleased(RuntimeInputs inputs)
    {
        // Open only; do not change verified bytes. A failed ordinary-sharing
        // check remains a failed probe, not permission to change ACL/attributes.
        foreach (string path in new[] { inputs.CorePath, inputs.Configuration, Path.Combine(Path.GetDirectoryName(inputs.CorePath)!,"wintun.dll") })
        { using var stream = new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None); }
    }
    internal static async Task Run(RuntimeInputs inputs, RuntimeReport report)
    {
        report.ProbeId = inputs.ProbeId; report.Instance = inputs.Instance;
        report.CoreSha256 = inputs.CoreSha256; report.CoreBytes = inputs.CoreBytes;
        report.SourceTreeSha256 = inputs.SourceTreeSha256; report.BuildRecipeSha256 = inputs.RecipeSha256;
        report.InertProfileSha256 = inputs.ProfileSha256;
        OwnedCoreReadChannel? channel = null; WindowsNicInventory? before = null;
        bool configurationOwned = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            report.Stage = "HOST"; Host();
            report.Stage = "PREFLIGHT";
            before = WindowsNicInventory.Capture(); report.KernelBeforeSha256 = before.Digest();
            Require(!before.ConflictsWithProbeSubnet(), "SUBNET_CONFLICT");
            Require(before.Interfaces.All(n => n.Guid != inputs.AdapterGuid && n.Alias != inputs.AdapterName), "ADAPTER_COLLISION");
            // Fixed nonzero loopback only. Close before core startup; a later
            // collision must fail the actual core, never trigger a port scan.
            try
            {
                using var rpc = new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);
                rpc.ExclusiveAddressUse = true; rpc.Bind(new IPEndPoint(IPAddress.Loopback,15998));
            }
            catch (SocketException) { throw new RuntimeCheckFailure("RPC_PORT_UNAVAILABLE"); }
            report.Stage = "CONFIGURATION";
            byte[] configuration = inputs.ConfigurationBytes(); report.ConfigurationSha256 = RuntimeInputs.Sha(configuration);
            using (var output = new FileStream(inputs.Configuration,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            { configurationOwned = true; output.Write(configuration); output.Flush(true); }
            var specification = inputs.CreateSpecification(configuration);
            report.Stage = "START"; report.OwnedProcessStartAttempted = true;
            channel = await OwnedCoreReadChannel.StartAsync(specification,inputs.Instance,timeout.Token).ConfigureAwait(false);
            report.CoreReadHelloCompleted = true;
            report.Stage = "OBSERVE";
            var response = await channel.ObserveAsync(RuntimeInputs.Source,timeout.Token).ConfigureAwait(false);
            Require(response is not null, "NIC_UNAVAILABLE");
            var observed = WindowsNicInventory.Capture();
            KernelNic owned = observed.Require(response!, inputs.AdapterName, inputs.AdapterGuid, RuntimeInputs.Source);
            observed.RequireOnlyOwnedProbeChanges(before,owned);
            Require(channel.IsOwnedProcessAlive, "CORE_EXITED_DURING_OBSERVATION");
            report.CorrectSourceKernelVerified = true;
            report.Stage = "WRONG_SOURCE";
            Require(await channel.ObserveAsync(RuntimeInputs.WrongSource,timeout.Token).ConfigureAwait(false) is null, "WRONG_SOURCE_ACCEPTED");
            Require(channel.IsOpen && channel.IsOwnedProcessAlive, "CORE_EXITED_DURING_OBSERVATION");
            report.WrongSourceUnavailable = true;
            report.Stage = "END_READS";
            await NormalCleanupObservation.Run(() => {
                bool aliveBefore = channel.IsOwnedProcessAlive;
                var current = WindowsNicInventory.Capture();
                return new CleanupSample(aliveBefore, channel.IsOwnedProcessAlive, current.Interfaces.Contains(owned),
                    current.Absent(owned), current.Digest(owned) == before.Digest());
            }, () => { channel.EndReads(); report.ReadChannelEnded = true; }, timeout.Token).ConfigureAwait(false);
            report.NicAbsentWhileCoreAlive = true;
        }
        catch (Exception error) { report.ErrorCode = Failure(error,report.Stage); }
        finally
        {
            // EndReads does not relinquish the process. Always stop the exact
            // job first. A missing/failed Start result is not evidence of drain.
            if (channel is not null)
            {
                try { channel.EndReads(); report.ReadChannelEnded = true; }
                catch { report.CleanupErrorCode ??= "READ_CLOSE_FAILED"; }
                try { await channel.DisposeAsync().ConfigureAwait(false); report.ExactOwnedJobDrained = true; }
                catch { report.CleanupErrorCode ??= "EXACT_JOB_DRAIN_FAILED"; }
            }
            // Never interpret query failure as adapter removal. Audit even when
            // the primary test failed; keep the original fixed failure code.
            if (before is not null)
            {
                try
                {
                    long deadline = Environment.TickCount64 + 8000;
                    while (true)
                    {
                        var after = WindowsNicInventory.Capture(); report.KernelAfterSha256 = after.Digest();
                        report.ForeignKernelIdentitiesEqual = after.Digest() == before.Digest();
                        if (report.ForeignKernelIdentitiesEqual || Environment.TickCount64 >= deadline) break;
                        await Task.Delay(50).ConfigureAwait(false);
                    }
                    if (!report.ForeignKernelIdentitiesEqual) report.CleanupErrorCode ??= "KERNEL_NOT_RESTORED";
                }
                catch { report.CleanupErrorCode ??= "KERNEL_POSTFLIGHT_FAILED"; }
            }
            if (configurationOwned && report.ExactOwnedJobDrained)
            {
                try { VerifyPinsReleased(inputs); report.InputPinsReleased = true; }
                catch { report.CleanupErrorCode ??= "INPUT_STILL_PINNED"; }
                try { File.Delete(inputs.Configuration); report.ConfigurationRemoved = !File.Exists(inputs.Configuration); }
                catch { report.CleanupErrorCode ??= "CONFIGURATION_CLEANUP_FAILED"; }
            }
            // Before any process attempt, only this exact CreateNew config can
            // be removed. After an unknown drain keep inputs and firewall blocks.
            else if (configurationOwned && !report.OwnedProcessStartAttempted)
            {
                try { File.Delete(inputs.Configuration); report.ConfigurationRemoved = !File.Exists(inputs.Configuration); }
                catch { report.CleanupErrorCode ??= "CONFIGURATION_CLEANUP_FAILED"; }
            }
        }
        report.Passed = report.ErrorCode is null && report.CleanupErrorCode is null
            && report.CoreReadHelloCompleted && report.CorrectSourceKernelVerified && report.WrongSourceUnavailable
            && report.ReadChannelEnded && report.NicAbsentWhileCoreAlive && report.ExactOwnedJobDrained
            && report.ForeignKernelIdentitiesEqual && report.InputPinsReleased && report.ConfigurationRemoved;
        if (report.Passed) report.Stage = "COMPLETE";
    }
}
