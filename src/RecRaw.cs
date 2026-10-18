using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace TrayTools
{
    // Dev tool: RecRaw.exe <out.wav> <seconds> — records the Leviosa after the user clicks OK.
    static class RecRaw
    {
        [STAThread]
        static int Main(string[] a)
        {
            int sec = int.Parse(a[1]);
            if (MessageBox.Show("Click OK, then talk normally (like on stream) for " + sec + " seconds.\nA second box appears when it's done.",
                "MicFX diagnostic recording", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return 1;
            short[] pcm = WaveIn.Record("Leviosa", sec * 1000);
            using (var f = File.Create(a[0])) WaveIn.ToWav(pcm).CopyTo(f);
            MessageBox.Show("Done, thanks!", "MicFX diagnostic recording");
            return 0;
        }
    }
}
