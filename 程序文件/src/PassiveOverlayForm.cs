using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HarmonicaStudio
{
    // Framework 4.x Form.TopMost calls SetWindowPos without NOACTIVATE,
    // including during HWND recreation. Keep the same public option while
    // using a non-activating native operation and initial extended style.
    internal class PassiveOverlayForm : Form
    {
        private bool keepOnTop;
        [DefaultValue(false)]
        public new bool TopMost
        {
            get { return keepOnTop; }
            set
            {
                if (keepOnTop == value) return;
                if (IsHandleCreated && !SetWindowPos(Handle, new IntPtr(value ? -1 : -2), 0, 0, 0, 0, 0x0013))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 无法更新窗口置顶状态。");
                keepOnTop = value;
            }
        }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                if (keepOnTop) parameters.ExStyle |= 0x00000008;
                else parameters.ExStyle &= ~0x00000008;
                return parameters;
            }
        }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr order, int x, int y, int width, int height, uint flags);
    }
}
