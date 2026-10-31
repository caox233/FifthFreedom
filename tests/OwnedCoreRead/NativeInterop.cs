using SplinterCellCNLauncher.Services;

internal static partial class Program
{
    private static async Task<int> NativeInterop(string executable)
    {
        int count=0;
        try
        {
            if(!OperatingSystem.IsWindows() || Environment.Is64BitProcess || !Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new InvalidOperationException();
            await using(var fixture=new Fixture("normal",executable))
            {
                await using var channel=await fixture.Start();
                var value=await channel.ObserveAsync("10.66.0.10");
                Check(value is {Name:"synthetic-rust-nic",Guid:"00000000000000000000000000000001",Luid:"0000000000000002",Index:7});
                await Task.Delay(TimeSpan.FromSeconds(4)); // Idle is not a frame timeout.
                Check(await channel.ObserveAsync("10.66.0.11")==null);
                Check(channel.IsOpen);
            }
            Console.WriteLine("PASS actual native parent anchor and read protocol");count++;
            await using(var fixture=new Fixture("wrong-birth",executable))
                await RejectAsync(async()=>{await using var channel=await fixture.Start();});
            Console.WriteLine("PASS actual native rejects wrong parent birth");count++;
            await using(var fixture=new Fixture("wrong-image",executable))
                await RejectAsync(async()=>{await using var channel=await fixture.Start();});
            Console.WriteLine("PASS actual native rejects wrong parent image digest");count++;
            await using(var fixture=new Fixture("normal",executable))
            {
                await using var channel=await fixture.Start();
                // Test-only malformed peer action against the actual Rust reader.
                // No production bypass or arbitrary stream API is exposed.
                var field=typeof(OwnedCoreReadChannel).GetField("pipe",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                var pipe=(System.IO.Pipes.NamedPipeServerStream)field.GetValue(channel)!;
                await pipe.WriteAsync(new byte[]{64}); await pipe.FlushAsync();
                await Until(()=>channel.Lifetime.IsCancellationRequested);
                Check(!channel.IsOpen);
            }
            Console.WriteLine("PASS actual native bounds incomplete prefix after first byte");count++;
            Console.WriteLine("OWNED_CORE_READ_INTEROP_OK 4");return 0;
        }
        catch(Exception error)
        {
            Console.WriteLine($"CI_MANAGED_FAILURE suite=OwnedCoreReadInterop case={count+1:00} exception={ExceptionType(error)}");
            return 1;
        }
    }
}
