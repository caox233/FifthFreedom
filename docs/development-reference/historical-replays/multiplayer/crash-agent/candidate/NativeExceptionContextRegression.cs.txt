using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using SplinterCellCNLauncher.Services;

internal static class NativeExceptionContextRegression
{
    internal static void Run(string root)
    {
        _=root;
        var assembly=typeof(DiagnosticBundleService).Assembly;
        var parser=assembly.GetType("SplinterCellCNLauncher.Services.NativeExceptionExport",true)!;
        var sourceType=assembly.GetType("SplinterCellCNLauncher.Services.DiagnosticExportSource",true)!;
        var read=parser.GetMethod("Read",BindingFlags.Static|BindingFlags.NonPublic)!;
        int checks=0;
        JsonObject Basic(int schema)=>new(){["Schema"]=schema,["Pid"]=7777,["ThreadId"]=11,["UtcFileTime"]=134371540869097288L,["Sequence"]=0,["Code"]=0xc0000005u,["Access"]=8,["NearNull"]=true,["Location"]=new JsonArray(0,0,0),["Frames"]=new JsonArray(new JsonArray(42,8192,128))};
        JsonObject Candidate()=>new(){["Source"]="stack-top-candidate",["Location"]=new JsonArray(42,8192,256)};
        JsonObject V2(){var r=Basic(2);r["FramesKind"]="observer-stack";r["FaultContext"]=new JsonObject{["Captured"]=true,["Instruction"]=new JsonArray(0,0,0),["ReturnCandidates"]=new JsonArray(Candidate())};return r;}
        JsonObject Export(JsonObject record,bool torn=false)
        {
            string text=record.ToJsonString();if(Encoding.UTF8.GetByteCount(text)>4095)throw new Exception("Fixture slot overflow");
            byte[] slot=Enumerable.Repeat((byte)' ',4096).ToArray();Encoding.UTF8.GetBytes(text).CopyTo(slot,0);slot[^1]=torn?(byte)0:(byte)'\n';
            object source=Activator.CreateInstance(sourceType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object?[]{"fixture","exception-observations-7777.jsonl","fixture.json","exceptions","fixture"},null)!;
            var output=(byte[])read.Invoke(null,new[]{(object)slot,source})!;
            string exported=Encoding.UTF8.GetString(output);
            if(exported.Contains("DO_NOT_EXPORT")||exported.Contains("ArbitraryPrivateField"))throw new Exception("Context export leaked an unknown field");
            return JsonNode.Parse(exported)!.AsObject();
        }
        void Check(bool condition,string message){checks++;if(!condition)throw new Exception(message);}
        int Count(JsonObject output)=>output["Records"]!.AsArray().Count;
        var old=Export(Basic(1));Check(Count(old)==1,"Schema1 was lost");
        var valid=Export(V2());Check(Count(valid)==1,"Schema2 original fault evidence was dropped");
        var context=valid["Records"]![0]!["FaultContext"]!;
        Check(context["ReturnCandidates"]![0]!["Source"]!.GetValue<string>()=="stack-top-candidate","Candidate provenance was lost");
        Check(context["ReturnCandidates"]![0]!["Location"]!["Rva"]!.GetValue<int>()==256,"Candidate RVA was changed");
        Check(valid["ObservationKind"]!.GetValue<string>()=="FirstChanceNotCrashVerdict","False crash verdict");
        var unknown=V2();unknown["ArbitraryPrivateField"]="DO_NOT_EXPORT";unknown["FaultContext"]!["ArbitraryPrivateField"]="DO_NOT_EXPORT";unknown["FaultContext"]!["ReturnCandidates"]![0]!["ArbitraryPrivateField"]="DO_NOT_EXPORT";Check(Count(Export(unknown))==1,"Unknown metadata should be removed without dropping safe evidence");
        var noContext=V2();noContext["FaultContext"]=new JsonObject{["Captured"]=false,["ArbitraryPrivateField"]="DO_NOT_EXPORT"};Check(Count(Export(noContext))==1,"Missing native context must remain valid evidence");
        var provenance=V2();provenance["FaultContext"]!["ReturnCandidates"]![0]!["Source"]="DO_NOT_EXPORT";Check(Count(Export(provenance))==0,"Arbitrary candidate labels accepted");
        var overflow=V2();overflow["FaultContext"]!["ReturnCandidates"]=new JsonArray(Enumerable.Range(0,9).Select(_=>(JsonNode)Candidate()).ToArray());Check(Count(Export(overflow))==0,"Unbounded candidates accepted");
        var invalid=V2();invalid["FaultContext"]!["ReturnCandidates"]![0]!["Location"]=new JsonArray(42,8192,8192);Check(Count(Export(invalid))==0,"Out-of-image RVA accepted");
        var zero=V2();zero["FaultContext"]!["ReturnCandidates"]![0]!["Location"]=new JsonArray(0,0,0);Check(Count(Export(zero))==0,"Non-image return candidate accepted");
        var frames=V2();frames["FramesKind"]="caller-stack";Check(Count(Export(frames))==0,"Observer stack can be relabelled as caller stack");
        var missing=V2();missing.Remove("FaultContext");Check(Count(Export(missing))==0,"Incomplete Schema2 accepted");
        Check(Count(Export(Basic(3)))==0,"Unknown future schema accepted");
        Check(Count(Export(V2(),torn:true))==0,"Torn record accepted");
        Console.WriteLine($"NATIVE_EXCEPTION_CONTEXT_EXPORT_PASS checks={checks} schema1_and_2=true candidate_provenance=true unknown_fields_removed=true no_memory_dump=true");
    }
}
