using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SteamAutoShutdown
{
    public enum Phase { Idle, WaitingForFinish, Busy, Settling, Ready }

    public class Decision
    {
        public Phase Phase;
        public int SecondsLeft;   // only meaningful while Settling
        public string Blocker;    // why a shutdown is being held back, if it is
    }

    // Decides when Steam's downloads are done, purely from lines of logs\content_log.txt.
    // No I/O here, so the rules can be tested against recorded log lines.
    public class Tracker
    {
        static readonly Regex StateLine = new Regex(@"AppID (\d+) state changed : (.*)$");
        static readonly Regex FinishedLine = new Regex(@"AppID (\d+) finished update");
        static readonly Regex RateLine = new Regex(@"Current download rate: ([\d.]+) Mbps");

        readonly Dictionary<string, string> states = new Dictionary<string, string>();
        readonly HashSet<string> watched = new HashSet<string>();
        readonly TimeSpan grace;
        DateTime? quietSince;

        public bool Armed { get; private set; }
        public int FinishedCount { get; private set; }
        public double? LastRateMbps { get; private set; }

        public Tracker(TimeSpan grace) { this.grace = grace; }

        // Steam writes "Update Queued"/"Running"/"Started" while an update is in the queue.
        // "Update delayed for N secs" is a future scheduled update, not a download happening now.
        static bool IsPending(string s)
        {
            if (s.Contains("Update delayed")) return false;
            return s.Contains("Update Queued") || s.Contains("Update Running")
                || s.Contains("Update Started") || s.Contains("Update Paused");
        }

        static bool IsPaused(string s) { return s.Contains("Update Paused"); }

        public void Feed(string line)
        {
            Match m = StateLine.Match(line);
            if (m.Success)
            {
                string app = m.Groups[1].Value, state = m.Groups[2].Value;
                states[app] = state;
                if (!Armed) return;
                if (!IsPending(state)) watched.Remove(app);
                // A paused app only stops being waited on if it was never being waited on.
                else if (!IsPaused(state)) watched.Add(app);
                return;
            }
            m = FinishedLine.Match(line);
            if (m.Success)
            {
                if (Armed) FinishedCount++;
                return;
            }
            m = RateLine.Match(line);
            if (m.Success)
                LastRateMbps = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        public void Arm()
        {
            Armed = true;
            FinishedCount = 0;
            quietSince = null;
            watched.Clear();
            // Updates parked with "Update Paused" before arming can sit there for weeks, so skip them.
            foreach (KeyValuePair<string, string> kv in states)
                if (IsPending(kv.Value) && !IsPaused(kv.Value)) watched.Add(kv.Key);
        }

        public void Disarm()
        {
            Armed = false;
            quietSince = null;
            watched.Clear();
        }

        // Called after a cancelled countdown is resumed, or when a new download interrupts one.
        public void ResetSettling() { quietSince = null; }

        // externalBlocker: reason from outside the log (game running, Steam closed, ...), or null.
        public Decision Evaluate(DateTime now, string externalBlocker)
        {
            if (!Armed) return new Decision { Phase = Phase.Idle };

            if (watched.Count > 0 || externalBlocker != null)
            {
                quietSince = null;
                return new Decision { Phase = Phase.Busy, Blocker = externalBlocker };
            }
            // Nothing queued but nothing completed either, e.g. the user removed it from the queue.
            if (FinishedCount == 0)
            {
                quietSince = null;
                return new Decision { Phase = Phase.WaitingForFinish };
            }
            if (quietSince == null) quietSince = now;
            TimeSpan left = grace - (now - quietSince.Value);
            if (left <= TimeSpan.Zero) return new Decision { Phase = Phase.Ready };
            return new Decision { Phase = Phase.Settling, SecondsLeft = (int)Math.Ceiling(left.TotalSeconds) };
        }

        public IEnumerable<KeyValuePair<string, string>> WatchedApps()
        {
            return watched.OrderBy(a => a).Select(a => new KeyValuePair<string, string>(a, Describe(states[a])));
        }

        public bool AnyRunning()
        {
            return watched.Any(a => states[a].Contains("Update Running") && !states[a].Contains("("));
        }

        static string Describe(string s)
        {
            if (s.Contains("(Suspended)")) return "Suspended by Steam (paused or a game is running)";
            if (IsPaused(s)) return "Paused";
            int paren = s.IndexOf('(');
            if (paren >= 0) return "Stopped: " + s.Substring(paren).Trim('(', ')', ' ');
            if (s.Contains("Update Running")) return "Downloading / installing";
            return "Queued";
        }
    }
}
