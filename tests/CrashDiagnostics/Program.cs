using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SplinterCellCNLauncher.Services;

if(args.Length!=2)throw new ArgumentException("fixture directory and output directory required");
string root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
string logs=Path.Combine(root,"data",Environment.MachineName,Environment.UserName,"logs","game");Directory.CreateDirectory(logs);
foreach(string source in Directory.EnumerateFiles(args[0],"exception-observations-*.jsonl"))File.Copy(source,Path.Combine(logs,Path.GetFileName(source)),true);
var sourcePath=Directory.GetFiles(logs,"*.jsonl").Single();
byte[] data=File.ReadAllBytes(sourcePath);if(data.Length==0)throw new Exception("Native observation required");
// Unknown fields, including secret-shaped data, must never be echoed.
string text=Encoding.UTF8.GetString(data,0,4096).TrimEnd();
text=text[..^1]+",\"ArbitraryPrivateField\":\"TEST_SECRET_DO_NOT_EXPORT\"}";
byte[] slot=Encoding.UTF8.GetBytes(text.PadRight(4095)+'\n');slot.CopyTo(data,0);File.WriteAllBytes(sourcePath,data);
string destination=Path.Combine(root,"diagnostics.zip");
DiagnosticBundleService.ExportRetained(destination,new(),root,overwrite:true,collectNetwork:false);
using var zip=ZipFile.OpenRead(destination);
var entry=zip.Entries.Single(e=>e.FullName.StartsWith("exceptions/fallback/"));
using var stream=entry.Open();using var reader=new StreamReader(stream);string output=reader.ReadToEnd();
if(output.Contains("TEST_SECRET_DO_NOT_EXPORT")||output.Contains("ArbitraryPrivateField"))throw new Exception("Unknown field leaked");
using var doc=JsonDocument.Parse(output);
if(doc.RootElement.GetProperty("ObservationKind").GetString()!="FirstChanceNotCrashVerdict")throw new Exception("False crash claim");
int count=doc.RootElement.GetProperty("Records").GetArrayLength();if(count!=data.Length/4096)throw new Exception("Record loss");
if(zip.Entries.Any(e=>e.FullName.EndsWith(".dmp")))throw new Exception("Unexpected dump");
Console.WriteLine($"DIAGNOSTIC_EXPORT_PASS records={count} unknown_fields_removed=true memory_dump=false archive={destination}");

// A torn final slot must be discarded, not turn the entire export into a crash.
data[^1]=0;File.WriteAllBytes(sourcePath,data);
string partial=Path.Combine(root,"diagnostics-partial.zip");
DiagnosticBundleService.ExportRetained(partial,new(),root,overwrite:true,collectNetwork:false);
using(var partialZip=ZipFile.OpenRead(partial))
using(var partialStream=partialZip.Entries.Single(e=>e.FullName.StartsWith("exceptions/fallback/")).Open())
using(var partialDoc=JsonDocument.Parse(partialStream))
{
    if(partialDoc.RootElement.GetProperty("Records").GetArrayLength()!=count-1 || partialDoc.RootElement.GetProperty("DroppedOrPartialSlots").GetInt32()!=1)throw new Exception("Partial record not safely rejected");
}
Console.WriteLine("PARTIAL_RECORD_PASS rejected=1 remaining="+(count-1));

DiagnosticByteDumpRegression.Run(Path.Combine(root,"byte-dump-regression"));

NativeExceptionContextRegression.Run(root);
