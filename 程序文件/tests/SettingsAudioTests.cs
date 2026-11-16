using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonicaStudio;

public static class SettingsAudioTests
{
    private static int checks;

    public static int Run()
    {
        checks = 0;
        TestSettings();
        TestWaveFormat();
        TestTempoAndPosition();
        TestCancellation();
        TestPublicValidation();
        TestPlannedDuration();
        TestPlayableRestRendering();
        TestFailureSessions();
        Console.WriteLine("设置与本地音频测试通过，共 " + checks + " 项断言（未播放音频）。");
        return checks;
    }

    private static void TestSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "HarmonicaStudioSettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string configPath = Path.Combine(directory, "input.json");
        string savedPath = Path.Combine(directory, "saved.json");
        try
        {
            File.WriteAllText(configPath, "{\"Compact\":true,\"AlwaysOnTop\":false}", Encoding.UTF8);
            AppSettings partial = AppSettings.Load(configPath);
            Assert(partial.Compact && !partial.AlwaysOnTop && partial.Opacity == 0.9 &&
                partial.BasePitch == 60 && partial.Speed == 1.0 && partial.X == -1,
                "带 UTF-8 BOM 的设置缺键时应保留默认值，并保留明确的 false。");
            Assert(!partial.AutoMode && partial.CountdownSeconds == 5 &&
                partial.StartHotkey == "F5" && partial.PauseHotkey == "F6" &&
                partial.StopHotkey == "F7" && partial.ToggleWindowHotkey == "F8" && partial.CompactHotkey == "F9",
                "旧配置应默认关闭自动模式，并补齐倒计时和热键。");
            File.WriteAllText(configPath, "{\"Compact\":true,\"AlwaysOnTop\":false}", new UTF8Encoding(false));
            partial = AppSettings.Load(configPath);
            Assert(partial.Compact && !partial.AlwaysOnTop && partial.Opacity == 0.9,
                "无 BOM 的 UTF-8 设置也应正确加载。");

            File.WriteAllText(configPath, "{broken", Encoding.UTF8);
            AppSettings broken = AppSettings.Load(configPath);
            Assert(broken.Opacity == 0.9 && broken.AlwaysOnTop && !broken.Compact &&
                broken.BasePitch == 60, "损坏的 JSON 应恢复为默认设置。");

            File.WriteAllText(configPath,
                "{\"Opacity\":0.1,\"BasePitch\":999,\"Speed\":8,\"Transpose\":-500,\"X\":999999}", Encoding.UTF8);
            AppSettings limits = AppSettings.Load(configPath);
            Assert(limits.Opacity == 0.65 && limits.BasePitch == 84 && limits.Speed == 1.5 &&
                limits.Transpose == -24 && limits.X == -1, "越界设置应落在实际界面范围内。");
            File.WriteAllText(configPath,
                "{\"AutoMode\":true,\"CountdownSeconds\":99,\"StartHotkey\":\"F12\",\"PauseHotkey\":\"F12\",\"StopHotkey\":\" f10 \",\"ToggleWindowHotkey\":\"F4\",\"CompactHotkey\":null}",
                new UTF8Encoding(false));
            AppSettings hotkeyLimits = AppSettings.Load(configPath);
            Assert(hotkeyLimits.AutoMode && hotkeyLimits.CountdownSeconds == 10 &&
                hotkeyLimits.StartHotkey == "F12" && hotkeyLimits.StopHotkey == "F10" &&
                hotkeyLimits.PauseHotkey == "F12" && hotkeyLimits.ToggleWindowHotkey == "F8" &&
                hotkeyLimits.CompactHotkey == "F9", "旧暂停键与播放键重复不会导致假冲突，四个实际功能各自保留合法选择。");
            AppSettings reserved = new AppSettings
            {
                CountdownSeconds = -1, StartHotkey = "F05", PauseHotkey = "F5", StopHotkey = "F6",
                ToggleWindowHotkey = "bad", CompactHotkey = "F6"
            };
            reserved.Normalize();
            Assert(reserved.CountdownSeconds == 3 && reserved.StartHotkey == "F5" &&
                reserved.PauseHotkey == "F5" && reserved.StopHotkey == "F6" &&
                reserved.ToggleWindowHotkey == "F8" && reserved.CompactHotkey == "F9" &&
                new HashSet<string>(new string[] { reserved.StartHotkey, reserved.StopHotkey,
                    reserved.ToggleWindowHotkey, reserved.CompactHotkey }).Count == 4,
                "修复先保留后续合法键，旧暂停键不占用按键且四项热键互不重复。");
            File.WriteAllText(configPath,
                "{\"StartHotkey\":\"F11\",\"PauseHotkey\":\"F7\",\"StopHotkey\":\"F7\",\"ToggleWindowHotkey\":\"F8\",\"CompactHotkey\":\"F9\"}",
                new UTF8Encoding(false));
            AppSettings legacy = AppSettings.Load(configPath);
            Assert(legacy.StartHotkey == "F11" && legacy.StopHotkey == "F7" && legacy.ToggleWindowHotkey == "F8" &&
                legacy.CompactHotkey == "F9" && legacy.PauseHotkey == "F7",
                "旧暂停配置等于停止键仍保留用户播放和停止选择，不虚构第五个绑定。");

            AppSettings original = new AppSettings
            {
                Opacity = 0.78, AlwaysOnTop = false, BasePitch = 48, Speed = 0.75,
                Transpose = 7, Compact = true, X = -600, Y = 200,
                AutoMode = true, CountdownSeconds = 7, StartHotkey = "F10", PauseHotkey = "F12",
                StopHotkey = "F5", ToggleWindowHotkey = "F6", CompactHotkey = "F11"
            };
            original.Save(savedPath);
            AppSettings restored = AppSettings.Load(savedPath);
            Assert(restored.Opacity == original.Opacity && restored.AlwaysOnTop == original.AlwaysOnTop &&
                restored.BasePitch == original.BasePitch && restored.Speed == original.Speed &&
                restored.Transpose == original.Transpose && restored.Compact == original.Compact &&
                restored.X == original.X && restored.Y == original.Y, "设置首次保存后应完整恢复。");
            Assert(restored.AutoMode && restored.CountdownSeconds == 7 && restored.StartHotkey == "F10" &&
                restored.PauseHotkey == "F12" && restored.StopHotkey == "F5" &&
                restored.ToggleWindowHotkey == "F6" && restored.CompactHotkey == "F11",
                "自动模式、倒计时与自定义热键应在保存后完整恢复。");
            original.Speed = 1.25;
            original.Opacity = Double.NaN;
            original.BasePitch = -12;
            original.Save(savedPath);
            restored = AppSettings.Load(savedPath);
            Assert(restored.Speed == 1.25 && restored.Opacity == 0.9 && restored.BasePitch == 36,
                "覆盖保存应替换旧设置并修正异常数值。");
            Assert(Directory.GetFiles(directory).Length == 2, "原子保存不应遗留临时文件。");
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
            if (File.Exists(savedPath)) File.Delete(savedPath);
            // No recursive deletion: this test creates only the two named files.
            Directory.Delete(directory);
        }
    }

    private static void TestWaveFormat()
    {
        byte[] wave = Render(0.0, 1.0, 0.0, 1.0, CancellationToken.None);
        Assert(Text(wave, 0, 4) == "RIFF" && Text(wave, 8, 4) == "WAVE" &&
            Text(wave, 12, 4) == "fmt " && Text(wave, 36, 4) == "data", "试听应生成标准 PCM WAV。");
        Assert(BitConverter.ToInt32(wave, 4) == wave.Length - 8 &&
            BitConverter.ToInt32(wave, 16) == 16 && BitConverter.ToInt16(wave, 20) == 1 &&
            BitConverter.ToInt16(wave, 22) == 1 && BitConverter.ToInt32(wave, 24) == 16000 &&
            BitConverter.ToInt32(wave, 28) == 32000 && BitConverter.ToInt16(wave, 32) == 2 &&
            BitConverter.ToInt16(wave, 34) == 16 && BitConverter.ToInt32(wave, 40) == 32000 &&
            wave.Length == 32044, "一秒试听应为 16 kHz、16-bit、单声道且长度一致。");
        Assert(Peak(wave, 0.05, 0.20) > 1000 && Sample(wave, 0) == 0 &&
            Math.Abs(Sample(wave, 15999)) < 100, "音频应有实际信号，起止包络应避免突变。");
    }

    private static void TestTempoAndPosition()
    {
        byte[] normal = Render(0.6, 1.2, 0.0, 1.0, CancellationToken.None);
        byte[] faster = Render(0.6, 1.2, 0.0, 1.5, CancellationToken.None);
        Assert(normal.Length == 44 + 38400 && faster.Length == 44 + 25600,
            "速度改变应同步缩放完整片段的时长。");
        Assert(Peak(normal, 0.1, 0.55) == 0 && Peak(normal, 0.7, 0.9) > 1000 &&
            Peak(faster, 0.1, 0.35) == 0 && Peak(faster, 0.45, 0.6) > 1000,
            "变速后实际音符起点应跟随时间缩放。");
        byte[] clipped = Render(0.6, 1.2, 0.75, 1.0, CancellationToken.None);
        Assert(clipped.Length == 44 + (int)Math.Ceiling(0.45 * 16000.0) * 2 &&
            Sample(clipped, 0) == 0 && Peak(clipped, 0.02, 0.15) > 1000,
            "从持续音中间开始，应剪掉前段并保留立即进入的旋律及渐入。");
    }

    private static void TestCancellation()
    {
        using (CancellationTokenSource source = new CancellationTokenSource())
        {
            source.Cancel();
            try
            {
                Render(0.0, 1.0, 0.0, 1.0, source.Token);
                throw new Exception("取消后的渲染不应成功。");
            }
            catch (TargetInvocationException ex)
            {
                Assert(ex.InnerException is OperationCanceledException, "取消应中止渲染，而不是产生音频。");
            }
        }
    }

    private static void TestPublicValidation()
    {
        using (AudioPreview preview = new AudioPreview())
        {
            bool invalidSpeed = false;
            try { preview.Play(new List<NoteEvent>(), 0.0, 0.1); }
            catch (ArgumentOutOfRangeException) { invalidSpeed = true; }
            Assert(invalidSpeed, "公共试听接口应拒绝超出范围的速度。");
            bool tooLong = false;
            try
            {
                preview.Play(new List<NoteEvent>
                {
                    new NoteEvent { Pitch = 60, Velocity = 100, Start = 0.0, Duration = 601.0 }
                }, 0.0, 1.0);
            }
            catch (ArgumentException) { tooLong = true; }
            Assert(tooLong, "超长片段应在创建渲染任务前被拒绝。");
            // An empty melody should create no SoundPlayer or rendering worker.
            preview.Play(new List<NoteEvent>(), 0.0, 1.0);
            preview.Stop();
        }
    }

    private static byte[] Render(double start, double end, double position, double speed, CancellationToken token)
    {
        Type noteType = typeof(AudioPreview).GetNestedType("RenderNote", BindingFlags.NonPublic);
        object note = Activator.CreateInstance(noteType, true);
        noteType.GetField("Pitch").SetValue(note, 60);
        noteType.GetField("Velocity").SetValue(note, 100);
        noteType.GetField("Start").SetValue(note, start);
        noteType.GetField("End").SetValue(note, end);
        IList notes = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(noteType));
        notes.Add(note);
        MethodInfo render = typeof(AudioPreview).GetMethod("RenderWave", BindingFlags.Static | BindingFlags.NonPublic);
        return (byte[])render.Invoke(null, new object[] { notes, position, speed, (end - position) / speed, token });
    }

    private static void TestPlannedDuration()
    {
        NoteEvent[] source = { new NoteEvent { Pitch = 60, Velocity = 100, Start = 0.5, Duration = 0.5 } };
        MethodInfo prepare = typeof(AudioPreview).GetMethod("Prepare", BindingFlags.Static | BindingFlags.NonPublic);
        object request = prepare.Invoke(null, new object[] { source, 0.0, 1.0, (double?)3.0 });
        Type requestType = request.GetType();
        double seconds = (double)requestType.GetField("Seconds").GetValue(request);
        object snapshot = requestType.GetField("Notes").GetValue(request);
        Assert(Math.Abs(seconds - 3.0) < 0.000001, "试听请求使用计划结束时刻，而非最后音符。");
        MethodInfo render = typeof(AudioPreview).GetMethod("RenderWave", BindingFlags.Static | BindingFlags.NonPublic);
        byte[] wave = (byte[])render.Invoke(null, new object[] { snapshot, 0.0, 1.0, seconds, CancellationToken.None });
        Assert(wave.Length == 44 + 3 * 16000 * 2, "计划尾静音生成完整三秒PCM。");
        Assert(Peak(wave, 0.0, 0.4) == 0 && Peak(wave, 0.6, 0.9) > 1000 && Peak(wave, 1.05, 3.0) == 0,
            "试听同时保留前导静音、真实音符和尾静音，没有伪造声音。");
        object later = prepare.Invoke(null, new object[] { source, 0.75, 1.5, (double?)3.0 });
        Assert(Math.Abs((double)requestType.GetField("Seconds").GetValue(later) - 1.5) < 0.000001,
            "试听从中间开始的实际剩余时间也按速度缩放。");
        source[0].Duration = 2.0;
        Type renderNote = typeof(AudioPreview).GetNestedType("RenderNote", BindingFlags.NonPublic);
        Assert(Math.Abs((double)renderNote.GetField("End").GetValue(((IList)snapshot)[0]) - 1.0) < 0.000001,
            "计划试听持有私有副本，不随源音符修改。");
        using (AudioPreview preview = new AudioPreview())
        {
            // Empty snapshots validate limits without creating workers or playing audio.
            preview.Play(new NoteEvent[0], 0, 1, 600);
            preview.Play(new NoteEvent[0], 0, 1.5, 900);
            preview.Play(new NoteEvent[0], 30, 1, 630);
            Assert(true, "600秒限制依据变速后实际剩余时间，边界无需发声。");
            foreach (double duration in new[] { 0.0, Double.NaN, Double.PositiveInfinity, 601.0 })
            {
                bool rejected = false;
                try { preview.Play(new NoteEvent[0], 0, 1, duration); }
                catch (ArgumentException) { rejected = true; }
                Assert(rejected, "无效显式试听时长在创建音频任务之前拒绝。");
            }
            bool tooShort = false;
            try { preview.Play(source, 0, 1, 1); }
            catch (ArgumentOutOfRangeException) { tooShort = true; }
            Assert(tooShort, "试听不自动丢弃超过计划终点的实际音符。");
            bool pastEnd = false;
            try { preview.Play(new NoteEvent[0], 2, 1, 1); }
            catch (ArgumentOutOfRangeException) { pastEnd = true; }
            Assert(pastEnd, "试听当前位置超过计划终点时明确拒绝。");
        }
    }

    private static string Text(byte[] data, int start, int length)
    {
        return Encoding.ASCII.GetString(data, start, length);
    }

    private static void TestPlayableRestRendering()
    {
        NoteEvent[] source = {
            new NoteEvent { Pitch = 40, Velocity = 100, Start = 0, Duration = 0.5 },
            new NoteEvent { Pitch = 60, Velocity = 100, Start = 0.5, Duration = 0.5 },
            new NoteEvent { Pitch = 110, Velocity = 100, Start = 1, Duration = 0.5 },
            new NoteEvent { Pitch = 62, Velocity = 100, Start = 1.5, Duration = 0.5 },
            new NoteEvent { Pitch = 110, Velocity = 100, Start = 2, Duration = 0.5 }
        };
        PlaybackPlan plan = PlaybackPlan.PreparePlayableOutput(source, 3, 60);
        foreach (double speed in new[] { 0.5, 1.0, 1.5 })
        {
            byte[] wave = RenderPrepared(plan.Notes, 0, speed, plan.Duration);
            Assert(wave.Length == 44 + (int)Math.Ceiling(3 / speed * 16000) * 2,
                "可奏试听保留包括超域尾音在内的完整变速时间轴。");
            Assert(Peak(wave, 0.05 / speed, 0.4 / speed) == 0 &&
                Peak(wave, 1.05 / speed, 1.4 / speed) == 0 && Peak(wave, 2.05 / speed, 3 / speed) == 0,
                "不可奏的前导、中间、尾部音都在原时段静音。");
            Assert(Peak(wave, 0.6 / speed, 0.9 / speed) > 1000 && Peak(wave, 1.6 / speed, 1.9 / speed) > 1000,
                "两段可奏旋律仍按原时刻发声，未压缩中间休止。");
        }
        byte[] resumed = RenderPrepared(plan.Notes, 1.25, 1, plan.Duration);
        Assert(Peak(resumed, 0.01, 0.2) == 0 && Peak(resumed, 0.35, 0.6) > 1000 && Peak(resumed, 0.8, 1.7) == 0,
            "从超域休止中间继续试听，后续音按剩余时间进入并保留尾休止。");
        byte[] generic = RenderPrepared(new[] { source[0] }, 0, 1, 0.5);
        Assert(Peak(generic, 0.1, 0.4) > 1000, "通用 AudioPreview 仍可渲染口琴音域之外的合法 MIDI 音高。");
        Assert(source.Length == 5 && source[2].Pitch == 110 && source[3].Start == 1.5,
            "输出试听不修改原始显示计划。");
    }

    private static byte[] RenderPrepared(IList<NoteEvent> notes, double position, double speed, double duration)
    {
        MethodInfo prepare = typeof(AudioPreview).GetMethod("Prepare", BindingFlags.Static | BindingFlags.NonPublic);
        object request = prepare.Invoke(null, new object[] { notes, position, speed, (double?)duration });
        Type type = request.GetType();
        return (byte[])typeof(AudioPreview).GetMethod("RenderWave", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { type.GetField("Notes").GetValue(request), position, speed,
                (double)type.GetField("Seconds").GetValue(request), CancellationToken.None });
    }

    private static void TestFailureSessions()
    {
        using (var preview = new AudioPreview())
        {
            var pending = new List<Action>();
            int applied = 0, legacy = 0;
            string lastError = null;
            preview.Failed += delegate { legacy++; };
            preview.SessionFailed += delegate(long session, string error)
            {
                // Simulate a delayed UI queue without opening an audio device.
                pending.Add(delegate
                {
                    if (session != preview.SessionId) return;
                    applied++; lastError = error;
                });
            };
            long original = preview.SessionId;
            FailPreviewRender(preview, original);
            Assert(pending.Count == 1 && legacy == 1, "当前渲染失败提供会话号并兼容原失败事件。");
            preview.Play(new NoteEvent[0], 0, 1);
            Assert(preview.SessionId != original, "新试听即使没有可发声音符也会使旧会话失效。");
            pending[0]();
            Assert(applied == 0, "旧失败排队后切换试听，队列执行时不会覆盖新会话。");
            FailPreviewRender(preview, original);
            Assert(pending.Count == 1 && legacy == 1, "过期渲染线程失败不发布新通知。");
            FailPreviewRender(preview, preview.SessionId);
            pending[1]();
            Assert(applied == 1 && lastError.StartsWith("本地试听失败：", StringComparison.Ordinal),
                "当前会话的真实失败仍提供中文原因。");
            FailPreviewRender(preview, preview.SessionId);
            long beforeStop = preview.SessionId;
            preview.Stop();
            pending[2]();
            Assert(preview.SessionId != beforeStop && applied == 1, "停止后延迟失败同样失效。");
            preview.Dispose();
            FailPreviewRender(preview, preview.SessionId);
            Assert(pending.Count == 3 && legacy == 3, "关闭后的渲染失败不更新界面。");
        }
    }

    private static void FailPreviewRender(AudioPreview preview, long session)
    {
        // Null render input fails before SoundPlayer creation. Exercise the real
        // worker catch/finally path; this probe neither plays audio nor starts a task.
        MethodInfo render = typeof(AudioPreview).GetMethod("RenderAndPlay", BindingFlags.Instance | BindingFlags.NonPublic);
        render.Invoke(preview, new object[] { null, 0.0, 1.0, 0.0, session, new CancellationTokenSource() });
    }

    private static int Sample(byte[] wave, int index)
    {
        return BitConverter.ToInt16(wave, 44 + index * 2);
    }

    private static int Peak(byte[] wave, double start, double end)
    {
        int peak = 0;
        int first = (int)(start * 16000.0);
        int last = Math.Min((wave.Length - 44) / 2, (int)(end * 16000.0));
        for (int i = first; i < last; i++) peak = Math.Max(peak, Math.Abs(Sample(wave, i)));
        return peak;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }
}
