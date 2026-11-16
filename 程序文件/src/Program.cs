using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.Windows.Forms;

namespace HarmonicaStudio
{
    internal static class Program
    {
        private static readonly string InstanceName = InstanceKey();
        internal static readonly uint ActivateMessage = RegisterWindowMessage(InstanceName + ".Activate");
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
        private static string InstanceKey()
        {
            using (SHA256 hash = SHA256.Create())
                return "Local\\RhythmHarmonica." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(AppPaths.RootDirectory).ToUpperInvariant()))).Replace("-", "");
        }
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.Message, "节奏口琴", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            try
            {
                WaitForRestartParent(args);
                using (var instance = new Mutex(false, InstanceName))
                {
                    bool acquired;
                    try { acquired = instance.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) { PostMessage((IntPtr)0xffff, ActivateMessage, IntPtr.Zero, IntPtr.Zero); return; }
                    try
                    {
                        AppPaths.Initialize();
                        using (var main = new MainForm()) Application.Run(main);
                    }
                    finally
                    {
                        try { ProcessJob.ShutdownOwnedJobs(); }
                        finally { instance.ReleaseMutex(); }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败：" + ex.Message, "节奏口琴", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        internal static int RestartParentId(string[] args)
        {
            int id;
            return args != null && args.Length == 2 && args[0] == "--restart-after" &&
                Int32.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0 ? id : 0;
        }
        private static void WaitForRestartParent(string[] args)
        {
            int id = RestartParentId(args);
            if (id == 0) return;
            try
            {
                using (Process parent = Process.GetProcessById(id))
                {
                    if (parent.HasExited) return;
                    string parentPath;
                    try { parentPath = parent.MainModule.FileName; }
                    catch (InvalidOperationException) { if (parent.HasExited) return; throw; }
                    if (id == Process.GetCurrentProcess().Id || !String.Equals(Path.GetFullPath(parentPath),
                        Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("管理员重启的旧程序标识无效。");
                    if (!parent.WaitForExit(8000)) throw new InvalidOperationException("旧程序尚未关闭，请先关闭旧窗口再重新启动。");
                }
            }
            catch (ArgumentException) { }
        }
        internal static bool RequestElevatedRestart(out string failure)
        {
            try
            {
                using (Process own = Process.GetCurrentProcess())
                using (Process started = Process.Start(new ProcessStartInfo(Application.ExecutablePath,
                    "--restart-after " + own.Id.ToString(CultureInfo.InvariantCulture))
                    { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppPaths.RootDirectory }))
                {
                    if (started == null) { failure = "管理员重启没有启动，当前程序仍保留。"; return false; }
                }
                failure = null; return true;
            }
            catch (Win32Exception error)
            { failure = error.NativeErrorCode == 1223 ? "已取消 Windows 授权，演奏已停止，当前程序仍保留。" : "Windows 未能启动管理员窗口，当前程序仍保留。"; return false; }
            catch (InvalidOperationException)
            { failure = "管理员重启未完成，当前程序仍保留。"; return false; }
        }
    }
}
