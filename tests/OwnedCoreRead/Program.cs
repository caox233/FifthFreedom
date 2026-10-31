using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SplinterCellCNLauncher.Services;

internal static partial class Program
{
    private const string Instance="12345678-1234-4321-9234-123456789abc";
    private static int ordinal,passed,skipped;
    private static readonly string Attempt=new('1',32),Nonce=new('2',64);
    private static async Task<int> Main(string[] args)
    {
        if(args.Length==2 && args[0]=="--native-helper") return await NativeInterop(args[1]);
        if(args.Length!=0) {try{return await Helper(args);}catch{return 1;}}
        try {
            await Case("canonical explicit instance",()=>{foreach(var x in new[]{"",Guid.Empty.ToString(),Instance.ToUpperInvariant(),Instance.Replace("-","")})Reject(()=>OwnedCoreReadProtocol.Instance(x));Check(OwnedCoreReadProtocol.Instance(Instance)==Instance);return Task.CompletedTask;});
            await Case("source aliases and unsafe ranges rejected",()=>{foreach(var x in new[]{"127.1","10.01.0.1","0.0.0.0","127.0.0.1","169.254.0.1","224.0.0.1","10.0.0.256","10.0.0.1:2"," 10.0.0.1"})Reject(()=>OwnedCoreReadProtocol.Source(x));Check(OwnedCoreReadProtocol.Source("10.66.0.10")=="10.66.0.10");return Task.CompletedTask;});
            await Case("hello rejects unknown duplicate null schema and cross-attempt",()=>{string text=JsonSerializer.Serialize(new{schema=1,kind="hello",attempt=Attempt,nonce=Nonce,instance=Instance});foreach(var x in new[]{text.Replace("\"schema\":1","\"schema\":1,\"schema\":1"),text.Replace("\"schema\":1","\"schema\":true"),text.Replace("\"hello\"","null"),text.Replace(Attempt,new string('3',32)),text.Replace("{","{\"extra\":1,")}){using var d=JsonDocument.Parse(x);Reject(()=>OwnedCoreReadProtocol.Hello(d.RootElement,Attempt,Nonce,Instance));}return Task.CompletedTask;});
            await Case("observed reply binds request nonce source instance and tuple",()=>{var frame=Response(1,Nonce);using var d=JsonDocument.Parse(frame);var s=OwnedCoreReadProtocol.Response(d.RootElement,Attempt,Nonce,Instance,1,"10.66.0.10");Check(s is {Index:7});foreach(var x in new[]{frame.Replace(Nonce,new string('3',64)),frame.Replace("\"request\":1","\"request\":2"),frame.Replace("10.66.0.10","10.66.0.11"),frame.Replace("\"index\":7","\"index\":0"),frame.Replace("\"luid\":\"0000000000000001\"","\"luid\":null")}){using var bad=JsonDocument.Parse(x);Reject(()=>OwnedCoreReadProtocol.Response(bad.RootElement,Attempt,Nonce,Instance,1,"10.66.0.10"));}return Task.CompletedTask;});
            await Case("unavailable is explicit and does not return a snapshot",()=>{using var d=JsonDocument.Parse(JsonSerializer.Serialize(new{schema=1,kind="unavailable",attempt=Attempt,nonce=Nonce,instance=Instance,request=1,source="10.66.0.10",code="NIC_UNAVAILABLE"}));Check(OwnedCoreReadProtocol.Response(d.RootElement,Attempt,Nonce,Instance,1,"10.66.0.10")==null);return Task.CompletedTask;});
            await Case("framing rejects oversized and truncated bodies",async()=>{foreach(byte[] x in new[]{new byte[]{1,8,0,0},new byte[]{0,0,0,0},new byte[]{5,0,0,0,123},new byte[]{1,0}}){await RejectAsync(async()=>{using var d=await OwnedCoreReadProtocol.Read(new MemoryStream(x),default);});}});
            await Case("actual frame round trip and pre-cancel",async()=>{using var s=new MemoryStream();await OwnedCoreReadProtocol.Write(s,OwnedCoreReadProtocol.Challenge(Attempt,Nonce,Instance),default);s.Position=0;using var d=await OwnedCoreReadProtocol.Read(s,default);Check(d.RootElement.GetProperty("kind").GetString()=="challenge");using var stop=new CancellationTokenSource();stop.Cancel();await RejectAsync(async()=>{using var a=await OwnedCoreReadProtocol.Read(s,stop.Token);});});
            await Case("snapshot representation contains no dynamic identity",()=>{string s=new OwnedCoreNicSnapshot("private-label",new string('4',32),new string('5',16),8).ToString();Check(!s.Contains("private-label")&&!s.Contains(new string('4',32)));return Task.CompletedTask;});
            await Case("exact owned child returns historical snapshots",async()=>{Check(!Environment.Is64BitProcess);await using var f=new Fixture("normal");await using var c=await f.Start();Check(c.IsOpen);var a=await c.ObserveAsync("10.66.0.10");var b=await c.ObserveAsync("10.66.0.11");Check(a is {Index:7}&&b is {Index:7});},true);
            await Case("unavailable never falls back to configuration",async()=>{await using var f=new Fixture("unavailable");await using var c=await f.Start();Check(await c.ObserveAsync("10.66.0.10")==null&&c.IsOpen);},true);
            await Case("wrong response nonce permanently closes attempt",async()=>{await using var f=new Fixture("nonce");await using var c=await f.Start();await RejectAsync(()=>c.ObserveAsync("10.66.0.10"));Check(!c.IsOpen&&c.Lifetime.IsCancellationRequested);await RejectAsync(()=>c.ObserveAsync("10.66.0.10"));},true);
            await Case("old response cannot answer a later request",async()=>{await using var f=new Fixture("replay");await using var c=await f.Start();Check(await c.ObserveAsync("10.66.0.10")!=null);await RejectAsync(()=>c.ObserveAsync("10.66.0.10"));Check(!c.IsOpen);},true);
            await Case("actual pending request cancellation prevents late publication",async()=>{await using var f=new Fixture("stall");await using var c=await f.Start();using var stop=new CancellationTokenSource();var pending=c.ObserveAsync("10.66.0.10",stop.Token);await f.RequestArrived();stop.Cancel();await RejectAsync(()=>pending);Check(!c.IsOpen);},true);
            await Case("request timeout is bounded and terminal",async()=>{await using var f=new Fixture("stall");await using var c=await f.Start();var clock=Stopwatch.StartNew();await RejectAsync(()=>c.ObserveAsync("10.66.0.10"));Check(clock.Elapsed<TimeSpan.FromSeconds(5)&&!c.IsOpen);},true);
            await Case("pipe EOF revokes while owned process remains alive",async()=>{await using var f=new Fixture("eof");await using var c=await f.Start();await Until(()=>c.Lifetime.IsCancellationRequested);Check(!c.IsOpen);},true);
            await Case("owner exit revokes attempt",async()=>{await using var f=new Fixture("exit");await using var c=await f.Start();await Until(()=>c.Lifetime.IsCancellationRequested);Check(!c.IsOpen);},true);
            await Case("same-image descendant is not the exact owned pipe peer",async()=>{await using var f=new Fixture("foreign");await RejectAsync(async()=>{await using var c=await f.Start();});},true);
            await Case("dispose cancels real pending pipe IO and drains owned job",async()=>{await using var f=new Fixture("stall");var c=await f.Start();var pending=c.ObserveAsync("10.66.0.10");await f.RequestArrived();await c.DisposeAsync();await RejectAsync(()=>pending);Check(!c.IsOpen);},true);
            await Case("throwing lifetime callback cannot bypass exact owned job cleanup",async()=>{
                await using var f=new Fixture("normal");var c=await f.Start();
                var field=typeof(OwnedCoreReadChannel).GetField("owner",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                var owner=(OwnedCoreProcess)field.GetValue(c)!;
                using var registration=c.Lifetime.Register(()=>throw new InvalidOperationException("synthetic callback"));
                try {await c.DisposeAsync();throw new InvalidOperationException();}
                catch(IOException error) when(error.Message=="OWNED_CORE_READ_CLEANUP_FAILED") { }
                Check(!owner.IsAlive&&!c.IsOpen);f.ExpectedCleanupFailureObserved(c);
            },true);
            await Case("only explicit fixed RPC modes can add arguments",()=>{
                string root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"ff-rpc-declaration"));
                OwnedCoreLaunchInputs inputs(OwnedCoreRpcMode mode)=>new(Path.Combine(root,"child.exe"),1,new string('1',64),Path.Combine(root,"config.json"),1,new string('2',64),rpcMode:mode);
                Check(inputs(OwnedCoreRpcMode.Default).FixedRpcArguments=="");
                Reject(()=>inputs((OwnedCoreRpcMode)99));return Task.CompletedTask;
            });
            await Case("ending reads cancels pending IO but retains exact live owner and pins until dispose",async()=>{
                await using var f=new Fixture("stall");var c=await f.Start();
                var pending=c.ObserveAsync("10.66.0.10");await f.RequestArrived();
                c.EndReads();c.EndReads();await RejectAsync(()=>pending);
                Check(!c.IsOpen&&c.Lifetime.IsCancellationRequested&&c.IsOwnedProcessAlive);
                await RejectAsync(()=>c.ObserveAsync("10.66.0.10"));
                Reject(()=>File.WriteAllText(f.Config,"must remain pinned"));
                await c.DisposeAsync();Check(!c.IsOwnedProcessAlive);File.WriteAllText(f.Config,"released");
            },true);
            await Case("actual owned child receives only the fixed nonzero loopback RPC arguments",async()=>{
                await using var f=new Fixture("rpc-fixed");await using var c=await f.Start(OwnedCoreRpcMode.LoopbackProbe);
                Check(await c.ObserveAsync("10.66.0.10") is {Index:7});
            },true);
            Console.WriteLine($"RESULT {passed} passed; {skipped} skipped");return 0;
        }catch{return 1;}
    }
    private static string Response(ulong request,string nonce,string attempt="",string source="10.66.0.10")=>JsonSerializer.Serialize(new{schema=1,kind="observed",attempt=attempt==""?Attempt:attempt,nonce,instance=Instance,request,source,name="synthetic-nic",guid=new string('1',32),luid="0000000000000001",index=7});
    private static void Check(bool value){if(!value)throw new InvalidOperationException();}
    private static void Reject(Action action){try{action();}catch{return;}throw new InvalidOperationException();}
    private static async Task RejectAsync(Func<Task> action){try{await action();}catch{return;}throw new InvalidOperationException();}
    private static async Task Until(Func<bool> condition){using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!condition())await Task.Delay(10,stop.Token);}
    private static async Task Case(string name,Func<Task> action,bool windows=false){ordinal++;if(windows&&!OperatingSystem.IsWindows()){skipped++;Console.WriteLine("SKIP Windows-only: "+name);return;}try{await action();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){Console.WriteLine($"CI_MANAGED_FAILURE suite=OwnedCoreRead case={ordinal:00} exception={ExceptionType(e)}");throw;}}
    private static string ExceptionType(Exception e)=>e switch{UnauthorizedAccessException=>"UnauthorizedAccessException",OperationCanceledException=>"OperationCanceledException",IOException=>"IOException",InvalidOperationException=>"InvalidOperationException",TimeoutException=>"TimeoutException",_=>"OtherException"};
    private sealed class Fixture:IAsyncDisposable
    {
        internal readonly string Root,Config,Arrived;private readonly string image;private readonly List<OwnedCoreReadChannel> channels=[];
        internal Fixture(string mode,string? executable=null){Root=Path.Combine(Path.GetTempPath(),"ff-core-read-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Root);image=executable??Environment.ProcessPath!;Config=Path.Combine(Root,"config.json");Arrived=Path.Combine(Root,"arrived");File.WriteAllText(Config,JsonSerializer.Serialize(new{mode,arrived=Arrived,started=Path.Combine(Root,"started")}));}
        internal async Task<OwnedCoreReadChannel> Start(OwnedCoreRpcMode rpcMode=OwnedCoreRpcMode.Default){string hash(string p){using var s=File.OpenRead(p);return Convert.ToHexStringLower(SHA256.HashData(s));}var inputs=new OwnedCoreLaunchInputs(image,new FileInfo(image).Length,hash(image),Config,new FileInfo(Config).Length,hash(Config),rpcMode:rpcMode);var c=await OwnedCoreReadChannel.StartAsync(inputs,Instance);channels.Add(c);File.WriteAllText(Path.Combine(Root,"started"),"started");return c;}
        internal Task RequestArrived()=>Until(()=>File.Exists(Arrived));
        internal void ExpectedCleanupFailureObserved(OwnedCoreReadChannel channel)=>channels.Remove(channel);
        public async ValueTask DisposeAsync(){foreach(var c in channels)await c.DisposeAsync();await DeleteOwnedDirectoryAsync(Root,()=>true);}
    }
    private static async Task<int> Helper(string[] args)
    {
        bool descendant=args.Length==2&&args[0]=="--peer";
        string config=descendant?args[1]:(args.Length is 7 or 9)&&args[0]=="--config-file"?args[1]:throw new InvalidOperationException();
        using var cfg=JsonDocument.Parse(File.ReadAllBytes(config));string mode=cfg.RootElement.GetProperty("mode").GetString()!;
        if(!descendant){Check(args[2]=="--disable-env-parsing"&&args[3]=="--console-log-level"&&args[4]=="off"&&args[5]=="--file-log-level"&&args[6]=="off");
            Check(mode=="rpc-fixed"?args.Length==9&&args[7]=="--rpc-portal"&&args[8]=="127.0.0.1:15998":args.Length==7);}
        if(mode=="foreign"&&!descendant){using var p=Process.Start(new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,ArgumentList={"--peer",config}})!;await Task.Delay(20000);return 0;}
        using var descriptor=JsonDocument.Parse(Environment.GetEnvironmentVariable(OwnedCoreReadDescriptor.Variable)!);var d=descriptor.RootElement;
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var pipe=new NamedPipeClientStream(".",d.GetProperty("pipe").GetString()!,PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(stop.Token);Check(GetNamedPipeServerProcessId(pipe.SafePipeHandle,out uint server)&&server==d.GetProperty("parent_pid").GetUInt32());
        using(var parent=Process.GetProcessById((int)server))Check((ulong)parent.StartTime.ToUniversalTime().ToFileTimeUtc()==d.GetProperty("parent_birth").GetUInt64());
        using(var challenge=await OwnedCoreReadProtocol.Read(pipe,stop.Token)){var r=challenge.RootElement;await OwnedCoreReadProtocol.Write(pipe,new{schema=1,kind="hello",attempt=r.GetProperty("attempt").GetString(),nonce=r.GetProperty("nonce").GetString(),instance=r.GetProperty("instance").GetString()},stop.Token);}
        if(mode is "eof" or "exit"){await Until(()=>File.Exists(cfg.RootElement.GetProperty("started").GetString()!));pipe.Dispose();if(mode=="eof")await Task.Delay(15000,stop.Token);return 0;}
        JsonElement? old=null;
        while(true){using var request=await OwnedCoreReadProtocol.Read(pipe,stop.Token);var r=request.RootElement;File.WriteAllText(cfg.RootElement.GetProperty("arrived").GetString()!,"arrived");if(mode=="stall"){await Task.Delay(15000,stop.Token);continue;}string attempt=r.GetProperty("attempt").GetString()!,nonce=r.GetProperty("nonce").GetString()!,source=r.GetProperty("source").GetString()!;ulong id=r.GetProperty("request").GetUInt64();
            if(mode=="unavailable"){await OwnedCoreReadProtocol.Write(pipe,new{schema=1,kind="unavailable",attempt,nonce,instance=Instance,request=id,source,code="NIC_UNAVAILABLE"},stop.Token);continue;}
            if(mode=="nonce")nonce=new string('f',64);
            using var response=JsonDocument.Parse(Response(id,nonce,attempt,source));var value=mode=="replay"&&old.HasValue?old.Value:response.RootElement;await OwnedCoreReadProtocol.Write(pipe,value,stop.Token);old=response.RootElement.Clone();
        }
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
}
