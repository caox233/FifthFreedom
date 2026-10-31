using Grpc.Core;
using ScblLan;
using SplinterCellCNLauncher.Models;
using SplinterCellCNLauncher.Services;

int passed=0,failed=0;
async Task Check(string name,Func<Task<bool>> check)
{
    try { if(!await check())throw new Exception("assertion");passed++;Console.WriteLine("PASS "+name); }
    catch(Exception) { failed++;Console.WriteLine("FAIL "+name); }
}
const string suffix=": Update/restore the complete SCBL LAN package (launcher, main DLL, gameplay DLL, server).";
foreach(bool register in new[]{false,true})
{
    string operation=register?"register":"login";
    async Task<(bool Success,bool ServerError,string Message)> Invoke()
    {
        var auth=new AuthService();
        if(register){var r=await auth.RegisterAsync("fixture.example.invalid","fixture-user","fixture-secret","unused");return(r.Status==RegisterStatus.Success,r.Status==RegisterStatus.ServerError,r.Message);}
        var l=await auth.LoginAsync("fixture.example.invalid","fixture-user","fixture-secret");return(l.Status==LoginStatus.Success,l.Status==LoginStatus.ServerError,l.Message);
    }
    foreach(string reason in new[]{"RELEASE_MISMATCH","PACKAGE_CATALOG_MISMATCH"})
    foreach(bool rpc in new[]{false,true})
    foreach(bool withExplanation in new[]{false,true})
    {
        await Check($"{operation}/{reason}/{(rpc?"rpc":"response")}/{withExplanation}",async()=>{
            Fixture.Reset();string detail=reason+(withExplanation?suffix:"");
            if(rpc)Fixture.Error=new RpcException(new Status(StatusCode.FailedPrecondition,detail));else Fixture.ResponseError=detail;
            var result=await Invoke();
            return !result.Success&&result.ServerError&&result.Message.Contains(reason,StringComparison.Ordinal)
                &&result.Message.Contains("房主和加入端",StringComparison.Ordinal)&&result.Message.Contains("同一个完整 ZIP",StringComparison.Ordinal)
                &&result.Message.Contains("same complete ZIP",StringComparison.Ordinal)
                &&result.Message.Contains(reason=="RELEASE_MISMATCH"?"发行版本不同":"目录校验不同",StringComparison.Ordinal)
                &&!result.Message.Contains(reason=="RELEASE_MISMATCH"?"目录校验不同":"发行版本不同",StringComparison.Ordinal);
        });
    }
    foreach(string unknown in new[]{"COMPAT_MISMATCH","PACKAGE_IDENTITY_REQUIRED","PACKAGE_MIXED","prefix RELEASE_MISMATCH","RELEASE_MISMATCH_SUFFIX","release_mismatch","RELEASE_MISMATCH without delimiter","PACKAGE_CATALOG_MISMATCHx"})
    {
        await Check(operation+"/unrelated/"+unknown,async()=>{
            Fixture.Reset();Fixture.Error=new RpcException(new Status(StatusCode.FailedPrecondition,unknown));var r=await Invoke();
            return !r.Success&&!r.ServerError&&r.Message==Fixture.Error.Message;
        });
    }
    foreach(var code in new[]{StatusCode.Unavailable,StatusCode.Cancelled,StatusCode.Internal})
    {
        await Check(operation+"/transport/"+code,async()=>{
            Fixture.Reset();Fixture.Error=new RpcException(new Status(code,"RELEASE_MISMATCH"+suffix));var r=await Invoke();
            return !r.Success&&!r.ServerError&&r.Message==Fixture.Error.Message;
        });
    }
    await Check(operation+"/success-preserved",async()=>{Fixture.Reset();var r=await Invoke();return r.Success&&!r.ServerError;});
}
foreach(var code in new[]{StatusCode.Unavailable,StatusCode.Cancelled})
    await Check("capability/"+code,async()=>{Fixture.Reset();Fixture.ConnectionError=new RpcException(new Status(code,"owned fixture"));return !await new AuthService().CheckServerReachableAsync("fixture.example.invalid");});
await Check("capability/pre-cancel",async()=>{
    Fixture.Reset();using var c=new CancellationTokenSource();c.Cancel();
    try{await new AuthService().CheckServerReachableAsync("fixture.example.invalid",cancellationToken:c.Token);return false;}
    catch(OperationCanceledException){return Fixture.Connections==0;}
});
Console.WriteLine($"RESULT package-admission-messages passed={passed} failed={failed}; production AuthService with controlled in-memory transport; no server or game");
Environment.ExitCode=failed==0?0:1;
