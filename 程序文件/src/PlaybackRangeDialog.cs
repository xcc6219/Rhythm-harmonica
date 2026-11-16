using System;
using System.Drawing;
using System.Windows.Forms;

namespace HarmonicaStudio
{
    internal sealed class PlaybackRangeDialog : Form
    {
        private readonly NumericUpDown start, end, total;
        private readonly CheckBox repeat;
        private readonly Label message;
        private readonly double speed;
        public double StartSeconds { get { return (double)start.Value; } }
        public double EndSeconds { get { return (double)end.Value; } }
        public double TargetSeconds { get { return (double)total.Value; } }
        public bool Repeat { get { return repeat.Checked; } }

        public PlaybackRangeDialog(double duration, double currentStart, double currentEnd, double currentTarget, bool currentRepeat, double playbackSpeed)
        {
            speed = playbackSpeed;
            Text = "片段与播放时长"; AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(620, 384); StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            BackColor = Theme.Panel; ForeColor = Theme.Text; Font = Theme.PixelFont(10);
            AddLabel("整首播放，或截取片段循环练习", 22, 18, 576, 30, 14, Theme.Text);
            AddLabel("起止位置按当前谱面秒数填写；总时长是实际播放时间。", 22, 60, 576, 30, 10, Theme.Muted);
            AddLabel("片段起点（秒）", 22, 108, 210, 30, 11, Theme.Text);
            start = Number(244, 108, duration, currentStart);
            AddLabel("片段终点（秒）", 22, 151, 210, 30, 11, Theme.Text);
            end = Number(244, 151, duration, currentEnd <= 0 ? duration : currentEnd);
            AddLabel("播放总时长（秒）", 22, 194, 210, 30, 11, Theme.Text);
            total = Number(244, 194, Math.Min(ScoreIO.MaximumDurationSeconds, ScoreIO.MaximumDurationSeconds / Math.Max(.5, speed)), currentTarget);
            AddLabel("0 = 播放片段一遍", 412, 195, 186, 30, 9, Theme.Muted);
            repeat = new CheckBox { Text = "循环片段，直到指定总时长结束", Checked = currentRepeat,
                Location = new Point(22, 240), Size = new Size(576, 28), Font = Theme.PixelFont(11) };
            Controls.Add(repeat);
            message = AddLabel("", 22, 277, 576, 39, 10, Theme.Muted);
            var reset = new SoftButton("恢复整首", false) { Location = new Point(22, 330), Width = 132 };
            reset.Click += delegate { start.Value = 0; end.Value = (decimal)duration; total.Value = 0; repeat.Checked = false; };
            var cancel = new SoftButton("取消", false) { Location = new Point(350, 330), Width = 110, DialogResult = DialogResult.Cancel };
            var save = new SoftButton("应用时长", true) { Location = new Point(472, 330), Width = 126 };
            save.Click += delegate
            {
                if (EndSeconds <= StartSeconds) { message.Text = "终点必须晚于起点。"; message.ForeColor = Theme.Warm; return; }
                if (TargetSeconds > (EndSeconds - StartSeconds) / speed + .000001 && !Repeat)
                { message.Text = "总时长超过片段一遍，请勾选循环，或缩短总时长。"; message.ForeColor = Theme.Warm; return; }
                DialogResult = DialogResult.OK;
            };
            Controls.AddRange(new Control[] { reset, cancel, save }); CancelButton = cancel; AcceptButton = save;
            start.ValueChanged += delegate { Describe(); }; end.ValueChanged += delegate { Describe(); };
            total.ValueChanged += delegate { Describe(); }; repeat.CheckedChanged += delegate { Describe(); };
            Describe();
        }
        private void Describe()
        {
            message.ForeColor = Theme.Muted;
            double seconds = TargetSeconds > 0 ? TargetSeconds : Math.Max(0, EndSeconds - StartSeconds) / speed;
            message.Text = "片段一遍 " + Math.Max(0, EndSeconds - StartSeconds).ToString("0.##") + " 谱面秒 · 实际播放 " + seconds.ToString("0.##") + " 秒。应用后停止当前播放。";
        }
        private NumericUpDown Number(int x, int y, double maximum, double value)
        {
            var field = new NumericUpDown { Location = new Point(x, y), Width = 150, Minimum = 0,
                Maximum = (decimal)Math.Max(0, maximum), DecimalPlaces = 2, Increment = 1,
                Font = Theme.PixelFont(11), BackColor = Theme.Input, ForeColor = Theme.Text };
            field.Value = Math.Min(field.Maximum, Math.Max(0, (decimal)value)); Controls.Add(field); return field;
        }
        private Label AddLabel(string text, int x, int y, int w, int h, float size, Color color)
        {
            var label = new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h),
                Font = Theme.PixelFont(size), ForeColor = color, TextAlign = ContentAlignment.MiddleLeft };
            Controls.Add(label); return label;
        }
    }

    internal sealed class WindowResizeGrip : Control
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        public WindowResizeGrip()
        {
            Size = new Size(22, 22); Cursor = Cursors.SizeNWSE; BackColor = Theme.Background;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            AccessibleName = "拖动调整窗口大小";
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Muted, 1.6f))
                for (int step = 5; step <= 15; step += 5) e.Graphics.DrawLine(pen, Width - step, Height - 3, Width - 3, Height - step);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Form form = FindForm(); if (form == null) return;
            ReleaseCapture(); SendMessage(form.Handle, 0x00a1, (IntPtr)17, IntPtr.Zero);
        }
    }
}
