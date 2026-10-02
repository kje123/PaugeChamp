// Keeps several overlapped WinUSB reads queued on the bulk-in pipe so the device's
// transport-stream output is never left waiting on the host.
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaugeChamp
{
    internal sealed class BulkReader : IDisposable
    {
        const int Depth = 8;

        readonly HdPvrDevice dev;
        readonly IntPtr[] overlapped = new IntPtr[Depth];
        readonly IntPtr[] buffers = new IntPtr[Depth];
        readonly ManualResetEvent[] events = new ManualResetEvent[Depth];
        readonly bool[] pending = new bool[Depth];
        readonly byte[] managed = new byte[HdPvrDevice.BulkChunk];
        int next;

        public BulkReader(HdPvrDevice dev)
        {
            this.dev = dev;
            int ovSize = Marshal.SizeOf(typeof(NativeOverlapped));
            for (int i = 0; i < Depth; i++)
            {
                events[i] = new ManualResetEvent(false);
                buffers[i] = Marshal.AllocHGlobal(HdPvrDevice.BulkChunk);
                overlapped[i] = Marshal.AllocHGlobal(ovSize);
            }
        }

        public void Start()
        {
            for (int i = 0; i < Depth; i++)
                Submit(i);
            next = 0;
        }

        void Submit(int i)
        {
            var ov = new NativeOverlapped();
            ov.EventHandle = events[i].SafeWaitHandle.DangerousGetHandle();
            events[i].Reset();
            Marshal.StructureToPtr(ov, overlapped[i], false);
            if (!Native.WinUsb_ReadPipe(dev.UsbHandle, dev.BulkInPipe, buffers[i], HdPvrDevice.BulkChunk,
                    IntPtr.Zero, overlapped[i]))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != Native.ERROR_IO_PENDING)
                    throw new HdPvrException("Bulk read failed", err);
            }
            pending[i] = true;
        }

        /// <summary>
        /// Waits up to <paramref name="timeoutMs"/> for the oldest queued read. Returns the number of bytes
        /// copied into <see cref="Data"/> (0 on timeout) and requeues the read.
        /// </summary>
        public int Read(int timeoutMs)
        {
            int i = next;
            if (!events[i].WaitOne(timeoutMs))
                return 0;
            uint got;
            pending[i] = false;
            if (!Native.WinUsb_GetOverlappedResult(dev.UsbHandle, overlapped[i], out got, false))
            {
                int err = Marshal.GetLastWin32Error();
                throw new HdPvrException("Bulk transfer failed", err);
            }
            if (got > 0)
                Marshal.Copy(buffers[i], managed, 0, (int)got);
            Submit(i);
            next = (next + 1) % Depth;
            return (int)got;
        }

        public byte[] Data { get { return managed; } }

        /// <summary>Cancels all queued reads and waits for them to complete.</summary>
        public void Stop()
        {
            Native.WinUsb_AbortPipe(dev.UsbHandle, dev.BulkInPipe);
            for (int i = 0; i < Depth; i++)
            {
                if (!pending[i])
                    continue;
                uint got;
                events[i].WaitOne(2000);
                Native.WinUsb_GetOverlappedResult(dev.UsbHandle, overlapped[i], out got, false);
                pending[i] = false;
            }
        }

        public void Dispose()
        {
            try { Stop(); }
            catch (HdPvrException) { }
            for (int i = 0; i < Depth; i++)
            {
                // Only free memory once nothing can still be written into it.
                if (pending[i])
                    continue;
                Marshal.FreeHGlobal(buffers[i]);
                Marshal.FreeHGlobal(overlapped[i]);
                events[i].Dispose();
            }
        }
    }
}
