// Owns the HD PVR. All USB traffic happens on one worker thread; the UI, the named-pipe
// server and the CLI only queue requests and read status snapshots.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PaugeChamp
{
    public enum EngineState { Searching, DriverSetupNeeded, Error, Idle, WaitingForSignal, Streaming }

    public sealed class EngineStatus
    {
        public EngineState State;
        public string Message;
        public DeviceCandidate Device;
        public int FirmwareVersion;
        public string FirmwareDate;
        public SignalInfo Signal;
        public bool StreamWanted;
        public double Mbps;
        public bool Recording;
        public string RecordPath;
        public long RecordBytes;
        public int UdpPort;

        public EngineStatus Clone()
        {
            return (EngineStatus)MemberwiseClone();
        }

        public string ToLines()
        {
            return "state=" + State.ToString().ToLowerInvariant() + "\n" +
                   "message=" + (Message ?? "") + "\n" +
                   "firmware=" + (FirmwareVersion > 0 ? "0x" + FirmwareVersion.ToString("x2") : "") + "\n" +
                   "signal=" + (Signal.Valid ? Signal.ToString() : "none") + "\n" +
                   "streaming=" + (State == EngineState.Streaming ? "1" : "0") + "\n" +
                   "stream_url=udp://127.0.0.1:" + UdpPort + "\n" +
                   "mbps=" + Mbps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "\n" +
                   "recording=" + (Recording ? "1" : "0") + "\n" +
                   "record_path=" + (RecordPath ?? "") + "\n";
        }
    }

    public sealed class CaptureEngine : IDisposable
    {
        // The firmware needs about 4 seconds after a stop before it will start again.
        const int RestartHoldoffMs = 4000;
        const int StallTimeoutMs = 4000;

        readonly object sync = new object();
        readonly Queue<Action> commands = new Queue<Action>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread worker;
        volatile bool quit;

        HdPvrSettings desired;           // what the user asked for (guarded by sync)
        HdPvrSettings applied;           // what the device currently has (worker thread only)
        EngineStatus status = new EngineStatus();

        HdPvrDevice dev;
        BulkReader reader;
        readonly StreamOutput output = new StreamOutput();
        bool streamWanted;
        int lastStopTick = Environment.TickCount - RestartHoldoffMs;
        int lastDataTick, lastStatTick, lastSignalPoll;
        long statBytes;

        // Demuxing is only done when something needs elementary streams (MP4 recording or preview).
        readonly TsDemuxer demux = new TsDemuxer();
        Mp4Writer mp4;
        bool mp4Active;
        string mp4Base;
        int mp4Part;
        volatile PreviewDecoder preview;

        public event EventHandler StatusChanged;
        public event EventHandler SettingsChanged;

        public CaptureEngine(HdPvrSettings settings)
        {
            desired = settings.Clone();
            desired.Normalize();
            streamWanted = desired.AutoStart;
            status.UdpPort = desired.UdpPort;
            status.StreamWanted = streamWanted;
            output.SetPort(desired.UdpPort);
            demux.OnVideo = OnVideoPes;
            demux.OnAudio = OnAudioPes;
            worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Name = "HD PVR worker";
        }

        public void Start() { worker.Start(); }

        /// <summary>Attaches (or with null, detaches) a preview decoder fed from the live stream.</summary>
        public void SetPreview(PreviewDecoder decoder)
        {
            preview = decoder;
        }

        public HdPvrSettings Settings
        {
            get { lock (sync) return desired.Clone(); }
        }

        public EngineStatus Status
        {
            get { lock (sync) return status.Clone(); }
        }

        // ---- requests (any thread) ----

        /// <summary>Validates, saves and queues new settings. Returns null on success or an error message.</summary>
        public string Apply(HdPvrSettings s)
        {
            s = s.Clone();
            s.Normalize();
            lock (sync)
            {
                if (dev != null && s.Hue > HdPvrSettings.MaxHue(status.FirmwareVersion))
                    s.Hue = HdPvrSettings.MaxHue(status.FirmwareVersion);
                if (s.AudioCodec == AudioCodec.Ac3 && dev != null && status.FirmwareVersion < 0x0d)
                    return "This unit's firmware does not support AC-3 audio";
                desired = s;
            }
            s.Save();
            Post(SyncSettings);
            var h = SettingsChanged;
            if (h != null)
                h(this, EventArgs.Empty);
            return null;
        }

        /// <summary>Applies "key=value" lines on top of the current settings.</summary>
        public string ApplyText(IEnumerable<string> lines)
        {
            var s = Settings;
            try { s.SetLines(lines); }
            catch (ArgumentException ex) { return ex.Message; }
            return Apply(s);
        }

        public void SetStreaming(bool on)
        {
            Post(delegate
            {
                streamWanted = on;
                if (!on && (output.Recording || mp4Active))
                    StopRecordingNow();
                if (!on)
                    StopStream();
                UpdateStatus(null);
            });
        }

        public void StartRecording()
        {
            Post(delegate
            {
                HdPvrSettings s = Settings;
                try
                {
                    StopRecordingNow();
                    Directory.CreateDirectory(s.RecordFolder);
                    string baseName = Path.Combine(s.RecordFolder, "PaugeChamp_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                    string path;
                    if (s.RecordFormat == RecordFormat.Ts)
                    {
                        path = baseName + ".ts";
                        output.StartRecording(path);
                    }
                    else
                    {
                        // The MP4 file is created at the first keyframe (see OnVideoPes).
                        path = baseName + ".mp4";
                        mp4Base = baseName;
                        mp4Part = 1;
                        mp4Active = true;
                    }
                    streamWanted = true;
                    lock (sync)
                    {
                        status.Recording = true;
                        status.RecordPath = path;
                    }
                    UpdateStatus(null);
                }
                catch (Exception ex)
                {
                    UpdateStatus("Could not start recording: " + ex.Message);
                }
            });
        }

        public void StopRecording()
        {
            Post(StopRecordingNow);
        }

        public void Rescan() { wake.Set(); }

        void Post(Action a)
        {
            lock (commands) commands.Enqueue(a);
            wake.Set();
        }

        // ---- worker thread ----

        void Run()
        {
            while (!quit)
            {
                try
                {
                    if (dev == null && !TryConnect())
                    {
                        RunCommands();
                        wake.WaitOne(2000);
                        continue;
                    }
                    RunCommands();
                    if (quit)
                        break;
                    if (streamWanted)
                        StreamStep();
                    else
                        IdleStep();
                }
                catch (HdPvrException ex)
                {
                    CloseDevice();
                    SetState(ex.IsDisconnect ? EngineState.Searching : EngineState.Error,
                        ex.IsDisconnect ? "HD PVR disconnected - waiting for it to come back" : ex.Message);
                    wake.WaitOne(2000);
                }
                catch (Exception ex)
                {
                    CloseDevice();
                    SetState(EngineState.Error, "Unexpected error: " + ex.Message);
                    wake.WaitOne(3000);
                }
            }
            try { if (reader != null) StopStream(); }
            catch (HdPvrException) { }
            CloseDevice();
            CloseMp4();   // finalize an MP4 in progress so it stays playable
            output.Dispose();
        }

        void RunCommands()
        {
            while (true)
            {
                Action a;
                lock (commands)
                {
                    if (commands.Count == 0)
                        return;
                    a = commands.Dequeue();
                }
                a();
            }
        }

        bool TryConnect()
        {
            List<DeviceCandidate> found = DeviceFinder.Find();
            if (found.Count == 0)
            {
                SetState(EngineState.Searching, "HD PVR not found - check the USB cable and that the unit is powered on");
                return false;
            }
            DeviceCandidate c = found[0];
            lock (sync) status.Device = c;
            if (!c.IsWinUsb)
            {
                SetState(EngineState.DriverSetupNeeded, "The HD PVR is using the '" + (c.Service ?? "none") +
                    "' driver. Switch it to WinUSB (see Driver setup).");
                return false;
            }
            if (c.InterfacePath == null)
            {
                SetState(EngineState.DriverSetupNeeded,
                    "WinUSB is installed but has no device interface yet. Click 'Fix driver' (needs admin).");
                return false;
            }

            var d = HdPvrDevice.Open(c.InterfacePath);
            try
            {
                d.Drain();
                d.Authorize();
                HdPvrSettings s;
                lock (sync)
                {
                    if (desired.PictureIsDefault)
                        desired.ResetPicture(d.FirmwareVersion);
                    if (desired.Hue > HdPvrSettings.MaxHue(d.FirmwareVersion))
                        desired.Hue = HdPvrSettings.MaxHue(d.FirmwareVersion);
                    if (desired.AudioCodec == AudioCodec.Ac3 && !d.SupportsAc3)
                        desired.AudioCodec = AudioCodec.Aac;
                    s = desired.Clone();
                    status.FirmwareVersion = d.FirmwareVersion;
                    status.FirmwareDate = d.FirmwareDate;
                }
                d.Initialize(s);
                applied = s;
            }
            catch
            {
                d.Dispose();
                throw;
            }
            dev = d;
            lastSignalPoll = Environment.TickCount - 10000;
            SetState(EngineState.Idle, "Connected");
            return true;
        }

        void SyncSettings()
        {
            HdPvrSettings s = Settings;
            output.SetPort(s.UdpPort);
            lock (sync) status.UdpPort = s.UdpPort;
            if (dev == null || applied == null)
                return;

            bool needsRestart = s.VideoInput != applied.VideoInput || s.AudioInput != applied.AudioInput ||
                                s.AudioCodec != applied.AudioCodec || s.VideoStandard != applied.VideoStandard;
            if (needsRestart && reader != null)
                StopStream();

            if (s.VideoStandard != applied.VideoStandard) dev.SetVideoStandard(s.VideoStandard);
            if (s.VideoInput != applied.VideoInput) dev.SetVideoInput(s.VideoInput);
            if (s.AudioInput != applied.AudioInput || s.AudioCodec != applied.AudioCodec) dev.SetAudio(s.AudioInput, s.AudioCodec);
            if (s.Bitrate != applied.Bitrate || s.PeakBitrate != applied.PeakBitrate) dev.SetBitrate(s.Bitrate, s.PeakBitrate);
            if (s.BitrateMode != applied.BitrateMode) dev.SetBitrateMode(s.BitrateMode);
            if (s.Brightness != applied.Brightness || s.Contrast != applied.Contrast || s.Hue != applied.Hue ||
                s.Saturation != applied.Saturation || s.Sharpness != applied.Sharpness)
                dev.SetPicture(s);
            if (s.AudioBoost != applied.AudioBoost) dev.SetAudioBoost(s.AudioBoost);
            applied = s;
            lastSignalPoll = Environment.TickCount - 10000;
            UpdateStatus(needsRestart && streamWanted ? "Input changed - restarting stream" : "Settings applied");
        }

        void StreamStep()
        {
            int now = Environment.TickCount;
            if (reader == null)
            {
                int hold = lastStopTick + RestartHoldoffMs - now;
                if (hold > 0)
                {
                    wake.WaitOne(Math.Min(hold, 500));
                    return;
                }
                SignalInfo sig = dev.GetSignal();
                lock (sync) status.Signal = sig;
                if (!sig.Valid)
                {
                    SetState(EngineState.WaitingForSignal, "No video signal on the " + InputName() + " input");
                    wake.WaitOne(1000);
                    return;
                }
                dev.StartEncoder();
                reader = new BulkReader(dev);
                reader.Start();
                lastDataTick = lastStatTick = now;
                statBytes = 0;
                SetState(EngineState.Streaming, "Streaming " + sig);
                return;
            }

            int n = reader.Read(100);
            now = Environment.TickCount;
            if (n > 0)
            {
                lastDataTick = now;
                statBytes += n;
                string err = output.Write(reader.Data, n);
                if (err != null)
                {
                    StopRecordingNow();
                    UpdateStatus("Recording stopped: " + err);
                }
                if (mp4Active || preview != null)
                {
                    try { demux.Feed(reader.Data, n); }
                    catch (IndexOutOfRangeException) { demux.Reset(); }   // malformed packet; resync
                    catch (ArgumentException) { demux.Reset(); }
                }
            }
            else if (now - lastDataTick > StallTimeoutMs)
            {
                // Typically the source changed resolution or was unplugged; restart cleanly.
                StopStream();
                SetState(EngineState.WaitingForSignal, "Stream stalled - restarting");
                return;
            }

            if (now - lastStatTick >= 1000)
            {
                lock (sync)
                {
                    status.Mbps = statBytes * 8.0 / ((now - lastStatTick) * 1000.0);
                    status.RecordBytes = mp4 != null ? mp4.Length : output.RecordBytes;
                }
                statBytes = 0;
                lastStatTick = now;
                Raise();
            }
        }

        void IdleStep()
        {
            if (Environment.TickCount - lastSignalPoll >= 1500)
            {
                SignalInfo sig = dev.GetSignal();
                lastSignalPoll = Environment.TickCount;
                lock (sync) status.Signal = sig;
                SetState(EngineState.Idle, sig.Valid ? "Ready - input signal " + sig : "Ready - no signal on the " + InputName() + " input");
            }
            wake.WaitOne(500);
        }

        void StopStream()
        {
            if (reader == null || dev == null)
                return;
            try
            {
                dev.StopEncoder();
                Thread.Sleep(50);
                reader.Stop();
                dev.Drain();
            }
            finally
            {
                reader.Dispose();
                reader = null;
                lastStopTick = Environment.TickCount;
                output.FlushUdp();
                ResetElementaryStreams();
                lock (sync) status.Mbps = 0;
            }
        }

        /// <summary>
        /// A stream restart resets the encoder's timestamps, so an MP4 in progress is finished and
        /// recording continues in a new "_partN" file; the preview decoder is rebuilt too.
        /// </summary>
        void ResetElementaryStreams()
        {
            demux.Reset();
            CloseMp4();
            PreviewDecoder pv = preview;
            if (pv != null)
                pv.Reset();
        }

        void StopRecordingNow()
        {
            output.StopRecording();
            mp4Active = false;
            CloseMp4();
            lock (sync)
            {
                status.Recording = false;
                status.RecordBytes = 0;
            }
            UpdateStatus(null);
        }

        // ---- elementary streams (worker thread, via demux.Feed) ----

        void OnVideoPes(byte[] data, int off, int len, long pts, long dts, bool hasPts)
        {
            PreviewDecoder pv = preview;
            if (pv != null)
                pv.Submit(data, off, len, pts);

            if (!mp4Active)
                return;
            try
            {
                if (mp4 == null)
                    OpenMp4Part();
                if (!mp4.AddVideo(data, off, len, pts, dts, hasPts))
                {
                    // New sequence header or timestamp jump: continue in a fresh file.
                    CloseMp4();
                    OpenMp4Part();
                    mp4.AddVideo(data, off, len, pts, dts, hasPts);
                }
            }
            catch (Exception ex)
            {
                // Disk full, file locked, or unexpected stream data: stop recording, keep streaming.
                StopRecordingNow();
                UpdateStatus("Recording stopped: " + ex.Message);
            }
        }

        void OnAudioPes(byte[] data, int off, int len, long pts, long dts, bool hasPts)
        {
            if (mp4 == null)
                return;
            try
            {
                mp4.AddAudio(demux.AudioType, data, off, len, pts, hasPts);
            }
            catch (Exception ex)
            {
                StopRecordingNow();
                UpdateStatus("Recording stopped: " + ex.Message);
            }
        }

        void OpenMp4Part()
        {
            string path = mp4Part <= 1 ? mp4Base + ".mp4" : mp4Base + "_part" + mp4Part + ".mp4";
            mp4Part++;
            SignalInfo sig;
            lock (sync) sig = status.Signal;
            mp4 = new Mp4Writer(path, sig.Width, sig.Height);
            lock (sync) status.RecordPath = path;
        }

        void CloseMp4()
        {
            if (mp4 == null)
                return;
            Mp4Writer m = mp4;
            mp4 = null;
            try
            {
                bool hadVideo = m.HasVideo;
                m.Close();
                if (!hadVideo)
                    mp4Part--;   // empty part was deleted; reuse its name
            }
            catch (IOException ex)
            {
                m.Dispose();
                UpdateStatus("Could not finish " + Path.GetFileName(m.Path) + ": " + ex.Message);
            }
        }

        void CloseDevice()
        {
            if (reader != null)
            {
                reader.Dispose();
                reader = null;
            }
            ResetElementaryStreams();
            if (dev != null)
            {
                dev.Dispose();
                dev = null;
            }
            applied = null;
            lock (sync)
            {
                status.Signal = new SignalInfo();
                status.Mbps = 0;
            }
        }

        string InputName()
        {
            switch (Settings.VideoInput)
            {
                case VideoInput.Component: return "component";
                case VideoInput.SVideo: return "S-Video";
                default: return "composite";
            }
        }

        void SetState(EngineState state, string message)
        {
            lock (sync)
            {
                if (status.State == state && status.Message == message)
                    return;
                status.State = state;
                status.Message = message;
                status.StreamWanted = streamWanted;
            }
            Raise();
        }

        void UpdateStatus(string message)
        {
            lock (sync)
            {
                if (message != null)
                    status.Message = message;
                status.StreamWanted = streamWanted;
            }
            Raise();
        }

        void Raise()
        {
            var h = StatusChanged;
            if (h != null)
                h(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            quit = true;
            wake.Set();
            if (worker.IsAlive)
                worker.Join(8000);
        }
    }

    /// <summary>Sends the transport stream to localhost over UDP and, optionally, to a .ts file.</summary>
    internal sealed class StreamOutput : IDisposable
    {
        const int UdpPayload = 7 * 188;   // 1316 bytes: standard MPEG-TS-over-UDP datagram

        readonly Socket udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        readonly byte[] pending = new byte[UdpPayload];
        int pendingLen;
        IPEndPoint target;
        FileStream file;

        public bool Recording { get { return file != null; } }
        public long RecordBytes { get { return file != null ? file.Position : 0; } }

        public void SetPort(int port)
        {
            target = new IPEndPoint(IPAddress.Loopback, port);
        }

        public void StartRecording(string path)
        {
            StopRecording();
            file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20);
        }

        public void StopRecording()
        {
            if (file == null)
                return;
            try { file.Dispose(); }
            catch (IOException) { }
            file = null;
        }

        /// <summary>Returns an error message if writing the recording failed.</summary>
        public string Write(byte[] data, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = Math.Min(UdpPayload - pendingLen, count - off);
                Buffer.BlockCopy(data, off, pending, pendingLen, n);
                pendingLen += n;
                off += n;
                if (pendingLen == UdpPayload)
                    FlushUdp();
            }
            if (file != null)
            {
                try { file.Write(data, 0, count); }
                catch (IOException ex) { return ex.Message; }
            }
            return null;
        }

        public void FlushUdp()
        {
            if (pendingLen == 0)
                return;
            try { udp.SendTo(pending, pendingLen, SocketFlags.None, target); }
            catch (SocketException) { }   // nobody listening is fine
            pendingLen = 0;
        }

        public void Dispose()
        {
            StopRecording();
            udp.Close();
        }
    }
}
