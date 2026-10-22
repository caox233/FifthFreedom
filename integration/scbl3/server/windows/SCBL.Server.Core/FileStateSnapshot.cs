namespace Scbl.Server;

public sealed record FileStateSnapshot(string Path, byte[]? Content)
{
    public static FileStateSnapshot Capture(string path) => new(path, File.Exists(path) ? File.ReadAllBytes(path) : null);

    public void Restore()
    {
        if (Content is null)
        {
            File.Delete(Path);
            return;
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temporary = Path + ".snapshot." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
            WindowsSecurity.RestrictFile(Path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
