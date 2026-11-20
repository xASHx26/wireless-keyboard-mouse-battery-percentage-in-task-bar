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
                    // Allow the receiver time to populate telemetry. Prefer a live reading.
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

                        // AJ-series firmware may expose the same status as feature report 05.
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

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_Interface_List_Size(out uint length, ref Guid type, string id, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_Interface_List(ref Guid type, string id, [Out] char[] list, uint length, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
            uint disposition, uint attributes, IntPtr template);
        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr descriptor);
        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_FreePreparsedData(IntPtr descriptor);
        // HIDP_CAPS contains 32 consecutive USHORT fields, including its reserved array.
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







