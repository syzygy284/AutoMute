using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AutoMute
{
    /// <summary>
    /// Horizontal dBFS meter with a draggable threshold marker.
    /// Click or drag anywhere on it to move the threshold.
    /// </summary>
    public sealed class LevelMeter : Control
    {
        private float minDb = -60f;
        private float maxDb = 0f;
        private float display = -100f;
        private float peakHold = -100f;
        private long peakHoldAt;
        private float threshold = -13f;
        private bool active = true;
        private bool dragging;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        public event EventHandler ThresholdDragged;

        public LevelMeter()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        public float MinDb
        {
            get { return minDb; }
            set { minDb = value; Invalidate(); }
        }

        public float Threshold
        {
            get { return threshold; }
            set
            {
                if (Math.Abs(value - threshold) < 0.001f) return;
                threshold = value;
                Invalidate();
            }
        }

        /// <summary>False draws the meter greyed out (trigger disabled).</summary>
        public bool Active
        {
            get { return active; }
            set
            {
                if (active == value) return;
                active = value;
                Invalidate();
            }
        }

        /// <summary>Feed the latest reading; dt = seconds since the last call.</summary>
        public void Push(float db, float dt)
        {
            // fast attack, smooth ~30 dB/s release (visual only)
            display = db >= display ? db : Math.Max(db, display - 30f * dt);
            long now = Clock.ElapsedMilliseconds;
            if (db >= peakHold || now - peakHoldAt > 1500)
            {
                peakHold = db;
                peakHoldAt = now;
            }
            Invalidate();
        }

        private float Inset
        {
            get { return Math.Max(4, Height / 6); }
        }

        private float XFor(float db)
        {
            float w = Width - 2 * Inset;
            float t = (db - minDb) / (maxDb - minDb);
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Inset + t * w;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = ClientRectangle;
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(32, 34, 38)))
                g.FillRectangle(bg, r);

            float inset = Inset;
            RectangleF track = new RectangleF(inset, inset, Width - 2 * inset, Height - 2 * inset);
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(52, 55, 61)))
                g.FillRectangle(tb, track);

            // 10 dB tick marks
            using (Pen tick = new Pen(Color.FromArgb(80, 84, 92)))
            {
                for (float db = (float)Math.Ceiling(minDb / 10f) * 10f; db <= maxDb; db += 10f)
                {
                    float x = XFor(db);
                    g.DrawLine(tick, x, track.Bottom - track.Height * 0.3f, x, track.Bottom);
                }
            }

            // level bar
            float barEnd = XFor(display);
            if (barEnd > track.Left + 0.5f)
            {
                Color c;
                if (!active) c = Color.FromArgb(110, 115, 125);
                else if (display >= threshold) c = Color.FromArgb(232, 72, 72);
                else if (display >= threshold - 6f) c = Color.FromArgb(240, 180, 60);
                else c = Color.FromArgb(76, 190, 110);
                using (SolidBrush bar = new SolidBrush(c))
                    g.FillRectangle(bar, track.Left, track.Top, barEnd - track.Left, track.Height);
            }

            // peak hold tick
            if (peakHold > minDb)
            {
                float px = XFor(peakHold);
                using (Pen pp = new Pen(Color.FromArgb(220, 225, 230), 2f))
                    g.DrawLine(pp, px, track.Top, px, track.Bottom);
            }

            // threshold marker
            float tx = XFor(threshold);
            Color mc = active ? Color.FromArgb(255, 214, 10) : Color.FromArgb(150, 150, 150);
            using (Pen mp = new Pen(mc, 2f))
                g.DrawLine(mp, tx, 1, tx, Height - 1);
            float s = inset;
            using (SolidBrush mb = new SolidBrush(mc))
            {
                g.FillPolygon(mb, new PointF[] { new PointF(tx - s, 0), new PointF(tx + s, 0), new PointF(tx, s) });
                g.FillPolygon(mb, new PointF[] { new PointF(tx - s, Height), new PointF(tx + s, Height), new PointF(tx, Height - s) });
            }

            // readout text
            string text = display <= -99f ? "silence" : display.ToString("0.0") + " dB";
            using (SolidBrush tb2 = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                sf.LineAlignment = StringAlignment.Center;
                RectangleF tr = new RectangleF(track.Left, track.Top, track.Width - 4, track.Height);
                g.DrawString(text, Font, tb2, tr, sf);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            dragging = true;
            SetFromX(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) SetFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
        }

        private void SetFromX(int x)
        {
            float w = Width - 2 * Inset;
            if (w <= 0) return;
            float db = minDb + (x - Inset) / w * (maxDb - minDb);
            db = (float)Math.Round(db * 2f) / 2f;   // 0.5 dB steps
            if (db < minDb) db = minDb;
            if (db > maxDb) db = maxDb;
            Threshold = db;
            if (ThresholdDragged != null) ThresholdDragged(this, EventArgs.Empty);
        }
    }
}
