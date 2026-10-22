using System.Text;
using System.Text.Json;

namespace SplinterCellCNLauncher.Services;

// The native writer emits fixed 4096-byte slots (latest eight observations).
// Reconstruct a typed allowlist, never export raw exception files or memory.
internal static class NativeExceptionExport
{
    internal static byte[] Read(byte[] input,DiagnosticExportSource source)
    {
        if(input.Length>32768)throw new InvalidDataException("Exception record limit exceeded.");
        string name=Path.GetFileNameWithoutExtension(source.Path);
        const string prefix="exception-observations-";
        if(!name.StartsWith(prefix,StringComparison.Ordinal)||!int.TryParse(name[prefix.Length..],out int pid)||pid<=0)throw new InvalidDataException("Invalid exception identity.");
        var records=new List<Observation>();
        for(int offset=0;offset<input.Length;offset+=4096)
        {
            int length=Math.Min(4096,input.Length-offset);
            if(length!=4096 || input[offset+4095]!=(byte)'\n'){source.InvalidValues++;continue;}
            try {
                using var doc=JsonDocument.Parse(input.AsMemory(offset,4096),new(){MaxDepth=5});var e=doc.RootElement;
                int schema=e.GetProperty("Schema").GetInt32();
                if(schema is not(1 or 2)||e.GetProperty("Pid").GetInt32()!=pid)throw new JsonException();
                uint code=e.GetProperty("Code").GetUInt32();
                if(code is not(0xc0000005 or 0xc000001d or 0xc0000094 or 0xc0000096 or 0xc0000409 or 0xc0000374))throw new JsonException();
                int access=e.GetProperty("Access").GetInt32();if(access is not(0 or 1 or 8 or 9))throw new JsonException();
                long time=e.GetProperty("UtcFileTime").GetInt64();var utc=DateTimeOffset.FromFileTime(time);
                uint sequence=e.GetProperty("Sequence").GetUInt32();
                if(sequence%8!=offset/4096)throw new JsonException();
                var raw=e.GetProperty("Frames");if(raw.ValueKind!=JsonValueKind.Array||raw.GetArrayLength()>24)throw new JsonException();
                var frames=raw.EnumerateArray().Select(Location).ToArray();
                FaultContextEvidence? context=schema==2?ReadContext(e,source):null;
                records.Add(new(sequence,pid,e.GetProperty("ThreadId").GetUInt32(),utc,code,access,e.GetProperty("NearNull").GetBoolean(),Location(e.GetProperty("Location")),frames,context));
                source.UnknownFields+=e.EnumerateObject().Count(p=>p.Name is not("Schema" or "Pid" or "ThreadId" or "UtcFileTime" or "Sequence" or "Code" or "Access" or "NearNull" or "Location" or "Frames") && (schema!=2 || p.Name is not("FramesKind" or "FaultContext")));
            } catch(Exception error) when(error is JsonException or InvalidOperationException or FormatException or ArgumentOutOfRangeException or OverflowException or KeyNotFoundException){source.InvalidValues++;}
        }
        return JsonSerializer.SerializeToUtf8Bytes(new{Schema=2,ObservationKind="FirstChanceNotCrashVerdict",StackKind="HandlerStackNotFullContextUnwind",FaultContextKind="ImageLocationsAndUnverifiedReturnAddressCandidatesOnly",DroppedOrPartialSlots=source.InvalidValues,Records=records.OrderBy(r=>r.Sequence)});
    }
    private static FaultContextEvidence ReadContext(JsonElement root,DiagnosticExportSource source)
    {
        if(root.GetProperty("FramesKind").GetString()!="observer-stack")throw new JsonException();
        var context=root.GetProperty("FaultContext");
        if(context.ValueKind!=JsonValueKind.Object)throw new JsonException();
        bool captured=context.GetProperty("Captured").GetBoolean();
        source.UnknownFields+=context.EnumerateObject().Count(p=>p.Name is not("Captured" or "Instruction" or "ReturnCandidates"));
        if(!captured)return new(false,null,[]);
        var instruction=Location(context.GetProperty("Instruction"));
        var raw=context.GetProperty("ReturnCandidates");
        if(raw.ValueKind!=JsonValueKind.Array||raw.GetArrayLength()>8)throw new JsonException();
        var candidates=new List<ReturnAddressCandidate>();
        foreach(var value in raw.EnumerateArray())
        {
            if(value.ValueKind!=JsonValueKind.Object)throw new JsonException();
            string? provenance=value.GetProperty("Source").GetString();
            if(provenance is not("stack-top-candidate" or "frame-chain-candidate"))throw new JsonException();
            var location=Location(value.GetProperty("Location"));
            if(location.SizeOfImage==0)throw new JsonException();
            source.UnknownFields+=value.EnumerateObject().Count(p=>p.Name is not("Source" or "Location"));
            candidates.Add(new(provenance,location));
        }
        return new(true,instruction,candidates.ToArray());
    }
    private static ImageLocation Location(JsonElement e)
    {
        if(e.ValueKind!=JsonValueKind.Array||e.GetArrayLength()!=3)throw new JsonException();
        uint t=e[0].GetUInt32(),size=e[1].GetUInt32(),rva=e[2].GetUInt32();
        if(size==0 && (t!=0||rva!=0) || size!=0 && rva>=size)throw new JsonException();
        return new(t,size,rva);
    }
    private sealed record ImageLocation(uint TimeDateStamp,uint SizeOfImage,uint Rva);
    private sealed record Observation(uint Sequence,int Pid,uint ThreadId,DateTimeOffset Utc,uint Code,int Access,bool NearNull,ImageLocation Location,ImageLocation[] Frames,FaultContextEvidence? FaultContext);
    private sealed record FaultContextEvidence(bool Captured,ImageLocation? Instruction,ReturnAddressCandidate[] ReturnCandidates);
    private sealed record ReturnAddressCandidate(string Source,ImageLocation Location);
}
