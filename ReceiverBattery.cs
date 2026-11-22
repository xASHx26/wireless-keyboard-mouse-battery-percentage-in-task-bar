using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeripheralBattery
{
    internal static class ReceiverBattery
    {
        // Rongyuan mouse receiver on the AJAZZ AJ179 Pro and Attack Shark R3.
        // Protocol: github.com/qu1ncky/BatteryTray (RongyuanDongleProvider).
        public static bool IsRongyuanMouse(string id)
        {
            return id.StartsWith(@"USB\VID_3151&PID_402D\", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith(@"USB\VID_3151&PID_402D&", StringComparison.OrdinalIgnoreCase);
        }

        // NACODEX / YJX mouse receiver (e.g. NACODEX NJ07 MC, NJ08 MC, etc.).
        public static bool IsNacodexMouse(string id)
        {
            return id.IndexOf("VID_A8A5&PID_2167", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   id.IndexOf("VID_A8A4&PID_2167", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   id.IndexOf("VID_A8A5&PID_2168", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   id.IndexOf("VID_A8A4&PID_2168", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   id.IndexOf("VID_A8A5&PID_2177", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   id.IndexOf("VID_A8A4&PID_2177", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static int? ReadNacodexMouse(string hidId, out bool lastReported)
        {
            lastReported = false;
            if (!IsNacodexMouse(hidId))
                return null;

            if (hidId.IndexOf("Col06", StringComparison.OrdinalIgnoreCase) < 0)
                return null;

            Guid hid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
            uint length;
            if (CM_Get_Device_Interface_List_Size(out length, ref hid, hidId, 0) != 0 || length <= 1)
                return null;
            var paths = new char[length];
            if (CM_Get_Device_Interface_List(ref hid, hidId, paths, length, 0) != 0)
                return null;

            foreach (string path in new string(paths).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
            {
                const uint FILE_FLAG_OVERLAPPED = 0x40000000;
                using (SafeFileHandle handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, FILE_FLAG_OVERLAPPED, IntPtr.Zero))
                {
                    if (handle.IsInvalid) continue;

                    IntPtr descriptor;
                    if (!HidD_GetPreparsedData(handle, out descriptor)) continue;
                    var caps = new ushort[32];
                    try
                    {
                        // caps[0]=Usage (0x10), caps[1]=UsagePage (0xFF01)
                        if (HidP_GetCaps(descriptor, caps) != 0x00110000 || caps[0] != 0x0010 || caps[1] != 0xFF01)
                            continue;
                    }
                    finally
                    {
                        HidD_FreePreparsedData(descriptor);
                    }

                    // Report ID 0xF0, command 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01, 0x01
                    var request = new byte[64];
                    request[0] = 0xF0;
                    request[1] = 0x30;
                    request[2] = 0xA5;
                    request[3] = 0x0B;
                    request[4] = 0x2E;
                    request[5] = 0x01;
                    request[6] = 0x01;
                    request[7] = 0x01;

                    IntPtr writeEvent = CreateEvent(IntPtr.Zero, true, false, null);
                    try
                    {
                        var ovWrite = new OVERLAPPED { hEvent = writeEvent };
                        IntPtr pOvWrite = Marshal.AllocHGlobal(Marshal.SizeOf(ovWrite));
                        try
                        {
                            Marshal.StructureToPtr(ovWrite, pOvWrite, false);
                            uint written;
                            bool ok = WriteFile(handle, request, 64, out written, pOvWrite);
                            if (!ok && Marshal.GetLastWin32Error() == 997) // ERROR_IO_PENDING
                            {
                                if (WaitForSingleObject(writeEvent, 500) == 0)
                                    GetOverlappedResult(handle, pOvWrite, out written, false);
                                else
                                {
                                    CancelIo(handle);
                                    continue;
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pOvWrite);
                        }
                    }
                    finally
                    {
                        CloseHandle(writeEvent);
                    }

                    var reply = new byte[64];
                    IntPtr readEvent = CreateEvent(IntPtr.Zero, true, false, null);
                    try
                    {
                        var ovRead = new OVERLAPPED { hEvent = readEvent };
                        IntPtr pOvRead = Marshal.AllocHGlobal(Marshal.SizeOf(ovRead));
                        try
                        {
                            Marshal.StructureToPtr(ovRead, pOvRead, false);
                            uint read;
                            bool readOk = ReadFile(handle, reply, 64, out read, pOvRead);
                            if (!readOk && Marshal.GetLastWin32Error() == 997)
                            {
                                if (WaitForSingleObject(readEvent, 500) == 0)
                                    readOk = GetOverlappedResult(handle, pOvRead, out read, false);
                                else
                                {
                                    CancelIo(handle);
                                    readOk = false;
                                }
                            }

                            if (readOk && reply[0] == 0xF0 && reply[1] == 0x30 && reply[8] <= 100)
                            {
                                return (int)reply[8];
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pOvRead);
                        }
                    }
                    finally
                    {
                        CloseHandle(readEvent);
                    }
                }
            }

            return null;
        }

        public static int? ReadRongyuanMouse(string hidId, out bool lastReported)
        {
            lastReported = false;
            if (!hidId.StartsWith(@"HID\VID_3151&PID_402D&MI_02", StringComparison.OrdinalIgnoreCase))
                return null;

            Guid hid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
            uint length;
            if (CM_Get_Device_Interface_List_Size(out length, ref hid, hidId, 0) != 0 || length <= 1)
                return null;
            var paths = new char[length];
            if (CM_Get_Device_Interface_List(ref hid, hidId, paths, length, 0) != 0)
                return null;

            foreach (string path in new string(paths).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
            {
                using (SafeFileHandle handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    if (handle.IsInvalid) continue;
                    IntPtr descriptor;
                    if (!HidD_GetPreparsedData(handle, out descriptor)) continue;
                    var caps = new ushort[32];
                    try
                    {
                        if (HidP_GetCaps(descriptor, caps) != 0x00110000 || caps[1] != 0xFFFF || caps[0] != 2 ||
                            caps[4] < 6 || caps[4] > 256) continue;
                    }
                    finally
                    {
                        HidD_FreePreparsedData(descriptor);
                    }

                    // Report ID 0. F7 requests status and changes no device settings.
                    var request = new byte[caps[4]];
                    request[1] = 0xF7;
                    if (!HidD_SetFeature(handle, request, request.Length)) continue;
                    int? retained = null;
                    var reply = new byte[request.Length];
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        System.Threading.Thread.Sleep(attempt == 0 ? 30 : 100);
                        Array.Clear(reply, 0, reply.Length);
                        if (HidD_GetFeature(handle, reply, reply.Length) && reply[0] == 0)
                        {
                            if (reply[1] == 1 && reply[5] == 0 && reply[3] <= 100)
                                return reply[3];

                            if (IsAjSeriesStatus(reply)) retained = reply[3];
                        }

                        Array.Clear(reply, 0, reply.Length);
                        reply[0] = 5;
                        if (HidD_GetFeature(handle, reply, reply.Length) &&
                            reply[0] == 5 && IsAjSeriesStatus(reply))
                            retained = reply[3];
                    }

                    if (retained.HasValue)
                    {
                        lastReported = true;
                        return retained;
                    }
                }
            }
            return null;
        }

        private static bool IsAjSeriesStatus(byte[] reply)
        {
            return reply.Length >= 8 && reply[1] == 0 && reply[2] == 0 &&
                reply[3] > 0 && reply[3] <= 100 &&
                reply[4] == 1 && reply[5] == 1 && reply[6] == 1 && reply[7] == 2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct OVERLAPPED
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint Offset;
            public uint OffsetHigh;
            public IntPtr hEvent;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_Interface_List_Size(out uint length, ref Guid type, string id, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_Interface_List(ref Guid type, string id, [Out] char[] list, uint length, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
            uint disposition, uint attributes, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteFile(SafeFileHandle h, byte[] buf, uint len, out uint written, IntPtr ovl);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(SafeFileHandle h, [Out] byte[] buf, uint len, out uint read, IntPtr ovl);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ovl, out uint read, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CancelIo(SafeFileHandle h);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEvent(IntPtr sec, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr descriptor);
        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_FreePreparsedData(IntPtr descriptor);
        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr descriptor, [Out] ushort[] caps);
        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] report, int length);
        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetFeature(SafeFileHandle handle, [In, Out] byte[] report, int length);
    }
}
