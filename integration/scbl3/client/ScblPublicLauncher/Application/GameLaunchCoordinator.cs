using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

namespace SplinterCellCNLauncher.ApplicationLayer;

public enum GameLaunchStage
{
    ValidatingGame,
    CleaningConflicts,
    ConfirmingNetwork,
    ConnectingServer,
    CheckingVirtualLan,
    DeployingComponents,
    WritingConfiguration,
    StartingGame
}

public sealed record GameLaunchRequest(
    string Username,
    string Password,
    string GameDirectory,
    string GameExecutable,
    string GameplayMode,
    string LauncherVersion,
    string TunnelSecret,
    double ModFovScale);

public sealed record AccountLaunchContext(
    string Username,
    string Password,
    string AccountId,
    string BindIp,
    LoginResult? InitialLogin,
    bool AccountKnownMissing);

public sealed record GameLaunchCallbacks(
    Action SaveRuntimeSettings,
    Action<GameLaunchStage> ReportStage,
    Func<Task> CloseConflictingProcessesAsync,
    Func<Task<string>> EnsureNetworkReadyAsync,
    Func<string, Task> EnsureVirtualLanReadyAsync,
    Action<ControlPlaneBootstrapContext?> ApplyBootstrapContext,
    Func<AccountLaunchContext, Task> EnsureAccountReadyAsync,
    Action<string> StartBackgroundVirtualLanDiagnostics,
    Func<string, Task> StartGameAsync);

public sealed record GameLaunchExecutionResult(
    string AccountId,
    string BindIp,
    HookConfigWriteResult HookConfiguration);

/// <summary>
/// Executes the launcher use case without referencing WPF controls. UI prompts,
/// rendering and game-process presentation are supplied through narrow callbacks.
/// </summary>
public sealed class GameLaunchCoordinator
{
    private readonly GameLocatorService _gameLocatorService;
    private readonly DxModeCompatibilityService _dxModeCompatibilityService;
    private readonly ControlPlaneService _controlPlaneService;
    private readonly AuthService _authService;
    private readonly HookDllService _hookDllService;
    private readonly SaveGameService _saveGameService;
    private readonly HookConfigService _hookConfigService;
    private readonly AuthenticatedSessionStore _authenticatedSessionStore;
    private readonly ModSelectionPreparationService _modSelectionPreparationService;

    public GameLaunchCoordinator(
        GameLocatorService gameLocatorService,
        DxModeCompatibilityService dxModeCompatibilityService,
        ControlPlaneService controlPlaneService,
        AuthService authService,
        HookDllService hookDllService,
        SaveGameService saveGameService,
        HookConfigService hookConfigService,
        AuthenticatedSessionStore? authenticatedSessionStore = null,
        ModSelectionPreparationService? modSelectionPreparationService = null)
    {
        _gameLocatorService = gameLocatorService;
        _dxModeCompatibilityService = dxModeCompatibilityService;
        _controlPlaneService = controlPlaneService;
        _authService = authService;
        _hookDllService = hookDllService;
        _saveGameService = saveGameService;
        _hookConfigService = hookConfigService;
        _authenticatedSessionStore = authenticatedSessionStore ?? AuthenticatedSessionStore.Shared;
        _modSelectionPreparationService = modSelectionPreparationService
            ?? new ModSelectionPreparationService();
    }

    public async Task<GameLaunchExecutionResult> ExecuteAsync(
        GameLaunchRequest request,
        GameLaunchCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callbacks);

        GameplayMode requestedGameplayMode = GameplayModeNames.ParseOrOfficial(
            request.GameplayMode);
        callbacks.ReportStage(GameLaunchStage.ValidatingGame);
        ModRuntimeObservation modRuntime = requestedGameplayMode == GameplayMode.Mod
                ? await _hookDllService.ObserveModRuntimeFilesAsync()
                : ModRuntimeObservation.Unavailable;
        GameplayMode gameplayMode = LauncherConfigurationAvailabilityPolicy.ResolveForLaunch(
            request.GameplayMode,
            modRuntime);

        callbacks.SaveRuntimeSettings();
        _gameLocatorService.ValidateGameDirectory(request.GameDirectory, request.GameExecutable);
        if (!_dxModeCompatibilityService.ValidateModeFiles(
                request.GameDirectory,
                request.GameExecutable,
                out string dxValidationMessage))
        {
            throw new InvalidOperationException(dxValidationMessage);
        }

        callbacks.ReportStage(GameLaunchStage.CleaningConflicts);
        await callbacks.CloseConflictingProcessesAsync();

        callbacks.ReportStage(GameLaunchStage.ConfirmingNetwork);
        string bindIp = await callbacks.EnsureNetworkReadyAsync();
        if (string.IsNullOrWhiteSpace(bindIp))
            throw new InvalidOperationException("SCBL network returned an empty virtual IP address.");

        callbacks.ReportStage(GameLaunchStage.ConnectingServer);
        string accountId = AccountIdService.CreateStableAccountId(request.Username);
        Task<ControlPlaneBootstrapContext?> bootstrapTask = _controlPlaneService.GetBootstrapAsync(
            request.Username,
            request.LauncherVersion,
            bindIp,
            request.TunnelSecret);
        Task<LoginResult> loginTask = _authService.LoginPublicAsync(
            request.Username,
            request.Password,
            bindIp);
        Task lanPreflightTask = callbacks.EnsureVirtualLanReadyAsync(bindIp);

        ControlPlaneBootstrapContext? bootstrap = null;
        Task firstAccountSignal = await Task.WhenAny(bootstrapTask, loginTask);
        if (firstAccountSignal == bootstrapTask)
        {
            bootstrap = await bootstrapTask;
            callbacks.ApplyBootstrapContext(bootstrap);
            bool accountKnownMissing = bootstrap?.AccountExists == false;
            await callbacks.EnsureAccountReadyAsync(new AccountLaunchContext(
                request.Username,
                request.Password,
                accountId,
                bindIp,
                InitialLogin: accountKnownMissing ? null : await loginTask,
                AccountKnownMissing: accountKnownMissing));
        }
        else
        {
            await callbacks.EnsureAccountReadyAsync(new AccountLaunchContext(
                request.Username,
                request.Password,
                accountId,
                bindIp,
                InitialLogin: await loginTask,
                AccountKnownMissing: false));
        }

        if (_authenticatedSessionStore.TryGetForBindIp(bindIp, out AuthenticatedSession authenticatedSession))
            accountId = authenticatedSession.AccountId;

        callbacks.ReportStage(GameLaunchStage.CheckingVirtualLan);
        await lanPreflightTask;
        bootstrap ??= await bootstrapTask;
        callbacks.ApplyBootstrapContext(bootstrap);
        callbacks.StartBackgroundVirtualLanDiagnostics(bindIp);

        callbacks.ReportStage(GameLaunchStage.DeployingComponents);
        await _modSelectionPreparationService.EnsureForLaunchAsync(
            request.GameDirectory,
            gameplayMode);
        await _hookDllService.DeployHookDllSafelyAsync(
            request.GameDirectory,
            GameplayModeNames.ToName(gameplayMode));
        _saveGameService.DeployBaseSavesIfMissing();

        callbacks.ReportStage(GameLaunchStage.WritingConfiguration);
        HookConfigWriteResult hookConfig = _hookConfigService.WriteAuthFile(
            request.GameDirectory,
            request.Username,
            request.Password,
            accountId,
            bindIp,
            gameplayMode,
            request.ModFovScale);
        _hookConfigService.ValidateWrittenFileOrThrow(
            request.GameDirectory,
            request.Username,
            accountId,
            bindIp,
            gameplayMode,
            request.ModFovScale);
        LogService.Info(
            $"Hooks gameplay profile written: mode={GameplayModeNames.ToName(gameplayMode)}, " +
            $"gameplayDll={(gameplayMode == GameplayMode.Mod ? "required" : "disabled")}, " +
            $"fov={request.ModFovScale:0.00}.");

        callbacks.ReportStage(GameLaunchStage.StartingGame);
        await callbacks.StartGameAsync(request.GameExecutable);
        return new GameLaunchExecutionResult(accountId, bindIp, hookConfig);
    }
}
