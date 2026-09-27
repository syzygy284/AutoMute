using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoMute
{
    /// <summary>
    /// The application itself: tray icon + menu, the audio engine, settings
    /// persistence, and the (hidden-by-default) settings window.
    /// </summary>
    public sealed class TrayContext : ApplicationContext
    {
        private readonly AudioEngine engine;
        private Settings settings;
        private readonly NotifyIcon tray;
        private readonly ToolStripMenuItem miEnabled, miUnmute, miStartup;
        private readonly Timer tickTimer;
        private readonly Timer saveTimer;
        private readonly Icon iconNormal, iconMuted, iconPaused;
        private readonly List<string> log = new List<string>();
        private MainForm form;
        private int iconState = -1;
        private bool exiting;
        private bool shutDown;
        private bool trayHintShown;

        public event EventHandler SettingsChanged;
        public event Action<string> LogAdded;

        public TrayContext(bool startHidden)
        {
            settings = Settings.Load();
            StartupManager.RepairPathIfMoved();

            engine = new AudioEngine(settings.Clone());
            engine.Start();

            iconNormal = TrayIcons.Make(0);
            iconMuted = TrayIcons.Make(1);
            iconPaused = TrayIcons.Make(2);

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem miOpen = new ToolStripMenuItem("Open AutoMute");
            miOpen.Font = new Font(miOpen.Font, FontStyle.Bold);
            miOpen.Click += delegate { ShowForm(); };
            miEnabled = new ToolStripMenuItem("Monitoring enabled");
            miEnabled.Click += delegate { bool on = !settings.Enabled; Update(delegate(Settings s) { s.Enabled = on; }); };
            miUnmute = new ToolStripMenuItem("Unmute now");
            miUnmute.Click += delegate { engine.RequestUnmute(); };
            miStartup = new ToolStripMenuItem("Start with Windows");
            miStartup.Click += delegate { ToggleStartup(!StartupManager.IsEnabled()); };
            ToolStripMenuItem miExit = new ToolStripMenuItem("Exit");
            miExit.Click += delegate { Exit(); };
            menu.Items.AddRange(new ToolStripItem[] { miOpen, new ToolStripSeparator(), miEnabled, miUnmute, miStartup, new ToolStripSeparator(), miExit });
            menu.Opening += delegate
            {
                miEnabled.Checked = settings.Enabled;
                miStartup.Checked = StartupManager.IsEnabled();
            };

            tray = new NotifyIcon();
            tray.Icon = iconNormal;
            tray.Text = "AutoMute";
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowForm(); };
            tray.Visible = true;

            tickTimer = new Timer();
            tickTimer.Interval = 150;
            tickTimer.Tick += OnTick;
            tickTimer.Start();

            saveTimer = new Timer();
            saveTimer.Interval = 600;
            saveTimer.Tick += delegate { saveTimer.Stop(); settings.Save(); };

            Application.ApplicationExit += delegate { Shutdown(); };
            SystemEvents.SessionEnding += OnSessionEnding;

            if (!startHidden) ShowForm();
        }

        public AudioEngine Engine { get { return engine; } }
        public Settings Current { get { return settings; } }
        public bool IsExiting { get { return exiting; } }
        public IList<string> LogLines { get { return log; } }

        /// <summary>Apply a settings change live: engine picks it up on the next block.</summary>
        public void Update(Action<Settings> change)
        {
            Settings s = settings.Clone();
            change(s);
            settings = s;
            engine.Apply(s.Clone());
            saveTimer.Stop();
            saveTimer.Start();
            if (SettingsChanged != null) SettingsChanged(this, EventArgs.Empty);
        }

        public void ToggleStartup(bool enable)
        {
            try
            {
                StartupManager.SetEnabled(enable);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't change the startup setting:\n" + ex.Message, "AutoMute",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            if (SettingsChanged != null) SettingsChanged(this, EventArgs.Empty);
        }

        public void ShowForm()
        {
            if (form == null || form.IsDisposed) form = new MainForm(this);
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
        }

        public void HideForm()
        {
            if (form != null) form.Hide();
            if (!trayHintShown)
            {
                trayHintShown = true;
                tray.ShowBalloonTip(3000, "AutoMute is still running",
                    "It keeps monitoring from the tray. Right-click the icon to exit.", ToolTipIcon.Info);
            }
        }

        public void Exit()
        {
            exiting = true;
            Shutdown();
            if (form != null && !form.IsDisposed) form.Close();
            ExitThread();
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            Shutdown();   // Windows remembers mute across reboots - make sure we unmute
        }

        /// <summary>Stops the engine (unmuting if we muted) and removes the tray icon. Safe to call repeatedly.</summary>
        public void Shutdown()
        {
            if (shutDown) return;
            shutDown = true;
            SystemEvents.SessionEnding -= OnSessionEnding;
            tickTimer.Stop();
            if (saveTimer.Enabled)
            {
                saveTimer.Stop();
                settings.Save();
            }
            engine.Dispose();   // unmutes if we muted
            tray.Visible = false;
            tray.Dispose();
        }

        private void OnTick(object sender, EventArgs e)
        {
            string line;
            while (engine.Events.TryDequeue(out line))
            {
                log.Add(line);
                if (log.Count > 300) log.RemoveAt(0);
                if (LogAdded != null) LogAdded(line);
            }

            EngineSnapshot snap = engine.Snapshot(false);
            int state = snap.MutedByUs ? 1 : (settings.Enabled ? 0 : 2);
            if (state != iconState)
            {
                iconState = state;
                tray.Icon = state == 1 ? iconMuted : (state == 2 ? iconPaused : iconNormal);
            }

            string tip;
            if (snap.MutedByUs) tip = "AutoMute - muted (" + Math.Ceiling(snap.RemainingSeconds) + " s)";
            else if (!settings.Enabled) tip = "AutoMute - paused";
            else tip = "AutoMute - monitoring";
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            if (tray.Text != tip) tray.Text = tip;
            miUnmute.Enabled = snap.MutedByUs;
        }
    }

    /// <summary>Tray icons drawn at runtime: 0 = normal, 1 = muted, 2 = paused.</summary>
    public static class TrayIcons
    {
        public static Icon Make(int state)
        {
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            float u = size / 16f;
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                Color body = state == 2 ? Color.FromArgb(150, 150, 150) : Color.FromArgb(58, 150, 221);
                PointF[] speaker =
                {
                    new PointF(1f * u, 5.5f * u), new PointF(4.5f * u, 5.5f * u), new PointF(8.5f * u, 1.5f * u),
                    new PointF(8.5f * u, 14.5f * u), new PointF(4.5f * u, 10.5f * u), new PointF(1f * u, 10.5f * u)
                };
                using (SolidBrush b = new SolidBrush(body)) g.FillPolygon(b, speaker);

                if (state == 1)
                {
                    using (Pen p = new Pen(Color.FromArgb(235, 50, 50), 2.2f * u))
                    {
                        g.DrawLine(p, 10f * u, 5f * u, 15f * u, 11f * u);
                        g.DrawLine(p, 15f * u, 5f * u, 10f * u, 11f * u);
                    }
                }
                else if (state == 2)
                {
                    using (SolidBrush b = new SolidBrush(body))
                    {
                        g.FillRectangle(b, 10.5f * u, 5f * u, 1.6f * u, 6f * u);
                        g.FillRectangle(b, 13.3f * u, 5f * u, 1.6f * u, 6f * u);
                    }
                }
                else
                {
                    using (Pen p = new Pen(body, 1.5f * u))
                    {
                        g.DrawArc(p, 6.5f * u, 5f * u, 5f * u, 6f * u, -55f, 110f);
                        g.DrawArc(p, 6.5f * u, 2.5f * u, 8.5f * u, 11f * u, -55f, 110f);
                    }
                }
            }
            // The handle stays alive for the app's lifetime (only 3 icons are ever made).
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
