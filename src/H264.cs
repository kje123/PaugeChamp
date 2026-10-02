// Minimal H.264 Annex-B helpers: NAL unit splitting and SPS frame-size parsing.
using System;
using System.Collections.Generic;

namespace PaugeChamp
{
    internal static class H264
    {
        public const int NalIdr = 5, NalSei = 6, NalSps = 7, NalPps = 8, NalAud = 9, NalFiller = 12;

        public struct Nal
        {
            public int Offset, Length, Type;
        }

        /// <summary>Splits an Annex-B buffer into NAL units (start codes excluded).</summary>
        public static List<Nal> Split(byte[] d, int off, int len)
        {
            var list = new List<Nal>();
            int end = off + len;
            int start = -1;
            int i = off;
            while (i + 2 < end)
            {
                if (d[i + 2] > 1)
                {
                    i += 3;   // fast skip: no start code can end here
                    continue;
                }
                if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1)
                {
                    if (start >= 0)
                        Add(list, d, start, i);
                    i += 3;
                    start = i;
                    continue;
                }
                i++;
            }
            if (start >= 0)
                Add(list, d, start, end);
            return list;
        }

        static void Add(List<Nal> list, byte[] d, int start, int stop)
        {
            while (stop > start && d[stop - 1] == 0)
                stop--;   // the leading zero of a 4-byte start code belongs to no NAL
            if (stop > start)
            {
                var n = new Nal();
                n.Offset = start;
                n.Length = stop - start;
                n.Type = d[start] & 0x1F;
                list.Add(n);
            }
        }

        public static bool ContainsIdr(byte[] d, int off, int len)
        {
            foreach (Nal n in Split(d, off, len))
                if (n.Type == NalIdr)
                    return true;
            return false;
        }

        /// <summary>Reads the cropped display size from an SPS NAL unit (header byte included).</summary>
        public static bool ParseSps(byte[] sps, out int width, out int height)
        {
            width = height = 0;
            try
            {
                var r = new BitReader(Unescape(sps, 1, sps.Length - 1));
                int profile = (int)r.U(8);
                r.U(16);           // constraint flags + level
                r.Ue();            // seq_parameter_set_id
                int chroma = 1;
                if (profile == 100 || profile == 110 || profile == 122 || profile == 244 || profile == 44 ||
                    profile == 83 || profile == 86 || profile == 118 || profile == 128 || profile == 138 ||
                    profile == 139 || profile == 134 || profile == 135)
                {
                    chroma = (int)r.Ue();
                    if (chroma == 3)
                        r.U(1);    // separate_colour_plane_flag
                    r.Ue();        // bit_depth_luma
                    r.Ue();        // bit_depth_chroma
                    r.U(1);        // qpprime_y_zero_transform_bypass
                    if (r.U(1) != 0)   // seq_scaling_matrix_present
                    {
                        int lists = chroma != 3 ? 8 : 12;
                        for (int i = 0; i < lists; i++)
                            if (r.U(1) != 0)
                                SkipScalingList(r, i < 6 ? 16 : 64);
                    }
                }
                r.Ue();                         // log2_max_frame_num
                uint pocType = r.Ue();
                if (pocType == 0)
                    r.Ue();
                else if (pocType == 1)
                {
                    r.U(1);
                    r.Se();
                    r.Se();
                    uint cycle = r.Ue();
                    for (uint i = 0; i < cycle; i++)
                        r.Se();
                }
                r.Ue();                         // max_num_ref_frames
                r.U(1);                         // gaps_in_frame_num_allowed
                int wMbs = (int)r.Ue() + 1;
                int hMap = (int)r.Ue() + 1;
                int frameMbsOnly = (int)r.U(1);
                if (frameMbsOnly == 0)
                    r.U(1);                     // mb_adaptive_frame_field
                r.U(1);                         // direct_8x8_inference
                int cl = 0, cr = 0, ct = 0, cb = 0;
                if (r.U(1) != 0)
                {
                    cl = (int)r.Ue(); cr = (int)r.Ue(); ct = (int)r.Ue(); cb = (int)r.Ue();
                }
                int cropX = chroma == 0 || chroma == 3 ? 1 : 2;
                int cropY = (chroma == 1 ? 2 : 1) * (2 - frameMbsOnly);
                width = wMbs * 16 - cropX * (cl + cr);
                height = (2 - frameMbsOnly) * hMap * 16 - cropY * (ct + cb);
                return width > 0 && height > 0;
            }
            catch (IndexOutOfRangeException)
            {
                return false;
            }
        }

        static void SkipScalingList(BitReader r, int size)
        {
            int last = 8, next = 8;
            for (int j = 0; j < size; j++)
            {
                if (next != 0)
                    next = (last + r.Se() + 256) % 256;
                last = next == 0 ? last : next;
            }
        }

        /// <summary>Removes emulation-prevention bytes (00 00 03 -> 00 00).</summary>
        static byte[] Unescape(byte[] d, int off, int len)
        {
            var o = new List<byte>(len);
            int zeros = 0;
            for (int i = off; i < off + len; i++)
            {
                if (zeros >= 2 && d[i] == 3)
                {
                    zeros = 0;
                    continue;
                }
                zeros = d[i] == 0 ? zeros + 1 : 0;
                o.Add(d[i]);
            }
            return o.ToArray();
        }

        sealed class BitReader
        {
            readonly byte[] b;
            int pos;

            public BitReader(byte[] b) { this.b = b; }

            public uint U(int n)
            {
                uint v = 0;
                for (int i = 0; i < n; i++, pos++)
                    v = (v << 1) | (uint)((b[pos >> 3] >> (7 - (pos & 7))) & 1);
                return v;
            }

            public uint Ue()
            {
                int zeros = 0;
                while (U(1) == 0)
                    if (++zeros > 31)
                        throw new IndexOutOfRangeException();
                return (uint)((1L << zeros) - 1 + U(zeros));
            }

            public int Se()
            {
                uint k = Ue();
                return (k & 1) != 0 ? (int)((k + 1) / 2) : -(int)(k / 2);
            }
        }
    }
}
