using System.Collections.Concurrent;

namespace Scbl.Server;

public sealed class ServerLog
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<string> _recent = new();
    private readonly string _path;

    public ServerLog(ServerPaths paths, string name = "server-host")
    {
        Directory.CreateDirectory(paths.Logs);
        _path = Path.Combine(paths.Logs, $"{name}.log");
    }

    public void Write(string component, string message)
    {
        string sanitized = message.Replace("\r", " ").Replace("\n", " ").Trim();
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{component}] {sanitized}";
        lock (_gate)
        {
            RotateIfNeeded();
            File.AppendAllText(_path, line + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        _recent.Enqueue(line);
        while (_recent.Count > 500 && _recent.TryDequeue(out _)) { }
    }

    public IReadOnlyList<string> Recent(int maximum = 200) => _recent.Reverse().Take(Math.Clamp(maximum, 1, 500)).Reverse().ToArray();

    private void RotateIfNeeded()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < 8 * 1024 * 1024) return;
        for (int index = 2; index >= 0; index--)
        {
            string source = index == 0 ? _path : _path + "." + index;
            string target = _path + "." + (index + 1);
            if (!File.Exists(source)) continue;
            File.Move(source, target, overwrite: true);
        }
    }
}
