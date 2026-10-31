using System;
using SteamAutoShutdown;

class Tests
{
    const string Running = "AppID 1 state changed : Update Required,Fully Installed,Update Queued,Update Running,Update Started,";
    const string Done1 = "AppID 1 finished update, 1 mounted depots";
    const string Settled1 = "AppID 1 state changed : Fully Installed,";
    const string Suspended1 = "AppID 1 state changed : Update Required,Fully Installed,Update Queued,Update Started, (Suspended)";
    static int failed;
    static readonly DateTime T0 = new DateTime(2026, 1, 1);

    static Tracker New() { return new Tracker(TimeSpan.FromSeconds(120)); }

    static void Check(string name, Phase want, Phase got)
    {
        if (want != got) failed++;
        Console.WriteLine((want == got ? "ok    " : "FAIL  ") + name + (want == got ? "" : " want " + want + " got " + got));
    }

    static int Main()
    {
        Tracker t = New();
        Check("not armed never fires", Phase.Idle, t.Evaluate(T0, null).Phase);

        t = New(); t.Feed(Running); t.Arm();
        Check("download running at arm", Phase.Busy, t.Evaluate(T0, null).Phase);
        t.Feed(Done1); t.Feed(Settled1);
        Check("settles first", Phase.Settling, t.Evaluate(T0, null).Phase);
        Check("still settling at 119s", Phase.Settling, t.Evaluate(T0.AddSeconds(119), null).Phase);
        Check("ready at 120s", Phase.Ready, t.Evaluate(T0.AddSeconds(120), null).Phase);

        t = New(); t.Feed(Running); t.Arm(); t.Feed(Done1); t.Feed(Settled1);
        t.Evaluate(T0, null);
        t.Feed("AppID 2 state changed : Update Required,Update Queued,Update Running,");
        Check("next in queue resets the wait", Phase.Busy, t.Evaluate(T0.AddSeconds(200), null).Phase);
        t.Feed("AppID 2 finished update"); t.Feed("AppID 2 state changed : Fully Installed,");
        Check("wait restarts after second app", Phase.Settling, t.Evaluate(T0.AddSeconds(201), null).Phase);

        t = New(); t.Feed(Running); t.Arm(); t.Feed(Suspended1);
        Check("suspended holds", Phase.Busy, t.Evaluate(T0.AddHours(9), null).Phase);

        t = New(); t.Feed(Running); t.Arm();
        t.Feed("AppID 1 state changed : Update Required,Fully Installed,Update Paused,Update Started,");
        Check("pausing the watched app holds", Phase.Busy, t.Evaluate(T0.AddHours(9), null).Phase);

        t = New(); t.Feed(Running); t.Arm();
        t.Feed("AppID 1 state changed : Update Required,Fully Installed,Update Queued,Update Started, (Not enough disk space)");
        Check("disk full holds", Phase.Busy, t.Evaluate(T0.AddHours(9), null).Phase);

        t = New(); t.Feed(Running); t.Arm(); t.Feed("AppID 1 state changed : Update Required,");
        Check("removed from queue is not a finish", Phase.WaitingForFinish, t.Evaluate(T0.AddHours(9), null).Phase);

        t = New(); t.Feed("AppID 9 state changed : Update Required,Fully Installed,Update Paused,Update Started,"); t.Arm();
        Check("update parked before arming is ignored", Phase.WaitingForFinish, t.Evaluate(T0, null).Phase);
        t.Feed(Running); t.Feed(Done1); t.Feed(Settled1);
        t.Evaluate(T0, null);
        Check("parked update does not block a finish", Phase.Ready, t.Evaluate(T0.AddSeconds(120), null).Phase);

        t = New(); t.Feed(Running); t.Feed(Done1); t.Feed(Settled1); t.Arm();
        Check("finish before arming does not count", Phase.WaitingForFinish, t.Evaluate(T0, null).Phase);

        t = New(); t.Feed(Running); t.Arm(); t.Feed("AppID 1 state changed : Update Required,Fully Installed,Update Queued, (Update delayed for 35890 secs)");
        Check("rescheduled later is not a finish", Phase.WaitingForFinish, t.Evaluate(T0, null).Phase);

        t = New(); t.Feed(Running); t.Arm(); t.Feed(Done1); t.Feed(Settled1);
        t.Evaluate(T0, null);
        Check("game running blocks", Phase.Busy, t.Evaluate(T0.AddSeconds(500), "A game is running").Phase);
        Check("wait restarts after block clears", Phase.Settling, t.Evaluate(T0.AddSeconds(501), null).Phase);

        Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
        return failed;
    }
}
