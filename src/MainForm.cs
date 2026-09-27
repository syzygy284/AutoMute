using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace AutoMute
{
    /// <summary>Settings window with live meters. Every change applies instantly.</summary>
    public sealed class MainForm : Form
    {
        private readonly TrayContext ctx;
        private readonly Label lblStatus, lblDevice, lblPitch, lblHighCaption, lblFile;
        private readonly LevelMeter meterLevel, meterHigh;
        private readonly NumericUpDown numThreshold, numWindow, numMute, numCutoff, numFreqThr;
        private readonly CheckBox chkExtend, chkFreq, chkEnabled, chkStartup;
        private readonly Button btnUnmute;
        private readonly ListBox lstLog;
        private readonly Timer uiTimer;
        private readonly ToolTip tips = new ToolTip();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long lastTickMs;
        private bool loading;
        private readonly int u;   // layout unit = font height, so everything scales with DPI

        public MainForm(TrayContext ctx)
        {
            this.ctx = ctx;
            SuspendLayout();
            Text = "AutoMute";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            tips.AutoPopDelay = 15000;

            u = Font.Height;
            int wide = u * 30;
            Padding = new Padding(u / 2);

            TableLayoutPanel root = new TableLayoutPanel();
            root.ColumnCount = 1;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.Location = new Point(u / 2, u / 2);

            // --- status + meters ---
            lblStatus = FixedLabel(wide);
            lblStatus.Font = new Font(Font.FontFamily, Font.Size * 1.15f, FontStyle.Bold);
            lblStatus.Height = (int)(u * 1.5);
            root.Controls.Add(lblStatus);
            lblDevice = FixedLabel(wide);
            lblDevice.ForeColor = SystemColors.GrayText;
            root.Controls.Add(lblDevice);

            root.Controls.Add(Caption("Volume"));
            meterLevel = new LevelMeter();
            meterLevel.Size = new Size(wide, u * 2);
            meterLevel.MinDb = -60f;
            meterLevel.Margin = new Padding(3, 0, 3, u / 3);
            meterLevel.ThresholdDragged += delegate { SetNumeric(numThreshold, (decimal)meterLevel.Threshold); };
            tips.SetToolTip(meterLevel, "Current loudness. Click or drag to set the mute threshold (yellow marker).");
            root.Controls.Add(meterLevel);

            lblHighCaption = Caption("High band");
            root.Controls.Add(lblHighCaption);
            meterHigh = new LevelMeter();
            meterHigh.Size = new Size(wide, u * 2);
            meterHigh.MinDb = -90f;
            meterHigh.Margin = new Padding(3, 0, 3, 0);
            meterHigh.ThresholdDragged += delegate { SetNumeric(numFreqThr, (decimal)meterHigh.Threshold); };
            tips.SetToolTip(meterHigh, "Energy above the cutoff frequency. Click or drag to set its threshold.");
            root.Controls.Add(meterHigh);
            lblPitch = FixedLabel(wide);
            lblPitch.ForeColor = SystemColors.GrayText;
            root.Controls.Add(lblPitch);

            // --- loudness trigger ---
            TableLayoutPanel g1 = Section(root, "Loudness trigger");
            numThreshold = Numeric(-100m, 0m, 0.5m, 1);
            AddRow(g1, "Mute above", numThreshold, "dBFS  (or drag the marker)");
            numThreshold.ValueChanged += delegate
            {
                meterLevel.Threshold = (float)numThreshold.Value;
                if (!loading) ctx.Update(delegate(Settings s) { s.ThresholdDb = (double)numThreshold.Value; });
            };
            numWindow = Numeric(0m, 5000m, 10m, 0);
            AddRow(g1, "Averaging", numWindow, "ms  (0 = instant)");
            tips.SetToolTip(numWindow, "Average loudness over this long before comparing. Higher values ignore short clicks and pops (you may need a lower threshold).");
            numWindow.ValueChanged += delegate
            {
                if (!loading) ctx.Update(delegate(Settings s) { s.WindowMs = (int)numWindow.Value; });
            };

            // --- muting ---
            TableLayoutPanel g2 = Section(root, "Muting");
            numMute = Numeric(0.1m, 3600m, 0.5m, 1);
            AddRow(g2, "Unmute after", numMute, "seconds");
            numMute.ValueChanged += delegate
            {
                if (!loading) ctx.Update(delegate(Settings s) { s.MuteSeconds = (double)numMute.Value; });
            };
            chkExtend = new CheckBox();
            chkExtend.Text = "Stay muted while it's still loud (timer restarts on each trigger)";
            chkExtend.AutoSize = true;
            AddSpanRow(g2, chkExtend);
            chkExtend.CheckedChanged += delegate
            {
                if (!loading) ctx.Update(delegate(Settings s) { s.ExtendWhileLoud = chkExtend.Checked; });
            };

            // --- high-pitch trigger ---
            TableLayoutPanel g3 = Section(root, "High-pitch trigger (experimental)");
            chkFreq = new CheckBox();
            chkFreq.Text = "Enabled";
            chkFreq.AutoSize = true;
            AddSpanRow(g3, chkFreq);
            chkFreq.CheckedChanged += delegate
            {
                meterHigh.Active = chkFreq.Checked;
                if (!loading) ctx.Update(delegate(Settings s) { s.FreqEnabled = chkFreq.Checked; });
            };
            numCutoff = Numeric(100m, 20000m, 100m, 0);
            AddRow(g3, "Frequencies above", numCutoff, "Hz");
            numCutoff.ValueChanged += delegate
            {
                lblHighCaption.Text = "High band (above " + numCutoff.Value + " Hz)";
                if (!loading) ctx.Update(delegate(Settings s) { s.FreqCutoffHz = (int)numCutoff.Value; });
            };
            numFreqThr = Numeric(-120m, 0m, 0.5m, 1);
            AddRow(g3, "Mute above", numFreqThr, "dBFS  (or drag the marker)");
            numFreqThr.ValueChanged += delegate
            {
                meterHigh.Threshold = (float)numFreqThr.Value;
                if (!loading) ctx.Update(delegate(Settings s) { s.FreqThresholdDb = (double)numFreqThr.Value; });
            };

            // --- general ---
            FlowLayoutPanel bottom = new FlowLayoutPanel();
            bottom.AutoSize = true;
            bottom.WrapContents = false;
            bottom.Margin = new Padding(0, u / 3, 0, 0);
            chkEnabled = new CheckBox();
            chkEnabled.Text = "Monitoring enabled";
            chkEnabled.AutoSize = true;
            chkEnabled.Margin = new Padding(3, 6, u, 3);
            chkEnabled.CheckedChanged += delegate
            {
                if (!loading) ctx.Update(delegate(Settings s) { s.Enabled = chkEnabled.Checked; });
            };
            chkStartup = new CheckBox();
            chkStartup.Text = "Start with Windows";
            chkStartup.AutoSize = true;
            chkStartup.Margin = new Padding(3, 6, u, 3);
            chkStartup.CheckedChanged += delegate
            {
                if (!loading) ctx.ToggleStartup(chkStartup.Checked);
            };
            btnUnmute = new Button();
            btnUnmute.Text = "Unmute now";
            btnUnmute.AutoSize = true;
            btnUnmute.Click += delegate { ctx.Engine.RequestUnmute(); };
            bottom.Controls.Add(chkEnabled);
            bottom.Controls.Add(chkStartup);
            bottom.Controls.Add(btnUnmute);
            root.Controls.Add(bottom);

            root.Controls.Add(Caption("Activity"));
            lstLog = new ListBox();
            lstLog.Size = new Size(wide, u * 7);
            lstLog.IntegralHeight = false;
            lstLog.HorizontalScrollbar = true;
            root.Controls.Add(lstLog);
            foreach (string line in ctx.LogLines) lstLog.Items.Add(line);
            ScrollLogToEnd();

            lblFile = FixedLabel(wide);
            lblFile.ForeColor = SystemColors.GrayText;
            lblFile.Text = "Settings file: " + Settings.FilePath + "   -   closing this window keeps AutoMute running in the tray";
            lblFile.Height = u * 2;
            root.Controls.Add(lblFile);

            Controls.Add(root);
            ResumeLayout(true);

            LoadFromSettings();
            ctx.SettingsChanged += OnSettingsChanged;
            ctx.LogAdded += OnLogAdded;

            uiTimer = new Timer();
            uiTimer.Interval = 40;
            uiTimer.Tick += OnUiTick;
        }

        // ------------------------------------------------------------------
        private void LoadFromSettings()
        {
            loading = true;
            try
            {
                Settings s = ctx.Current;
                SetNumeric(numThreshold, (decimal)s.ThresholdDb);
                SetNumeric(numWindow, s.WindowMs);
                SetNumeric(numMute, (decimal)s.MuteSeconds);
                SetNumeric(numCutoff, s.FreqCutoffHz);
                SetNumeric(numFreqThr, (decimal)s.FreqThresholdDb);
                chkExtend.Checked = s.ExtendWhileLoud;
                chkFreq.Checked = s.FreqEnabled;
                chkEnabled.Checked = s.Enabled;
                chkStartup.Checked = StartupManager.IsEnabled();
                meterLevel.Threshold = (float)s.ThresholdDb;
                meterLevel.Active = s.Enabled;
                meterHigh.Threshold = (float)s.FreqThresholdDb;
                meterHigh.Active = s.Enabled && s.FreqEnabled;
                lblHighCaption.Text = "High band (above " + s.FreqCutoffHz + " Hz)";
            }
            finally
            {
                loading = false;
            }
        }

        private void OnSettingsChanged(object sender, EventArgs e)
        {
            LoadFromSettings();
        }

        private void OnLogAdded(string line)
        {
            lstLog.Items.Add(line);
            while (lstLog.Items.Count > 300) lstLog.Items.RemoveAt(0);
            ScrollLogToEnd();
        }

        private void ScrollLogToEnd()
        {
            if (lstLog.Items.Count > 0) lstLog.TopIndex = lstLog.Items.Count - 1;
        }

        private void OnUiTick(object sender, EventArgs e)
        {
            long now = clock.ElapsedMilliseconds;
            float dt = Math.Min(0.5f, (now - lastTickMs) / 1000f);
            lastTickMs = now;

            EngineSnapshot snap = ctx.Engine.Snapshot(true);
            meterLevel.Push(snap.LevelDb, dt);
            meterHigh.Push(snap.HighDb, dt);

            string pitch = snap.DominantHz > 0 ? "Dominant pitch: " + snap.DominantHz.ToString("0") + " Hz" : "Dominant pitch: -";
            SetText(lblPitch, pitch);
            SetText(lblDevice, "Output: " + snap.Device);

            string status;
            Color color;
            if (snap.MutedByUs)
            {
                status = "MUTED  -  unmuting in " + Math.Max(0f, snap.RemainingSeconds).ToString("0.0") + " s";
                color = Color.FromArgb(200, 40, 40);
            }
            else if (!ctx.Current.Enabled)
            {
                status = "Paused (not muting)";
                color = SystemColors.GrayText;
            }
            else
            {
                status = snap.Status;
                color = snap.Status == "Monitoring" ? Color.FromArgb(30, 130, 60) : Color.FromArgb(180, 120, 0);
            }
            SetText(lblStatus, status);
            if (lblStatus.ForeColor != color) lblStatus.ForeColor = color;
            if (btnUnmute.Enabled != snap.MutedByUs) btnUnmute.Enabled = snap.MutedByUs;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            ctx.Engine.SetWantSpectrum(Visible);   // only do the FFT while someone is looking (or the trigger is on)
            if (Visible)
            {
                ctx.Engine.Snapshot(true);
                lastTickMs = clock.ElapsedMilliseconds;
                loading = true;
                chkStartup.Checked = StartupManager.IsEnabled();
                loading = false;
                uiTimer.Start();
            }
            else
            {
                uiTimer.Stop();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!ctx.IsExiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;     // X just hides to the tray
                ctx.HideForm();
                return;
            }
            if (e.CloseReason == CloseReason.WindowsShutDown) ctx.Shutdown();   // unmute before Windows ends
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            uiTimer.Stop();
            ctx.SettingsChanged -= OnSettingsChanged;
            ctx.LogAdded -= OnLogAdded;
            base.OnFormClosed(e);
        }

        // ------------------------------------------------------------------
        // small layout helpers
        // ------------------------------------------------------------------
        private static void SetText(Label l, string text)
        {
            if (l.Text != text) l.Text = text;
        }

        private static void SetNumeric(NumericUpDown n, decimal value)
        {
            if (value < n.Minimum) value = n.Minimum;
            if (value > n.Maximum) value = n.Maximum;
            value = Math.Round(value, n.DecimalPlaces);
            if (n.Value != value) n.Value = value;
        }

        private Label FixedLabel(int width)
        {
            Label l = new Label();
            l.AutoSize = false;
            l.Size = new Size(width, (int)(u * 1.3));
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.AutoEllipsis = true;
            return l;
        }

        private Label Caption(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(3, u / 3, 3, 2);
            return l;
        }

        private NumericUpDown Numeric(decimal min, decimal max, decimal step, int decimals)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Increment = step;
            n.DecimalPlaces = decimals;
            n.Width = u * 5;
            n.TextAlign = HorizontalAlignment.Right;
            return n;
        }

        private TableLayoutPanel Section(TableLayoutPanel root, string title)
        {
            Label h = new Label();
            h.Text = title;
            h.AutoSize = true;
            h.Font = new Font(Font, FontStyle.Bold);
            h.Margin = new Padding(3, u * 2 / 3, 3, 2);
            root.Controls.Add(h);
            TableLayoutPanel t = new TableLayoutPanel();
            t.ColumnCount = 3;
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.Margin = new Padding(u, 0, 3, 0);
            root.Controls.Add(t);
            return t;
        }

        private void AddRow(TableLayoutPanel t, string label, Control input, string hint)
        {
            Label l = new Label();
            l.Text = label;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            Label h = new Label();
            h.Text = hint;
            h.AutoSize = true;
            h.Anchor = AnchorStyles.Left;
            h.ForeColor = SystemColors.GrayText;
            int row = t.RowCount;
            t.RowCount = row + 1;
            t.Controls.Add(l, 0, row);
            t.Controls.Add(input, 1, row);
            t.Controls.Add(h, 2, row);
        }

        private void AddSpanRow(TableLayoutPanel t, Control c)
        {
            int row = t.RowCount;
            t.RowCount = row + 1;
            t.Controls.Add(c, 0, row);
            t.SetColumnSpan(c, 3);
        }
    }
}
