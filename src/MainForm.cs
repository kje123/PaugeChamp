// Settings window with live preview + tray icon.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace PaugeChamp
{
    public sealed class MainForm : Form
    {
        readonly CaptureEngine engine;
        readonly PipeServer pipe;
        readonly NotifyIcon tray;
        readonly Timer applyTimer = new Timer();
        readonly Timer previewTimer = new Timer();
        bool loading, exiting, trayHintShown;
        int shownFirmware = -1;

        // Status
        readonly Label stateLabel = new Label();
        readonly Label messageLabel = new Label();
        readonly Label detailLabel = new Label();
        readonly Panel driverPanel = new Panel();
        readonly Button fixDriverButton = new Button();

        // Preview
        readonly PictureBox previewBox = new PictureBox();
        readonly Label previewOverlay = new Label();
        readonly CheckBox showPreview = new CheckBox();
        PreviewDecoder previewDecoder;
        DateTime lastPreviewFrame = DateTime.MinValue;
        string previewError;

        // Input
        readonly ComboBox videoInput = Combo("Component (YPbPr)", "S-Video", "Composite");
        readonly ComboBox audioInput = Combo("RCA - rear", "RCA - front", "Optical (S/PDIF)");
        readonly ComboBox audioCodec = Combo("AAC", "AC-3 (Dolby Digital)");
        readonly ComboBox videoStandard = Combo("NTSC / 60 Hz", "PAL / 50 Hz");
        readonly CheckBox audioBoost = new CheckBox();

        // Quality
        readonly ComboBox preset = Combo("Custom", "Low - 4 Mbps VBR", "Standard - 8 Mbps VBR", "High - 13.5 Mbps CBR", "Maximum - 13.5 Mbps VBR (20 peak)");
        readonly ComboBox bitrateMode = Combo("Constant (CBR)", "Variable (VBR)", "Variable, peak-limited");
        readonly NumericUpDown bitrate = Num(1.0m, 13.5m, 0.1m, 1);
        readonly NumericUpDown peakBitrate = Num(1.1m, 20.2m, 0.1m, 1);

        // Picture
        readonly TrackBar brightness = Track(255), contrast = Track(255), hue = Track(255), saturation = Track(255), sharpness = Track(255);
        readonly Label brightnessVal = new Label(), contrastVal = new Label(), hueVal = new Label(), saturationVal = new Label(), sharpnessVal = new Label();

        // Output
        readonly NumericUpDown udpPort = Num(1, 65535, 1, 0);
        readonly TextBox streamUrl = new TextBox();
        readonly TextBox recordFolder = new TextBox();
        readonly CheckBox autoStart = new CheckBox();
        readonly CheckBox startMinimized = new CheckBox();

        // Transport
        readonly Button streamButton = new Button();
        readonly Button recordButton = new Button();
        readonly ComboBox recordFormat = Combo("MP4 (.mp4)", "MPEG-TS (.ts)");

        public MainForm(CaptureEngine engine, PipeServer pipe, bool minimized)
        {
            this.engine = engine;
            this.pipe = pipe;

            Text = "PaugeChamp";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = MakeIcon();
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(10);

            BuildLayout();
            LoadSettings(engine.Settings);
            HookChanges();

            applyTimer.Interval = 300;
            applyTimer.Tick += delegate { applyTimer.Stop(); ApplyFromUi(); };
            previewTimer.Interval = 1000;
            previewTimer.Tick += delegate { UpdatePreviewOverlay(); };
            previewTimer.Start();

            tray = new NotifyIcon();
            tray.Icon = Icon;
            tray.Text = "PaugeChamp";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowFromTray(); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show window", null, delegate { ShowFromTray(); });
            menu.Items.Add("Start / stop stream", null, delegate { ToggleStream(); });
            menu.Items.Add("Start / stop recording", null, delegate { ToggleRecord(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { exiting = true; Close(); });
            tray.ContextMenuStrip = menu;

            engine.StatusChanged += delegate { SafeInvoke(UpdateStatus); };
            pipe.ShowRequested += delegate { SafeInvoke(ShowFromTray); };
            engine.SettingsChanged += OnEngineSettingsChanged;

            // Only decode while the window is actually visible.
            VisibleChanged += delegate { UpdatePreviewState(); };
            Resize += delegate { UpdatePreviewState(); };

            startHidden = minimized;
            UpdateStatus();
        }

        bool startHidden;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            BeginInvoke((Action)UpdateStatus);   // catch up on anything raised before the handle existed
        }

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                startHidden = false;
                if (!IsHandleCreated)
                    CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        // ---- layout ----

        void BuildLayout()
        {
            const int previewW = 640, previewH = 360;
            const int tabsW = 480;
            const int fullWidth = previewW + 10 + tabsW;
            const int groupW = tabsW - 30;

            var root = new FlowLayoutPanel();
            root.FlowDirection = FlowDirection.TopDown;
            root.WrapContents = false;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.Dock = DockStyle.Fill;
            Controls.Add(root);

            // Status
            var statusBox = new Panel();
            statusBox.Width = fullWidth;
            statusBox.Height = 78;
            statusBox.BackColor = Color.FromArgb(245, 246, 248);
            stateLabel.Font = new Font("Segoe UI Semibold", 11f);
            stateLabel.AutoSize = true;
            stateLabel.Location = new Point(10, 6);
            messageLabel.AutoSize = false;
            messageLabel.Location = new Point(10, 30);
            messageLabel.Size = new Size(fullWidth - 20, 20);
            messageLabel.AutoEllipsis = true;
            detailLabel.AutoSize = false;
            detailLabel.Location = new Point(10, 52);
            detailLabel.Size = new Size(fullWidth - 20, 20);
            detailLabel.AutoEllipsis = true;
            detailLabel.ForeColor = Color.DimGray;
            statusBox.Controls.AddRange(new Control[] { stateLabel, messageLabel, detailLabel });
            root.Controls.Add(statusBox);

            // Driver banner
            driverPanel.Width = fullWidth;
            driverPanel.Height = 40;
            driverPanel.BackColor = Color.FromArgb(255, 244, 214);
            driverPanel.Margin = new Padding(0, 6, 0, 0);
            var helpButton = new Button();
            helpButton.Text = "Driver setup help";
            helpButton.AutoSize = true;
            helpButton.Location = new Point(8, 7);
            helpButton.Click += delegate { ShowDriverHelp(); };
            fixDriverButton.Text = "Fix driver (admin)";
            fixDriverButton.AutoSize = true;
            fixDriverButton.Location = new Point(150, 7);
            fixDriverButton.Click += delegate { FixDriver(); };
            var devMgr = new Button();
            devMgr.Text = "Open Device Manager";
            devMgr.AutoSize = true;
            devMgr.Location = new Point(292, 7);
            devMgr.Click += delegate { Process.Start("devmgmt.msc"); };
            driverPanel.Controls.AddRange(new Control[] { helpButton, fixDriverButton, devMgr });
            driverPanel.Visible = false;
            root.Controls.Add(driverPanel);

            var columns = new FlowLayoutPanel();
            columns.AutoSize = true;
            columns.WrapContents = false;
            columns.Margin = new Padding(0, 8, 0, 0);
            var left = Column();
            root.Controls.Add(columns);
            columns.Controls.Add(left);

            // ---- left: preview + transport ----
            previewBox.Size = new Size(previewW, previewH);
            previewBox.BackColor = Color.Black;
            previewBox.SizeMode = PictureBoxSizeMode.Zoom;
            previewBox.Margin = Padding.Empty;
            previewOverlay.Parent = previewBox;
            previewOverlay.Dock = DockStyle.Fill;
            previewOverlay.BackColor = Color.Transparent;
            previewOverlay.ForeColor = Color.Gainsboro;
            previewOverlay.Font = new Font("Segoe UI", 11f);
            previewOverlay.TextAlign = ContentAlignment.MiddleCenter;
            left.Controls.Add(previewBox);

            showPreview.Text = "Show preview";
            showPreview.AutoSize = true;
            showPreview.Margin = new Padding(0, 6, 0, 0);
            left.Controls.Add(showPreview);

            var transport = new FlowLayoutPanel();
            transport.AutoSize = true;
            transport.WrapContents = false;
            transport.Margin = new Padding(0, 8, 0, 0);
            streamButton.AutoSize = true;
            streamButton.MinimumSize = new Size(120, 32);
            streamButton.Click += delegate { ToggleStream(); };
            recordButton.AutoSize = true;
            recordButton.MinimumSize = new Size(130, 32);
            recordButton.Click += delegate { ToggleRecord(); };
            recordFormat.Width = 130;
            recordFormat.Margin = new Padding(3, 7, 12, 3);
            var openFolder = new Button();
            openFolder.Text = "Open recordings";
            openFolder.AutoSize = true;
            openFolder.MinimumSize = new Size(120, 32);
            openFolder.Click += delegate
            {
                string f = engine.Settings.RecordFolder;
                Directory.CreateDirectory(f);
                Process.Start("explorer.exe", "\"" + f + "\"");
            };
            transport.Controls.AddRange(new Control[] { streamButton, recordButton, recordFormat, openFolder });
            left.Controls.Add(transport);

            // ---- right: settings tabs ----
            var tabs = new TabControl();
            tabs.Size = new Size(tabsW, previewH + 80);
            tabs.Margin = new Padding(10, 0, 0, 0);
            columns.Controls.Add(tabs);

            audioBoost.Text = "Boost analog audio level";
            audioBoost.AutoSize = true;
            tabs.TabPages.Add(Page("Input && quality",
                Group("Input", groupW,
                    Row("Video input", videoInput),
                    Row("Audio input", audioInput),
                    Row("Audio format", audioCodec),
                    Row("Analog standard", videoStandard),
                    Row("", audioBoost)),
                Group("Recording quality (H.264, encoded on the HD PVR)", groupW,
                    Row("Preset", preset),
                    Row("Bitrate mode", bitrateMode),
                    Row("Average (Mbps)", bitrate),
                    Row("Peak (Mbps)", peakBitrate))));

            var reset = new Button();
            reset.Text = "Reset to defaults";
            reset.AutoSize = true;
            reset.Click += delegate
            {
                var s = engine.Settings;
                s.ResetPicture(engine.Status.FirmwareVersion > 0 ? engine.Status.FirmwareVersion : 0x1e);
                s.PictureIsDefault = true;
                engine.Apply(s);
                LoadSettings(s);
            };
            tabs.TabPages.Add(Page("Picture",
                Group("Picture adjustments", groupW,
                    TrackRow("Brightness", brightness, brightnessVal),
                    TrackRow("Contrast", contrast, contrastVal),
                    TrackRow("Hue", hue, hueVal),
                    TrackRow("Saturation", saturation, saturationVal),
                    TrackRow("Sharpness", sharpness, sharpnessVal),
                    Row("", reset))));

            streamUrl.ReadOnly = true;
            streamUrl.Width = 220;
            var copy = new Button();
            copy.Text = "Copy";
            copy.AutoSize = true;
            copy.Click += delegate { Clipboard.SetText(streamUrl.Text); };
            recordFolder.Width = 220;
            var browse = new Button();
            browse.Text = "Browse...";
            browse.AutoSize = true;
            browse.Click += delegate { BrowseFolder(); };
            autoStart.Text = "Start streaming automatically";
            autoStart.AutoSize = true;
            startMinimized.Text = "Start minimized to the tray";
            startMinimized.AutoSize = true;
            tabs.TabPages.Add(Page("Output",
                Group("Stream for OBS", groupW,
                    Row("UDP port", udpPort),
                    Row("Media URL", Inline(streamUrl, copy))),
                Group("Recording", groupW,
                    Row("Folder", Inline(recordFolder, browse))),
                Group("Startup", groupW,
                    Row("", autoStart),
                    Row("", startMinimized))));
        }

        static TabPage Page(string title, params Control[] groups)
        {
            var page = new TabPage(title);
            page.Padding = new Padding(6);
            page.UseVisualStyleBackColor = true;
            var flow = Column();
            flow.Dock = DockStyle.Fill;
            foreach (Control g in groups)
                flow.Controls.Add(g);
            page.Controls.Add(flow);
            return page;
        }

        static GroupBox Group(string title, int width, params Control[] rows)
        {
            var g = new GroupBox();
            g.Text = title;
            g.MinimumSize = new Size(width, 0);
            g.AutoSize = true;
            g.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            g.Margin = new Padding(0, 0, 0, 8);
            var t = new TableLayoutPanel();
            t.ColumnCount = 1;
            t.AutoSize = true;
            t.Dock = DockStyle.Fill;
            t.Padding = new Padding(4, 2, 4, 4);
            foreach (Control r in rows)
                t.Controls.Add(r);
            g.Controls.Add(t);
            return g;
        }

        static FlowLayoutPanel Column()
        {
            var p = new FlowLayoutPanel();
            p.FlowDirection = FlowDirection.TopDown;
            p.WrapContents = false;
            p.AutoSize = true;
            p.Margin = Padding.Empty;
            return p;
        }

        static Control Inline(params Control[] controls)
        {
            var p = new FlowLayoutPanel();
            p.AutoSize = true;
            p.WrapContents = false;
            p.Margin = Padding.Empty;
            p.Controls.AddRange(controls);
            return p;
        }

        static Control Row(string label, Control c)
        {
            var p = new FlowLayoutPanel();
            p.AutoSize = true;
            p.WrapContents = false;
            p.Margin = new Padding(0, 1, 0, 1);
            var l = new Label();
            l.Text = label;
            l.Width = 115;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Height = 26;
            p.Controls.Add(l);
            p.Controls.Add(c);
            return p;
        }

        static Control TrackRow(string label, TrackBar t, Label value)
        {
            value.Width = 40;
            value.Height = 26;
            value.TextAlign = ContentAlignment.MiddleLeft;
            var p = (FlowLayoutPanel)Row(label, t);
            p.Controls.Add(value);
            return p;
        }

        static ComboBox Combo(params string[] items)
        {
            var c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Width = 240;
            c.Items.AddRange(items);
            return c;
        }

        static NumericUpDown Num(decimal min, decimal max, decimal step, int decimals)
        {
            var n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Increment = step;
            n.DecimalPlaces = decimals;
            n.Width = 80;
            return n;
        }

        static TrackBar Track(int max)
        {
            var t = new TrackBar();
            t.Minimum = 0;
            t.Maximum = max;
            t.TickStyle = TickStyle.None;
            t.Width = 260;
            t.Height = 26;
            t.AutoSize = false;
            t.LargeChange = 8;
            return t;
        }

        // ---- settings <-> controls ----

        void LoadSettings(HdPvrSettings s)
        {
            loading = true;
            try
            {
                videoInput.SelectedIndex = (int)s.VideoInput;
                audioInput.SelectedIndex = (int)s.AudioInput;
                audioCodec.SelectedIndex = (int)s.AudioCodec;
                videoStandard.SelectedIndex = (int)s.VideoStandard;
                audioBoost.Checked = s.AudioBoost;
                bitrateMode.SelectedIndex = s.BitrateMode == BitrateMode.Constant ? 0 : s.BitrateMode == BitrateMode.VariableAverage ? 1 : 2;
                bitrate.Value = Math.Max(bitrate.Minimum, Math.Min(bitrate.Maximum, s.Bitrate / 10m));
                peakBitrate.Value = Math.Max(peakBitrate.Minimum, Math.Min(peakBitrate.Maximum, s.PeakBitrate / 10m));
                SetTrack(brightness, brightnessVal, s.Brightness);
                SetTrack(contrast, contrastVal, s.Contrast);
                SetTrack(hue, hueVal, s.Hue);
                SetTrack(saturation, saturationVal, s.Saturation);
                SetTrack(sharpness, sharpnessVal, s.Sharpness);
                udpPort.Value = s.UdpPort;
                streamUrl.Text = "udp://127.0.0.1:" + s.UdpPort;
                recordFolder.Text = s.RecordFolder;
                recordFormat.SelectedIndex = (int)s.RecordFormat;
                autoStart.Checked = s.AutoStart;
                startMinimized.Checked = s.StartMinimized;
                showPreview.Checked = s.ShowPreview;
                preset.SelectedIndex = MatchPreset(s);
                peakBitrate.Enabled = s.BitrateMode != BitrateMode.Constant;
            }
            finally
            {
                loading = false;
            }
            UpdatePreviewState();
        }

        static void SetTrack(TrackBar t, Label l, int v)
        {
            t.Value = Math.Max(t.Minimum, Math.Min(t.Maximum, v));
            l.Text = t.Value.ToString();
        }

        HdPvrSettings ReadControls()
        {
            var s = engine.Settings;
            s.VideoInput = (VideoInput)videoInput.SelectedIndex;
            s.AudioInput = (AudioInput)audioInput.SelectedIndex;
            s.AudioCodec = (AudioCodec)audioCodec.SelectedIndex;
            s.VideoStandard = (VideoStandard)videoStandard.SelectedIndex;
            s.AudioBoost = audioBoost.Checked;
            s.BitrateMode = bitrateMode.SelectedIndex == 0 ? BitrateMode.Constant :
                            bitrateMode.SelectedIndex == 1 ? BitrateMode.VariableAverage : BitrateMode.VariablePeak;
            s.Bitrate = (int)Math.Round(bitrate.Value * 10);
            s.PeakBitrate = (int)Math.Round(peakBitrate.Value * 10);
            if (brightness.Value != s.Brightness || contrast.Value != s.Contrast || hue.Value != s.Hue ||
                saturation.Value != s.Saturation || sharpness.Value != s.Sharpness)
                s.PictureIsDefault = false;
            s.Brightness = brightness.Value;
            s.Contrast = contrast.Value;
            s.Hue = hue.Value;
            s.Saturation = saturation.Value;
            s.Sharpness = sharpness.Value;
            s.UdpPort = (int)udpPort.Value;
            s.RecordFolder = recordFolder.Text.Trim();
            s.RecordFormat = (RecordFormat)recordFormat.SelectedIndex;
            s.AutoStart = autoStart.Checked;
            s.StartMinimized = startMinimized.Checked;
            s.ShowPreview = showPreview.Checked;
            return s;
        }

        static readonly int[,] Presets = {
            // mode, avg, peak (100 kbit/s units)
            { 3, 40, 60 },
            { 3, 80, 120 },
            { 1, 135, 135 },
            { 3, 135, 202 } };

        static int MatchPreset(HdPvrSettings s)
        {
            for (int i = 0; i < Presets.GetLength(0); i++)
            {
                bool cbr = Presets[i, 0] == 1;
                if ((int)s.BitrateMode == Presets[i, 0] && s.Bitrate == Presets[i, 1] && (cbr || s.PeakBitrate == Presets[i, 2]))
                    return i + 1;
            }
            return 0;
        }

        void HookChanges()
        {
            EventHandler changed = delegate { if (!loading) applyTimer.Stop(); if (!loading) applyTimer.Start(); };
            foreach (ComboBox c in new[] { videoInput, audioInput, audioCodec, videoStandard, bitrateMode, recordFormat })
                c.SelectedIndexChanged += changed;
            foreach (CheckBox c in new[] { audioBoost, autoStart, startMinimized, showPreview })
                c.CheckedChanged += changed;
            bitrate.ValueChanged += changed;
            peakBitrate.ValueChanged += changed;
            udpPort.ValueChanged += changed;
            recordFolder.Leave += changed;
            showPreview.CheckedChanged += delegate { if (!loading) UpdatePreviewState(); };

            var tracks = new[] { brightness, contrast, hue, saturation, sharpness };
            var labels = new[] { brightnessVal, contrastVal, hueVal, saturationVal, sharpnessVal };
            for (int i = 0; i < tracks.Length; i++)
            {
                TrackBar t = tracks[i];
                Label l = labels[i];
                t.ValueChanged += delegate { l.Text = t.Value.ToString(); };
                t.ValueChanged += changed;
            }

            bitrateMode.SelectedIndexChanged += delegate { peakBitrate.Enabled = bitrateMode.SelectedIndex != 0; };
            preset.SelectedIndexChanged += delegate
            {
                if (loading || preset.SelectedIndex <= 0)
                    return;
                int p = preset.SelectedIndex - 1;
                loading = true;
                bitrateMode.SelectedIndex = Presets[p, 0] == 1 ? 0 : 1;
                bitrate.Value = Presets[p, 1] / 10m;
                peakBitrate.Value = Presets[p, 2] / 10m;
                peakBitrate.Enabled = Presets[p, 0] != 1;
                loading = false;
                ApplyFromUi();
            };
        }

        void ApplyFromUi()
        {
            HdPvrSettings s = ReadControls();
            string err = engine.Apply(s);
            if (err != null)
                messageLabel.Text = err;
            // Reflect normalization (e.g. peak raised above average) and preset matching.
            LoadSettings(engine.Settings);
        }

        void OnEngineSettingsChanged(object sender, EventArgs e)
        {
            // Changes from OBS / the command line arrive on other threads; ours are already shown.
            if (InvokeRequired)
                SafeInvoke(delegate { if (!applyTimer.Enabled) LoadSettings(engine.Settings); });
        }

        // ---- preview ----

        void UpdatePreviewState()
        {
            bool want = showPreview.Checked && Visible && WindowState != FormWindowState.Minimized && previewError == null;
            if (want && previewDecoder == null)
            {
                var dec = new PreviewDecoder();
                dec.FrameReady += delegate (Bitmap bmp) { OnPreviewFrame(dec, bmp); };
                dec.Failed += delegate (string msg) { SafeInvoke(delegate { previewError = msg; UpdatePreviewState(); }); };
                previewDecoder = dec;
                engine.SetPreview(dec);
            }
            else if (!want && previewDecoder != null)
            {
                engine.SetPreview(null);
                previewDecoder.Dispose();
                previewDecoder = null;
                SetPreviewImage(null);
            }
            UpdatePreviewOverlay();
        }

        void OnPreviewFrame(PreviewDecoder source, Bitmap bmp)
        {
            // Decoder thread.
            if (IsDisposed || !IsHandleCreated)
            {
                bmp.Dispose();
                return;
            }
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (previewDecoder != source)
                    {
                        bmp.Dispose();
                        return;
                    }
                    SetPreviewImage(bmp);
                    lastPreviewFrame = DateTime.UtcNow;
                    if (previewOverlay.Visible)
                        previewOverlay.Visible = false;
                    source.FrameConsumed();
                });
            }
            catch (InvalidOperationException)
            {
                bmp.Dispose();
            }
        }

        void SetPreviewImage(Image img)
        {
            Image old = previewBox.Image;
            previewBox.Image = img;
            if (old != null)
                old.Dispose();
        }

        void UpdatePreviewOverlay()
        {
            bool live = previewDecoder != null && (DateTime.UtcNow - lastPreviewFrame).TotalSeconds < 2;
            if (live)
            {
                previewOverlay.Visible = false;
                return;
            }
            if (previewBox.Image != null && previewDecoder != null)
                SetPreviewImage(null);   // don't leave a frozen frame up

            string text;
            EngineStatus st = engine.Status;
            if (previewError != null)
                text = "Preview unavailable\n" + previewError;
            else if (!showPreview.Checked)
                text = "Preview off";
            else if (st.State == EngineState.Streaming)
                text = "Waiting for video...";
            else if (st.State == EngineState.WaitingForSignal || (st.State == EngineState.Idle && !st.Signal.Valid))
                text = "No signal";
            else if (st.State == EngineState.Idle)
                text = "Stream stopped - click Start stream";
            else
                text = "HD PVR not ready";
            previewOverlay.Text = text;
            previewOverlay.Visible = true;
        }

        // ---- status ----

        void UpdateStatus()
        {
            EngineStatus st = engine.Status;
            string state;
            Color color;
            switch (st.State)
            {
                case EngineState.Streaming: state = st.Recording ? "● Recording" : "● Streaming"; color = st.Recording ? Color.Firebrick : Color.ForestGreen; break;
                case EngineState.Idle: state = "● Connected"; color = Color.SteelBlue; break;
                case EngineState.WaitingForSignal: state = "● Waiting for signal"; color = Color.DarkOrange; break;
                case EngineState.DriverSetupNeeded: state = "● Driver setup needed"; color = Color.DarkOrange; break;
                case EngineState.Error: state = "● Error"; color = Color.Firebrick; break;
                default: state = "● Searching for HD PVR"; color = Color.Gray; break;
            }
            stateLabel.Text = state;
            stateLabel.ForeColor = color;
            messageLabel.Text = st.Message ?? "";

            string detail = "";
            if (st.FirmwareVersion > 0)
                detail += "Firmware 0x" + st.FirmwareVersion.ToString("X2") + "   ";
            if (st.Signal.Valid)
                detail += "Input " + st.Signal + "   ";
            if (st.State == EngineState.Streaming)
                detail += st.Mbps.ToString("0.0") + " Mbps   ";
            if (st.Recording)
                detail += (st.RecordBytes / 1048576.0).ToString("0") + " MB  " + Path.GetFileName(st.RecordPath);
            detailLabel.Text = detail;

            driverPanel.Visible = st.State == EngineState.DriverSetupNeeded;
            fixDriverButton.Enabled = st.Device != null && st.Device.IsWinUsb;
            streamButton.Text = st.StreamWanted ? "Stop stream" : "Start stream";
            recordButton.Text = st.Recording ? "■ Stop recording" : "● Record";
            recordButton.ForeColor = st.Recording ? Color.Firebrick : SystemColors.ControlText;
            recordFormat.Enabled = !st.Recording;
            tray.Text = Truncate("PaugeChamp - " + state.Substring(2), 63);

            if (st.FirmwareVersion > 0 && st.FirmwareVersion != shownFirmware)
            {
                shownFirmware = st.FirmwareVersion;
                hue.Maximum = HdPvrSettings.MaxHue(st.FirmwareVersion);
                audioCodec.Enabled = st.FirmwareVersion >= 0x0d;
                LoadSettings(engine.Settings);   // picture defaults may have been filled in for this firmware
            }
            UpdatePreviewOverlay();
        }

        static string Truncate(string s, int n)
        {
            return s.Length <= n ? s : s.Substring(0, n);
        }

        // ---- actions ----

        void ToggleStream()
        {
            engine.SetStreaming(!engine.Status.StreamWanted);
        }

        void ToggleRecord()
        {
            if (engine.Status.Recording)
                engine.StopRecording();
            else
            {
                ApplyFromUi();
                engine.StartRecording();
            }
        }

        void BrowseFolder()
        {
            using (var d = new FolderBrowserDialog())
            {
                d.SelectedPath = recordFolder.Text;
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    recordFolder.Text = d.SelectedPath;
                    ApplyFromUi();
                }
            }
        }

        void ShowDriverHelp()
        {
            MessageBox.Show(this,
                "This app talks to the HD PVR through Microsoft's built-in WinUSB driver instead of the 2012 Hauppauge driver.\n\n" +
                "Option A - no downloads:\n" +
                "  1. Open Device Manager and find 'Hauppauge HD PVR Capture Device' (Sound, video and game controllers).\n" +
                "  2. Right-click > Update driver > Browse my computer > Let me pick from a list.\n" +
                "  3. Untick 'Show compatible hardware'. Choose 'Universal Serial Bus devices' then 'WinUsb Device'. Accept the warning.\n" +
                "  4. Come back here and click 'Fix driver (admin)'.\n\n" +
                "Option B - Zadig (zadig.akeo.ie): Options > List All Devices, pick the HD PVR, choose WinUSB, click Replace Driver.\n\n" +
                "To go back to the Hauppauge driver: Device Manager > Update driver > Let me pick > 'Hauppauge HD PVR Capture Device'.",
                "Driver setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void FixDriver()
        {
            DeviceCandidate d = engine.Status.Device;
            if (d == null)
                return;
            try
            {
                var psi = Program.SelfStartInfo("--register-interface \"" + d.InstanceId + "\"");
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                using (var p = Process.Start(psi))
                    p.WaitForExit(30000);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return;   // UAC prompt cancelled
            }
            engine.Rescan();
        }

        // ---- tray / lifetime ----

        void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                // Keep running in the tray so OBS keeps receiving video.
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(3000, "PaugeChamp", "Still running in the tray. Right-click the icon to exit.", ToolTipIcon.Info);
                }
                return;
            }
            applyTimer.Stop();
            previewTimer.Stop();
            if (previewDecoder != null)
            {
                engine.SetPreview(null);
                previewDecoder.Dispose();
                previewDecoder = null;
            }
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        void SafeInvoke(Action a)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            try { BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var body = new SolidBrush(Color.FromArgb(40, 44, 52)))
                        g.FillRectangle(body, 1, 8, 30, 17);
                    using (var led = new SolidBrush(Color.FromArgb(64, 160, 255)))
                        g.FillEllipse(led, 4, 13, 7, 7);
                    using (var red = new SolidBrush(Color.FromArgb(230, 60, 60)))
                        g.FillEllipse(red, 22, 13, 7, 7);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
