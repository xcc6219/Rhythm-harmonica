using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace HarmonicaStudio
{
    internal sealed class InputAction
    {
        public bool Mouse, Down;
        public int Button;
        public string Key;
        public static InputAction Keyboard(string key, bool down) { return new InputAction { Key = key, Down = down }; }
        public static InputAction MouseButton(int button, bool down) { return new InputAction { Mouse = true, Button = button, Down = down }; }
    }

    internal interface IInputBackend
    {
        IntPtr GetForegroundWindow();
        bool IsGameWindow(IntPtr window, out string reason);
        bool Send(InputAction action, out string reason);
    }

    internal interface IPlaybackClock
    {
        long Milliseconds { get; }
        void Wait(int milliseconds);
    }

    public sealed class AutoPlayer : IDisposable
    {
        private readonly object Gate = new object();
        private readonly IInputBackend Input;
        private readonly IPlaybackClock Clock;
        private readonly HashSet<string> HeldKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<int> HeldButtons = new HashSet<int>();
        private long Generation, ReleaseVersion, LastClock, PlaybackSessionId;
        private bool IsRunning, IsPaused, Playing, AwaitingTarget, Disposed;
        private int SubmittedNotes;
        private IntPtr Target;
        private double ActiveMilliseconds, DelayMilliseconds, SongDuration, Speed, SongPosition;
        private double ModifierReadyMilliseconds, PositionLimit;
        private string CompletionMessage;

        public event Action<string> StateChanged;
        internal event Action<long, string> SessionStateChanged;
        public bool Running { get { lock (Gate) return IsRunning; } }
        public bool Paused { get { lock (Gate) return IsPaused; } }
        public bool CountingDown { get { lock (Gate) return IsRunning && Target == IntPtr.Zero && !AwaitingTarget; } }
        public bool WaitingForGame { get { lock (Gate) return IsRunning && AwaitingTarget; } }
        public int SubmittedNoteInputs { get { lock (Gate) return SubmittedNotes; } }
        public double Position { get { lock (Gate) { AdvanceClock(); return SongPosition; } } }
        internal string LastCompletionMessage { get { lock (Gate) return CompletionMessage; } }
        internal long SessionId { get { lock (Gate) return PlaybackSessionId; } }
        internal static int NativeInputSize { get { return Marshal.SizeOf(typeof(WinInput)); } }
        internal const int GameWindowWaitSeconds = 15;

        public AutoPlayer() : this(new SystemInput(), new PlaybackClock()) { }
        internal AutoPlayer(IInputBackend input, IPlaybackClock clock)
        {
            if (input == null || clock == null) throw new ArgumentNullException("自动演奏后端和时钟不能为空。");
            Input = input;
            Clock = clock;
            LastClock = Clock.Milliseconds;
        }

        public void Start(IList<NoteEvent> notes, double speed, int basePitch, int countdownSeconds)
        {
            StartCore(notes, speed, basePitch, countdownSeconds, null);
        }

        public void Start(IList<NoteEvent> notes, double speed, int basePitch, int countdownSeconds, double duration)
        {
            StartCore(notes, speed, basePitch, countdownSeconds, duration);
        }

        private void StartCore(IList<NoteEvent> notes, double speed, int basePitch, int countdownSeconds, double? requestedDuration)
        {
            if (notes == null || notes.Count == 0) throw new ArgumentException("请先选择包含音符的乐谱。", "notes");
            if (notes.Count > ScoreIO.MaximumNotes) throw new ArgumentException("乐谱超过 200000 个音符。", "notes");
            if (!Finite(speed) || speed <= 0 || speed > 8) throw new ArgumentOutOfRangeException("speed", "速度倍率必须大于 0 且不超过 8。");
            if (basePitch < 0 || basePitch > 127) throw new ArgumentOutOfRangeException("basePitch", "基准音高必须在 0 到 127 之间。");
            if (countdownSeconds < 0 || countdownSeconds > 30) throw new ArgumentOutOfRangeException("countdownSeconds", "倒计时必须在 0 到 30 秒之间。");
            List<NoteEvent> copy = new List<NoteEvent>(notes.Count);
            double duration = 0, previousEnd = 0;
            bool hasPlayableNote = false;
            foreach (NoteEvent note in notes)
            {
                if (note == null || note.Pitch < 0 || note.Pitch > 127 || !Finite(note.Start) || !Finite(note.Duration) || note.Start < 0 || note.Duration <= 0 || note.Start + note.Duration > ScoreIO.MaximumDurationSeconds)
                    throw new ArgumentException("乐谱包含无效音符或超过 4 小时的时长。", "notes");
                if (copy.Count > 0 && note.Start < previousEnd - 0.0000001)
                    throw new ArgumentException("自动演奏需要按时间排序的单声部乐谱，请先选择旋律轨并归约。", "notes");
                copy.Add(new NoteEvent { Pitch = note.Pitch, Start = note.Start, Duration = note.Duration, Velocity = note.Velocity });
                if (HarmonicaMap.Map(note.Pitch, basePitch).Playable) hasPlayableNote = true;
                previousEnd = note.Start + note.Duration;
                duration = Math.Max(duration, previousEnd);
            }
            if (requestedDuration.HasValue)
            {
                if (!Finite(requestedDuration.Value) || requestedDuration.Value <= 0 ||
                    requestedDuration.Value > ScoreIO.MaximumDurationSeconds || requestedDuration.Value < duration - 0.0000001)
                    throw new ArgumentOutOfRangeException("duration", "播放时长必须覆盖全部音符，且不能超过 4 小时。");
                duration = Math.Max(duration, requestedDuration.Value);
            }
            if (!hasPlayableNote)
                throw new ArgumentException("当前乐谱全部超出口琴音域，无法自动吹奏；请调整移调、基准音高，或选择可吹奏的旋律轨。", "notes");
            long generation;
            lock (Gate)
            {
                if (Disposed) throw new ObjectDisposedException("AutoPlayer");
                AdvanceClock();
                IsRunning = false;
                IsPaused = false;
                Playing = false;
                AwaitingTarget = false;
                Generation++;
                string releaseError = ReleaseAll();
                if (releaseError != null) throw new InvalidOperationException("无法开始：上一次输入未能全部释放。" + releaseError);
                generation = ++Generation;
                PlaybackSessionId = generation;
                ReleaseVersion++;
                Target = IntPtr.Zero;
                SubmittedNotes = 0;
                CompletionMessage = null;
                ActiveMilliseconds = DelayMilliseconds = ModifierReadyMilliseconds = SongPosition = PositionLimit = 0;
                SongDuration = duration;
                Speed = speed;
                LastClock = Clock.Milliseconds;
                IsRunning = true;
            }
            Thread worker = new Thread(new ThreadStart(delegate { Run(generation, copy, speed, basePitch, countdownSeconds, duration); }));
            worker.IsBackground = true;
            worker.Name = "HarmonicaAutoPlayer";
            worker.Start();
        }

        public void TogglePause()
        {
            string message;
            long generation;
            lock (Gate)
            {
                if (!IsRunning || Disposed) return;
                generation = PlaybackSessionId;
                AdvanceClock();
                if (!IsPaused)
                {
                    IsPaused = true;
                    ReleaseVersion++;
                    string error = ReleaseAll();
                    if (error != null) message = AbortLocked(Generation, "暂停时释放输入失败。" + error);
                    else message = Target == IntPtr.Zero ? (AwaitingTarget ? "等待游戏窗口已暂停。" : "倒计时已暂停。") : "已暂停；请回到原游戏窗口后继续。";
                }
                else
                {
                    if (Target != IntPtr.Zero && Input.GetForegroundWindow() != Target)
                        message = "仍处于暂停：请先将原来的三角洲窗口切回前台。";
                    else
                    {
                        IsPaused = false;
                        LastClock = Clock.Milliseconds;
                        message = Target == IntPtr.Zero ? (AwaitingTarget ? "继续等待游戏窗口。" : "倒计时继续。") : "自动演奏继续。";
                    }
                }
            }
            Publish(message, generation);
        }

        public void Stop()
        {
            string message = null;
            long generation;
            lock (Gate)
            {
                generation = PlaybackSessionId;
                AdvanceClock();
                bool hadSession = IsRunning || HeldKeys.Count > 0 || HeldButtons.Count > 0;
                Generation++;
                ReleaseVersion++;
                IsRunning = IsPaused = Playing = AwaitingTarget = false;
                string error = ReleaseAll();
                Target = IntPtr.Zero;
                if (hadSession) message = error == null ? "自动演奏已停止。" : "已停止；部分输入释放失败。" + error;
                if (message != null) CompletionMessage = message;
            }
            Publish(message, generation);
        }

        public void Dispose()
        {
            lock (Gate)
            {
                if (Disposed) return;
                Disposed = true;
            }
            Stop();
        }

        private void Run(long generation, List<NoteEvent> notes, double speed, int basePitch, int countdown, double duration)
        {
            try
            {
                if (!Countdown(generation, countdown)) return;
                if (!CaptureGameWindow(generation, countdown > 0)) return;
                string message = null;
                if (!CurrentThreadSafe(generation)) return;
                double delay = 0, lastKeyUp = -1000;
                string lastKey = null;
                for (int noteIndex = 0; noteIndex < notes.Count; noteIndex++)
                {
                    NoteEvent note = notes[noteIndex];
                    SetPositionLimit(generation, note.Start);
                    Fingering fingering = HarmonicaMap.Map(note.Pitch, basePitch);
                    double scheduled = note.Start * 1000 / speed + delay;
                    // Key-up alone leaves the mouse modifiers held. Release them
                    // for a real rest with enough time to prepare the next note;
                    // adjacent notes keep their existing modifier timing.
                    if (noteIndex > 0 && note.Start > notes[noteIndex - 1].Start + notes[noteIndex - 1].Duration + 0.0000001 &&
                        scheduled - ActiveTime() > 35 && !ReleaseForRest(generation)) return;
                    if (!fingering.Playable)
                    {
                        if (WaitUntil(generation, scheduled, -1) == WaitResult.Cancelled) return;
                        if (!ReleaseForRest(generation)) return;
                        double now = ActiveTime();
                        delay += Math.Max(0, now - scheduled);
                        SetDelay(generation, delay);
                        SetPositionLimit(generation, note.Start + note.Duration);
                        if (WaitUntil(generation, now + note.Duration * 1000 / speed, -1) == WaitResult.Cancelled) return;
                        continue;
                    }
                    double pressedAt;
                    long version;
                    if (!PressNote(generation, fingering, scheduled, lastKey == fingering.Key ? lastKeyUp : -1000, out pressedAt, out version)) return;
                    delay += Math.Max(0, pressedAt - scheduled);
                    SetDelay(generation, delay);
                    SetPositionLimit(generation, note.Start + note.Duration);
                    double hold = note.Duration * 1000 / speed;
                    if (noteIndex + 1 < notes.Count)
                    {
                        NoteEvent next = notes[noteIndex + 1];
                        Fingering nextFingering = HarmonicaMap.Map(next.Pitch, basePitch);
                        if (nextFingering.Playable)
                        {
                            double preparation = fingering.Key == nextFingering.Key ? 20 : 0;
                            if (fingering.Modifier != nextFingering.Modifier) preparation = Math.Max(preparation, 35);
                            // Use the notated rest first; only release a held note
                            // early when the next attack needs more preparation.
                            hold = Math.Min(hold, (next.Start - note.Start) * 1000 / speed - preparation);
                        }
                    }
                    double end = pressedAt + Math.Max(45, hold);
                    while (true)
                    {
                        WaitResult result = WaitUntil(generation, end, version);
                        if (result == WaitResult.Cancelled) return;
                        if (result == WaitResult.Reached) break;
                        double resumeFrom = ActiveTime();
                        if (resumeFrom >= end) break;
                        double resumedAt;
                        if (!PressNote(generation, fingering, resumeFrom, resumeFrom, out resumedAt, out version)) return;
                        double adjustment = Math.Max(0, resumedAt - resumeFrom);
                        end = Math.Max(end + adjustment, resumedAt + 45);
                        delay += adjustment;
                        SetDelay(generation, delay);
                    }
                    if (!ReleaseNote(generation, fingering.Key, out lastKeyUp)) return;
                    lastKey = fingering.Key;
                }
                if (duration > notes[notes.Count - 1].Start + notes[notes.Count - 1].Duration + 0.0000001)
                {
                    if (!ReleaseForRest(generation)) return;
                    SetPositionLimit(generation, duration);
                    if (WaitUntil(generation, duration * 1000 / speed + delay, -1) == WaitResult.Cancelled) return;
                }
                lock (Gate)
                {
                    if (!Current(generation)) return;
                    AdvanceClock();
                    string error = ReleaseAll();
                    IsRunning = IsPaused = Playing = AwaitingTarget = false;
                    Target = IntPtr.Zero;
                    Generation++;
                    if (error == null) SongPosition = SongDuration;
                    message = error == null ? "自动演奏完成。" : "演奏结束；部分输入释放失败。" + error;
                    CompletionMessage = message;
                }
                Publish(message, generation);
            }
            catch (Exception ex)
            {
                string message;
                lock (Gate) message = AbortLocked(generation, "自动演奏停止：" + ex.Message);
                Publish(message, generation);
            }
        }

        private bool Countdown(long generation, int seconds)
        {
            double remaining = seconds * 1000;
            long last = Clock.Milliseconds;
            int announced = -1;
            while (true)
            {
                string message = null;
                bool done;
                lock (Gate)
                {
                    if (!Current(generation)) return false;
                    long now = Clock.Milliseconds;
                    if (!IsPaused) remaining -= Math.Max(0, now - last);
                    last = now;
                    done = !IsPaused && remaining <= 0;
                    int value = Math.Max(0, (int)Math.Ceiling(remaining / 1000));
                    if (!IsPaused && value > 0 && value != announced)
                    {
                        announced = value;
                        message = "倒计时 " + value + " 秒，请切换到三角洲窗口。";
                    }
                }
                Publish(message, generation);
                if (done) return true;
                Clock.Wait(10);
            }
        }

        private bool CaptureGameWindow(long generation, bool allowWait)
        {
            double remaining = GameWindowWaitSeconds * 1000;
            long last = Clock.Milliseconds;
            int announced = -1;
            while (true)
            {
                string message = null;
                bool captured = false;
                lock (Gate)
                {
                    if (!Current(generation)) return false;
                    long now = Clock.Milliseconds;
                    if (!IsPaused) remaining -= Math.Max(0, now - last);
                    last = now;
                    if (!IsPaused)
                    {
                        string reason = null;
                        IntPtr target = IntPtr.Zero;
                        if (allowWait && AwaitingTarget && remaining <= 0)
                            message = AbortLocked(generation, "未开始：等待三角洲游戏窗口超时，请切回游戏后重新开始。");
                        else if (Input.IsGameWindow(target = Input.GetForegroundWindow(), out reason))
                        {
                            Target = target;
                            AwaitingTarget = false;
                            Playing = true;
                            LastClock = Clock.Milliseconds;
                            captured = true;
                            message = "正在向游戏前台发送演奏输入；切出目标窗口会立即停止。";
                        }
                        else if (!allowWait || (reason != null && reason.StartsWith("权限：", StringComparison.Ordinal)))
                            message = AbortLocked(generation, "未开始：" + reason);
                        else if (remaining <= 0)
                            message = AbortLocked(generation, "未开始：等待三角洲游戏窗口超时，请切回游戏后重新开始。" + reason);
                        else
                        {
                            AwaitingTarget = true;
                            int seconds = (int)Math.Ceiling(remaining / 1000);
                            if (seconds != announced)
                            {
                                announced = seconds;
                                message = "等待游戏窗口（剩余 " + seconds + " 秒）；切回三角洲后开始，当前未发送演奏输入。";
                            }
                        }
                    }
                }
                Publish(message, generation);
                if (captured) return CurrentThreadSafe(generation);
                if (!CurrentThreadSafe(generation)) return false;
                Clock.Wait(10);
            }
        }

        private enum WaitResult { Reached, Released, Cancelled }
        private WaitResult WaitUntil(long generation, double deadline, long watchVersion)
        {
            while (true)
            {
                string message;
                bool paused, reached, released;
                int waitMilliseconds = 10;
                lock (Gate)
                {
                    if (!CheckSession(generation, out message)) { paused = reached = released = false; }
                    else
                    {
                        paused = IsPaused;
                        released = watchVersion >= 0 && watchVersion != ReleaseVersion;
                        reached = !paused && ActiveMilliseconds >= deadline - 0.0000001;
                        if (!paused && !reached)
                            waitMilliseconds = (int)Math.Max(1, Math.Min(10, Math.Ceiling(deadline - ActiveMilliseconds - 0.0000001)));
                    }
                }
                if (message != null) { Publish(message, generation); return WaitResult.Cancelled; }
                if (!CurrentThreadSafe(generation)) return WaitResult.Cancelled;
                if (released) return WaitResult.Released;
                if (reached) return WaitResult.Reached;
                Clock.Wait(waitMilliseconds);
            }
        }

        private bool PressNote(long generation, Fingering fingering, double scheduled, double sameKeyUp, out double pressedAt, out long version)
        {
            pressedAt = 0;
            version = 0;
            while (true)
            {
                if (WaitUntil(generation, Math.Max(0, scheduled - 35), -1) == WaitResult.Cancelled) return false;
                string message = null;
                double ready = 0;
                long expectedVersion = 0;
                bool retry = false;
                lock (Gate)
                {
                    if (CheckSession(generation, out message))
                    {
                        if (IsPaused) retry = true;
                        else
                        {
                            if (!SetModifiers(generation, fingering, out message)) { }
                            else
                            {
                                ready = Math.Max(scheduled, Math.Max(ModifierReadyMilliseconds, sameKeyUp + 20));
                                expectedVersion = ReleaseVersion;
                            }
                        }
                    }
                }
                if (message != null) { Publish(message, generation); return false; }
                if (!CurrentThreadSafe(generation)) return false;
                if (retry) continue;
                WaitResult wait = WaitUntil(generation, ready, expectedVersion);
                if (wait == WaitResult.Cancelled) return false;
                if (wait == WaitResult.Released) continue;
                bool sent = false;
                lock (Gate)
                {
                    if (CheckSession(generation, out message))
                    {
                        if (!IsPaused && expectedVersion == ReleaseVersion)
                        {
                            if (SendOne(InputAction.Keyboard(fingering.Key, true), out message))
                            {
                                AdvanceClock();
                                pressedAt = ActiveMilliseconds;
                                version = ReleaseVersion;
                                sent = true;
                            }
                            else message = AbortLocked(generation, "按键输入失败。" + message);
                        }
                    }
                }
                if (message != null) { Publish(message, generation); return false; }
                if (sent) return true;
                if (!CurrentThreadSafe(generation)) return false;
            }
        }

        private bool SetModifiers(long generation, Fingering fingering, out string message)
        {
            message = null;
            HashSet<int> desired = new HashSet<int>();
            if (fingering.Modifier.Contains("左键")) desired.Add(0);
            if (fingering.Modifier.Contains("右键")) desired.Add(1);
            if (fingering.Modifier.Contains("中键")) desired.Add(2);
            bool changed = false;
            foreach (int button in new List<int>(HeldButtons))
                if (!desired.Contains(button))
                {
                    if (!SendOne(InputAction.MouseButton(button, false), out message))
                    {
                        message = AbortLocked(generation, "鼠标修饰键释放失败。" + message);
                        return false;
                    }
                    changed = true;
                }
            foreach (int button in desired)
                if (!HeldButtons.Contains(button))
                {
                    if (!SendOne(InputAction.MouseButton(button, true), out message))
                    {
                        message = AbortLocked(generation, "鼠标修饰键输入失败。" + message);
                        return false;
                    }
                    changed = true;
                }
            if (changed) { AdvanceClock(); ModifierReadyMilliseconds = ActiveMilliseconds + 35; }
            return true;
        }

        private bool ReleaseNote(long generation, string key, out double releasedAt)
        {
            string message;
            bool success = false;
            releasedAt = 0;
            lock (Gate)
            {
                if (CheckSession(generation, out message))
                {
                    if (!HeldKeys.Contains(key) || SendOne(InputAction.Keyboard(key, false), out message))
                    {
                        AdvanceClock();
                        releasedAt = ActiveMilliseconds;
                        success = true;
                    }
                    else message = AbortLocked(generation, "按键释放失败。" + message);
                }
            }
            Publish(message, generation);
            return success;
        }

        private bool ReleaseForRest(long generation)
        {
            string message;
            bool success = false;
            lock (Gate)
            {
                if (CheckSession(generation, out message))
                {
                    ReleaseVersion++;
                    string error = ReleaseAll();
                    if (error == null) success = true;
                    else message = AbortLocked(generation, "休止时释放输入失败。" + error);
                }
            }
            Publish(message, generation);
            return success;
        }

        private bool SendOne(InputAction action, out string reason)
        {
            try
            {
                if (action.Down && (!IsRunning || IsPaused || !Playing || Target == IntPtr.Zero || Input.GetForegroundWindow() != Target))
                {
                    reason = "目标游戏窗口已不在前台，本次按下输入已取消。";
                    return false;
                }
                if (action.Down && !Input.IsGameWindow(Target, out reason)) return false;
                if (!Input.Send(action, out reason)) return false;
                if (action.Mouse)
                {
                    if (action.Down) HeldButtons.Add(action.Button); else HeldButtons.Remove(action.Button);
                }
                else
                {
                    if (action.Down)
                    {
                        HeldKeys.Add(action.Key);
                        if (SubmittedNotes < Int32.MaxValue) SubmittedNotes++;
                    }
                    else HeldKeys.Remove(action.Key);
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = "键鼠输入接口异常：" + ex.Message;
                return false;
            }
        }

        private string ReleaseAll()
        {
            string error = null, reason;
            foreach (string key in new List<string>(HeldKeys))
                if (!SendOne(InputAction.Keyboard(key, false), out reason)) error = reason;
            foreach (int button in new List<int>(HeldButtons))
                if (!SendOne(InputAction.MouseButton(button, false), out reason)) error = reason;
            ModifierReadyMilliseconds = 0;
            return error;
        }

        private bool CheckSession(long generation, out string message)
        {
            message = null;
            if (!Current(generation)) return false;
            AdvanceClock();
            if (!IsPaused && Target != IntPtr.Zero && Input.GetForegroundWindow() != Target)
            {
                message = AbortLocked(generation, "已停止：目标游戏窗口已不在前台。");
                return false;
            }
            return true;
        }

        private string AbortLocked(long generation, string reason)
        {
            if (!Current(generation)) return null;
            AdvanceClock();
            Generation++;
            ReleaseVersion++;
            IsRunning = IsPaused = Playing = AwaitingTarget = false;
            Target = IntPtr.Zero;
            string error = ReleaseAll();
            CompletionMessage = reason + (error == null ? "" : "部分输入释放失败：" + error);
            return CompletionMessage;
        }
        private bool Current(long generation) { return !Disposed && IsRunning && generation == Generation; }
        private bool CurrentThreadSafe(long generation) { lock (Gate) return Current(generation); }
        private double ActiveTime() { lock (Gate) { AdvanceClock(); return ActiveMilliseconds; } }
        private void SetDelay(long generation, double delay)
        {
            lock (Gate) if (Current(generation)) { DelayMilliseconds = delay; AdvanceClock(); }
        }
        private void SetPositionLimit(long generation, double position)
        {
            lock (Gate) if (Current(generation)) { PositionLimit = position; AdvanceClock(); }
        }
        private void AdvanceClock()
        {
            long now = Clock.Milliseconds;
            if (IsRunning && Playing && !IsPaused)
            {
                ActiveMilliseconds += Math.Max(0, now - LastClock);
                double position = Math.Max(0, (ActiveMilliseconds - DelayMilliseconds) * Speed / 1000);
                SongPosition = Math.Min(PositionLimit, Math.Min(SongDuration, Math.Max(SongPosition, position)));
            }
            LastClock = now;
        }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        internal static bool ClassifyGameWindow(string title, string className, out string reason)
        {
            reason = null;
            if (String.IsNullOrWhiteSpace(className)) reason = "无法读取前台窗口类别，已取消自动演奏。";
            else
            {
                string[] desktopClasses =
                {
                    "Chrome_WidgetWin_1", "Chrome_WidgetWin_0", "MozillaWindowClass", "CabinetWClass",
                    "ExploreWClass", "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "Notepad",
                    "Notepad++", "ApplicationFrameWindow", "WinUIDesktopWin32WindowClass"
                };
                foreach (string value in desktopClasses)
                    if (String.Equals(className, value, StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "前台是浏览器、终端或其他桌面窗口，已取消自动演奏。";
                        break;
                    }
                if (reason == null && (className.StartsWith("WindowsForms10", StringComparison.OrdinalIgnoreCase) || className.StartsWith("HwndWrapper[", StringComparison.OrdinalIgnoreCase)))
                    reason = "前台是桌面应用窗口，已取消自动演奏。";
                if (reason == null && (String.IsNullOrEmpty(title) || (title.IndexOf("三角洲", StringComparison.OrdinalIgnoreCase) < 0 && title.IndexOf("Delta Force", StringComparison.OrdinalIgnoreCase) < 0 && title.IndexOf("DeltaForce", StringComparison.OrdinalIgnoreCase) < 0)))
                    reason = "前台窗口标题不包含“三角洲”或“Delta Force”，已取消自动演奏。";
            }
            return reason == null;
        }
        private void Publish(string message, long sessionId)
        {
            if (message == null) return;
            Action<long, string> sessionHandlers = SessionStateChanged;
            if (sessionHandlers != null)
                foreach (Action<long, string> handler in sessionHandlers.GetInvocationList())
                    try { handler(sessionId, message); } catch (Exception) { }
            Action<string> handlers = StateChanged;
            if (handlers == null) return;
            foreach (Action<string> handler in handlers.GetInvocationList())
                try { handler(message); } catch (Exception) { }
        }

        private sealed class PlaybackClock : IPlaybackClock
        {
            private readonly Stopwatch Timer = Stopwatch.StartNew();
            public long Milliseconds { get { return Timer.ElapsedMilliseconds; } }
            public void Wait(int milliseconds) { Thread.Sleep(milliseconds); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinInput { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
            [FieldOffset(0)] public HardwareInput Hardware;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        private struct HardwareInput { public uint Message; public ushort Low, High; }

        private sealed class SystemInput : IInputBackend
        {
            private readonly uint OwnProcessId;
            public SystemInput()
            {
                int expected = IntPtr.Size == 8 ? 40 : 28;
                if (NativeInputSize != expected) throw new InvalidOperationException("Windows INPUT 结构尺寸无效，无法启用自动输入。");
                using (Process process = Process.GetCurrentProcess()) OwnProcessId = (uint)process.Id;
            }
            public IntPtr GetForegroundWindow() { return NativeGetForegroundWindow(); }
            public bool IsGameWindow(IntPtr window, out string reason)
            {
                reason = null;
                if (window == IntPtr.Zero || !IsWindow(window) || !IsWindowVisible(window)) reason = "前台没有有效的游戏窗口。";
                else
                {
                    uint process;
                    if (GetWindowThreadProcessId(window, out process) == 0 || process == 0 || process == OwnProcessId)
                        reason = "不能向本软件窗口或无效窗口发送输入。";
                    else
                    {
                        StringBuilder title = new StringBuilder(512);
                        GetWindowText(window, title, title.Capacity);
                        StringBuilder className = new StringBuilder(256);
                        if (GetClassName(window, className, className.Capacity) == 0) reason = "无法读取前台窗口类别，已取消自动演奏。";
                        else if (ClassifyGameWindow(title.ToString(), className.ToString(), out reason))
                            WindowsInputAccess.CanSendTo(process, out reason);
                    }
                }
                return reason == null;
            }
            public bool Send(InputAction action, out string reason)
            {
                WinInput input = new WinInput();
                if (action.Mouse)
                {
                    input.Type = 0;
                    uint[] down = { 0x0002, 0x0008, 0x0020 }, up = { 0x0004, 0x0010, 0x0040 };
                    if (action.Button < 0 || action.Button > 2) { reason = "未知鼠标按钮。"; return false; }
                    input.Data.Mouse.Flags = action.Down ? down[action.Button] : up[action.Button];
                }
                else
                {
                    int scan = ScanCode(action.Key);
                    if (scan == 0) { reason = "未知音符键。"; return false; }
                    input.Type = 1;
                    input.Data.Keyboard.Scan = (ushort)scan;
                    input.Data.Keyboard.Flags = 0x0008u | (action.Down ? 0u : 0x0002u);
                }
                uint sent = SendInput(1, new[] { input }, NativeInputSize);
                if (sent != 1)
                {
                    reason = "Windows 未接受输入（错误码 " + Marshal.GetLastWin32Error() + "）；请检查普通输入和窗口权限是否允许。";
                    return false;
                }
                reason = null;
                return true;
            }
            private static int ScanCode(string key)
            {
                switch (key)
                {
                    case "Z": return 0x2c; case "X": return 0x2d; case "C": return 0x2e;
                    case "V": return 0x2f; case "B": return 0x30; case "N": return 0x31;
                    case "M": return 0x32; case ",": return 0x33; default: return 0;
                }
            }
            [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
            private static extern IntPtr NativeGetForegroundWindow();
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool IsWindow(IntPtr window);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool IsWindowVisible(IntPtr window);
            [DllImport("user32.dll")]
            private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
            [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
            private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
            [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
            private static extern int GetClassName(IntPtr window, StringBuilder text, int maximum);
            [DllImport("user32.dll", SetLastError = true)]
            private static extern uint SendInput(uint count, WinInput[] inputs, int size);
        }
    }
}
