using System;
using System.IO;

// Dev tool. gen <out.wav>: 48k mono 16-bit, 1 kHz tone 1s @ -30 dBFS, 1s @ -20, 1s @ -6.
//           ana <in.wav>: per-second RMS / peak in dBFS (supports 16-bit PCM and 32-bit float).
static class WavTool
{
    static int Main(string[] a)
    {
        if (a[0] == "gen")
        {
            int sr = 48000; double[] lv = { -30, -20, -6 };
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            for (int s = 0; s < 3; s++)
                for (int i = 0; i < sr; i++)
                    w.Write((short)(32767 * Math.Pow(10, lv[s] / 20) * Math.Sqrt(2) * Math.Sin(2 * Math.PI * 1000 * i / sr) * 0.999));
            byte[] data = ms.ToArray();
            using (var f = new BinaryWriter(File.Create(a[1])))
            {
                f.Write("RIFF".ToCharArray()); f.Write(36 + data.Length); f.Write("WAVEfmt ".ToCharArray());
                f.Write(16); f.Write((short)1); f.Write((short)1); f.Write(sr); f.Write(sr * 2); f.Write((short)2); f.Write((short)16);
                f.Write("data".ToCharArray()); f.Write(data.Length); f.Write(data);
            }
            return 0;
        }
        if (a[0] == "gate")
        {
            // 10 ms frames: how much is muted, and how often the output flips between muted and open.
            var b = File.ReadAllBytes(a[1]);
            int n = (b.Length - 44) / 2, fr = 480, muted = 0, flips = 0, frames = 0; bool prev = false;
            double peak = 0;
            for (int s = 0; s + fr <= n; s += fr, frames++)
            {
                double sum = 0;
                for (int i = s; i < s + fr; i++) { double v = BitConverter.ToInt16(b, 44 + 2 * i) / 32768.0; sum += v * v; peak = Math.Max(peak, Math.Abs(v)); }
                bool m = 10 * Math.Log10(sum / fr + 1e-20) < -65;
                if (m) muted++;
                if (s > 0 && m != prev) flips++;
                prev = m;
            }
            Console.WriteLine("{0,-28} muted {1,3}%  gate flips/s {2,5:0.0}  peak {3,6:0.0} dBFS",
                Path.GetFileName(a[1]), 100 * muted / frames, flips / (frames / 100.0), 20 * Math.Log10(peak + 1e-20));
            return 0;
        }
        var r = new BinaryReader(File.OpenRead(a[1]));
        r.ReadBytes(12);
        int fmt = 0, ch = 0, rate = 0, bits = 0;
        while (true)
        {
            string id = new string(r.ReadChars(4)); int len = r.ReadInt32();
            if (id == "fmt ") { fmt = r.ReadInt16(); ch = r.ReadInt16(); rate = r.ReadInt32(); r.ReadBytes(6); bits = r.ReadInt16(); r.ReadBytes(len - 16); }
            else if (id == "data")
            {
                if (fmt == 0xFFFE) fmt = bits == 32 ? 3 : 1;
                Console.WriteLine("fmt={0} ch={1} rate={2} bits={3}", fmt, ch, rate, bits);
                int frames = len / (bits / 8) / ch;
                for (int sec = 0; sec * rate < frames; sec++)
                {
                    double sum = 0, peak = 0; int n = Math.Min(rate, frames - sec * rate);
                    for (int i = 0; i < n; i++)
                    {
                        double v = 0;
                        for (int c = 0; c < ch; c++) { double x = fmt == 3 ? r.ReadSingle() : r.ReadInt16() / 32768.0; if (c == 0) v = x; }
                        if (i >= n / 2) { sum += v * v; peak = Math.Max(peak, Math.Abs(v)); } // second half: settled
                    }
                    Console.WriteLine("sec {0}: rms {1:0.0} dBFS  peak {2:0.0} dBFS", sec, 10 * Math.Log10(sum / (n - n / 2) + 1e-20), 20 * Math.Log10(peak + 1e-20));
                }
                return 0;
            }
            else r.ReadBytes(len);
        }
    }
}
