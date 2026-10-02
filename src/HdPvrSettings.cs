// User-facing settings, their validation, and the key=value text format shared by the
// settings file, the named-pipe protocol and the OBS script.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PaugeChamp
{
    public enum VideoInput { Component = 0, SVideo = 1, Composite = 2 }
    public enum AudioInput { RcaBack = 0, RcaFront = 1, Spdif = 2 }
    public enum AudioCodec { Aac = 0, Ac3 = 1 }
    public enum VideoStandard { Ntsc60Hz = 0, Pal50Hz = 1 }
    public enum BitrateMode { Constant = 1, VariablePeak = 2, VariableAverage = 3 }
    public enum RecordFormat { Mp4 = 0, Ts = 1 }

    public sealed class HdPvrSettings
    {
        // Device settings. Bitrates are in units of 100 kbit/s, the unit the device uses.
        public VideoInput VideoInput = VideoInput.Component;
        public AudioInput AudioInput = AudioInput.RcaBack;
        public AudioCodec AudioCodec = AudioCodec.Aac;
        public VideoStandard VideoStandard = VideoStandard.Ntsc60Hz;
        public BitrateMode BitrateMode = BitrateMode.Constant;
        public int Bitrate = 100;
        public int PeakBitrate = 135;
        public bool AudioBoost = false;
        public int Brightness = 0x80;
        public int Contrast = 0x40;
        public int Hue = 0x0f;
        public int Saturation = 0x40;
        public int Sharpness = 0x80;

        // App settings.
        public int UdpPort = 5004;
        public string RecordFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "PaugeChamp");
        public RecordFormat RecordFormat = RecordFormat.Mp4;
        public bool AutoStart = true;
        public bool StartMinimized = false;
        public bool ShowPreview = true;

        // True until picture controls have been saved once; the engine then fills in the
        // defaults that match the connected unit's firmware.
        public bool PictureIsDefault = true;

        public const int MinBitrate = 10, MaxBitrate = 135;
        public const int MinPeakBitrate = 11, MaxPeakBitrate = 202;

        public static readonly string[] DeviceKeys = {
            "video_input", "audio_input", "audio_codec", "video_standard", "bitrate_mode",
            "bitrate", "peak_bitrate", "audio_boost",
            "brightness", "contrast", "hue", "saturation", "sharpness" };

        public static readonly string[] AppKeys = { "udp_port", "record_folder", "record_format", "auto_start", "start_minimized", "show_preview" };

        public HdPvrSettings Clone()
        {
            return (HdPvrSettings)MemberwiseClone();
        }

        public static string SettingsPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PaugeChamp", "settings.ini");
            }
        }

        /// <summary>Firmware 0x16 and later use different picture-control defaults and a 0-30 hue range.</summary>
        public void ResetPicture(int firmwareVersion)
        {
            bool newFw = firmwareVersion > 0x15;
            Brightness = newFw ? 0x80 : 0x86;
            Contrast = newFw ? 0x40 : 0x80;
            Hue = newFw ? 0x0f : 0x80;
            Saturation = newFw ? 0x40 : 0x80;
            Sharpness = 0x80;
        }

        public static int MaxHue(int firmwareVersion)
        {
            return firmwareVersion > 0x15 ? 0x1e : 0xff;
        }

        /// <summary>Clamps every value into the range the device accepts.</summary>
        public void Normalize()
        {
            Bitrate = Clamp(Bitrate, MinBitrate, MaxBitrate);
            PeakBitrate = Clamp(PeakBitrate, MinPeakBitrate, MaxPeakBitrate);
            if (BitrateMode != BitrateMode.Constant && PeakBitrate <= Bitrate)
                PeakBitrate = Math.Min(Bitrate + 1, MaxPeakBitrate);
            Brightness = Clamp(Brightness, 0, 255);
            Contrast = Clamp(Contrast, 0, 255);
            Hue = Clamp(Hue, 0, 255);
            Saturation = Clamp(Saturation, 0, 255);
            Sharpness = Clamp(Sharpness, 0, 255);
            UdpPort = Clamp(UdpPort, 1, 65535);
        }

        static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        // ---- key=value text format ----

        public string Get(string key)
        {
            switch (key)
            {
                case "video_input": return VideoInput == VideoInput.Component ? "component" : VideoInput == VideoInput.SVideo ? "svideo" : "composite";
                case "audio_input": return AudioInput == AudioInput.RcaBack ? "rca_back" : AudioInput == AudioInput.RcaFront ? "rca_front" : "spdif";
                case "audio_codec": return AudioCodec == AudioCodec.Aac ? "aac" : "ac3";
                case "video_standard": return VideoStandard == VideoStandard.Ntsc60Hz ? "ntsc" : "pal";
                case "bitrate_mode": return BitrateMode == BitrateMode.Constant ? "cbr" : BitrateMode == BitrateMode.VariablePeak ? "vbr_peak" : "vbr";
                case "bitrate": return Mbps(Bitrate);
                case "peak_bitrate": return Mbps(PeakBitrate);
                case "audio_boost": return AudioBoost ? "1" : "0";
                case "brightness": return Brightness.ToString(CultureInfo.InvariantCulture);
                case "contrast": return Contrast.ToString(CultureInfo.InvariantCulture);
                case "hue": return Hue.ToString(CultureInfo.InvariantCulture);
                case "saturation": return Saturation.ToString(CultureInfo.InvariantCulture);
                case "sharpness": return Sharpness.ToString(CultureInfo.InvariantCulture);
                case "udp_port": return UdpPort.ToString(CultureInfo.InvariantCulture);
                case "record_folder": return RecordFolder;
                case "record_format": return RecordFormat == RecordFormat.Mp4 ? "mp4" : "ts";
                case "auto_start": return AutoStart ? "1" : "0";
                case "start_minimized": return StartMinimized ? "1" : "0";
                case "show_preview": return ShowPreview ? "1" : "0";
            }
            throw new ArgumentException("unknown setting '" + key + "'");
        }

        /// <summary>Sets one value from text; throws ArgumentException on bad input.</summary>
        public void Set(string key, string value)
        {
            string v = value.Trim();
            string lv = v.ToLowerInvariant();
            switch (key)
            {
                case "video_input":
                    VideoInput = Pick(lv, key, new[] { "component", "svideo", "composite" }, new[] { VideoInput.Component, VideoInput.SVideo, VideoInput.Composite });
                    break;
                case "audio_input":
                    AudioInput = Pick(lv, key, new[] { "rca_back", "rca_front", "spdif" }, new[] { AudioInput.RcaBack, AudioInput.RcaFront, AudioInput.Spdif });
                    break;
                case "audio_codec":
                    AudioCodec = Pick(lv, key, new[] { "aac", "ac3" }, new[] { AudioCodec.Aac, AudioCodec.Ac3 });
                    break;
                case "video_standard":
                    VideoStandard = Pick(lv, key, new[] { "ntsc", "pal" }, new[] { VideoStandard.Ntsc60Hz, VideoStandard.Pal50Hz });
                    break;
                case "bitrate_mode":
                    BitrateMode = Pick(lv, key, new[] { "cbr", "vbr", "vbr_peak" }, new[] { BitrateMode.Constant, BitrateMode.VariableAverage, BitrateMode.VariablePeak });
                    break;
                case "bitrate": Bitrate = ParseMbps(v, key); break;
                case "peak_bitrate": PeakBitrate = ParseMbps(v, key); break;
                case "audio_boost": AudioBoost = ParseBool(lv, key); break;
                case "brightness": Brightness = ParseInt(v, key); PictureIsDefault = false; break;
                case "contrast": Contrast = ParseInt(v, key); PictureIsDefault = false; break;
                case "hue": Hue = ParseInt(v, key); PictureIsDefault = false; break;
                case "saturation": Saturation = ParseInt(v, key); PictureIsDefault = false; break;
                case "sharpness": Sharpness = ParseInt(v, key); PictureIsDefault = false; break;
                case "udp_port": UdpPort = ParseInt(v, key); break;
                case "record_folder": RecordFolder = v; break;
                case "record_format":
                    RecordFormat = Pick(lv, key, new[] { "mp4", "ts" }, new[] { RecordFormat.Mp4, RecordFormat.Ts });
                    break;
                case "auto_start": AutoStart = ParseBool(lv, key); break;
                case "start_minimized": StartMinimized = ParseBool(lv, key); break;
                case "show_preview": ShowPreview = ParseBool(lv, key); break;
                case "picture_is_default": PictureIsDefault = ParseBool(lv, key); break;
                default: throw new ArgumentException("unknown setting '" + key + "'");
            }
        }

        /// <summary>Applies "key=value" lines. Blank lines and lines starting with # or ; are ignored.</summary>
        public void SetLines(IEnumerable<string> lines)
        {
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[')
                    continue;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    throw new ArgumentException("expected key=value, got '" + line + "'");
                Set(line.Substring(0, eq).Trim().ToLowerInvariant(), line.Substring(eq + 1));
            }
        }

        public string ToLines(bool includeApp)
        {
            var sb = new StringBuilder();
            foreach (string k in DeviceKeys)
                sb.Append(k).Append('=').Append(Get(k)).Append('\n');
            if (includeApp)
                foreach (string k in AppKeys)
                    sb.Append(k).Append('=').Append(Get(k)).Append('\n');
            return sb.ToString();
        }

        public static HdPvrSettings Load()
        {
            var s = new HdPvrSettings();
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var lines = File.ReadAllLines(SettingsPath);
                    s.PictureIsDefault = true;
                    foreach (string line in lines)
                    {
                        // Skip bad lines individually so one typo doesn't reset everything.
                        try { s.SetLines(new[] { line }); }
                        catch (ArgumentException) { }
                    }
                    s.Normalize();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                var text = "# PaugeChamp settings\n" + ToLines(true) +
                           "picture_is_default=" + (PictureIsDefault ? "1" : "0") + "\n";
                File.WriteAllText(SettingsPath, text.Replace("\n", "\r\n"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static string Mbps(int units)
        {
            return (units / 10.0).ToString("0.0", CultureInfo.InvariantCulture);
        }

        static int ParseMbps(string v, string key)
        {
            double d;
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new ArgumentException(key + ": expected a number in Mbps");
            return (int)Math.Round(d * 10);
        }

        static int ParseInt(string v, string key)
        {
            int i;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                throw new ArgumentException(key + ": expected an integer");
            return i;
        }

        static bool ParseBool(string v, string key)
        {
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;
            throw new ArgumentException(key + ": expected 1 or 0");
        }

        static T Pick<T>(string v, string key, string[] names, T[] values)
        {
            int i = Array.IndexOf(names, v);
            if (i < 0)
                throw new ArgumentException(key + ": expected one of " + string.Join(", ", names));
            return values[i];
        }
    }
}
