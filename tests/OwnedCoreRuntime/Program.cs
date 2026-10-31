using System.Text.Json;
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--contracts")
        {
            try { int passed = InventoryContracts.Run() + InputContracts.Run(); Console.WriteLine($"RESULT {passed} passed; 0 skipped"); return passed == 22 ? 0 : 1; }
            catch { Console.Error.WriteLine("CORE_RUNTIME_CONTRACT_FAILED"); return 1; }
        }
        var report = new RuntimeReport();
        try
        {
            if (args.Length != 2 || args[0] != "--manifest") throw RuntimeInputs.Invalid();
            byte[] manifest;
            using (var input = new FileStream(args[1],FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if (input.Length is <= 0 or > 4096) throw RuntimeInputs.Invalid();
                manifest = new byte[checked((int)input.Length)]; input.ReadExactly(manifest);
            }
            var inputs = new RuntimeInputs(manifest,RuntimeInputs.Resource("inert-recipe.json"),RuntimeInputs.Resource("inert-profile.toml"));
            if (Path.GetFullPath(args[1]) != Path.Combine(Path.GetDirectoryName(inputs.CorePath)!,"runtime-input.json")) throw RuntimeInputs.Invalid();
            await RuntimeExperiment.Run(inputs,report).ConfigureAwait(false);
        }
        catch (Exception error) { report.ErrorCode = RuntimeExperiment.Failure(error,report.Stage); }
        Console.WriteLine(JsonSerializer.Serialize(report,new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        return report.Passed ? 0 : 1;
    }
}
