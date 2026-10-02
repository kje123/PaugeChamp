// Remuxes the HD PVR's H.264 + AAC/AC-3 elementary streams into a standard .mp4 file without
// re-encoding. Samples go straight into one 'mdat'; the index ('moov') is written on Close().
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaugeChamp
{
    internal sealed class Mp4Writer : IDisposable
    {
        const int MovieTimescale = 1000;
        const int VideoTimescale = 90000;

        sealed class Track
        {
            public readonly List<int> Sizes = new List<int>();
            public readonly List<long> ChunkOffsets = new List<long>();
            public readonly List<int> ChunkSamples = new List<int>();
        }

        readonly FileStream fs;
        readonly long mdatPos;
        readonly Track video = new Track(), audio = new Track();
        int lastTrack = -1;

        // video
        byte[] sps, pps;
        int width, height;
        long firstPts = -1, lastDts = -1, lastPts;
        readonly List<long> dts = new List<long>();
        readonly List<int> cts = new List<int>();
        readonly List<int> sync = new List<int>();

        // audio
        int audioType, sampleRate, samplesPerFrame, channels;
        byte[] audioConfig;            // AAC AudioSpecificConfig or AC-3 dac3 payload
        long audioFirstPts = -1, pendingAudioPts = -1;
        byte[] audioBuf = new byte[16384];
        int audioLen;

        public string Path { get; private set; }
        public long Length { get { return fs.Length; } }
        public bool HasVideo { get { return video.Sizes.Count > 0; } }

        public Mp4Writer(string path, int fallbackWidth, int fallbackHeight)
        {
            Path = path;
            width = fallbackWidth;
            height = fallbackHeight;
            fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
            var b = new BoxWriter();
            b.Begin("ftyp");
            b.Fourcc("isom"); b.U32(0x200);
            b.Fourcc("isom"); b.Fourcc("iso2"); b.Fourcc("avc1"); b.Fourcc("mp41");
            b.End();
            b.WriteTo(fs);
            mdatPos = fs.Position;
            // 64-bit mdat header; the size is patched in Close().
            fs.Write(new byte[] { 0, 0, 0, 1, (byte)'m', (byte)'d', (byte)'a', (byte)'t', 0, 0, 0, 0, 0, 0, 0, 0 }, 0, 16);
        }

        static long Unwrap(long ts, long reference)
        {
            const long wrap = 1L << 33;
            while (ts < reference - wrap / 2) ts += wrap;
            while (ts > reference + wrap / 2) ts -= wrap;
            return ts;
        }

        /// <summary>
        /// Adds one video access unit (Annex B). Returns false when the stream has a discontinuity or a
        /// new sequence header; the caller should close this file and continue in a new one.
        /// </summary>
        public bool AddVideo(byte[] data, int off, int len, long pts, long dtsIn, bool hasPts)
        {
            List<H264.Nal> nals = H264.Split(data, off, len);
            bool key = false;
            byte[] s = null, p = null;
            foreach (H264.Nal n in nals)
            {
                if (n.Type == H264.NalIdr) key = true;
                else if (n.Type == H264.NalSps) s = Copy(data, n);
                else if (n.Type == H264.NalPps) p = Copy(data, n);
            }

            if (sps == null)
            {
                // Start on the first keyframe that carries its parameter sets.
                if (!key || s == null || p == null || !hasPts)
                    return true;
                sps = s;
                pps = p;
                int w, h;
                if (H264.ParseSps(sps, out w, out h))
                {
                    width = w;
                    height = h;
                }
                firstPts = pts;
            }
            else
            {
                if (s != null && !Same(s, sps))
                    return false;
                if (!hasPts)
                {
                    // Continuation of the previous picture (e.g. second field in its own PES).
                    if (lastTrack == 0)
                        video.Sizes[video.Sizes.Count - 1] += WriteNals(data, nals);
                    return true;
                }
                pts = Unwrap(pts, lastPts);
                dtsIn = Unwrap(dtsIn, lastDts);
                if (dtsIn <= lastDts || dtsIn - lastDts > VideoTimescale)
                    return false;
            }
            if (lastDts < 0)
                dtsIn = Unwrap(dtsIn, pts);

            BeginSample(video, 0);
            video.Sizes.Add(WriteNals(data, nals));
            dts.Add(dtsIn);
            cts.Add((int)Math.Max(0, pts - dtsIn));
            if (key)
                sync.Add(video.Sizes.Count);
            lastDts = dtsIn;
            lastPts = pts;
            return true;
        }

        int WriteNals(byte[] data, List<H264.Nal> nals)
        {
            int total = 0;
            var len = new byte[4];
            foreach (H264.Nal n in nals)
            {
                if (n.Type == H264.NalSps || n.Type == H264.NalPps || n.Type == H264.NalAud || n.Type == H264.NalFiller)
                    continue;
                len[0] = (byte)(n.Length >> 24); len[1] = (byte)(n.Length >> 16);
                len[2] = (byte)(n.Length >> 8); len[3] = (byte)n.Length;
                fs.Write(len, 0, 4);
                fs.Write(data, n.Offset, n.Length);
                total += 4 + n.Length;
            }
            return total;
        }

        void BeginSample(Track t, int index)
        {
            if (lastTrack != index)
            {
                t.ChunkOffsets.Add(fs.Position);
                t.ChunkSamples.Add(0);
                lastTrack = index;
            }
            t.ChunkSamples[t.ChunkSamples.Count - 1]++;
        }

        /// <summary>Adds an audio PES payload (AAC with ADTS headers, or AC-3).</summary>
        public void AddAudio(int type, byte[] data, int off, int len, long pts, bool hasPts)
        {
            if (sps == null)
                return;   // nothing before the first video keyframe
            if (audioType != 0 && type != audioType)
                return;
            audioType = type;
            // Only the first frame's timestamp matters; the rest follow at a fixed frame duration.
            if (hasPts && (audioLen == 0 || audioFirstPts < 0))
                pendingAudioPts = pts;
            if (audioLen + len > audioBuf.Length)
                Array.Resize(ref audioBuf, Math.Max(audioBuf.Length * 2, audioLen + len));
            Buffer.BlockCopy(data, off, audioBuf, audioLen, len);
            audioLen += len;

            int pos = 0;
            while (true)
            {
                int frameLen, headerLen = 0;
                int start = type == TsDemuxer.TypeAc3
                    ? NextAc3Frame(pos, out frameLen)
                    : NextAdtsFrame(pos, out frameLen, out headerLen);
                if (start < 0 || start + frameLen > audioLen)
                {
                    pos = start < 0 ? Math.Max(pos, audioLen - 8) : start;
                    break;
                }
                WriteAudioFrame(start, frameLen, headerLen);
                pos = start + frameLen;
            }
            if (pos > 0)
            {
                Buffer.BlockCopy(audioBuf, pos, audioBuf, 0, audioLen - pos);
                audioLen -= pos;
            }
        }

        void WriteAudioFrame(int start, int frameLen, int headerLen)
        {
            if (audioConfig == null && !ParseAudioConfig(start))
                return;
            long framePts = pendingAudioPts;
            pendingAudioPts = -1;
            if (audioFirstPts < 0)
            {
                if (framePts < 0)
                    return;
                framePts = Unwrap(framePts, firstPts);
                if (framePts < firstPts)
                    return;   // drop audio that precedes the first video frame
                audioFirstPts = framePts;
            }
            BeginSample(audio, 1);
            fs.Write(audioBuf, start + headerLen, frameLen - headerLen);
            audio.Sizes.Add(frameLen - headerLen);
        }

        int NextAdtsFrame(int pos, out int frameLen, out int headerLen)
        {
            frameLen = headerLen = 0;
            for (int i = pos; i + 7 <= audioLen; i++)
            {
                if (audioBuf[i] != 0xFF || (audioBuf[i + 1] & 0xF6) != 0xF0)
                    continue;
                frameLen = ((audioBuf[i + 3] & 0x03) << 11) | (audioBuf[i + 4] << 3) | (audioBuf[i + 5] >> 5);
                headerLen = (audioBuf[i + 1] & 1) != 0 ? 7 : 9;
                if (frameLen > headerLen)
                    return i;
            }
            return -1;
        }

        int NextAc3Frame(int pos, out int frameLen)
        {
            frameLen = 0;
            for (int i = pos; i + 6 <= audioLen; i++)
            {
                if (audioBuf[i] != 0x0B || audioBuf[i + 1] != 0x77)
                    continue;
                frameLen = Ac3FrameSize(audioBuf[i + 4] >> 6, audioBuf[i + 4] & 0x3F);
                if (frameLen > 0)
                    return i;
            }
            return -1;
        }

        static readonly int[] Ac3Bitrates = { 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640 };
        static readonly int[] AacRates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

        static int Ac3FrameSize(int fscod, int frmsizecod)
        {
            if (frmsizecod / 2 >= Ac3Bitrates.Length)
                return 0;
            int kbps = Ac3Bitrates[frmsizecod / 2];
            switch (fscod)
            {
                case 0: return kbps * 4;                                         // 48 kHz
                case 1: return (kbps * 1000 * 1536 / 44100 / 16 + (frmsizecod & 1)) * 2;   // 44.1 kHz
                case 2: return kbps * 6;                                         // 32 kHz
            }
            return 0;
        }

        bool ParseAudioConfig(int i)
        {
            byte[] b = audioBuf;
            if (audioType == TsDemuxer.TypeAc3)
            {
                int fscod = b[i + 4] >> 6, frmsizecod = b[i + 4] & 0x3F;
                int bsid = b[i + 5] >> 3, bsmod = b[i + 5] & 7;
                int acmod = b[i + 6] >> 5;
                // lfeon follows optional mix-level fields whose presence depends on acmod.
                int bit = 3;
                if ((acmod & 1) != 0 && acmod != 1) bit += 2;
                if ((acmod & 4) != 0) bit += 2;
                if (acmod == 2) bit += 2;
                int lfe = (b[i + 6 + bit / 8] >> (7 - bit % 8)) & 1;
                sampleRate = fscod == 0 ? 48000 : fscod == 1 ? 44100 : 32000;
                samplesPerFrame = 1536;
                channels = 2;
                int v = (fscod << 22) | (bsid << 17) | (bsmod << 14) | (acmod << 11) | (lfe << 10) | ((frmsizecod >> 1) << 5);
                audioConfig = new[] { (byte)(v >> 16), (byte)(v >> 8), (byte)v };
                return true;
            }
            int profile = b[i + 2] >> 6;
            int sfi = (b[i + 2] >> 2) & 0x0F;
            int chan = ((b[i + 2] & 1) << 2) | (b[i + 3] >> 6);
            if (sfi >= AacRates.Length)
                return false;
            sampleRate = AacRates[sfi];
            samplesPerFrame = 1024;
            channels = chan == 0 ? 2 : chan;
            int asc = ((profile + 1) << 11) | (sfi << 7) | (chan << 3);
            audioConfig = new[] { (byte)(asc >> 8), (byte)asc };
            return true;
        }

        static byte[] Copy(byte[] d, H264.Nal n)
        {
            var c = new byte[n.Length];
            Buffer.BlockCopy(d, n.Offset, c, 0, n.Length);
            return c;
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        // ---- finalization ----

        public void Close()
        {
            long mdatEnd = fs.Position;
            if (!HasVideo)
            {
                fs.Dispose();
                try { File.Delete(Path); }
                catch (IOException) { }
                return;
            }

            // Video timing (90 kHz).
            int n = dts.Count;
            var durations = new int[n];
            for (int i = 0; i < n - 1; i++)
                durations[i] = (int)Math.Max(1, dts[i + 1] - dts[i]);
            durations[n - 1] = n > 1 ? durations[n - 2] : 3003;
            long videoMediaDur = dts[n - 1] - dts[0] + durations[n - 1];
            long videoMs = videoMediaDur * MovieTimescale / VideoTimescale;

            bool hasAudio = audio.Sizes.Count > 0 && audioConfig != null;
            long audioMediaDur = hasAudio ? (long)audio.Sizes.Count * samplesPerFrame : 0;
            long audioMs = hasAudio ? audioMediaDur * MovieTimescale / sampleRate : 0;
            long audioDelayMs = hasAudio ? Math.Max(0, (audioFirstPts - firstPts) * MovieTimescale / VideoTimescale) : 0;
            long movieMs = Math.Max(videoMs, audioDelayMs + audioMs);

            var b = new BoxWriter();
            b.Begin("moov");

            b.BeginFull("mvhd", 0, 0);
            b.U32(0); b.U32(0); b.U32(MovieTimescale); b.U32((uint)movieMs);
            b.U32(0x00010000); b.U16(0x0100); b.Zeros(10);
            Matrix(b);
            b.Zeros(24);
            b.U32(hasAudio ? 3u : 2u);
            b.End();

            // ---- video track ----
            b.Begin("trak");
            Tkhd(b, 1, videoMs, false);
            b.Begin("edts");
            b.BeginFull("elst", 0, 0);
            b.U32(1);
            b.U32((uint)videoMs); b.U32((uint)cts[0]); b.U32(0x00010000);
            b.End();
            b.End();
            b.Begin("mdia");
            Mdhd(b, VideoTimescale, videoMediaDur);
            Hdlr(b, "vide", "VideoHandler");
            b.Begin("minf");
            b.BeginFull("vmhd", 0, 1); b.Zeros(8); b.End();
            Dinf(b);
            b.Begin("stbl");
            b.BeginFull("stsd", 0, 0);
            b.U32(1);
            b.Begin("avc1");
            b.Zeros(6); b.U16(1);
            b.Zeros(16);
            b.U16((ushort)width); b.U16((ushort)height);
            b.U32(0x00480000); b.U32(0x00480000); b.U32(0);
            b.U16(1);
            b.Zeros(32);
            b.U16(0x18); b.U16(0xFFFF);
            b.Begin("avcC");
            b.U8(1); b.U8(sps[1]); b.U8(sps[2]); b.U8(sps[3]);
            b.U8(0xFF); b.U8(0xE1);
            b.U16((ushort)sps.Length); b.Bytes(sps);
            b.U8(1); b.U16((ushort)pps.Length); b.Bytes(pps);
            b.End();
            b.End();
            b.End();
            Stts(b, durations);
            Ctts(b);
            b.BeginFull("stss", 0, 0);
            b.U32((uint)sync.Count);
            foreach (int s in sync) b.U32((uint)s);
            b.End();
            SampleTables(b, video);
            b.End(); // stbl
            b.End(); // minf
            b.End(); // mdia
            b.End(); // trak

            // ---- audio track ----
            if (hasAudio)
            {
                b.Begin("trak");
                Tkhd(b, 2, audioDelayMs + audioMs, true);
                b.Begin("edts");
                b.BeginFull("elst", 0, 0);
                if (audioDelayMs > 0)
                {
                    b.U32(2);
                    b.U32((uint)audioDelayMs); b.U32(0xFFFFFFFF); b.U32(0x00010000);   // empty edit
                }
                else
                    b.U32(1);
                b.U32((uint)audioMs); b.U32(0); b.U32(0x00010000);
                b.End();
                b.End();
                b.Begin("mdia");
                Mdhd(b, sampleRate, audioMediaDur);
                Hdlr(b, "soun", "SoundHandler");
                b.Begin("minf");
                b.BeginFull("smhd", 0, 0); b.U32(0); b.End();
                Dinf(b);
                b.Begin("stbl");
                b.BeginFull("stsd", 0, 0);
                b.U32(1);
                b.Begin(audioType == TsDemuxer.TypeAc3 ? "ac-3" : "mp4a");
                b.Zeros(6); b.U16(1);
                b.Zeros(8);
                b.U16((ushort)channels); b.U16(16);
                b.U32(0);
                b.U32((uint)(sampleRate << 16));
                if (audioType == TsDemuxer.TypeAc3)
                {
                    b.Begin("dac3");
                    b.Bytes(audioConfig);
                    b.End();
                }
                else
                    Esds(b);
                b.End();
                b.End();
                b.BeginFull("stts", 0, 0);
                b.U32(1); b.U32((uint)audio.Sizes.Count); b.U32((uint)samplesPerFrame);
                b.End();
                SampleTables(b, audio);
                b.End(); // stbl
                b.End(); // minf
                b.End(); // mdia
                b.End(); // trak
            }

            b.End(); // moov

            fs.Position = mdatEnd;
            b.WriteTo(fs);
            long mdatSize = mdatEnd - mdatPos;
            fs.Position = mdatPos + 8;
            var size = new byte[8];
            for (int i = 0; i < 8; i++)
                size[i] = (byte)(mdatSize >> (56 - 8 * i));
            fs.Write(size, 0, 8);
            fs.Dispose();
        }

        public void Dispose()
        {
            fs.Dispose();
        }

        static void Matrix(BoxWriter b)
        {
            b.U32(0x00010000); b.U32(0); b.U32(0);
            b.U32(0); b.U32(0x00010000); b.U32(0);
            b.U32(0); b.U32(0); b.U32(0x40000000);
        }

        void Tkhd(BoxWriter b, int id, long durationMs, bool isAudio)
        {
            b.BeginFull("tkhd", 0, 3);
            b.U32(0); b.U32(0); b.U32((uint)id); b.U32(0); b.U32((uint)durationMs);
            b.Zeros(8);
            b.U16(0); b.U16(isAudio ? (ushort)1 : (ushort)0);
            b.U16(isAudio ? (ushort)0x0100 : (ushort)0); b.U16(0);
            Matrix(b);
            b.U32(isAudio ? 0 : (uint)width << 16);
            b.U32(isAudio ? 0 : (uint)height << 16);
            b.End();
        }

        static void Mdhd(BoxWriter b, int timescale, long duration)
        {
            bool v1 = duration > uint.MaxValue;
            b.BeginFull("mdhd", v1 ? 1 : 0, 0);
            if (v1)
            {
                b.U64(0); b.U64(0); b.U32((uint)timescale); b.U64((ulong)duration);
            }
            else
            {
                b.U32(0); b.U32(0); b.U32((uint)timescale); b.U32((uint)duration);
            }
            b.U16(0x55C4);   // language "und"
            b.U16(0);
            b.End();
        }

        static void Hdlr(BoxWriter b, string type, string name)
        {
            b.BeginFull("hdlr", 0, 0);
            b.U32(0); b.Fourcc(type); b.Zeros(12);
            b.Bytes(Encoding.ASCII.GetBytes(name)); b.U8(0);
            b.End();
        }

        static void Dinf(BoxWriter b)
        {
            b.Begin("dinf");
            b.BeginFull("dref", 0, 0);
            b.U32(1);
            b.BeginFull("url ", 0, 1);
            b.End();
            b.End();
            b.End();
        }

        static void Stts(BoxWriter b, int[] durations)
        {
            var runs = new List<KeyValuePair<int, int>>();
            foreach (int d in durations)
            {
                if (runs.Count > 0 && runs[runs.Count - 1].Value == d)
                    runs[runs.Count - 1] = new KeyValuePair<int, int>(runs[runs.Count - 1].Key + 1, d);
                else
                    runs.Add(new KeyValuePair<int, int>(1, d));
            }
            b.BeginFull("stts", 0, 0);
            b.U32((uint)runs.Count);
            foreach (var r in runs) { b.U32((uint)r.Key); b.U32((uint)r.Value); }
            b.End();
        }

        void Ctts(BoxWriter b)
        {
            bool any = false;
            foreach (int c in cts)
                if (c != 0) { any = true; break; }
            if (!any)
                return;
            var runs = new List<KeyValuePair<int, int>>();
            foreach (int c in cts)
            {
                if (runs.Count > 0 && runs[runs.Count - 1].Value == c)
                    runs[runs.Count - 1] = new KeyValuePair<int, int>(runs[runs.Count - 1].Key + 1, c);
                else
                    runs.Add(new KeyValuePair<int, int>(1, c));
            }
            b.BeginFull("ctts", 0, 0);
            b.U32((uint)runs.Count);
            foreach (var r in runs) { b.U32((uint)r.Key); b.U32((uint)r.Value); }
            b.End();
        }

        static void SampleTables(BoxWriter b, Track t)
        {
            // stsc: only record changes in samples-per-chunk.
            var entries = new List<int[]>();
            for (int i = 0; i < t.ChunkSamples.Count; i++)
                if (entries.Count == 0 || entries[entries.Count - 1][1] != t.ChunkSamples[i])
                    entries.Add(new[] { i + 1, t.ChunkSamples[i] });
            b.BeginFull("stsc", 0, 0);
            b.U32((uint)entries.Count);
            foreach (int[] e in entries) { b.U32((uint)e[0]); b.U32((uint)e[1]); b.U32(1); }
            b.End();

            b.BeginFull("stsz", 0, 0);
            b.U32(0); b.U32((uint)t.Sizes.Count);
            foreach (int s in t.Sizes) b.U32((uint)s);
            b.End();

            b.BeginFull("co64", 0, 0);
            b.U32((uint)t.ChunkOffsets.Count);
            foreach (long o in t.ChunkOffsets) b.U64((ulong)o);
            b.End();
        }

        void Esds(BoxWriter b)
        {
            b.BeginFull("esds", 0, 0);
            // ES_Descriptor
            b.U8(0x03); b.U8((byte)(23 + audioConfig.Length));
            b.U16(2); b.U8(0);
            // DecoderConfigDescriptor
            b.U8(0x04); b.U8((byte)(15 + audioConfig.Length));
            b.U8(0x40);          // MPEG-4 Audio
            b.U8(0x15);          // audio stream
            b.U8(0); b.U16(0);   // buffer size
            b.U32(0); b.U32(0);  // max / avg bitrate (unknown)
            // DecoderSpecificInfo
            b.U8(0x05); b.U8((byte)audioConfig.Length); b.Bytes(audioConfig);
            // SLConfigDescriptor
            b.U8(0x06); b.U8(1); b.U8(0x02);
            b.End();
        }

        sealed class BoxWriter
        {
            readonly MemoryStream ms = new MemoryStream();
            readonly Stack<long> starts = new Stack<long>();

            public void Begin(string type) { starts.Push(ms.Position); U32(0); Fourcc(type); }
            public void BeginFull(string type, int version, int flags) { Begin(type); U8((byte)version); U24(flags); }

            public void End()
            {
                long start = starts.Pop();
                long end = ms.Position;
                ms.Position = start;
                U32((uint)(end - start));
                ms.Position = end;
            }

            public void U8(byte v) { ms.WriteByte(v); }
            public void U16(ushort v) { U8((byte)(v >> 8)); U8((byte)v); }
            public void U24(int v) { U8((byte)(v >> 16)); U8((byte)(v >> 8)); U8((byte)v); }
            public void U32(uint v) { U16((ushort)(v >> 16)); U16((ushort)v); }
            public void U64(ulong v) { U32((uint)(v >> 32)); U32((uint)v); }
            public void Zeros(int n) { for (int i = 0; i < n; i++) U8(0); }
            public void Bytes(byte[] d) { ms.Write(d, 0, d.Length); }
            public void Fourcc(string s) { Bytes(Encoding.ASCII.GetBytes(s)); }
            public void WriteTo(Stream s) { ms.WriteTo(s); }
        }
    }
}
