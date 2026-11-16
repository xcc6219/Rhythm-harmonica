using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HarmonicaStudio;

namespace HarmonicaStudio.Tests
{
    public static class AutoPlayerTests
    {
        private static int Checks;

        public static int Run(bool includeNativeWindowChecks = true)
        {
            Checks = 0;
            Check(AutoPlayer.NativeInputSize == (IntPtr.Size == 8 ? 40 : 28), "Windows INPUT 结构尺寸");
            WindowClassification();
            DenseTimingAndCleanup();
            RepeatedOnsetsKeepScoreTiming();
            ModifierChangesKeepScoreTiming();
            RestsAndNaturalChangesKeepNoteLengths();
            ForegroundLoss();
            MouseModifierForegroundLoss();
            PauseAndResume();
            CountdownCancellation();
            CountdownResume();
            PreparationAfterCountdown();
            PreparationPause();
            PreparationTimeoutAndCancellation();
            PreparationPermissionFailure();
            RejectedWindowAndInput();
            CleanupFailure();
            OutOfRangeRest();
            NotatedRestsReleaseModifiers();
            ExplicitDuration();
            if (includeNativeWindowChecks) Checks += AutoPlaybackWindowGuardTests.Run();
            return Checks;
        }

        private static void ExplicitDuration()
        {
            VirtualClock clock = new VirtualClock();
            FakeInput input = new FakeInput(clock);
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                player.Start(new[] { Note(61, 0, 0.05) }, 1, 60, 0, 1);
                Await(delegate { return Filter(input.Snapshot(), false, false).Count == 1 && player.Position > 0.05; }, "进入计划尾部静音");
                Check(player.Running && input.HeldCount == 0, "尾静音继续计时且不保持音键或鼠标修饰");
                int sent = input.Snapshot().Count;
                player.TogglePause();
                double pausedAt = player.Position;
                Thread.Sleep(25);
                Check(player.Paused && Math.Abs(player.Position - pausedAt) < 0.000001, "尾静音暂停也冻结谱面时间");
                Check(input.Snapshot().Count == sent, "静音暂停期间无新增输入");
                player.TogglePause();
                Await(delegate { return !player.Running; }, "尾静音恢复后按计划结束");
                Check(Math.Abs(player.Position - 1) < 0.000001 && clock.Milliseconds >= 1000, "自动结束使用计划完整时长");
                Check(input.Snapshot().Count == sent && input.HeldCount == 0, "尾静音不靠伪音符延长");
                Check(player.LastCompletionMessage == "自动演奏完成。", "结束原因与后端停止状态同步可读，界面无需等待消息队列");
                foreach (double duration in new[] { 0.0, 0.01, Double.NaN, Double.PositiveInfinity, ScoreIO.MaximumDurationSeconds + 1 })
                {
                    bool rejected = false;
                    try { player.Start(new[] { Note(60, 0, 0.1) }, 1, 60, 0, duration); }
                    catch (ArgumentOutOfRangeException) { rejected = true; }
                    Check(rejected && !player.Running, "无效或短于音符的计划时长拒绝启动");
                }
            }
            clock = new VirtualClock(); input = new FakeInput(clock);
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                player.Start(new[] { Note(60, 0, 0.05) }, 1, 60, 0, 2);
                Await(delegate { return player.Position > 0.05; }, "准备静音时切出窗口");
                input.Foreground = 2;
                Await(delegate { return !player.Running; }, "尾静音保持前台丢失保护");
                Check(player.Position < 2 && input.HeldCount == 0, "尾静音切出终止而非继续等待");
                Check(player.LastCompletionMessage.Contains("目标游戏窗口"), "异常结束保留具体原因，不能误报演奏完成");
            }
        }

        internal static AutoPlayer CompletedBackendForUiTest(bool loseForeground)
        {
            VirtualClock clock=new VirtualClock();
            FakeInput input=new FakeInput(clock) { SwitchAfterKeyboardDown=loseForeground };
            var player=new AutoPlayer(input,clock);
            try
            {
                player.Start(new[] {Note(60,0,.05)},1,60,0);
                Stopwatch limit=Stopwatch.StartNew();
                while (player.Running)
                {
                    if (limit.ElapsedMilliseconds>3000) throw new Exception("界面结束测试的模拟后端超时。");
                    Thread.Sleep(1);
                }
                if (input.HeldCount!=0 || String.IsNullOrEmpty(player.LastCompletionMessage))
                    throw new Exception("模拟后端未释放输入或没有结束原因。");
                return player;
            }
            catch { player.Dispose(); throw; }
        }

        private static void DenseTimingAndCleanup()
        {
            VirtualClock clock = new VirtualClock();
            FakeInput input = new FakeInput(clock);
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                player.Start(new[] { Note(61, 0, 0.01), Note(61, 0.01, 0.01), Note(65, 0.02, 0.02) }, 1, 60, 0);
                Await(delegate { return !player.Running; }, "密集乐谱完成");
                List<Record> events = input.Snapshot();
                List<Record> keyDown = Filter(events, false, true);
                List<Record> keyUp = Filter(events, false, false);
                List<Record> mouseDown = Filter(events, true, true);
                Check(keyDown.Count == 3 && keyUp.Count == 3, "密集音符不丢失");
                Check(mouseDown.Count == 1 && mouseDown[0].Button == 2, "连续升半音保留中键修饰");
                Check(keyDown[0].Time - mouseDown[0].Time >= 35, "鼠标修饰至少提前 35 ms");
                for (int i = 0; i < 3; i++) Check(keyUp[i].Time - keyDown[i].Time >= 45, "每个音键最短 45 ms");
                Check(keyDown[1].Time - keyUp[0].Time >= 20, "同音键松开间隔至少 20 ms");
                List<Record> mouseUp = Filter(events, true, false);
                Check(mouseUp.Count == 1 && keyDown[2].Time - mouseUp[0].Time >= 35, "从升半音转自然音的修饰释放也提前 35 ms");
                Check(input.HeldCount == 0, "正常结束释放全部输入");
                Check(Math.Abs(player.Position - 0.04) < 0.000001, "结束后位置为谱面时长");
                foreach (Record record in keyDown) Check(record.Window == 1, "音键只发送给绑定前台窗口");
            }
        }

        private static void RepeatedOnsetsKeepScoreTiming()
        {
            foreach (double speed in new[] { 0.5, 1.0, 1.5 })
            {
                VirtualClock clock = new VirtualClock();
                FakeInput input = new FakeInput(clock);
                var notes = new List<NoteEvent>();
                for (int i = 0; i < 10; i++) notes.Add(Note(60, i * 0.3, 0.3));
                using (AutoPlayer player = new AutoPlayer(input, clock))
                {
                    player.Start(notes, speed, 60, 0);
                    Await(delegate { return !player.Running; }, "连续同音按原谱节奏完成", 20000);
                    List<Record> downs = Filter(input.Snapshot(), false, true), ups = Filter(input.Snapshot(), false, false);
                    Check(downs.Count == 10 && ups.Count == 10, "连续同音保留全部十次起音");
                    for (int i = 0; i < downs.Count; i++)
                    {
                        double expected = notes[i].Start * 1000 / speed;
                        Check(Math.Abs(downs[i].Time - downs[0].Time - expected) <= 1,
                            "连续同音不累计重触延迟：第" + (i + 1) + "音应在" + expected + "ms，实际" + (downs[i].Time - downs[0].Time) + "ms");
                        Check(ups[i].Time - downs[i].Time >= 45, "连续同音仍满足最短按键时长");
                        if (i > 0) Check(downs[i].Time - ups[i - 1].Time >= 20, "每次同音仍有至少20ms松键间隔");
                        Check(notes[i].Start == i * 0.3 && notes[i].Duration == 0.3, "换指时序不能回写源谱时刻或音长");
                    }
                    Check(Math.Abs(ups[9].Time - downs[0].Time - 3000 / speed) <= 1 && input.HeldCount == 0,
                        "末音保留完整音长，实际结束时间不因十次同音逐次拖长");
                }
            }
        }

        private static void ModifierChangesKeepScoreTiming()
        {
            // Add/remove semitone and octave modifiers, including a simultaneous
            // same-key retrigger: the required gaps overlap rather than add up.
            int[] pitches = { 60, 61, 49, 61, 60, 72, 84, 72, 60 };
            VirtualClock clock = new VirtualClock();
            FakeInput input = new FakeInput(clock);
            var notes = new List<NoteEvent>();
            for (int i = 0; i < pitches.Length; i++) notes.Add(Note(pitches[i], i * 0.2, 0.2));
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                player.Start(notes, 1, 60, 0);
                Await(delegate { return !player.Running; }, "半音与八度切换按谱完成", 10000);
                List<Record> events = input.Snapshot();
                List<Record> downs = Filter(events, false, true), ups = Filter(events, false, false);
                Check(downs.Count == pitches.Length && ups.Count == pitches.Length, "切换修饰键不丢失音符");
                for (int i = 0; i < downs.Count; i++)
                {
                    Check(Math.Abs(downs[i].Time - downs[0].Time - i * 200) <= 1,
                        "连续换修饰键不累计准备延迟：第" + (i + 1) + "音实际" + (downs[i].Time - downs[0].Time) + "ms");
                    Check(ups[i].Time - downs[i].Time >= 45, "换修饰键仍满足最短按键时长");
                    if (i == 0) continue;
                    foreach (Record action in events)
                        if (action.Mouse && action.Time >= ups[i - 1].Time && action.Time < downs[i].Time)
                            Check(downs[i].Time - action.Time >= 35, "修饰键按下或释放均至少提前35ms");
                    if (downs[i].Key == downs[i - 1].Key)
                        Check(downs[i].Time - ups[i - 1].Time >= 20, "同键跨音区仍保留20ms重触间隔");
                }
                Check(ups[ups.Count - 1].Time - downs[0].Time == 1800 && input.HeldCount == 0,
                    "多次换修饰键后末音和实际结束时间仍与谱面对齐");
            }
        }

        private static void RestsAndNaturalChangesKeepNoteLengths()
        {
            // first pitch, next pitch, next start (ms), expected first key-up (ms)
            int[][] cases = {
                new[] { 60, 62, 200, 200 }, new[] { 61, 61, 240, 200 },
                new[] { 61, 49, 300, 200 }, new[] { 60, 61, 220, 185 },
                new[] { 60, 61, 235, 200 }, new[] { 61, 61, 210, 190 }
            };
            foreach (int[] item in cases)
            {
                VirtualClock clock = new VirtualClock();
                FakeInput input = new FakeInput(clock);
                using (AutoPlayer player = new AutoPlayer(input, clock))
                {
                    player.Start(new[] { Note(item[0], 0, 0.2), Note(item[1], item[2] / 1000.0, 0.2) }, 1, 60, 0);
                    Await(delegate { return !player.Running; }, "自然换音与真实休止完成");
                    List<Record> downs = Filter(input.Snapshot(), false, true), ups = Filter(input.Snapshot(), false, false);
                    Check(downs.Count == 2 && ups.Count == 2, "真实休止前后各保留一次起音");
                    Check(Math.Abs(downs[1].Time - downs[0].Time - item[2]) <= 1, "休止后的音符不被额外延后");
                    Check(Math.Abs(ups[0].Time - downs[0].Time - item[3]) <= 1,
                        "已有休止优先用于换指；无需准备时保留完整音长，需要时只缩短不足部分");
                    Check(ups[1].Time - downs[1].Time == 200 && input.HeldCount == 0, "最后一个音不额外截短并释放全部输入");
                }
            }
        }

        private static void ForegroundLoss()
        {
            VirtualClock clock = new VirtualClock();
            FakeInput input = new FakeInput(clock);
            input.SwitchAfterKeyboardDown = true;
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                player.Start(new[] { Note(61, 0, 2) }, 1, 60, 0);
                Await(delegate { return !player.Running; }, "切出游戏终止演奏");
                Check(input.HeldCount == 0, "切出后释放本程序按下的键鼠");
                Check(Filter(input.Snapshot(), false, true).Count == 1, "切出后没有继续按下音键");
                Check(Filter(input.Snapshot(), true, false).Count == 1, "切出后鼠标修饰释放");
                int sent = input.Snapshot().Count;
                Thread.Sleep(25);
                Check(input.Snapshot().Count == sent, "终止后的旧 worker 不继续发送");
            }
        }

        private static void MouseModifierForegroundLoss()
        {
            var clock = new VirtualClock();
            var input = new FakeInput(clock) { SwitchAfterMouseDown = true };
            using (var player = new AutoPlayer(input, clock))
            {
                player.Start(new[] { Note(61, 0, 2) }, 1, 60, 0);
                Await(delegate { return !player.Running; }, "模拟鼠标修饰按钮激活overlay后严格停止");
                var events = input.Snapshot();
                var mouseDown = Filter(events, true, true);
                Check(mouseDown.Count == 1 && mouseDown[0].Button == 2, "复现半音鼠标修饰先于音键导致前台切换的链条");
                Check(Filter(events, false, true).Count == 0 && player.SubmittedNoteInputs == 0,
                    "鼠标改变前台后不把后续音键发送给overlay或另一游戏窗口");
                Check(input.HeldCount == 0 && Filter(events, true, false).Count == 1,
                    "修饰按钮抢前台时仍释放本轮已按输入，不以关闭前台保护解决");
                foreach (Record action in events) if (action.Down) Check(action.Window == 1, "全部按下仍只提交给本轮原前台");
                int sent = events.Count; Thread.Sleep(25);
                Check(input.Snapshot().Count == sent, "鼠标引发终止后旧worker不继续输入");
                input.SwitchAfterMouseDown = false; input.Foreground = 1;
                player.Start(new[] { Note(61, 0, 0.1) }, 1, 60, 0);
                Await(delegate { return !player.Running; }, "取消鼠标抢前台后仍可重新正常演奏");
                Check(Filter(input.Snapshot(), false, true).Count == 1 && input.HeldCount == 0,
                    "原前台保持时鼠标修饰加音键流程继续有效，不替换或省略口琴指法");
            }
        }
        private static void PauseAndResume()
        {
            VirtualClock clock = new VirtualClock();
            FakeInput input = new FakeInput(clock);
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                player.Start(new[] { Note(61, 0, 4) }, 1, 60, 0);
                Await(delegate { return Filter(input.Snapshot(), false, true).Count == 1; }, "开始暂停测试");
                player.TogglePause();
                Check(player.Paused && input.HeldCount == 0, "暂停立即释放键鼠");
                double position = player.Position;
                input.Foreground = 2;
                Thread.Sleep(25);
                Check(player.Running && player.Paused, "暂停允许临时切出");
                Check(Math.Abs(player.Position - position) < 0.000001, "暂停冻结谱面位置");
                player.TogglePause();
                Check(player.Paused, "未恢复原窗口时拒绝继续");
                input.Foreground = 1;
                player.TogglePause();
                Await(delegate { return Filter(input.Snapshot(), false, true).Count >= 2; }, "原窗口恢复后继续当前音符");
                Check(!player.Paused, "恢复后退出暂停状态");
                player.Stop();
                Check(!player.Running && input.HeldCount == 0, "停止已恢复的乐曲时释放输入");
                int sent = input.Snapshot().Count;
                Thread.Sleep(25);
                Check(input.Snapshot().Count == sent, "Stop 使旧 worker 失效");
            }
        }

        private static void CountdownCancellation()
        {
            VirtualClock clock = new VirtualClock();
            FakeInput input = new FakeInput(clock);
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                player.Start(new[] { Note(60, 0, 0.1) }, 1, 60, 5);
                player.TogglePause();
                Thread.Sleep(25);
                Check(player.Paused && input.Snapshot().Count == 0, "倒计时暂停不输入键鼠");
                player.Stop();
                Thread.Sleep(25);
                Check(!player.Running && input.Snapshot().Count == 0, "取消倒计时不输入键鼠");
                player.Start(new[] { Note(60, 0, 0.1) }, 1, 60, 0);
                Await(delegate { return !player.Running; }, "取消后重新开始");
                Check(Filter(input.Snapshot(), false, true).Count == 1 && input.HeldCount == 0, "旧倒计时不会干扰新一轮演奏");
            }
        }

        private static void CountdownResume()
        {
            VirtualClock clock = new VirtualClock();
            FakeInput input = new FakeInput(clock);
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                player.Start(new[] { Note(61, 0, 2) }, 1, 60, 3);
                Check(player.CountingDown, "开始后明确处于倒计时状态");
                player.TogglePause();
                Thread.Sleep(25);
                Check(player.CountingDown && player.Paused && player.Position == 0 && input.Snapshot().Count == 0,
                    "准备暂停保持零位置且不采集目标或输入");
                input.Foreground = 2;
                long resumedAt = clock.Milliseconds;
                player.TogglePause();
                // Timer granularity varies across Windows hosts: the virtual
                // countdown still advances exactly 10 ms per worker wake.
                Await(delegate { return Filter(input.Snapshot(), false, true).Count == 1; }, "倒计时继续后首音", 10000);
                List<Record> keys = Filter(input.Snapshot(), false, true);
                Check(keys[0].Time >= resumedAt + 2950, "准备暂停经过的时间不扣掉剩余倒计时");
                Check(!player.CountingDown && !player.Paused && keys[0].Window == 2,
                    "继续准备可先切游戏窗口，倒计时结束才绑定当前前台");
                player.TogglePause();
                Check(input.HeldCount == 0 && player.Paused, "进入实际吹奏后同一控制释放长音键鼠");
                input.Foreground = 1;
                player.TogglePause();
                Check(player.Paused && input.HeldCount == 0, "绑定目标后继续仍严格要求原游戏前台");
                player.Stop();
                Check(!player.Running && !player.CountingDown && input.HeldCount == 0, "停止统一清除运行和倒计时状态");
            }
        }

        private static void RejectedWindowAndInput()
        {
            VirtualClock clock = new VirtualClock();
            FakeInput input = new FakeInput(clock);
            input.Allowed = false;
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                string status = null;
                player.StateChanged += delegate(string value) { status = value; bool running = player.Running; };
                player.Start(new[] { Note(60, 0, 1) }, 1, 60, 0);
                Await(delegate { return !player.Running; }, "无效目标窗口拒绝启动");
                Check(input.Snapshot().Count == 0, "自己的窗口或无效目标无输入");
                Await(delegate { return status != null; }, "拒绝窗口的中文状态");
                Check(status.Contains("未开始"), "窗口拒绝理由传给 UI");
            }
            clock = new VirtualClock();
            input = new FakeInput(clock);
            input.FailKeyboardDown = true;
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                player.Start(new[] { Note(61, 0, 1) }, 1, 60, 0);
                Await(delegate { return !player.Running; }, "SendInput 失败终止演奏");
                Check(input.Failures == 1 && input.HeldCount == 0, "输入失败仍释放先前鼠标修饰");
                Check(Filter(input.Snapshot(), true, false).Count == 1, "失败路径尝试释放鼠标");
            }
            clock = new VirtualClock();
            input = new FakeInput(clock);
            input.ThrowKeyboardDown = true;
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                player.Start(new[] { Note(61, 0, 1) }, 1, 60, 0);
                Await(delegate { return !player.Running; }, "输入后端异常被捕获");
                Check(input.Failures == 1 && input.HeldCount == 0, "后端抛出异常也尝试释放已有输入");
            }
        }

        private static void PreparationAfterCountdown()
        {
            VirtualClock clock = new VirtualClock { WaitScale = 50 };
            FakeInput input = new FakeInput(clock) { Foreground = 3 };
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                player.Start(new[] { Note(61, 0, 20) }, 1, 60, 1);
                Await(delegate { return player.WaitingForGame; }, "倒计时后保留切窗准备机会");
                Check(player.Running && !player.CountingDown && player.Position == 0, "等待前台不会提前推进乐谱或继续显示倒计时");
                Check(input.Snapshot().Count == 0 && player.SubmittedNoteInputs == 0, "本软件前台时没有演奏输入");
                input.Foreground = 0;
                Thread.Sleep(15);
                Check(player.WaitingForGame && input.Snapshot().Count == 0, "AltTab暂时无前台窗口时继续等待且不输入");
                input.Foreground = 2;
                Await(delegate { return player.SubmittedNoteInputs == 1; }, "晚于倒计时切回游戏也可开始");
                List<Record> keys = Filter(input.Snapshot(), false, true);
                Check(!player.WaitingForGame && !player.CountingDown && keys.Count == 1 && keys[0].Window == 2,
                    "准备结束只绑定此时确认的游戏前台");
                input.Foreground = 1;
                Await(delegate { return !player.Running; }, "准备完成后切出原窗口仍立即停止");
                Check(input.HeldCount == 0 && player.SubmittedNoteInputs == 1, "切出后释放输入且没有重绑定其他游戏窗口");
            }
        }

        private static void PreparationPause()
        {
            VirtualClock clock = new VirtualClock { WaitScale = 100 };
            FakeInput input = new FakeInput(clock) { Foreground = 3 };
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                player.Start(new[] { Note(60, 0, 20) }, 1, 60, 1);
                Await(delegate { return player.WaitingForGame; }, "准备等待可以暂停");
                player.TogglePause();
                long pausedAt = clock.Milliseconds;
                Await(delegate { return clock.Milliseconds >= pausedAt + 20000; }, "暂停经过大于等待时限的虚拟时间");
                input.Foreground = 2;
                Check(player.Running && player.Paused && player.WaitingForGame && player.Position == 0,
                    "暂停冻结等待时限并保持零乐谱位置");
                Check(input.Snapshot().Count == 0, "等待暂停即使游戏已在前台也不发送输入");
                player.TogglePause();
                Await(delegate { return player.SubmittedNoteInputs == 1; }, "等待恢复后继续捕获游戏而非错误超时");
                player.Stop();
                Check(!player.Running && !player.WaitingForGame && input.HeldCount == 0, "停止清除准备阶段和已按输入");
            }
        }

        private static void PreparationTimeoutAndCancellation()
        {
            VirtualClock clock = new VirtualClock { WaitScale = 100 };
            FakeInput input = new FakeInput(clock) { Foreground = 3 };
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                string status = null;
                player.StateChanged += delegate(string value) { status = value; };
                player.Start(new[] { Note(60, 0, 1) }, 1, 60, 1);
                Await(delegate { return !player.Running; }, "前台未切游戏时准备等待有明确期限");
                Await(delegate { return status != null && status.Contains("超时"); }, "等待到期中文说明");
                Check(clock.Milliseconds >= (1 + AutoPlayer.GameWindowWaitSeconds) * 1000,
                    "准备超时从倒计时结束后单独计算");
                Check(input.Snapshot().Count == 0 && player.SubmittedNoteInputs == 0 && !player.WaitingForGame,
                    "准备超时没有输入并清除等待状态");
                input.Foreground = 2;
                Thread.Sleep(20);
                Check(input.Snapshot().Count == 0, "超时后切入游戏不会意外自动开始");
            }

            clock = new VirtualClock { WaitScale = 50 };
            input = new FakeInput(clock) { Foreground = 3 };
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                player.Start(new[] { Note(61, 0, 20) }, 1, 60, 1);
                Await(delegate { return player.WaitingForGame; }, "准备等待可以取消");
                player.Stop();
                input.Foreground = 2;
                Thread.Sleep(20);
                Check(input.Snapshot().Count == 0 && !player.Running && !player.WaitingForGame,
                    "取消准备后旧worker不能捕获新前台或发送输入");
                player.Start(new[] { Note(60, 0, 1) }, 1, 60, 0);
                Await(delegate { return !player.Running; }, "取消准备后仍可正常开始下一轮");
                Check(Filter(input.Snapshot(), false, true).Count == 1 && player.SubmittedNoteInputs == 1 && input.HeldCount == 0,
                    "取消后只有新一轮的一个音符且结束释放");
            }
        }

        private static void PreparationPermissionFailure()
        {
            VirtualClock clock = new VirtualClock { WaitScale = 50 };
            FakeInput input = new FakeInput(clock) { RejectReason = "权限：游戏窗口权限高于本软件。" };
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                string status = null;
                player.StateChanged += delegate(string value) { status = value; };
                player.Start(new[] { Note(61, 0, 1) }, 1, 60, 1);
                Await(delegate { return !player.Running; }, "权限不满足立即拒绝而非等待切窗");
                Await(delegate { return status != null && status.Contains("权限："); }, "权限拒绝保留明确原因");
                Check(clock.Milliseconds < (1 + AutoPlayer.GameWindowWaitSeconds) * 1000,
                    "权限拒绝不浪费十五秒重试");
                Check(input.Snapshot().Count == 0 && player.SubmittedNoteInputs == 0 && !player.WaitingForGame,
                    "权限拒绝没有任何演奏输入");
            }
        }

        private static void CleanupFailure()
        {
            VirtualClock clock = new VirtualClock();
            FakeInput input = new FakeInput(clock);
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                string status = null;
                player.StateChanged += delegate(string value) { status = value; };
                player.Start(new[] { Note(49, 0, 4) }, 1, 60, 0);
                Await(delegate { return Filter(input.Snapshot(), false, true).Count == 1; }, "准备释放失败测试");
                input.FailNextRelease = true;
                player.Stop();
                Check(status != null && status.Contains("释放失败"), "释放失败向用户报告");
                Check(Filter(input.Snapshot(), true, false).Count == 2, "一个释放失败仍尝试其他键鼠释放");
                Check(input.HeldCount == 1, "未释放成功的输入保留待重试状态");
                player.Stop();
                Check(input.HeldCount == 0, "再次 Stop 重试释放失败的输入");
            }
        }

        private static void OutOfRangeRest()
        {
            VirtualClock clock = new VirtualClock();
            FakeInput input = new FakeInput(clock);
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                string message = null;
                try { player.Start(new[] { Note(30, 0, 1), Note(100, 1, 1) }, 1, 60, 0); }
                catch (ArgumentException ex) { message = ex.Message; }
                Check(message != null && message.Contains("全部超出口琴音域"), "全部超音域时明确拒绝而非静默休止完成");
                Check(!player.Running && input.Snapshot().Count == 0 && player.SubmittedNoteInputs == 0,
                    "全部超音域校验在倒计时和任何输入之前完成");
                player.Start(new[] { Note(30, 0, 0.02), Note(60, 0.02, 0.05) }, 1, 60, 0);
                Await(delegate { return !player.Running; }, "超音域按休止处理");
                List<Record> keys = Filter(input.Snapshot(), false, true);
                Check(keys.Count == 1 && keys[0].Key == "Z", "超音域音符不发送音键");
                Check(keys[0].Time >= 20, "超音域音符仍保留谱面时间");
                Check(input.HeldCount == 0, "休止曲目结束释放全部输入");
            }
        }

        private static NoteEvent Note(int pitch, double start, double duration)
        {
            return new NoteEvent { Pitch = pitch, Start = start, Duration = duration, Velocity = 90 };
        }

        private static void NotatedRestsReleaseModifiers()
        {
            VirtualClock clock = new VirtualClock();
            FakeInput input = new FakeInput(clock);
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                // Low sharp needs two mouse buttons; the next middle sharp needs
                // only the wheel button. Neither may remain held through a rest.
                player.Start(new[] { Note(49, 0, 0.2), Note(61, 1.2, 0.2) }, 1, 60, 0);
                Await(delegate { return player.Position >= 0.4; }, "进入真实谱面休止");
                Check(player.Running && input.HeldCount == 0, "真实休止期间全部松开音键和鼠标修饰");
                List<Record> restEvents = input.Snapshot();
                Check(Filter(restEvents, false, true).Count == 1 && Filter(restEvents, true, false).Count == 2,
                    "休止从上一音结束就释放两种修饰，不等待下次起音");
                player.TogglePause();
                double position = player.Position;
                int sent = restEvents.Count;
                Thread.Sleep(20);
                Check(input.HeldCount == 0 && input.Snapshot().Count == sent && Math.Abs(player.Position - position) < 0.000001,
                    "休止内暂停保持零输入并冻结谱面时间");
                player.TogglePause();
                Await(delegate { return !player.Running; }, "休止恢复后继续下一音并完成");
                List<Record> events = input.Snapshot();
                List<Record> keys = Filter(events, false, true), ups = Filter(events, false, false);
                List<Record> modifiers = Filter(events, true, true);
                Check(keys.Count == 2 && modifiers.Count == 3 && modifiers[2].Button == 2,
                    "休止后只重新按当前音所需修饰且没有增添音符");
                Check(modifiers[2].Time - ups[0].Time >= 900 && keys[1].Time - modifiers[2].Time >= 35,
                    "真实休止完整保留，下一音仍满足鼠标修饰提前量");
                Check(input.HeldCount == 0 && Math.Abs(player.Position - 1.4) < 0.000001,
                    "有休止的曲目按原末音结束且没有遗留输入");
            }
            clock = new VirtualClock(); input = new FakeInput(clock);
            using (AutoPlayer player = new AutoPlayer(input, clock))
            {
                player.Start(new[] { Note(61, 0, 0.1), Note(61, 0.13, 0.1) }, 1.5, 60, 0);
                Await(delegate { return !player.Running; }, "快速短休止的连续指法正常完成");
                List<Record> events = input.Snapshot();
                Check(Filter(events, true, true).Count == 1 && Filter(events, false, true).Count == 2,
                    "不足修饰准备时间的短休止不反复按放鼠标而额外拉长节奏");
                Check(input.HeldCount == 0 && Math.Abs(player.Position - 0.23) < 0.000001,
                    "快速短休止也保留原谱末音与完整释放");
            }
        }
        private static void WindowClassification()
        {
            string reason;
            string[] rejected = { "Chrome_WidgetWin_1", "Chrome_WidgetWin_0", "MozillaWindowClass", "CabinetWClass", "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "WindowsForms10.Window.8.app.0", "Notepad", "Notepad++", "" };
            foreach (string value in rejected)
                Check(!AutoPlayer.ClassifyGameWindow("DeltaForce 三角洲 GitHub - 浏览器", value, out reason) && reason != null, "拒绝标题含游戏名的桌面窗口：" + value);
            Check(AutoPlayer.ClassifyGameWindow("三角洲行动", "UnrealWindow", out reason), "三角洲标题与非桌面类别");
            Check(AutoPlayer.ClassifyGameWindow("Delta Force", "UnrealWindow", out reason), "Delta Force 标题");
            Check(!AutoPlayer.ClassifyGameWindow("其他游戏", "UnrealWindow", out reason), "标题不匹配时拒绝");
        }
        private static List<Record> Filter(List<Record> values, bool mouse, bool down)
        {
            List<Record> output = new List<Record>();
            foreach (Record value in values) if (value.Mouse == mouse && value.Down == down) output.Add(value);
            return output;
        }
        private static void Await(Func<bool> condition, string name, int timeoutMilliseconds = 3000)
        {
            Stopwatch limit = Stopwatch.StartNew();
            while (!condition())
            {
                if (limit.ElapsedMilliseconds > timeoutMilliseconds) throw new Exception(name + "超时。");
                Thread.Sleep(1);
            }
            Check(true, name);
        }
        private static void Check(bool condition, string name)
        {
            Checks++;
            if (!condition) throw new Exception("自动演奏测试失败：" + name);
        }

        private sealed class VirtualClock : IPlaybackClock
        {
            private long Now;
            public int WaitScale = 1;
            public long Milliseconds { get { return Interlocked.Read(ref Now); } }
            public void Wait(int milliseconds) { Interlocked.Add(ref Now, milliseconds * WaitScale); Thread.Sleep(1); }
        }
        private sealed class Record
        {
            public bool Mouse, Down;
            public int Button, Window;
            public string Key;
            public long Time;
        }
        private sealed class FakeInput : IInputBackend
        {
            private readonly object Gate = new object();
            private readonly VirtualClock Clock;
            private readonly List<Record> Events = new List<Record>();
            private readonly HashSet<string> Held = new HashSet<string>();
            public volatile int Foreground = 1;
            public volatile bool Allowed = true, SwitchAfterKeyboardDown, SwitchAfterMouseDown, FailKeyboardDown, ThrowKeyboardDown, FailNextRelease;
            public string RejectReason;
            public int Failures;
            public int HeldCount { get { lock (Gate) return Held.Count; } }
            public FakeInput(VirtualClock clock) { Clock = clock; }
            public IntPtr GetForegroundWindow() { return new IntPtr(Foreground); }
            public bool IsGameWindow(IntPtr window, out string reason)
            {
                reason = RejectReason ?? (Allowed && (window == new IntPtr(1) || window == new IntPtr(2)) ? null : "前台窗口不是有效外部游戏窗口。");
                return reason == null;
            }
            public bool Send(InputAction action, out string reason)
            {
                lock (Gate)
                {
                    if (FailNextRelease && !action.Down)
                    {
                        FailNextRelease = false;
                        Failures++;
                        reason = "模拟系统暂时拒绝释放输入。";
                        return false;
                    }
                    if (ThrowKeyboardDown && !action.Mouse && action.Down)
                    {
                        Failures++;
                        throw new InvalidOperationException("模拟输入后端异常。");
                    }
                    if (FailKeyboardDown && !action.Mouse && action.Down)
                    {
                        Failures++;
                        reason = "模拟系统拒绝按键输入。";
                        return false;
                    }
                    Events.Add(new Record { Mouse = action.Mouse, Down = action.Down, Button = action.Button, Key = action.Key, Time = Clock.Milliseconds, Window = Foreground });
                    string id = action.Mouse ? "鼠标" + action.Button : "按键" + action.Key;
                    if (action.Down) Held.Add(id); else Held.Remove(id);
                    if (SwitchAfterKeyboardDown && !action.Mouse && action.Down) Foreground = 2;
                    if (SwitchAfterMouseDown && action.Mouse && action.Down) Foreground = 2;
                    reason = null;
                    return true;
                }
            }
            public List<Record> Snapshot() { lock (Gate) return new List<Record>(Events); }
        }
    }
}
