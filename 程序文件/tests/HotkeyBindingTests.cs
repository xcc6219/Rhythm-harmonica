using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using HarmonicaStudio;

public static class HotkeyBindingTests
{
    private static int checks;
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowStyle(IntPtr window, int index);
    public static int Run()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            int value = 0; Exception error = null;
            var thread = new Thread(delegate() { try { value = Run(); } catch (Exception ex) { error = ex; } });
            thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!thread.Join(30000)) throw new Exception("统一播放热键验证超时。");
            if (error != null) throw error;
            return value;
        }
        checks = 0;
        string[] preferred = { "F5", "F7", "F8", "F9" };
        var registrar = new Registrar();
        HotkeyBindingResult result = HotkeyBindings.Register(preferred, registrar.Register);
        Assert(AllRegistered(result) && registrar.Calls.Count == 4, "无冲突只注册四个用户选择。");
        Assert(result.Changes.Count == 0 && result.Names[1] == "F7", "成功热键保留且无虚假替换提示。");
        Assert(registrar.Calls[0] == "1:F5" && registrar.Calls[3] == "4:F9", "Win32注册ID从1到4。");
        Assert(!registrar.Bound.ContainsValue("F6"), "默认F6没有单独的暂停绑定，不产生额外冲突。");

        registrar = new Registrar(); registrar.Occupied.Add("F7");
        result = HotkeyBindings.Register(preferred, registrar.Register);
        Assert(AllRegistered(result) && result.Names[1] == "F6", "F7占用时停止自动改为真正空闲的F6。");
        Assert(result.Names[0] == "F5" && result.Names[2] == "F8" && result.Names[3] == "F9",
            "替代不会夺取其余成功slot。");
        Assert(result.Changes.Count == 1 && result.Changes[0] == "F7 → F6" && result.Errors[1] == 0, "替换结果和错误状态准确。");
        Assert(preferred[1] == "F7", "登记结果不修改原始设置数组。");

        // A canceled settings dialog releases then restores existing choices;
        // registration must not retain a stale global attempted-key set.
        registrar.ReleaseBindings();
        HotkeyBindingResult restored = HotkeyBindings.Register(result.Names, registrar.Register);
        Assert(AllRegistered(restored) && restored.Names[1] == "F6" && restored.Changes.Count == 0,
            "取消设置后可用当前绑定重新登记。");
        Assert(result.Names[1] == "F6" && preferred[1] == "F7", "重新绑定不改上次结果或用户数组。");

        registrar = new Registrar();
        registrar.Occupied.Add("F5"); registrar.Occupied.Add("F7"); registrar.Occupied.Add("F8"); registrar.Occupied.Add("F9");
        result = HotkeyBindings.Register(preferred, registrar.Register);
        Assert(result.Names[1] == "F6" && result.Names[0] == "F10" && result.Names[2] == "F11" && result.Names[3] == "F12",
            "多个冲突优先停止、统一播放、显隐、迷你。");
        Assert(registrar.Calls[4] == "2:F6" && registrar.Calls[5] == "1:F10" && registrar.Calls[6] == "3:F11" && registrar.Calls[7] == "4:F12",
            "所有偏好尝试完成后按保护顺序分配空闲键。");
        Assert(AllRegistered(result) && result.Changes.Count == 4, "四项失败可恰好占用四个备用键。");

        registrar = new Registrar();
        for (int number = 5; number <= 12; number++) registrar.Occupied.Add("F" + number);
        result = HotkeyBindings.Register(preferred, registrar.Register);
        Assert(registrar.Calls.Count == 8 && UniqueKeys(registrar.Calls) == 8, "所有八键被占用时只尝试八次。");
        Assert(!result.Registered[1] && result.Errors[1] == 1409 && result.Names[1] == "F7" && result.Changes.Count == 0,
            "没有空闲键保留失败和原名，不声称替换成功。");
        Assert(!result.Registered[0] && !result.Registered[2] && !result.Registered[3], "全占用各slot均不可全局注册。");

        registrar = new Registrar(); registrar.Errors["F7"] = 5;
        result = HotkeyBindings.Register(preferred, registrar.Register);
        Assert(registrar.Calls.Count == 4 && !result.Registered[1] && result.Errors[1] == 5,
            "权限类错误保留原始错误，不归因占用。");
        Assert(result.Names[1] == "F7" && result.Changes.Count == 0 && result.Registered[0], "非1409不自动换键且其余成功保留。");

        registrar = new Registrar(); registrar.Occupied.Add("F7"); registrar.Occupied.Add("F5"); registrar.Errors["F6"] = 87;
        result = HotkeyBindings.Register(preferred, registrar.Register);
        Assert(!result.Registered[1] && result.Errors[1] == 87 && result.Names[1] == "F7", "备用注册出现非占用错误时停止该slot替换。");
        Assert(result.Names[0] == "F10" && result.Registered[0] && registrar.Calls.Count == 6,
            "一个slot的参数异常不会夺取后续slot剩余备用键。");

        registrar = new Registrar(); registrar.Occupied.Add("F8"); registrar.Occupied.Add("F9");
        result = HotkeyBindings.Register(preferred, registrar.Register);
        Assert(result.Names[2] == "F6" && result.Names[3] == "F10" && result.Names[1] == "F7", "显示与迷你冲突也自动替换，已成功停止不变。");
        registrar = new Registrar(); registrar.Occupied.Add("F7");
        string[] custom = { "F12", "F7", "F9", "F8" };
        result = HotkeyBindings.Register(custom, registrar.Register);
        Assert(result.Names[1] == "F5" && result.Names[0] == "F12" && result.Names[2] == "F9", "保留自定义偏好后从低到高选择真正空闲键。");

        int attempts = 0;
        Throws<ArgumentException>(delegate { HotkeyBindings.Register(new string[] { "F5", "F5", "F8", "F9" }, delegate { attempts++; return 0; }); }, "重复未规范化输入拒绝。");
        Throws<ArgumentException>(delegate { HotkeyBindings.Register(new string[] { "F5", "F7", "F8", "F99" }, delegate { attempts++; return 0; }); }, "越界按键拒绝。");
        Throws<ArgumentException>(delegate { HotkeyBindings.Register(new string[] { "F5" }, delegate { attempts++; return 0; }); }, "必须提供四个绑定。");
        Assert(attempts == 0, "先验证全部输入，非法配置不留下部分注册。");
        DialogControls();
        PlaybackControls();
        LifecycleAndModalWindows();
        ClosingOwnedWorkflows();
        Console.WriteLine("热键与统一播放控制测试通过，共 " + checks + " 项断言（模拟注册 / 输入，隔离原生窗口）。");
        return checks;
    }

    private static void DialogControls()
    {
        var settings = new AppSettings { StartHotkey = "F11", PauseHotkey = "F11", StopHotkey = "F7" };
        using (var dialog = new HotkeyDialog(settings))
        {
            var combos = Field<ComboBox[]>(dialog, "hotkeys");
            Assert(combos.Length == 4 && combos[0].SelectedItem as string == "F11", "设置只展示四个功能并保留旧播放键。");
            Assert(dialog.Controls.Cast<Control>().Any(c => c.Text == "开始 / 暂停 / 继续"), "设置明确说明单键的三个状态。");
            Assert(settings.StartHotkey == "F11" && settings.PauseHotkey == "F11", "仅打开设置不会改变用户配置。");
            foreach (Control control in dialog.Controls)
                Assert(control.Left >= 0 && control.Top >= 0 && control.Right <= dialog.ClientSize.Width && control.Bottom <= dialog.ClientSize.Height,
                    "热键设置所有控件均在窗口内：" + control.Text);
            for (int i = 0; i < dialog.Controls.Count; i++) for (int j = i + 1; j < dialog.Controls.Count; j++)
                Assert(!dialog.Controls[i].Bounds.IntersectsWith(dialog.Controls[j].Bounds), "四行热键设置无控件遮挡：" +
                    dialog.Controls[i].Text + " " + dialog.Controls[i].Bounds + " / " + dialog.Controls[j].Text + " " + dialog.Controls[j].Bounds);
            combos[1].SelectedItem = "F11";
            Call(dialog, "SaveSettings");
            Assert(dialog.DialogResult != DialogResult.OK && Field<Label>(dialog, "error").Text.Contains("不能重复"), "四个有效功能重复时阻止保存。");
            Assert(settings.StartHotkey == "F11" && settings.StopHotkey == "F7", "无效保存不部分改变设置。");
            combos[1].SelectedItem = "F7";
            combos[2].SelectedItem = "F10";
            Call(dialog, "SaveSettings");
            Assert(dialog.DialogResult == DialogResult.OK && settings.ToggleWindowHotkey == "F10" && settings.PauseHotkey == "F11",
                "有效四键保存不改变兼容暂停字段。");
        }
        var invalid = new AppSettings { StartHotkey = "bad", StopHotkey = "F7" };
        using (var cancelled = new HotkeyDialog(invalid))
            Assert(invalid.StartHotkey == "bad", "取消设置不触发配置修复或覆盖。");
    }

    private static void PlaybackControls()
    {
        string directory = Path.Combine(AppPaths.ContentDirectory, "data");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        bool existed = File.Exists(path);
        byte[] original = existed ? File.ReadAllBytes(path) : null;
        MainForm form = null;
        var clock = new ControlledClock();
        var input = new EmptyInput();
        try
        {
            File.WriteAllText(path, "{\"AutoMode\":false,\"AlwaysOnTop\":false,\"StartHotkey\":\"F5\",\"PauseHotkey\":\"F6\",\"CountdownSeconds\":3}");
            form = new MainForm(false);
            form.ControlKeyReader = delegate { return false; }; form.ControlModifiersReader = delegate { return false; };
            form.HotkeyRegistrar = delegate { return 0; }; form.HotkeyReleaser = delegate { };
            Call(form, "RegisterControlHotkeys");
            Field<System.Windows.Forms.Timer>(form, "timer").Stop();
            var settings = Field<AppSettings>(form, "settings");
            Assert(Field<Label>(form, "hotkeyLabel").Text.Contains("F5 开始 / 暂停 / 继续") &&
                !Field<Label>(form, "hotkeyLabel").Text.Contains("F6"), "主窗底栏只说明实际四键。");
            var notes = new List<NoteEvent> { new NoteEvent { Pitch = 60, Start = 0, Duration = 10, Velocity = 90 } };
            Set(form, "notes", notes); Set(form, "noteDuration", 10.0);
            Hotkey(form, 1);
            Assert(Field<bool>(form, "playing") && !Field<bool>(form, "autoActive"), "统一播放热键第一次开始手动跟谱。");
            Thread.Sleep(15); Hotkey(form, 1);
            double paused = Field<double>(form, "offset");
            Assert(!Field<bool>(form, "playing") && paused > 0 && Field<Label>(form, "statusLabel").Text.Contains("F5 继续"), "同一键暂停手动跟谱并提示继续键。");
            Assert(Field<SoftButton>(form, "playButton").Text.Contains("继续") && Field<SoftButton>(form, "compactPlayButton").Text.Contains("继续"),
                "完整与迷你窗口均显示继续状态。");
            Call(Field<SoftButton>(form, "compactPlayButton"), "OnClick", EventArgs.Empty);
            Assert(Field<bool>(form, "playing") && Field<double>(form, "offset") == paused, "迷你按钮从相同时间继续，不重新开始。");
            Call(Field<SoftButton>(form, "playButton"), "OnClick", EventArgs.Empty);
            Assert(!Field<bool>(form, "playing"), "完整按钮与热键共用暂停状态。");
            Hotkey(form, 1); Hotkey(form, 2);
            Assert(!Field<bool>(form, "playing") && Field<double>(form, "offset") == 0 && Field<SoftButton>(form, "compactPlayButton").Text.Contains("开始"),
                "单独停止键清零，窗口按钮恢复开始。");
            bool compact = Field<bool>(form, "compact");
            Hotkey(form, 4); Assert(Field<bool>(form, "compact") != compact, "第四热键仅切换迷你。");
            Hotkey(form, 4); Assert(Field<bool>(form, "compact") == compact, "同一迷你键再次按恢复。");
            // Exercise a running preview's pause branch without starting audio playback.
            Set(form, "listening", true); Set(form, "playing", true);
            Field<Stopwatch>(form, "playbackClock").Restart();
            Hotkey(form, 1);
            Assert(!Field<bool>(form, "playing") && Field<bool>(form, "listening") && !Field<AutoPlayer>(form, "autoPlayer").Running,
                "统一播放键暂停试听状态，不错误启动自动输入。");
            Call(form, "StopPlayback");
            settings.AutoMode = true; Set(form, "stopHotkeyAvailable", false);
            Hotkey(form, 1);
            Assert(!Field<AutoPlayer>(form, "autoPlayer").Running && !Field<bool>(form, "autoActive") && Field<Label>(form, "statusLabel").Text.Contains("停止热键"),
                "停止键不可用时统一播放键仍禁止自动吹奏。");
            Field<AutoPlayer>(form, "autoPlayer").Dispose();
            var fakePlayer = new AutoPlayer(input, clock);
            Set(form, "autoPlayer", fakePlayer); Set(form, "stopHotkeyAvailable", true);
            Hotkey(form, 1);
            Await(delegate { return fakePlayer.CountingDown; }, "同一键启动自动倒计时");
            Hotkey(form, 1);
            Assert(fakePlayer.Paused && Field<ScoreCanvas>(form, "canvas").CountdownText.Contains("F5 继续") && !Field<bool>(form, "playing"),
                "倒计时重复按播放键立即暂停，顶部写明实际继续键。");
            long generation = Field<long>(fakePlayer, "Generation");
            clock.Advance(8000); Thread.Sleep(25);
            Assert(fakePlayer.Paused && fakePlayer.CountingDown && input.Sends == 0 && fakePlayer.Position == 0, "倒计时暂停冻结时间且不发送键鼠。");
            Hotkey(form, 1);
            Assert(!fakePlayer.Paused && fakePlayer.CountingDown && Field<long>(fakePlayer, "Generation") == generation &&
                Field<ScoreCanvas>(form, "canvas").CountdownText.Contains("继续"), "继续倒计时沿用同一播放会话和剩余时间。");
            for (int i = 0; i < 8; i++) Hotkey(form, 1);
            Assert(!fakePlayer.Paused && Field<long>(fakePlayer, "Generation") == generation && input.Sends == 0,
                "快速反复切换仅暂停继续，未建立第二个输入会话。");
            Hotkey(form, 2);
            Assert(!fakePlayer.Running && !Field<bool>(form, "autoActive") && String.IsNullOrEmpty(Field<ScoreCanvas>(form, "canvas").CountdownText),
                "停止键取消倒计时并清除顶部准备提示。");
            Assert(input.Sends == 0, "实际MainForm统一播放流程全部只用测试后端，零真实输入。");
        }
        finally
        {
            if (form != null)
            {
                Field<System.Windows.Forms.Timer>(form, "timer").Dispose(); Field<AutoPlayer>(form, "autoPlayer").Dispose();
                Field<AudioPreview>(form, "audio").Dispose(); Field<ToolTip>(form, "sourceTip").Dispose(); form.Dispose();
            }
            if (existed) File.WriteAllBytes(path, original);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void LifecycleAndModalWindows()
    {
        string directory = Path.Combine(AppPaths.ContentDirectory, "data"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        bool existed = File.Exists(path); byte[] original = existed ? File.ReadAllBytes(path) : null;
        MainForm form = null;
        var registrar = new Registrar();
        NotifyIcon tray = null; ContextMenuStrip menu = null;
        try
        {
            File.WriteAllText(path, "{\"AutoMode\":false,\"Compact\":false,\"AlwaysOnTop\":false,\"Opacity\":1}");
            form = new MainForm(false); Field<System.Windows.Forms.Timer>(form, "timer").Stop();
            form.ControlKeyReader = delegate { return false; }; form.ControlModifiersReader = delegate { return false; };
            form.HotkeyRegistrar = registrar.Register; form.HotkeyReleaser = delegate(int id) { registrar.Bound.Remove(id); };
            form.ShowInTaskbar = true; form.Opacity = 0; form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000); form.Show(); form.Location = new Point(-30000, -30000);
            Call(form, "RegisterControlHotkeys");
            Assert(registrar.Bound.Count == 4 && registrar.Calls.Count == 4, "真实窗口只登记四键，不预留旧F6。");
            tray = Field<NotifyIcon>(form, "trayIcon"); menu = Field<ContextMenuStrip>(form, "trayMenu");
            Assert(tray.Icon != null && menu.Items.Count >= 6 && tray.Text.Contains(typeof(MainForm).Assembly.GetName().Version.ToString(3)),
                "通知区域入口具备真实图标、明确菜单和当前版本。");
            Assert(menu.Items.Cast<ToolStripItem>().Any(item => item.Text.Contains("退出节奏口琴")) &&
                menu.Items.Cast<ToolStripItem>().Any(item => item.Text.Contains("停止演奏")), "停止与退出在托盘菜单中明确区分。");
            NativeHotkey(form, 3);
            Assert(TaskbarCollapsed(form) && !form.IsDisposed && !Field<bool>(form, "closing") && registrar.Bound.Count == 4 && registrar.Calls.Count == 4,
                "实际WM_HOTKEY显隐只最小化到任务栏，不退出、不注销热键。");
            NativeHotkey(form, 3);
            Assert(form.Visible && form.WindowState == FormWindowState.Normal && !IsIconic(form.Handle) && registrar.Bound.Count == 4 && registrar.Calls.Count == 4,
                "收起后同一显隐消息恢复原窗及四个绑定。");
            NativeHotkey(form, 3);
            SendMessage(form.Handle, 0x0112, new IntPtr(0xf120), IntPtr.Zero);
            Assert(form.Visible && form.ShowInTaskbar && form.WindowState == FormWindowState.Normal && !IsIconic(form.Handle) && registrar.Bound.Count == 4,
                "任务栏使用的原生SC_RESTORE能够恢复主窗，不依赖托盘或重新启动。");
            int beforeRecreate = registrar.Calls.Count;
            typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
            Assert(registrar.Bound.Count == 4 && registrar.Calls.Count == beforeRecreate + 4,
                "窗口句柄重建后实际重新登记四键，不能只在首次Shown注册。");
            Assert(AllRegistered(Field<HotkeyBindingResult>(form, "currentHotkeyResult")), "句柄重建后运行状态仍报告四键已登记。");
            Call(form, "BeginPlaybackOperation");
            int callsBeforeModal = registrar.Calls.Count;
            NativeHotkey(form, 1);
            Assert(!Field<bool>(form, "playing") && Field<Label>(form, "statusLabel").Text.Contains("完成当前操作"),
                "选歌操作期间全局播放键给出理由而不启动。");
            NativeHotkey(form, 2);
            Assert(!Field<bool>(form, "playing") && registrar.Bound.Count == 4, "操作期间停止键始终可用。");
            bool compact = Field<bool>(form, "compact"); NativeHotkey(form, 4);
            Assert(Field<bool>(form, "compact") != compact && registrar.Calls.Count == callsBeforeModal,
                "操作期间迷你键有效且保留原绑定。");
            Call(form, "EndPlaybackOperation");
            RealModalVisibility(form, registrar);
            NestedModalVisibility(form, registrar);
            ForegroundPreservation(form, registrar);
            string diagnostics = (string)CallResult(form, "HotkeyDiagnostics");
            Assert(diagnostics.Contains("程序权限：") && diagnostics.Contains("已全局注册") &&
                diagnostics.Contains("旧版独立暂停键已取消") && diagnostics.Contains("最近收到的全局键"),
                "运行状态展示真实登记、版本、权限和最近收到消息，解释旧暂停键。");
            int closed = 0; form.FormClosed += delegate { closed++; };
            Field<Panel>(form, "header").Controls.OfType<SoftButton>().Single(button => button.Text == "×").PerformClick();
            Assert(closed == 1 && form.IsDisposed && Field<bool>(form, "closing") && Field<bool>(form, "resourcesDisposed") &&
                registrar.Bound.Count == 0 && !tray.Visible && menu.IsDisposed && !Field<System.Windows.Forms.Timer>(form, "timer").Enabled &&
                !Application.OpenForms.Cast<Form>().Contains(form),
                "实际X按钮真正关闭主窗并释放Timer、托盘、菜单和四键，不转成隐藏后台窗口。");
        }
        finally
        {
            if (form != null) form.Dispose();
            if (existed) File.WriteAllBytes(path, original); else if (File.Exists(path)) File.Delete(path);
        }
    }
    private static void ClosingOwnedWorkflows()
    {
        string path = Path.Combine(AppPaths.ContentDirectory, "data", "settings.json");
        bool existed = File.Exists(path); byte[] original = existed ? File.ReadAllBytes(path) : null;
        try
        {
            File.WriteAllText(path, "{\"AutoMode\":false,\"Compact\":false,\"AlwaysOnTop\":false,\"Opacity\":1}");
            foreach (string mode in new[] { "audio-close", "inline-close", "backup-close", "audio-dispose", "inline-dispose" })
            {
                bool audio = mode.StartsWith("audio", StringComparison.Ordinal);
                bool backup = mode == "backup-close", directDispose = mode.EndsWith("dispose", StringComparison.Ordinal);
                using (var owner = new MainForm(false))
                using (var cancellation = new CancellationTokenSource())
                using (Form work = audio ? (Form)new AudioMidiDialog("模拟进行中转录") : new OnlineCatalogDialog("", new List<SongEntry>(),
                    null, null, null, delegate(string query, int page) { return new FreeScorePage(); },
                    delegate(FreeScore score) { return new byte[0]; }, delegate(string query, int page) { return new List<OnlineSong>(); }))
                using (var login = new QqLoginDialog())
                using (var timeout = new System.Windows.Forms.Timer { Interval = 5000 })
                {
                    var registrar = new Registrar();
                    owner.ControlKeyReader = delegate { return false; }; owner.ControlModifiersReader = delegate { return false; };
                    owner.HotkeyRegistrar = registrar.Register; owner.HotkeyReleaser = delegate(int id) { registrar.Bound.Remove(id); };
                    owner.ShowInTaskbar = true; owner.Opacity = 0; owner.StartPosition = FormStartPosition.Manual;
                    owner.Location = new Point(-30000, -30000); owner.Show(); owner.Location = new Point(-30000, -30000);
                    Call(owner, "RegisterControlHotkeys");
                    work.ShowInTaskbar = false; work.Opacity = 0; work.StartPosition = FormStartPosition.Manual;
                    work.Location = new Point(-30000, -30000);
                    login.Owner = work; // Construct only: never show/initialize the browser or contact QQ.
                    if (audio) { Set(work, "working", true); Set(work, "cancellation", cancellation); }
                    else { Set(work, "downloading", true); Set(work, "inlineWorking", !backup); if (!backup) Set(work, "songCancellation", cancellation); }
                    CancellationToken token = cancellation.Token;
                    Exception failure = null; bool returned = false;
                    timeout.Tick += delegate { failure = new Exception("工作模态窗随主窗退出超时：" + mode); work.Dispose(); owner.Dispose(); };
                    work.Shown += delegate
                    {
                        work.BeginInvoke(new Action(delegate
                        {
                            try
                            {
                                NativeHotkey(owner, 3);
                                Assert(TaskbarCollapsed(owner) && work.Modal && !IsWindowVisible(work.Handle),
                                    "进行中" + mode + "随主窗收起保留任务栏和模态循环，未启动实际下载或转换。");
                                if (directDispose) owner.Dispose(); else owner.Close();
                                Assert(owner.IsDisposed && work.IsDisposed && (backup || token.IsCancellationRequested),
                                    "主窗" + (directDispose ? "Dispose" : "Close") + "真正结束进行中的" + mode + "，取消自己的任务而不被子窗阻挡。");
                                Assert(registrar.Bound.Count == 0 && !Field<System.Windows.Forms.Timer>(owner, "timer").Enabled &&
                                    !Field<NotifyIcon>(owner, "trayIcon").Visible && Field<ContextMenuStrip>(owner, "trayMenu").IsDisposed &&
                                    login.IsDisposed && Field<bool>(Field<QqWebViewHost>(login, "browser"), "disposed") &&
                                    (!audio || !Field<System.Windows.Forms.Timer>(work, "downloadTimer").Enabled),
                                    "工作中退出释放四键、主Timer、托盘、音频监测Timer及未启动的QQ浏览器host，无自身窗口后台残留。");
                            }
                            catch (Exception error) { failure = error; work.Dispose(); owner.Dispose(); }
                        }));
                    };
                    timeout.Start();
                    try { work.ShowDialog(owner); returned = true; }
                    finally { timeout.Stop(); }
                    if (failure != null) throw failure;
                    Assert(returned && owner.IsDisposed && work.IsDisposed,
                        "关闭或Dispose主窗后实际ShowDialog循环已返回，不留下隐藏工作模态窗口：" + mode);
                }
            }
        }
        finally
        {
            if (existed) File.WriteAllBytes(path, original); else if (File.Exists(path)) File.Delete(path);
        }
    }
    private static void RealModalVisibility(MainForm form, Registrar registrar)
    {
        using (var modal = new Form { Text = "保留选歌会话测试", ShowInTaskbar = false, Opacity = 0,
            StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), Size = new Size(360, 180) })
        using (var timeout = new System.Windows.Forms.Timer { Interval = 5000 })
        {
            var input = new TextBox { Text = "同一歌曲 / 同一下载会话", Location = new Point(20, 20), Width = 300 };
            modal.Controls.Add(input);
            Exception failure = null; bool secondTurn = false; int closingCalls = 0, registrations = registrar.Calls.Count;
            modal.FormClosing += delegate { closingCalls++; };
            timeout.Tick += delegate { failure = new Exception("模态显隐回归超时。"); modal.DialogResult = DialogResult.Cancel; };
            modal.Shown += delegate
            {
                modal.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        IntPtr sameHandle = modal.Handle;
                        NativeHotkey(form, 1);
                        Assert(!Field<bool>(form, "playing"), "真实ShowDialog期间播放键不开始演奏。");
                        NativeHotkey(form, 3);
                        Assert(TaskbarCollapsed(form) && !IsWindowVisible(modal.Handle), "显隐键将主窗保留在任务栏并隐藏实际模态HWND。");
                        Assert(modal.Visible && modal.Modal && modal.DialogResult == DialogResult.None && closingCalls == 0,
                            "原生隐藏保持托管Visible与ShowDialog循环，不误触关闭会话。");
                        Assert(registrar.Bound.Count == 4 && registrar.Calls.Count == registrations, "在线式模态窗口隐藏期间仍保留同一四键。");
                        NativeHotkey(form, 4);
                        modal.BeginInvoke(new Action(delegate
                        {
                            try
                            {
                                secondTurn = true;
                                Assert(modal.Modal && modal.DialogResult == DialogResult.None && closingCalls == 0,
                                    "返回真实模态消息循环后仍未关闭，不能只在一次回调中假保持。");
                                NativeHotkey(form, 3);
                                Assert(form.Visible && form.WindowState == FormWindowState.Normal && IsWindowVisible(modal.Handle) && modal.Handle == sameHandle && input.Text == "同一歌曲 / 同一下载会话",
                                    "同一显隐键恢复原模态窗口、HWND和用户当前内容。");
                                Assert(registrar.Bound.Count == 4 && registrar.Calls.Count == registrations && !Field<bool>(form, "closing"),
                                    "模态恢复没有重注册、退出或丢四键。");
                                NativeHotkey(form, 3);
                                Assert(TaskbarCollapsed(form) && !IsWindowVisible(modal.Handle) && modal.Modal && modal.DialogResult == DialogResult.None,
                                    "模态窗收起后主任务栏入口保留，原有选歌会话继续。");
                                SendMessage(form.Handle, 0x0112, new IntPtr(0xf120), IntPtr.Zero);
                                Assert(form.WindowState == FormWindowState.Normal && IsWindowVisible(modal.Handle) &&
                                    modal.Handle == sameHandle && modal.Modal && input.Text == "同一歌曲 / 同一下载会话",
                                    "任务栏SC_RESTORE恢复同一模态窗及当前输入，不重新打开选歌窗口。");
                                NativeHotkey(form, 3); form.WindowState = FormWindowState.Normal;
                                Assert(form.WindowState == FormWindowState.Normal && IsWindowVisible(modal.Handle) && modal.Modal &&
                                    modal.DialogResult == DialogResult.None && modal.Handle == sameHandle,
                                    "系统直接恢复最小化窗口的WM_SIZE路径也找回原有模态窗，不只依赖显隐键。");
                                modal.DialogResult = DialogResult.Cancel;
                            }
                            catch (Exception error) { failure = error; modal.DialogResult = DialogResult.Cancel; }
                        }));
                    }
                    catch (Exception error) { failure = error; modal.DialogResult = DialogResult.Cancel; }
                }));
            };
            Call(form, "BeginPlaybackOperation"); timeout.Start();
            try
            {
                modal.ShowDialog(form);
                if (failure != null) throw failure;
                Assert(secondTurn && closingCalls == 1, "只有最终明确关闭结束真实模态会话。");
            }
            finally { timeout.Stop(); Call(form, "EndPlaybackOperation"); }
        }
    }
    private static void NestedModalVisibility(MainForm form, Registrar registrar)
    {
        using (var catalog = new Form { Text = "在线曲库宿主测试", ShowInTaskbar = false, Opacity = 0,
            StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), Size = new Size(360, 180) })
        using (var login = new Form { Text = "登录页宿主测试", ShowInTaskbar = false, Opacity = 0,
            StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), Size = new Size(300, 160) })
        using (var timeout = new System.Windows.Forms.Timer { Interval = 5000 })
        {
            var query = new TextBox { Text = "已选歌曲", Location = new Point(20, 20), Width = 260 };
            var session = new TextBox { Text = "同一登录会话", Location = new Point(20, 20), Width = 240 };
            catalog.Controls.Add(query); login.Controls.Add(session);
            Exception failure = null; bool secondTurn = false;
            int catalogClosing = 0, loginClosing = 0, registrations = registrar.Calls.Count;
            catalog.FormClosing += delegate { catalogClosing++; };
            login.FormClosing += delegate { loginClosing++; };
            timeout.Tick += delegate
            {
                if (failure == null) failure = new Exception("嵌套模态显隐回归超时。");
                login.DialogResult = DialogResult.Cancel; catalog.DialogResult = DialogResult.Cancel;
            };
            catalog.Shown += delegate
            {
                catalog.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        login.ShowDialog(catalog);
                        if (failure != null) throw failure;
                        Assert(catalog.Modal && catalog.DialogResult == DialogResult.None && loginClosing == 1 && catalogClosing == 0,
                            "关闭登录子窗后返回原在线曲库会话，不关闭其宿主。");
                        catalog.DialogResult = DialogResult.Cancel;
                    }
                    catch (Exception error) { failure = error; login.DialogResult = DialogResult.Cancel; catalog.DialogResult = DialogResult.Cancel; }
                }));
            };
            login.Shown += delegate
            {
                login.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        IntPtr catalogHandle = catalog.Handle, loginHandle = login.Handle;
                        Assert(login.Owner == catalog && catalog.Owner == form && catalog.Modal && login.Modal,
                            "登录页以在线曲库为真实Owner，形成两层实际ShowDialog循环。");
                        NativeHotkey(form, 1);
                        Assert(!Field<bool>(form, "playing"), "嵌套登录期间播放键不启动演奏。");
                        NativeHotkey(form, 3);
                        Assert(TaskbarCollapsed(form) && !IsWindowVisible(catalog.Handle) && !IsWindowVisible(login.Handle),
                            "显隐键保留主任务栏入口，同时隐藏在线曲库与嵌套登录宿主的实际HWND。");
                        Assert(catalog.Visible && login.Visible && catalog.Modal && login.Modal &&
                            catalog.DialogResult == DialogResult.None && login.DialogResult == DialogResult.None && catalogClosing == 0 && loginClosing == 0,
                            "两层模态窗原生隐藏后仍保留托管会话，不误触任一关闭事件。");
                        NativeHotkey(form, 2);
                        NativeHotkey(form, 4);
                        Assert(registrar.Bound.Count == 4 && registrar.Calls.Count == registrations,
                            "嵌套登录隐藏时停止、迷你和四个全局登记仍保留。");
                        login.BeginInvoke(new Action(delegate
                        {
                            try
                            {
                                secondTurn = true;
                                Assert(catalog.Modal && login.Modal && catalog.DialogResult == DialogResult.None &&
                                    login.DialogResult == DialogResult.None && catalogClosing == 0 && loginClosing == 0,
                                    "回到真实嵌套消息循环后两个会话均保持打开。");
                                NativeHotkey(form, 3);
                                Assert(form.Visible && IsWindowVisible(catalog.Handle) && IsWindowVisible(login.Handle) &&
                                    catalog.Handle == catalogHandle && login.Handle == loginHandle,
                                    "恢复同一主窗和两个原有模态HWND，无重新创建登录宿主。");
                                Assert(query.Text == "已选歌曲" && session.Text == "同一登录会话" && login.Owner == catalog && catalog.Owner == form,
                                    "嵌套恢复保留歌曲、登录页面状态和原Owner链。");
                                Assert(registrar.Bound.Count == 4 && registrar.Calls.Count == registrations && !Field<bool>(form, "closing"),
                                    "嵌套恢复不重新登记热键、不退出程序。");
                                NativeHotkey(form, 3);
                                SendMessage(form.Handle, 0x0112, new IntPtr(0xf120), IntPtr.Zero);
                                Assert(form.WindowState == FormWindowState.Normal && IsWindowVisible(catalog.Handle) && IsWindowVisible(login.Handle) &&
                                    catalog.Handle == catalogHandle && login.Handle == loginHandle && login.Modal && catalog.Modal &&
                                    query.Text == "已选歌曲" && session.Text == "同一登录会话",
                                    "任务栏恢复同时找回原有两层模态窗和会话，保持Owner链。");
                                login.DialogResult = DialogResult.Cancel;
                            }
                            catch (Exception error) { failure = error; login.DialogResult = DialogResult.Cancel; }
                        }));
                    }
                    catch (Exception error) { failure = error; login.DialogResult = DialogResult.Cancel; }
                }));
            };
            Call(form, "BeginPlaybackOperation"); timeout.Start();
            try
            {
                catalog.ShowDialog(form);
                if (failure != null) throw failure;
                Assert(secondTurn && loginClosing == 1 && catalogClosing == 1,
                    "两个嵌套模态会话仅各在明确关闭时结束一次。");
            }
            finally { timeout.Stop(); Call(form, "EndPlaybackOperation"); }
        }
    }
    private static void ForegroundPreservation(MainForm form, Registrar registrar)
    {
        var timer = Field<System.Windows.Forms.Timer>(form, "timer");
        bool enabled = timer.Enabled;
        using (var foreground = new ForegroundPlaceholder())
        using (var activation = new ActivationProbe(form))
        {
            try
            {
                Call(form, "SetCompact", false); form.Activate();
                Field<TextBox>(form, "search").Focus();
                Assert(form.ContainsFocus, "后台布局验证先建立主窗曲库输入框的真实旧焦点。");
                foreground.Activate(); activation.Reset(); timer.Start();
                int registrations = registrar.Calls.Count;
                NativeHotkey(form, 3);
                Assert(TaskbarCollapsed(form) && timer.Enabled && registrar.Bound.Count == 4 && registrar.Calls.Count == registrations,
                    "F8收起后任务栏保留，25ms监测Timer及四个注册保持运行，无需切回软件重新启动。");
                Assert(GetForegroundWindow() == foreground.Handle && foreground.FocusHandle() == foreground.InputHandle,
                    "后台隐藏主窗不改变另一UI线程占位窗的前台与键盘焦点。");
                NativeHotkey(form, 3);
                Assert(form.Visible && form.ShowInTaskbar && form.WindowState == FormWindowState.Normal && IsWindowVisible(form.Handle) && GetForegroundWindow() == foreground.Handle,
                    "F8后台恢复显示软件，同时保留原前台窗口。");
                Assert(activation.Activations == 0 && activation.Focuses == 0,
                    "F8恢复全程没有主窗WM_ACTIVATE/WM_SETFOCUS，不能先抢焦点再还回。");
                bool compact = Field<bool>(form, "compact");
                NativeHotkey(form, 4);
                Assert(Field<bool>(form, "compact") != compact && form.ShowInTaskbar && (GetWindowStyle(form.Handle, -20) & 0x40000) != 0 &&
                    GetForegroundWindow() == foreground.Handle && activation.Activations == 0 && activation.Focuses == 0,
                    "F9隐藏旧焦点面板并显示迷你布局时也不激活主窗。实际 compact=" + Field<bool>(form, "compact") +
                    " / previous=" + compact + " / foreground=" + GetForegroundWindow() + " / expected=" + foreground.Handle +
                    " / activations=" + activation.Activations + " / focuses=" + activation.Focuses);
                Assert(NativeMinimumSize(form) == form.MinimumSize && form.Width >= form.MinimumSize.Width && form.Height >= form.MinimumSize.Height,
                    "后台迷你切换保留原生WM_GETMINMAXINFO与程序尺寸的最小窗口约束。");
                NativeHotkey(form, 4);
                Assert(Field<bool>(form, "compact") == compact && GetForegroundWindow() == foreground.Handle && activation.Activations == 0 && activation.Focuses == 0,
                    "F9恢复完整布局仍保持原前台，没有瞬态切出。");
                Assert(NativeMinimumSize(form) == form.MinimumSize && form.Width >= form.MinimumSize.Width && form.Height >= form.MinimumSize.Height,
                    "后台展开同步恢复完整布局的原生最小窗口约束。");
                Size fullWindowSize = form.Size;
                Point fullWindowLocation = form.Location;
                NativeHotkey(form, 3); NativeHotkey(form, 4);
                AppSettings saved = AppSettings.Load(Path.Combine(AppPaths.ContentDirectory, "data", "settings.json"));
                Assert(TaskbarCollapsed(form) && Field<Size>(form, "fullSize") == fullWindowSize && saved.X == fullWindowLocation.X && saved.Y == fullWindowLocation.Y &&
                    timer.Enabled && GetForegroundWindow() == foreground.Handle,
                    "窗口收起时F9只改变布局，保留任务栏、完整尺寸及原位置，监测不停止也不抢前台。");
                NativeHotkey(form, 3);
                Assert(form.Visible && form.ShowInTaskbar && form.WindowState == FormWindowState.Normal && GetForegroundWindow() == foreground.Handle && activation.Activations == 0 && activation.Focuses == 0,
                    "隐藏中切换迷你后F8继续无激活恢复。");
                form.WindowState = FormWindowState.Minimized; foreground.Activate(); activation.Reset();
                NativeHotkey(form, 3);
                Assert(form.WindowState == FormWindowState.Normal && form.Visible && GetForegroundWindow() == foreground.Handle &&
                    activation.Activations == 0 && activation.Focuses == 0,
                    "全局F8从最小化恢复使用无激活显示，不能通过WindowState先抢焦点。");
                Assert(foreground.FocusHandle() == foreground.InputHandle && registrar.Bound.Count == 4 && registrar.Calls.Count == registrations,
                    "显隐、迷你及最小化恢复均保留外部线程输入焦点和原四键。");
                NativeHotkey(form, 3); activation.Reset();
                Call(form, "ToggleWindowVisibility");
                WaitForeground(form.Handle, "托盘显式恢复：Visible=" + form.Visible + ", State=" + form.WindowState + ", Enabled=" + form.Enabled +
                    ", ExStyle=" + GetWindowStyle(form.Handle, -20) + ", passive=" + Field<bool>(form,"preserveForegroundForWindowControl") +
                    ", guard=" + Field<AutoPlaybackWindowGuard>(form,"autoWindowGuard").Enabled);
                Assert(form.Visible && activation.Activations > 0,
                    "托盘共用显隐入口仍正常激活主窗，便于用户直接操作软件。");
                NativeHotkey(form, 3); foreground.Activate(); activation.Reset();
                SendMessage(form.Handle, (int)Program.ActivateMessage, IntPtr.Zero, IntPtr.Zero);
                WaitForeground(form.Handle, "再次启动显式恢复");
                Assert(form.Visible && activation.Activations > 0,
                    "再次启动同一EXE的恢复消息仍激活主窗，不被全局无激活作用域遗留影响。");
                Assert(!Field<bool>(form, "autoActive") && !Field<AutoPlayer>(form, "autoPlayer").Running,
                    "前台回归只使用本软件占位窗口，不启动自动输入或向游戏发送按键。");
            }
            finally { if (!enabled) timer.Stop(); }
        }
    }
    private static void WaitForeground(IntPtr window, string stage = "占位窗口")
    {
        var timeout = Stopwatch.StartNew();
        while (GetForegroundWindow() != window)
        {
            Application.DoEvents();
            if (timeout.ElapsedMilliseconds > 3000) throw new Exception("无法将本软件自己的测试窗口置于前台，无法验证焦点交互。" + stage +
                "; expected=" + window + "; actual=" + GetForegroundWindow() + "; focus=" + GetFocus());
            Thread.Sleep(1);
        }
        Application.DoEvents();
    }
    private static bool TaskbarCollapsed(MainForm window)
    { return window.Visible && window.ShowInTaskbar && IsWindowVisible(window.Handle) && IsIconic(window.Handle) &&
        window.WindowState == FormWindowState.Minimized && (GetWindowStyle(window.Handle, -20) & 0x40000) != 0; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMinMaxInfo
    { internal NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    private static Size NativeMinimumSize(Form window)
    {
        IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMinMaxInfo)));
        try
        {
            Marshal.StructureToPtr(new NativeMinMaxInfo(), memory, false);
            SendMessage(window.Handle, 0x0024, IntPtr.Zero, memory);
            var value = (NativeMinMaxInfo)Marshal.PtrToStructure(memory, typeof(NativeMinMaxInfo));
            return new Size(value.MinTrackSize.X, value.MinTrackSize.Y);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    private sealed class ActivationProbe : NativeWindow, IDisposable
    {
        internal int Activations, Focuses;
        internal ActivationProbe(Form window) { AssignHandle(window.Handle); }
        internal void Reset() { Activations = Focuses = 0; }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0006 && (message.WParam.ToInt64() & 0xffff) != 0) Activations++;
            if (message.Msg == 0x0007) Focuses++;
            base.WndProc(ref message);
        }
        public void Dispose() { ReleaseHandle(); }
    }
    private sealed class ForegroundPlaceholder : IDisposable
    {
        private readonly Thread thread;
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private Form window;
        private TextBox input;
        private Exception failure;
        internal IntPtr Handle { get; private set; }
        internal IntPtr InputHandle { get; private set; }
        internal ForegroundPlaceholder()
        {
            thread = new Thread(delegate()
            {
                try
                {
                    using (window = new Form { Text = "本软件另一UI线程前台占位测试", ShowInTaskbar = false, Opacity = 0,
                        StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), Size = new Size(300, 150) })
                    {
                        input = new TextBox { Text = "保留占位窗输入焦点", Location = new Point(15, 15), Width = 250 };
                        window.Controls.Add(input);
                        window.Shown += delegate { Handle = window.Handle; InputHandle = input.Handle; ready.Set(); };
                        Application.Run(window);
                    }
                }
                catch (Exception error) { failure = error; ready.Set(); }
            });
            thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!WaitWithMessages(ready, 5000)) { Dispose(); throw new Exception("前台占位窗口创建超时。"); }
            if (failure != null) { Dispose(); throw failure; }
        }
        internal void Activate()
        {
            Invoke(delegate { window.Activate(); input.Focus(); });
            WaitForeground(Handle);
        }
        internal IntPtr FocusHandle()
        { IntPtr focused = IntPtr.Zero; Invoke(delegate { focused = GetFocus(); }); return focused; }
        private void Invoke(Action action)
        {
            Exception error = null;
            using (var completed = new ManualResetEvent(false))
            {
                window.BeginInvoke(new Action(delegate { try { action(); } catch (Exception caught) { error = caught; } finally { completed.Set(); } }));
                if (!WaitWithMessages(completed, 3000)) throw new Exception("前台占位窗口操作超时。");
            }
            if (error != null) throw error;
        }
        private static bool WaitWithMessages(WaitHandle completed, int milliseconds)
        {
            var timeout = Stopwatch.StartNew();
            while (!completed.WaitOne(0))
            {
                // Activation sends messages to the old foreground UI thread.
                // Keep its normal message pump alive while the placeholder works.
                Application.DoEvents();
                if (timeout.ElapsedMilliseconds > milliseconds) return false;
                Thread.Sleep(1);
            }
            return true;
        }
        public void Dispose()
        {
            if (window != null && !window.IsDisposed && window.IsHandleCreated) window.BeginInvoke(new Action(delegate { window.Close(); }));
            var timeout = Stopwatch.StartNew();
            while (thread != null && thread.IsAlive && timeout.ElapsedMilliseconds < 5000)
            { Application.DoEvents(); Thread.Sleep(1); }
            ready.Dispose();
            if (thread != null && thread.IsAlive) throw new Exception("本软件前台占位测试线程未能退出。");
        }
    }
    private static void NativeHotkey(MainForm form, int id)
    {
        form.HotkeyMessageTick = delegate { return unchecked((uint)Environment.TickCount); };
        form.ControlKeyReader = delegate { return false; }; form.ControlModifiersReader = delegate { return false; };
        string key = Field<HotkeyBindingResult>(form, "currentHotkeyResult").Names[id - 1];
        SendMessage(form.Handle, 0x0312, new IntPtr(id), new IntPtr(((int)(Keys)Enum.Parse(typeof(Keys), key)) << 16));
    }
    private static object CallResult(object target, string name, params object[] values)
    { return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, values); }

    private sealed class ControlledClock : IPlaybackClock
    {
        private long elapsed;
        public long Milliseconds { get { return Interlocked.Read(ref elapsed); } }
        public void Wait(int milliseconds) { Thread.Sleep(1); }
        public void Advance(int milliseconds) { Interlocked.Add(ref elapsed, milliseconds); }
    }
    private sealed class EmptyInput : IInputBackend
    {
        internal int Sends;
        public IntPtr GetForegroundWindow() { return new IntPtr(1); }
        public bool IsGameWindow(IntPtr window, out string reason) { reason = "测试窗口"; return false; }
        public bool Send(InputAction action, out string reason) { Interlocked.Increment(ref Sends); reason = null; return true; }
    }
    private static void Hotkey(MainForm form, int id)
    {
        form.HotkeyMessageTick = delegate { return unchecked((uint)Environment.TickCount); };
        string key = Field<HotkeyBindingResult>(form, "currentHotkeyResult").Names[id - 1];
        Message message = Message.Create(form.Handle, 0x0312, new IntPtr(id), new IntPtr(((int)(Keys)Enum.Parse(typeof(Keys), key)) << 16));
        Call(form, "WndProc", message);
    }
    private static T Field<T>(object target, string name)
    { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    private static void Set(object target, string name, object value)
    { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
    private static void Call(object target, string name, params object[] values)
    { target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, values); }
    private static void Await(Func<bool> condition, string name)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition()) { if (timeout.ElapsedMilliseconds > 3000) throw new Exception(name + "超时。"); Thread.Sleep(1); }
        Assert(true, name);
    }

    private sealed class Registrar
    {
        internal readonly HashSet<string> Occupied = new HashSet<string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> Errors = new Dictionary<string, int>(StringComparer.Ordinal);
        internal readonly Dictionary<int, string> Bound = new Dictionary<int, string>();
        internal readonly List<string> Calls = new List<string>();
        internal int Register(int id, string key)
        {
            Calls.Add(id + ":" + key);
            int error;
            if (Errors.TryGetValue(key, out error)) return error;
            if (Occupied.Contains(key) || Bound.ContainsValue(key)) return 1409;
            Bound[id] = key; return 0;
        }
        internal void ReleaseBindings() { Bound.Clear(); Calls.Clear(); }
    }
    private static bool AllRegistered(HotkeyBindingResult result)
    { foreach (bool value in result.Registered) if (!value) return false; return true; }
    private static int UniqueKeys(List<string> calls)
    { var keys = new HashSet<string>(); foreach (string call in calls) keys.Add(call.Split(':')[1]); return keys.Count; }
    private static void Assert(bool condition, string message)
    { checks++; if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    { checks++; try { action(); } catch (T) { return; } throw new Exception(message); }
}
