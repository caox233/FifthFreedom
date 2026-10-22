using System;
namespace SplinterCellCNLauncher.Models;

public enum LoginStatus
{
    Success,
    InvalidPassword,
    UserNotFound,
    ConnectionFailed,
    ServerError
}

public sealed class LoginResult
{
    public LoginStatus Status { get; init; }
    public string Message { get; init; } = "";
    public string AccountId { get; init; } = "";
    public string CanonicalUsername { get; init; } = "";
    public string SessionToken { get; init; } = "";

    public static LoginResult Success(
        string accountId,
        string canonicalUsername,
        string sessionToken) => new()
        {
            Status = LoginStatus.Success,
            Message = "登录成功。",
            AccountId = accountId,
            CanonicalUsername = canonicalUsername,
            SessionToken = sessionToken
        };
}

public enum RegisterStatus
{
    Success,
    AlreadyExists,
    ConnectionFailed,
    ServerError
}

public sealed class RegisterResult
{
    public RegisterStatus Status { get; init; }
    public string Message { get; init; } = "";
    public string RecoveryCode { get; init; } = "";

    public static RegisterResult Success(string recoveryCode) => new()
    {
        Status = RegisterStatus.Success,
        Message = "注册成功。",
        RecoveryCode = recoveryCode
    };
}

public enum PasswordResetStatus
{
    Success,
    InvalidRecoveryCode,
    ConnectionFailed,
    ServerError
}

public sealed class PasswordResetResult
{
    public PasswordResetStatus Status { get; init; }
    public string Message { get; init; } = "";
    public string RecoveryCode { get; init; } = "";

    public static PasswordResetResult Success(string recoveryCode) => new()
    {
        Status = PasswordResetStatus.Success,
        Message = "密码已重置。",
        RecoveryCode = recoveryCode
    };
}
