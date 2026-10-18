using System;
using System.Linq;
using System.Threading;

namespace TrayTools
{
    static class Probe
    {
        static void Main()
        {
            foreach (var d in Hid.Enumerate(0x3554))
                Console.WriteLine("{0:X4}:{1:X4} page={2:X4} usage={3:X4} in={4} out={5} feat={6}",
                    d.Vid, d.Pid, d.UsagePage, d.Usage, d.InputLen, d.OutputLen, d.FeatureLen);

            var dev = Hid.Enumerate(0x3554).FirstOrDefault(x => x.UsagePage == 0xFF02 && x.Usage == 0x0002);
            if (dev == null) { Console.WriteLine("vendor collection not found"); return; }
            using (var h = Hid.Open(dev.Path))
            {
                if (h.IsInvalid) { Console.WriteLine("open failed"); return; }
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    Hid.Flush(h);
                    var req = new byte[dev.OutputLen];
                    req[0] = 0x08; req[1] = 0x04; req[16] = 0x49;
                    Console.WriteLine("write: " + Hid.Write(h, req));
                    for (int i = 0; i < 4; i++)
                    {
                        var r = Hid.Read(h, dev.InputLen, 500);
                        Console.WriteLine(r == null ? "  (timeout)" : "  " + BitConverter.ToString(r));
                    }
                    Thread.Sleep(300);
                }
            }
        }
    }
}
