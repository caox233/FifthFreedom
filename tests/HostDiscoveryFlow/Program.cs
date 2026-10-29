using System.Reflection;

// The same runner can inspect a preserved old SourceRoot without making the
// missing new API a compile error. This baseline is one capability failure,
// not a claim that unavailable behavioral cases actually executed.
var checks = Assembly.GetExecutingAssembly().GetType("HostDiscoveryFlowChecks");
if (checks == null)
{
    Console.Error.WriteLine("FAIL FLOW_IMPLEMENTATION_MISSING: bounded local discovery unavailable; behavioral_cases_executed=0");
    return 1;
}
try
{
    await (Task)checks.GetMethod("Run")!.Invoke(null, null)!;
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error is TargetInvocationException { InnerException: { } inner } ? inner : error);
    return 1;
}
