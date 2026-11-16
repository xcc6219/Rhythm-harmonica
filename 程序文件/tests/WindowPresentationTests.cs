using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HarmonicaStudio;

public static class WindowPresentationTests
{
    private static int checks;
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowStyle(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool GetUpdateRect(IntPtr window, IntPtr rectangle, bool erase);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);

    private sealed class PaintProbe : NativeWindow, IDisposable
    {
        public int Paints, CompositedPaints;
        private readonly MainForm form;
        public PaintProbe(Control control, MainForm owner)
        {
            form = owner; AssignHandle(control.Handle);
        }
        public void Reset() { Paints = CompositedPaints = 0; }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x000F)
            {
                Paints++;
                if ((GetWindowStyle(form.Handle, -20) & 0x02000000) != 0) CompositedPaints++;
            }
            base.WndProc(ref message);
        }
        public void Dispose() { ReleaseHandle(); }
    }

    public static int Run()
    {
        checks = 0;
        string data = Path.Combine(AppPaths.ContentDirectory, "data");
        string settingsPath = Path.Combine(data, "settings.json");
        bool hadDirectory = Directory.Exists(data), hadSettings = File.Exists(settingsPath);
        byte[] savedSettings = hadSettings ? File.ReadAllBytes(settingsPath) : null;
        var probes = new List<PaintProbe>();
        MainForm form = null;
        try
        {
            Directory.CreateDirectory(data);
            File.WriteAllText(settingsPath, "{\"AutoMode\":false,\"Compact\":false,\"AlwaysOnTop\":false,\"Opacity\":1}");
            form = new MainForm(false);
            Field<System.Windows.Forms.Timer>(form, "timer").Stop();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000); form.ShowInTaskbar = false;
            form.Opacity = 0; form.Show(); Application.DoEvents();
            for (int i = 1; i <= 5; i++) UnregisterHotKey(form.Handle, i);
            // The window remains outside every monitor. Probe real WM_PAINT messages;
            // DrawToBitmap/PrintWindow would repaint and could conceal a late first frame.
            form.Location = new Point(-30000, -30000);
            var canvas = Field<ScoreCanvas>(form, "canvas");
            var compactCanvas = Field<ScoreCanvas>(form, "compactCanvas");
            var header = Field<Panel>(form, "header");
            var footer = Field<Panel>(form, "footer");
            var sidebar = Field<Panel>(form, "sidebar");
            CheckDefaultTextFits(form);
            Control[] watched = { canvas, compactCanvas, header, footer, sidebar };
            foreach (Control control in watched) probes.Add(new PaintProbe(control, form));
            Assert((GetWindowStyle(form.Handle, -20) & 0x02000000) == 0,
                "持续播放不能保留整个窗口的子窗口合成样式。");

            foreach (double opacity in new[] { 1.0, 0.9, 0.65 })
            {
                form.Opacity = opacity;
                foreach (bool compact in new[] { false, true })
                {
                    form.Hide();
                    Call(form, "SetCompact", compact);
                    form.Location = new Point(-30000, -30000);
                    IntPtr originalHandle = form.Handle;
                    form.Hide();
                    foreach (PaintProbe probe in probes) probe.Reset();
                    var watch = Stopwatch.StartNew();
                    form.Show(); watch.Stop();
                    int active = compact ? 1 : 0;
                    Assert(form.Handle == originalHandle, "隐藏恢复不能重建句柄或丢失热键绑定。");
                    Assert(probes[active].Paints > 0 && probes[active].CompositedPaints > 0,
                        "恢复返回前，可见瀑布须已经在合成阶段完成真实原生首帧。");
                    Assert(probes[2].CompositedPaints > 0 && probes[3].CompositedPaints > 0,
                        "恢复时标题栏和底部状态栏须跟瀑布一起完成首帧。");
                    if (!compact) Assert(probes[4].CompositedPaints > 0, "完整窗口恢复时曲库须同步绘制。");
                    Assert(!GetUpdateRect(watched[active].Handle, IntPtr.Zero, false) &&
                        !GetUpdateRect(header.Handle, IntPtr.Zero, false) && !GetUpdateRect(footer.Handle, IntPtr.Zero, false),
                        "恢复返回时，重要子窗口不得遗留等待异步绘制的脏区域。");
                    Assert((GetWindowStyle(form.Handle, -20) & 0x02000000) == 0,
                        "首帧提交后须立即撤销整窗合成，避免动画每帧重新绘制所有控件。");
                    Assert(Math.Abs(form.Opacity - opacity) < 0.001,
                        "首帧恢复必须兼容65%、90%和100%不透明度。");
                    Assert(watch.Elapsed.TotalSeconds < 1,
                        "恢复须直接完成，不能依赖高延迟定时器或延后出现的遮罩。");
                    Console.WriteLine("隐藏恢复原生首帧：" + (compact ? "迷你" : "完整") + " / " +
                        (int)(opacity * 100) + "% / " + watch.ElapsedMilliseconds + " ms。");

                    foreach (PaintProbe probe in probes) probe.Reset();
                    Call(form, "UpdatePlaybackUI");
                    watched[active].Update();
                    Assert(probes[active].Paints > 0 && probes[1 - active].Paints == 0,
                        "普通动画只刷新当前可见瀑布，不绘制另一个隐藏的谱面。");
                    Assert(probes[active].CompositedPaints == 0,
                        "普通动画不得触发整窗合成绘制。");
                }
            }

            form.Hide(); Call(form, "SetCompact", false); form.Location = new Point(-30000, -30000); form.Show();
            form.WindowState = FormWindowState.Minimized;
            foreach (PaintProbe probe in probes) probe.Reset();
            form.WindowState = FormWindowState.Normal;
            Assert(probes[0].CompositedPaints > 0 && !GetUpdateRect(canvas.Handle, IntPtr.Zero, false),
                "从最小化恢复也须同步完成瀑布首帧。");
            Assert((GetWindowStyle(form.Handle, -20) & 0x02000000) == 0,
                "从最小化恢复后须立即回到普通动画绘制。");
            form.Hide(); foreach (PaintProbe probe in probes) probe.Reset();
            Call(form, "UpdatePlaybackUI");
            Assert(probes.TrueForAll(p => p.Paints == 0), "窗口隐藏时只更新播放状态，不强制绘制控件。");

            Call(form, "StartPlayback");
            Assert(Field<bool>(form, "playing") && !Field<bool>(form, "autoActive"),
                "恢复验证必须能够覆盖真实手动播放，不调用自动输入后端。");
            double position = (double)typeof(MainForm).GetProperty("Position", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form, null);
            System.Threading.Thread.Sleep(40);
            foreach (PaintProbe probe in probes) probe.Reset();
            Call(form, "ToggleWindowVisibility");
            Assert(form.Visible && probes[0].CompositedPaints > 0, "显隐热键的共用入口须提交完整原生首帧。");
            Assert(canvas.Position > position && Field<bool>(form, "playing"),
                "播放中恢复应显示当前进度，不能重置时钟或短暂出现旧帧。");
            Call(form, "ToggleWindowVisibility");
            Assert(form.Visible && form.WindowState == FormWindowState.Minimized,
                "显隐入口再次调用应最小化窗口，保留任务栏入口所需的Visible状态。");
            foreach (PaintProbe probe in probes) probe.Reset();
            SendMessage(form.Handle, 0x0112, new IntPtr(0xf120), IntPtr.Zero);
            Assert(form.WindowState == FormWindowState.Normal && probes[0].CompositedPaints > 0 &&
                probes[2].CompositedPaints > 0 && probes[3].CompositedPaints > 0 && !GetUpdateRect(canvas.Handle, IntPtr.Zero, false),
                "任务栏SC_RESTORE在返回前同步提交瀑布、标题栏及底栏首帧，无分块黑区。");
            Assert((GetWindowStyle(form.Handle, -20) & 0x02000000) == 0 && Field<bool>(form, "playing"),
                "任务栏恢复不重启当前播放时钟，提交首帧后撤销临时整窗合成。");
            Call(form, "StopPlayback");

            int configuredCountdown = Field<AppSettings>(form, "settings").CountdownSeconds;
            foreach (bool compact in new[] { false, true })
            {
                Call(form, "SetCompact", compact);
                Call(form, "SetStatus", "倒计时 3 秒，请切换到三角洲窗口。", true);
                Assert(canvas.CountdownText.Contains("3 秒") && compactCanvas.CountdownText == canvas.CountdownText,
                    "完整与迷你谱面须同时收到顶部倒计时文本。");
                Label status = Field<Label>(form, "statusLabel");
                Assert(!status.Text.Contains("3 秒") && status.Text.Contains("准备自动吹奏"),
                    "倒计时数字不能留在窗口底部状态栏。");
                Call(form, "SetStatus", "倒计时已暂停。", false);
                Assert(canvas.CountdownText == "倒计时已暂停。" && !canvas.CountdownText.Contains("3"),
                    "倒计时暂停时顶部须显示暂停状态，不能遗留旧数字。");
                Call(form, "SetStatus", "倒计时继续。", false);
                Assert(canvas.CountdownText == compactCanvas.CountdownText && canvas.CountdownText.Contains("继续"),
                    "倒计时继续状态须同步两个谱面。");
                Call(form, "SetStatus", "倒计时 2 秒，请切换到三角洲窗口。", true);
                Call(form, "StopPlayback");
                Assert(String.IsNullOrEmpty(canvas.CountdownText) && String.IsNullOrEmpty(compactCanvas.CountdownText) &&
                    !status.Text.Contains("准备自动吹奏"),
                    "取消倒计时须清除顶部数字与底部准备状态。");
                Call(form, "SetStatus", "倒计时 1 秒，请切换到三角洲窗口。", true);
                Call(form, "SetStatus", "正在自动演奏。", false);
                Assert(String.IsNullOrEmpty(canvas.CountdownText) && String.IsNullOrEmpty(compactCanvas.CountdownText),
                    "进入演奏后须恢复正常顶部指法，不得保留倒计时数字。");
                Call(form, "SetStatus", "倒计时 1 秒，请切换到三角洲窗口。", true);
                Call(form, "SetStatus", "本曲播放结束", false);
                Assert(String.IsNullOrEmpty(canvas.CountdownText), "结束状态须清除顶部倒计时。");
            }
            Assert(Field<AppSettings>(form, "settings").CountdownSeconds == configuredCountdown,
                "挪动倒计时显示位置不能改变用户配置的倒计时时长。");

            Console.WriteLine("窗口显隐首帧测试通过，共 " + checks +
                " 项断言（屏幕外原生窗口，未发送键鼠输入）。");
            return checks;
        }
        finally
        {
            foreach (PaintProbe probe in probes) probe.Dispose();
            if (form != null)
            {
                for (int i = 1; i <= 5; i++) if (form.IsHandleCreated) UnregisterHotKey(form.Handle, i);
                Field<System.Windows.Forms.Timer>(form, "timer").Dispose();
                Field<AudioPreview>(form, "audio").Dispose(); Field<AutoPlayer>(form, "autoPlayer").Dispose();
                Field<ToolTip>(form, "sourceTip").Dispose(); form.Dispose();
            }
            if (hadSettings) File.WriteAllBytes(settingsPath, savedSettings);
            else if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (!hadDirectory && Directory.Exists(data) && Directory.GetFileSystemEntries(data).Length == 0)
                Directory.Delete(data);
        }
    }

    private static T Field<T>(object target, string name)
    { return (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target); }
    private static void Call(object target, string name, params object[] arguments)
    { target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, arguments); }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new Exception(message); checks++; }

    private static void CheckDefaultTextFits(MainForm form)
    {
        Assert(form.AutoScaleMode == AutoScaleMode.None, "主窗口必须保持固定像素布局，不进行二次DPI缩放。");
        foreach (int dpi in new[] { 96, 144, 192 })
        {
            using (var bitmap = new Bitmap(1600, 100))
            {
                bitmap.SetResolution(dpi, dpi);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    var problems = new List<string>();
                    CheckTextTree(form, graphics, problems);
                    Assert(problems.Count == 0, "默认控件的完整文字检查失败（" + dpi + " DPI）：\n" + String.Join("\n", problems));
                    using (Font listTitle = Theme.PixelFont(10, FontStyle.Bold))
                    using (Font listDetail = Theme.PixelFont(8))
                    {
                        Assert(listTitle.GetHeight(graphics) <= 23 && listDetail.GetHeight(graphics) <= 19,
                            "曲库双行标题和副标题不能超出固定行高：" + dpi + " DPI。");
                    }
                    var time = Field<Label>(form, "timeLabel");
                    Size duration = TextRenderer.MeasureText(graphics, "999:59 / 999:59", time.Font, new Size(Int32.MaxValue, Int32.MaxValue),
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                    Assert(duration.Width <= time.ClientSize.Width && duration.Height <= time.ClientSize.Height,
                        "分钟数达到三位的当前/总时长也须显示完整：" + dpi + " DPI。");
                }
            }
        }
        Assert(Field<Label>(form, "titleLabel").AutoEllipsis && Field<Label>(form, "compactTitle").AutoEllipsis,
            "自定义超长曲名须使用省略号，避免挤压旁边的按钮。");
    }

    private static void CheckTextTree(Control parent, Graphics graphics, List<string> problems)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is ScoreCanvas) continue;
            if (control is Label || control is Button || control is CheckBox || control is TextBox ||
                control is ComboBox || control is NumericUpDown || control is ListBox)
            {
                Assert(control.Font.Unit == GraphicsUnit.Pixel, "主窗文字须与固定像素布局保持相同单位：" + control.Text);
                if (control is Label || control is Button || control is CheckBox)
                {
                    Size text = TextRenderer.MeasureText(graphics, control.Text, control.Font, new Size(Int32.MaxValue, Int32.MaxValue),
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                    int available = control.ClientSize.Width - (control is CheckBox ? 24 : control is Button ? 6 : 0);
                    var label = control as Label;
                    if (text.Height > control.ClientSize.Height)
                        problems.Add("垂直裁切：" + control.Text + " " + text + " / " + control.ClientSize);
                    if (label == null || !label.AutoEllipsis)
                        if (text.Width > available) problems.Add("横向裁切：" + control.Text + " " + text + " / " + available);
                }
            }
            CheckTextTree(control, graphics, problems);
        }
    }
}
