using System.Text;
using System.Text.Json;

internal static class InputContracts
{
    private const string Probe = "123456781234123412341234567890ab";
    private static readonly string Source = new('a',64), Image = new('b',64);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("CORE_RUNTIME_CONTRACT_FAILED"); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { return; }
        throw new Exception("CORE_RUNTIME_CONTRACT_FAILED");
    }
    private static byte[] Recipe(byte[] profile, string kind = "independent-windows-inert-owned-read-build-only") => JsonSerializer.SerializeToUtf8Bytes(new {
        schema=1, kind, expected_prepared=new {source_tree_sha256=Source}, inert_profile=new {bytes=profile.Length,sha256=RuntimeInputs.Sha(profile)} });
    private static byte[] Manifest(byte[] recipe, byte[] profile, string? core = null) => JsonSerializer.SerializeToUtf8Bytes(new {
        schema=1, probe_id=Probe, core_path=core ?? Path.Combine(Path.GetTempPath(),"ff-core-runtime-"+Probe,"easytier-core.exe"), core_bytes=123456,
        core_sha256=Image, source_tree_sha256=Source, build_recipe_sha256=RuntimeInputs.Sha(recipe), inert_profile_sha256=RuntimeInputs.Sha(profile)});
    internal static int Run()
    {
        int passed=0; byte[] profile=RuntimeInputs.Resource("inert-profile.toml"), recipe=Recipe(profile), manifest=Manifest(recipe,profile);
        RuntimeInputs Parse(byte[]? row=null,byte[]? r=null,byte[]? p=null)=>new(row??manifest,r??recipe,p??profile);
        void Case(string label,Action action){action();passed++;Console.WriteLine("PASS "+label);}
        Case("only fresh instance and adapter name are added to exact inert template",()=>{
            var inputs=Parse();byte[] result=inputs.ConfigurationBytes();string text=Encoding.UTF8.GetString(result);
            Check(Guid.TryParseExact(inputs.Instance,"D",out var instance)&&instance!=Guid.Empty);
            Check(Guid.TryParseExact(inputs.AdapterGuid,"N",out var adapter)&&adapter!=Guid.Empty);
            string restored=text.Replace($"instance_id = \"{inputs.Instance}\"\n","",StringComparison.Ordinal)
                .Replace($"dev_name = \"{inputs.AdapterName}\"\n","",StringComparison.Ordinal);
            Check(Encoding.UTF8.GetBytes(restored).AsSpan().SequenceEqual(profile));
            Check(inputs.Instance!=Parse().Instance&&inputs.AdapterGuid!=Parse().AdapterGuid);
        });
        Case("strict runtime manifest rejects duplicates unknown fields and wrong schema",()=>{
            string text=Encoding.UTF8.GetString(manifest);
            foreach(string row in new[]{text.Replace("\"schema\":1","\"schema\":1,\"schema\":1"),text.Replace("\"schema\":1","\"schema\":true"),text.Replace("{","{\"extra\":0,"),text.Replace("\"schema\":1,","")})
                Reject(()=>Parse(Encoding.UTF8.GetBytes(row)));
            Reject(()=>Parse(new byte[4097]));
        });
        Case("old twelve-patch kind and wrong prepared source cannot launch",()=>{
            byte[] old=Recipe(profile,"independent-windows-owned-read-build-only");
            Reject(()=>new RuntimeInputs(Manifest(old,profile),old,profile));
            Reject(()=>Parse(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace(Source,new string('c',64)))));
        });
        Case("both embedded recipe bytes and inert profile bytes are bound",()=>{
            byte[] changedRecipe=[..recipe,(byte)' '];Reject(()=>Parse(r:changedRecipe));
            byte[] changedProfile=[..profile,(byte)' '];Reject(()=>Parse(p:changedProfile));
            byte[] stale=Recipe(profile);Reject(()=>new RuntimeInputs(Manifest(stale,changedProfile),stale,changedProfile));
        });
        Case("runtime image requires canonical owned staging parent and exact basename",()=>{
            foreach(string path in new[]{"relative.exe",Path.Combine(Path.GetTempPath(),"unowned","easytier-core.exe"),Path.Combine(Path.GetTempPath(),"ff-core-runtime-"+Probe,"other.exe"),Path.Combine(Path.GetTempPath(),"ff-core-runtime-"+Probe,"..","easytier-core.exe")})
                Reject(()=>Parse(Manifest(recipe,profile,path)));
        });
        Case("manifest refuses zero uppercase malformed identities and invalid image lengths",()=>{
            string text=Encoding.UTF8.GetString(manifest);
            foreach(string row in new[]{text.Replace(Probe,new string('0',32)),text.Replace(Probe,Probe.ToUpperInvariant()),text.Replace(Image,new string('z',64)),text.Replace("\"core_bytes\":123456","\"core_bytes\":0"),text.Replace("\"core_bytes\":123456","\"core_bytes\":268435457")})
                Reject(()=>Parse(Encoding.UTF8.GetBytes(row)));
        });
        Case("template insertion rejects ambiguous flags and preexisting identity keys",()=>{
            foreach(string text in new[]{"[flags]\n[flags]\n","[flags]\ndev_name=\"old\"\n","instance_id=\"old\"\n[flags]\n","[flags]\r\n"})
            {
                byte[] bad=Encoding.UTF8.GetBytes(text),r=Recipe(bad);Reject(()=>new RuntimeInputs(Manifest(r,bad),r,bad));
            }
        });
        Case("private paths and digests are absent from input representation",()=>{
            var inputs=Parse();string text=inputs.ToString();Check(!text.Contains(inputs.CorePath,StringComparison.Ordinal)&&!text.Contains(Image,StringComparison.Ordinal)&&!text.Contains(Probe,StringComparison.Ordinal));
            Check(inputs.CreateSpecification(inputs.ConfigurationBytes()).FixedRpcArguments==" --rpc-portal 127.0.0.1:15998");
        });
        Case("embedded source recipe and profile agree before runtime input can bind",()=>{
            byte[] actualRecipe=RuntimeInputs.Resource("inert-recipe.json");
            using var row=JsonDocument.Parse(actualRecipe);
            string actualSource=row.RootElement.GetProperty("expected_prepared").GetProperty("source_tree_sha256").GetString()!;
            byte[] actualManifest=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Manifest(actualRecipe,profile)).Replace(Source,actualSource,StringComparison.Ordinal));
            var inputs=new RuntimeInputs(actualManifest,actualRecipe,profile);
            Check(inputs.SourceTreeSha256==actualSource&&inputs.RecipeSha256==RuntimeInputs.Sha(actualRecipe));
            Check(row.RootElement.GetProperty("inert_profile").GetProperty("ipv4").GetString()=="10.254.253.1/24");
        });
        return passed;
    }
}
