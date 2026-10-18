using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrayTools
{
    // Tray app that drives Equalizer APO's capture chain for the Fantech Leviosa.
    // Equalizer APO's config.txt includes C:\ProgramData\MicFX\active.txt, which this app rewrites;
    // Equalizer APO picks up changes live.
    class MicFxApp : ApplicationContext
    {
        const string AppName = "MicFX";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string DeviceMatch = "Fantech Leviosa";
        static readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"MicFX\active.txt");
        static readonly string ApoDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO");
        static readonly string RnNoise = Path.Combine(ApoDir, @"VSTPlugins\rnnoise_mono.dll");
        static readonly string ReaComp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"VSTPlugins\ReaPlugs\reacomp-standalone.dll");

        static readonly string[] Presets = { "Streamer", "Natural", "Max noise kill", "Bypass" };
        static readonly Color[] PresetColors = { Color.FromArgb(145, 70, 255), Color.FromArgb(60, 170, 255), Color.FromArgb(255, 90, 90), Color.Gray };

        readonly NotifyIcon tray = new NotifyIcon();
        readonly SynchronizationContext ui;
        readonly List<ToolStripMenuItem> presetItems = new List<ToolStripMenuItem>();
        readonly ToolStripMenuItem gainItem, startupItem;
        string preset = "Streamer";
        double gainDb = 0;
        IntPtr currentHicon = IntPtr.Zero;
        int busy;

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            bool created;
            using (new Mutex(true, "TrayTools.MicFX", out created))
            {
                if (!created) return;
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.Run(new MicFxApp());
            }
        }

        MicFxApp()
        {
            ui = new WindowsFormsSynchronizationContext();
            LoadState();

            var menu = new ContextMenuStrip();
            foreach (string p in Presets)
            {
                string name = p;
                var item = new ToolStripMenuItem(name, null, delegate { preset = name; Apply(); });
                presetItems.Add(item);
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Calibrate to my voice…", null, delegate { Calibrate(); });
            menu.Items.Add("Record 5 s test and play it back", null, delegate { TestRecording(); });
            gainItem = new ToolStripMenuItem("Input gain");
            foreach (double g in new double[] { -6, -3, 0, 3, 6, 9, 12, 15, 18 })
            {
                double v = g;
                gainItem.DropDownItems.Add(new ToolStripMenuItem(Db(v), null, delegate { gainDb = v; Apply(); }));
            }
            menu.Items.Add(gainItem);
            var tone = new ToolStripMenuItem("Tone");
            tone.DropDownItems.Add(ToneMenu("Warmth / depth", new double[] { 0, 2, 3, 4, 5, 6, 7, 8 }, () => warmth, v => warmth = v));
            tone.DropDownItems.Add(ToneMenu("Nasal cut", new double[] { 0, 2, 3, 4, 5, 6, 8 }, () => nasal, v => nasal = v));
            tone.DropDownItems.Add(ToneMenu("Brightness", new double[] { -6, -4, -3, -2, -1, 0, 2 }, () => bright, v => bright = v));
            tone.DropDownItems.Add(new ToolStripSeparator());
            tone.DropDownItems.Add("Reset tone", null, delegate { warmth = DefWarmth; nasal = DefNasal; bright = DefBright; Apply(); });
            menu.Items.Add(tone);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Windows recording devices", null, delegate { System.Diagnostics.Process.Start("control.exe", "mmsys.cpl,,1"); });
            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { ToggleStartup(); });
            menu.Items.Add(startupItem);
            menu.Items.Add("Exit", null, delegate { tray.Visible = false; Application.Exit(); });

            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                // Left click toggles between the chosen preset and bypass.
                if (e.Button != MouseButtons.Left) return;
                if (preset == "Bypass") preset = lastActive; else { lastActive = preset; preset = "Bypass"; }
                Apply();
            };
            tray.Visible = true;

            if (!File.Exists(ReaComp) || !File.Exists(RnNoise))
                tray.ShowBalloonTip(8000, AppName, "Equalizer APO plugins missing — run setup\\RUN-SETUP.cmd.", ToolTipIcon.Error);
            Apply();
        }

        string lastActive = "Streamer";
        readonly List<Action> menuRefreshers = new List<Action>();

        ToolStripMenuItem ToneMenu(string label, double[] values, Func<double> get, Action<double> set)
        {
            var parent = new ToolStripMenuItem(label);
            foreach (double v in values)
            {
                double val = v;
                var item = new ToolStripMenuItem(Db(val), null, delegate { set(val); Apply(); });
                parent.DropDownItems.Add(item);
                menuRefreshers.Add(() => item.Checked = Math.Abs(get() - val) < 0.01);
            }
            menuRefreshers.Add(() => parent.Text = label + " (" + Db(get()) + ")");
            return parent;
        }

        static string Db(double v) { return (v > 0 ? "+" : "") + v.ToString("0.#", CultureInfo.InvariantCulture) + " dB"; }
        static string F(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }
        static double Lin(double db) { return Math.Pow(10, db / 20); }

        // ---- Equalizer APO config generation -------------------------------------------------------
        // Normalized VST values were mapped with VstInspect against ReaComp/RNNoise:
        //   ReaComp: Thresh = linear amplitude, Ratio = (r-1)/99, Attack = ms/500, Release = ms/5000,
        //            Knee = dB/24, Pre-comp = ms/250, RMS size = ms/1000.
        //   RNNoise: VAD Threshold = 0..1, Grace = ms/5000 (10 ms units, max 500), Retroactive = ms/100.

        static string RnNoiseLine(double vad, int graceMs, int retroMs)
        {
            return string.Format("VSTPlugin: Library \"{0}\" \"VAD Threshold\" {1} \"VAD Grace Period (10ms per unit)\" {2} \"Retroactive VAD Grace Period (10\" {3}",
                RnNoise, F(vad), F(graceMs / 5000.0), F(retroMs / 100.0));
        }

        static string CompLine(double threshDb, double ratio, double attackMs, double releaseMs, double kneeDb)
        {
            return string.Format("VSTPlugin: Library \"{0}\" Thresh {1} Ratio {2} Attack {3} Release {4} Knee {5} \"RMS size\" 0.005",
                ReaComp, F(Lin(threshDb)), F((ratio - 1) / 99), F(attackMs / 500), F(releaseMs / 5000), F(kneeDb / 24));
        }

        static string LimiterLine(double ceilingDb)
        {
            // Infinite ratio, zero attack, peak detection and 1 ms lookahead: a brickwall peak limiter.
            return string.Format("VSTPlugin: Library \"{0}\" Thresh {1} Ratio 1 Attack 0 Release 0.01 Pre-comp 0.004 \"RMS size\" 0",
                ReaComp, F(Lin(ceilingDb)));
        }

        // Tone controls (dB), shared by all presets. Defaults tuned for a warm, non-nasal voice.
        const double DefWarmth = 5, DefNasal = 4, DefBright = -2;
        double warmth = DefWarmth, nasal = DefNasal, bright = DefBright;

        string BuildConfig(string p, double gain)
        {
            var c = new List<string>();
            c.Add(string.Format(CultureInfo.InvariantCulture, "# MicFX preset={0}|gain={1}|warmth={2}|nasal={3}|bright={4}", p, gain, warmth, nasal, bright));
            c.Add("# Generated by the MicFX tray app; edits here are overwritten.");
            c.Add("Device: " + DeviceMatch);
            if (p == "Bypass") return string.Join("\r\n", c) + "\r\n";

            // RNNoise runs on the raw signal, before any gain: it produces fewer artifacts there.
            if (p == "Streamer") c.Add(RnNoiseLine(0.5, 300, 50));      // gentle AI noise removal
            if (p == "Max noise kill") c.Add(RnNoiseLine(0.9, 200, 50)); // strict; Natural has none, so it is never robotic
            c.Add("Preamp: " + F(gain) + " dB");

            c.Add("Filter: ON HPQ Fc " + (p == "Max noise kill" ? "75" : "55") + " Hz Q 0.707"); // rumble cut, keeps chest
            c.Add("Filter: ON LSC Fc 110 Hz Gain " + F(warmth) + " dB Q 0.7");                   // depth
            c.Add("Filter: ON PK Fc 250 Hz Gain " + F(warmth * 0.5) + " dB Q 0.9");             // low-mid warmth / richness
            c.Add("Filter: ON PK Fc 900 Hz Gain " + F(-nasal) + " dB Q 1.0");                   // nasal honk
            c.Add("Filter: ON PK Fc 1600 Hz Gain " + F(-nasal * 0.4) + " dB Q 2.0");            // nasal edge

            if (p == "Natural") { c.Add(CompLine(-20, 2, 15, 120, 8)); c.Add("Preamp: 3 dB"); }
            else { c.Add(CompLine(-24, 3, 15, 100, 8)); c.Add("Preamp: 6 dB"); }              // slower attack keeps it full, not squashed

            c.Add("Filter: ON PK Fc 7000 Hz Gain -2 dB Q 3");                                   // tame sibilance
            c.Add("Filter: ON HSC Fc 9000 Hz Gain " + F(bright) + " dB Q 0.7");                 // negative = warmer, less cold
            c.Add(LimiterLine(-1));
            return string.Join("\r\n", c) + "\r\n";
        }

        void LoadState()
        {
            try
            {
                string first = File.ReadAllLines(ConfigPath).FirstOrDefault() ?? "";
                if (!first.StartsWith("# MicFX ")) return;
                string body = first.Substring(8);
                foreach (string kv in body.Contains("|") ? body.Split('|') : body.Split(' ')) // old format was space-separated
                {
                    int eq = kv.IndexOf('=');
                    if (eq < 0) continue;
                    string k = kv.Substring(0, eq), v = kv.Substring(eq + 1);
                    double d;
                    bool num = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                    if (k == "preset" && Presets.Contains(v)) preset = v;
                    else if (k == "gain" && num) gainDb = d;
                    else if (k == "warmth" && num) warmth = d;
                    else if (k == "nasal" && num) nasal = d;
                    else if (k == "bright" && num) bright = d;
                }
                if (preset != "Bypass") lastActive = preset;
            }
            catch { }
        }

        void Apply()
        {
            try { WriteConfig(BuildConfig(preset, gainDb)); }
            catch (Exception ex) { tray.ShowBalloonTip(5000, AppName, "Could not write config: " + ex.Message, ToolTipIcon.Error); }
            if (preset != "Bypass") lastActive = preset;
            for (int i = 0; i < Presets.Length; i++) presetItems[i].Checked = Presets[i] == preset;
            foreach (ToolStripMenuItem g in gainItem.DropDownItems) g.Checked = g.Text == Db(gainDb);
            gainItem.Text = "Input gain (" + Db(gainDb) + ")";
            startupItem.Checked = IsStartup();
            foreach (var r in menuRefreshers) r();
            int idx = Array.IndexOf(Presets, preset);
            SetIcon(PresetColors[idx], preset == "Bypass");
            tray.Text = string.Format("{0}: {1}, gain {2}\nLeft-click: toggle bypass", AppName, preset, Db(gainDb));
        }

        static void WriteConfig(string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            string tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, text, Encoding.ASCII);
            if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, null); else File.Move(tmp, ConfigPath);
        }

        // ---- Calibration / test recording ------------------------------------------------------------

        void Calibrate()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            if (MessageBox.Show("After you click OK, talk for 5 seconds at your normal streaming volume and distance.\n\n" +
                                "MicFX will set the input gain so the compressor works in its sweet spot.",
                                AppName + " calibration", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            { busy = 0; return; }

            string keepPreset = preset;
            WriteConfig(BuildConfig("Bypass", 0)); // measure the raw mic
            SetIcon(Color.FromArgb(255, 60, 60), false);
            tray.Text = AppName + ": listening…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(400); // let Equalizer APO reload
                short[] pcm; string err = null;
                try { pcm = WaveIn.Record(DeviceMatch, 5000); } catch (Exception ex) { pcm = null; err = ex.Message; }
                ui.Post(delegate
                {
                    busy = 0;
                    if (pcm == null) { preset = keepPreset; Apply(); MessageBox.Show("Recording failed: " + err, AppName); return; }
                    double speechDb, peakDb;
                    Analyze(pcm, out speechDb, out peakDb);
                    if (double.IsNaN(speechDb))
                    {
                        preset = keepPreset; Apply();
                        MessageBox.Show("I didn't hear any speech. Check the mic isn't muted and try again.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    // Aim for speech around -20 dBFS RMS before the compressor (threshold sits at -24).
                    double g = Math.Round(Math.Max(-10, Math.Min(24, -20 - speechDb)) * 2) / 2;
                    gainDb = g;
                    preset = keepPreset == "Bypass" ? lastActive : keepPreset;
                    Apply();
                    string msg = string.Format(CultureInfo.InvariantCulture,
                        "Your voice measured {0:0.0} dBFS (peaks {1:0.0} dBFS).\nInput gain set to {2}.", speechDb, peakDb, Db(g));
                    if (peakDb > -1.5) msg += "\n\nYour raw signal is clipping. Lower the mic level in Windows recording devices (or turn the gain knob down) and calibrate again.";
                    else if (g >= 18) msg += "\n\nThe raw signal is very quiet. Raise the mic level in Windows to 100% or move closer, then calibrate again for less noise.";
                    MessageBox.Show(msg, AppName + " calibration", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }, null);
            });
        }

        static void Analyze(short[] pcm, out double speechDb, out double peakDb)
        {
            int frame = 48000 / 20; // 50 ms
            var levels = new List<double>();
            double peak = 0;
            for (int s = 0; s + frame <= pcm.Length; s += frame)
            {
                double sum = 0;
                for (int i = s; i < s + frame; i++) { double v = pcm[i] / 32768.0; sum += v * v; peak = Math.Max(peak, Math.Abs(v)); }
                levels.Add(10 * Math.Log10(sum / frame + 1e-12));
            }
            peakDb = 20 * Math.Log10(peak + 1e-12);
            double loudest = levels.Count == 0 ? -120 : levels.Max();
            var speech = levels.Where(l => l > loudest - 20 && l > -60).OrderBy(l => l).ToList();
            speechDb = speech.Count < 6 ? double.NaN : speech[speech.Count / 2]; // median of the active frames
        }

        void TestRecording()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            tray.ShowBalloonTip(3000, AppName, "Recording 5 seconds with the current preset — talk now.", ToolTipIcon.Info);
            SetIcon(Color.FromArgb(255, 60, 60), false);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = null;
                try
                {
                    Thread.Sleep(300);
                    short[] pcm = WaveIn.Record(DeviceMatch, 5000);
                    using (var player = new System.Media.SoundPlayer(WaveIn.ToWav(pcm))) player.PlaySync();
                }
                catch (Exception ex) { err = ex.Message; }
                ui.Post(delegate { busy = 0; Apply(); if (err != null) MessageBox.Show("Test failed: " + err, AppName); }, null);
            });
        }

        // ---- Tray icon / startup ---------------------------------------------------------------------

        void SetIcon(Color color, bool slashed)
        {
            int size = SystemInformation.SmallIconSize.Width;
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                string fontName = new InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
                using (var font = new Font(fontName, size * 0.85f, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(color))
                {
                    var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(slashed ? "\uF781" : "\uE720", font, brush, new RectangleF(0, 0, size, size), fmt);
                }
                IntPtr h = bmp.GetHicon();
                tray.Icon = Icon.FromHandle(h);
                if (currentHicon != IntPtr.Zero) DestroyIcon(currentHicon);
                currentHicon = h;
            }
        }

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

    // Blocking mono 48 kHz 16-bit capture from a named device via the classic waveIn API.
    static class WaveIn
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WAVEINCAPS { public ushort wMid, wPid; public uint vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public uint dwFormats; public ushort wChannels, wReserved1; }

        [DllImport("winmm.dll")] static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int waveInGetDevCapsW(UIntPtr id, ref WAVEINCAPS caps, int size);
        [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, uint id, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);

        public static short[] Record(string deviceMatch, int ms)
        {
            uint id = uint.MaxValue;
            for (uint i = 0; i < waveInGetNumDevs(); i++)
            {
                var caps = new WAVEINCAPS();
                if (waveInGetDevCapsW(new UIntPtr(i), ref caps, Marshal.SizeOf(caps)) == 0 && caps.szPname.Contains("Leviosa")) { id = i; break; }
            }
            if (id == uint.MaxValue) throw new Exception("Fantech Leviosa microphone not found. Is it plugged in?");

            var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = 48000, nAvgBytesPerSec = 96000, nBlockAlign = 2, wBitsPerSample = 16 };
            IntPtr h;
            int rc = waveInOpen(out h, id, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (rc != 0) throw new Exception("waveInOpen failed (" + rc + ")");
            int bytes = 96000 * ms / 1000;
            int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            IntPtr data = Marshal.AllocHGlobal(bytes), hdr = Marshal.AllocHGlobal(hdrSize);
            try
            {
                Marshal.StructureToPtr(new WAVEHDR { lpData = data, dwBufferLength = (uint)bytes }, hdr, false);
                waveInPrepareHeader(h, hdr, hdrSize);
                waveInAddBuffer(h, hdr, hdrSize);
                waveInStart(h);
                var deadline = DateTime.Now.AddMilliseconds(ms + 2000);
                while ((((WAVEHDR)Marshal.PtrToStructure(hdr, typeof(WAVEHDR))).dwFlags & 1) == 0 && DateTime.Now < deadline) Thread.Sleep(50);
                waveInReset(h);
                var done = (WAVEHDR)Marshal.PtrToStructure(hdr, typeof(WAVEHDR));
                waveInUnprepareHeader(h, hdr, hdrSize);
                var pcm = new short[done.dwBytesRecorded / 2];
                Marshal.Copy(data, pcm, 0, pcm.Length);
                return pcm;
            }
            finally
            {
                waveInClose(h);
                Marshal.FreeHGlobal(data);
                Marshal.FreeHGlobal(hdr);
            }
        }

        public static Stream ToWav(short[] pcm)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            int len = pcm.Length * 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + len); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(48000); w.Write(96000); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(len);
            foreach (short s in pcm) w.Write(s);
            ms.Position = 0;
            return ms;
        }
    }
}
