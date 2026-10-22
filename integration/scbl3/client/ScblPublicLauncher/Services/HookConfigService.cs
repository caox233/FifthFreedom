using System.Text;

using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Globalization;
using SplinterCellCNLauncher.Models;
namespace SplinterCellCNLauncher.Services;

public sealed class HookConfigService
{
    private readonly Func<string> _getConfigServerHost;
    private readonly Func<string> _getApiServer;

    public HookConfigService(
        Func<string>? getConfigServerHost = null,
        Func<string>? getApiServer = null)
    {
        _getConfigServerHost = getConfigServerHost ?? (() => AuthService.PublicConfigServerHost);
        _getApiServer = getApiServer ?? (() => AuthService.PublicGrpcAddress);
    }

    public HookConfigWriteResult WriteAuthFile(
        string gameDir,
        string username,
        string password,
        string accountId,
        string bindIp,
        GameplayMode gameplayMode,
        double modFovScale = LauncherSettingsService.DefaultModFovScale)
    {
        modFovScale = LauncherSettingsService.NormalizeModFovScale(modFovScale);
        string configServer = _getConfigServerHost();
        string apiServer = _getApiServer().TrimEnd('/') + "/";

        string content =
$@"ConfigServer = ""{TomlEscape(configServer)}""
ApiServer = ""{TomlEscape(apiServer)}""
AutoJoinInvite = false
EnableOverlay = true
GameplayMode = ""{GameplayModeNames.ToName(gameplayMode)}""

[User]
Username = ""{TomlEscape(username)}""
Password = ""{TomlEscape(password)}""
AccountId = ""{TomlEscape(accountId)}""

[Networking]
IpAddress = ""{TomlEscape(bindIp)}""

[Logging]
Level = ""INFO""

[Features]
FovFactor = {modFovScale.ToString("0.0##", CultureInfo.InvariantCulture)}
";

        string path = Path.Combine(gameDir, "scbl.toml");
        File.WriteAllText(path, content.Trim(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        string sha256;
        using (FileStream stream = File.OpenRead(path))
            sha256 = Convert.ToHexString(SHA256.HashData(stream));

        string retiredPath = Path.Combine(gameDir, "5th_auth.dat");
        if (File.Exists(retiredPath))
        {
            try
            {
                File.Delete(retiredPath);
                LogService.Info($"已删除停用的旧 Hooks 配置：{retiredPath}");
            }
            catch (Exception ex)
            {
                // The retired file is never read. A stale antivirus/file-system lock
                // must not prevent the new scbl.toml from launching the game.
                LogService.Warning($"无法删除已停用的旧 Hooks 配置（不会读取）：{retiredPath}, {ex.Message}");
            }
        }

        LogService.Info($"已写入 SCBL Hooks TOML 配置：{path}");
        return new HookConfigWriteResult(path, sha256);
    }

    public void ValidateWrittenFileOrThrow(
        string gameDir,
        string username,
        string accountId,
        string bindIp,
        GameplayMode gameplayMode,
        double modFovScale = LauncherSettingsService.DefaultModFovScale)
    {
        string path = Path.Combine(gameDir, "scbl.toml");
        if (!File.Exists(path))
            throw new InvalidOperationException($"scbl.toml 写入失败：文件不存在。\n{path}");

        Dictionary<string, string> values = ReadValues(path);
        var errors = new List<string>();
        CheckValue("User.Username", username);
        CheckValue("User.AccountId", accountId);
        CheckValue("ConfigServer", _getConfigServerHost());
        CheckValue("ApiServer", _getApiServer().TrimEnd('/') + "/");
        CheckValue("Networking.IpAddress", bindIp);
        CheckExactValue("GameplayMode", GameplayModeNames.ToName(gameplayMode));
        CheckValue(
            "Features.FovFactor",
            LauncherSettingsService.NormalizeModFovScale(modFovScale)
                .ToString("0.0##", CultureInfo.InvariantCulture));

        void CheckValue(string key, string expected)
        {
            if (!values.TryGetValue(key, out string? actual))
            {
                errors.Add($"缺少字段：{key}");
                return;
            }

            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{key} 不一致：实际 [{actual}]，应为 [{expected}]");
        }

        void CheckExactValue(string key, string expected)
        {
            if (!values.TryGetValue(key, out string? actual))
            {
                errors.Add($"缺少字段：{key}");
                return;
            }

            if (!actual.Equals(expected, StringComparison.Ordinal))
                errors.Add($"{key} 不一致：实际 [{actual}]，应为 [{expected}]");
        }

        if (errors.Count > 0)
            throw new InvalidOperationException("scbl.toml 写入后校验失败，已取消启动。\n\n" + string.Join("\n", errors));

        LogService.Info($"scbl.toml read-back validation succeeded: {path}");
    }

    private static Dictionary<string, string> ReadValues(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        foreach (string rawLine in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            Match sectionMatch = Regex.Match(line, "^\\[([A-Za-z0-9_]+)\\]$");
            if (sectionMatch.Success)
            {
                section = sectionMatch.Groups[1].Value;
                continue;
            }

            Match match = Regex.Match(line, "^([A-Za-z0-9_]+)\\s*=\\s*(.+?)\\s*$");
            if (!match.Success)
                continue;

            string key = string.IsNullOrWhiteSpace(section)
                ? match.Groups[1].Value
                : section + "." + match.Groups[1].Value;
            string rawValue = match.Groups[2].Value;
            values[key] = rawValue.Length >= 2
                && rawValue[0] == '"'
                && rawValue[^1] == '"'
                    ? TomlUnescape(rawValue[1..^1])
                    : rawValue;
        }
        return values;
    }

    private static string TomlUnescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        bool escape = false;
        foreach (char ch in value)
        {
            if (!escape)
            {
                if (ch == '\\')
                    escape = true;
                else
                    sb.Append(ch);
                continue;
            }

            sb.Append(ch switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' => '\\',
                '"' => '"',
                _ => ch
            });
            escape = false;
        }
        if (escape)
            sb.Append('\\');
        return sb.ToString();
    }

    private static string TomlEscape(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

}

public sealed record HookConfigWriteResult(string Path, string Sha256);
