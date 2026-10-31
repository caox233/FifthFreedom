using System.Text;
using System.Text.Json;
using SplinterCellCNLauncher.Services;

var cases = new (string Name, Action<Fixture> Check)[]
{
    ("utf8-crosses-real-read-boundary", f =>
    {
        // The first Chinese character straddles the production 16 KiB read.
        string text = new string('x', 16383) + "中文🙂\n";
        f.Copy(Bytes(text));
        Require(f.ReadSizes.SequenceEqual(new[] { 16384, Bytes(text).Length - 16384 }), "read boundary");
        Require(f.Text == text && f.Redactions == 0 && f.Lines == 1, "unaltered UTF8");
    }),
    ("json-secret-crosses-read-boundary", f =>
    {
        const string secret = "SYNTHETIC_TOKEN_MUST_NOT_LEAVE";
        string prefix = "{\"padding\":\"";
        const string delimiter = "\",\"token\":\"";
        string body = prefix + new string('x', 16380 - prefix.Length - delimiter.Length) + delimiter + secret + "\",\"safe\":\"中文\"}\r\n";
        Require(body.IndexOf(secret, StringComparison.Ordinal) == 16380, "secret crosses boundary");
        f.Copy(Bytes(body));
        using var doc = JsonDocument.Parse(f.Output.ToArray());
        Require(doc.RootElement.GetProperty("token").GetString() == "[REDACTED]", "secret redacted");
        Require(doc.RootElement.GetProperty("safe").GetString() == "中文", "safe text retained");
        Require(!f.Text.Contains(secret, StringComparison.Ordinal) && f.Redactions == 1 && f.Lines == 1, "redaction count");
        Require(f.Text.EndsWith("\r\n", StringComparison.Ordinal) && f.ReadSizes.Count == 2, "CRLF and reads");
    }),
    ("crlf-split-and-no-final-newline", f =>
    {
        string text = new string('x', 16383) + "\r\n" + "末尾🙂";
        f.Copy(Bytes(text));
        Require(f.Text == text && f.Lines == 2 && f.Redactions == 0, "line endings retained");
        Require(f.ReadSizes[0] == 16384, "CRLF boundary");
    }),
    ("server-dump-after-chunk-boundary", f =>
    {
        string first = new string('x', 16380) + "\n";
        f.Copy(Bytes(first + "[0, a]\r\n[ff, 1]"), server: true);
        Require(f.Text == first + "[REDACTED_SERVER_BYTE_DUMP]\r\n[REDACTED_SERVER_BYTE_DUMP]", "dump removed");
        Require(f.Lines == 3 && f.Redactions == 2, "dump accounting");
    }),
    ("line-limit-includes-newline", f =>
    {
        string exact = new string('x', 16383) + "\n";
        f.Copy(Bytes(exact), maxLine: 16384);
        Require(f.Text == exact && f.Lines == 1, "exact line budget accepted");
        f.Reset();
        Reject<InvalidDataException>(() => f.Copy(Bytes(new string('x', 16384) + "\n"), maxLine: 16384));
        Require(f.Output.Length == 0 && f.Lines == 0, "over-limit line not emitted");
    }),
    ("invalid-utf8-straddles-read", f =>
    {
        byte[] bytes = Bytes(new string('x', 16383)).Concat(new byte[] { 0xe4, 0x20, 0x0a }).ToArray();
        Reject<InvalidDataException>(() => f.Copy(bytes));
        Require(f.Output.Length == 0 && f.Lines == 0 && f.ReadSizes.Count == 2, "UTF8 rejected before output");
    }),
    ("truncated-snapshot-does-not-succeed", f =>
    {
        byte[] bytes = Bytes("ordinary prefix\npartial");
        Reject<IOException>(() => f.Copy(bytes, length: bytes.Length + 9));
        Require(f.Text == "ordinary prefix\n" && f.Lines == 1, "only earlier complete line emitted");
        // This is a streaming failure, not an assertion of ZIP rollback.
    }),
    ("snapshot-length-does-not-read-appended-tail", f =>
    {
        byte[] prefix = Bytes("snapshot末尾");
        f.Copy(prefix.Concat(Bytes("\nUNSELECTED_SYNTHETIC_TAIL")).ToArray(), length: prefix.Length);
        Require(f.Text == "snapshot末尾" && f.ReadSizes.Sum() == prefix.Length && f.Lines == 1, "exact snapshot extent");
    }),
    ("malformed-final-json-remains-fatal", f =>
    {
        Reject<InvalidDataException>(() => f.Copy(Bytes("safe\n{\"token\":\"SYNTHETIC_TORN")));
        Require(f.Text == "safe\n" && f.Lines == 1 && f.Redactions == 0, "no malformed final output");
    }),
    ("json-depth-and-node-limits-remain-fatal", f =>
    {
        Reject<InvalidDataException>(() => f.Copy(Bytes("{\"child\":{\"value\":1}}\n"), maxDepth: 1));
        Require(f.Output.Length == 0, "depth budget");
        f.Reset();
        Reject<InvalidDataException>(() => f.Copy(Bytes("{\"a\":1,\"b\":2}\n"), maxNodes: 2));
        Require(f.Output.Length == 0, "node budget");
    }),
    ("cancellation-before-read-and-before-line", f =>
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Reject<OperationCanceledException>(() => f.Copy(Bytes("safe\n"), token: stop.Token));
        Require(f.ReadSizes.Count == 0 && f.Output.Length == 0, "no initial read");
        using var afterRead = new CancellationTokenSource();
        Reject<OperationCanceledException>(() => f.Copy(Bytes("safe\n"), token: afterRead.Token, onRead: afterRead.Cancel));
        Require(f.ReadSizes.Count == 1 && f.Output.Length == 0 && f.Lines == 0, "no cancelled line");
    }),
    ("output-failure-stops-before-next-read", f =>
    {
        // The caller's output exception must escape; no second chunk is read.
        Reject<IOException>(() => f.Copy(Bytes("safe\n" + new string('x', 32768)), failOutput: true));
        Require(f.ReadSizes.SequenceEqual(new[] { 16384 }) && f.Output.Length == 0, "output failure propagated");
    }),
};

foreach (var (name, check) in cases)
{
    using var fixture = new Fixture();
    check(fixture);
    Console.WriteLine("DIAGNOSTIC_STREAM_PASS " + name);
}
Console.WriteLine($"DIAGNOSTIC_STREAM_SUMMARY cases={cases.Length} linked_production=true source_unchanged=true full_zip_tested=false network=false game=false");

static byte[] Bytes(string text) => new UTF8Encoding(false, true).GetBytes(text);
static void Require(bool condition, string code) { if (!condition) throw new Exception("DIAGNOSTIC_STREAM_ASSERT " + code); }
static void Reject<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("DIAGNOSTIC_STREAM_EXPECTED " + typeof(T).Name);
}

sealed class Fixture : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ff-diagnostic-stream-" + Guid.NewGuid().ToString("N"));
    internal readonly MemoryStream Output = new();
    internal readonly List<int> ReadSizes = [];
    internal int Redactions, Lines;
    internal string Text => new UTF8Encoding(false, true).GetString(Output.ToArray());
    internal Fixture() => Directory.CreateDirectory(directory);
    internal void Reset() { Output.SetLength(0); ReadSizes.Clear(); Redactions = 0; Lines = 0; }
    internal void Copy(byte[] bytes, bool server = false, int maxLine = 65536, long? length = null,
        int maxDepth = 16, int maxNodes = 32768, CancellationToken token = default, Action? onRead = null, bool failOutput = false)
    {
        string path = Path.Combine(directory, "synthetic.log");
        File.WriteAllBytes(path, bytes);
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
            DiagnosticTextStream.CopyLines(input, length ?? bytes.Length, maxLine, token, token.ThrowIfCancellationRequested,
                read => { ReadSizes.Add(read); onRead?.Invoke(); }, line =>
                {
                    byte[] sanitized = DiagnosticTextStream.Line(line.Span, maxDepth, maxNodes, token.ThrowIfCancellationRequested, ref Redactions, server);
                    token.ThrowIfCancellationRequested();
                    if (failOutput) throw new IOException("SYNTHETIC_OUTPUT_FAILURE");
                    Output.Write(sanitized); Lines++;
                });
        }
        finally
        {
            if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new Exception("DIAGNOSTIC_STREAM_SOURCE_CHANGED");
        }
    }
    public void Dispose() { Output.Dispose(); Directory.Delete(directory, true); }
}
