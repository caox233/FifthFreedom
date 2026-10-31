using System.Reflection;

var checks = Assembly.GetExecutingAssembly().GetType("GameplayDraftChecks");
if (checks == null)
{
    Console.Error.WriteLine("FAIL GAMEPLAY_DRAFT_MISSING: no confirmation/cancel state boundary; behavioral_cases_executed=0");
    return 1;
}
try { checks.GetMethod("Run")!.Invoke(null, null); return 0; }
catch (Exception error)
{
    Console.Error.WriteLine(error is TargetInvocationException { InnerException: { } inner } ? inner : error);
    return 1;
}
