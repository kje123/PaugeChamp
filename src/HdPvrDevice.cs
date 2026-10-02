// HD PVR (model 1212) USB protocol over WinUSB.
//
// The command set, authorization handshake and stream start/stop sequence follow the Linux
// kernel driver (drivers/media/usb/hdpvr, GPL-2.0), the best public description of the device.
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PaugeChamp
{
    public class HdPvrException : Exception
    {
        public readonly int Win32Error;

        public HdPvrException(string message, int win32Error)
            : base(win32Error != 0 ? message + " (Windows error " + win32Error + ")" : message)
        {
            Win32Error = win32Error;
        }

        /// <summary>True when the error means the unit is gone (unplugged, powered off, driver changed).</summary>
        public bool IsDisconnect
        {
            get
            {
                return Win32Error == Native.ERROR_DEVICE_NOT_CONNECTED || Win32Error == Native.ERROR_GEN_FAILURE ||
                       Win32Error == Native.ERROR_BAD_COMMAND || Win32Error == Native.ERROR_FILE_NOT_FOUND;
            }
        }
    }

    public struct SignalInfo
    {
        public int Width, Height, Fps;

        public bool Valid { get { return Width > 0 && Height > 0 && Fps > 0; } }

        public override string ToString()
        {
            return Valid ? string.Format("{0}x{1} @ {2} Hz", Width, Height, Fps) : "no signal";
        }
    }

    public sealed class HdPvrDevice : IDisposable
    {
        // Vendor requests. Request type 0x38 / 0xB8 is what the device firmware expects.
        const byte ReqTypeOut = 0x38;
        const byte ReqTypeIn = 0xB8;
        const byte ReqConfig = 0x01;
        const byte ReqStatus = 0x81;
        const byte ReqAuthResponse = 0xD1;
        const byte ReqFanLeds = 0xD4;
        const byte ReqAudioBoost = 0xD5;
        const byte ReqEncoderStart = 0xB8;
        const ushort DefaultIndex = 0x0003;

        const ushort CtrlStartStreaming = 0x0700;
        const ushort CtrlStopStreaming = 0x0800;
        const ushort CtrlBitrate = 0x1000;
        const ushort CtrlBitrateMode = 0x1200;
        const ushort CtrlGopMode = 0x1300;
        const ushort CtrlVideoInfo = 0x1400;
        const ushort CtrlVideoInput = 0x1500;
        const ushort CtrlVideoStandard = 0x1700;
        const ushort CtrlAudioInput = 0x2500;
        const ushort CtrlBrightness = 0x2900;
        const ushort CtrlContrast = 0x2A00;
        const ushort CtrlHue = 0x2B00;
        const ushort CtrlSaturation = 0x2C00;
        const ushort CtrlSharpness = 0x2D00;
        const ushort CtrlLowPassFilter = 0x3100;
        const ushort CtrlFirmwareInfo = 0x0400;

        const byte SimpleIdrGop = 1;

        // Linux notes the USB descriptor's 512-byte max packet is wrong; the Windows driver reads 8 KiB.
        public const int BulkChunk = 8192;

        SafeFileHandle file;
        IntPtr usb;
        byte bulkIn;

        public int FirmwareVersion { get; private set; }
        public string FirmwareDate { get; private set; }
        public bool SupportsAc3 { get { return FirmwareVersion >= 0x0d; } }
        internal IntPtr UsbHandle { get { return usb; } }
        internal byte BulkInPipe { get { return bulkIn; } }

        public static HdPvrDevice Open(string interfacePath)
        {
            var dev = new HdPvrDevice();
            try
            {
                dev.file = Native.CreateFile(interfacePath, Native.GENERIC_READ | Native.GENERIC_WRITE,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                    Native.FILE_ATTRIBUTE_NORMAL | Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (dev.file.IsInvalid)
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new HdPvrException(err == Native.ERROR_ACCESS_DENIED
                        ? "The HD PVR is in use by another program" : "Could not open the HD PVR", err);
                }
                if (!Native.WinUsb_Initialize(dev.file, out dev.usb))
                    throw new HdPvrException("WinUSB initialization failed", Marshal.GetLastWin32Error());
                dev.FindBulkIn();

                uint ctrlTimeout = 10000;
                Native.WinUsb_SetPipePolicy(dev.usb, 0, Native.PIPE_TRANSFER_TIMEOUT, 4, ref ctrlTimeout);
                byte on = 1;
                Native.WinUsb_SetPipePolicy(dev.usb, dev.bulkIn, Native.AUTO_CLEAR_STALL, 1, ref on);
                dev.SetReadTimeout(0);
                return dev;
            }
            catch
            {
                dev.Dispose();
                throw;
            }
        }

        void FindBulkIn()
        {
            Native.USB_INTERFACE_DESCRIPTOR ifd;
            if (!Native.WinUsb_QueryInterfaceSettings(usb, 0, out ifd))
                throw new HdPvrException("Could not read the USB interface descriptor", Marshal.GetLastWin32Error());
            for (byte i = 0; i < ifd.bNumEndpoints; i++)
            {
                Native.WINUSB_PIPE_INFORMATION pipe;
                if (Native.WinUsb_QueryPipe(usb, 0, i, out pipe) &&
                    pipe.PipeType == Native.UsbdPipeTypeBulk && (pipe.PipeId & 0x80) != 0)
                {
                    bulkIn = pipe.PipeId;
                    return;
                }
            }
            throw new HdPvrException("The HD PVR has no bulk-in endpoint", 0);
        }

        internal void SetReadTimeout(uint ms)
        {
            Native.WinUsb_SetPipePolicy(usb, bulkIn, Native.PIPE_TRANSFER_TIMEOUT, 4, ref ms);
        }

        // ---- control transfers ----

        void ControlOut(byte request, ushort value, ushort index, byte[] data)
        {
            var setup = new Native.WINUSB_SETUP_PACKET();
            setup.RequestType = ReqTypeOut;
            setup.Request = request;
            setup.Value = value;
            setup.Index = index;
            setup.Length = (ushort)(data == null ? 0 : data.Length);
            uint sent;
            if (!Native.WinUsb_ControlTransfer(usb, setup, data, setup.Length, out sent, IntPtr.Zero))
                throw new HdPvrException(string.Format("Control request 0x{0:X2}/0x{1:X4} failed", request, value),
                    Marshal.GetLastWin32Error());
        }

        byte[] ControlIn(byte request, ushort value, ushort index, int length)
        {
            var setup = new Native.WINUSB_SETUP_PACKET();
            setup.RequestType = ReqTypeIn;
            setup.Request = request;
            setup.Value = value;
            setup.Index = index;
            setup.Length = (ushort)length;
            var buf = new byte[length];
            uint got;
            if (!Native.WinUsb_ControlTransfer(usb, setup, buf, (uint)length, out got, IntPtr.Zero))
                throw new HdPvrException(string.Format("Status request 0x{0:X4} failed", value), Marshal.GetLastWin32Error());
            if (got < length)
                Array.Resize(ref buf, (int)got);
            return buf;
        }

        void Config(ushort value, byte arg)
        {
            ControlOut(ReqConfig, value, DefaultIndex, new[] { arg });
        }

        // ---- initialization ----

        /// <summary>
        /// Reads firmware info and answers the device's challenge. The encoder refuses to work until
        /// this handshake is done after every power-up / re-enumeration.
        /// </summary>
        public void Authorize()
        {
            byte[] info = ControlIn(ReqStatus, CtrlFirmwareInfo, DefaultIndex, 46);
            if (info.Length != 46)
                throw new HdPvrException("Unexpected firmware status length " + info.Length, 0);
            FirmwareVersion = info[1];
            int end = Array.IndexOf(info, (byte)0, 2);
            if (end < 0 || end > 38) end = 38;
            FirmwareDate = Encoding.ASCII.GetString(info, 2, end - 2).Trim();

            var response = new byte[8];
            Array.Copy(info, 38, response, 0, 8);
            Challenge(response);
            Thread.Sleep(100);
            ControlOut(ReqAuthResponse, 0, 0, response);
        }

        /// <summary>Transforms the 8 challenge bytes into the response the firmware expects.</summary>
        internal static void Challenge(byte[] b)
        {
            for (int idx = 0; idx < 32; ++idx)
            {
                if ((idx & 0x3) != 0)
                    b[(idx >> 3) + 3] = b[(idx >> 2) & 0x3];

                switch (idx & 0x3)
                {
                    case 0x3:
                        b[2] = (byte)(b[2] + b[3] * 4 + b[4] + b[5]);
                        b[4] = (byte)(b[4] + b[(idx & 0x1) * 2] * 9 + 9);
                        break;
                    case 0x1:
                        b[0] = (byte)(b[0] * 8);
                        b[0] = (byte)(b[0] + 7 * idx + 4);
                        b[6] = (byte)(b[6] + b[3] * 3);
                        break;
                    case 0x0:
                        b[3 - (idx >> 3)] = b[idx >> 2];
                        b[5] = (byte)(b[5] + b[6] * 3);
                        for (int i = 0; i < 3; i++)
                            b[3] = (byte)(b[3] * (b[3] + 1));
                        break;
                    case 0x2:
                        for (int i = 0; i < 3; i++)
                            b[1] = (byte)(b[1] * (b[6] + 1));
                        for (int i = 0; i < 3; i++)
                        {
                            ulong v = BitConverter.ToUInt64(b, 0);
                            v = unchecked(v + (v << (b[7] & 0x0f)));
                            byte[] nb = BitConverter.GetBytes(v);
                            Array.Copy(nb, b, 8);
                        }
                        break;
                }
            }
        }

        /// <summary>Post-authorization setup: noise filter, fan + LEDs, analog audio boost, then all settings.</summary>
        public void Initialize(HdPvrSettings s)
        {
            ApplyAll(s);
            ControlOut(ReqConfig, CtrlLowPassFilter, DefaultIndex, new byte[] { 3, 3, 0, 0 });
            // Always enable the fan: the HD PVR overheats without it.
            ControlOut(ReqFanLeds, 0, 0, new byte[] { 1 });
        }

        // ---- settings ----

        public void ApplyAll(HdPvrSettings s)
        {
            SetVideoStandard(s.VideoStandard);
            SetVideoInput(s.VideoInput);
            SetAudio(s.AudioInput, s.AudioCodec);
            SetBitrate(s.Bitrate, s.PeakBitrate);
            SetBitrateMode(s.BitrateMode);
            Config(CtrlGopMode, SimpleIdrGop);
            SetPicture(s);
            SetAudioBoost(s.AudioBoost);
        }

        public void SetVideoStandard(VideoStandard std) { Config(CtrlVideoStandard, (byte)std); }

        // Inputs are 1-based on the wire.
        public void SetVideoInput(VideoInput input) { Config(CtrlVideoInput, (byte)((int)input + 1)); }

        public void SetAudio(AudioInput input, AudioCodec codec)
        {
            byte wireInput = (byte)((int)input + 1);
            if (SupportsAc3)
                ControlOut(ReqConfig, CtrlAudioInput, DefaultIndex, new[] { wireInput, (byte)codec });
            else
                Config(CtrlAudioInput, wireInput);
        }

        public void SetBitrate(int bitrate, int peak)
        {
            ControlOut(ReqConfig, CtrlBitrate, DefaultIndex, new byte[] { (byte)bitrate, 0, (byte)peak, 0 });
        }

        public void SetBitrateMode(BitrateMode mode) { Config(CtrlBitrateMode, (byte)mode); }

        public void SetPicture(HdPvrSettings s)
        {
            Config(CtrlBrightness, (byte)s.Brightness);
            Config(CtrlContrast, (byte)s.Contrast);
            Config(CtrlHue, (byte)s.Hue);
            Config(CtrlSaturation, (byte)s.Saturation);
            Config(CtrlSharpness, (byte)s.Sharpness);
        }

        public void SetAudioBoost(bool on) { ControlOut(ReqAudioBoost, 0, 0, new[] { (byte)(on ? 1 : 0) }); }

        public SignalInfo GetSignal()
        {
            byte[] b = ControlIn(ReqStatus, CtrlVideoInfo, DefaultIndex, 5);
            var info = new SignalInfo();
            if (b.Length >= 5)
            {
                info.Width = b[1] << 8 | b[0];
                info.Height = b[3] << 8 | b[2];
                info.Fps = b[4];
            }
            return info;
        }

        // ---- streaming ----

        public void StartEncoder()
        {
            var setup = new Native.WINUSB_SETUP_PACKET();
            setup.RequestType = ReqTypeOut;
            setup.Request = ReqEncoderStart;
            setup.Value = 1;
            uint n;
            if (!Native.WinUsb_ControlTransfer(usb, setup, null, 0, out n, IntPtr.Zero))
                throw new HdPvrException("Encoder start request failed", Marshal.GetLastWin32Error());
            Config(CtrlStartStreaming, 0);
        }

        public void StopEncoder()
        {
            Config(CtrlStopStreaming, 0);
        }

        /// <summary>Empties the device's stream buffer after stopping, as the Linux driver does.</summary>
        public void Drain()
        {
            SetReadTimeout(90);
            try
            {
                var buf = new byte[BulkChunk];
                for (int i = 0; i < 500; i++)
                {
                    uint got;
                    if (!Native.WinUsb_ReadPipe(usb, bulkIn, buf, (uint)buf.Length, out got, IntPtr.Zero) || got == 0)
                        break;
                }
            }
            finally
            {
                SetReadTimeout(0);
            }
        }

        public void Dispose()
        {
            if (usb != IntPtr.Zero)
            {
                Native.WinUsb_Free(usb);
                usb = IntPtr.Zero;
            }
            if (file != null)
            {
                file.Dispose();
                file = null;
            }
        }
    }
}
