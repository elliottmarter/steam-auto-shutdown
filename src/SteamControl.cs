using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SteamAutoShutdown
{
    // Drives Steam's own download buttons. Steam has no public API for this, so it goes through
    // the debug port of Steam's UI, which only exists after launching with -cef-enable-debugging.
    // The flag lasts until Steam next restarts; nothing is changed on disk.
    class SteamControl
    {
        const string DebugList = "http://127.0.0.1:8080/json";
        const string LocalClient = "'0'";
        static readonly Regex SharedContext = new Regex(
            "\"title\"\\s*:\\s*\"SharedJSContext\".*?\"webSocketDebuggerUrl\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Singleline);
        static readonly Regex ResultValue = new Regex("\"value\"\\s*:\\s*\"\\[([\\d,]*)\\]\"");

        readonly string steamExe;

        public SteamControl(string steamDir) { steamExe = Path.Combine(steamDir, "steam.exe"); }

        static string FetchTargets()
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(DebugList);
                req.Timeout = 2000;
                req.Proxy = null;
                using (WebResponse resp = req.GetResponse())
                using (StreamReader r = new StreamReader(resp.GetResponseStream()))
                    return r.ReadToEnd();
            }
            catch (WebException) { return null; }
        }

        public bool DebugPortOpen() { return FetchTargets() != null; }

        public void RestartInDebugMode()
        {
            Process.Start(steamExe, "-shutdown").Dispose();
            for (int i = 0; i < 60 && IsSteamRunning(); i++) Thread.Sleep(1000);
            if (IsSteamRunning()) throw new InvalidOperationException("Steam did not close");
            Process.Start(steamExe, "-silent -cef-enable-debugging").Dispose();
        }

        static bool IsSteamRunning()
        {
            Process[] steam = Process.GetProcessesByName("steam");
            foreach (Process p in steam) p.Dispose();
            return steam.Length > 0;
        }

        // Waits for a signed-in Steam UI; false if that never happens (e.g. it wants a password).
        public bool WaitUntilReady(TimeSpan timeout, Func<bool> keepGoing)
        {
            DateTime end = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < end && keepGoing())
            {
                string targets = FetchTargets();
                if (targets != null && SharedContext.IsMatch(targets) && !targets.Contains("Sign in to Steam")) return true;
                Thread.Sleep(2000);
            }
            return false;
        }

        // Returns the app IDs in Steam's download list that aren't complete, and resumes them
        // unless readOnly. Scheduled and paused updates are both resumed by ResumeAppUpdate,
        // the same call as the Downloads page's button.
        public string[] ResumePending(bool readOnly)
        {
            string act = readOnly ? "" :
                "SteamClient.Downloads.EnableAllDownloads(true," + LocalClient + ");" +
                "ids.forEach(a=>SteamClient.Downloads.ResumeAppUpdate(a," + LocalClient + "));";
            string js = "new Promise(res=>{const h=SteamClient.Downloads.RegisterForDownloadItems((paused,clients)=>{" +
                "h.unregister();const local=clients.find(c=>c.remote_client_id==='0');" +
                "const ids=(local?local.item_data:[]).filter(i=>!i.completed).map(i=>i.appid);" +
                act + "res(JSON.stringify(ids));});setTimeout(()=>res('timeout'),10000)})";

            Match target = SharedContext.Match(FetchTargets() ?? "");
            if (!target.Success) throw new InvalidOperationException("Steam's debug port is not available");

            string request = "{\"id\":1,\"method\":\"Runtime.evaluate\",\"params\":{\"returnByValue\":true,\"awaitPromise\":true,\"expression\":\""
                + js.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}}";
            string reply = Evaluate(target.Groups[1].Value, request);
            Match m = ResultValue.Match(reply);
            if (!m.Success) throw new InvalidOperationException("Unexpected reply from Steam: " + reply);
            return m.Groups[1].Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        }

        static string Evaluate(string url, string request)
        {
            using (ClientWebSocket ws = new ClientWebSocket())
            using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                ws.ConnectAsync(new Uri(url), cts.Token).Wait();
                byte[] bytes = Encoding.UTF8.GetBytes(request);
                ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token).Wait();
                StringBuilder sb = new StringBuilder();
                byte[] buf = new byte[65536];
                while (true)
                {
                    WebSocketReceiveResult r = ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token).Result;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    if (r.EndOfMessage) return sb.ToString();
                }
            }
        }
    }
}
