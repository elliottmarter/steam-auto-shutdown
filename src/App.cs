using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SteamAutoShutdown
{
    // Follows content_log.txt, including when Steam rotates it to content_log.previous.txt.
    class LogTail
    {
        readonly string path, previousPath;
        long offset;
        string carry = "";

        public LogTail(string path)
        {
            this.path = path;
            previousPath = Path.Combine(Path.GetDirectoryName(path), "content_log.previous.txt");
        }

        public IEnumerable<string> ReadHistory()
        {
            List<string> lines = new List<string>();
            long ignored = 0;
            string c = "";
            if (File.Exists(previousPath)) ReadFrom(previousPath, ref ignored, ref c, lines);
            carry = "";
            lines.AddRange(ReadNew());
            return lines;
        }

        public List<string> ReadNew()
        {
            List<string> lines = new List<string>();
            if (!File.Exists(path)) return lines;
            if (new FileInfo(path).Length < offset)
            {
                // Rotated: the rest of what we were reading is now in the previous file.
                if (File.Exists(previousPath)) ReadFrom(previousPath, ref offset, ref carry, lines);
                offset = 0;
            }
            ReadFrom(path, ref offset, ref carry, lines);
            return lines;
        }

        static void ReadFrom(string file, ref long pos, ref string partial, List<string> lines)
        {
            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length <= pos) return;
                fs.Seek(pos, SeekOrigin.Begin);
                byte[] buf = new byte[fs.Length - pos];
                int n = fs.Read(buf, 0, buf.Length);
                pos += n;
                string[] parts = (partial + Encoding.UTF8.GetString(buf, 0, n)).Split('\n');
                for (int i = 0; i < parts.Length - 1; i++) lines.Add(parts[i].TrimEnd('\r'));
                partial = parts[parts.Length - 1];
            }
        }
    }

    class Options
    {
        public bool DryRun, AutoArm, CompletePending;
        public bool RealResume;  // with --dry-run: really resume downloads, but still never shut down
        public string LogPath;
        public int GraceSeconds = 30, CountdownSeconds = 30;

        public static Options Parse(string[] args)
        {
            Options o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--dry-run") o.DryRun = true;
                else if (args[i] == "--auto-arm") o.AutoArm = true;
                else if (args[i] == "--complete-pending") o.CompletePending = true;
                else if (args[i] == "--real-resume") o.RealResume = true;
                else if (args[i] == "--log") o.LogPath = args[++i];
                else if (args[i] == "--grace") o.GraceSeconds = int.Parse(args[++i]);
                else if (args[i] == "--countdown") o.CountdownSeconds = int.Parse(args[++i]);
            }
            return o;
        }
    }

    // Steam client colours.
    static class Theme
    {
        public static readonly Color Background = Color.FromArgb(27, 40, 56);
        public static readonly Color Panel = Color.FromArgb(23, 26, 33);
        public static readonly Color Text = Color.FromArgb(199, 213, 224);
        public static readonly Color Accent = Color.FromArgb(102, 192, 244);
        public static readonly Color Warning = Color.FromArgb(255, 140, 90);
        public static readonly Color GreenTop = Color.FromArgb(117, 176, 34);
        public static readonly Color GreenBottom = Color.FromArgb(88, 138, 27);
        public static readonly Color GreyTop = Color.FromArgb(70, 92, 112);
        public static readonly Color GreyBottom = Color.FromArgb(50, 68, 86);
    }

    // Steam's gradient action button: green to go, grey to back out.
    class SteamButton : Button
    {
        bool hover;
        public bool Green = true;

        public SteamButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            MouseEnter += delegate { hover = true; Invalidate(); };
            MouseLeave += delegate { hover = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color top = Green ? Theme.GreenTop : Theme.GreyTop;
            Color bottom = Green ? Theme.GreenBottom : Theme.GreyBottom;
            if (hover)
            {
                top = ControlPaint.Light(top, 0.3f);
                bottom = ControlPaint.Light(bottom, 0.3f);
            }
            using (LinearGradientBrush fill = new LinearGradientBrush(ClientRectangle, top, bottom, LinearGradientMode.Vertical))
                e.Graphics.FillRectangle(fill, ClientRectangle);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // Windows always draws a flat checkbox's square white, so draw the whole control.
    class SteamCheckBox : CheckBox
    {
        public SteamCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Background);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color mark = Enabled ? Theme.Accent : Theme.GreyTop;
            Rectangle square = new Rectangle(1, (Height - 14) / 2, 14, 14);
            using (SolidBrush fill = new SolidBrush(Theme.Panel)) e.Graphics.FillRectangle(fill, square);
            using (Pen border = new Pen(mark)) e.Graphics.DrawRectangle(border, square);
            if (Checked)
                using (Pen tick = new Pen(mark, 2f))
                    e.Graphics.DrawLines(tick, new[] {
                        new Point(square.X + 3, square.Y + 7),
                        new Point(square.X + 6, square.Y + 10),
                        new Point(square.X + 11, square.Y + 4) });
            Rectangle label = new Rectangle(22, 0, Width - 22, Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, label, Enabled ? Theme.Text : Theme.GreyTop,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }

    class MainForm : Form
    {
        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_CAPTION_COLOR = 35;
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        readonly Options opt;
        readonly Tracker tracker;
        readonly LogTail tail;
        readonly bool usingRealSteam;
        readonly string activityLog;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly Label status = new Label();
        readonly ListBox apps = new ListBox();
        readonly SteamCheckBox force = new SteamCheckBox();
        readonly SteamCheckBox pendingBox = new SteamCheckBox();
        readonly SteamButton armButton = new SteamButton();
        readonly NotifyIcon tray = new NotifyIcon();
        SteamControl steamControl;
        int countdown = -1;
        Phase lastPhase = Phase.Idle;
        volatile int armGeneration;
        volatile string pendingStatus;

        public MainForm(Options opt)
        {
            this.opt = opt;
            tracker = new Tracker(TimeSpan.FromSeconds(opt.GraceSeconds));
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamAutoShutdown");
            Directory.CreateDirectory(dir);
            activityLog = Path.Combine(dir, "activity.log");

            string logPath = opt.LogPath;
            usingRealSteam = logPath == null;
            if (usingRealSteam)
            {
                string steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (steam != null)
                {
                    steam = steam.Replace('/', '\\');
                    logPath = Path.Combine(steam, "logs", "content_log.txt");
                    steamControl = new SteamControl(steam);
                }
            }

            BuildUi();
            if (logPath == null || !File.Exists(logPath))
            {
                status.Text = "Could not find Steam's content log. Is Steam installed?";
                armButton.Enabled = false;
                return;
            }
            tail = new LogTail(logPath);
            foreach (string line in tail.ReadHistory()) tracker.Feed(line);

            timer.Tick += delegate { Tick(); };
            timer.Start();
            if (opt.AutoArm) SetArmed(true);
            Tick();
        }

        void BuildUi()
        {
            Text = "Steam Auto Shutdown" + (opt.DryRun ? " [DRY RUN]" : "");
            ClientSize = new Size(480, 300);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);
            Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Icon = appIcon;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;

            status.SetBounds(12, 12, 456, 60);
            status.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            status.BackColor = Color.Transparent;
            apps.SetBounds(12, 76, 456, 110);
            apps.BackColor = Theme.Panel;
            apps.ForeColor = Theme.Text;
            apps.BorderStyle = BorderStyle.None;
            force.SetBounds(12, 192, 456, 24);
            force.Text = "Force-close apps that would block the shutdown (unsaved work is lost)";
            pendingBox.SetBounds(12, 220, 456, 24);
            pendingBox.Text = "Complete any other pending downloads";
            pendingBox.Checked = opt.CompletePending;
            new ToolTip().SetToolTip(pendingBox, "Resumes paused and scheduled updates. Steam restarts once to allow this.");
            armButton.SetBounds(12, 254, 456, 36);
            armButton.Click += delegate { SetArmed(!tracker.Armed); };
            Controls.AddRange(new Control[] { status, apps, force, pendingBox, armButton });
            AcceptButton = armButton;
            tray.Icon = appIcon;
            tray.Text = "Steam Auto Shutdown";
            EventHandler showWindow = delegate { WindowState = FormWindowState.Normal; Activate(); };
            tray.DoubleClick += showWindow;
            tray.BalloonTipClicked += showWindow;
            FormClosed += delegate { tray.Dispose(); };
            SetArmedUi(false);
        }

        void SetArmedUi(bool armed)
        {
            armButton.Text = armed ? "Cancel - keep the PC on" : "Shut down when downloads finish";
            pendingBox.Enabled = !armed;
            tray.Visible = armed;
            armButton.Green = !armed;
            armButton.Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Dark title bar on Windows 10 and 11; the exact caption colour is Windows 11 only.
            int dark = 1;
            DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int caption = ColorTranslator.ToWin32(Theme.Panel);
            DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        }

        void CompletePendingDownloads(int generation)
        {
            Func<bool> stillArmed = () => generation == armGeneration;
            bool readOnly = opt.DryRun && !opt.RealResume;
            try
            {
                if (!steamControl.DebugPortOpen())
                {
                    if (readOnly)
                    {
                        pendingStatus = "DRY RUN: would restart Steam to resume pending downloads.";
                        Log(pendingStatus);
                        return;
                    }
                    object game = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID", 0);
                    if (game is int && (int)game != 0)
                    {
                        pendingStatus = "Pending downloads not started: a game is running.";
                        Log(pendingStatus);
                        return;
                    }
                    pendingStatus = "Restarting Steam to resume pending downloads...";
                    Log(pendingStatus);
                    steamControl.RestartInDebugMode();
                }
                if (!steamControl.WaitUntilReady(TimeSpan.FromMinutes(3), stillArmed))
                {
                    if (stillArmed()) { pendingStatus = "Pending downloads not started: Steam did not finish signing in."; Log(pendingStatus); }
                    return;
                }
                Thread.Sleep(10000);  // Steam's download list fills in shortly after its UI appears
                if (!stillArmed()) return;
                string[] ids = steamControl.ResumePending(readOnly);
                string names = string.Join(", ", ids.Select(AppName));
                pendingStatus = ids.Length == 0 ? "No other pending downloads."
                    : (readOnly ? "DRY RUN: would resume " : "Resumed ") + names;
                Log(pendingStatus);
            }
            catch (Exception e)
            {
                pendingStatus = "Pending downloads not started: " + e.GetBaseException().Message;
                Log(pendingStatus);
            }
        }

        void SetArmed(bool armed)
        {
            countdown = -1;
            armGeneration++;
            pendingStatus = null;
            if (armed) tracker.Arm(); else tracker.Disarm();
            if (armed && pendingBox.Checked && steamControl != null)
            {
                int generation = armGeneration;
                Task.Run(() => CompletePendingDownloads(generation));
            }
            // Without this the PC could go to sleep mid-download and never reach the shutdown.
            SetThreadExecutionState(armed ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);
            SetArmedUi(armed);
            Log(armed ? "Armed" : "Disarmed");
            Tick();
        }

        string ExternalBlocker()
        {
            if (!usingRealSteam) return null;
            Process[] steam = Process.GetProcessesByName("steam");
            foreach (Process p in steam) p.Dispose();
            if (steam.Length == 0) return "Steam is closed";
            object running = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "RunningAppID", 0);
            if (running is int && (int)running != 0) return "A game is running";
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\Apps"))
            {
                if (key != null)
                    foreach (string id in key.GetSubKeyNames())
                    {
                        object u = Registry.GetValue(key.Name + "\\" + id, "Updating", 0);
                        if (u is int && (int)u == 1) return "Steam reports " + AppName(id) + " still updating";
                    }
            }
            return null;
        }

        string AppName(string id)
        {
            string name = usingRealSteam
                ? Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\Apps\" + id, "Name", null) as string
                : null;
            return string.IsNullOrWhiteSpace(name) ? "App " + id : name.Trim();
        }

        void Tick()
        {
            try
            {
                foreach (string line in tail.ReadNew()) tracker.Feed(line);
            }
            catch (IOException) { return; }  // Steam is rotating the log; try again next tick

            Decision d = tracker.Evaluate(DateTime.Now, tracker.Armed ? ExternalBlocker() : null);
            if (d.Phase != lastPhase) { Log("Phase " + lastPhase + " -> " + d.Phase + (d.Blocker != null ? " (" + d.Blocker + ")" : "")); lastPhase = d.Phase; }
            UpdateList();

            if (d.Phase == Phase.Ready)
            {
                if (countdown < 0)
                {
                    countdown = opt.CountdownSeconds;
                    Log("Countdown started");
                    tray.ShowBalloonTip(10000, "PC shutting down in " + countdown + " seconds",
                        "Steam downloads have finished. Click here to open the app and cancel.", ToolTipIcon.Warning);
                }
                status.ForeColor = Theme.Warning;
                status.Text = "Downloads finished.\nShutting down in " + countdown + " seconds.";
                if (countdown == 0) { ShutDown(); return; }
                countdown--;
                return;
            }
            if (countdown >= 0)
            {
                Log("Countdown aborted: activity resumed");
                countdown = -1;
                tracker.ResetSettling();
            }
            status.ForeColor = d.Phase == Phase.Idle ? Theme.Text : Theme.Accent;
            switch (d.Phase)
            {
                case Phase.Idle:
                    status.Text = "Not armed. The PC will stay on.";
                    break;
                case Phase.WaitingForFinish:
                    status.Text = "Armed. Waiting for a Steam download to complete.";
                    if (pendingStatus != null) status.Text += "\n" + pendingStatus;
                    break;
                case Phase.Busy:
                    string detail = d.Blocker ?? (tracker.AnyRunning() && tracker.LastRateMbps.HasValue
                        ? "Last reported speed: " + tracker.LastRateMbps.Value.ToString("0.0") + " Mbps" : "Holding until these finish");
                    status.Text = "Armed. Waiting for Steam.\n" + detail;
                    break;
                case Phase.Settling:
                    status.Text = "Queue looks empty. Confirming for " + d.SecondsLeft + " more seconds.";
                    break;
            }
        }

        void UpdateList()
        {
            string[] rows = tracker.WatchedApps().Select(kv => AppName(kv.Key) + " - " + kv.Value).ToArray();
            if (!rows.SequenceEqual(apps.Items.Cast<string>()))
            {
                apps.Items.Clear();
                apps.Items.AddRange(rows);
            }
        }

        void ShutDown()
        {
            timer.Stop();
            string args = "/s /t 0" + (force.Checked ? " /f" : "");
            if (opt.DryRun)
            {
                Log("DRY RUN: would run shutdown.exe " + args);
                status.Text = "DRY RUN: the PC would shut down now.";
                return;
            }
            Log("Running shutdown.exe " + args);
            Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true });
            status.Text = "Shutting down...";
        }

        void Log(string message)
        {
            try { File.AppendAllText(activityLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine); }
            catch (IOException) { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.Run(new MainForm(Options.Parse(args)));
        }
    }
}
