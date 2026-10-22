using System.Security.Cryptography;
using SplinterCellCNLauncher.Services;
using Xunit;

namespace SCBL.Client.Tests;

public sealed class InstalledComponentReceiptServiceTests
{
    [Fact]
    public void ChannelReceiptsRemainIndependentAndExactLocalContentCanBeAdopted()
    {
        string root = CreateRoot();
        try
        {
            string receiptPath = Path.Combine(root, "installed_components.json");
            string installedPath = Path.Combine(root, "uplay_r1_loader.dll");
            File.WriteAllBytes(installedPath, [1, 2, 3, 4, 5]);
            string fileSha = ComputeSha256(installedPath);
            const long artifactSize = 5;

            using (InstalledComponentReceiptService.UseReceiptPathForTest(receiptPath))
            {
                InstalledComponentReceiptService.MarkApplied(
                    "stable",
                    "hooks",
                    "2.0.110",
                    fileSha,
                    artifactSize,
                    new Dictionary<string, string> { [installedPath] = fileSha });

                Assert.Contains("hooks", InstalledComponentReceiptService.Read("stable"));
                Assert.Empty(InstalledComponentReceiptService.Read("test"));

                Assert.True(InstalledComponentReceiptService.TryAdoptInstalledFact(
                    "test",
                    "hooks",
                    "2.0.110",
                    fileSha,
                    artifactSize));
                Assert.Contains("hooks", InstalledComponentReceiptService.Read("stable"));
                Assert.Contains("hooks", InstalledComponentReceiptService.Read("test"));

                Assert.False(InstalledComponentReceiptService.TryAdoptInstalledFact(
                    "test",
                    "hooks",
                    "2.0.111",
                    fileSha,
                    artifactSize));
                Assert.False(InstalledComponentReceiptService.TryAdoptInstalledFact(
                    "test",
                    "hooks",
                    "2.0.110",
                    fileSha,
                    artifactSize + 1));
                Assert.Contains("hooks", InstalledComponentReceiptService.Read("stable"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Unsupported_old_receipt_schema_is_ignored()
    {
        string root = CreateRoot();
        try
        {
            string receiptPath = Path.Combine(root, "installed_components.json");
            string oldDocument = """
                {"SchemaVersion":3,"Channel":"stable","Components":{"hooks":{"Version":"2.0.110","Sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Files":{"tools/uplay_r1_loader.dll":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}}}
                """;
            File.WriteAllText(receiptPath, oldDocument);

            using (InstalledComponentReceiptService.UseReceiptPathForTest(receiptPath))
            {
                Assert.Empty(InstalledComponentReceiptService.Read("stable"));
                Assert.Empty(InstalledComponentReceiptService.Read("test"));
            }

            Assert.Equal(oldDocument, File.ReadAllText(receiptPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingOrChangedInstalledFileCannotCreateTargetChannelReceipt()
    {
        string root = CreateRoot();
        try
        {
            string receiptPath = Path.Combine(root, "installed_components.json");
            string installedPath = Path.Combine(root, "uplay_r1_loader.dll");
            File.WriteAllBytes(installedPath, [1, 3, 3, 7]);
            string fileSha = ComputeSha256(installedPath);

            using (InstalledComponentReceiptService.UseReceiptPathForTest(receiptPath))
            {
                InstalledComponentReceiptService.MarkApplied(
                    "stable",
                    "hooks",
                    "2.0.110",
                    fileSha,
                    4,
                    new Dictionary<string, string> { [installedPath] = fileSha });
                File.WriteAllBytes(installedPath, [0, 0, 0, 0]);

                Assert.False(InstalledComponentReceiptService.TryAdoptInstalledFact(
                    "test",
                    "hooks",
                    "2.0.110",
                    fileSha,
                    4));
                Assert.Empty(InstalledComponentReceiptService.Read("test"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StableTestStableRoundTripKeepsSixIndependentReceipts()
    {
        string root = CreateRoot();
        try
        {
            string receiptPath = Path.Combine(root, "installed_components.json");
            string[] components = ["easytier", "hooks", "launcher", "route-guard", "updater", "client-files"];
            using (InstalledComponentReceiptService.UseReceiptPathForTest(receiptPath))
            {
                foreach ((string component, int index) in components.Select((value, index) => (value, index)))
                {
                    string path = Path.Combine(root, component + ".bin");
                    File.WriteAllBytes(path, [(byte)(index + 1), (byte)(index + 11)]);
                    string sha = ComputeSha256(path);
                    InstalledComponentReceiptService.MarkApplied(
                        "stable",
                        component,
                        $"2.0.{index}",
                        sha,
                        2,
                        new Dictionary<string, string> { [path] = sha });
                    Assert.True(InstalledComponentReceiptService.TryAdoptInstalledFact(
                        "test",
                        component,
                        $"2.0.{index}",
                        sha,
                        2));
                }

                Assert.Equal(6, InstalledComponentReceiptService.Read("stable").Count);
                Assert.Equal(6, InstalledComponentReceiptService.Read("test").Count);
                Assert.Equal(6, InstalledComponentReceiptService.Read("stable").Count);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompleteTemporarySchema4ReceiptIsRecoveredWhenPrimaryIsMissing()
    {
        string root = CreateRoot();
        try
        {
            string receiptPath = Path.Combine(root, "installed_components.json");
            string installedPath = Path.Combine(root, "SCBL.exe");
            File.WriteAllBytes(installedPath, [4, 3, 2, 1]);
            string sha = ComputeSha256(installedPath);
            using (InstalledComponentReceiptService.UseReceiptPathForTest(receiptPath))
            {
                InstalledComponentReceiptService.MarkApplied(
                    "stable",
                    "launcher",
                    "2.2.2",
                    sha,
                    4,
                    new Dictionary<string, string> { [installedPath] = sha });
                File.Move(receiptPath, receiptPath + ".tmp", overwrite: true);

                Assert.Contains("launcher", InstalledComponentReceiptService.Read("stable"));
                Assert.True(File.Exists(receiptPath));
                Assert.False(File.Exists(receiptPath + ".tmp"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(
            AppContext.BaseDirectory,
            "receipt-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
