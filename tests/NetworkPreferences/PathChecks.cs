using SplinterCellCNLauncher.Services;

internal static class PathChecks
{
    internal static void Run(Action<bool, string> check)
    {
        // Every unsafe Windows example is a string only. Invalid grammar must
        // be rejected even before the injected drive-metadata callback runs.
        string?[] rejected = [null, "", " ", @"\\example.invalid\share\profile.json",
            @"\\?\UNC\example.invalid\share\profile.json", @"\\?\C:\profile.json", @"\\.\C:\profile.json",
            @"\??\C:\profile.json", "//example.invalid/share/profile.json", @"\profile.json",
            @"C:profile.json", @"1:\profile.json", "C:/profile.json", @"C:\profile.json:secret",
            @"C:\profile.json::$DATA", @"C:\runtime:stream\profile.json", @"C:\.\profile.json",
            @"C:\runtime\..\profile.json", @"C:\runtime\\profile.json", @"C:\runtime\",
            @"C:\runtime.\profile.json", @"C:\runtime \profile.json", "C:\\profile\0.json",
            @"C:\profile*.json", @"C:\profile?.json", @"C:\profile|name.json", "C:\\profile\".json",
            @"C:\profile<name>.json", @"C:\NUL", @"C:\con.json", @"C:\CON .json", @"C:\aux\profile.json",
            @"C:\COM1.json", @"C:\LPT9\profile.json", @"C:\COM¹.json", @"C:\CONIN$", @"C:\CONOUT$"];
        for (int i = 0; i < rejected.Length; i++)
            check(!PublicLocalPath.IsAllowed(rejected[i], true, _ => throw new InvalidOperationException("unexpected metadata query")),
                "nonlocal, device or noncanonical Windows path rejected before drive lookup " + i);

        foreach (var type in new[] { DriveType.Network, DriveType.Unknown, DriveType.NoRootDirectory, (DriveType)999 })
        {
            var roots = new List<string>();
            check(!PublicLocalPath.IsAllowed(@"Z:\runtime\profile.json", true, root => { roots.Add(root); return type; })
                && roots.SequenceEqual(new[] { @"Z:\" }), "nonlocal or unclassified drive rejected using root metadata only: " + type);
        }
        foreach (var type in new[] { DriveType.Fixed, DriveType.Removable, DriveType.CDRom, DriveType.Ram })
        {
            var roots = new List<string>();
            check(PublicLocalPath.IsAllowed(@"C:\owned fixture\配置.json", true, root => { roots.Add(root); return type; })
                && roots.SequenceEqual(new[] { @"C:\" }), "ordinary local Unicode path admitted from local drive metadata: " + type);
        }
        check(PublicLocalPath.IsAllowed(@"c:\", true, _ => DriveType.Fixed), "ordinary local drive root retains its canonical spelling");
        foreach (Exception error in new Exception[] { new IOException(), new UnauthorizedAccessException(), new ArgumentException(), new System.Security.SecurityException() })
            check(!PublicLocalPath.IsAllowed(@"Z:\profile.json", true, _ => throw error), "drive metadata failure rejects input: " + error.GetType().Name);
        foreach (string path in new[] { "/", "/tmp/owned fixture/配置.json" })
            check(PublicLocalPath.IsAllowed(path, false, _ => throw new InvalidOperationException("unexpected drive lookup")), "Unix absolute fixture grammar remains available without Windows drive queries");
        foreach (string path in new[] { "//example.invalid/share/profile.json", "relative/file", "/tmp/../file", "/tmp/./file", "/tmp//file", "/tmp/", "/tmp/\\device", "/tmp/file\0.json" })
            check(!PublicLocalPath.IsAllowed(path, false, _ => throw new InvalidOperationException("unexpected drive lookup")), "noncanonical Unix fixture input rejected without target I/O");

        foreach (bool windows in new[] { false, true })
        {
            string[] chain = windows ? [@"C:\", @"C:\owned", @"C:\owned\link", @"C:\owned\link\profile.json"]
                : ["/", "/owned", "/owned/link", "/owned/link/profile.json"];
            var visited = new List<string>();
            PublicLocalPath.RequirePlain(chain[^1], windows, _ => DriveType.Fixed,
                item => { visited.Add(item); return item == chain[^1] ? FileAttributes.Normal : FileAttributes.Directory; });
            check(visited.SequenceEqual(chain), "plain metadata is checked root to leaf before a descendant: " + windows);
            for (int stop = 0; stop < chain.Length; stop++)
            {
                visited.Clear();
                bool linkRejected = false;
                try
                {
                    PublicLocalPath.RequirePlain(chain[^1], windows, _ => DriveType.Fixed, item =>
                    {
                        if (!chain.Take(stop + 1).Contains(item)) throw new InvalidOperationException("descendant metadata reached after reparse point");
                        visited.Add(item);
                        return FileAttributes.Directory | (item == chain[stop] ? FileAttributes.ReparsePoint : 0);
                    });
                }
                catch (IOException e) { linkRejected = e.Message == "PUBLIC_DEPLOYMENT_CONFIGURATION_INVALID"; }
                check(linkRejected && visited.SequenceEqual(chain.Take(stop + 1)), "reparse ancestor or leaf stops later metadata: " + windows + "/" + stop);
            }
            visited.Clear();
            bool nonDirectoryRejected = false;
            try { PublicLocalPath.RequirePlain(chain[^1], windows, _ => DriveType.Fixed, item => { visited.Add(item); return FileAttributes.Normal; }); }
            catch (IOException) { nonDirectoryRejected = true; }
            check(nonDirectoryRejected && visited.SequenceEqual(chain.Take(1)), "non-directory ancestor rejects before descendant metadata: " + windows);
        }
        bool invalidRejected = false;
        try
        {
            PublicLocalPath.RequirePlain(@"\\example.invalid\share\profile.json", true,
                _ => throw new InvalidOperationException("remote metadata query"),
                _ => throw new InvalidOperationException("remote attribute query"));
        }
        catch (IOException) { invalidRejected = true; }
        check(invalidRejected, "plain path walk rejects UNC before any injected metadata query");
    }
}
