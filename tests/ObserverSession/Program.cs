using SplinterCellCNLauncher.Services;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static void Reject(Action action, string text)
{
    try { action(); } catch (InvalidOperationException e) when (e.Message.Contains(text)) { return; }
    throw new Exception("Expected rejection: " + text);
}
var s = new ObserverSession();
Check(!s.Requested && !s.HostAllows(true) && !s.JoinAsObserver(false), "Default must be off");
s.Requested = true;
Check(s.HostAllows(true) && !s.JoinAsObserver(true), "Host grants, never observes");
Check(!s.HostAllows(false) && s.JoinAsObserver(false), "Joiner requests, never grants");
s.Launched(true);
Check(s.HostAllows(true), "Host permission belongs to the server session");
s.ChangeRole();
Check(!s.Requested, "Role changes cannot carry an old opt-in");
s.Requested = true;
s.Launched(false);
Check(!s.Requested, "A successful observer launch consumes its opt-in");
ObserverSession.ValidatePermission(false, false);
ObserverSession.ValidatePermission(true, true);
Reject(() => ObserverSession.ValidatePermission(true, false), "房主未开启");
Check(!new ObserverSession().Requested, "No state survives a new window/session");
var env=new Dictionary<string,string?> {{"SCBL_OBSERVER","1"},{"UNRELATED","keep"}};
ObserverSession.ApplyLaunchEnvironment(env,false);
Check(!env.ContainsKey("SCBL_OBSERVER") && env["UNRELATED"]=="keep","Ordinary/host launch clears inherited observer opt-in only");
ObserverSession.ApplyLaunchEnvironment(env,true);
Check(env["SCBL_OBSERVER"]=="1","Requested joiner is scoped to this child environment");
Console.WriteLine("PASS: default off; host grants only; joiner consumes once; role reset; closed-host rejection; per-child environment. Native backend and package validation are separate required gates.");
