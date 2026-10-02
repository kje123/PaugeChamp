// MPEG transport stream demuxer: finds the H.264 and audio streams via PAT/PMT and hands out
// complete PES payloads with their timestamps.
using System;

namespace PaugeChamp
{
    internal sealed class TsDemuxer
    {
        public const int TypeH264 = 0x1B, TypeAac = 0x0F, TypeAc3 = 0x81;
        const int PacketSize = 188;

        /// <summary>The buffer is reused after the call returns; copy anything you keep.</summary>
        public delegate void PesHandler(byte[] data, int offset, int length, long pts, long dts, bool hasPts);

        public PesHandler OnVideo;
        public PesHandler OnAudio;
        public int AudioType { get; private set; }

        readonly byte[] packet = new byte[PacketSize];
        int packetLen;
        int pmtPid = -1, videoPid = -1, audioPid = -1;
        readonly PesBuffer video = new PesBuffer();
        readonly PesBuffer audio = new PesBuffer();

        public void Reset()
        {
            packetLen = 0;
            pmtPid = videoPid = audioPid = -1;
            AudioType = 0;
            video.Clear();
            audio.Clear();
        }

        public void Feed(byte[] data, int count)
        {
            int i = 0;
            while (i < count)
            {
                if (packetLen == 0 && data[i] != 0x47)
                {
                    i++;   // resync on the next sync byte
                    continue;
                }
                int n = Math.Min(PacketSize - packetLen, count - i);
                Buffer.BlockCopy(data, i, packet, packetLen, n);
                packetLen += n;
                i += n;
                if (packetLen == PacketSize)
                {
                    Parse(packet);
                    packetLen = 0;
                }
            }
        }

        void Parse(byte[] p)
        {
            if ((p[1] & 0x80) != 0)
                return;   // transport error
            bool start = (p[1] & 0x40) != 0;
            int pid = ((p[1] & 0x1F) << 8) | p[2];
            int afc = (p[3] >> 4) & 3;
            int off = 4;
            if ((afc & 2) != 0)
                off += 1 + p[4];
            if ((afc & 1) == 0 || off >= PacketSize)
                return;

            if (pid == 0)
            {
                if (start) ParsePat(p, off);
            }
            else if (pid == pmtPid)
            {
                if (start) ParsePmt(p, off);
            }
            else if (pid == videoPid)
                Pes(video, p, off, start, OnVideo);
            else if (pid == audioPid)
                Pes(audio, p, off, start, OnAudio);
        }

        void ParsePat(byte[] p, int off)
        {
            off += 1 + p[off];   // pointer_field
            if (off + 8 > PacketSize || p[off] != 0x00)
                return;
            int sectionLen = ((p[off + 1] & 0x0F) << 8) | p[off + 2];
            int end = Math.Min(off + 3 + sectionLen - 4, PacketSize);
            for (int i = off + 8; i + 4 <= end; i += 4)
            {
                int program = (p[i] << 8) | p[i + 1];
                if (program != 0)
                {
                    pmtPid = ((p[i + 2] & 0x1F) << 8) | p[i + 3];
                    return;
                }
            }
        }

        void ParsePmt(byte[] p, int off)
        {
            off += 1 + p[off];
            if (off + 12 > PacketSize || p[off] != 0x02)
                return;
            int sectionLen = ((p[off + 1] & 0x0F) << 8) | p[off + 2];
            int end = Math.Min(off + 3 + sectionLen - 4, PacketSize);
            int infoLen = ((p[off + 10] & 0x0F) << 8) | p[off + 11];
            int vpid = -1, apid = -1, atype = 0;
            for (int i = off + 12 + infoLen; i + 5 <= end; )
            {
                int type = p[i];
                int pid = ((p[i + 1] & 0x1F) << 8) | p[i + 2];
                int esLen = ((p[i + 3] & 0x0F) << 8) | p[i + 4];
                if (type == TypeH264 && vpid < 0)
                    vpid = pid;
                else if ((type == TypeAac || type == TypeAc3) && apid < 0)
                {
                    apid = pid;
                    atype = type;
                }
                else if (type == 0x06 && apid < 0 && HasAc3Descriptor(p, i + 5, Math.Min(i + 5 + esLen, end)))
                {
                    apid = pid;
                    atype = TypeAc3;
                }
                i += 5 + esLen;
            }
            if (vpid != videoPid)
            {
                video.Clear();
                videoPid = vpid;
            }
            if (apid != audioPid)
            {
                audio.Clear();
                audioPid = apid;
            }
            AudioType = atype;
        }

        static bool HasAc3Descriptor(byte[] p, int i, int end)
        {
            while (i + 2 <= end)
            {
                int tag = p[i], len = p[i + 1];
                if (tag == 0x6A || tag == 0x81)
                    return true;
                if (tag == 0x05 && len >= 4 && i + 6 <= end && p[i + 2] == 'A' && p[i + 3] == 'C' && p[i + 4] == '-' && p[i + 5] == '3')
                    return true;
                i += 2 + len;
            }
            return false;
        }

        static void Pes(PesBuffer b, byte[] p, int off, bool start, PesHandler handler)
        {
            if (start)
            {
                if (b.Length > 0)
                    Emit(b, handler);
                b.Length = 0;
                b.Started = true;
            }
            if (b.Started)
                b.Append(p, off, PacketSize - off);
        }

        static void Emit(PesBuffer pes, PesHandler handler)
        {
            byte[] d = pes.Data;
            int len = pes.Length;
            if (handler == null || len < 9 || d[0] != 0 || d[1] != 0 || d[2] != 1)
                return;
            int pesLen = (d[4] << 8) | d[5];
            int flags = d[7];
            int dataOff = 9 + d[8];
            long pts = 0, dts = 0;
            bool hasPts = (flags & 0x80) != 0 && len >= 14;
            if (hasPts)
            {
                pts = ReadTimestamp(d, 9);
                dts = (flags & 0xC0) == 0xC0 && len >= 19 ? ReadTimestamp(d, 14) : pts;
            }
            int end = pesLen != 0 ? Math.Min(6 + pesLen, len) : len;
            if (end > dataOff)
                handler(d, dataOff, end - dataOff, pts, dts, hasPts);
        }

        static long ReadTimestamp(byte[] d, int i)
        {
            return ((long)((d[i] >> 1) & 0x07) << 30) | ((long)d[i + 1] << 22) | ((long)(d[i + 2] >> 1) << 15) |
                   ((long)d[i + 3] << 7) | ((long)d[i + 4] >> 1);
        }

        sealed class PesBuffer
        {
            public byte[] Data = new byte[256 * 1024];
            public int Length;
            public bool Started;

            public void Append(byte[] src, int off, int count)
            {
                if (Length + count > Data.Length)
                    Array.Resize(ref Data, Math.Max(Data.Length * 2, Length + count));
                Buffer.BlockCopy(src, off, Data, Length, count);
                Length += count;
            }

            public void Clear()
            {
                Length = 0;
                Started = false;
            }
        }
    }
}
