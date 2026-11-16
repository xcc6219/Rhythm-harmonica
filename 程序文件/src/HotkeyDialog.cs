using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace HarmonicaStudio
{
    public sealed class HotkeyDialog : Form
    {
        private readonly AppSettings settings;
        private readonly ComboBox[] hotkeys = new ComboBox[4];
        private readonly NumericUpDown countdown;
        private readonly Label error;

        public HotkeyDialog(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            this.settings = settings;
            // Normalize a private copy. Opening or cancelling the dialog must
            // not repair or otherwise mutate the live application settings.
            AppSettings current = new AppSettings
            {
                CountdownSeconds = settings.CountdownSeconds,
                StartHotkey = settings.StartHotkey,
                StopHotkey = settings.StopHotkey,
                ToggleWindowHotkey = settings.ToggleWindowHotkey,
                CompactHotkey = settings.CompactHotkey
            };
            current.Normalize();
            Text = "热键与倒计时";
            ClientSize = new Size(456, 445);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Font = Theme.PixelFont(10);
            AutoScaleMode = AutoScaleMode.None;

            AddLabel("按自己的习惯控制演奏", 24, 17, 410, 28, Theme.Text, 14, FontStyle.Bold);
            AddLabel("播放键重复按：开始 → 暂停 → 继续。", 24, 50, 408, 25, Theme.Muted, 9);
            AddLabel("选择 F5–F12，四个功能的按键不能重复。", 24, 76, 408, 23, Theme.Muted, 9);
            string[] captions = { "开始 / 暂停 / 继续", "停止并回到开头", "显示 / 隐藏窗口", "切换迷你模式" };
            string[] selected = { current.StartHotkey, current.StopHotkey,
                current.ToggleWindowHotkey, current.CompactHotkey };
            for (int i = 0; i < hotkeys.Length; i++)
            {
                int y = 103 + i * 39;
                AddLabel(captions[i], 26, y, 244, 30, Theme.Text, 10);
                ComboBox combo = new ComboBox
                {
                    Name = "hotkey" + i,
                    Location = new Point(286, y + 2),
                    Width = 142,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Theme.Input,
                    ForeColor = Theme.Text,
                    Font = Theme.PixelFont(10)
                };
                for (int number = 5; number <= 12; number++) combo.Items.Add("F" + number);
                combo.SelectedItem = selected[i];
                combo.SelectedIndexChanged += delegate { error.Text = ""; };
                hotkeys[i] = combo;
                Controls.Add(combo);
            }

            AddLabel("自动演奏前倒计时", 26, 266, 244, 30, Theme.Text, 10);
            countdown = new NumericUpDown
            {
                Location = new Point(286, 269),
                Width = 104,
                Minimum = 3,
                Maximum = 10,
                Value = current.CountdownSeconds,
                DecimalPlaces = 0,
                BackColor = Theme.Input,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(countdown);
            AddLabel("秒", 398, 267, 30, 30, Theme.Muted, 9);
            AddLabel("倒计时也可暂停 / 继续；停止键可随时取消。", 26, 303, 404, 25, Theme.Muted, 9);
            AddLabel("隐藏后仍在运行；点击通知区域图标可恢复。", 26, 331, 404, 25, Theme.Muted, 9);
            error = AddLabel("", 26, 365, 402, 25, Theme.Warm, 9);

            SoftButton cancel = new SoftButton("取消", false)
            {
                Location = new Point(229, 401), Width = 91, DialogResult = DialogResult.Cancel
            };
            SoftButton save = new SoftButton("保存", true) { Location = new Point(332, 401), Width = 96 };
            save.Click += delegate { SaveSettings(); };
            Controls.Add(cancel);
            Controls.Add(save);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private Label AddLabel(string text, int x, int y, int width, int height,
            Color color, float size, FontStyle style = FontStyle.Regular)
        {
            Label label = new Label
            {
                Text = text, Location = new Point(x, y), Size = new Size(width, height),
                ForeColor = color, BackColor = Color.Transparent, Font = Theme.PixelFont(size, style),
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(label);
            return label;
        }

        private void SaveSettings()
        {
            string[] values = new string[hotkeys.Length];
            HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < hotkeys.Length; i++)
            {
                values[i] = hotkeys[i].SelectedItem as string;
                if (values[i] == null || !used.Add(values[i]))
                {
                    error.Text = "热键不能重复，请为每个功能选择不同按键。";
                    hotkeys[i].Focus();
                    return;
                }
            }
            settings.StartHotkey = values[0];
            settings.StopHotkey = values[1];
            settings.ToggleWindowHotkey = values[2];
            settings.CompactHotkey = values[3];
            settings.CountdownSeconds = (int)countdown.Value;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
