using System.Text;

namespace SplinterCellCNLauncher.Services;

// Retained export integration: the shared stream path performs the same text
// parsing on Windows and in portable regressions. DiagnosticBundleService still
// owns Windows file identity checks and atomic ZIP publication.
internal static class DiagnosticTextSanitizer
{
    private static readonly UTF8Encoding Utf8=new(false,true);
    internal static string Scrub(string text,ref int count,bool serverText=false)
        =>DiagnosticTextStream.Scrub(text,ref count,serverText);
    internal static byte[] Line(ReadOnlySpan<byte> bytes,DiagnosticExportContext context,ref int redactions,bool serverText=false)
        =>DiagnosticTextStream.Line(bytes,context.Budget.MaxJsonDepth,context.Budget.MaxJsonNodes,context.Check,ref redactions,serverText);
    internal static void Copy(FileStream input,Stream output,long length,DiagnosticExportSource source,DiagnosticExportContext context)
    {
        int sourceRedactions=source.Redactions;
        var projected=new Queue<DiagnosticSourceProjection.Event>();
        DiagnosticTextStream.CopyLines(input,length,context.Budget.MaxLineBytes,context.Token,context.Check,
            read=>{context.Input(read);source.ReadBytes+=read;},Emit);
        context.Projection(source,projected);
        void Emit(ReadOnlyMemory<byte> line)
        {
            byte[] sanitized=Line(line.Span,context,ref sourceRedactions,source.Id.StartsWith("host:",StringComparison.Ordinal));
            string original=Utf8.GetString(line.Span).TrimEnd('\r','\n');
            if(original.Length<=8192)
            {
                source.UnknownFields+=DiagnosticSourceProjection.CountUnknownFields(original);
                var e=DiagnosticSourceProjection.Project(source.Id,original);
                if(e==null)source.Dropped++;
                else{source.EventsRecognized++;projected.Enqueue(e);if(projected.Count>256){projected.Dequeue();source.Dropped++;}}
            }
            else source.Dropped++;
            context.Check();source.Redactions=sourceRedactions;context.Output(sanitized.Length);output.WriteAsync(sanitized,context.Token).AsTask().GetAwaiter().GetResult();source.ExportBytes+=sanitized.Length;source.Lines++;
        }
    }
}
