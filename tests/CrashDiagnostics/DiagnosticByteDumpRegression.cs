using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SplinterCellCNLauncher.Services;

internal static class DiagnosticByteDumpRegression
{
    internal static void Run(string root)
    {
        int count=0;
        void Case(string name, string file, byte[] content, bool accepted, bool dump=false, DiagnosticExportBudget? budget=null)
        {
            string directory=Path.Combine(root,name);
            string data=Path.Combine(directory,"data",ScblLan.LocalData.Segment(Environment.MachineName),ScblLan.LocalData.Segment(Environment.UserName));
            string source=Path.Combine(data,file);Directory.CreateDirectory(Path.GetDirectoryName(source)!);File.WriteAllBytes(source,content);
            string output=Path.Combine(directory,"export.zip");
            Exception? error=null;
            try{DiagnosticBundleService.ExportRetained(output,new(),directory,collectNetwork:false,budget:budget);}
            catch(InvalidDataException e){error=e;}
            if(accepted!=(error==null) || File.Exists(output)!=accepted)throw new Exception("BYTE_DUMP_REGRESSION "+name+" accepted="+(error==null));
            if(!File.ReadAllBytes(source).SequenceEqual(content))throw new Exception("Source changed");
            if(Directory.EnumerateFiles(directory,"*.tmp").Any())throw new Exception("Temporary archive leaked");
            if(accepted)
            {
                using var zip=ZipFile.OpenRead(output);
                var log=zip.Entries.Single(e=>e.FullName.StartsWith("logs/",StringComparison.Ordinal));
                using var reader=new StreamReader(log.Open(),Encoding.UTF8);string text=reader.ReadToEnd();
                if(text.Contains("TEST_SECRET") || (dump && (!text.Contains("[REDACTED_SERVER_BYTE_DUMP]") || text.Contains("[0, a]") || text.Contains("[00, FF]"))))throw new Exception("Byte dump/secret leak");
                using var manifestStream=zip.GetEntry("manifest.json")!.Open();using var manifest=JsonDocument.Parse(manifestStream);
                var exported=manifest.RootElement.GetProperty("Sources").EnumerateArray().Single(s=>s.GetProperty("Status").GetString()=="Read");
                if(!exported.GetProperty("ReadComplete").GetBoolean() || (dump && exported.GetProperty("Redactions").GetInt32()<1))throw new Exception("Misleading source status");
                if(file.EndsWith("server.log.json"))using(JsonDocument.Parse(text)){}
            }
            Console.WriteLine("BYTE_DUMP_PASS "+name+" accepted="+accepted+" source_unchanged=true");count++;
        }
        byte[] Bytes(string s)=>new UTF8Encoding(false,true).GetBytes(s);
        const string stderr="server/127_0_0_1/server.stderr.txt";
        Case("rust-hex",stderr,Bytes("[0, a]\n"),true,true);
        Case("rust-hex-letter-first",stderr,Bytes("[a, b]\n"),true,true);
        Case("truncated-letter-first",stderr,Bytes("[a, b\n"),false);
        Case("truncated-single-hex",stderr,Bytes("[ab\n"),false);
        Case("truncated-ff-first",stderr,Bytes("[ff, 1\n"),false);
        Case("ordinary-bracket-label",stderr,Bytes("[debug] harmless.\n"),true);
        Case("rust-hex-crlf",stderr,Bytes("[00, FF]\r\n"),true,true);
        Case("rust-hex-no-newline",stderr,Bytes("[0, a]"),true,true);
        Case("rust-hex-bom-space",stderr,Bytes("\uFEFF \t[0, a] \r\n"),true,true);
        Case("numeric-only-byte-dump",stderr,Bytes("[0, 10, 99]\n"),true,true);
        Case("server-json-message","server/127_0_0_1/server.log.json",Bytes("{\"msg\":\"Sending probe\\n[0, a]\",\"level\":\"INFO\"}\n"),true,true);
        Case("server-json-secret","server/127_0_0_1/server.log.json",Bytes("{\"msg\":\"Sending probe\\n[0, a]\",\"token\":\"TEST_SECRET\"}\n"),true,true);
        Case("server-json-objects",stderr,Bytes("[{\"token\":\"TEST_SECRET\",\"safe\":1}]\n"),true);
        Case("server-json-mixed-array",stderr,Bytes("[1,{\"token\":\"TEST_SECRET\"}]\n"),true);
        Case("server-regular",stderr,Bytes("A harmless producer line.\n"),true);
        Case("launcher-time-prefix","launcher.log",Bytes("[2026-10-22 00:00:00] INFO harmless.\n"),true);
        Case("other-producer-not-reclassified","launcher.log",Bytes("[0, a]\n"),false);
        Case("malformed-object",stderr,Bytes("{\"token\":\"TEST_SECRET\"\n"),false);
        Case("malformed-array",stderr,Bytes("[0, missing]\n"),false);
        Case("truncated-hex-dump",stderr,Bytes("[0, a\n"),false);
        Case("utf8-invalid",stderr,new byte[]{0xff,10},false);
        Case("line-limit",stderr,Bytes("[0, a]\n"),false,budget:new(){MaxLineBytes=4});
        Case("file-limit",stderr,Bytes("[0, a]\n"),false,budget:new(){MaxFileInputBytes=4});
        Case("total-limit",stderr,Bytes("[0, a]\n"),false,budget:new(){MaxTotalInputBytes=4});
        Console.WriteLine("BYTE_DUMP_SUMMARY cases="+count+" real_ExportRetained=true no_network=true no_game=true");
    }
}
