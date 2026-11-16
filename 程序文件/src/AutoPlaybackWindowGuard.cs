using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HarmonicaStudio
{
    // Protect only this application's overlay. Layered + Transparent performs
    // native cross-thread mouse pass-through; HTTRANSPARENT alone does not.
    internal sealed class AutoPlaybackWindowGuard : NativeWindow, IDisposable
    {
        internal const int TransparentStyle = 0x00000020;
        internal const int LayeredStyle = 0x00080000;
        internal const int NoActivateStyle = 0x08000000;
        private const int ProtectionStyles = TransparentStyle | LayeredStyle | NoActivateStyle;
        private readonly Form window;
        private bool enabled, disposed, baselineCaptured, clearingLocalState;
        private int baselineStyles;
        private IntPtr appliedHandle;
        internal event Action<string> Failed;
        internal bool Enabled { get { return enabled; } }

        internal AutoPlaybackWindowGuard(Form window)
        {
            if (window == null) throw new ArgumentNullException("window");
            this.window = window;
            window.HandleCreated += HandleCreated;
            window.HandleDestroyed += HandleDestroyed;
        }

        internal int ApplyCreateStyle(int style)
        {
            if (!enabled || disposed) return style;
            if (!baselineCaptured) { baselineStyles = style & ProtectionStyles; baselineCaptured = true; }
            return style | ProtectionStyles;
        }

        internal bool TrySetEnabled(bool value, out string reason)
        {
            reason = null;
            if (disposed) { reason = "窗口交互保护已释放。"; return !value; }
            if (value && window.IsDisposed) { reason = "窗口已关闭，不能开启鼠标穿透。"; return false; }
            if (window.IsHandleCreated && window.InvokeRequired)
            { reason = "窗口交互保护必须由本窗口的界面线程更新。"; return false; }
            if (value == enabled) return Refresh(out reason);
            if (value)
            {
                if (window.IsHandleCreated)
                {
                    int style;
                    if (!ReadStyle(window.Handle, out style, out reason)) return false;
                    baselineStyles = style & ProtectionStyles; baselineCaptured = true;
                }
                enabled = true;
                if (Refresh(out reason)) return true;
                string restoreReason;
                if (RestoreStyles(out restoreReason))
                { enabled = false; baselineCaptured = false; appliedHandle = IntPtr.Zero; Detach(); }
                else
                {
                    // Retain the captured baseline so the caller can retry
                    // cleanup after stopping the session; never start input.
                    reason += " 还原窗口交互也失败：" + restoreReason;
                }
                return false;
            }
            if (!RestoreStyles(out reason)) return false;
            enabled = false; baselineCaptured = false; appliedHandle = IntPtr.Zero; Detach();
            return true;
        }

        internal bool Refresh(out string reason)
        {
            reason = null;
            if (disposed) { reason = "窗口交互保护已释放。"; return false; }
            if (!enabled || !window.IsHandleCreated || window.IsDisposed) return true;
            if (window.InvokeRequired) { reason = "窗口交互保护必须由本窗口的界面线程更新。"; return false; }
            IntPtr handle = window.Handle;
            ClearStaleWindowFocus(false);
            int style;
            if (!ReadStyle(handle, out style, out reason)) return false;
            if (!baselineCaptured) { baselineStyles = style & ProtectionStyles; baselineCaptured = true; }
            bool first = appliedHandle != handle;
            bool addLayer = (style & LayeredStyle) == 0;
            int desired = style | ProtectionStyles;
            if (desired != style && !WriteStyle(handle, desired, out reason)) return false;
            if (!EnsureLayerAttributes(handle, addLayer || (first && (baselineStyles & LayeredStyle) == 0), out reason)) return false;
            if ((first || desired != style) && !CommitStyle(handle, out reason)) return false;
            if (Handle != handle) { Detach(); AssignHandle(handle); }
            appliedHandle = handle;
            return true;
        }

        private bool RestoreStyles(out string reason)
        {
            reason = null;
            if (!baselineCaptured || !window.IsHandleCreated || window.IsDisposed) return true;
            int style;
            if (!ReadStyle(window.Handle, out style, out reason)) return false;
            int added = ProtectionStyles & ~baselineStyles;
            // Preserve layering owned by the user's current opacity/color key.
            if (window.Opacity < 1 || !window.TransparencyKey.IsEmpty) added &= ~LayeredStyle;
            int desired = style & ~added;
            return desired == style || (WriteStyle(window.Handle, desired, out reason) && CommitStyle(window.Handle, out reason));
        }

        private bool EnsureLayerAttributes(IntPtr handle, bool initialize, out string reason)
        {
            reason = null;
            uint color, flags; byte alpha;
            if (!initialize && GetLayeredWindowAttributes(handle, out color, out alpha, out flags)) return true;
            Color key = window.TransparencyKey;
            color = key.IsEmpty ? 0u : (uint)(key.R | (key.G << 8) | (key.B << 16));
            alpha = (byte)Math.Max(0, Math.Min(255, (int)(window.Opacity * 255)));
            flags = key.IsEmpty ? 2u : 3u;
            if (SetLayeredWindowAttributes(handle, color, alpha, flags)) return true;
            reason = "Windows 无法准备鼠标穿透窗口的透明度（" + Marshal.GetLastWin32Error() + "）。";
            return false;
        }

        private static bool ReadStyle(IntPtr handle, out int style, out string reason)
        {
            ClearLastError(0); style = GetWindowStyle(handle, -20);
            int error = Marshal.GetLastWin32Error();
            reason = style == 0 && error != 0 ? "Windows 无法读取窗口交互样式（" + error + "）。" : null;
            return reason == null;
        }
        private static bool WriteStyle(IntPtr handle, int style, out string reason)
        {
            ClearLastError(0); int previous = SetWindowStyle(handle, -20, style);
            int error = Marshal.GetLastWin32Error();
            reason = previous == 0 && error != 0 ? "Windows 无法更新窗口交互样式（" + error + "）。" : null;
            return reason == null;
        }
        private static bool CommitStyle(IntPtr handle, out string reason)
        {
            if (SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0037)) { reason = null; return true; }
            reason = "Windows 无法应用窗口交互样式（" + Marshal.GetLastWin32Error() + "）。";
            return false;
        }
        private void HandleCreated(object sender, EventArgs args)
        {
            if (!enabled || disposed) return;
            string reason;
            if (!Refresh(out reason)) { Action<string> handler = Failed; if (handler != null) handler(reason); }
        }
        private void HandleDestroyed(object sender, EventArgs args)
        { appliedHandle = IntPtr.Zero; Detach(); }
        private void Detach() { if (Handle != IntPtr.Zero) ReleaseHandle(); }
        private void ClearStaleWindowFocus(bool deactivating)
        {
            if (!enabled || !window.IsHandleCreated || clearingLocalState) return;
            IntPtr owner = window.Handle;
            bool ownForeground = GetAncestor(GetForegroundWindow(), 3) == owner;
            if (!deactivating && ownForeground) return;
            clearingLocalState = true;
            try
            {
                IntPtr focus = GetFocus();
                // Windows keeps keyboard focus separately per UI thread.
                // WinForms restores it while recreating a window.
                // Clear only this overlay's state after it loses foreground;
                // never attach queues or change another thread's window.
                if (focus == owner || (focus != IntPtr.Zero && IsChild(owner, focus))) SetFocus(IntPtr.Zero);
            }
            finally { clearingLocalState = false; }
        }
        protected override void WndProc(ref Message message)
        {
            if (enabled && message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            if (enabled && message.Msg == 0x0006 && (message.WParam.ToInt64() & 0xffff) == 0)
                ClearStaleWindowFocus(true);
            base.WndProc(ref message);
        }
        public void Dispose()
        {
            if (disposed) return;
            string ignored; TrySetEnabled(false, out ignored);
            window.HandleCreated -= HandleCreated; window.HandleDestroyed -= HandleDestroyed;
            Detach(); disposed = true;
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowStyle(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowStyle(IntPtr window, int index, int value);
        [DllImport("kernel32.dll", EntryPoint = "SetLastError")] private static extern void ClearLastError(uint error);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr order, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint color, out byte alpha, out uint flags);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    }
}
