using System;
using System.Drawing;
using System.Windows.Forms;

namespace HarmonicaStudio
{
    internal sealed class RuntimeDiagnosticsDialog : Form
    {
        internal RuntimeDiagnosticsDialog(string details, Func<string> restart)
        {
            Text = "热键与运行状态"; AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(690, 570); MinimumSize = new Size(560, 450);
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            BackColor = Theme.Background; ForeColor = Theme.Text; Font = Theme.PixelFont(10);
            var title = new Label { Text = "游戏内控制与自动吹奏状态", Location = new Point(18, 14), Size = new Size(650, 30),
                Font = Theme.PixelFont(14), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            var text = new TextBox { Text = details, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
                Location = new Point(18, 53), Size = new Size(654, 425), BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.PixelFont(10),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            var status = new Label { Location = new Point(18, 485), Size = new Size(654, 32), ForeColor = Theme.Warm,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, UseMnemonic = false };
            var elevate = new SoftButton("管理员方式重启", true) { Location = new Point(18, 522), Width = 170,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Enabled = restart != null && WindowsInputAccess.OwnDescription() != "管理员权限" };
            var copy = new SoftButton("复制状态", false) { Location = new Point(198, 522), Width = 120, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var close = new SoftButton("关闭", false) { Location = new Point(562, 522), Width = 110,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.Cancel };
            copy.Click += delegate
            {
                try { Clipboard.SetText(text.Text); status.Text = "状态已复制，可直接粘贴反馈。"; }
                catch (System.Runtime.InteropServices.ExternalException) { status.Text = "剪贴板正被占用，请稍后重试。"; }
            };
            elevate.Click += delegate
            {
                elevate.Enabled = false;
                string failure = restart();
                if (failure == null) { if (!IsDisposed) Close(); return; }
                if (!IsDisposed) { status.Text = failure; elevate.Enabled = true; }
            };
            Controls.AddRange(new Control[] { title, text, status, elevate, copy, close }); CancelButton = close;
        }
    }
}
