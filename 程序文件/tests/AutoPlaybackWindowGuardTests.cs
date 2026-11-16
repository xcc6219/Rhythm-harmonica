using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using HarmonicaStudio;

internal static class AutoPlaybackWindowGuardTests
{
    private static int checks;
    private const int Styles = AutoPlaybackWindowGuard.LayeredStyle | AutoPlaybackWindowGuard.TransparentStyle | AutoPlaybackWindowGuard.NoActivateStyle;
    private static readonly Point Location = new Point(-28000, -28000);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowStyle(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowStyle(IntPtr window, int index, int style);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint color, out byte alpha, out uint flags);

    internal static int Run()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            int count = 0; Exception failure = null;
            var thread = new Thread(delegate() { try { count = Run(); } catch (Exception error) { failure = error; } });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (failure != null) throw failure; return count;
        }
        checks = 0;
        using (var target = new BackgroundWindow())
        {
            foreach (double opacity in new[] { 1.0, 0.9, 0.65 }) CheckWindow(target, opacity, false);
            CheckWindow(target, 1, true);
        }
        using (var closed = new GuardForm())
        {
            closed.Dispose(); string reason;
            Check(!closed.Guard.TrySetEnabled(true, out reason) && !String.IsNullOrEmpty(reason), "已关闭窗口不能启动自动穿透会话。");
        }
        Console.WriteLine("自动窗口鼠标穿透测试通过，共 " + checks + " 项断言（不同UI线程原生命中，未移动鼠标、未发送键鼠输入）。");
        return checks;
    }

    private static void CheckWindow(BackgroundWindow target, double opacity, bool colorKey)
    {
        using (var overlay = new GuardForm())
        using (var activation = new ActivationProbe(overlay))
        {
            overlay.Opacity = opacity;
            if (colorKey) overlay.TransparencyKey = Color.Magenta;
            overlay.Show(); overlay.Location = Location;
            target.Activate(); activation.Reset();
            int before = GetWindowStyle(overlay.Handle, -20);
            uint ownProcess, otherProcess;
            uint ownThread = GetWindowThreadProcessId(overlay.Handle, out ownProcess);
            uint targetThread = GetWindowThreadProcessId(target.Handle, out otherProcess);
            Check(ownThread != targetThread && ownProcess == otherProcess,
                "两个测试窗口来自不同真实UI线程；没有以HTTRANSPARENT同线程行为替代跨线程穿透。");
            Point buttonPoint = overlay.PointToScreen(new Point(70, 60));
            Point canvasPoint = overlay.PointToScreen(new Point(150, 125));
            Check(RootAt(buttonPoint) == overlay.Handle && RootAt(canvasPoint) == overlay.Handle,
                "启用前非零透明度、enabled/visible的上层窗口确实挡住底窗，按钮与空白区均真实命中。");
            uint originalColor, originalFlags; byte originalAlpha;
            bool wasLayered = GetLayeredWindowAttributes(overlay.Handle, out originalColor, out originalAlpha, out originalFlags);
            string reason;
            Check(overlay.Guard.TrySetEnabled(true, out reason) && overlay.Guard.Enabled, "自动会话开启本窗口鼠标穿透：" + reason);
            int active = GetWindowStyle(overlay.Handle, -20);
            Check((active & Styles) == Styles && (active & ~Styles) == (before & ~Styles) && (active & 0x40000) != 0,
                "保护同时使用Layered/Transparent/NoActivate，保留任务栏、TopMost及其它原样式。");
            Check(RootAt(buttonPoint) == target.Handle && RootAt(canvasPoint) == target.Handle,
                "原生WindowFromPoint在按钮和空白区均越过上层，命中另一UI线程的底窗。");
            uint protectedColor, protectedFlags; byte protectedAlpha;
            Check(GetLayeredWindowAttributes(overlay.Handle, out protectedColor, out protectedAlpha, out protectedFlags) &&
                overlay.Enabled && overlay.Visible && overlay.Opacity > 0 &&
                ((protectedFlags & 2) == 0 || protectedAlpha > 0) &&
                (!wasLayered || (originalColor == protectedColor && originalAlpha == protectedAlpha && originalFlags == protectedFlags)) && overlay.Opacity == opacity,
                "真实命中穿透不依赖有效alpha=0；仅color key时alpha参数不参与混合，既有透明度与color key保持不变。" +
                " opacity=" + opacity + " key=" + colorKey + " original=" + originalFlags + "/" + originalAlpha +
                " protected=" + protectedFlags + "/" + protectedAlpha);
            Check(SendMessage(overlay.Handle, 0x0021, target.Handle, new IntPtr(1 | (0x0201 << 16))) == new IntPtr(3),
                "自动保护对本窗WM_MOUSEACTIVATE明确返回MA_NOACTIVATE，不吞改其它应用消息。");
            Check(GetForegroundWindow() == target.Handle && activation.Activations == 0 && activation.Focuses == 0,
                "开启保护和原生命中检查没有把底窗前台转给overlay，也没有瞬态激活。");
            Check(overlay.Guard.TrySetEnabled(true, out reason) && overlay.Guard.Refresh(out reason) && RootAt(buttonPoint) == target.Handle,
                "倒计时、等待或暂停期间重复同步同一自动会话不会恢复鼠标点击。");
            Rectangle originalBounds = overlay.Bounds;
            overlay.TopMost = false;
            Check(!overlay.TopMost && !((Form)overlay).TopMost && (GetWindowStyle(overlay.Handle, -20) & 8) == 0 &&
                overlay.Bounds == originalBounds && GetForegroundWindow() == target.Handle && activation.Activations == 0 && activation.Focuses == 0,
                "窗口置顶选项可关闭，使用NOACTIVATE且不改位置大小；不设置Framework的隐式激活标记。");
            overlay.TopMost = true;
            Check(overlay.TopMost && !((Form)overlay).TopMost && (GetWindowStyle(overlay.Handle, -20) & 8) != 0 &&
                overlay.Bounds == originalBounds && GetForegroundWindow() == target.Handle && activation.Activations == 0 && activation.Focuses == 0,
                "窗口置顶选项可恢复，原生层级正确而用户前台和焦点保持不变。");
            overlay.Rebuild();
            Check(overlay.Guard.Enabled && (GetWindowStyle(overlay.Handle, -20) & Styles) == Styles && RootAt(buttonPoint) == target.Handle,
                "句柄重建在CreateParams/HandleCreated阶段重建穿透，不能只保护首个HWND。");
            Check(GetForegroundWindow() == target.Handle && activation.Activations == 0 && activation.Focuses == 0,
                "自动状态下重建句柄仍不激活overlay或抢走底窗焦点。" +
                " opacity=" + opacity + " foreground=" + GetForegroundWindow() + " expected=" + target.Handle +
                " activate=" + activation.Activations + " focus=" + activation.Focuses + activation.ActivationTrace);
            Check(overlay.Guard.TrySetEnabled(false, out reason) && !overlay.Guard.Enabled, "停止自动输入后恢复本窗交互：" + reason);
            int restored = GetWindowStyle(overlay.Handle, -20);
            Check((restored & Styles) == (before & Styles) && (restored & ~Styles) == (before & ~Styles),
                "停止只撤销保护新增的样式，保留原来的透明度、任务栏与正常窗口行为。");
            Check(RootAt(buttonPoint) == overlay.Handle && RootAt(canvasPoint) == overlay.Handle,
                "停止后按钮和空白区重新命中软件，支持选歌、拖动和缩放。");
            Check(GetForegroundWindow() == target.Handle && activation.Activations == 0 && activation.Focuses == 0,
                "解除保护也不自行激活窗口，继续由用户正常点击选择前台。");
            // An independent pre-existing transparent bit must survive cleanup.
            SetWindowStyle(overlay.Handle, -20, GetWindowStyle(overlay.Handle, -20) | AutoPlaybackWindowGuard.TransparentStyle);
            Check(overlay.Guard.TrySetEnabled(true, out reason) && overlay.Guard.TrySetEnabled(false, out reason) &&
                (GetWindowStyle(overlay.Handle, -20) & AutoPlaybackWindowGuard.TransparentStyle) != 0,
                "再次启动/停止保留应用在会话开始前已存在的透明样式。");
            SetWindowStyle(overlay.Handle, -20, GetWindowStyle(overlay.Handle, -20) & ~AutoPlaybackWindowGuard.TransparentStyle);
            overlay.Activate(); overlay.Controls[0].Focus();
            Check(GetForegroundWindow() == overlay.Handle && overlay.Guard.TrySetEnabled(true, out reason),
                "允许用户在软件前台启动倒计时，不提前激活底窗或向它强行转移焦点。");
            target.Activate(); activation.Reset();
            overlay.Rebuild();
            Check(GetForegroundWindow() == target.Handle && activation.Activations == 0 && activation.Focuses == 0 &&
                RootAt(buttonPoint) == target.Handle && overlay.Guard.TrySetEnabled(false, out reason),
                "前台启动后正常切到别的UI线程，会清本窗旧焦点；立即重建不等待Timer也不抢回前台。" +
                " opacity=" + opacity + " foreground=" + GetForegroundWindow() + " expected=" + target.Handle +
                " activate=" + activation.Activations + " focus=" + activation.Focuses);
        }
    }

    private static IntPtr RootAt(Point point)
    { IntPtr hit = WindowFromPoint(point); return hit == IntPtr.Zero ? hit : GetAncestor(hit, 2); }
    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new Exception("鼠标穿透测试失败：" + message); }
    private static bool Wait(Func<bool> completed, int milliseconds)
    {
        var timeout = Stopwatch.StartNew();
        while (!completed()) { Application.DoEvents(); if (timeout.ElapsedMilliseconds > milliseconds) return false; Thread.Sleep(1); }
        Application.DoEvents(); return true;
    }
    private sealed class GuardForm : PassiveOverlayForm
    {
        internal readonly AutoPlaybackWindowGuard Guard;
        internal GuardForm()
        {
            Guard = new AutoPlaybackWindowGuard(this);
            Text = "自动穿透原生上层测试"; FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
            Location = AutoPlaybackWindowGuardTests.Location; Size = new Size(240, 180); ShowInTaskbar = true; TopMost = true;
            BackColor = Color.DarkSlateGray;
            Controls.Add(new Button { Text = "真实原生按钮", Location = new Point(30, 35), Size = new Size(100, 50) });
        }
        internal void Rebuild() { RecreateHandle(); }
        protected override CreateParams CreateParams
        {
            get { CreateParams value = base.CreateParams; if (Guard != null) value.ExStyle = Guard.ApplyCreateStyle(value.ExStyle); return value; }
        }
        protected override bool ShowWithoutActivation { get { return Guard != null && Guard.Enabled; } }
        protected override void Dispose(bool disposing) { if (disposing && Guard != null) Guard.Dispose(); base.Dispose(disposing); }
    }
    private sealed class ActivationProbe : NativeWindow, IDisposable
    {
        private readonly Form window;
        internal int Activations, Focuses;
        internal string ActivationTrace;
        internal ActivationProbe(Form window)
        { this.window = window; window.HandleCreated += Created; window.HandleDestroyed += Destroyed; if (window.IsHandleCreated) AssignHandle(window.Handle); }
        internal void Reset() { Activations = Focuses = 0; ActivationTrace = ""; }
        private void Created(object sender, EventArgs args) { AssignHandle(window.Handle); }
        private void Destroyed(object sender, EventArgs args) { if (Handle != IntPtr.Zero) ReleaseHandle(); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0006 && (message.WParam.ToInt64() & 0xffff) != 0)
            { Activations++; ActivationTrace = "\r\nactivation wparam=" + message.WParam + " lparam=" + message.LParam + "\r\n" + Environment.StackTrace; }
            if (message.Msg == 0x0007) Focuses++;
            base.WndProc(ref message);
        }
        public void Dispose() { window.HandleCreated -= Created; window.HandleDestroyed -= Destroyed; if (Handle != IntPtr.Zero) ReleaseHandle(); }
    }
    private sealed class BackgroundWindow : IDisposable
    {
        private readonly Thread thread;
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private Form window;
        private Exception failure;
        internal IntPtr Handle { get; private set; }
        internal BackgroundWindow()
        {
            thread = new Thread(delegate()
            {
                try
                {
                    using (window = new Form { Text = "另一UI线程鼠标目标测试", FormBorderStyle = FormBorderStyle.None,
                        StartPosition = FormStartPosition.Manual, Location = AutoPlaybackWindowGuardTests.Location, Size = new Size(240, 180), ShowInTaskbar = false })
                    { window.Shown += delegate { Handle = window.Handle; ready.Set(); }; Application.Run(window); }
                }
                catch (Exception error) { failure = error; ready.Set(); }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
            if (!Wait(delegate { return ready.WaitOne(0); }, 5000)) { Dispose(); throw new Exception("鼠标目标测试窗创建超时。"); }
            if (failure != null) { Dispose(); throw failure; }
        }
        internal void Activate()
        {
            Exception error = null; int completed = 0;
            window.BeginInvoke(new Action(delegate { try { window.Activate(); } catch (Exception caught) { error = caught; } finally { Interlocked.Exchange(ref completed, 1); } }));
            if (!Wait(delegate { return Interlocked.CompareExchange(ref completed, 0, 0) != 0; }, 3000)) throw new Exception("鼠标底窗前台准备超时。");
            if (error != null) throw error;
            if (!Wait(delegate { return GetForegroundWindow() == Handle; }, 3000)) throw new Exception("无法把本软件的鼠标底窗设为测试前台。");
        }
        public void Dispose()
        {
            if (window != null && !window.IsDisposed && window.IsHandleCreated) window.BeginInvoke(new Action(delegate { window.Close(); }));
            if (!Wait(delegate { return !thread.IsAlive; }, 5000)) throw new Exception("鼠标底窗测试线程未退出。");
            ready.Dispose();
        }
    }
}
