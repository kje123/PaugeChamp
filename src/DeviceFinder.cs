// Locates HD PVR units on the USB bus and reports which driver each one is bound to.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PaugeChamp
{
    public sealed class DeviceCandidate
    {
        public string InstanceId;
        public ushort ProductId;
        public string Service;        // bound driver service, e.g. "WinUSB" or "hcwhdpvr"
        public string InterfacePath;  // WinUSB device path, null if no interface is registered

        public bool IsWinUsb
        {
            get { return Service != null && Service.Equals("WinUSB", StringComparison.OrdinalIgnoreCase); }
        }
    }

    public static class DeviceFinder
    {
        public const ushort VendorId = 0x2040;
        public static readonly ushort[] ProductIds = { 0x4900, 0x4901, 0x4902, 0x4903, 0x4982 };

        // Interface GUID we register when WinUSB was bound without one (e.g. picked by hand in Device Manager).
        public static readonly Guid DefaultInterfaceGuid = new Guid("8f3b2d61-5c4e-4a7b-9e12-04a5c7d01212");

        public static List<DeviceCandidate> Find()
        {
            var result = new List<DeviceCandidate>();
            IntPtr set = Native.SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero,
                Native.DIGCF_ALLCLASSES | Native.DIGCF_PRESENT);
            if (set == Native.INVALID_HANDLE_VALUE)
                return result;
            try
            {
                var data = new Native.SP_DEVINFO_DATA();
                data.cbSize = (uint)Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));
                for (uint i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    ushort pid;
                    string[] hwIds = GetMultiSz(set, ref data, Native.SPDRP_HARDWAREID);
                    if (!MatchHardwareId(hwIds, out pid))
                        continue;

                    var c = new DeviceCandidate();
                    c.ProductId = pid;
                    c.InstanceId = GetInstanceId(set, ref data);
                    string[] svc = GetMultiSz(set, ref data, Native.SPDRP_SERVICE);
                    c.Service = svc.Length > 0 ? svc[0] : null;
                    if (c.IsWinUsb)
                    {
                        foreach (Guid g in GetInterfaceGuids(set, ref data))
                        {
                            c.InterfacePath = FindInterfacePath(g, pid);
                            if (c.InterfacePath != null)
                                break;
                        }
                    }
                    result.Add(c);
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(set);
            }
            return result;
        }

        static bool MatchHardwareId(string[] hwIds, out ushort pid)
        {
            pid = 0;
            foreach (string id in hwIds)
            {
                string u = id.ToUpperInvariant();
                int v = u.IndexOf("VID_2040&PID_", StringComparison.Ordinal);
                if (v < 0 || u.Length < v + 17)
                    continue;
                ushort p;
                if (!ushort.TryParse(u.Substring(v + 13, 4), System.Globalization.NumberStyles.HexNumber, null, out p))
                    continue;
                if (Array.IndexOf(ProductIds, p) >= 0)
                {
                    pid = p;
                    return true;
                }
            }
            return false;
        }

        static string[] GetMultiSz(IntPtr set, ref Native.SP_DEVINFO_DATA data, uint prop)
        {
            uint type, size;
            var buf = new byte[2048];
            if (!Native.SetupDiGetDeviceRegistryProperty(set, ref data, prop, out type, buf, (uint)buf.Length, out size))
                return new string[0];
            string s = Encoding.Unicode.GetString(buf, 0, (int)size);
            return s.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }

        static string GetInstanceId(IntPtr set, ref Native.SP_DEVINFO_DATA data)
        {
            var sb = new StringBuilder(512);
            int req;
            return Native.SetupDiGetDeviceInstanceId(set, ref data, sb, sb.Capacity, out req) ? sb.ToString() : null;
        }

        static List<Guid> GetInterfaceGuids(IntPtr set, ref Native.SP_DEVINFO_DATA data)
        {
            var guids = new List<Guid>();
            IntPtr hkey = Native.SetupDiOpenDevRegKey(set, ref data, Native.DICS_FLAG_GLOBAL, 0, Native.DIREG_DEV, Native.KEY_READ);
            if (hkey == Native.INVALID_HANDLE_VALUE)
                return guids;
            using (var key = RegistryKey.FromHandle(new SafeRegistryHandle(hkey, true)))
            {
                var values = new List<string>();
                var multi = key.GetValue("DeviceInterfaceGUIDs") as string[];
                if (multi != null)
                    values.AddRange(multi);
                var single = key.GetValue("DeviceInterfaceGUID") as string;
                if (single != null)
                    values.Add(single);
                foreach (string v in values)
                {
                    try { guids.Add(new Guid(v.Trim())); }
                    catch (FormatException) { }
                }
            }
            return guids;
        }

        static string FindInterfacePath(Guid guid, ushort pid)
        {
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == Native.INVALID_HANDLE_VALUE)
                return null;
            try
            {
                string want = string.Format("vid_2040&pid_{0:x4}", pid);
                var ifData = new Native.SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = (uint)Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifData); i++)
                {
                    uint required;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required == 0)
                        continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 8 on x64, 6 on x86.
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref ifData, detail, required, out required, IntPtr.Zero))
                            continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                        if (path != null && path.ToLowerInvariant().Contains(want))
                            return path;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(set);
            }
            return null;
        }

        /// <summary>
        /// Writes a DeviceInterfaceGUIDs value for a WinUSB-bound HD PVR and restarts it so WinUSB
        /// registers a device interface. Requires administrator rights.
        /// </summary>
        public static string RegisterInterface(string instanceId)
        {
            IntPtr set = Native.SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero,
                Native.DIGCF_ALLCLASSES | Native.DIGCF_PRESENT);
            if (set == Native.INVALID_HANDLE_VALUE)
                return "Could not enumerate USB devices.";
            try
            {
                var data = new Native.SP_DEVINFO_DATA();
                data.cbSize = (uint)Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));
                for (uint i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    if (!string.Equals(GetInstanceId(set, ref data), instanceId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    IntPtr hkey = Native.SetupDiOpenDevRegKey(set, ref data, Native.DICS_FLAG_GLOBAL, 0,
                        Native.DIREG_DEV, Native.KEY_ALL_ACCESS);
                    if (hkey == Native.INVALID_HANDLE_VALUE)
                        return "Could not open the device registry key (error " + Marshal.GetLastWin32Error() + ").";
                    using (var key = RegistryKey.FromHandle(new SafeRegistryHandle(hkey, true)))
                    {
                        key.SetValue("DeviceInterfaceGUIDs", new[] { DefaultInterfaceGuid.ToString("B") },
                            RegistryValueKind.MultiString);
                    }
                    return RestartDevice(instanceId);
                }
                return "Device " + instanceId + " not found.";
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(set);
            }
        }

        static string RestartDevice(string instanceId)
        {
            var psi = new ProcessStartInfo("pnputil.exe", "/restart-device \"" + instanceId + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                return p.ExitCode == 0 ? null : "pnputil failed: " + output.Trim();
            }
        }
    }
}
