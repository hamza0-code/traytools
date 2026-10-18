using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrayTools
{
    // Tray icon showing the battery % of a VXE R1 Pro / Pro Max (Compx receiver 3554:F58A, wired 3554:F58C).
    class MouseBatteryApp : ApplicationContext
    {
        const ushort Vid = 0x3554;
        static readonly ushort[] Pids = { 0xF58A, 0xF58C };
        const string AppName = "VXE Battery";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly ToolStripMenuItem startupItem;
        readonly SynchronizationContext ui;
        int lastPercent = -1, warnedAt = 101;
        DateTime lastSeen;
        IntPtr currentHicon = IntPtr.Zero;
        int busy;

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        struct Reading { public bool Found, Asleep, Wired; public int Percent, MilliVolts; }

        [STAThread]
        static void Main()
        {
            bool created;
            using (new Mutex(true, "TrayTools.VxeBattery", out created))
            {
                if (!created) return;
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.Run(new MouseBatteryApp());
            }
        }

        MouseBatteryApp()
        {
            ui = new WindowsFormsSynchronizationContext();
            var menu = new ContextMenuStrip();
            menu.Items.Add("Refresh now", null, delegate { Poll(); });
            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { ToggleStartup(); });
            startupItem.Checked = IsStartup();
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { tray.Visible = false; Application.Exit(); });

            tray.ContextMenuStrip = menu;
            tray.Text = AppName;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Poll(); };
            SetIcon("-", Color.Gray);
            tray.Visible = true;

            timer.Interval = 60 * 1000;
            timer.Tick += delegate { Poll(); };
            timer.Start();
            Poll();
        }

        void Poll()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Reading r;
                try { r = Query(); } catch { r = new Reading(); }
                ui.Post(delegate { Show(r); Interlocked.Exchange(ref busy, 0); }, null);
            });
        }

        static Reading Query()
        {
            var r = new Reading();
            var devs = Hid.Enumerate(Vid);
            // Prefer the wired PID if the mouse is plugged in by cable, else the wireless receiver.
            var dev = devs.Where(d => Pids.Contains(d.Pid) && d.UsagePage == 0xFF02 && d.Usage == 0x0002)
                          .OrderByDescending(d => d.Pid).FirstOrDefault();
            if (dev == null) return r;
            r.Found = true;
            using (var h = Hid.Open(dev.Path))
            {
                if (h.IsInvalid) return r;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    Hid.Flush(h);
                    var req = new byte[dev.OutputLen];
                    req[0] = 0x08; req[1] = 0x04; req[req.Length - 1] = 0x49;
                    if (!Hid.Write(h, req)) continue;
                    for (int i = 0; i < 4; i++)
                    {
                        var res = Hid.Read(h, dev.InputLen, 400);
                        if (res == null) break;
                        if (res.Length < 10 || res[0] != 0x08 || res[1] != 0x04) continue;
                        int pct = res[6];
                        if (pct == 0 || pct > 100) { r.Asleep = true; return r; } // receiver answers but mouse is off/asleep
                        r.Percent = pct;
                        r.Wired = res[7] != 0;
                        r.MilliVolts = (res[8] << 8) | res[9];
                        r.Asleep = false;
                        return r;
                    }
                    Thread.Sleep(200);
                }
            }
            r.Asleep = true;
            return r;
        }

        void Show(Reading r)
        {
            if (!r.Found)
            {
                SetIcon("x", Color.Gray);
                tray.Text = AppName + ": receiver not found";
                return;
            }
            if (r.Asleep)
            {
                if (lastPercent >= 0)
                {
                    SetIcon(lastPercent.ToString(), Color.Gray);
                    tray.Text = Trim(string.Format("{0}: {1}% (mouse asleep, last seen {2:HH:mm})", AppName, lastPercent, lastSeen));
                }
                else
                {
                    SetIcon("z", Color.Gray);
                    tray.Text = AppName + ": mouse asleep — move it";
                }
                return;
            }

            lastPercent = r.Percent;
            lastSeen = DateTime.Now;
            Color c = r.Wired ? Color.FromArgb(80, 170, 255)
                    : r.Percent <= 15 ? Color.FromArgb(255, 70, 70)
                    : r.Percent <= 30 ? Color.FromArgb(255, 170, 40)
                    : TextColor();
            SetIcon(r.Percent.ToString(), c);
            tray.Text = Trim(string.Format("{0}: {1}%{2}  ({3:0.00} V)", AppName, r.Percent,
                r.Wired ? " — charging" : "", r.MilliVolts / 1000.0));

            if (r.Wired) { warnedAt = 101; return; }
            foreach (int level in new[] { 20, 10, 5 })
            {
                if (r.Percent <= level && warnedAt > level)
                {
                    warnedAt = level;
                    tray.ShowBalloonTip(5000, "Mouse battery low", string.Format("VXE R1 Pro is at {0}%. Plug it in soon.", r.Percent), ToolTipIcon.Warning);
                    break;
                }
            }
        }

        static string Trim(string s) { return s.Length > 63 ? s.Substring(0, 63) : s; }

        static Color TextColor()
        {
            // Match the taskbar theme: white text on a dark taskbar, near-black on a light one.
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    if (v is int && (int)v == 1) return Color.FromArgb(20, 20, 20);
                }
            }
            catch { }
            return Color.White;
        }

        void SetIcon(string text, Color color)
        {
            int size = SystemInformation.SmallIconSize.Width;
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(color))
            {
                g.Clear(Color.Transparent);
                // Pixel-block font: glyphs on a 5-row grid, scaled by a whole number so edges stay crisp.
                var glyphs = text.Select(ch => Glyphs.ContainsKey(ch) ? Glyphs[ch] : Glyphs['-']).ToList();
                int cols = glyphs.Sum(gl => gl[0].Length) + glyphs.Count - 1;
                int scale = Math.Max(1, Math.Min(size / cols, size / 5));
                int x = (size - cols * scale) / 2, y = (size - 5 * scale) / 2;
                foreach (var gl in glyphs)
                {
                    for (int r = 0; r < 5; r++)
                        for (int c = 0; c < gl[r].Length; c++)
                            if (gl[r][c] == '#') g.FillRectangle(brush, x + c * scale, y + r * scale, scale, scale);
                    x += (gl[0].Length + 1) * scale;
                }
                IntPtr h = bmp.GetHicon();
                tray.Icon = Icon.FromHandle(h);
                if (currentHicon != IntPtr.Zero) DestroyIcon(currentHicon);
                currentHicon = h;
            }
        }

        static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
        {
            { '0', new[] { "###", "#.#", "#.#", "#.#", "###" } },
            { '1', new[] { ".#", "##", ".#", ".#", ".#" } },
            { '2', new[] { "###", "..#", "###", "#..", "###" } },
            { '3', new[] { "###", "..#", "###", "..#", "###" } },
            { '4', new[] { "#.#", "#.#", "###", "..#", "..#" } },
            { '5', new[] { "###", "#..", "###", "..#", "###" } },
            { '6', new[] { "###", "#..", "###", "#.#", "###" } },
            { '7', new[] { "###", "..#", "..#", "..#", "..#" } },
            { '8', new[] { "###", "#.#", "###", "#.#", "###" } },
            { '9', new[] { "###", "#.#", "###", "..#", "###" } },
            { 'z', new[] { "###", "..#", ".#.", "#..", "###" } },
            { 'x', new[] { "#.#", "#.#", ".#.", "#.#", "#.#" } },
            { '-', new[] { "...", "...", "###", "...", "..." } },
        };

        static bool IsStartup()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(AppName) != null;
        }

        void ToggleStartup()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (IsStartup()) k.DeleteValue(AppName, false);
                else k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
            }
            startupItem.Checked = IsStartup();
        }
    }
}
