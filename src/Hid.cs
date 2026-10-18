using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace TrayTools
{
    public class HidInfo
    {
        public string Path;
        public ushort Vid, Pid, UsagePage, Usage;
        public int InputLen, OutputLen, FeatureLen;
    }

    // Minimal Win32 HID access: enumerate interfaces, send output reports, read input reports with a timeout.
    public static class Hid
    {
        const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        const int ERROR_IO_PENDING = 997;
        const uint WAIT_OBJECT_0 = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
        [DllImport("hid.dll")] static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] static extern bool HidD_FlushQueue(SafeFileHandle h);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, IntPtr read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out int transferred, bool wait);
        [DllImport("kernel32.dll")] static extern bool CancelIo(SafeFileHandle h);
        [DllImport("kernel32.dll")] static extern IntPtr CreateEvent(IntPtr sec, bool manual, bool initial, string name);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        public static List<HidInfo> Enumerate(ushort vid)
        {
            var list = new List<HidInfo>();
            Guid g;
            HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return list;
            try
            {
                var d = new SP_DEVICE_INTERFACE_DATA();
                d.cbSize = Marshal.SizeOf(d);
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++)
                {
                    int size;
                    SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out size, IntPtr.Zero);
                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, size, out size, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                        HidInfo info = Describe(path);
                        if (info != null && info.Vid == vid) list.Add(info);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        static HidInfo Describe(string path)
        {
            // Zero access rights is enough to query attributes, even for devices the OS holds exclusively.
            using (var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                var a = new HIDD_ATTRIBUTES();
                a.Size = Marshal.SizeOf(a);
                if (!HidD_GetAttributes(h, ref a)) return null;
                var info = new HidInfo { Path = path, Vid = a.VendorID, Pid = a.ProductID };
                IntPtr pre;
                if (HidD_GetPreparsedData(h, out pre))
                {
                    HIDP_CAPS caps;
                    HidP_GetCaps(pre, out caps);
                    HidD_FreePreparsedData(pre);
                    info.UsagePage = caps.UsagePage;
                    info.Usage = caps.Usage;
                    info.InputLen = caps.InputReportByteLength;
                    info.OutputLen = caps.OutputReportByteLength;
                    info.FeatureLen = caps.FeatureReportByteLength;
                }
                return info;
            }
        }

        public static SafeFileHandle Open(string path)
        {
            return CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        }

        public static bool Write(SafeFileHandle h, byte[] report)
        {
            return HidD_SetOutputReport(h, report, report.Length);
        }

        public static void Flush(SafeFileHandle h) { HidD_FlushQueue(h); }

        // Returns the report (including report ID at [0]) or null on timeout.
        public static byte[] Read(SafeFileHandle h, int len, int timeoutMs)
        {
            IntPtr evt = CreateEvent(IntPtr.Zero, true, false, null);
            int ovSize = Marshal.SizeOf(typeof(NativeOverlapped));
            IntPtr ov = Marshal.AllocHGlobal(ovSize);
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                var no = new NativeOverlapped { EventHandle = evt };
                Marshal.StructureToPtr(no, ov, false);
                if (!ReadFile(h, buf, len, IntPtr.Zero, ov) && Marshal.GetLastWin32Error() != ERROR_IO_PENDING)
                    return null;
                int got;
                if (WaitForSingleObject(evt, (uint)timeoutMs) != WAIT_OBJECT_0)
                {
                    CancelIo(h);
                    GetOverlappedResult(h, ov, out got, true); // let the cancel complete before freeing buffers
                    return null;
                }
                if (!GetOverlappedResult(h, ov, out got, false) || got <= 0) return null;
                var result = new byte[got];
                Marshal.Copy(buf, result, 0, got);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                Marshal.FreeHGlobal(ov);
                CloseHandle(evt);
            }
        }
    }
}
