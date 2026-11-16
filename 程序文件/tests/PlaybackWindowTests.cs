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
            ArrangementMainForm(form);
            OrnamentMainForm(form, settingsPath);
            NaturalPassIgnoresUnusedSeam(form);
            TargetEndIgnoresUnplayedNotes(form);
            UnplayablePreviewDoesNotConsumeAudioLimit();
            ExportPlayableTimeline(form);
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

    private static void ArrangementMainForm(MainForm form)
    {
        Set(form, "rangeStart", 0.0); Set(form, "rangeEnd", 0.0); Set(form, "targetSeconds", 0.0); Set(form, "repeatRange", false);
        Field<NumericUpDown>(form, "speedInput").Value = 1.5m;
        var source = new ScoreTrack { Name = "MELODY", Index = 7,
            Notes = new List<NoteEvent> { N(60, 0, .06), N(60, .06, .06), N(61, .12, .12), N(62, .24, .12) } };
        SetFixture(form, new[] { source });
        List<NoteEvent> arranged = Field<List<NoteEvent>>(form, "notes");
        Check(arranged.Count == 4 && arranged[0].Duration < .06,
            "主窗必须为重复键真实预留收键间隔，瀑布不能继续显示原连奏音长");
        double effective = Value(form, "EffectiveSpeed");
        Check(effective >= .5 && effective < 1.5, "密集换指按整个片段统一降速至可奏区间");
        Near(Field<AppSettings>(form, "settings").Speed, 1.5, "自动降速不能覆盖用户请求速度");
        Check(Field<NumericUpDown>(form, "speedInput").Value == 1.5m, "用户速度输入仍显示请求值");
        ScoreCanvas full = Field<ScoreCanvas>(form, "canvas"), mini = Field<ScoreCanvas>(form, "compactCanvas");
        Check(Object.ReferenceEquals(arranged, full.Notes) && Object.ReferenceEquals(arranged, mini.Notes),
            "两种瀑布使用统一编配音符");
        Near(full.PlaybackSpeed, effective, "完整瀑布显示实际口琴速度");
        Near(mini.PlaybackSpeed, effective, "迷你瀑布也明确显示自动降速后的实际倍率");
        Check(Object.ReferenceEquals(arranged, full.BackgroundTracks.Single(t => t.Index == 7).Notes),
            "选中声轨显示快照也替换为已编配音符，不能遗留旧时值");
        Check(Field<Label>(form, "detailLabel").Text.Contains("口琴速度") && Field<Label>(form, "detailLabel").Text.Contains("换指"),
            "主窗披露实际口琴速度与调整音数");
        Near(source.Notes[0].Duration, .06, "口琴编配不修改原始声轨音长");
        Near(source.Notes[1].Start, .06, "口琴编配不修改原始声轨起音");
        double naturalDuration = Value(form, "Duration");
        Field<AudioPreview>(form, "audio").Dispose();
        Set(form, "noteDuration", 600 * effective + 1);
        Call(form, "StartAudio");
        Check(Field<Label>(form, "statusLabel").Text.Contains("10 分钟"),
            "真实试听调用按实际口琴速度计算时长；准备阶段拒绝过长音频，未访问音频设备");
        Set(form, "noteDuration", naturalDuration);

        // End at a whole number of analyzed cycles so this case isolates speed
        // and expansion; a clipped short final note is checked separately below.
        double requestedSeconds = .36 * 3 / effective;
        Set(form, "targetSeconds", requestedSeconds); Set(form, "repeatRange", true); Call(form, "RebuildNotes");
        arranged = Field<List<NoteEvent>>(form, "notes");
        Near(Value(form, "Duration") / Value(form, "EffectiveSpeed"), requestedSeconds, "循环按实际有效速度保留用户指定墙钟总时长");
        Check(arranged.Count == 12, "三轮循环保留全部原旋律音而不是逐音删减");
        Near(arranged[4].Start, .36, "循环接缝仍使用原谱周期");
        string path = Path.Combine(AppPaths.RootDirectory, "arranged-window-" + Guid.NewGuid().ToString("N") + ".mid");
        try
        {
            Call(form, "SaveCurrentMidi", path);
            List<NoteEvent> output = ScoreIO.LoadMidi(path).Tracks[0].Notes;
            Check(output.Count == arranged.Count, "实际导出与瀑布共用同一已编配旋律");
            for (int i = 0; i < output.Count; i++)
            {
                Check(output[i].Pitch == arranged[i].Pitch, "导出口琴编配保留音高");
                Check(Math.Abs(output[i].Start - arranged[i].Start / effective) <= 1.0 / MidiWriter.TicksPerSecond,
                    "导出按实际口琴速度换算起音而非用户请求速度");
                Check(Math.Abs(output[i].Duration - arranged[i].Duration / effective) <= 1.0 / MidiWriter.TicksPerSecond,
                    "导出保留已预留换指间隔的音长");
            }
            Check(Math.Abs(ReadMidiEndSeconds(File.ReadAllBytes(path)) - requestedSeconds) <= 1.0 / MidiWriter.TicksPerSecond,
                "实际 MIDI 结束事件保留指定墙钟总时长");
        }
        finally { if (File.Exists(path)) File.Delete(path); }

        Set(form, "targetSeconds", 0.0); Set(form, "repeatRange", false);
        Field<NumericUpDown>(form, "speedInput").Value = .5m;
        Near(Value(form, "EffectiveSpeed"), .5, "没有自定总时长时修改速度也必须重建编配");
        Check(!Object.ReferenceEquals(arranged, Field<List<NoteEvent>>(form, "notes")), "变速产生新的统一编配快照");
        Set(form, "rangeStart", .06); Set(form, "rangeEnd", .36); Call(form, "RebuildNotes");
        Near(Field<double>(form, "rangeStart"), .06, "编配保留用户选择的片段起点");
        Near(Field<double>(form, "rangeEnd"), .36, "编配保留用户选择的片段终点");
        Near(Value(form, "Duration"), .30, "片段时长仍以原谱边界为准");
        Near(Field<List<NoteEvent>>(form, "notes")[0].Start, 0, "已选片段共同从零开始，起音不额外移动");
        Set(form, "rangeStart", 0.0); Set(form, "rangeEnd", 0.0);
        Field<NumericUpDown>(form, "speedInput").Value = 1;
        SetFixture(form, new[] { new ScoreTrack { Name = "MELODY", Index = 3,
            Notes = new List<NoteEvent> { N(60, 0, .01), N(61, .01, .1) } } });
        CheckArrangementBlocked(form, "低于最低可奏速度的片段");
        Check(Field<double>(form, "sourceDuration") > 0 && Field<ComboBox>(form, "tracks").Items.Count > 0,
            "编配失败仍保留源轨和范围入口，允许重新选轨或选段");
        SetFixture(form, new[] { new ScoreTrack { Name = "MELODY", Index = 3, Notes = new List<NoteEvent> { N(60, 0, 1) } } });
        Set(form, "targetSeconds", .01); Call(form, "RebuildNotes");
        CheckArrangementBlocked(form, "指定终点截出的超短尾音");
        Near(Field<double>(form, "targetSeconds"), .01, "不能偷偷延长用户指定时长来掩盖短尾音");
        Set(form, "targetSeconds", 0.0); Call(form, "RebuildNotes");
        Check(Field<List<NoteEvent>>(form, "notes").Count == 1 && String.IsNullOrEmpty(Field<string>(form, "arrangementError")),
            "重新选择有效范围后可解除失败状态并恢复统一编配");
    }

    private static void CheckArrangementBlocked(MainForm form, string reason)
    {
        string error = Field<string>(form, "arrangementError");
        Check(!String.IsNullOrWhiteSpace(error) && Field<List<NoteEvent>>(form, "notes").Count == 0,
            reason + "必须明确阻止生成可奏输出");
        foreach (string operation in new[] { "ToggleListen", "StartAutomatic", "StartAudio" })
        {
            Call(form, operation);
            Check(Field<Label>(form, "statusLabel").Text.Contains(error) && !Field<bool>(form, "listening") &&
                !Field<AutoPlayer>(form, "autoPlayer").Running, reason + "阻止" + operation + "且保留具体原因");
        }
        string path = Path.Combine(AppPaths.RootDirectory, "blocked-arrangement-" + Guid.NewGuid().ToString("N") + ".mid");
        bool rejected = false;
        try { Call(form, "SaveCurrentMidi", path); }
        catch (TargetInvocationException ex) { rejected = ex.InnerException != null && ex.InnerException.Message.Contains(error); }
        Check(rejected && !File.Exists(path), reason + "不能写出假称可奏的 MIDI");
    }

    private static void OrnamentMainForm(MainForm form, string settingsPath)
    {
        Set(form, "rangeStart", 0.0); Set(form, "rangeEnd", 0.0); Set(form, "targetSeconds", 0.0); Set(form, "repeatRange", false);
        Field<NumericUpDown>(form, "speedInput").Value = 1;
        var source = new ScoreTrack { Name = "单音旋律候选", Index = 5,
            Notes = new List<NoteEvent> { N(60, 0, .30), N(61, .30, .06), N(60, .36, .30) } };
        source.Notes[1].Velocity = 35;
        SetFixture(form, new[] { source });
        var notes = Field<List<NoteEvent>>(form, "notes");
        Check(notes.Count == 2, "默认流畅优先应省去导致换指瓶颈的短弱邻音回返候选");
        double simplifiedSpeed = Value(form, "EffectiveSpeed");
        Near(simplifiedSpeed, 1, "简化装饰后可按请求速度演奏主音");
        CheckBox option = Field<CheckBox>(form, "simplifyOrnamentsCheck");
        Check(option.Checked && option.Text == "流畅优先", "主窗提供默认开启的流畅优先选项");
        Check(Field<ToolTip>(form, "sourceTip").GetToolTip(option).Contains("关闭"), "选项说明必须清楚提示关闭后保留装饰并必要时降速");
        ScoreCanvas full = Field<ScoreCanvas>(form, "canvas"), mini = Field<ScoreCanvas>(form, "compactCanvas");
        Check(full.SongSubtitle.StartsWith("旋律候选", StringComparison.Ordinal) && mini.SongSubtitle == full.SongSubtitle,
            "流畅优先不能把候选旋律伪装成确定主旋律");
        Check(Field<Label>(form, "detailLabel").Text.Contains("简化 1 个装饰音候选") &&
            Field<Label>(form, "statusLabel").Text.Contains("简化 1 个装饰音候选"), "状态与详情如实披露装饰候选简化数");
        option.Checked = false;
        notes = Field<List<NoteEvent>>(form, "notes");
        Check(notes.Count == 3 && notes[1].Pitch == 61 && Value(form, "EffectiveSpeed") < simplifiedSpeed,
            "关闭流畅优先恢复同一三音旋律，以统一降速保留装饰");
        Check(File.ReadAllText(settingsPath).Contains("\"SimplifyOrnaments\":false"), "关闭选项即时保存用户偏好");
        Near(Field<AppSettings>(form, "settings").Speed, 1, "开关简化不能改写用户请求速度");
        Check(source.Notes.Count == 3 && source.Notes[1].Pitch == 61 && source.Notes[1].Velocity == 35,
            "开关简化不删改原始声轨的装饰音或力度");
        Near(source.Notes[0].Duration, .30, "简化不改写原始前音时长");

        Set(form, "rangeStart", .12); Set(form, "rangeEnd", .66);
        Set(form, "targetSeconds", 1.62); Set(form, "repeatRange", true);
        option.Checked = true;
        notes = Field<List<NoteEvent>>(form, "notes");
        Check(notes.Count == 6 && notes.All(n => n.Pitch == 60), "片段三次循环必须每轮使用简化后的双主音，不能重新展开未简化源谱；音数=" +
            notes.Count + "，状态=" + Field<string>(form, "arrangementError"));
        Near(notes[0].Start, 0, "片段主音裁切后从零开始");
        Near(notes[1].Start, .24, "省略候选不移动后续主音起点");
        Near(notes[2].Start, .54, "循环使用原始片段周期而非删音后的末尾长度");
        Near(Value(form, "Duration") / Value(form, "EffectiveSpeed"), 1.62, "简化及循环仍保留目标实际总秒数");
        Near(Field<double>(form, "rangeStart"), .12, "流畅优先不改动片段起点设置");
        Near(Field<double>(form, "rangeEnd"), .66, "流畅优先不改动片段终点设置");
        Check(Object.ReferenceEquals(notes, full.Notes) && Object.ReferenceEquals(notes, mini.Notes) &&
            Object.ReferenceEquals(notes, full.BackgroundTracks.Single(t => t.Index == 5).Notes),
            "两种瀑布与选中声轨共享最终简化编配快照");
        Check(AppSettings.Load(settingsPath).Speed == 1 && File.ReadAllText(settingsPath).Contains("\"SimplifyOrnaments\":true"),
            "重新开启选项保存恢复且仍保留请求速度");
        Set(form, "rangeStart", 0.0); Set(form, "rangeEnd", 0.0); Set(form, "targetSeconds", 0.0); Set(form, "repeatRange", false);
        Call(form, "RebuildNotes");
    }

    private static void UnplayablePreviewDoesNotConsumeAudioLimit()
    {
        MainForm previewForm = new MainForm(false);
        try
        {
            Field<System.Windows.Forms.Timer>(previewForm, "timer").Stop();
            var source = Enumerable.Range(0, 20001).Select(i => N(110, 0, 0.5)).ToList();
            source.Add(N(60, 0.5, 0.5));
            Set(previewForm, "notes", source); Set(previewForm, "noteDuration", 2.0);
            // Disposing the real backend prevents any worker or device access.
            // Its validation still detects whether StartAudio sent raw skipped notes.
            Field<AudioPreview>(previewForm, "audio").Dispose();
            Call(previewForm, "StartAudio");
            string status = Field<Label>(previewForm, "statusLabel").Text;
            Check(status.Contains("试听失败") && !status.Contains("20000"),
                "实际主窗试听先排除不可奏音，不让作为休止的音符耗尽音频数量限制");
            Check(source.Count == 20002 && source[0].Pitch == 110,
                "试听过滤保留显示计划中的超域音供提示和重新移调");
        }
        finally { DisposeForm(previewForm); }
    }

    private static void NaturalPassIgnoresUnusedSeam(MainForm form)
    {
        Set(form, "rangeStart", 0.0); Set(form, "rangeEnd", 0.0); Set(form, "targetSeconds", 0.0); Set(form, "repeatRange", true);
        Field<NumericUpDown>(form, "speedInput").Value = 1.5m;
        SetFixture(form, new[] { new ScoreTrack { Name = "MELODY", Index = 4, Notes = new List<NoteEvent> { N(60, 0, .10) } } });
        Near(Value(form, "EffectiveSpeed"), 1.5, "自然一遍即使保留循环勾选，也不能为未播放接缝额外降速");
        Check(Field<List<NoteEvent>>(form, "notes").Count == 1, "自然一遍仅生成一个原音");
        Set(form, "targetSeconds", .06); Call(form, "RebuildNotes");
        Near(Value(form, "EffectiveSpeed"), 1.5, "目标短于一轮时不检查不存在的循环接缝");
        Near(Value(form, "Duration") / Value(form, "EffectiveSpeed"), .06, "单遍短片段保留指定墙钟时长");

        Set(form, "targetSeconds", 0.0);
        SetFixture(form, new[] { new ScoreTrack { Name = "MELODY", Index = 4, Notes = new List<NoteEvent> { N(60, 0, .03) } } });
        Check(Field<List<NoteEvent>>(form, "notes").Count == 1 && Value(form, "EffectiveSpeed") >= .5,
            "足够持音但不够自循环重触的短音，自然单遍仍可演奏");
        Set(form, "targetSeconds", .05); Call(form, "RebuildNotes");
        Check(Field<List<NoteEvent>>(form, "notes").Count == 1 && String.IsNullOrEmpty(Field<string>(form, "arrangementError")),
            "单遍降速即可填满目标时长时，不能因从未播放的接缝误判不可奏");
        Near(Value(form, "Duration") / Value(form, "EffectiveSpeed"), .05, "单遍填满目标时长不延长或缩短用户指定终点");
        Set(form, "targetSeconds", 0.0); Set(form, "repeatRange", false);
        Field<NumericUpDown>(form, "speedInput").Value = 1;
    }

    private static void TargetEndIgnoresUnplayedNotes(MainForm form)
    {
        Set(form, "rangeStart", 0.0); Set(form, "rangeEnd", 0.0); Set(form, "targetSeconds", 1.0); Set(form, "repeatRange", false);
        Field<NumericUpDown>(form, "speedInput").Value = 1;
        SetFixture(form, new[] { new ScoreTrack { Name = "MELODY", Index = 6,
            Notes = new List<NoteEvent> { N(60, 0, 1), N(62, 2, .01) } } });
        Check(Field<List<NoteEvent>>(form, "notes").Count == 1 && String.IsNullOrEmpty(Field<string>(form, "arrangementError")),
            "目标终点之后不会演奏的超短音，不能阻止当前一秒主音编配");
        Near(Value(form, "EffectiveSpeed"), 1, "未覆盖的尾段不能导致实际片段额外降速");
        Near(Value(form, "Duration"), 1, "按目标截取后仍保留指定终点");
        Set(form, "targetSeconds", .5);
        Field<NumericUpDown>(form, "speedInput").Value = 1.5m;
        Near(Value(form, "EffectiveSpeed"), 1.5, "目标覆盖范围按候选请求速度换算，未播尾音不限制快放");
        Near(Value(form, "Duration"), .75, "半秒墙钟目标在1.5倍速对应0.75秒谱面");
        Set(form, "targetSeconds", 1.5);
        Field<NumericUpDown>(form, "speedInput").Value = 1;
        Near(Value(form, "Duration"), 1.5, "目标内主音后的真实休止保留，未播尾音不参与分析");

        SetFixture(form, new[] { new ScoreTrack { Name = "MELODY", Index = 6,
            Notes = new List<NoteEvent> { N(60, 0, .03), N(62, .03, .97) } } });
        Check(Field<List<NoteEvent>>(form, "notes").Count == 2 && String.IsNullOrEmpty(Field<string>(form, "arrangementError")),
            "非循环目标虽超过请求速度下的一遍，仍须允许有效降速后覆盖的合法时长");
        Near(Value(form, "EffectiveSpeed"), .65, "短主音决定有效速度，候选阶段不提前拒绝合法非循环目标");
        Near(Value(form, "Duration") / Value(form, "EffectiveSpeed"), 1.5, "降速后的非循环计划精确保留1.5秒目标");
        Set(form, "targetSeconds", 2.0); Call(form, "RebuildNotes");
        CheckArrangementBlocked(form, "非循环目标超过有效速度下一遍时长");
        Near(Field<double>(form, "targetSeconds"), 2, "真正超长的非循环请求不得被候选分析静默截短");

        Set(form, "targetSeconds", .31);
        SetFixture(form, new[] { new ScoreTrack { Name = "MELODY", Index = 6,
            Notes = new List<NoteEvent> { N(60, 0, .3), N(61, .3, .3), N(60, .6, .3) } } });
        CheckArrangementBlocked(form, "目标终点截出的短尾音不是原谱装饰候选");
        Near(Field<double>(form, "targetSeconds"), .31, "不能把真实截断的短尾音当装饰删除或悄悄移动终点");
        Set(form, "targetSeconds", .33); Call(form, "RebuildNotes");
        CheckArrangementBlocked(form, "短尾音不能通过降速移到目标之外而静默消失");
        Near(Field<double>(form, "targetSeconds"), .33, "可通过减速避开的真实短尾音仍须明确让用户调整终点");
        Set(form, "targetSeconds", 0.0); Call(form, "RebuildNotes");
    }

    private static void ExportPlayableTimeline(MainForm form)
    {
        string path = Path.Combine(AppPaths.RootDirectory, "playable-output-" + Guid.NewGuid().ToString("N") + ".mid");
        var source = new[] { N(40, 0, 0.5), N(60, 0.5, 0.5), N(110, 1, 0.5),
            N(62, 1.5, 0.5), N(110, 2, 0.5) };
        AppSettings settings = Field<AppSettings>(form, "settings");
        try
        {
            foreach (double speed in new[] { 0.5, 1.0, 1.5 })
            {
                PlaybackPlan plan = PlaybackPlan.Build(source, null, 0, 3, 7, true, speed);
                settings.Speed = speed; settings.BasePitch = 60;
                Set(form, "effectiveSpeed", speed);
                Set(form, "notes", plan.Notes); Set(form, "noteDuration", plan.Duration);
                Call(form, "SaveCurrentMidi", path);
                List<NoteEvent> read = ScoreIO.LoadMidi(path).Tracks[0].Notes;
                List<NoteEvent> expected = plan.Notes.Where(n => HarmonicaMap.Map(n.Pitch, 60).Playable).ToList();
                Check(read.Count == expected.Count && read.All(n => HarmonicaMap.Map(n.Pitch, 60).Playable),
                    "实际主窗导出只写可奏旋律，循环中的超域音不漏出");
                for (int i = 0; i < read.Count; i++)
                {
                    Check(read[i].Pitch == expected[i].Pitch && read[i].Velocity == expected[i].Velocity,
                        "实际 MIDI 往返保留可奏音高与力度");
                    Check(Math.Abs(read[i].Start - expected[i].Start / speed) <= 1.0 / MidiWriter.TicksPerSecond,
                        "导出只换算一次速度，循环起音与中间休止不提前");
                    Check(Math.Abs(read[i].Duration - expected[i].Duration / speed) <= 1.0 / MidiWriter.TicksPerSecond,
                        "导出只换算一次可奏音长");
                }
                Near(ReadMidiEndSeconds(File.ReadAllBytes(path)), 7,
                    "真实 MIDI 结束事件保留目标七秒，含超域尾音与尾休止");
                Check(Object.ReferenceEquals(Field<List<NoteEvent>>(form, "notes"), plan.Notes) &&
                    plan.Notes.Any(n => n.Pitch == 110), "导出不替换或删除主窗显示计划中的不可奏音");
            }
            Near(source[1].Start, 0.5, "多次变速导出不修改源 MIDI 起音");
            Near(source[3].Duration, 0.5, "多次变速导出不修改源 MIDI 时值");
        }
        finally
        {
            settings.Speed = 1;
            Set(form, "effectiveSpeed", 1.0);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static double ReadMidiEndSeconds(byte[] bytes)
    {
        int cursor = 22, tick = 0;
        while (cursor < bytes.Length)
        {
            tick += ReadVariable(bytes, ref cursor);
            int status = bytes[cursor++];
            if (status == 0xff)
            {
                int type = bytes[cursor++], length = ReadVariable(bytes, ref cursor);
                if (type == 0x2f) return tick / (double)MidiWriter.TicksPerSecond;
                cursor += length;
            }
            else if (status == 0x80 || status == 0x90) cursor += 2;
            else if (status == 0xc0) cursor++;
            else throw new Exception("导出 MIDI 出现未预期事件。");
        }
        throw new Exception("导出 MIDI 缺少结束事件。");
    }
    private static int ReadVariable(byte[] bytes, ref int cursor)
    {
        int value = 0, part;
        do { part = bytes[cursor++]; value = (value << 7) | (part & 0x7f); } while ((part & 0x80) != 0);
        return value;
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
