using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HarmonicaStudio
{
    public sealed class SongEntry
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Jianpu { get; set; }
        public string FileName { get; set; }
        public string AudioFileName { get; set; }
        public double Bpm { get; set; }
        public bool Builtin { get; set; }
        public string SourceHash { get; set; }
        public string SourceUrl { get; set; }
        public string License { get; set; }
        public string Attribution { get; set; }
        public string Artist { get; set; }
        public string SourceEdition { get; set; }
        public string LicenseUrl { get; set; }
        public bool Chinese { get; set; }
        public override string ToString() { return Title; }
    }

    internal static class Theme
    {
        public static readonly Color Background = Color.FromArgb(31, 34, 35);
        public static readonly Color Panel = Color.FromArgb(42, 45, 45);
        public static readonly Color Input = Color.FromArgb(55, 57, 55);
        public static readonly Color Line = Color.FromArgb(91, 89, 74);
        public static readonly Color Text = Color.FromArgb(249, 242, 215);
        public static readonly Color Muted = Color.FromArgb(199, 197, 181);
        public static readonly Color Accent = Color.FromArgb(245, 206, 105);
        public static readonly Color Warm = Color.FromArgb(245, 206, 105);
        public static Font Font(float size, FontStyle style = FontStyle.Regular)
        { return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point); }
        // Main-window control bounds use pixels. Keep their text on the same scale
        // instead of enlarging point fonts again when Windows uses 125%/150% DPI.
        public static Font PixelFont(float size, FontStyle style = FontStyle.Regular)
        { return new Font("Microsoft YaHei UI", size * 96f / 72f, style, GraphicsUnit.Pixel); }
    }

    internal sealed class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw, true);
        }
    }

    internal sealed class SoftButton : Button
    {
        public bool Primary { get; set; }
        public SoftButton(string text, bool primary)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw, true);
            Text = text; Primary = primary;
            Font = Theme.PixelFont(9); Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            BackColor = primary ? Theme.Accent : Theme.Input;
            ForeColor = primary ? Theme.Background : Theme.Text;
            Height = 34; TabStop = true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled) { base.OnPaint(e); return; }
            e.Graphics.Clear(Theme.Input);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    internal sealed class MainForm : PassiveOverlayForm
    {
        private readonly string dataDirectory;
        private readonly string settingsPath;
        private readonly SongLibraryStore libraryStore;
        private readonly AppSettings settings;
        private readonly AudioPreview audio = new AudioPreview();
        private readonly AutoPlayer autoPlayer = new AutoPlayer();
        private readonly List<SongEntry> library = new List<SongEntry>();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly Stopwatch playbackClock = new Stopwatch();
        private List<NoteEvent> notes = new List<NoteEvent>();
        private SongScore score;
        private SongEntry selected;
        private Panel header, body, footer, compactBody, sidebar, content, upperSection;
        private Label titleLabel, detailLabel, statusLabel, timeLabel, compactTitle, modeLabel, hotkeyLabel;
        private Label libraryLabel;
        private TextBox search;
        private ListBox songs;
        private ComboBox tracks;
        private ScoreCanvas canvas, compactCanvas;
        private SoftButton playButton, listenButton, compactPlayButton, sizeButton;
        private SoftButton deleteSongButton;
        private ContextMenuStrip songLibraryMenu;
        private CheckBox trimTailCheck, adaptPitchCheck;
        private double resizeAspect;
        private string tailMessage = "";
        private readonly AutoPlaybackWindowGuard autoWindowGuard;
        private SoftButton sourceButton;
        private Label licenseLabel;
        private readonly ToolTip sourceTip = new ToolTip();
        private TrackBar opacitySlider;
        private Label opacityValue;
        private NumericUpDown speedInput, transposeInput, baseInput;
        private CheckBox topCheck, autoModeCheck;
        private bool playing, listening, loading, compact;
        private double offset;
        private double noteDuration;
        private double sourceDuration, rangeStart, rangeEnd, targetSeconds;
        private bool repeatRange;
        private Point dragStart;
        private Size fullSize = new Size(1280, 720);
        private Size minimumWindowSize;
        private bool hotkeysAvailable = true;
        private bool autoActive, closing;
        private bool stopHotkeyAvailable;
        private bool presentingFrame, minimizedFrame;
        private readonly bool desktopIntegrationEnabled;
        private bool initialized, applicationCollapsed, restoringApplicationWindows, restoreHotkeysOnNewHandle, resourcesDisposed;
        private bool preserveForegroundForWindowControl;
        private int blockedPlaybackOperations;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem trayVisibility, trayPlayback, trayStop, trayCompact;
        private HotkeyBindingResult currentHotkeyResult;
        private readonly RegisteredHotkeyController hotkeyController;
        private readonly object diagnosticsGate = new object();
        private int lastReceivedHotkey;
        private readonly List<OwnedWindowVisibility> hiddenOwnedWindows = new List<OwnedWindowVisibility>();
        internal Func<int, string, int> HotkeyRegistrar;
        internal Action<int> HotkeyReleaser;
        internal Func<uint> HotkeyMessageTick;
        internal Func<string, bool> ControlKeyReader;
        internal Func<bool> ControlModifiersReader;
        private const int CompositedStyle = 0x02000000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowStyle(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowStyle(IntPtr window, int index, int value);
        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr window, IntPtr rectangle, IntPtr region, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern int GetMessageTime();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern IntPtr NativeForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);

        public MainForm() : this(true) { }
        internal MainForm(bool desktopIntegrationEnabled)
        {
            this.desktopIntegrationEnabled = desktopIntegrationEnabled;
            HotkeyMessageTick = delegate { return unchecked((uint)GetMessageTime()); };
            ControlKeyReader = IsControlKeyDown; ControlModifiersReader = AnyControlModifiersDown;
            hotkeyController = new RegisteredHotkeyController(delegate(string name) { return ControlKeyReader(name); },
                delegate { return ControlModifiersReader(); },
                delegate { return unchecked((uint)Environment.TickCount); }, delegate(int id)
                {
                    lastReceivedHotkey = id;
                    WritePlaybackDiagnostic("control", "功能 " + id + " · " + hotkeyController.LastTriggerSource);
                    if (id == 3 || id == 4) RunWindowControlWithoutActivation(delegate { HandleControlHotkey(id); });
                    else HandleControlHotkey(id);
                });
            HotkeyRegistrar = delegate(int id, string name)
            {
                Keys key = (Keys)Enum.Parse(typeof(Keys), name);
                if (RegisterHotKey(Handle, id, 0x4000, (uint)key)) return 0;
                int error = Marshal.GetLastWin32Error(); return error == 0 ? -1 : error;
            };
            HotkeyReleaser = delegate(int id) { if (IsHandleCreated) UnregisterHotKey(Handle, id); };
            dataDirectory = Path.Combine(AppPaths.ContentDirectory, "data");
            libraryStore = new SongLibraryStore(dataDirectory);
            settingsPath = Path.Combine(dataDirectory, "settings.json");
            settings = AppSettings.Load(settingsPath);
            // The game's keyboard register is fixed. Adapt the song instead of
            // asking the user to change the meaning of the game's note keys.
            settings.BasePitch = 60;
            autoWindowGuard = new AutoPlaybackWindowGuard(this);
            autoWindowGuard.Failed += delegate(string reason) { if (autoActive || autoPlayer.Running) { StopPlayback(); SetStatus("窗口保护未准备好：" + reason, true); } };
            Text = "节奏口琴";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.PixelFont(9); BackColor = Theme.Background; ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen;
            Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
            fullSize = FitLandscapeSize(new Size(settings.FullWidth, settings.FullHeight), workArea);
            Size = fullSize; MinimumSize = LandscapeMinimum(false, workArea);
            DoubleBuffered = true; KeyPreview = true;
            TopMost = settings.AlwaysOnTop; Opacity = settings.Opacity;
            if (settings.X != -1 && settings.Y != -1)
            {
                Rectangle saved = new Rectangle(settings.X, settings.Y, Width, Height);
                if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved)))
                { StartPosition = FormStartPosition.Manual; Location = saved.Location; }
            }
            BuildInterface(); LoadLibrary(); FilterLibrary(); InitializeTray();
            if (!String.IsNullOrEmpty(libraryStore.Warning)) SetStatus(libraryStore.Warning, true);
            audio.SessionFailed += delegate(long sessionId, string error)
            {
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke(new Action(delegate {
                    if (closing || IsDisposed || sessionId != audio.SessionId) return;
                    listening = false; SetStatus(error, true); UpdatePlaybackUI();
                })); }
                catch (InvalidOperationException) { }
            };
            autoPlayer.SessionStateChanged += delegate(long sessionId, string message)
            {
                WritePlaybackDiagnostic("state", message);
                if (closing || IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new Action(delegate { ApplyAutoPlayerState(message, sessionId); }));
                }
                catch (InvalidOperationException) { }
            };
            timer.Interval = 25; timer.Tick += delegate { TickPlayback(); }; timer.Start();
            FormClosing += ClosingForm;
            Shown += delegate
            {
                WritePlaybackDiagnostic("startup", "版本 " + typeof(MainForm).Assembly.GetName().Version + " · " + WindowsInputAccess.OwnDescription());
                if (this.desktopIntegrationEnabled) { trayIcon.Visible = true; RegisterControlHotkeys(); }
                SetCompact(settings.Compact); ClampToWorkArea();
            };
            initialized = true;
        }

        private void ApplyAutoPlayerState(string message, long sessionId)
        {
            if (closing || sessionId != autoPlayer.SessionId) return;
            if (!autoActive)
            {
                if (message.Contains("释放") && message.Contains("失败")) SetStatus(message, true);
                return;
            }
            if (!autoPlayer.Running) { CompleteAutomaticPlayback(); return; }
            playing = !autoPlayer.Paused;
            if (autoPlayer.WaitingForGame && autoPlayer.Paused)
                message = "等待游戏窗口已暂停 · " + settings.StartHotkey + " 继续";
            else if (autoPlayer.WaitingForGame && message == "继续等待游戏窗口。")
                message = "继续等待游戏窗口 · 请装备口琴并切回游戏";
            else if (autoPlayer.CountingDown && autoPlayer.Paused)
                message = "倒计时已暂停 · " + settings.StartHotkey + " 继续";
            else if (autoPlayer.CountingDown && message == "倒计时继续。")
                message = "倒计时继续 · " + settings.StartHotkey + " 可暂停";
            else if (!autoPlayer.CountingDown && message.StartsWith("倒计时", StringComparison.Ordinal)) return;
            SetStatus(message, message.Contains("失败") || message.Contains("停止") || message.Contains("无法"));
            UpdatePlaybackUI();
        }

        private void CompleteAutomaticPlayback()
        {
            offset = Math.Min(Duration, autoPlayer.Position);
            autoActive = playing = listening = false; playbackClock.Stop(); audio.Stop();
            string message = autoPlayer.LastCompletionMessage ?? "自动演奏已结束。";
            SetStatus(message, message.Contains("失败") || message.Contains("停止") || message.Contains("未开始"));
            UpdatePlaybackUI();
        }

        private void InitializeTray()
        {
            trayMenu = new ContextMenuStrip();
            trayVisibility = new ToolStripMenuItem("显示 / 收起窗口", null, delegate { ToggleWindowVisibility(); });
            trayPlayback = new ToolStripMenuItem("开始 / 暂停 / 继续", null, delegate { HandleControlHotkey(1); });
            trayStop = new ToolStripMenuItem("停止演奏", null, delegate { HandleControlHotkey(2); });
            trayCompact = new ToolStripMenuItem("切换迷你模式", null, delegate { HandleControlHotkey(4); });
            trayMenu.Items.AddRange(new ToolStripItem[] { trayVisibility, trayPlayback, trayStop, trayCompact,
                new ToolStripSeparator(), new ToolStripMenuItem("热键与运行状态…", null, delegate { ShowHotkeyDiagnostics(); }),
                new ToolStripSeparator(), new ToolStripMenuItem("退出节奏口琴", null, delegate { Close(); }) });
            trayIcon = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, ContextMenuStrip = trayMenu, Visible = false };
            trayIcon.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ToggleWindowVisibility(); };
            UpdateTrayState();
        }
        private void UpdateTrayState()
        {
            if (trayIcon == null || resourcesDisposed) return;
            string version = typeof(MainForm).Assembly.GetName().Version.ToString(3);
            string hint = "节奏口琴 " + version + " · " + settings.StartHotkey + " 播放 / " + settings.ToggleWindowHotkey + " 显隐";
            trayIcon.Text = hint.Length > 63 ? hint.Substring(0, 63) : hint;
            trayVisibility.Text = settings.ToggleWindowHotkey + (applicationCollapsed || !Visible || WindowState == FormWindowState.Minimized ? " 显示窗口" : " 收起到任务栏");
            trayPlayback.Text = settings.StartHotkey + " 开始 / 暂停 / 继续";
            trayStop.Text = settings.StopHotkey + " 停止演奏";
            trayCompact.Text = settings.CompactHotkey + (compact ? " 展开主窗口" : " 切换迷你模式");
        }
        private void BeginPlaybackOperation() { blockedPlaybackOperations++; UpdateTrayState(); }
        private void EndPlaybackOperation()
        { if (blockedPlaybackOperations > 0) blockedPlaybackOperations--; UpdateTrayState(); }
        private void HandleControlHotkey(int id)
        {
            if (closing || IsDisposed) return;
            switch (id)
            {
                case 1:
                    if (blockedPlaybackOperations > 0 || !Enabled || OwnedForms.Any(f => f.Modal && !f.IsDisposed))
                    { SetStatus("当前正在选歌、导入或设置；完成当前操作后再按 " + settings.StartHotkey + "。显隐、迷你和停止键仍可用。", true); return; }
                    TogglePlayback(); break;
                case 2: StopPlayback(); SetStatus("已停止", false); break;
                case 3: ToggleWindowVisibility(); break;
                case 4: SetCompact(!compact); UpdateTrayState(); break;
            }
        }
        private string HotkeyDiagnostics()
        {
            string[] names = { settings.StartHotkey, settings.StopHotkey, settings.ToggleWindowHotkey, settings.CompactHotkey };
            string[] actions = { "开始 / 暂停 / 继续", "停止", "显示 / 隐藏", "迷你 / 展开" };
            var text = new StringBuilder("节奏口琴 " + typeof(MainForm).Assembly.GetName().Version.ToString(3));
            for (int i = 0; i < names.Length; i++)
                text.AppendLine().Append(names[i]).Append("  ").Append(actions[i]).Append(currentHotkeyResult != null && currentHotkeyResult.Registered[i]
                    ? "（已全局注册）" : "（未注册" + (currentHotkeyResult == null ? "" : "，系统代码 " + currentHotkeyResult.Errors[i]) + "）");
            text.AppendLine().AppendLine().Append("程序权限：").Append(WindowsInputAccess.OwnDescription());
            text.AppendLine().Append("最近收到的全局键：").Append(lastReceivedHotkey >= 1 && lastReceivedHotkey <= 4 ? names[lastReceivedHotkey - 1] : "尚未收到");
            text.AppendLine().Append("最近触发来源：").Append(hotkeyController.LastTriggerSource ?? "尚未触发");
            text.AppendLine().Append("自动吹奏：").Append(settings.AutoMode ? "已开启" : "未开启（目前只滚动参考谱面）");
            text.AppendLine().Append("本轮已提交音符按下：").Append(autoPlayer.SubmittedNoteInputs).Append(" 次（不代表游戏已接收）");
            text.AppendLine().Append("当前状态：").Append(statusLabel.Text);
            text.AppendLine().AppendLine().Append("播放使用同一个键，旧版独立暂停键已取消。收起后程序仍运行，任务栏图标保留，可点击任务栏恢复；关闭按钮会真正退出。");
            text.AppendLine().Append("选歌、导入和设置期间播放键暂不启动演奏；显隐、迷你和停止仍有效。");
            text.AppendLine().Append("全局显隐／迷你仅改变窗口并保留游戏前台；点击托盘或再次打开 EXE 会激活软件。");
            text.AppendLine().Append("自动吹奏倒计时后会再等待游戏前台最多 15 秒；等待中没有演奏输入。游戏需装备口琴，开始后保持原游戏窗口在前台。");
            text.AppendLine().Append("Windows 热键消息与四个已注册按键的状态监测合并，每次松开触发一次。被游戏或系统保护屏蔽时仍可能不可用。");
            text.AppendLine().Append("游戏权限高于软件时可按下方“管理员方式重启”，由 Windows 请求授权；不改变游戏保护。F12 为 Windows 调试器保留键，建议用 F5–F11。");
            text.AppendLine().Append("诊断记录：程序文件/data/logs/playback.log（不含歌曲音频或账号会话）");
            return text.ToString();
        }
        private void ShowHotkeyDiagnostics()
        {
            RestoreApplicationWindows(); BeginPlaybackOperation();
            try
            {
                using (var dialog = new RuntimeDiagnosticsDialog(HotkeyDiagnostics(), RestartAsAdministrator))
                    dialog.ShowDialog(OwnedForms.LastOrDefault(f => f.Visible && !f.IsDisposed) ?? this);
            }
            finally { EndPlaybackOperation(); }
        }
        private static bool IsControlKeyDown(string name)
        { return (GetAsyncKeyState((int)(Keys)Enum.Parse(typeof(Keys), name)) & 0x8000) != 0; }
        private static bool AnyControlModifiersDown()
        {
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c })
                if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
            return false;
        }
        private string RestartAsAdministrator()
        {
            StopPlayback(); SaveSettings();
            string failure;
            if (!Program.RequestElevatedRestart(out failure)) return failure;
            Close(); return null;
        }
        private void WritePlaybackDiagnostic(string kind, string message)
        {
            if (!desktopIntegrationEnabled) return;
            try
            {
                lock (diagnosticsGate)
                {
                    string directory = Path.Combine(dataDirectory, "logs"), path = Path.Combine(directory, "playback.log");
                    Directory.CreateDirectory(directory);
                    string value = (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
                    if (value.Length > 768) value = value.Substring(0, 768);
                    if (File.Exists(path) && new FileInfo(path).Length > 65536) File.WriteAllText(path, "", new UTF8Encoding(false));
                    File.AppendAllText(path, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + " [" + kind + "] " + value + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private Label LabelAt(string text, int x, int y, int width, int height, float size, Color color, FontStyle style = FontStyle.Regular)
        {
            return new Label { Text = text, Location = new Point(x, y), Size = new Size(width, height), Font = Theme.PixelFont(size, style), ForeColor = color, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft };
        }
        private SoftButton ButtonAt(string text, int x, int y, int width, bool primary, Action action)
        {
            var button = new SoftButton(text, primary) { Location = new Point(x, y), Width = width };
            button.Click += delegate { action(); }; return button;
        }
        private NumericUpDown NumberAt(int x, int y, int width, decimal min, decimal max, decimal value, int decimals, decimal increment)
        {
            return new NumericUpDown { Location = new Point(x, y), Width = width, Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), DecimalPlaces = decimals, Increment = increment, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = Theme.PixelFont(10), Height = 30 };
        }
        private void BuildInterface()
        {
            int mainWidth = Width - 32 - 276 - 18;
            header = new BufferedPanel { Dock = DockStyle.Top, Size = new Size(Width, 50), BackColor = Theme.Background };
            header.Controls.Add(LabelAt("♫", 20, 8, 36, 38, 20, Theme.Accent, FontStyle.Bold));
            header.Controls.Add(LabelAt("节奏口琴", 57, 4, 170, 26, 12, Theme.Text, FontStyle.Bold));
            header.Controls.Add(LabelAt("多轨口琴  /  瀑布谱面", 58, 28, 225, 18, 7, Theme.Muted));
            var reference = ButtonAt("参考谱面", Width - 281, 8, 104, false, ApplyReferenceLayout);
            reference.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            sourceTip.SetToolTip(reference, "展开大瀑布与按键图例，收起设置区；调整为 75% 不透明度，可继续拖边缩放");
            sizeButton = ButtonAt("迷你", Width - 169, 8, 54, false, delegate { SetCompact(!compact); });
            var minimize = ButtonAt("—", Width - 107, 8, 38, false, CollapseApplicationWindows);
            var close = ButtonAt("×", Width - 61, 8, 38, false, Close);
            sizeButton.Anchor = minimize.Anchor = close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            header.Controls.AddRange(new Control[] { reference, sizeButton, minimize, close });
            header.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) dragStart = e.Location; };
            header.MouseMove += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Location = new Point(Left + e.X - dragStart.X, Top + e.Y - dragStart.Y); };
            foreach (Control c in header.Controls.OfType<Label>())
            {
                c.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) dragStart = PointToClient(((Control)sender).PointToScreen(e.Location)); };
                c.MouseMove += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Point p = PointToClient(((Control)sender).PointToScreen(e.Location)); Location = new Point(Left + p.X - dragStart.X, Top + p.Y - dragStart.Y); } };
            }
            footer = new BufferedPanel { Dock = DockStyle.Bottom, Size = new Size(Width, 38), BackColor = Theme.Background };
            statusLabel = LabelAt("已就绪 · 选择歌曲或导入自己的乐谱", 20, 0, Width - 68, 18, 8.5f, Theme.Muted);
            statusLabel.AutoEllipsis = true;
            statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            footer.Controls.Add(statusLabel);
            hotkeyLabel = LabelAt("", 20, 18, Width - 68, 20, 9, Theme.Muted);
            hotkeyLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            footer.Controls.Add(hotkeyLabel);
            var resizeGrip = new WindowResizeGrip { Location = new Point(Width - 26, 14), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            sourceTip.SetToolTip(resizeGrip, "拖动调整窗口大小；完整和迷你模式均可调整"); footer.Controls.Add(resizeGrip);
            body = new BufferedPanel { Dock = DockStyle.Fill, Size = new Size(Width, Height - 88), Padding = new Padding(16, 4, 16, 4) };
            upperSection = new BufferedPanel { Dock = DockStyle.Top, Height = 244, Width = Width - 32 };
            sidebar = new BufferedPanel { Dock = DockStyle.Left, Size = new Size(276, 244), BackColor = Theme.Panel };
            libraryLabel = LabelAt("我的曲库", 12, 4, 195, 25, 11, Theme.Text, FontStyle.Bold);
            sidebar.Controls.Add(libraryLabel);
            deleteSongButton = ButtonAt("删除", 213, 3, 51, false, DeleteSelectedSong);
            deleteSongButton.Enabled = false; sidebar.Controls.Add(deleteSongButton);
            search = new TextBox { Location = new Point(12, 36), Width = 252, MaxLength = 80, Font = Theme.PixelFont(11), BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Input, ForeColor = Theme.Text, AccessibleName = "输入歌名，选择对应乐谱" };
            search.TextChanged += delegate { FilterLibrary(); };
            search.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter && songs.Items.Count > 0) { songs.SelectedIndex = 0; e.SuppressKeyPress = true; } };
            songs = new ListBox { Location = new Point(8, 72), Size = new Size(260, 124), BorderStyle = BorderStyle.None, BackColor = Theme.Panel, ForeColor = Theme.Text, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 54, IntegralHeight = false, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            songs.DrawItem += DrawSong; songs.SelectedIndexChanged += delegate { deleteSongButton.Enabled = songs.SelectedItem != null; if (songs.SelectedItem != null) LoadSong((SongEntry)songs.SelectedItem); };
            songs.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Delete && e.Modifiers == Keys.None) { e.SuppressKeyPress = true; DeleteSelectedSong(); } };
            songLibraryMenu = new ContextMenuStrip();
            var remove = songLibraryMenu.Items.Add("删除所选曲目…", null, delegate { DeleteSelectedSong(); });
            var findMelody = songLibraryMenu.Items.Add("查找主旋律谱（保留原谱）", null, delegate { FindMelodyScoreFromLibrary(); });
            var reconvert = songLibraryMenu.Items.Add("重新转录已有音频（保留原谱）", null, delegate { ReconvertSelectedAudio(); });
            var restore = songLibraryMenu.Items.Add("恢复内置曲目", null, delegate
            { try { RestoreBuiltinSongs(); } catch (Exception error) { SetStatus("恢复未完成：" + error.Message, true); } });
            songLibraryMenu.Opening += delegate
            {
                remove.Enabled = songs.SelectedItem != null;
                findMelody.Enabled = songs.SelectedItem != null;
                SongEntry entry = songs.SelectedItem as SongEntry;
                reconvert.Enabled = entry != null && !String.IsNullOrEmpty(entry.AudioFileName);
                restore.Enabled = settings.HiddenBuiltinTitles.Count > 0;
            };
            songs.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Right) { int index = songs.IndexFromPoint(e.Location); if (index >= 0) songs.SelectedIndex = index; } };
            songs.ContextMenuStrip = songLibraryMenu;
            sidebar.Controls.AddRange(new Control[] { search, songs });
            var lookup = ButtonAt("在线曲库", 12, 204, 80, false, FindScoreOnline);
            var import = ButtonAt("导入 MIDI", 100, 204, 80, false, ImportMidi);
            var paste = ButtonAt("简谱/音频", 188, 204, 80, false, AddScoreSource);
            lookup.Anchor = import.Anchor = paste.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            sidebar.Controls.AddRange(new Control[] { lookup, import, paste });
            content = new BufferedPanel { Dock = DockStyle.Fill, Size = new Size(mainWidth + 18, 244), Padding = new Padding(18, 0, 0, 0) };
            var main = new BufferedPanel { Dock = DockStyle.Fill, Size = new Size(mainWidth, 244), BackColor = Theme.Background };
            titleLabel = LabelAt("选择歌曲", 0, 0, mainWidth - 300, 32, 17, Theme.Text, FontStyle.Bold);
            detailLabel = LabelAt("选择歌曲后自动使用主旋律", 0, 34, mainWidth - 300, 21, 8, Theme.Muted);
            titleLabel.AutoEllipsis = detailLabel.AutoEllipsis = true;
            titleLabel.Anchor = detailLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            autoModeCheck = new CheckBox { Text = "自动吹奏", Location = new Point(485, 102), Size = new Size(115, 25), ForeColor = Theme.Text, BackColor = Theme.Background, Checked = settings.AutoMode, Font = Theme.PixelFont(9) };
            modeLabel = LabelAt("手动跟谱 · 本地提示", 450, 139, mainWidth - 450, 27, 8, Theme.Accent);
            modeLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            main.Controls.AddRange(new Control[] { titleLabel, detailLabel, modeLabel });
            main.Controls.Add(autoModeCheck);
            autoModeCheck.CheckedChanged += delegate
            {
                StopPlayback(); settings.AutoMode = autoModeCheck.Checked;
                UpdateModeUI(); SaveSettings();
                SetStatus(settings.AutoMode ? "自动输入可能违规或封号；准备口琴后按开始键。" : "手动跟谱模式 · 按音符提示自己吹奏", settings.AutoMode);
            };
            canvas = new ScoreCanvas { Name = "FullWaterfall", Dock = DockStyle.Fill, Size = new Size(Width - 32, 370) };
            canvas.Seek += SeekTo;
            main.Controls.Add(LabelAt("主旋律", 0, 61, 64, 29, 9, Theme.Muted));
            tracks = new ComboBox { Location = new Point(72, 61), Width = mainWidth - 72, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.Input, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat, Font = Theme.PixelFont(9), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            tracks.DrawMode = DrawMode.OwnerDrawFixed; tracks.ItemHeight = 24;
            tracks.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                using (var brush = new SolidBrush(Theme.Input)) e.Graphics.FillRectangle(brush, e.Bounds);
                if (e.Index >= 0)
                {
                    ScoreTrack track = (ScoreTrack)tracks.Items[e.Index];
                    string text = (MelodyPreparation.IsPrimaryRole(track) ? "" : "旋律候选 · ") + track.ToString();
                    TextRenderer.DrawText(e.Graphics, text, tracks.Font, e.Bounds, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            };
            tracks.SelectedIndexChanged += delegate { if (!loading) RebuildNotes(); };
            main.Controls.Add(tracks);
            timeLabel = LabelAt("00:00 / 00:00", 0, 100, 130, 27, 10, Theme.Muted);
            playButton = ButtonAt("▶ 开始跟谱", 142, 97, 140, true, TogglePlayback);
            listenButton = ButtonAt("♫ 离线试听", 292, 97, 124, false, ToggleListen);
            var stop = ButtonAt("■", 426, 97, 42, false, StopPlayback);
            main.Controls.AddRange(new Control[] { timeLabel, playButton, listenButton, stop });
            main.Controls.Add(LabelAt("速度", 0, 139, 38, 27, 8, Theme.Muted));
            speedInput = NumberAt(40, 140, 75, 0.5m, 1.5m, (decimal)settings.Speed, 2, 0.05m);
            main.Controls.Add(LabelAt("移调", 130, 139, 36, 27, 8, Theme.Muted));
            transposeInput = NumberAt(169, 140, 65, -24, 24, settings.Transpose, 0, 1);
            baseInput = NumberAt(360, 140, 67, 36, 84, settings.BasePitch, 0, 1);
            baseInput.Visible = false;
            main.Controls.Add(LabelAt("音域根据旋律自动适配", 249, 139, 185, 27, 8, Theme.Muted));
            speedInput.ValueChanged += delegate
            {
                if (autoActive) StopPlayback();
                double position = Position; offset = position; playbackClock.Restart();
                settings.Speed = (double)speedInput.Value;
                if (targetSeconds > 0) RebuildNotes();
                else if (listening && playing) StartAudio();
                SaveSettings();
            };
            transposeInput.ValueChanged += delegate { settings.Transpose = (int)transposeInput.Value; RebuildNotes(); SaveSettings(); };
            baseInput.ValueChanged += delegate { settings.BasePitch = (int)baseInput.Value; canvas.BasePitch = compactCanvas.BasePitch = settings.BasePitch; RebuildNotes(); SaveSettings(); };
            main.Controls.AddRange(new Control[] { speedInput, transposeInput });
            main.Controls.Add(LabelAt("窗口不透明度", 0, 180, 90, 29, 8, Theme.Muted));
            opacitySlider = new TrackBar { Location = new Point(92, 179), Width = 146, AutoSize = false, Height = 32, Minimum = 65, Maximum = 100, TickStyle = TickStyle.None, Value = (int)(settings.Opacity * 100), BackColor = Theme.Background };
            opacityValue = LabelAt(opacitySlider.Value + "%", 242, 180, 49, 29, 8, Theme.Muted);
            opacitySlider.ValueChanged += delegate { Opacity = opacitySlider.Value / 100.0; settings.Opacity = Opacity; opacityValue.Text = opacitySlider.Value + "%"; SaveSettings(); };
            topCheck = new CheckBox { Text = "始终置顶", Location = new Point(305, 183), Size = new Size(99, 25), ForeColor = Theme.Text, BackColor = Theme.Background, Checked = settings.AlwaysOnTop, Font = Theme.PixelFont(9) };
            topCheck.CheckedChanged += delegate { TopMost = topCheck.Checked; settings.AlwaysOnTop = TopMost; SaveSettings(); };
            main.Controls.AddRange(new Control[] { opacitySlider, opacityValue, topCheck });
            var help = ButtonAt("使用说明 ↗", 419, 179, 104, false, OpenHelp);
            main.Controls.Add(help);
            main.Controls.Add(ButtonAt("热键 / 倒计时", 531, 179, 128, false, ConfigureHotkeys));
            main.Controls.Add(ButtonAt("片段时长", 605, 97, 96, false, ConfigurePlaybackRange));
            trimTailCheck = new CheckBox { Text = "自动略过无效尾音", Location = new Point(0, 217), Size = new Size(228, 25), Font = Theme.PixelFont(9), Checked = settings.TrimInvalidTail };
            adaptPitchCheck = new CheckBox { Text = "适配附近音／八度", Location = new Point(246, 217), Size = new Size(228, 25), Font = Theme.PixelFont(9), Checked = settings.AdaptNearbyPitch };
            trimTailCheck.CheckedChanged += delegate { settings.TrimInvalidTail = trimTailCheck.Checked; RebuildNotes(); SaveSettings(); };
            adaptPitchCheck.CheckedChanged += delegate { settings.AdaptNearbyPitch = adaptPitchCheck.Checked; RebuildNotes(); SaveSettings(); };
            sourceTip.SetToolTip(trimTailCheck, "完整播放时略过超音域尾音和少量疑似弱噪声；手动片段及指定循环时长优先，原文件不改。关闭可恢复原尾部。");
            sourceTip.SetToolTip(adaptPitchCheck, "只对超音域音符尝试一次八度调整或最多两半音邻近音，保留原文件。");
            main.Controls.AddRange(new Control[] { trimTailCheck, adaptPitchCheck });
            var export = ButtonAt("导出 MIDI", mainWidth - 284, 0, 132, false, ExportMidi);
            export.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            main.Controls.Add(export);
            sourceButton = ButtonAt("乐谱来源 ↗", mainWidth - 142, 0, 142, false, OpenSongSource);
            sourceButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            sourceButton.Enabled = false;
            licenseLabel = LabelAt("本地乐谱", mainWidth - 284, 34, 284, 21, 8, Theme.Muted);
            licenseLabel.AutoEllipsis = true; licenseLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            main.Controls.AddRange(new Control[] { sourceButton, licenseLabel });
            content.Controls.Add(main);
            upperSection.Controls.Add(content); upperSection.Controls.Add(sidebar);
            body.Controls.Add(canvas);
            body.Controls.Add(new BufferedPanel { Dock = DockStyle.Top, Height = 8 });
            body.Controls.Add(upperSection);
            compactBody = new BufferedPanel { Dock = DockStyle.Fill, Size = new Size(Width, Height - 88), Padding = new Padding(16, 0, 16, 4), Visible = false };
            var compactTop = new BufferedPanel { Dock = DockStyle.Top, Height = 42, Width = Width - 32 };
            compactTitle = LabelAt("选择歌曲", 0, 4, Width - 32 - 358, 30, 11, Theme.Text, FontStyle.Bold);
            compactTitle.AutoEllipsis = true; compactTitle.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            var compactHelp = ButtonAt("图例详解 ↗", Width - 32 - 338, 2, 112, false, OpenWaterfallHelp);
            compactPlayButton = ButtonAt("▶ 跟谱", Width - 32 - 214, 2, 132, true, TogglePlayback);
            var compactStop = ButtonAt("■", Width - 32 - 72, 2, 54, false, StopPlayback);
            compactHelp.Anchor = compactPlayButton.Anchor = compactStop.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            compactCanvas = new ScoreCanvas { Name = "CompactWaterfall", Dock = DockStyle.Fill, Size = new Size(Width - 32, 256), Compact = true };
            compactCanvas.Seek += SeekTo;
            compactTop.Controls.AddRange(new Control[] { compactTitle, compactHelp, compactPlayButton, compactStop });
            compactBody.Controls.Add(compactCanvas); compactBody.Controls.Add(compactTop);
            Controls.Add(body); Controls.Add(compactBody); Controls.Add(footer); Controls.Add(header);
            canvas.BasePitch = compactCanvas.BasePitch = settings.BasePitch;
            UpdateLandscapeLayout(); UpdateModeUI(); UpdateHotkeyHint();
        }

        private void LoadLibrary()
        {
            foreach (LibrarySong song in ScoreIO.Builtins())
                if (!settings.HiddenBuiltinTitles.Contains(song.Title)) library.Add(new SongEntry { Title = song.Title, Subtitle = song.Subtitle, Jianpu = song.Jianpu, Bpm = song.Bpm, Builtin = true, Chinese = song.Chinese });
            library.AddRange(libraryStore.Load().Where(entry => !entry.Builtin));
            try { SongFileLibrary.Synchronize(dataDirectory, library); }
            catch (Exception ex) { SetStatus("部分曲目文件未整理完成：" + ex.Message + "。原曲库仍可使用。", true); }
        }
        private bool ValidLibraryFile(string file)
        {
            return Path.GetFileName(file) == file && (file.EndsWith(".mid", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".midi", StringComparison.OrdinalIgnoreCase));
        }
        private void SaveLibrary()
        {
            libraryStore.Save(library);
        }
        private void FilterLibrary()
        {
            SongEntry previous = songs.SelectedItem as SongEntry;
            string query = search.Text.Trim();
            songs.BeginUpdate(); songs.Items.Clear();
            foreach (SongEntry item in library.Where(s => String.IsNullOrEmpty(query) || s.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (s.Subtitle ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)) songs.Items.Add(item);
            songs.EndUpdate();
            libraryLabel.Text = "我的曲库 · " + library.Count + " 首";
            if (previous != null && songs.Items.Contains(previous)) songs.SelectedItem = previous;
            else if (songs.Items.Count > 0) songs.SelectedIndex = 0;
            else
            {
                StopPlayback();
                selected = null; score = null; notes = new List<NoteEvent>(); noteDuration = sourceDuration = 0;
                rangeStart = rangeEnd = targetSeconds = 0; repeatRange = false; tailMessage = "";
                tracks.Items.Clear(); tracks.Enabled = false; canvas.Notes = compactCanvas.Notes = notes;
                canvas.BackgroundTracks = compactCanvas.BackgroundTracks = null;
                canvas.SongTitle = compactCanvas.SongTitle = titleLabel.Text = compactTitle.Text = "选择歌曲";
                canvas.SongSubtitle = compactCanvas.SongSubtitle = detailLabel.Text = "曲库没有匹配项";
                deleteSongButton.Enabled = false; UpdatePlaybackUI();
                UpdateSourceUI();
                SetStatus("本地曲库没有匹配项，可搜索乐谱或导入 MIDI / 简谱。", false);
            }
        }
        private void DrawSong(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            SongEntry item = (SongEntry)songs.Items[e.Index];
            bool active = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(active ? Theme.Input : Theme.Panel)) e.Graphics.FillRectangle(b, e.Bounds);
            if (active) using (var b = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(b, e.Bounds.X, e.Bounds.Y + 12, 3, 36);
            using (var title = Theme.PixelFont(10, FontStyle.Bold))
            using (var subtitle = Theme.PixelFont(8))
            using (var b = new SolidBrush(active ? Theme.Accent : Theme.Text))
            using (var m = new SolidBrush(Theme.Muted))
            {
                TextRenderer.DrawText(e.Graphics, item.Title, title, new Rectangle(e.Bounds.X + 12, e.Bounds.Y + 10, e.Bounds.Width - 20, 23), active ? Theme.Accent : Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(e.Graphics, item.Subtitle ?? "我的乐谱", subtitle, new Rectangle(e.Bounds.X + 12, e.Bounds.Y + 34, e.Bounds.Width - 20, 19), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }

        private void DeleteSelectedSong()
        {
            SongEntry entry = songs.SelectedItem as SongEntry;
            if (entry == null) return;
            StopPlayback(); BeginPlaybackOperation();
            try
            {
                using (var dialog = new LibraryRemovalDialog(entry))
                    if (dialog.ShowDialog(this) == DialogResult.OK) DeleteLibraryEntry(entry, dialog.RemoveFiles);
            }
            catch (Exception error) { SetStatus("删除未完成：" + error.Message, true); }
            finally { EndPlaybackOperation(); }
        }
        private void DeleteLibraryEntry(SongEntry entry, bool removeFiles)
        {
            int index = library.IndexOf(entry);
            if (index < 0) return;
            StopPlayback();
            var remaining = library.Where(item => !Object.ReferenceEquals(item, entry)).ToList();
            List<string> paths = removeFiles ? SongFileLibrary.RemovalPaths(dataDirectory, entry, remaining) : new List<string>();
            var oldHidden = new List<string>(settings.HiddenBuiltinTitles);
            library.RemoveAt(index);
            if (entry.Builtin && !settings.HiddenBuiltinTitles.Contains(entry.Title)) settings.HiddenBuiltinTitles.Add(entry.Title);
            try { if (entry.Builtin) settings.Save(settingsPath); else SaveLibrary(); }
            catch
            {
                library.Insert(index, entry); settings.HiddenBuiltinTitles = oldHidden;
                FilterLibrary(); throw;
            }
            var retained = new List<string>();
            foreach (string path in paths)
            {
                try
                {
                    if (SongFileLibrary.RemovalPaths(dataDirectory, entry, remaining).Contains(path)) File.Delete(path);
                }
                catch (IOException) { retained.Add(Path.GetFileName(path)); }
                catch (UnauthorizedAccessException) { retained.Add(Path.GetFileName(path)); }
                catch (InvalidDataException) { retained.Add(Path.GetFileName(path)); }
            }
            search.Text = ""; FilterLibrary(); songs.Refresh();
            SetStatus("已从曲库删除「" + entry.Title + "」。" + (retained.Count > 0 ? " " + retained.Count + " 个曲目文件仍被占用或无法删除，已保留。" : removeFiles ? " 独占的曲目文件已删除。" : " 本机文件保留。"), retained.Count > 0);
        }
        private void RestoreBuiltinSongs()
        {
            if (settings.HiddenBuiltinTitles.Count == 0) return;
            var hidden = new List<string>(settings.HiddenBuiltinTitles);
            settings.HiddenBuiltinTitles.Clear();
            try { settings.Save(settingsPath); }
            catch { settings.HiddenBuiltinTitles = hidden; throw; }
            foreach (LibrarySong song in ScoreIO.Builtins())
                if (!library.Any(item => item.Builtin && item.Title == song.Title))
                    library.Add(new SongEntry { Title = song.Title, Subtitle = song.Subtitle, Jianpu = song.Jianpu, Bpm = song.Bpm, Builtin = true, Chinese = song.Chinese });
            SongFileLibrary.Synchronize(dataDirectory, library);
            search.Text = ""; FilterLibrary(); SetStatus("内置曲目已恢复。", false);
        }
        private void LoadSong(SongEntry entry)
        {
            if (selected == entry && score != null) return;
            StopPlayback();
            try
            {
                SongScore next;
                if (!String.IsNullOrEmpty(entry.FileName))
                {
                    if (!ValidLibraryFile(entry.FileName)) throw new InvalidDataException("曲库文件路径无效。");
                    next = ScoreIO.LoadMidi(SongFileLibrary.ResolveMidi(dataDirectory, entry.FileName));
                }
                else
                {
                    next = new SongScore { Title = entry.Title };
                    next.Tracks.Add(ScoreIO.ParseJianpu(entry.Jianpu, entry.Bpm, 60));
                }
                if (next.Tracks.Count == 0) throw new InvalidDataException("乐谱没有可用的旋律声轨。");
                int preferred = MelodyPreparation.PreferredTrack(next.Tracks);
                if (preferred < 0) throw new InvalidDataException("这份 MIDI 没有可选的单音主旋律，可能只有伴奏、鼓点或复音编配。请查找主旋律谱。");
                selected = entry; score = next;
                rangeStart = rangeEnd = targetSeconds = 0; repeatRange = false;
                loading = true; tracks.Items.Clear();
                foreach (ScoreTrack track in score.Tracks)
                    if (track.Notes.Count > 0 && MelodyPreparation.IsPrimaryRole(track)) tracks.Items.Add(track);
                if (tracks.Items.Count == 0) tracks.Items.Add(score.Tracks[preferred]);
                tracks.SelectedItem = score.Tracks[preferred];
                if (tracks.SelectedIndex < 0) tracks.SelectedIndex = 0;
                tracks.Enabled = tracks.Items.Count > 1; loading = false;
                titleLabel.Text = compactTitle.Text = entry.Title;
                canvas.SongTitle = compactCanvas.SongTitle = entry.Title;
                canvas.SongSubtitle = compactCanvas.SongSubtitle = entry.Subtitle ?? "";
                UpdateSourceUI();
                if (!RebuildNotes()) return;
                string warning = score.Tracks.Any(t => (t.Name ?? "").Contains("短音较多") || (t.Name ?? "").Contains("伴奏难区分") || (t.Name ?? "").Contains("旋律依据弱") || (t.Name ?? "").Contains("低音可能干扰") || (t.Name ?? "").Contains("短音未定") || (t.Name ?? "").Contains("候选需核对"))
                    ? " 转录质量提示见声轨名称；可能仍有断音或伴奏，请先试听。" : " 可先试听确认。";
                bool estimated = (entry.SourceEdition ?? "").Contains("自动转录") || score.Tracks.Any(t => (t.Name ?? "").Contains("音频自动转录"));
                bool uncertain = !MelodyPreparation.IsPrimaryRole((ScoreTrack)tracks.SelectedItem);
                SetStatus("已载入「" + entry.Title + "」· " + (estimated ? "这是音频旋律估计，可右键查找已有主旋律谱。" : uncertain ? "乐谱未标明主旋律，已选择单音旋律候选，请先试听。" : "已选择主旋律，其他声轨仅在瀑布显示。") + tailMessage + warning,
                    estimated || uncertain || notes.Count == 0 || warning.Contains("质量提示"));
            }
            catch (Exception ex)
            {
                loading = true; tracks.Items.Clear(); loading = false;
                selected = null; score = null; notes = new List<NoteEvent>();
                noteDuration = sourceDuration = rangeStart = rangeEnd = targetSeconds = 0;
                repeatRange = false; tailMessage = ""; tracks.Enabled = false;
                canvas.Notes = compactCanvas.Notes = notes;
                canvas.BackgroundTracks = compactCanvas.BackgroundTracks = null;
                titleLabel.Text = compactTitle.Text = "乐谱载入失败";
                detailLabel.Text = "请检查文件，或重新导入对应乐谱";
                canvas.SongTitle = compactCanvas.SongTitle = titleLabel.Text;
                canvas.SongSubtitle = compactCanvas.SongSubtitle = detailLabel.Text;
                UpdateSourceUI();
                UpdatePlaybackUI(); SetStatus("载入失败：" + ex.Message, true);
            }
        }
        private bool RebuildNotes()
        {
            if (tracks.SelectedItem == null) return false;
            StopPlayback();
            PreparedMelody prepared;
            bool inferred = selected != null && (selected.SourceEdition ?? "").Contains("Basic Pitch 自动转录") || (((ScoreTrack)tracks.SelectedItem).Name ?? "").Contains("音频自动转录");
            try { prepared = MelodyPreparation.Build((ScoreTrack)tracks.SelectedItem, (int)transposeInput.Value, inferred); notes = prepared.Notes; }
            catch (Exception ex)
            {
                notes = new List<NoteEvent>(); noteDuration = 0; canvas.Notes = compactCanvas.Notes = notes;
                sourceDuration = rangeStart = rangeEnd = targetSeconds = 0; repeatRange = false; tailMessage = "";
                canvas.BackgroundTracks = compactCanvas.BackgroundTracks = null;
                detailLabel.Text = "此移调不可用，请调整移调数值";
                canvas.SongSubtitle = compactCanvas.SongSubtitle = detailLabel.Text;
                UpdatePlaybackUI(); SetStatus(ex.Message, true); return false;
            }
            PitchAdaptationResult adapted = PitchAdaptationPolicy.Apply(notes, settings.BasePitch, settings.AdaptNearbyPitch, settings.Transpose == 0);
            notes = adapted.Notes;
            double origin = prepared.SourceOrigin;
            var displayTracks = new List<ScoreTrack>();
            foreach (ScoreTrack track in score.Tracks)
                displayTracks.Add(new ScoreTrack { Name = track.Name, Index = track.Index,
                    Notes = track.Notes.Select(n => new NoteEvent { Pitch = n.Pitch + settings.Transpose,
                        Start = n.Start - origin, Duration = n.Duration, Velocity = n.Velocity }).OrderBy(n => n.Start).ToList() });
            sourceDuration = notes.Count == 0 ? 0 : notes.Max(n => n.Start + n.Duration);
            tailMessage = "";
            foreach (ScoreTrack display in displayTracks)
                if (display.Index == ((ScoreTrack)tracks.SelectedItem).Index) display.Notes = notes.Select(n => new NoteEvent { Pitch = n.Pitch, Start = n.Start, Duration = n.Duration, Velocity = n.Velocity }).ToList();
            double effectiveEnd = rangeEnd <= 0 ? sourceDuration : Math.Min(rangeEnd, sourceDuration);
            string planError = null;
            try
            {
                PlaybackPlan plan = PlaybackPlan.Build(notes, displayTracks, Math.Min(rangeStart, effectiveEnd), effectiveEnd, targetSeconds, repeatRange, settings.Speed,
                    new ScoreTailOptions { Enabled = settings.TrimInvalidTail, BasePitch = settings.BasePitch,
                        IsAudioTranscription = inferred, PreserveExplicitRange = rangeStart > 0 || rangeEnd > 0 || targetSeconds > 0 || repeatRange });
                notes = plan.Notes; displayTracks = plan.BackgroundTracks; noteDuration = plan.Duration;
                if (plan.TailDecision != null && plan.TailDecision.Applied) tailMessage = " " + plan.TailDecision.Message;
                if (plan.TailDecision != null && !plan.TailDecision.HasPlayableNotes)
                {
                    // Keep the source track available for a different transpose,
                    // but never run a silent timeline as if notes were being played.
                    notes = new List<NoteEvent>(); noteDuration = 0;
                    tailMessage = " 当前旋律没有可吹奏的音符，请查找主旋律谱或调整移调。";
                }
            }
            catch (Exception ex)
            {
                notes = new List<NoteEvent>(); displayTracks.Clear(); noteDuration = 0;
                planError = "片段设置不可用：" + ex.Message + "，请重新设置时长。";
            }
            canvas.Notes = compactCanvas.Notes = notes;
            canvas.BackgroundTracks = compactCanvas.BackgroundTracks = displayTracks;
            canvas.SelectedTrackIndex = compactCanvas.SelectedTrackIndex = ((ScoreTrack)tracks.SelectedItem).Index;
            int outside = notes.Count(n => !HarmonicaMap.Map(n.Pitch, settings.BasePitch).Playable);
            detailLabel.Text = notes.Count + " 个音符  ·  " + FormatTime(Duration / settings.Speed) + "  ·  " + score.Tracks.Count + " 个声轨" + (targetSeconds > 0 ? "  ·  自定时长" : rangeEnd > 0 ? "  ·  片段" : "") + (outside > 0 ? "  ·  " + outside + " 个超出音域" : "");
            if (adapted.WholeSongShift != 0) detailLabel.Text += "  ·  自动移调 " + (adapted.WholeSongShift > 0 ? "+" : "") + adapted.WholeSongShift;
            if (adapted.AdaptedCount > 0) detailLabel.Text += "  ·  " + adapted.AdaptedCount + " 个邻音适配";
            if (settings.Transpose != 0) detailLabel.Text += "  ·  手动移调 " + (settings.Transpose > 0 ? "+" : "") + settings.Transpose;
            if (prepared.SuppressedOverlaps > 0 || prepared.JoinedMicroGaps > 0) detailLabel.Text += "  ·  旋律连贯整理";
            if (tailMessage.Length > 0 && notes.Count > 0) detailLabel.Text += "  ·  尾部已截短";
            string tempo = selected != null && String.IsNullOrEmpty(selected.FileName) && selected.Bpm > 0
                ? "  ·  " + selected.Bpm.ToString("0.#", CultureInfo.InvariantCulture) + " BPM" : "";
            string role = MelodyPreparation.IsPrimaryRole((ScoreTrack)tracks.SelectedItem) ? "主旋律" : inferred ? "旋律估计" : "旋律候选";
            canvas.SongSubtitle = compactCanvas.SongSubtitle = role + " · " + ((ScoreTrack)tracks.SelectedItem).Name + "  ·  " + detailLabel.Text + tempo;
            if (outside > 0) SetStatus("有 " + outside + " 个音仍无法适配口琴音域，将略过；可更换旋律声轨或调整移调。", true);
            else if (tailMessage.Length > 0) SetStatus(tailMessage.Trim(), notes.Count == 0);
            UpdatePlaybackUI();
            if (planError != null) SetStatus(planError, true);
            return planError == null;
        }
        private double Duration { get { return noteDuration; } }
        private double Position { get { return Math.Min(Duration, autoActive ? autoPlayer.Position : offset + (playing ? playbackClock.Elapsed.TotalSeconds * settings.Speed : 0)); } }
        private static string FormatTime(double seconds)
        {
            var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return ((int)time.TotalMinutes).ToString("00") + ":" + time.Seconds.ToString("00");
        }
        private void TogglePlayback()
        {
            if (notes.Count == 0) { SetStatus("请先选择有音符的乐谱。", true); return; }
            if (listening) { ToggleManualPlayback(); return; }
            if (autoActive)
            {
                if (autoPlayer.Running)
                {
                    autoPlayer.TogglePause(); playing = !autoPlayer.Paused && autoPlayer.Running;
                    if (autoPlayer.CountingDown)
                        SetStatus(autoPlayer.Paused ? "倒计时已暂停 · " + settings.StartHotkey + " 继续"
                            : "倒计时继续 · " + settings.StartHotkey + " 可暂停", false);
                    UpdatePlaybackUI(); return;
                }
                offset = autoPlayer.Position; autoActive = playing = false;
            }
            if (settings.AutoMode) { StartAutomatic(); return; }
            ToggleManualPlayback();
        }
        private void ToggleManualPlayback()
        {
            if (playing)
            {
                offset = Position; playing = false; playbackClock.Stop(); audio.Stop();
                SetStatus("已暂停 · " + settings.StartHotkey + " 继续", false);
            }
            else
            {
                if (offset >= Duration) offset = 0;
                playing = true; playbackClock.Restart();
                if (listening) StartAudio();
                SetStatus(listening ? "正在本机试听 · 可手动跟奏" : "正在滚动乐谱 · 按提示手动演奏", false);
            }
            UpdatePlaybackUI();
        }
        private void StartPlayback()
        {
            TogglePlayback();
        }
        private void StartAutomatic()
        {
            if (!stopHotkeyAvailable)
            {
                SetStatus("停止热键 " + settings.StopHotkey + " 不可用，请先在热键设置中更换。", true); return;
            }
            StopPlayback();
            try
            {
                autoActive = true; playing = true;
                string guardFailure;
                if (!autoWindowGuard.TrySetEnabled(true, out guardFailure)) throw new InvalidOperationException(guardFailure);
                WritePlaybackDiagnostic("start", "自动吹奏 · 倒计时 " + settings.CountdownSeconds + " 秒 · " + WindowsInputAccess.OwnDescription());
                autoPlayer.Start(notes, settings.Speed, settings.BasePitch, settings.CountdownSeconds, Duration);
                SetStatus("倒计时 " + settings.CountdownSeconds + " 秒：切回游戏、装备口琴，保持游戏在前台。", true);
                UpdatePlaybackUI();
            }
            catch (Exception ex) { autoPlayer.Stop(); autoActive = false; playing = false; SetStatus("无法开始自动吹奏：" + ex.Message, true); UpdatePlaybackUI(); }
        }
        private void ToggleListen()
        {
            if (notes.Count == 0) { SetStatus("请先选择有音符的乐谱。", true); return; }
            if (autoActive) StopPlayback();
            if (listening) { listening = false; audio.Stop(); }
            else
            {
                listening = true;
                if (!playing) { if (offset >= Duration) offset = 0; playing = true; playbackClock.Restart(); }
                StartAudio();
            }
            UpdatePlaybackUI();
        }
        private void StartAudio()
        {
            try
            {
                PlaybackPlan output = PlaybackPlan.PreparePlayableOutput(notes, Duration, settings.BasePitch);
                audio.Play(output.Notes, Position, settings.Speed, output.Duration);
            }
            catch (Exception ex) { listening = false; SetStatus("试听失败：" + ex.Message, true); }
        }
        private void StopPlayback()
        {
            autoActive = false; autoPlayer.Stop();
            if (canvas != null && !String.IsNullOrEmpty(canvas.CountdownText)) SetStatus("已停止", false);
            playing = listening = false; offset = 0; playbackClock.Reset(); audio.Stop(); UpdatePlaybackUI();
        }
        private void SeekTo(double position)
        {
            if (autoActive) { SetStatus("自动吹奏中暂不支持跳转，请先停止再试听定位。", true); return; }
            offset = Math.Max(0, Math.Min(Duration, position)); playbackClock.Restart();
            if (listening && playing) StartAudio();
            UpdatePlaybackUI();
        }
        private void TickPlayback()
        {
            hotkeyController.Poll();
            if (autoActive)
            {
                // StateChanged is marshalled asynchronously; the timer also
                // reconciles completion while hidden or after a delayed UI callback.
                if (!autoPlayer.Running)
                {
                    CompleteAutomaticPlayback(); return;
                }
                UpdatePlaybackUI(); return;
            }
            if (!playing) return;
            if (Position >= Duration)
            {
                offset = Duration; playing = false; listening = false; playbackClock.Stop(); audio.Stop();
                SetStatus("本曲播放结束 · " + settings.StartHotkey + " 可从头开始", false);
            }
            UpdatePlaybackUI();
        }
        private void UpdatePlaybackUI()
        {
            if (canvas == null || compactCanvas == null) return;
            if (autoWindowGuard != null)
            {
                string reason;
                if (!autoWindowGuard.TrySetEnabled(autoActive || autoPlayer.Running, out reason))
                { autoPlayer.Stop(); autoActive = playing = false; string ignored; autoWindowGuard.TrySetEnabled(false, out ignored); SetStatus("窗口保护未准备好：" + reason, true); }
            }
            canvas.TimelineDuration = compactCanvas.TimelineDuration = Duration;
            canvas.Position = compactCanvas.Position = Position;
            canvas.Automatic = compactCanvas.Automatic = settings.AutoMode && !listening;
            if (Visible && WindowState != FormWindowState.Minimized)
                (compact ? compactCanvas : canvas).Invalidate();
            if (selected != null) compactTitle.Text = selected.Title + " · " + (listening ? "试听" : settings.AutoMode ? "自动" : "跟谱");
            timeLabel.Text = FormatTime(Position / settings.Speed) + " / " + FormatTime(Duration / settings.Speed);
            string action = listening ? "试听" : settings.AutoMode ? "吹奏" : "跟谱";
            bool resumable = (autoActive && autoPlayer.Paused) || (offset > 0 && offset < Duration);
            playButton.Text = playing ? "Ⅱ 暂停" + action : "▶ " + (resumable ? "继续" : "开始") + action;
            compactPlayButton.Text = playing ? "Ⅱ 暂停" : "▶ " + (resumable ? "继续" : "开始") + action;
            listenButton.Text = listening ? "♫ 关闭试听" : "♫ 离线试听";
        }
        private void SetStatus(string message, bool warning)
        {
            if (statusLabel == null) return;
            bool countdown = message != null && message.StartsWith("倒计时", StringComparison.Ordinal);
            if (canvas != null && compactCanvas != null)
            {
                canvas.CountdownText = compactCanvas.CountdownText = countdown ? message : "";
                if (Visible && WindowState != FormWindowState.Minimized)
                    (compact ? compactCanvas : canvas).Invalidate();
            }
            statusLabel.Text = countdown ? (message.Contains("暂停") && !message.Contains("可暂停")
                ? "自动准备已暂停 · " + settings.StartHotkey + " 继续 / " + settings.StopHotkey + " 取消"
                : "准备自动吹奏 · 请装备口琴并切回游戏") : message;
            statusLabel.ForeColor = warning ? Theme.Warm : Theme.Muted;
        }
        private void ApplyReferenceLayout()
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            Size desired = FitLandscapeSize(new Size(1280, 620), area);
            settings.CompactWidth = desired.Width; settings.CompactHeight = desired.Height;
            if (!compact) SetCompact(true);
            else
            {
                bool frame = BeginFramePresentation();
                SuspendLayout();
                try { Size = desired; ClampToWorkArea(); }
                finally { ResumeLayout(true); EndFramePresentation(frame); }
            }
            opacitySlider.Value = 75;
            SaveSettings();
        }

        private void SetCompact(bool value)
        {
            if (compact == value) return;
            bool frame = BeginFramePresentation();
            SuspendLayout();
            try
            {
                if (value) fullSize = WindowState == FormWindowState.Minimized ? RestoreBounds.Size : Size;
                compact = value;
                body.Visible = !value; compactBody.Visible = value;
                Rectangle area = Screen.FromControl(this).WorkingArea;
                MinimumSize = LandscapeMinimum(value, area);
                Size = FitLandscapeSize(value ? new Size(settings.CompactWidth, settings.CompactHeight) : fullSize, area);
                sizeButton.Text = value ? "展开" : "迷你";
                UpdateLandscapeLayout(); ClampToWorkArea();
            }
            finally { ResumeLayout(true); EndFramePresentation(frame); }
            settings.Compact = value; SaveSettings();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                if (presentingFrame) parameters.ExStyle |= CompositedStyle;
                if (autoWindowGuard != null) parameters.ExStyle = autoWindowGuard.ApplyCreateStyle(parameters.ExStyle);
                return parameters;
            }
        }

        protected override bool ShowWithoutActivation
        { get { return preserveForegroundForWindowControl || (autoWindowGuard != null && autoWindowGuard.Enabled) || base.ShowWithoutActivation; } }

        public override Size MinimumSize
        {
            get { return minimumWindowSize; }
            set
            {
                if (value.Width < 0 || value.Height < 0) throw new ArgumentOutOfRangeException("value");
                if (minimumWindowSize == value) return;
                // Framework 4.x Form.MinimumSize uses SetWindowPos without
                // SWP_NOACTIVATE. Keep the virtual constraint used by Form's
                // WM_GETMINMAXINFO and Control's bounds validation without that call.
                minimumWindowSize = value;
                if (Width < value.Width || Height < value.Height)
                    Size = new Size(Math.Max(Width, value.Width), Math.Max(Height, value.Height));
                OnMinimumSizeChanged(EventArgs.Empty);
            }
        }

        private void RunWindowControlWithoutActivation(Action action)
        {
            bool previous = preserveForegroundForWindowControl;
            preserveForegroundForWindowControl = true;
            try
            {
                IntPtr foreground = NativeForegroundWindow();
                bool ownForeground = IsHandleCreated && foreground == Handle;
                if (!ownForeground)
                    ownForeground = OwnedWindows(this).Any(window => window.IsHandleCreated && window.Handle == foreground);
                // SetFocus(NULL) only clears this UI thread's stale focus. Otherwise
                // hiding a focused panel can focus its replacement and activate us.
                // No input queues are attached and the foreground thread is untouched.
                if (!ownForeground) SetFocus(IntPtr.Zero);
                action();
            }
            finally { preserveForegroundForWindowControl = previous; }
        }

        private void RestoreWindowState()
        {
            if (WindowState != FormWindowState.Minimized) return;
            if (preserveForegroundForWindowControl && IsHandleCreated) ShowWindow(Handle, 4);
            else WindowState = FormWindowState.Normal;
        }

        // Composite only a reveal or layout transition. Keeping this style throughout
        // playback would repaint every child window for each waterfall animation frame.
        private bool BeginFramePresentation()
        {
            if (presentingFrame || closing || IsDisposed) return false;
            presentingFrame = true;
            if (IsHandleCreated) SetWindowStyle(Handle, -20, GetWindowStyle(Handle, -20) | CompositedStyle);
            return true;
        }

        private void EndFramePresentation(bool owner)
        {
            if (!owner) return;
            try
            {
                if (IsHandleCreated && Visible && WindowState != FormWindowState.Minimized)
                {
                    UpdatePlaybackUI();
                    // Paint all child HWNDs before releasing the composited first frame.
                    // RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW.
                    RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0185);
                }
            }
            finally
            {
                if (IsHandleCreated) SetWindowStyle(Handle, -20, GetWindowStyle(Handle, -20) & ~CompositedStyle);
                presentingFrame = false;
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!value || Visible || header == null) { base.SetVisibleCore(value); return; }
            bool frame = BeginFramePresentation();
            try
            {
                RestoreWindowState();
                base.SetVisibleCore(true);
            }
            finally { EndFramePresentation(frame); }
        }

        private void ToggleWindowVisibility()
        {
            if (applicationCollapsed || !Visible || WindowState == FormWindowState.Minimized) RestoreApplicationWindows();
            else CollapseApplicationWindows();
        }
        private IEnumerable<Form> OwnedWindows(Form owner)
        {
            foreach (Form child in owner.OwnedForms)
            {
                if (child.IsDisposed) continue;
                yield return child;
                foreach (Form descendant in OwnedWindows(child)) yield return descendant;
            }
        }
        private void CollapseApplicationWindows()
        {
            if (closing || IsDisposed || applicationCollapsed) return;
            hiddenOwnedWindows.Clear();
            foreach (Form window in OwnedWindows(this).Where(f => f.Visible && f.IsHandleCreated))
                hiddenOwnedWindows.Add(new OwnedWindowVisibility(window));
            applicationCollapsed = true;
            foreach (OwnedWindowVisibility window in hiddenOwnedWindows) window.HideNative();
            // Keep the visible top-level HWND and its taskbar entry. Unlike Hide(),
            // SW_SHOWMINNOACTIVE minimizes without activating the application.
            if (IsHandleCreated) ShowWindow(Handle, 7);
            else WindowState = FormWindowState.Minimized;
            if (desktopIntegrationEnabled) trayIcon.Visible = true;
            UpdateTrayState();
        }
        private void RestoreApplicationWindows()
        {
            if (closing || IsDisposed || restoringApplicationWindows) return;
            restoringApplicationWindows = true;
            bool frame = BeginFramePresentation();
            try
            {
                foreach (OwnedWindowVisibility window in hiddenOwnedWindows) window.PrepareRestore();
                RestoreWindowState();
                if (!Visible) Show();
                foreach (OwnedWindowVisibility window in hiddenOwnedWindows) window.RestoreNative();
                Form active = hiddenOwnedWindows.Select(w => w.Window).LastOrDefault(f => !f.IsDisposed && f.Visible);
                foreach (OwnedWindowVisibility window in hiddenOwnedWindows) window.Dispose();
                hiddenOwnedWindows.Clear(); applicationCollapsed = false;
                if (!preserveForegroundForWindowControl)
                { if (active != null) active.Activate(); else Activate(); }
                UpdateTrayState();
            }
            finally { restoringApplicationWindows = false; EndFramePresentation(frame); }
        }
        private static Size LandscapeMinimum(bool mini, Rectangle area)
        {
            return FitLandscapeSize(mini ? new Size(900, 390) : new Size(1032, 620), area);
        }
        private static Size FitLandscapeSize(Size desired, Rectangle area)
        {
            return new Size(Math.Min(desired.Width, Math.Max(320, area.Width - 24)),
                Math.Min(desired.Height, Math.Max(200, area.Height - 24)));
        }
        private void ClampToWorkArea()
        {
            if (WindowState != FormWindowState.Normal) return;
            Rectangle area = Screen.FromControl(this).WorkingArea;
            Size fitted = FitLandscapeSize(Size, area);
            if (fitted != Size) Size = fitted;
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)),
                Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        }
        private void UpdateLandscapeLayout()
        {
            if (upperSection == null || body == null) return;
            upperSection.Height = 244;
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateLandscapeLayout();
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (initialized && !closing && (desktopIntegrationEnabled || restoreHotkeysOnNewHandle)) RegisterControlHotkeys();
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            restoreHotkeysOnNewHandle = !closing && currentHotkeyResult != null;
            ReleaseControlHotkeys();
            base.OnHandleDestroyed(e);
        }
        private void SaveSettings()
        {
            try
            {
                Point position = WindowState == FormWindowState.Normal ? Location : RestoreBounds.Location;
                settings.X = position.X; settings.Y = position.Y;
                if (WindowState == FormWindowState.Normal)
                {
                    if (compact) { settings.CompactWidth = Width; settings.CompactHeight = Height; }
                    else { settings.FullWidth = Width; settings.FullHeight = Height; }
                }
                settings.Save(settingsPath);
            }
            catch (Exception ex) { SetStatus("设置未能保存：" + ex.Message, true); }
        }
        private void FindScoreOnline()
        {
            StopPlayback();
            BeginPlaybackOperation();
            string initial = search.Text.Trim();
            if (initial.Length == 0 && selected != null) initial = selected.Title;
            OpenScoreCatalog(initial, null);
        }
        private void FindMelodyScoreFromLibrary()
        {
            SongEntry entry = songs.SelectedItem as SongEntry;
            if (entry == null) return;
            StopPlayback(); BeginPlaybackOperation();
            OpenScoreCatalog(entry.Title, entry);
        }
        private void OpenScoreCatalog(string initial, SongEntry melodyRequest)
        {
            try
            {
                using (var dialog = new OnlineCatalogDialog(initial, library, ImportMidiForSong, AddJianpuForSong, ImportFreeScore, ConvertScoreToMidi, ImportChineseScore))
                {
                    dialog.ImportTranscribedAudio = delegate(string title, string path, byte[] bytes)
                    { return ImportTranscribedSong(title, path, bytes, dialog.ConversionSong); };
                    if (melodyRequest != null) dialog.PreferExistingMelody(melodyRequest.Title, KnownSongArtist(melodyRequest));
                    if (dialog.ShowDialog(this) == DialogResult.OK && dialog.ChosenScore != null)
                    { search.Text = ""; FilterLibrary(); songs.SelectedItem = dialog.ChosenScore; }
                }
            }
            finally { EndPlaybackOperation(); }
        }
        private static string KnownSongArtist(SongEntry entry)
        {
            if (entry.Artist != null) return entry.Artist.Trim();
            string edition = entry.SourceEdition ?? "";
            const string prefix = "POP909 钢琴编配 · ";
            if (edition.StartsWith(prefix, StringComparison.Ordinal)) return edition.Substring(prefix.Length).Trim();
            if (edition.Contains("自动转录")) return (entry.Attribution ?? "").Trim();
            return "";
        }
        private async void ImportMidi()
        {
            StopPlayback();
            BeginPlaybackOperation();
            try
            {
                string[] files;
                string title = null;
                using (var picker = new OpenFileDialog { Title = "选择 MIDI 乐谱（可多选批量导入，最多 100 个）", Filter = "MIDI 乐谱 (*.mid;*.midi)|*.mid;*.midi", Multiselect = true })
                {
                    if (picker.ShowDialog(this) != DialogResult.OK) return;
                    files = picker.FileNames;
                }
                if (files.Length > 100) { MessageBox.Show(this, "每次最多导入 100 个文件，请分批选择。", "批量导入"); return; }
                if (files.Length == 1)
                {
                    using (var nameDialog = new NameDialog("为乐谱命名", Path.GetFileNameWithoutExtension(files[0])))
                    {
                        if (nameDialog.ShowDialog(this) != DialogResult.OK) return;
                        title = nameDialog.SongName;
                    }
                }
                int added = 0, reused = 0;
                SongEntry last = null;
                var failed = new List<string>();
                Enabled = false; UseWaitCursor = true;
                for (int i = 0; i < files.Length; i++)
                {
                    if (closing || IsDisposed) break;
                    string path = files[i];
                    SetStatus("正在导入 " + (i + 1) + " / " + files.Length + "：" + Path.GetFileName(path), false);
                    try
                    {
                        SongScore parsed = await Task.Run(delegate { return ScoreIO.LoadMidi(path); });
                        if (closing || IsDisposed) break;
                        int previousCount = library.Count;
                        last = StoreMidi(path, title ?? Path.GetFileNameWithoutExtension(path), "", parsed);
                        if (library.Count > previousCount) added++; else reused++;
                    }
                    catch (Exception ex) { failed.Add(Path.GetFileName(path) + "：" + ex.Message); }
                }
                if (closing || IsDisposed) return;
                search.Text = ""; FilterLibrary(); if (last != null) songs.SelectedItem = last;
                SetStatus("导入完成：新增 " + added + " 首，已有 " + reused + " 首，失败 " + failed.Count + " 个。", failed.Count > 0);
                if (failed.Count > 0)
                    MessageBox.Show(this, String.Join("\n", failed.Take(12).ToArray()) + (failed.Count > 12 ? "\n另有 " + (failed.Count - 12) + " 个失败文件。" : ""), "部分乐谱未导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                if (!closing && !IsDisposed) { Enabled = true; UseWaitCursor = false; EndPlaybackOperation(); }
            }
        }
        private SongEntry StoreMidi(string sourcePath, string title, string artist, SongScore parsed, FreeScore origin = null)
        {
            if (parsed.Tracks.Count == 0) throw new InvalidDataException("此文件没有非打击乐旋律。");
            libraryStore.EnsureWritable();
            string hash;
            using (var algorithm = SHA256.Create())
            using (var input = File.OpenRead(sourcePath))
                hash = BitConverter.ToString(algorithm.ComputeHash(input)).Replace("-", "");
            SongEntry existing = library.FirstOrDefault(s => !String.IsNullOrEmpty(s.SourceHash) && s.SourceHash == hash);
            if (existing != null)
            {
                string oldAudio = existing.AudioFileName, oldUrl = existing.SourceUrl, oldArtist = existing.Artist;
                string oldLicense = existing.License, oldAttribution = existing.Attribution;
                string oldEdition = existing.SourceEdition, oldLicenseUrl = existing.LicenseUrl;
                string createdAudio = null;
                bool changed = false;
                try
                {
                    if (origin != null && !String.IsNullOrEmpty(origin.AudioFilePath) && String.IsNullOrEmpty(existing.AudioFileName))
                    {
                        createdAudio = NewAudioAttachmentPath(existing.FileName, origin.AudioFilePath);
                        existing.AudioFileName = SongFileLibrary.AttachAudio(dataDirectory, existing.FileName, origin.AudioFilePath);
                        changed = true;
                    }
                    if (origin != null && String.IsNullOrEmpty(existing.SourceUrl))
                    {
                        existing.SourceUrl = origin.InfoUrl; existing.License = origin.License;
                        existing.Attribution = origin.Attribution; existing.SourceEdition = origin.SourceEdition; existing.LicenseUrl = origin.LicenseUrl;
                        if (existing.Artist == null) existing.Artist = artist ?? "";
                        changed = true;
                    }
                    if (String.IsNullOrWhiteSpace(existing.Artist) && !String.IsNullOrWhiteSpace(artist))
                    { existing.Artist = artist.Trim(); changed = true; }
                    if (changed) SaveLibrary();
                }
                catch
                {
                    existing.AudioFileName = oldAudio; existing.SourceUrl = oldUrl; existing.Artist = oldArtist;
                    existing.License = oldLicense; existing.Attribution = oldAttribution;
                    existing.SourceEdition = oldEdition; existing.LicenseUrl = oldLicenseUrl;
                    RemoveFailedImportFile(createdAudio); throw;
                }
                return existing;
            }
            title = (title ?? "").Trim(); if (title.Length == 0) title = "未命名乐谱";
            if (title.Length > 80) title = title.Substring(0, 80);
            Directory.CreateDirectory(SongFileLibrary.DirectoryFor(dataDirectory));
            string file = SongFileLibrary.NewMidiFilename(dataDirectory, title, origin == null ? null : origin.AudioFilePath);
            string destination = Path.Combine(SongFileLibrary.DirectoryFor(dataDirectory), file);
            string createdAudioPath = origin == null ? null : NewAudioAttachmentPath(file, origin.AudioFilePath);
            bool ownsMidi = false;
            var entry = new SongEntry { Title = title, Subtitle = (origin == null ? "" : (origin.Provider ?? "Mutopia") + " · ") + (String.IsNullOrWhiteSpace(artist) ? "" : artist + " · ") + "MIDI · " + parsed.Tracks.Count + " 个声轨", FileName = file, SourceHash = hash, Bpm = 120, Chinese = origin != null && origin.Chinese,
                SourceUrl = origin == null ? null : origin.InfoUrl, License = origin == null ? null : origin.License, Attribution = origin == null ? null : origin.Attribution,
                SourceEdition = origin == null ? null : origin.SourceEdition, LicenseUrl = origin == null ? null : origin.LicenseUrl, Artist = artist };
            try
            {
                using (var input = File.OpenRead(sourcePath))
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { ownsMidi = true; input.CopyTo(output); output.Flush(true); }
                if (origin != null && !String.IsNullOrEmpty(origin.AudioFilePath))
                    entry.AudioFileName = SongFileLibrary.AttachAudio(dataDirectory, file, origin.AudioFilePath);
                library.Add(entry); SaveLibrary();
            }
            catch { library.Remove(entry); if (ownsMidi) RemoveFailedImportFile(destination); RemoveFailedImportFile(createdAudioPath); throw; }
            return entry;
        }
        private string NewAudioAttachmentPath(string midiFile, string audioPath)
        {
            if (String.IsNullOrEmpty(audioPath) || String.IsNullOrEmpty(midiFile)) return null;
            if (!ValidLibraryFile(midiFile) || !SongAudioDownload.IsSupported(audioPath)) return null;
            string path = Path.Combine(SongFileLibrary.DirectoryFor(dataDirectory), Path.GetFileNameWithoutExtension(midiFile) + Path.GetExtension(audioPath).ToLowerInvariant());
            return File.Exists(path) || String.Equals(Path.GetFullPath(path), Path.GetFullPath(audioPath), StringComparison.OrdinalIgnoreCase) ? null : path;
        }
        private static void RemoveFailedImportFile(string path)
        {
            if (String.IsNullOrEmpty(path)) return;
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        private SongEntry ImportFreeScore(FreeScore origin, byte[] bytes)
        {
            if (bytes == null || bytes.Length < 14 || bytes.Length > 16 * 1024 * 1024)
                throw new InvalidDataException("下载的 MIDI 文件为空或过大。");
            Directory.CreateDirectory(dataDirectory);
            string temporary = Path.Combine(dataDirectory, Guid.NewGuid().ToString("N") + ".mid.import");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                SongScore parsed = ScoreIO.LoadMidi(temporary);
                return StoreMidi(temporary, origin.Title, origin.Composer, parsed, origin);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private SongEntry ImportChineseScore(ChineseScore origin, byte[] bytes)
        {
            var source = new FreeScore { Title = origin.Title, Composer = origin.Artist, License = origin.License,
                InfoUrl = origin.SourceUrl, LicenseUrl = ChineseScoreCatalog.LicenseUrl, Attribution = origin.Attribution,
                SourceEdition = "POP909 钢琴编配 · " + origin.Artist, Provider = "POP909", Chinese = true };
            return ImportFreeScore(source, bytes);
        }
        private void AddScoreSource()
        {
            var menu = new ContextMenuStrip { BackColor = Theme.Panel, ForeColor = Theme.Text };
            menu.Items.Add("打开曲目文件夹", null, delegate
            {
                string folder = SongFileLibrary.DirectoryFor(dataDirectory); Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            });
            menu.Items.Add("新建文字简谱", null, delegate { AddJianpu(); });
            menu.Items.Add("歌曲音频 → MIDI", null, delegate
            {
                StopPlayback();
                BeginPlaybackOperation();
                try
                {
                    SongEntry entry = ImportAudioForSong(search.Text.Trim(), "");
                    if (entry != null) { search.Text = ""; FilterLibrary(); songs.SelectedItem = entry; }
                }
                finally { EndPlaybackOperation(); }
            });
            menu.Closed += delegate { BeginInvoke(new Action(delegate { menu.Dispose(); })); };
            menu.Show(Cursor.Position);
        }
        private SongEntry ImportAudioForSong(string title, string artist)
        {
            return ImportSongAudio(title, artist, null);
        }
        private SongEntry ImportDownloadedAudioForSong(OnlineSong song)
        {
            if (song == null) return null;
            return ImportSongAudio(song.Title, song.Artist, song);
        }
        private SongEntry ImportTranscribedSong(string title, string path, byte[] bytes, OnlineSong song)
        {
            string artist = song == null ? "" : song.Artist;
            return CompleteTranscription(new FreeScore
            {
                Title = title, Composer = artist, Provider = "音频自动转录",
                License = "用户取得音频 · 原作品权益保留", InfoUrl = song == null ? null : song.SourceUrl,
                SourceEdition = "Basic Pitch 自动转录 · 需试听确认", Attribution = artist,
                AudioFilePath = path, Chinese = title.Any(c => c >= '\u4e00' && c <= '\u9fff')
            }, bytes);
        }
        private SongEntry ImportRetranscribedSong(string title, string path, byte[] bytes, SongEntry original)
        {
            return CompleteTranscription(new FreeScore
            {
                Title = title, Composer = KnownSongArtist(original), Provider = "音频自动转录",
                License = original.License ?? "用户取得音频 · 原作品权益保留", LicenseUrl = original.LicenseUrl,
                InfoUrl = original.SourceUrl, Attribution = original.Attribution,
                SourceEdition = "Basic Pitch 自动转录 · 需试听确认", AudioFilePath = path,
                Chinese = original.Chinese || title.Any(c => c >= '\u4e00' && c <= '\u9fff')
            }, bytes);
        }
        private SongEntry CompleteTranscription(FreeScore origin, byte[] bytes)
        {
            SongEntry entry = ImportFreeScore(origin, bytes);
            if (entry != null) { search.Text = ""; FilterLibrary(); songs.SelectedItem = entry; songs.Refresh(); }
            return entry;
        }

        private SongEntry ImportSongAudio(string title, string artist, OnlineSong song)
        {
            using (var dialog = new AudioMidiDialog(title, song, settings.AudioDownloadDirectory,
                delegate(string folder) { settings.AudioDownloadDirectory = folder; SaveSettings(); }))
            {
                dialog.AutoDownload = song != null;
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.MidiBytes == null) return null;
                return ImportTranscribedSong(dialog.SongName, dialog.AudioFilePath, dialog.MidiBytes, song);
            }
        }
        private void ReconvertSelectedAudio()
        {
            SongEntry entry = songs.SelectedItem as SongEntry;
            if (entry == null || String.IsNullOrEmpty(entry.AudioFileName)) return;
            StopPlayback(); BeginPlaybackOperation();
            try
            {
                // Restrict the convenience action to an actual file from the managed library.
                if (Path.GetFileName(entry.AudioFileName) != entry.AudioFileName ||
                    entry.AudioFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    !SongAudioDownload.IsSupported(entry.AudioFileName)) throw new InvalidDataException("曲目音频路径无效。");
                string path = Path.Combine(SongFileLibrary.DirectoryFor(dataDirectory), entry.AudioFileName);
                if (!File.Exists(path)) throw new FileNotFoundException("配对音频不存在，请重新选择音频。");
                using (var dialog = new AudioMidiDialog(entry.Title + " · 连续旋律版"))
                {
                    dialog.InitialAudioPath = path; dialog.AutoConvertLocal = true;
                    if (dialog.ShowDialog(this) == DialogResult.OK && dialog.MidiBytes != null)
                        ImportRetranscribedSong(dialog.SongName, path, dialog.MidiBytes, entry);
                }
            }
            catch (Exception error) { SetStatus("重新转录未完成：" + error.Message, true); }
            finally { EndPlaybackOperation(); }
        }
        private SongEntry ConvertScoreToMidi(SongEntry source)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (!String.IsNullOrEmpty(source.FileName))
            {
                ScoreIO.LoadMidi(SongFileLibrary.ResolveMidi(dataDirectory, source.FileName));
                return source;
            }
            ScoreTrack original = ScoreIO.ParseJianpu(source.Jianpu, source.Bpm, 60);
            byte[] bytes = MidiWriter.ToBytes(original.Notes, source.Title);
            SongEntry result = ImportFreeScore(new FreeScore { Title = source.Title, Composer = "",
                License = source.License ?? (source.Builtin ? "传统主题 / 公版片段" : "自行导入的简谱"),
                InfoUrl = source.SourceUrl, LicenseUrl = source.LicenseUrl,
                Attribution = source.Attribution ?? source.Subtitle, SourceEdition = "根据已选简谱生成 MIDI · " + source.Bpm + " BPM",
                Provider = "简谱转 MIDI", Chinese = source.Chinese }, bytes);
            return result;
        }
        private void ExportMidi()
        {
            if (notes.Count == 0) { SetStatus("请先选择有音符的乐谱。", true); return; }
            StopPlayback();
            string fileTitle = selected == null ? "旋律" : selected.Title;
            foreach (char invalid in Path.GetInvalidFileNameChars()) fileTitle = fileTitle.Replace(invalid, '_');
            BeginPlaybackOperation();
            try
            {
                using (var picker = new SaveFileDialog { Title = "导出当前旋律、移调和速度为 MIDI", Filter = "MIDI 乐谱 (*.mid)|*.mid", FileName = fileTitle + ".mid", AddExtension = true, DefaultExt = "mid" })
                {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    SaveCurrentMidi(picker.FileName);
                    SetStatus("已导出真实 MIDI：" + Path.GetFileName(picker.FileName), false);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "MIDI 导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                }
            }
            finally { EndPlaybackOperation(); }
        }
        private void SaveCurrentMidi(string path)
        {
            PlaybackPlan output = PlaybackPlan.PreparePlayableOutput(notes, Duration, settings.BasePitch);
            foreach (NoteEvent note in output.Notes)
            {
                note.Start /= settings.Speed;
                note.Duration /= settings.Speed;
            }
            MidiWriter.Save(path, output.Notes, selected == null ? "口琴旋律" : selected.Title, output.Duration / settings.Speed);
        }
        private SongEntry ImportMidiForSong(string title, string artist)
        {
            using (var picker = new OpenFileDialog { Title = "选择「" + title + "」对应的 MIDI 乐谱", Filter = "MIDI 乐谱 (*.mid;*.midi)|*.mid;*.midi", Multiselect = false })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return null;
                try
                {
                    SongScore parsed = ScoreIO.LoadMidi(picker.FileName);
                    using (var dialog = new NameDialog("确认乐谱名称", title))
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                        return StoreMidi(picker.FileName, dialog.SongName, artist, parsed);
                    }
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
            }
        }
        private void AddJianpu()
        {
            SongEntry entry = AddJianpuForSong("", "");
            if (entry != null) { search.Text = ""; FilterLibrary(); songs.SelectedItem = entry; }
        }
        private SongEntry AddJianpuForSong(string title, string artist)
        {
            StopPlayback();
            using (var dialog = new JianpuDialog(title))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                var entry = new SongEntry { Title = dialog.SongName, Subtitle = (String.IsNullOrWhiteSpace(artist) ? "" : artist + " · ") + "我的简谱 · " + dialog.Bpm + " BPM", Jianpu = dialog.Notation, Bpm = dialog.Bpm };
                try
                {
                    ScoreIO.ParseJianpu(entry.Jianpu, entry.Bpm, 60);
                    library.Add(entry); SaveLibrary(); return entry;
                }
                catch (Exception ex) { library.Remove(entry); MessageBox.Show(this, ex.Message, "简谱未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
            }
        }
        private void OpenHelp()
        { OpenHelpDocument("main.html", ""); }

        private void OpenWaterfallHelp()
        { OpenHelpDocument("使用指南.html", "waterfall"); }

        private void OpenHelpDocument(string filename, string anchor)
        {
            string path = Path.Combine(Path.GetDirectoryName(AppPaths.HelpPath()), filename);
            if (!File.Exists(path)) { SetStatus("找不到文档中心，请保留完整软件文件夹。", true); return; }
            try
            {
                string target = String.IsNullOrEmpty(anchor) ? path : new Uri(path, UriKind.Absolute).AbsoluteUri + "#" + anchor;
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex) { SetStatus("无法打开说明：" + ex.Message, true); }
        }
        private void UpdateSourceUI()
        {
            if (sourceButton == null) return;
            sourceButton.Enabled = selected != null && !String.IsNullOrEmpty(selected.SourceUrl);
            licenseLabel.Text = selected == null ? "本地乐谱" : selected.Builtin ? "公版主题 / 传统旋律" : String.IsNullOrEmpty(selected.License) ? "自行导入的乐谱" : selected.License;
            sourceTip.SetToolTip(sourceButton, selected == null ? "" : (selected.Attribution ?? "") + (String.IsNullOrEmpty(selected.SourceEdition) ? "" : "\n" + selected.SourceEdition));
            sourceTip.SetToolTip(licenseLabel, selected == null ? "" : (selected.License ?? "") + "\n" + (selected.LicenseUrl ?? ""));
        }
        private void OpenSongSource()
        {
            if (selected == null || String.IsNullOrEmpty(selected.SourceUrl)) return;
            Uri uri;
            if (!Uri.TryCreate(selected.SourceUrl, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
            { SetStatus("乐谱来源网址无效。", true); return; }
            StopPlayback();
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus("无法打开乐谱来源：" + ex.Message, true); }
        }
        private void UpdateModeUI()
        {
            modeLabel.Text = settings.AutoMode ? "自动输入 · 可能违规或封号" : "手动跟谱 · 本地音符提示";
            modeLabel.ForeColor = settings.AutoMode ? Theme.Warm : Theme.Accent;
            canvas.Automatic = compactCanvas.Automatic = settings.AutoMode;
            UpdatePlaybackUI();
        }
        private void UpdateHotkeyHint()
        {
            hotkeyLabel.Text = settings.StartHotkey + " 开始 / 暂停 / 继续    " + settings.StopHotkey + " 停止    " + settings.ToggleWindowHotkey + " 显示 / 收起    " + settings.CompactHotkey + " 迷你";
            UpdateTrayState();
        }
        private void ReleaseControlHotkeys()
        {
            hotkeyController.Reset();
            if (IsHandleCreated && HotkeyReleaser != null) for (int id = 1; id <= 4; id++) HotkeyReleaser(id);
            stopHotkeyAvailable = hotkeysAvailable = false;
            if (currentHotkeyResult != null) for (int i = 0; i < currentHotkeyResult.Registered.Length; i++) currentHotkeyResult.Registered[i] = false;
        }
        private void RegisterControlHotkeys()
        {
            if (closing || IsDisposed) return;
            ReleaseControlHotkeys();
            string[] names = { settings.StartHotkey, settings.StopHotkey, settings.ToggleWindowHotkey, settings.CompactHotkey };
            HotkeyBindingResult result = HotkeyBindings.Register(names, HotkeyRegistrar);
            currentHotkeyResult = result;
            hotkeyController.Configure(result);
            hotkeysAvailable = result.Registered.All(value => value); stopHotkeyAvailable = result.Registered[1];
            WritePlaybackDiagnostic("register", String.Join(" / ", result.Names) + " · 已注册 " + result.Registered.Count(value => value) + " / 4");
            settings.StartHotkey = result.Names[0]; settings.StopHotkey = result.Names[1];
            settings.ToggleWindowHotkey = result.Names[2]; settings.CompactHotkey = result.Names[3];
            UpdateHotkeyHint();
            if (result.Changes.Count > 0)
            { SaveSettings(); SetStatus("已自动换用空闲热键：" + String.Join("，", result.Changes.ToArray()) + "。底栏显示当前按键。", false); }
            if (!hotkeysAvailable)
            {
                var failed = new List<string>();
                for (int i = 0; i < names.Length; i++) if (!result.Registered[i])
                    failed.Add(result.Names[i] + (result.Errors[i] == 1409 ? " 已被其他窗口注册" : " 系统注册失败（" + result.Errors[i] + "）"));
                SetStatus(String.Join("；", failed.ToArray()) + "。跟谱和试听仍可用窗口按钮。", true);
            }
        }
        private void ConfigurePlaybackRange()
        {
            if (sourceDuration <= 0) { SetStatus("请先载入包含音符的歌曲。", true); return; }
            StopPlayback();
            BeginPlaybackOperation();
            try
            {
                using (var dialog = new PlaybackRangeDialog(sourceDuration, rangeStart, rangeEnd, targetSeconds, repeatRange, settings.Speed))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    rangeStart = dialog.StartSeconds; rangeEnd = dialog.EndSeconds;
                    targetSeconds = dialog.TargetSeconds; repeatRange = dialog.Repeat;
                    RebuildNotes();
                    if (notes.Count > 0) SetStatus("播放时长已应用：" + FormatTime(Duration / settings.Speed) + (repeatRange && targetSeconds > 0 ? " · 循环片段" : ""), false);
                }
            }
            finally { EndPlaybackOperation(); }
        }
        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            ClampToWorkArea();
            if (!compact) fullSize = Size;
            if (settings != null && !closing) SaveSettings();
        }
        protected override void OnResizeBegin(EventArgs e)
        {
            base.OnResizeBegin(e);
            if (WindowState == FormWindowState.Normal && Height > 0) resizeAspect = Width / (double)Height;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SizingRectangle { public int Left, Top, Right, Bottom; }
        private void ConfigureHotkeys()
        {
            StopPlayback();
            BeginPlaybackOperation();
            bool saved = false;
            try
            {
                using (var dialog = new HotkeyDialog(settings))
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK) { SaveSettings(); saved = true; }
                }
            }
            finally { EndPlaybackOperation(); if (saved) RegisterControlHotkeys(); UpdateHotkeyHint(); }
            if (saved && hotkeysAvailable) SetStatus("热键与倒计时已保存。", false);
        }
        protected override void WndProc(ref Message m)
        {
            bool restoring = m.Msg == 0x0005 && m.WParam == IntPtr.Zero && minimizedFrame;
            if (m.Msg == 0x0005) minimizedFrame = m.WParam == (IntPtr)1;
            bool frame = restoring && BeginFramePresentation();
            try
            {
                if (m.Msg == 0x0214 && m.LParam != IntPtr.Zero && resizeAspect > 0)
                {
                    SizingRectangle native = (SizingRectangle)Marshal.PtrToStructure(m.LParam, typeof(SizingRectangle));
                    Rectangle area = Screen.FromControl(this).WorkingArea;
                    Rectangle fitted = AspectResize.Fit(Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom),
                        m.WParam.ToInt32(), resizeAspect, MinimumSize, new Size(Math.Max(320, area.Width - 24), Math.Max(200, area.Height - 24)));
                    native.Left = fitted.Left; native.Top = fitted.Top; native.Right = fitted.Right; native.Bottom = fitted.Bottom;
                    Marshal.StructureToPtr(native, m.LParam, false); m.Result = (IntPtr)1; return;
                }
                if (m.Msg == 0x0112 && (m.WParam.ToInt64() & 0xfff0) == 0xf120 && applicationCollapsed)
                {
                    RestoreApplicationWindows(); m.Result = IntPtr.Zero; return;
                }
                if (m.Msg == 0x0312)
                {
                    int id = m.WParam.ToInt32();
                    if (currentHotkeyResult != null && id >= 1 && id <= 4 &&
                        ((m.LParam.ToInt64() >> 16) & 0xffff) == (int)(Keys)Enum.Parse(typeof(Keys), currentHotkeyResult.Names[id - 1]))
                        hotkeyController.Receive(id, HotkeyMessageTick());
                }
                if (Program.ActivateMessage != 0 && (uint)m.Msg == Program.ActivateMessage)
                {
                    RestoreApplicationWindows();
                }
                base.WndProc(ref m);
                if (restoring && applicationCollapsed && !restoringApplicationWindows) RestoreApplicationWindows();
                if (m.Msg == 0x0084 && m.Result == (IntPtr)1 && (autoWindowGuard == null || !autoWindowGuard.Enabled))
                {
                    Point point = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xffff), (short)((m.LParam.ToInt64() >> 16) & 0xffff)));
                    bool left = point.X < 7, right = point.X >= ClientSize.Width - 7;
                    bool top = point.Y < 7, bottom = point.Y >= ClientSize.Height - 7;
                    int hit = top ? (left ? 13 : right ? 14 : 12) : bottom ? (left ? 16 : right ? 17 : 15) : left ? 10 : right ? 11 : 1;
                    m.Result = (IntPtr)hit;
                }
            }
            finally { EndFramePresentation(frame); }
        }
        private void ClosingForm(object sender, FormClosingEventArgs e)
        {
            closing = true;
            ReleaseControlHotkeys();
            SaveSettings();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !resourcesDisposed)
            {
                closing = true; ReleaseControlHotkeys(); resourcesDisposed = true;
                timer.Dispose(); audio.Dispose(); autoPlayer.Dispose(); sourceTip.Dispose();
                if (autoWindowGuard != null) autoWindowGuard.Dispose();
                if (songLibraryMenu != null) songLibraryMenu.Dispose();
                foreach (OwnedWindowVisibility window in hiddenOwnedWindows) window.Dispose(); hiddenOwnedWindows.Clear();
                foreach (Form window in OwnedWindows(this).Reverse().ToArray()) if (!window.IsDisposed) window.Dispose();
                if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); }
                if (trayMenu != null) trayMenu.Dispose();
            }
            base.Dispose(disposing);
        }

        // ShowDialog exits when the managed Visible property becomes false.
        // Keep its modal loop alive while hiding only this application's HWNDs.
        private sealed class OwnedWindowVisibility : NativeWindow, IDisposable
        {
            internal readonly Form Window;
            private bool suppressHide = true, compositedBefore;
            internal OwnedWindowVisibility(Form window) { Window = window; AssignHandle(window.Handle); }
            internal void HideNative() { if (Handle != IntPtr.Zero) ShowWindow(Handle, 0); }
            internal void PrepareRestore()
            {
                if (Handle == IntPtr.Zero || Window.IsDisposed) return;
                int style = GetWindowStyle(Handle, -20); compositedBefore = (style & CompositedStyle) != 0;
                SetWindowStyle(Handle, -20, style | CompositedStyle);
            }
            internal void RestoreNative()
            {
                if (Handle == IntPtr.Zero || Window.IsDisposed) return;
                ShowWindow(Handle, 4);
                RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0185);
                if (!compositedBefore) SetWindowStyle(Handle, -20, GetWindowStyle(Handle, -20) & ~CompositedStyle);
            }
            protected override void WndProc(ref Message m)
            {
                if (suppressHide && m.Msg == 0x0018 && m.WParam == IntPtr.Zero) { m.Result = IntPtr.Zero; return; }
                base.WndProc(ref m);
            }
            public void Dispose() { suppressHide = false; if (Handle != IntPtr.Zero) ReleaseHandle(); }
        }
    }

    internal sealed class NameDialog : Form
    {
        private readonly TextBox name;
        public string SongName { get { return name.Text.Trim(); } }
        public NameDialog(string title, string initial)
        {
            Text = title; ClientSize = new Size(380, 148); StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            BackColor = Theme.Panel; ForeColor = Theme.Text; Font = Theme.Font(10);
            Controls.Add(new Label { Text = "歌曲名称", Location = new Point(20, 17), AutoSize = true });
            name = new TextBox { Text = initial, Location = new Point(20, 44), Width = 336, BackColor = Theme.Input, ForeColor = Theme.Text, MaxLength = 80 };
            var save = new SoftButton("保存到曲库", true) { Location = new Point(239, 94), Width = 117 };
            save.Click += delegate { if (SongName.Length == 0) { name.Focus(); return; } DialogResult = DialogResult.OK; };
            Controls.Add(name); Controls.Add(save); AcceptButton = save;
        }
    }

    internal sealed class JianpuDialog : Form
    {
        private readonly TextBox name, notation;
        private readonly NumericUpDown bpm;
        public string SongName { get { return name.Text.Trim(); } }
        public string Notation { get { return notation.Text.Trim(); } }
        public double Bpm { get { return (double)bpm.Value; } }
        public JianpuDialog(string initialTitle = "")
        {
            Text = "新建歌曲简谱"; ClientSize = new Size(590, 404); StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            BackColor = Theme.Panel; ForeColor = Theme.Text; Font = Theme.Font(10);
            Controls.Add(new Label { Text = "歌名", Location = new Point(20, 20), AutoSize = true });
            name = new TextBox { Text = initialTitle.Length > 80 ? initialTitle.Substring(0, 80) : initialTitle, Location = new Point(75, 17), Width = 322, BackColor = Theme.Input, ForeColor = Theme.Text, MaxLength = 80 };
            Controls.Add(new Label { Text = "BPM", Location = new Point(415, 20), AutoSize = true });
            bpm = new NumericUpDown { Location = new Point(460, 17), Width = 106, Minimum = 30, Maximum = 240, Value = 100, BackColor = Theme.Input, ForeColor = Theme.Text };
            Controls.Add(new Label { Text = "音符用空格分隔，0 为休止，| 为小节线", Location = new Point(20, 64), AutoSize = true, ForeColor = Theme.Muted });
            notation = new TextBox { Location = new Point(20, 96), Size = new Size(546, 187), Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.Font(12), MaxLength = 100000 };
            Controls.Add(new Label { Text = "示例：1 1 5 5 | 6 6 5:2\n1' 高八度 · 1, 低八度 · #4 升半音 · 5:0.5 半拍", Location = new Point(20, 296), Size = new Size(546, 48), ForeColor = Theme.Muted, Font = Theme.Font(9) });
            var save = new SoftButton("校验并保存", true) { Location = new Point(435, 353), Width = 131 };
            save.Click += delegate
            {
                if (SongName.Length == 0) { MessageBox.Show(this, "请输入歌名。"); name.Focus(); return; }
                try { ScoreTrack track = ScoreIO.ParseJianpu(Notation, Bpm, 60); if (track.Notes.Count == 0) throw new InvalidDataException("请填写含有音符的简谱。"); DialogResult = DialogResult.OK; }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "简谱格式错误", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            Controls.AddRange(new Control[] { name, bpm, notation, save });
        }
    }
}
