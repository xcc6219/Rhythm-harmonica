using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using HarmonicaStudio;

public static class LandscapeLayoutTests
{
    private static int checks;
    public static int Run()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            int result = 0; Exception failure = null;
            var thread = new Thread(delegate() { try { result = RunOnSta(); } catch (Exception error) { failure = error; } });
            thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!thread.Join(30000)) throw new Exception("横向窗口布局验证超时。");
            if (failure != null) throw failure;
            return result;
        }
        return RunOnSta();
    }

    private static int RunOnSta()
    {
        checks = 0;
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        string path = Path.Combine(directory, "settings.json");
        bool hadDirectory = Directory.Exists(directory), hadSettings = File.Exists(path);
        byte[] original = hadSettings ? File.ReadAllBytes(path) : null;
        MainForm form = null;
        try
        {
            form = new MainForm();
            form.Size = new Size(1280, 720); Layout(form);
            Assert(form.Width > form.Height * 1.5, "完整版应是宽横向窗口。");
            var body = Field<Panel>(form, "body");
            var upper = Field<Panel>(form, "upperSection");
            var canvas = Field<ScoreCanvas>(form, "canvas");
            Assert(canvas.Parent == body && canvas.Dock == DockStyle.Fill, "完整瀑布必须直接位于主体底部，横跨左右区域。");
            Assert(canvas.Width >= 1000 && canvas.Height >= 300, "默认窗口的大瀑布须达到实际可读的宽高。");
            CheckScoreFit(canvas);
            Assert(canvas.Top >= upper.Bottom && canvas.Bottom >= body.Height - 5,
                "瀑布应位于控制区下方，演奏区域末端靠近主体底部。");
            CheckChildrenInside(upper); CheckChildrenInside(Field<Panel>(form, "sidebar"));
            CheckNoOverlap(Field<Panel>(form, "content").Controls[0]);

            form.Size = new Size(1032, 620); Layout(form);
            Assert(canvas.Width >= 1000 && canvas.Height >= 270, "最小完整窗口保留新增两项设置后，仍须容纳整张参考谱面及清楚的左侧图例。");
            CheckScoreFit(canvas);
            CheckChildrenInside(Field<Panel>(form, "sidebar"));
            CheckNoOverlap(Field<Panel>(form, "content").Controls[0]);
            Call(form, "SetCompact", true); Layout(form);
            var compactBody = Field<Panel>(form, "compactBody");
            var compactCanvas = Field<ScoreCanvas>(form, "compactCanvas");
            Assert(form.Width >= 800 && form.Width > form.Height * 2, "迷你窗口应是宽长条，而非原有方形小窗。");
            Assert(compactCanvas.Width >= 760 && compactCanvas.Height >= 210,
                "迷你模式也须保留完整图例和八列动态瀑布。");
            CheckScoreFit(compactCanvas);
            Assert(compactCanvas.Parent == compactBody && compactCanvas.Bottom >= compactBody.Height - 5,
                "迷你瀑布也必须位于该窗口主体最下部。");
            CheckChildrenInside(compactBody);
            Call(form, "SetCompact", false); Layout(form);
            Assert(form.Size == new Size(1032, 620), "展开时应恢复进入迷你前的完整窗口尺寸。");

            CheckReferenceLayout(form);

            var fit = (Size)typeof(MainForm).GetMethod("FitLandscapeSize", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { new Size(1280, 720), new Rectangle(100, 50, 1100, 680) });
            Assert(fit.Width <= 1100 && fit.Height <= 680, "新窗口尺寸必须夹在屏幕可用工作区内。");
            Console.WriteLine("横向窗口布局测试通过，共 " + checks + " 项断言（未显示窗口、未发送键鼠输入）。");
            return checks;
        }
        finally
        {
            if (form != null)
            {
                Field<System.Windows.Forms.Timer>(form, "timer").Dispose();
                Field<AudioPreview>(form, "audio").Dispose();
                Field<AutoPlayer>(form, "autoPlayer").Dispose();
                Field<ToolTip>(form, "sourceTip").Dispose();
                form.Dispose();
            }
            if (hadSettings) File.WriteAllBytes(path, original);
            else if (File.Exists(path)) File.Delete(path);
            if (!hadDirectory && Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
                Directory.Delete(directory);
        }
    }
    private static void Layout(Control control)
    {
        control.PerformLayout();
        foreach (Control child in control.Controls) Layout(child);
    }
    private static void CheckReferenceLayout(MainForm form)
    {
        ScoreCanvas full = Field<ScoreCanvas>(form, "canvas"), mini = Field<ScoreCanvas>(form, "compactCanvas");
        List<SongEntry> songs = Field<List<SongEntry>>(form, "library");
        SongEntry first = songs.First(s => s.Builtin), second = songs.First(s => s.Builtin && !Object.ReferenceEquals(s, first));
        foreach (SongEntry entry in new[] { first, second })
        {
            Call(form, "LoadSong", entry);
            Assert(full.SongTitle == entry.Title && mini.SongTitle == entry.Title,
                "完整和参考瀑布头部须跟随当前真实曲目，不能固定示例图片的曲名。");
            ScoreTrack actualTrack = (ScoreTrack)Field<ComboBox>(form, "tracks").SelectedItem;
            List<NoteEvent> notes = Field<List<NoteEvent>>(form, "notes");
            Assert(full.SongSubtitle == mini.SongSubtitle && full.SongSubtitle.Contains(actualTrack.Name) &&
                full.SongSubtitle.Contains(notes.Count + " 个音符") && full.SongSubtitle.Contains(entry.Bpm.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " BPM"),
                "瀑布说明须引用实际轨名、音符数量和歌曲BPM。");
        }
        Size previousSize = form.Size;
        Button reference = Field<Panel>(form, "header").Controls.Cast<Control>().OfType<Button>().Single(b => b.Text == "参考谱面");
        Assert(reference.Width >= 100 && reference.Visible == form.Visible,
            "标题栏须有足够宽的参考谱面入口。");
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(reference, new object[] { EventArgs.Empty });
        Layout(form);
        Rectangle workArea = Screen.FromControl(form).WorkingArea;
        Size desired = (Size)typeof(MainForm).GetMethod("FitLandscapeSize", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { new Size(1280, 620), workArea });
        Assert(Field<bool>(form, "compact") && form.Size == desired,
            "点击参考谱面须收起曲库和设置，并使用受工作区约束的宽横向谱面。");
        Assert(Math.Abs(form.Opacity - .75) < .001 && Field<AppSettings>(form, "settings").Opacity == .75,
            "参考谱面入口须实际应用并保存75%不透明度。");
        Assert(mini.Width >= Math.Min(1000, desired.Width - 40) && mini.Height >= Math.Min(350, desired.Height - 140),
            "参考布局须将主体空间留给瀑布和详细按键说明。");
        CheckChildrenInside(Field<Panel>(form, "compactBody"));
        CheckScoreFit(mini);
        Call(form, "SetCompact", false); Layout(form);
        Assert(form.Size == previousSize, "退出参考布局须恢复用户先前完整窗口尺寸。");
        Assert(!Field<AutoPlayer>(form, "autoPlayer").Running,
            "布局和曲目信息验证不能启动自动输入后端。");
    }
    private static void CheckScoreFit(ScoreCanvas canvas)
    {
        RectangleF fitted = canvas.FittedBounds;
        Assert(fitted.Left >= -.001f && fitted.Top >= -.001f && fitted.Right <= canvas.Width + .001f && fitted.Bottom <= canvas.Height + .001f,
            "实际主窗与迷你窗的完整参考谱面须整体等比 Fit，不得裁切说明。");
        Assert(Math.Abs(fitted.Width / fitted.Height - 1200f / 360) < .001f,
            "主窗缩放后图例、音符和瀑布必须共用同一比例。");
        RectangleF legend = CanvasBounds(canvas, "MouseLegendBounds"), waterfall = CanvasBounds(canvas, "WaterfallBounds"),
            keyboard = CanvasBounds(canvas, "KeyboardBounds"), hints = CanvasBounds(canvas, "HintBounds");
        Assert(!legend.IsEmpty && legend.Right < waterfall.Left && legend.Contains(keyboard) && legend.Contains(hints),
            "完整和迷你实际主窗必须常驻左侧六种鼠标图例及八键说明，瀑布下方不重复。");
    }
    private static RectangleF CanvasBounds(ScoreCanvas canvas, string name)
    { return canvas.ToClient((RectangleF)typeof(ScoreCanvas).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(canvas, null)); }
    private static void CheckChildrenInside(Control parent)
    {
        foreach (Control child in parent.Controls)
            if (!(child.Left >= 0 && child.Top >= 0 && child.Right <= parent.ClientSize.Width && child.Bottom <= parent.ClientSize.Height))
                throw new Exception("控制项必须完整位于父区域内：" + child.Text + " " + child.Bounds + " / " + parent.ClientSize);
        Assert(true, "该区域所有控制项都完整位于父区域内。");
    }
    private static void CheckNoOverlap(Control parent)
    {
        CheckChildrenInside(parent);
        for (int i = 0; i < parent.Controls.Count; i++)
            for (int j = i + 1; j < parent.Controls.Count; j++)
                if (parent.Controls[i].Bounds.IntersectsWith(parent.Controls[j].Bounds))
                    throw new Exception("上方控制项不得相互覆盖：" + parent.Controls[i].Text + " / " + parent.Controls[j].Text);
        Assert(true, "该区域所有控制项都没有互相覆盖。");
    }
    private static T Field<T>(object target, string name)
    {
        return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }
    private static void Call(object target, string name, object argument)
    {
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, new object[] { argument });
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }
}
