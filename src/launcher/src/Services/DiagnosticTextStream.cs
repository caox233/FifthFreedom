using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;

namespace SplinterCellCNLauncher.Services;

// Retains every line, not just recognized events. Known-pattern minimization, NOT a universal secret detector.
internal static class DiagnosticTextStream
{
    private static readonly UTF8Encoding Utf8=new(false,true);
    private static Regex Rule(string pattern)=>new(pattern,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.NonBacktracking,TimeSpan.FromMilliseconds(50));
    private static readonly Regex Assignment=Rule("\\b(?:password|passwd|token|authorization|cookie|access_key|crypto_key|ticket_key|username|account_id|accountid|user_id|userid|cdkeys|signature|reset_code|recovery_code)\\\"?\\s*[:=]\\s*(?:\\\"(?:\\\\.|[^\\\"\\\\])*\\\"|'[^']*'|[^,;\\r\\n]+)");
    private static readonly Regex Bearer=Rule(@"\bBearer\s+[A-Za-z0-9._~+/-]+=*");
    private static readonly Regex UriCredentials=Rule(@"https?://[^\s/@]+:[^\s/@]+@[^\s]+" );
    private static readonly Regex Paths=Rule(@"(?:[A-Za-z]:[\\/]|\\\\)[^\r\n\""<>|]*");
    private static readonly Regex Email=Rule(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b");
    private static readonly Regex IdentityMessage=Rule(@"(?:Login successful for|New user|Password reset completed for|Verify plain password of|Verify password hash of)\s+[^\r\n]*");
    private static readonly HashSet<string> SecretKeys=new(StringComparer.OrdinalIgnoreCase){"password","passwd","token","authorization","cookie","access_key","crypto_key","ticket_key","username","account_id","accountid","user_id","userid","cdkeys","signature","reset_code","recovery_code"};
    // The reviewed server's NAT probe logger writes Rust {payload:x?}: this is a
    // byte dump, not JSON, and it can contain network payloads. Never export it raw.
    private static readonly Regex ServerByteDump=Rule(@"\[[0-9a-f]{1,2}(?:,\s*[0-9a-f]{1,2})*\]");
    // A torn server byte array can start with a-f, which is not a JSON start.
    // Route only a byte-array prefix through strict parsing; never scrub it as plain text.
    private static readonly Regex ServerByteDumpPrefix=Rule(@"\A\[[0-9a-f]{1,2}(?:\s*,|\s*\z)");
    internal static string Scrub(string text,ref int count,bool serverText=false)
    {
        if(serverText){int n=0;text=ServerByteDump.Replace(text,_=>{n++;return "[REDACTED_SERVER_BYTE_DUMP]";});count=checked(count+n);}
        foreach(var rule in new[]{Assignment,Bearer,UriCredentials,Paths,Email,IdentityMessage})
        {int n=0;text=rule.Replace(text,_=>{n++;return "[REDACTED]";});count=checked(count+n);}
        return text;
    }
    internal static byte[] Line(ReadOnlySpan<byte> bytes,int maxJsonDepth,int maxJsonNodes,Action check,ref int redactions,bool serverText=false)
    {
        check();string original;try{original=Utf8.GetString(bytes);}catch(DecoderFallbackException){throw new InvalidDataException("Unsupported UTF-8 diagnostic text; no archive published.");}
        string end=original.EndsWith("\r\n",StringComparison.Ordinal)?"\r\n":original.EndsWith('\n')?"\n":"";
        string body=end.Length==0?original:original[..^end.Length];int count=0;
        string trimmed=body.TrimStart('\uFEFF',' ','\t');
        if(serverText)
        {
            var dump=ServerByteDump.Match(trimmed.TrimEnd());
            if(dump.Success&&dump.Index==0&&dump.Length==trimmed.TrimEnd().Length)
            {redactions=checked(redactions+1);return Utf8.GetBytes("[REDACTED_SERVER_BYTE_DUMP]"+end);}
        }
        bool launcherPrefix=trimmed.Length>=21&&trimmed[0]=='['&&trimmed[20]==']'&&DateTime.TryParseExact(trimmed.Substring(1,19),"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out _);
        string arrayStart=trimmed.StartsWith('[')?trimmed[1..].TrimStart():"";
        bool arrayJson=!launcherPrefix&&arrayStart.Length>0&&("{[\"0123456789-tfn]".Contains(arrayStart[0]));
        if(trimmed.StartsWith('{')||arrayJson||(serverText&&ServerByteDumpPrefix.IsMatch(trimmed)))
        {
            try
            {
                using var doc=JsonDocument.Parse(body.TrimStart('\uFEFF'),new(){MaxDepth=maxJsonDepth});
                using var output=new MemoryStream();using(var writer=new Utf8JsonWriter(output))
                {int nodes=0;Write(doc.RootElement,writer,maxJsonNodes,check,ref nodes,ref count,serverText);}
                if(count>0)body=Utf8.GetString(output.ToArray());
            }
            catch(JsonException){throw new InvalidDataException("Unsupported JSON log line or depth limit; no archive published.");}
        }
        else body=Scrub(body,ref count,serverText);
        redactions=checked(redactions+count);return Utf8.GetBytes(body+end);
    }
    private static void Write(JsonElement e,Utf8JsonWriter writer,int maxJsonNodes,Action check,ref int nodes,ref int count,bool serverText)
    {
        check();if(++nodes>maxJsonNodes)throw new InvalidDataException("Diagnostic JSON node limit exceeded.");
        switch(e.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();foreach(var p in e.EnumerateObject())
                {writer.WritePropertyName(p.Name);if(SecretKeys.Contains(p.Name)){writer.WriteStringValue("[REDACTED]");count++;}else Write(p.Value,writer,maxJsonNodes,check,ref nodes,ref count,serverText);}writer.WriteEndObject();break;
            case JsonValueKind.Array:
                writer.WriteStartArray();foreach(var item in e.EnumerateArray())Write(item,writer,maxJsonNodes,check,ref nodes,ref count,serverText);writer.WriteEndArray();break;
            case JsonValueKind.String:writer.WriteStringValue(Scrub(e.GetString()!,ref count,serverText));break;
            default:e.WriteTo(writer);break;
        }
    }
    // The callback sees this line only until it returns; the bounded buffer is reused.
    // Callers own source accounting, projection, output budgets and atomic publication.
    internal static void CopyLines(FileStream input,long length,int maxLineBytes,CancellationToken token,
        Action check,Action<int> onRead,Action<ReadOnlyMemory<byte>> onLine)
    {
        byte[] chunk=new byte[16384],line=new byte[maxLineBytes];int pending=0;long left=length;
        while(left>0)
        {
            check();int read=input.ReadAsync(chunk.AsMemory(0,(int)Math.Min(left,chunk.Length)),token).AsTask().GetAwaiter().GetResult();
            if(read==0)throw new IOException("Diagnostic source truncated while reading.");onRead(read);left-=read;
            for(int i=0;i<read;i++)
            {
                if(pending==line.Length)throw new InvalidDataException("Diagnostic line limit exceeded; no archive published.");
                line[pending++]=chunk[i];if(chunk[i]=='\n'){Emit();}
            }
        }
        if(pending>0)Emit();
        void Emit(){onLine(line.AsMemory(0,pending));pending=0;}
    }
}
