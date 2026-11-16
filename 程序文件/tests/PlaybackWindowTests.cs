using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using HarmonicaStudio;

public static class PlaybackWindowTests
{
    private static int checks;
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);

    public static int Run()
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return RunOnSta();
        int result = 0; Exception failure = null;
        var thread = new Thread(delegate() { try { result = RunOnSta(); } catch (Exception error) { failure = error; } });
        thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(30000)) throw new Exception("播放范围与缩放界面验证超时。");
        if (failure != null) throw failure;
        return result;
    }

    private static int RunOnSta()
    {
        checks = 0;
        string directory = Path.Combine(AppPaths.ContentDirectory, "data");
        string settingsPath = Path.Combine(directory, "settings.json");
        bool hadDirectory = Directory.Exists(directory), hadSettings = File.Exists(settingsPath);
        byte[] original = hadSettings ? File.ReadAllBytes(settingsPath) : null;
        string separateSettings = Path.Combine(directory, "window-check-" + Guid.NewGuid().ToString("N") + ".json");
        MainForm form = null, reloaded = null;
        try
        {
            Directory.CreateDirectory(directory);
            SettingsCompatibility(separateSettings);
            File.WriteAllText(settingsPath, "{\"AutoMode\":false,\"Compact\":false,\"AlwaysOnTop\":false,\"Opacity\":1}");
            form = new MainForm();
            Field<System.Windows.Forms.Timer>(form, "timer").Stop();
            form.ShowInTaskbar = false; form.TopMost = false;
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(20, 20);
            // No Show means the Shown handler never registers global hotkeys.
            IntPtr unused = form.Handle;
            for (int i = 1; i <= 5; i++) UnregisterHotKey(form.Handle, i);
            PlannedMainForm(form);
            RangeDialog();
            ResizeAndPersistence(form, settingsPath);
            reloaded = new MainForm();
            Field<System.Windows.Forms.Timer>(reloaded, "timer").Stop();
            Check(reloaded.Size == new Size(1380, 790), "重开实际主窗恢复完整模式尺寸");
            Call(reloaded, "SetCompact", true); Layout(reloaded);
            Check(reloaded.Size == new Size(980, 420), "重开后切换迷你恢复独立迷你尺寸");
            Check(!Field<AutoPlayer>(form, "autoPlayer").Running && !Field<AutoPlayer>(reloaded, "autoPlayer").Running,
                "全部界面流程未启动自动输入后端");
            Console.WriteLine("播放范围与可调窗口测试通过，共 " + checks + " 项断言（窗口未显示、未发送输入）。");
            return checks;
        }
        finally
        {
            DisposeForm(reloaded); DisposeForm(form);
            if (File.Exists(separateSettings)) File.Delete(separateSettings);
            if (hadSettings) File.WriteAllBytes(settingsPath, original);
            else if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (!hadDirectory && Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
                Directory.Delete(directory);
        }
    }

    private static void PlannedMainForm(MainForm form)
    {
        List<LibrarySong> builtins = ScoreIO.Builtins();
        Check(builtins.Count == 26, "原有二十六首内置谱保留");
        Check(ScoreIO.MaximumNotes > 40 && builtins.Any(s => ScoreIO.ParseJianpu(s.Jianpu, s.Bpm, 60).Notes.Count == 40),
            "四十音符确为内置片段长度，非软件音符上限");
        var melody = new ScoreTrack { Name = "测试旋律", Index = 4,
            Notes = new List<NoteEvent> { N(60, 2, 4), N(62, 8, 2) } };
        var background = new ScoreTrack { Name = "跨起点伴奏", Index = 9,
            Notes = new List<NoteEvent> { N(48, 0, 6), N(65, 8, 5) } };
        SetFixture(form, new[] { melody, background });
        Set(form, "rangeStart", 1.0); Set(form, "rangeEnd", 7.0);
        Set(form, "targetSeconds", 10.0); Set(form, "repeatRange", true); Call(form, "RebuildNotes");
        var notes = Field<List<NoteEvent>>(form, "notes");
        ScoreCanvas full = Field<ScoreCanvas>(form, "canvas"), mini = Field<ScoreCanvas>(form, "compactCanvas");
        Check(notes.Count == 3 && notes[0].Pitch == 60, "实际MainForm应用片段后展开选中旋律");
        Near(notes[0].Start, 0, "主窗段前长音在片段零时刻进入");
        Near(notes[0].Duration, 3, "主窗起点裁切保留三秒长音");
        Near(notes[1].Start, 5, "主窗片段内休止没有被归零处理删除");
        Near(notes[2].Start, 6, "主窗第二遍使用六秒完整循环周期");
        Near(Value(form, "Duration"), 10, "主窗计划末音之后保留一秒尾静音");
        Near(full.TimelineDuration, 10, "完整谱进度条使用包含尾静音的计划时长");
        Near(mini.TimelineDuration, 10, "迷你谱进度条使用相同的计划时长");
        Check(Object.ReferenceEquals(notes, full.Notes) && Object.ReferenceEquals(notes, mini.Notes), "完整和迷你共用计划音符快照");
        Check(Object.ReferenceEquals(full.BackgroundTracks, mini.BackgroundTracks) && full.BackgroundTracks.Count == 2,
            "两种模式共用同一多声轨时间轴");
        ScoreTrack bass = full.BackgroundTracks[1];
        Check(bass.Name == "跨起点伴奏" && bass.Index == 9 && bass.Notes.Count == 3, "背景轨真实名称和稀疏来源轨号不丢失");
        Near(bass.Notes[0].Start, 0, "比旋律更早开始的背景长音也裁入同一起点");
        Near(bass.Notes[0].Duration, 3, "背景裁切与原谱相对位置一致");
        Near(bass.Notes[2].Start, notes[2].Start, "背景与旋律循环同步");
        Check(full.SelectedTrackIndex == 4 && mini.SelectedTrackIndex == 4, "两种画布仍选择真实来源旋律轨");
        Check(Field<Label>(form, "timeLabel").Text.EndsWith("00:10", StringComparison.Ordinal), "界面总时长显示实际十秒");
        Field<NumericUpDown>(form, "speedInput").Value = 1.5m;
        Near(Value(form, "Duration"), 15, "变速后计划自动重建为十五秒谱面");
        Near(Value(form, "Duration") / Field<AppSettings>(form, "settings").Speed, 10, "变速不改变指定实际总时长");
        Check(Field<List<NoteEvent>>(form, "notes").Count == 5 && full.BackgroundTracks[1].Notes.Count == 5,
            "变速重建时旋律和背景均追加同样循环");
        Near(Field<double>(form, "sourceDuration"), 8, "重建来源时长不会把上次循环结果当作原谱");
        Near(melody.Notes[0].Start, 2, "实际主窗播放范围不修改原始谱面");
        Field<CheckBox>(form, "autoModeCheck").Checked = true;
        Check(full.Automatic && mini.Automatic && Object.ReferenceEquals(full.BackgroundTracks, mini.BackgroundTracks),
            "切自动模式仍显示同一多轨瀑布，未启动自动演奏");
        Field<CheckBox>(form, "autoModeCheck").Checked = false;
        Field<NumericUpDown>(form, "speedInput").Value = 1.0m;
        Set(form, "rangeStart", 0.0); Set(form, "rangeEnd", 0.0); Set(form, "targetSeconds", 0.0); Set(form, "repeatRange", false);
        var longTrack = new ScoreTrack { Name = "八十音符测试旋律", Index = 6 };
        for (int i = 0; i < 80; i++) longTrack.Notes.Add(N(60 + (i % 2) * 2, i * 0.2, 0.2));
        SetFixture(form, new[] { longTrack });
        Check(Field<List<NoteEvent>>(form, "notes").Count == 80, "真实主窗载入八十音符，不受默认四十音符片段限制");
        Near(Value(form, "Duration"), 16, "长谱自然播放保留完整十六秒");
        Set(form, "rangeStart", 2.0); Set(form, "rangeEnd", 8.0); Set(form, "targetSeconds", 20.0); Set(form, "repeatRange", true);
        List<SongEntry> library = Field<List<SongEntry>>(form, "library");
        Call(form, "LoadSong", library.First(s => s.Builtin && !Object.ReferenceEquals(s, Field<SongEntry>(form, "selected"))));
        Check(Field<double>(form, "rangeStart") == 0 && Field<double>(form, "rangeEnd") == 0 &&
            Field<double>(form, "targetSeconds") == 0 && !Field<bool>(form, "repeatRange"), "切换歌曲清除前首专属区间，避免误套");
    }

    private static void RangeDialog()
    {
        using (var dialog = new PlaybackRangeDialog(120, 10, 30, 60, true, 1.5))
        {
            Check(dialog.StartSeconds == 10 && dialog.EndSeconds == 30 && dialog.TargetSeconds == 60 && dialog.Repeat,
                "范围窗口恢复已选片段和实际总时长");
            ChildrenInside(dialog); NoOverlap(dialog);
            Field<NumericUpDown>(dialog, "end").Value = 5;
            Click(dialog, "应用时长");
            Check(dialog.DialogResult == DialogResult.None && Field<Label>(dialog, "message").Text.Contains("终点"), "起点晚于终点不会应用错误范围");
            Field<NumericUpDown>(dialog, "end").Value = 30;
            Field<CheckBox>(dialog, "repeat").Checked = false;
            Click(dialog, "应用时长");
            Check(dialog.DialogResult == DialogResult.None && Field<Label>(dialog, "message").Text.Contains("勾选循环"), "长于片段必须明确选择循环");
            Click(dialog, "恢复整首");
            Check(dialog.StartSeconds == 0 && dialog.EndSeconds == 120 && dialog.TargetSeconds == 0 && !dialog.Repeat,
                "一键恢复整首清除所有片段与循环配置");
            Click(dialog, "应用时长");
            Check(dialog.DialogResult == DialogResult.OK, "自然一遍无需额外设置即可应用");
        }
        using (var slow = new PlaybackRangeDialog(120, 0, 0, 0, false, 0.5))
            Check(Field<NumericUpDown>(slow, "total").Maximum <= (decimal)ScoreIO.MaximumDurationSeconds,
                "半速窗口总时长最大值不能超出播放计划四小时实际上限");
    }

    private static void ResizeAndPersistence(MainForm form, string settingsPath)
    {
        foreach (bool compact in new[] { false, true })
        {
            Call(form, "SetCompact", compact);
            form.Size = compact ? new Size(800, 350) : new Size(1032, 620); Layout(form);
            Panel footer = Field<Panel>(form, "footer");
            Control grip = footer.Controls.Cast<Control>().Single(c => c is WindowResizeGrip);
            Check(grip.Right <= footer.ClientSize.Width && grip.Bottom <= footer.ClientSize.Height &&
                footer.ClientSize.Width - grip.Right <= 6, "两种模式右下角把手始终位于状态栏边缘");
            Check(grip.AccessibleName.Contains("调整窗口大小") && grip.Cursor == Cursors.SizeNWSE,
                "缩放把手有明确功能提示和光标");
            Check(!grip.Bounds.IntersectsWith(Field<Label>(form, "statusLabel").Bounds) &&
                !grip.Bounds.IntersectsWith(Field<Label>(form, "hotkeyLabel").Bounds), "缩放把手不遮挡状态与热键文字");
            ChildrenInside(footer); NoOverlap(footer);
            if (compact)
            {
                Panel compactBody = Field<Panel>(form, "compactBody");
                ChildrenInside(compactBody.Controls.Cast<Control>().Single(c => c is Panel));
                Check(Field<ScoreCanvas>(form, "compactCanvas").Width >= 760 && Field<ScoreCanvas>(form, "compactCanvas").Height >= 210,
                    "最小迷你窗口仍保留可读八列瀑布");
            }
            else
            {
                Control main = Field<Panel>(form, "content").Controls[0];
                ChildrenInside(main); NoOverlap(main);
                Check(main.Controls.Cast<Control>().Any(c => c.Text == "片段时长"), "最小完整窗口片段按钮仍完整可见");
                Check(Field<ScoreCanvas>(form, "canvas").Width >= 1000 && Field<ScoreCanvas>(form, "canvas").Height >= 270 &&
                    Field<ScoreCanvas>(form, "canvas").LayoutScale >= 0.72f,
                    "最小完整窗口控制区不挤占底部大瀑布");
            }
            HitTests(form);
        }
        Call(form, "SetCompact", false); form.Size = new Size(1380, 790); Call(form, "OnResizeEnd", EventArgs.Empty);
        Call(form, "SetCompact", true); form.Size = new Size(980, 420); Call(form, "OnResizeEnd", EventArgs.Empty);
        Call(form, "SetCompact", false); Layout(form);
        Check(form.Size == new Size(1380, 790), "迷你缩放后展开恢复先前完整尺寸");
        AppSettings saved = AppSettings.Load(settingsPath);
        Check(saved.FullWidth == 1380 && saved.FullHeight == 790 && saved.CompactWidth == 980 && saved.CompactHeight == 420,
            "实际窗口保存完整与迷你两套尺寸，互不覆盖");
    }

    private static void SettingsCompatibility(string path)
    {
        File.WriteAllText(path, "{\"Opacity\":0.9,\"Compact\":false}");
        AppSettings old = AppSettings.Load(path);
        Check(old.FullWidth == 1280 && old.FullHeight == 720 && old.CompactWidth == 900 && old.CompactHeight == 390,
            "旧JSON没有尺寸字段时兼容默认两种窗口大小");
        old.FullWidth = 1440; old.FullHeight = 810; old.CompactWidth = 1024; old.CompactHeight = 450; old.Save(path);
        AppSettings restored = AppSettings.Load(path);
        Check(restored.FullWidth == 1440 && restored.FullHeight == 810 && restored.CompactWidth == 1024 && restored.CompactHeight == 450,
            "独立设置保存和加载四个尺寸字段完整往返");
        File.WriteAllText(path, "{\"FullWidth\":100,\"FullHeight\":99999,\"CompactWidth\":50,\"CompactHeight\":1}");
        AppSettings bounded = AppSettings.Load(path);
        Check(bounded.FullWidth == 1032 && bounded.FullHeight == 4320 && bounded.CompactWidth == 900 && bounded.CompactHeight == 390,
            "错误尺寸配置按完整和迷你各自上下限修正");
    }

    private static void HitTests(MainForm form)
    {
        int width = form.ClientSize.Width, height = form.ClientSize.Height;
        Point[] points = { new Point(2, height / 2), new Point(width - 2, height / 2), new Point(width / 2, 2),
            new Point(width / 2, height - 2), new Point(2, 2), new Point(width - 2, 2), new Point(2, height - 2), new Point(width - 2, height - 2) };
        int[] expected = { 10, 11, 12, 15, 13, 14, 16, 17 };
        MethodInfo wndProc = typeof(MainForm).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic);
        for (int i = 0; i < points.Length; i++)
        {
            Point screen = form.PointToScreen(points[i]);
            int packed = unchecked((screen.Y & 0xffff) << 16) | (screen.X & 0xffff);
            Message message = Message.Create(form.Handle, 0x0084, IntPtr.Zero, new IntPtr(packed));
            object[] arguments = { message }; wndProc.Invoke(form, arguments);
            Check(((Message)arguments[0]).Result == (IntPtr)expected[i], "本窗口WM_NCHITTEST边界允许缩放方向 " + expected[i]);
        }
    }

    private static void SetFixture(MainForm form, IEnumerable<ScoreTrack> source)
    {
        var score = new SongScore { Title = "界面测试", Tracks = source.ToList() };
        Set(form, "score", score); Set(form, "loading", true);
        ComboBox tracks = Field<ComboBox>(form, "tracks"); tracks.Items.Clear();
        foreach (ScoreTrack track in score.Tracks) tracks.Items.Add(track);
        tracks.SelectedIndex = 0; Set(form, "loading", false); Call(form, "RebuildNotes");
    }
    private static void Click(Form form, string text)
    {
        Control button = form.Controls.Cast<Control>().Single(c => c.Text == text);
        typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(button, new object[] { EventArgs.Empty });
    }
    private static void DisposeForm(MainForm form)
    {
        if (form == null) return;
        if (form.IsHandleCreated) for (int i = 1; i <= 5; i++) UnregisterHotKey(form.Handle, i);
        Field<System.Windows.Forms.Timer>(form, "timer").Dispose(); Field<AudioPreview>(form, "audio").Dispose();
        Field<AutoPlayer>(form, "autoPlayer").Dispose(); Field<ToolTip>(form, "sourceTip").Dispose(); form.Dispose();
    }
    private static void ChildrenInside(Control parent)
    {
        foreach (Control child in parent.Controls)
            if (child.Left < 0 || child.Top < 0 || child.Right > parent.ClientSize.Width || child.Bottom > parent.ClientSize.Height)
                throw new Exception("控制项被最小窗口裁切：" + child.Text + " " + child.Bounds + " / " + parent.ClientSize);
        Check(true, "所有控制项均在父区域内部");
    }
    private static void NoOverlap(Control parent)
    {
        for (int i = 0; i < parent.Controls.Count; i++) for (int j = i + 1; j < parent.Controls.Count; j++)
            if (parent.Controls[i].Bounds.IntersectsWith(parent.Controls[j].Bounds))
                throw new Exception("控制项相互遮挡：" + parent.Controls[i].Text + " / " + parent.Controls[j].Text);
        Check(true, "区域内控制项没有重叠");
    }
    private static void Layout(Control control) { control.PerformLayout(); foreach (Control child in control.Controls) Layout(child); }
    private static NoteEvent N(int pitch, double start, double duration) { return new NoteEvent { Pitch = pitch, Start = start, Duration = duration, Velocity = 90 }; }
    private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
    private static void Call(object target, string name, params object[] values) { target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, values); }
    private static double Value(object target, string name) { return (double)target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target, null); }
    private static void Check(bool okay, string name) { checks++; if (!okay) throw new Exception("播放窗口测试失败：" + name); }
    private static void Near(double actual, double expected, string name) { Check(Math.Abs(actual - expected) < 0.0000001, name); }
}
