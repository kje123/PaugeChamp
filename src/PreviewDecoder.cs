// Decodes the HD PVR's H.264 with Windows' built-in decoder (Media Foundation) on its own thread
// and produces small RGB bitmaps for the preview window.
//
// Decoded frames arrive in bursts (USB transfers, decoder buffering), so a separate presenter
// thread hands them to the UI at the times given by their timestamps, a little behind real time.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaugeChamp
{
    public sealed class PreviewDecoder : IDisposable
    {
        const int MaxQueue = 30;          // undecoded input; beyond this we're behind and skip ahead
        const int TargetWidth = 640;
        const double PresentDelayMs = 80; // jitter buffer between decode and display
        const double LateRebaseMs = 150;  // a frame this late resets the clock mapping
        // Decoded frames waiting to be shown. The decoder emits bursts around keyframes, so this must
        // hold well over PresentDelayMs worth of 60 fps video or bursts trigger needless resyncs.
        const int MaxPending = 40;

        /// <summary>Raised on the presenter thread. The handler owns the bitmap and must dispose it.</summary>
        public event Action<Bitmap> FrameReady;
        public event Action<string> Failed;

        struct Item
        {
            public byte[] Data;
            public long Pts;
            public bool Reset;
        }

        struct Frame
        {
            public Bitmap Bitmap;
            public double TimeMs;   // presentation timestamp
        }

        [DllImport("winmm.dll")] static extern int timeBeginPeriod(int ms);
        [DllImport("winmm.dll")] static extern int timeEndPeriod(int ms);

        readonly Queue<Item> queue = new Queue<Item>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly Thread thread;
        volatile bool quit;
        bool needKey = true;
        int frameBusy;                    // 1 while the UI still holds an undelivered frame
        readonly Stopwatch clock = Stopwatch.StartNew();

        readonly Queue<Frame> pending = new Queue<Frame>();
        readonly AutoResetEvent pendingSignal = new AutoResetEvent(false);
        readonly Thread presenter;
        double clockOffsetMs = double.NaN;   // wall-clock ms = timestamp ms + offset

        IMFTransform decoder;
        bool providesSamples;
        int outWidth, outHeight, stride;
        int cropX, cropY, cropW, cropH;
        bool bt709;
        byte[] frame = new byte[0];
        int[] pixels = new int[0];

        public PreviewDecoder()
        {
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "HD PVR preview";
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            presenter = new Thread(Present);
            presenter.IsBackground = true;
            presenter.Name = "HD PVR preview presenter";
            presenter.Priority = ThreadPriority.AboveNormal;
            presenter.Start();
        }

        /// <summary>Called by the capture engine with each video access unit (Annex B).</summary>
        public void Submit(byte[] data, int off, int len, long pts)
        {
            lock (queue)
            {
                if (queue.Count >= MaxQueue)
                {
                    queue.Clear();
                    needKey = true;
                }
                if (needKey)
                {
                    if (!H264.ContainsIdr(data, off, len))
                        return;
                    needKey = false;
                }
                var copy = new byte[len];
                Buffer.BlockCopy(data, off, copy, 0, len);
                var item = new Item();
                item.Data = copy;
                item.Pts = pts;
                queue.Enqueue(item);
            }
            signal.Set();
        }

        /// <summary>Called when the stream restarts; the decoder is rebuilt at the next keyframe.</summary>
        public void Reset()
        {
            lock (queue)
            {
                queue.Clear();
                needKey = true;
                var item = new Item();
                item.Reset = true;
                queue.Enqueue(item);
            }
            signal.Set();
            ClearPending();
        }

        /// <summary>The UI calls this once it has taken the last frame, allowing the next one.</summary>
        public void FrameConsumed()
        {
            Interlocked.Exchange(ref frameBusy, 0);
        }

        void ClearPending()
        {
            lock (pending)
            {
                while (pending.Count > 0)
                    pending.Dequeue().Bitmap.Dispose();
                clockOffsetMs = double.NaN;
            }
        }

        // ---- presenter thread: shows frames at their timestamps ----

        void Present()
        {
            timeBeginPeriod(1);   // 1 ms sleep resolution instead of the default 15.6 ms
            try
            {
                while (!quit)
                {
                    Frame f;
                    double due;
                    lock (pending)
                    {
                        if (pending.Count == 0)
                            f = new Frame();
                        else
                            f = pending.Peek();
                    }
                    if (f.Bitmap == null)
                    {
                        pendingSignal.WaitOne(50);
                        continue;
                    }

                    double now = clock.Elapsed.TotalMilliseconds;
                    lock (pending)
                    {
                        if (double.IsNaN(clockOffsetMs))
                            clockOffsetMs = now - f.TimeMs + PresentDelayMs;
                        due = f.TimeMs + clockOffsetMs;
                        // Timestamp jump or we fell behind (e.g. a stall): re-anchor instead of
                        // fast-forwarding or freezing.
                        if (due - now > 1000 || now - due > LateRebaseMs)
                        {
                            clockOffsetMs = now - f.TimeMs + PresentDelayMs;
                            due = now + PresentDelayMs;
                        }
                    }

                    // Sleep coarsely, then finish with 1 ms steps for an accurate cadence.
                    while (!quit)
                    {
                        double remaining = due - clock.Elapsed.TotalMilliseconds;
                        if (remaining <= 0.5)
                            break;
                        Thread.Sleep(remaining > 3 ? (int)remaining - 2 : 1);
                    }

                    lock (pending)
                    {
                        if (pending.Count == 0 || !ReferenceEquals(pending.Peek().Bitmap, f.Bitmap))
                            continue;   // cleared by a reset while we waited
                        pending.Dequeue();
                    }
                    Action<Bitmap> h = FrameReady;
                    if (h == null || Interlocked.CompareExchange(ref frameBusy, 1, 0) != 0)
                    {
                        f.Bitmap.Dispose();   // UI still busy with the previous frame: drop this one
                        continue;
                    }
                    h(f.Bitmap);
                }
            }
            finally
            {
                timeEndPeriod(1);
                ClearPending();
            }
        }

        void Run()
        {
            if (MF.MFStartup(MF.MF_VERSION, 0) < 0)
            {
                Fail("Media Foundation is not available on this PC");
                return;
            }
            try
            {
                while (!quit)
                {
                    signal.WaitOne(500);
                    while (!quit)
                    {
                        Item item;
                        lock (queue)
                        {
                            if (queue.Count == 0)
                                break;
                            item = queue.Dequeue();
                        }
                        if (item.Reset)
                        {
                            DestroyDecoder();
                            continue;
                        }
                        try
                        {
                            if (decoder == null)
                                CreateDecoder();
                            Decode(item);
                        }
                        catch (COMException ex)
                        {
                            if (ex.ErrorCode == unchecked((int)0x80040154))   // REGDB_E_CLASSNOTREG
                            {
                                Fail("The Windows H.264 decoder is not installed (Windows N edition?)");
                                return;
                            }
                            // Rebuild on the next keyframe rather than give up.
                            DestroyDecoder();
                            lock (queue)
                            {
                                queue.Clear();
                                needKey = true;
                            }
                            Debug.WriteLine("Preview decode error: " + ex.Message);
                        }
                        catch (InvalidCastException)
                        {
                            Fail("The Windows H.264 decoder is not installed (Windows N edition?)");
                            return;
                        }
                        catch (Exception ex)
                        {
                            // Never let the preview take the app (and the OBS stream) down.
                            Fail(ex.Message);
                            return;
                        }
                    }
                }
            }
            finally
            {
                DestroyDecoder();
                MF.MFShutdown();
            }
        }

        void Fail(string message)
        {
            var h = Failed;
            if (h != null)
                h(message);
        }

        void CreateDecoder()
        {
            Type t = Type.GetTypeFromCLSID(MF.CLSID_MSH264DecoderMFT);
            decoder = (IMFTransform)Activator.CreateInstance(t);

            IMFAttributes attrs;
            if (decoder.GetAttributes(out attrs) >= 0 && attrs != null)
            {
                Guid lowLatency = MF.CODECAPI_AVLowLatencyMode;
                attrs.SetUINT32(ref lowLatency, 1);
                Marshal.ReleaseComObject(attrs);
            }

            IMFMediaType input;
            MF.Check(MF.MFCreateMediaType(out input), "MFCreateMediaType");
            Guid key = MF.MF_MT_MAJOR_TYPE, val = MF.MFMediaType_Video;
            input.SetGUID(ref key, ref val);
            key = MF.MF_MT_SUBTYPE;
            val = MF.MFVideoFormat_H264;
            input.SetGUID(ref key, ref val);
            MF.Check(decoder.SetInputType(0, input, 0), "SetInputType");
            Marshal.ReleaseComObject(input);

            SelectOutputType();
            decoder.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
            decoder.ProcessMessage(MF.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
        }

        void SelectOutputType()
        {
            for (uint i = 0; ; i++)
            {
                IMFMediaType type;
                int hr = decoder.GetOutputAvailableType(0, i, out type);
                if (hr == MF.MF_E_NO_MORE_TYPES)
                    throw new COMException("Decoder offers no NV12 output", hr);
                MF.Check(hr, "GetOutputAvailableType");
                Guid subKey = MF.MF_MT_SUBTYPE;
                Guid sub;
                type.GetGUID(ref subKey, out sub);
                if (sub != MF.MFVideoFormat_NV12)
                {
                    Marshal.ReleaseComObject(type);
                    continue;
                }
                MF.Check(decoder.SetOutputType(0, type, 0), "SetOutputType");

                Guid k = MF.MF_MT_FRAME_SIZE;
                ulong size;
                type.GetUINT64(ref k, out size);
                outWidth = (int)(size >> 32);
                outHeight = (int)(size & 0xFFFFFFFF);

                k = MF.MF_MT_DEFAULT_STRIDE;
                uint s;
                stride = type.GetUINT32(ref k, out s) >= 0 && (int)s > 0 ? (int)s : 0;

                cropX = cropY = 0;
                cropW = outWidth;
                cropH = outHeight;
                k = MF.MF_MT_MINIMUM_DISPLAY_APERTURE;
                var area = new byte[16];
                int written;
                if (type.GetBlob(ref k, area, 16, out written) >= 0 && written == 16)
                {
                    // MFVideoArea: MFOffset x, MFOffset y (16.16 fixed), SIZE cx, cy
                    cropX = BitConverter.ToInt16(area, 2);
                    cropY = BitConverter.ToInt16(area, 6);
                    cropW = BitConverter.ToInt32(area, 8);
                    cropH = BitConverter.ToInt32(area, 12);
                }

                k = MF.MF_MT_YUV_MATRIX;
                uint matrix;
                bt709 = type.GetUINT32(ref k, out matrix) >= 0 ? matrix == 1 : cropH >= 720;
                Marshal.ReleaseComObject(type);

                MFT_OUTPUT_STREAM_INFO info;
                MF.Check(decoder.GetOutputStreamInfo(0, out info), "GetOutputStreamInfo");
                providesSamples = (info.dwFlags & MF.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
                return;
            }
        }

        void Decode(Item item)
        {
            IMFMediaBuffer buf;
            MF.Check(MF.MFCreateMemoryBuffer(item.Data.Length, out buf), "MFCreateMemoryBuffer");
            IntPtr p;
            int max, cur;
            MF.Check(buf.Lock(out p, out max, out cur), "Lock");
            Marshal.Copy(item.Data, 0, p, item.Data.Length);
            buf.Unlock();
            buf.SetCurrentLength(item.Data.Length);

            IMFSample sample;
            MF.Check(MF.MFCreateSample(out sample), "MFCreateSample");
            sample.AddBuffer(buf);
            sample.SetSampleTime(item.Pts * 1000 / 9);   // 90 kHz -> 100 ns
            try
            {
                int hr = decoder.ProcessInput(0, sample, 0);
                if (hr == MF.MF_E_NOTACCEPTING)
                {
                    DrainOutput();
                    hr = decoder.ProcessInput(0, sample, 0);
                }
                MF.Check(hr, "ProcessInput");
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
                Marshal.ReleaseComObject(buf);
            }
            DrainOutput();
        }

        void DrainOutput()
        {
            while (true)
            {
                IMFSample ours = null;
                IntPtr oursPtr = IntPtr.Zero;
                if (!providesSamples)
                {
                    MFT_OUTPUT_STREAM_INFO info;
                    MF.Check(decoder.GetOutputStreamInfo(0, out info), "GetOutputStreamInfo");
                    IMFMediaBuffer ob;
                    MF.Check(MF.MFCreateMemoryBuffer((int)info.cbSize, out ob), "MFCreateMemoryBuffer");
                    MF.Check(MF.MFCreateSample(out ours), "MFCreateSample");
                    ours.AddBuffer(ob);
                    Marshal.ReleaseComObject(ob);
                    oursPtr = Marshal.GetComInterfaceForObject(ours, typeof(IMFSample));
                }

                var buffers = new MFT_OUTPUT_DATA_BUFFER[1];
                buffers[0].pSample = oursPtr;
                uint status;
                int hr = decoder.ProcessOutput(0, 1, buffers, out status);
                if (buffers[0].pEvents != IntPtr.Zero)
                    Marshal.Release(buffers[0].pEvents);

                IMFSample result = null;
                try
                {
                    if (hr == MF.MF_E_TRANSFORM_NEED_MORE_INPUT)
                        return;
                    if (hr == MF.MF_E_TRANSFORM_STREAM_CHANGE)
                    {
                        SelectOutputType();
                        continue;
                    }
                    MF.Check(hr, "ProcessOutput");
                    if (providesSamples)
                    {
                        if (buffers[0].pSample == IntPtr.Zero)
                            continue;
                        result = (IMFSample)Marshal.GetObjectForIUnknown(buffers[0].pSample);
                    }
                    else
                        result = ours;
                    QueueFrame(result);
                }
                finally
                {
                    if (providesSamples && buffers[0].pSample != IntPtr.Zero)
                        Marshal.Release(buffers[0].pSample);
                    if (result != null && providesSamples)
                        Marshal.ReleaseComObject(result);
                    if (oursPtr != IntPtr.Zero)
                        Marshal.Release(oursPtr);
                    if (ours != null)
                        Marshal.ReleaseComObject(ours);
                }
            }
        }

        void QueueFrame(IMFSample sample)
        {
            if (FrameReady == null)
                return;
            long time100ns;
            if (sample.GetSampleTime(out time100ns) < 0)
                time100ns = 0;

            IMFMediaBuffer buf;
            MF.Check(sample.ConvertToContiguousBuffer(out buf), "ConvertToContiguousBuffer");
            int len;
            try
            {
                IntPtr p;
                int max;
                MF.Check(buf.Lock(out p, out max, out len), "Lock");
                if (frame.Length < len)
                    frame = new byte[len];
                Marshal.Copy(p, frame, 0, len);
                buf.Unlock();
            }
            finally
            {
                Marshal.ReleaseComObject(buf);
            }

            Bitmap bmp = ToBitmap(len);
            if (bmp == null)
                return;
            var f = new Frame();
            f.Bitmap = bmp;
            f.TimeMs = time100ns / 10000.0;
            lock (pending)
            {
                if (pending.Count >= MaxPending)
                {
                    // Display can't keep up (or the clock drifted): skip ahead.
                    while (pending.Count > 0)
                        pending.Dequeue().Bitmap.Dispose();
                    clockOffsetMs = double.NaN;
                }
                pending.Enqueue(f);
            }
            pendingSignal.Set();
        }

        /// <summary>NV12 -> downscaled RGB. Samples one field only, which also deinterlaces 1080i/480i.</summary>
        Bitmap ToBitmap(int len)
        {
            int planeHeight = outHeight;
            int pitch = stride > 0 ? stride : (planeHeight > 0 ? len * 2 / (planeHeight * 3) : 0);
            if (pitch <= 0 || cropW <= 0 || cropH <= 0 || pitch * planeHeight * 3 / 2 > len)
                return null;
            int step = Math.Max(1, cropW / TargetWidth);
            int w = cropW / step, h = cropH / step;
            int uvBase = pitch * planeHeight;
            // Reused: a fresh 1+ MB array per frame lands on the large-object heap and the resulting
            // full garbage collections pause every thread, which shows up as preview hitches.
            if (pixels.Length != w * h)
                pixels = new int[w * h];
            int[] px = pixels;
            int kr = bt709 ? 459 : 409, kgu = bt709 ? 55 : 100, kgv = bt709 ? 136 : 208, kb = bt709 ? 541 : 516;
            byte[] f = frame;
            for (int y = 0; y < h; y++)
            {
                int sy = (cropY + y * step) & ~1;
                int rowY = sy * pitch;
                int rowUV = uvBase + (sy >> 1) * pitch;
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    int sx = cropX + x * step;
                    int c = (f[rowY + sx] - 16) * 298;
                    int uv = rowUV + (sx & ~1);
                    int d = f[uv] - 128, e = f[uv + 1] - 128;
                    int r = (c + kr * e + 128) >> 8;
                    int g = (c - kgu * d - kgv * e + 128) >> 8;
                    int b = (c + kb * d + 128) >> 8;
                    r = r < 0 ? 0 : r > 255 ? 255 : r;
                    g = g < 0 ? 0 : g > 255 ? 255 : g;
                    b = b < 0 ? 0 : b > 255 ? 255 : b;
                    px[o + x] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
                }
            }
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(px, y * w, new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), w);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
        }

        void DestroyDecoder()
        {
            if (decoder == null)
                return;
            try { decoder.ProcessMessage(MF.MFT_MESSAGE_COMMAND_FLUSH, IntPtr.Zero); }
            catch (COMException) { }
            Marshal.ReleaseComObject(decoder);
            decoder = null;
        }

        public void Dispose()
        {
            quit = true;
            signal.Set();
            pendingSignal.Set();
            thread.Join(3000);
            presenter.Join(1000);
        }
    }
}
