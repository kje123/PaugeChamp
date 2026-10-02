// Entry point: single-instance GUI, command-line remote control, and the elevated driver fix-up.
using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace PaugeChamp
{
    public static class Program
    {
        /// <summary>Set by PaugeChamp.ps1 when the app is hosted in PowerShell rather than run as an .exe.</summary>
        public static string LauncherScript;

        /// <summary>Returns the program + argument prefix that re-launches this app.</summary>
        public static System.Diagnostics.ProcessStartInfo SelfStartInfo(string args)
        {
            if (LauncherScript == null)
                return new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, args);
            return new System.Diagnostics.ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + LauncherScript + "\" " + args);
        }

        const string Usage =
            "PaugeChamp.exe                        open the settings window (or bring it to front)\n" +
            "PaugeChamp.exe --minimized            start in the tray\n" +
            "PaugeChamp.exe status | get           print status / current settings\n" +
            "PaugeChamp.exe set key=value ...      change settings, e.g. set video_input=component bitrate=12\n" +
            "PaugeChamp.exe start | stop           start / stop the stream\n" +
            "PaugeChamp.exe record start|stop      start / stop a recording (format: record_format)\n\n" +
            "Keys: video_input=component|svideo|composite  audio_input=rca_back|rca_front|spdif\n" +
            "      audio_codec=aac|ac3  video_standard=ntsc|pal  bitrate_mode=cbr|vbr|vbr_peak\n" +
            "      bitrate=<Mbps 1-13.5>  peak_bitrate=<Mbps 1.1-20.2>  audio_boost=0|1\n" +
            "      brightness|contrast|hue|saturation|sharpness=<0-255>  udp_port=<n>  record_folder=<path>\n" +
            "      record_format=mp4|ts  show_preview=0|1\n";

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--register-interface")
                return RegisterInterface(args[1]);

            bool minimized = args.Contains("--minimized");
            string[] cli = args.Where(a => a != "--minimized").ToArray();
            if (cli.Length > 0)
                return RunCli(cli);

            bool created;
            using (var mutex = new Mutex(true, @"Local\PaugeChamp", out created))
            {
                if (!created)
                {
                    PipeServer.Send("show", 2000);
                    return 0;
                }

                try { Native.SetProcessDPIAware(); }
                catch (EntryPointNotFoundException) { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                HdPvrSettings settings = HdPvrSettings.Load();
                using (var engine = new CaptureEngine(settings))
                using (var pipe = new PipeServer(engine))
                {
                    var form = new MainForm(engine, pipe, minimized || settings.StartMinimized);
                    engine.Start();
                    pipe.Start();
                    Application.Run(form);
                }
            }
            return 0;
        }

        static int RunCli(string[] args)
        {
            Native.AttachConsole(-1);
            Console.WriteLine();
            string cmd = args[0].ToLowerInvariant();
            string request;
            if (cmd == "set")
                request = "set\n" + string.Join("\n", args.Skip(1));
            else if (cmd == "record" && args.Length > 1)
                request = "record " + args[1].ToLowerInvariant();
            else if (cmd == "status" || cmd == "get" || cmd == "start" || cmd == "stop")
                request = cmd;
            else
            {
                Console.Write(Usage);
                return cmd == "help" || cmd == "--help" || cmd == "/?" ? 0 : 2;
            }

            string reply = PipeServer.Send(request, 3000);
            if (reply == null)
            {
                Console.WriteLine("PaugeChamp is not running. Start it first (Start menu or PaugeChamp.ps1).");
                return 1;
            }
            Console.Write(reply);
            return reply.StartsWith("OK") ? 0 : 1;
        }

        static int RegisterInterface(string instanceId)
        {
            string err = DeviceFinder.RegisterInterface(instanceId);
            if (err != null)
            {
                MessageBox.Show(err, "HD PVR driver fix", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            return 0;
        }
    }
}
