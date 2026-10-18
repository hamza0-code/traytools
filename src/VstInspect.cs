using System;
using System.Runtime.InteropServices;
using System.Text;

// Dev tool: list a VST2 plugin's parameter names and how normalized values map to displayed values.
// Usage: VstInspect.exe <plugin.dll> [name=value ...]   (sets params first, then prints)
static class VstInspect
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr HostCallback(IntPtr effect, int opcode, int index, IntPtr value, IntPtr ptr, float opt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr PluginMain(HostCallback cb);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr Dispatcher(IntPtr effect, int opcode, int index, IntPtr value, IntPtr ptr, float opt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void SetParam(IntPtr effect, int index, float v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate float GetParam(IntPtr effect, int index);

    [StructLayout(LayoutKind.Sequential)]
    struct AEffect
    {
        public int magic; public IntPtr dispatcher, process, setParameter, getParameter;
        public int numPrograms, numParams, numInputs, numOutputs, flags;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string p);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr m, string n);

    static HostCallback host = (e, op, i, v, p, o) => op == 1 ? new IntPtr(2400) : IntPtr.Zero;
    static Dispatcher disp; static IntPtr eff;

    static string Str(int op, int idx)
    {
        IntPtr buf = Marshal.AllocHGlobal(256);
        for (int i = 0; i < 256; i++) Marshal.WriteByte(buf, i, 0);
        disp(eff, op, idx, IntPtr.Zero, buf, 0);
        string s = Marshal.PtrToStringAnsi(buf);
        Marshal.FreeHGlobal(buf);
        return s.Trim();
    }

    static int Main(string[] args)
    {
        IntPtr lib = LoadLibrary(args[0]);
        if (lib == IntPtr.Zero) { Console.WriteLine("load failed " + Marshal.GetLastWin32Error()); return 1; }
        IntPtr entry = GetProcAddress(lib, "VSTPluginMain");
        if (entry == IntPtr.Zero) entry = GetProcAddress(lib, "main");
        eff = ((PluginMain)Marshal.GetDelegateForFunctionPointer(entry, typeof(PluginMain)))(host);
        var a = (AEffect)Marshal.PtrToStructure(eff, typeof(AEffect));
        disp = (Dispatcher)Marshal.GetDelegateForFunctionPointer(a.dispatcher, typeof(Dispatcher));
        var set = (SetParam)Marshal.GetDelegateForFunctionPointer(a.setParameter, typeof(SetParam));
        var get = (GetParam)Marshal.GetDelegateForFunctionPointer(a.getParameter, typeof(GetParam));
        disp(eff, 0, 0, IntPtr.Zero, IntPtr.Zero, 0);           // effOpen
        disp(eff, 10, 0, IntPtr.Zero, IntPtr.Zero, 48000f);     // effSetSampleRate
        Console.WriteLine("inputs={0} outputs={1} params={2}", a.numInputs, a.numOutputs, a.numParams);

        for (int k = 1; k < args.Length; k++)
        {
            int eq = args[k].LastIndexOf('=');
            string name = args[k].Substring(0, eq);
            float v = float.Parse(args[k].Substring(eq + 1), System.Globalization.CultureInfo.InvariantCulture);
            for (int i = 0; i < a.numParams; i++) if (Str(8, i) == name) set(eff, i, v);
        }

        for (int i = 0; i < a.numParams; i++)
        {
            float cur = get(eff, i);
            var sb = new StringBuilder();
            sb.AppendFormat("[{0}] \"{1}\" = {2:0.####} -> {3} {4}", i, Str(8, i), cur, Str(7, i), Str(6, i));
            if (args.Length == 1)
            {
                sb.Append("   | sweep:");
                foreach (float t in new[] { 0f, 0.1f, 0.25f, 0.5f, 0.75f, 0.9f, 1f })
                { set(eff, i, t); sb.AppendFormat(" {0}:{1}", t, Str(7, i)); }
                set(eff, i, cur);
            }
            Console.WriteLine(sb);
        }
        return 0;
    }
}
