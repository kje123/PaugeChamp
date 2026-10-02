// Local control channel (\\.\pipe\paugechamp) used by the OBS script and the command line.
//
// Request: a command line, optionally followed by key=value lines. Response: "OK" or "ERR <message>"
// on the first line, followed by key=value lines.
//   status | get | set\n<key=value>... | start | stop | record start | record stop | show
using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace PaugeChamp
{
    public sealed class PipeServer : IDisposable
    {
        public const string PipeName = "paugechamp";

        readonly CaptureEngine engine;
        readonly Thread thread;
        volatile bool quit;

        public event EventHandler ShowRequested;

        public PipeServer(CaptureEngine engine)
        {
            this.engine = engine;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "HD PVR pipe server";
        }

        public void Start() { thread.Start(); }

        static PipeSecurity CreateSecurity()
        {
            var sec = new PipeSecurity();
            sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            return sec;
        }

        void Run()
        {
            while (!quit)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Message, PipeOptions.None, 65536, 65536, CreateSecurity()))
                    {
                        pipe.WaitForConnection();
                        if (quit)
                            break;
                        string request = ReadMessage(pipe);
                        byte[] reply = Encoding.UTF8.GetBytes(Handle(request));
                        pipe.Write(reply, 0, reply.Length);
                        pipe.Flush();
                        try { pipe.WaitForPipeDrain(); }
                        catch (IOException) { }
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (UnauthorizedAccessException)
                {
                    // Another instance owns the pipe name; nothing useful we can do.
                    return;
                }
            }
        }

        static string ReadMessage(NamedPipeServerStream pipe)
        {
            var ms = new MemoryStream();
            var buf = new byte[4096];
            do
            {
                int n = pipe.Read(buf, 0, buf.Length);
                if (n == 0)
                    break;
                ms.Write(buf, 0, n);
            } while (!pipe.IsMessageComplete);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        /// <summary>Executes one request and formats the reply. Also used for in-process CLI calls.</summary>
        public string Handle(string request)
        {
            string[] lines = request.Replace("\r", "").Split('\n');
            string cmd = lines[0].Trim().ToLowerInvariant();
            var rest = lines.Skip(1);
            switch (cmd)
            {
                case "ping":
                    return "OK\n";
                case "status":
                    return "OK\n" + engine.Status.ToLines();
                case "get":
                    return "OK\n" + engine.Settings.ToLines(true) + "max_hue=" +
                           HdPvrSettings.MaxHue(engine.Status.FirmwareVersion) + "\n";
                case "set":
                    {
                        string err = engine.ApplyText(rest);
                        return err == null ? "OK\n" + engine.Settings.ToLines(true) : "ERR " + err + "\n";
                    }
                case "start":
                    engine.SetStreaming(true);
                    return "OK\n";
                case "stop":
                    engine.SetStreaming(false);
                    return "OK\n";
                case "record start":
                    engine.StartRecording();
                    return "OK\n";
                case "record stop":
                    engine.StopRecording();
                    return "OK\n";
                case "show":
                    var h = ShowRequested;
                    if (h != null)
                        h(this, EventArgs.Empty);
                    return "OK\n";
            }
            return "ERR unknown command '" + cmd + "'\n";
        }

        /// <summary>Sends a request to a running instance. Returns null if none is running.</summary>
        public static string Send(string request, int timeoutMs)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                {
                    client.Connect(timeoutMs);
                    client.ReadMode = PipeTransmissionMode.Message;
                    byte[] req = Encoding.UTF8.GetBytes(request);
                    client.Write(req, 0, req.Length);
                    client.Flush();
                    var ms = new MemoryStream();
                    var buf = new byte[4096];
                    do
                    {
                        int n = client.Read(buf, 0, buf.Length);
                        if (n == 0)
                            break;
                        ms.Write(buf, 0, n);
                    } while (!client.IsMessageComplete);
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
            catch (TimeoutException) { return null; }
            catch (IOException) { return null; }
        }

        public void Dispose()
        {
            quit = true;
            // Unblock WaitForConnection by connecting to ourselves.
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                    c.Connect(200);
            }
            catch (Exception) { }
        }
    }
}
