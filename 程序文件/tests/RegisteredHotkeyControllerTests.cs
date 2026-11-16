using System;
using System.Collections.Generic;
using System.Linq;
using HarmonicaStudio;

public static class RegisteredHotkeyControllerTests
{
    private static int checks;
    public static int Run()
    {
        checks = 0;
        RegisteredKeysOnly(); WindowsAndPollShareRelease(); PollBeforeDelayedMessage();
        ShortQueuedPresses(); LongPressAndInitialHold(); ModifierCombinations();
        ResetAndHandleReconfiguration(); CallbackChangesConfiguration(); TickWrapAndBoundedHistory();
        Console.WriteLine("已注册控制键备用监测测试通过，共 " + checks + " 项断言（模拟状态与时钟，未读取真实键盘、未发送输入）。");
        return checks;
    }

    private static void RegisteredKeysOnly()
    {
        var fixture = new Fixture();
        fixture.Bindings.Registered[1] = fixture.Bindings.Registered[3] = false;
        fixture.Controller.Configure(fixture.Bindings); fixture.Reads.Clear();
        fixture.Down["F7"] = fixture.Down["F9"] = true;
        fixture.Sample(110); fixture.Message(2, 112, 113); fixture.Message(4, 114, 115);
        fixture.Message(0, 116, 117); fixture.Message(5, 118, 119);
        Assert(fixture.Count == 0 && fixture.Reads.SequenceEqual(new[] { "F5", "F8" }),
            "轮询只读取成功注册的键；冲突、未注册及未知ID均不争抢或触发。");
        fixture.Down["F8"] = true; fixture.Sample(120);
        fixture.Down["F8"] = false; fixture.Sample(130);
        Assert(fixture.Count == 1 && fixture.Events[0].Id == 3, "部分键注册失败不阻止已登记的显隐键。");
        fixture.Controller.Reset(); fixture.Reads.Clear(); fixture.Sample(140);
        Assert(fixture.Reads.Count == 0 && fixture.Controller.LastTriggerSource == "", "停止或释放注册后不继续读取按键，也清除旧来源。");
    }

    private static void WindowsAndPollShareRelease()
    {
        var fixture = new Fixture();
        fixture.Down["F5"] = true; fixture.Message(1, 110, 110);
        for (uint tick = 115; tick < 180; tick += 5) fixture.Sample(tick);
        Assert(fixture.Count == 0, "WM热键与轮询都等待控制键释放，不在长按过程中切换播放。");
        fixture.Down["F5"] = false; fixture.Sample(180); fixture.Sample(185);
        Assert(fixture.Count == 1 && fixture.Events[0].Id == 1 && fixture.Events[0].Source == "Windows 热键消息",
            "同一物理按键的WM消息与状态监测合并，释放后只执行一次。");
        fixture.Down["F5"] = true; fixture.Message(1, 190, 190); fixture.Sample(195);
        fixture.Down["F5"] = false; fixture.Sample(200);
        Assert(fixture.Count == 2 && fixture.Events[1].Tick == 200, "快速再次按下释放保留第二次操作，不受粗略防抖时间吞键。");
    }

    private static void PollBeforeDelayedMessage()
    {
        var fixture = new Fixture();
        fixture.Sample(105); fixture.Down["F5"] = true; fixture.Sample(115);
        fixture.Down["F5"] = false; fixture.Sample(125);
        Assert(fixture.Count == 1 && fixture.Events[0].Source == "按键状态监测", "未收到WM时可由已登记键的完整按下释放周期触发。");
        fixture.Message(1, 110, 135);
        Assert(fixture.Count == 1, "轮询先执行后抵达的同一周期WM消息被消费，不会立即二次切换。");
        fixture.Message(1, 120, 136);
        Assert(fixture.Count == 2, "消息队列中同一区间的第二个真实通知仍代表独立按键，不能全部被截止时刻吞掉。");
        fixture.Down["F5"] = true; fixture.Sample(140);
        fixture.Down["F5"] = false; fixture.Sample(150);
        fixture.Down["F5"] = true; fixture.Sample(160);
        fixture.Down["F5"] = false; fixture.Sample(170);
        int beforeMessages = fixture.Count;
        fixture.Message(1, 155, 180); fixture.Message(1, 138, 181);
        Assert(fixture.Count == beforeMessages, "两个已由轮询执行的周期可独立消费各自迟到消息，包括反向抵达顺序。");
        fixture.Message(1, 185, 190);
        Assert(fixture.Count == beforeMessages + 1, "区间以外的新WM通知正常执行，不因以前的迟到历史被屏蔽。");
    }

    private static void ShortQueuedPresses()
    {
        var fixture = new Fixture();
        fixture.Message(3, 110, 150); fixture.Message(3, 120, 151);
        Assert(fixture.Count == 2 && fixture.Events.All(item => item.Id == 3),
            "UI空闲后两个已释放的排队点击都保留；处理时刻不能作为所有消息的截止。");
        fixture.Message(3, 160, 180); fixture.Message(3, 160, 181);
        Assert(fixture.Count == 4, "已释放时同毫秒的两个Windows通知也分别执行，兼容真实消息队列与隔离SendMessage验证。");
        fixture.Down["F8"] = true;
        fixture.Message(3, 190, 190); fixture.Message(3, 195, 196);
        Assert(fixture.Count == 4, "当前仍按住时两个独立通知保留候选，但尚不执行。");
        fixture.Down["F8"] = false; fixture.Sample(200);
        Assert(fixture.Count == 6, "轮询间隔内的两个有独立消息的快速点击不会被归并为一次。");
    }

    private static void LongPressAndInitialHold()
    {
        var fixture = new Fixture(); fixture.Down["F7"] = true;
        fixture.Message(2, 110, 110);
        for (uint tick = 120; tick <= 1110; tick += 10)
        { fixture.Sample(tick); fixture.Message(2, 110, tick); }
        Assert(fixture.Count == 0, "长按、连续轮询和仍按住时的同一通知不产生多个停止命令。");
        fixture.Down["F7"] = false; fixture.Sample(1120);
        Assert(fixture.Count == 1 && fixture.Events[0].Id == 2, "长按停止键只在释放时执行一次。");
        fixture.Down["F9"] = true; fixture.Now = 1200; fixture.Controller.Configure(fixture.Bindings);
        fixture.Message(4, 1200, 1201); fixture.Sample(1210);
        fixture.Down["F9"] = false; fixture.Sample(1220);
        Assert(fixture.Count == 1, "注册时已经按住的键先等待释放，启动软件或重建句柄不误切迷你。");
        fixture.Down["F9"] = true; fixture.Sample(1230);
        fixture.Down["F9"] = false; fixture.Sample(1240);
        Assert(fixture.Count == 2 && fixture.Events[1].Id == 4, "初始按住的键释放后重新按可正常使用。");
    }

    private static void ModifierCombinations()
    {
        foreach (string modifier in new[] { "Ctrl", "Alt", "Shift", "LWin", "RWin" })
        {
            var fixture = new Fixture(); fixture.Modifiers.Add(modifier); fixture.Down["F5"] = true;
            fixture.Message(1, 110, 110); fixture.Sample(115);
            fixture.Modifiers.Clear(); fixture.Sample(120);
            fixture.Down["F5"] = false; fixture.Sample(125);
            Assert(fixture.Count == 0, modifier + "+F5不会触发无修饰符播放键，先放修饰符也不补触发。");
            fixture.Down["F5"] = true; fixture.Sample(130);
            fixture.Down["F5"] = false; fixture.Sample(135);
            Assert(fixture.Count == 1, "修饰组合完整释放后可重新使用独立F5：" + modifier);
        }
        var midway = new Fixture(); midway.Down["F8"] = true; midway.Message(3, 110, 110);
        midway.Modifiers.Add("Alt"); midway.Sample(115); midway.Modifiers.Clear(); midway.Sample(120);
        midway.Down["F8"] = false; midway.Sample(125);
        Assert(midway.Count == 0, "按键持有期间出现修饰符会取消本轮，避免Alt切回窗口时误显隐。");
    }

    private static void ResetAndHandleReconfiguration()
    {
        var fixture = new Fixture(); fixture.Down["F5"] = true; fixture.Message(1, 110, 110);
        fixture.Controller.Reset(); fixture.Down["F5"] = false; fixture.Sample(120); fixture.Message(1, 110, 121);
        Assert(fixture.Count == 0, "停止接收或退出时Reset取消按住、消息与轮询候选，不在之后迟发命令。");
        fixture.Now = 200; fixture.Controller.Configure(fixture.Bindings);
        fixture.Message(1, 110, 210);
        Assert(fixture.Count == 0, "重新注册后旧代际创建的WM消息不会作用于新窗口。");
        fixture.Bindings.Names[0] = "F10"; fixture.Now = 220; fixture.Controller.Configure(fixture.Bindings);
        fixture.Down["F5"] = true; fixture.Sample(230); fixture.Down["F5"] = false; fixture.Sample(240);
        Assert(fixture.Count == 0, "更换绑定后旧F5不继续被状态监测占用。");
        fixture.Down["F10"] = true; fixture.Sample(250); fixture.Down["F10"] = false; fixture.Sample(260);
        Assert(fixture.Count == 1 && fixture.Events[0].Id == 1, "句柄重建后配置新的实际键名，可继续同一逻辑播放操作。");
        fixture.Controller.Configure(null); fixture.Reads.Clear(); fixture.Sample(270);
        Assert(fixture.Count == 1 && fixture.Reads.Count == 0, "空配置安全清除所有旧注册键。");
    }

    private static void CallbackChangesConfiguration()
    {
        var fixture = new Fixture(); fixture.Down["F5"] = true;
        fixture.Message(1, 110, 110); fixture.Message(1, 115, 115);
        fixture.AfterTrigger = delegate { fixture.Controller.Reset(); };
        fixture.Down["F5"] = false; fixture.Sample(120);
        Assert(fixture.Count == 1, "第一个操作导致退出或句柄重建时，旧配置剩余排队命令立即取消。");
        var reconfigured = new Fixture(); reconfigured.Down["F5"] = reconfigured.Down["F8"] = true; reconfigured.Sample(110);
        reconfigured.Down["F5"] = reconfigured.Down["F8"] = false;
        reconfigured.AfterTrigger = delegate { reconfigured.Controller.Configure(reconfigured.Bindings); };
        reconfigured.Sample(120);
        Assert(reconfigured.Count == 1, "Poll回调重新注册时中止旧数组后续键处理，不触发旧显隐候选。");
        var throwing = new Fixture(); throwing.Down["F5"] = true; throwing.Sample(110);
        throwing.AfterTrigger = delegate { throw new InvalidOperationException("模拟UI操作异常"); };
        throwing.Down["F5"] = false;
        Throws<InvalidOperationException>(delegate { throwing.Sample(120); }, "UI回调异常可以正常上报。");
        throwing.AfterTrigger = null; throwing.Sample(125);
        Assert(throwing.Count == 1, "回调发生异常前已清除候选，之后的轮询不重复执行失败操作。");
    }

    private static void TickWrapAndBoundedHistory()
    {
        var fixture = new Fixture(); fixture.Now = UInt32.MaxValue - 30; fixture.Controller.Configure(fixture.Bindings);
        fixture.Down["F5"] = true; fixture.Sample(UInt32.MaxValue - 10);
        fixture.Down["F5"] = false; fixture.Sample(5);
        fixture.Message(1, UInt32.MaxValue - 15, 10);
        Assert(fixture.Count == 1, "32位系统tick回绕后仍能正确匹配poll周期的迟到WM通知。");
        fixture.Message(1, 15, 20);
        Assert(fixture.Count == 2, "回绕后的新独立消息正常触发，不被当作旧时间。");
        fixture.Now = 30; fixture.Controller.Configure(fixture.Bindings);
        fixture.Message(1, UInt32.MaxValue - 15, 35);
        Assert(fixture.Count == 2, "回绕后重建配置仍能拒绝上一代际消息。");
        var history = new Fixture();
        for (uint index = 0; index < 70; index++)
        {
            history.Down["F5"] = true; history.Sample(110 + index * 20);
            history.Down["F5"] = false; history.Sample(120 + index * 20);
        }
        Assert(history.Count == 70, "持续仅备用监测时每次真实按下释放仍可靠执行。");
        history.Message(1, 115, 1600);
        Assert(history.Count == 70, "历史缓存淘汰后的旧WM消息也不能成为新命令。");
        history.Message(1, 1485, 1601);
        Assert(history.Count == 70, "保留区间的迟到消息继续正确去重。");
        history.Message(1, 1610, 1620);
        Assert(history.Count == 71, "历史缓存上限不阻止新的独立Windows通知。");
    }

    private sealed class Fixture
    {
        internal uint Now = 100;
        internal readonly HotkeyBindingResult Bindings = new HotkeyBindingResult();
        internal readonly Dictionary<string, bool> Down = new Dictionary<string, bool>();
        internal readonly HashSet<string> Modifiers = new HashSet<string>();
        internal readonly List<string> Reads = new List<string>();
        internal readonly List<Triggered> Events = new List<Triggered>();
        internal readonly RegisteredHotkeyController Controller;
        internal Action<int> AfterTrigger;
        internal int Count { get { return Events.Count; } }
        internal Fixture()
        {
            string[] names = { "F5", "F7", "F8", "F9" };
            for (int index = 0; index < names.Length; index++) { Bindings.Names[index] = names[index]; Bindings.Registered[index] = true; }
            Controller = new RegisteredHotkeyController(delegate(string key)
            { Reads.Add(key); bool value; return Down.TryGetValue(key, out value) && value; },
                delegate { return Modifiers.Count > 0; }, delegate { return Now; }, delegate(int id)
                { Events.Add(new Triggered { Id = id, Tick = Now, Source = Controller.LastTriggerSource }); if (AfterTrigger != null) AfterTrigger(id); });
            Controller.Configure(Bindings);
        }
        internal void Sample(uint tick) { Now = tick; Controller.Poll(); }
        internal void Message(int id, uint stamp, uint now) { Now = now; Controller.Receive(id, stamp); }
    }
    private sealed class Triggered { internal int Id; internal uint Tick; internal string Source; }
    private static void Assert(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    { checks++; try { action(); } catch (T) { return; } throw new Exception(message); }
}
