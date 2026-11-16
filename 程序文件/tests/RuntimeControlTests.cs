using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HarmonicaStudio;

internal static class RuntimeControlTests
{
    private static int checks;
    internal static int Run()
    {
        checks = 0;
        Check(WindowsInputAccess.AllowsIntegrity(0x2000, 0x2000), "相同完整性允许普通输入。");
        Check(WindowsInputAccess.AllowsIntegrity(0x3000, 0x2000), "管理员向普通窗口允许。");
        Check(!WindowsInputAccess.AllowsIntegrity(0x2000, 0x3000), "普通软件向管理员游戏明确拒绝。");
        Check(!WindowsInputAccess.AllowsIntegrity(-1, 0x2000) && !WindowsInputAccess.AllowsIntegrity(0x3000, -1), "未知权限不能当成可发送。");
        string reason;
        using (Process own = Process.GetCurrentProcess())
            Check(WindowsInputAccess.CanSendTo((uint)own.Id, out reason) && reason == null, "实际当前进程权限查询通过，未发按键。");
        Check(!WindowsInputAccess.CanSendTo(0, out reason) && reason.StartsWith("权限："), "无效目标的权限查询明确拒绝。");
        Check(WindowsInputAccess.OwnDescription() != "无法查询", "实际自身权限可读。");
        Check(Program.RestartParentId(new[] { "--restart-after", "42" }) == 42, "管理员重启可标记等待原实例退出。");
        foreach (string[] invalid in new[] { new[] { "--restart-after", "-1" }, new[] { "--restart-after", "0" },
            new[] { "--other", "42" }, new[] { "--restart-after", "42", "extra" }, new[] { "--restart-after", "a" } })
            Check(Program.RestartParentId(invalid) == 0, "无效重启标记不能指定外部进程。");
        using (var dialog = new RuntimeDiagnosticsDialog("状态示例", null))
        {
            dialog.Size = dialog.MinimumSize; dialog.PerformLayout();
            Control[] controls = dialog.Controls.Cast<Control>().ToArray();
            Check(controls.All(c => c.Left >= 0 && c.Top >= 0 && c.Right <= dialog.ClientSize.Width && c.Bottom <= dialog.ClientSize.Height),
                "最小诊断窗口所有内容完整位于窗口内。");
            Check(controls.SelectMany((a, i) => controls.Skip(i + 1).Select(b => !a.Bounds.IntersectsWith(b.Bounds))).All(value => value),
                "诊断正文、反馈说明和重启按钮没有重叠。");
            Check(controls.OfType<TextBox>().Single().ReadOnly && controls.OfType<TextBox>().Single().ScrollBars == ScrollBars.Vertical,
                "较长诊断可滚动读取，不裁掉失败原因。");
            Check(!controls.OfType<Button>().Single(b => b.Text == "管理员方式重启").Enabled,
                "没有明确重启入口时不启动外部进程。");
        }
        using (var form = new MainForm(false))
        {
            Field<Timer>(form, "timer").Stop();
            var down = new Dictionary<string, bool>();
            form.ControlKeyReader = delegate(string key) { bool value; return down.TryGetValue(key, out value) && value; };
            form.ControlModifiersReader = delegate { return false; };
            form.HotkeyRegistrar = delegate { return 0; }; form.HotkeyReleaser = delegate { };
            form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-30000, -30000); form.Show();
            Call(form, "RegisterControlHotkeys");
            HotkeyBindingResult bindings = Field<HotkeyBindingResult>(form, "currentHotkeyResult");
            string showKey = bindings.Names[2]; down[showKey] = true; Call(form, "TickPlayback");
            Check(form.Visible, "按住显隐键时不重复执行。");
            down[showKey] = false; Call(form, "TickPlayback");
            Check(form.Visible && form.WindowState == FormWindowState.Minimized && !form.IsDisposed,
                "没有WM通知的F8松开仍收起窗口，保持顶层窗口可见状态供任务栏恢复。");
            down[showKey] = true; Call(form, "TickPlayback"); down[showKey] = false; Call(form, "TickPlayback");
            Check(form.Visible && form.WindowState == FormWindowState.Normal, "收起期间普通状态检测仍可恢复窗口。");
            string details = (string)Call(form, "HotkeyDiagnostics");
            Check(details.Contains("按键状态监测") && details.Contains("不代表游戏已接收") && details.Contains("当前状态："),
                "诊断区分真实触发来源、Windows提交次数与游戏接收状态。");
            form.Dispose();
        }
        Console.WriteLine("游戏控制与权限诊断测试通过，共 " + checks + " 项断言（只读权限、模拟按键状态，未请求提权、未发送游戏输入）。");
        return checks;
    }
    private static T Field<T>(object target, string name)
    { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    private static object Call(object target, string name)
    { return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null); }
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; }
}
