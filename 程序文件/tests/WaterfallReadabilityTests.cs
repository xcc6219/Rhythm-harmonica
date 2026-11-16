using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
using System.Windows.Forms;
using HarmonicaStudio;

public static class WaterfallReadabilityTests
{
    private static int checks;
    private static readonly Color StageColor = Color.FromArgb(31, 34, 35);
    private static readonly Color NoteInkColor = Color.FromArgb(73, 62, 31);

    public static int Run()
    {
        checks = 0;
        foreach (float dpi in new[] { 96f, 144f, 192f })
        {
            TestEffectiveSpeedCaption(dpi, false);
            TestEffectiveSpeedCaption(dpi, true);
            TestCurrentAndNextCues(dpi, false);
            TestCurrentAndNextCues(dpi, true);
            TestCurrentMouseLegend(dpi, false);
            TestCurrentMouseLegend(dpi, true);
            TestDenseSameColumn(dpi, false);
            TestDenseSameColumn(dpi, true);
            TestHeldNoteSeparation(dpi);
            TestOnsetAndTopEdge(dpi);
            TestWarningSeparation(dpi);
            TestFooterInk(dpi, false);
            TestFooterInk(dpi, true);
            TestDenseCurrentWindow(dpi);
            TestUniformCardGlyphs(dpi, false);
            TestUniformCardGlyphs(dpi, true);
            TestFixedKeysContrast(dpi, false);
            TestFixedKeysContrast(dpi, true);
            TestReferenceRegions(dpi);
            TestModifierLegend(dpi);
            TestPersistentFingeringHints(dpi);
            TestNarrowUnifiedFingering(dpi);
        }
        Console.WriteLine("瀑布可读性测试通过，共 " + checks + " 项断言（真实像素、96/144/192 DPI，未发送键鼠输入）。");
        return checks;
    }

    private static void TestCurrentAndNextCues(float dpi, bool compact)
    {
        using (var canvas = NewCanvas(compact, 0))
        {
            canvas.Notes = new List<NoteEvent> { Note(60, 100, .2) };
            using (var first = Render(canvas, dpi))
            {
                canvas.Notes = new List<NoteEvent> { Note(75, 100, .2) };
                using (var next = Render(canvas, dpi))
                    Assert(ChangedPixels(first, next, canvas.ToClient(new RectangleF(388, 8, 796, 52))) > 15,
                        "下一音尚未落入瀑布时，固定提示也须明确显示它的键盘及鼠标指法。");
            }
            Assert((string)Call(canvas, "CurrentCueText") == "当前 等待首音", "首音之前须明确等待首音，不能误称休止或结束。");
            Assert((string)Call(canvas, "NextCueText") == "下一音 X · 右键 + 中键", "下一音须完整说明高八度半音的真实组合键。");
            canvas.Notes = new List<NoteEvent> { Note(75, 1, .2), Note(49, 1.4, .2), Note(120, 2, .2) };
            canvas.TimelineDuration = 8;
            double[] positions = { 1.1, 1.2, 1.5, 2.1, 2.2 };
            string[] currentTexts = { "当前 X · 右键 + 中键", "当前 休止 · 松开按键", "当前 Z · 左键 + 中键", "当前 超出音域 · 休止", "当前 旋律已结束" };
            string[] nextTexts = { "下一音 Z · 左键 + 中键", "下一音 Z · 左键 + 中键", "下一音 超出音域 · 休止", "下一音 无", "下一音 无" };
            for (int i = 0; i < positions.Length; i++)
            {
                canvas.Position = positions[i];
                using (var frame = Render(canvas, dpi))
                using (var paused = Render(canvas, dpi))
                {
                    string current = (string)Call(canvas, "CurrentCueText"), next = (string)Call(canvas, "NextCueText");
                    Assert(current == currentTexts[i] && next == nextTexts[i], "固定指法须区分当前、休止、超域与旋律末尾：" + positions[i]);
                    Assert(EqualPixels(frame, paused), "当前/下一音提示须与暂停的音乐时间一起冻结。");
                    CheckCueFits(canvas, frame, "CurrentCueBounds", current);
                    CheckCueFits(canvas, frame, "NextCueBounds", next);
                }
            }
            Assert(canvas.TimelineDuration == 8 && canvas.Notes[0].Pitch == 75 && canvas.Notes[2].Pitch == 120,
                "显示提示不能删改原音符、超域音或尾静音时长。");
            canvas.Position = 0; canvas.Notes = new List<NoteEvent>();
            using (var empty = Render(canvas, dpi))
                Assert((string)Call(canvas, "CurrentCueText") == "当前 等待乐谱" && (string)Call(canvas, "NextCueText") == "下一音 等待乐谱",
                    "无音符时须等待乐谱，不能显示旋律结束。");

            canvas.SongTitle = "一首名字很长但仍需明确旋律来源的示例歌曲";
            foreach (string role in new[] { "旋律估计", "旋律候选" })
            {
                canvas.SongSubtitle = role + " · 独奏声轨 · 1200 个音符 · 4 个声轨";
                using (var frame = Render(canvas, dpi))
                {
                    string caption = (string)Call(canvas, "SongCaption");
                    Assert(caption.StartsWith(role + " · ", StringComparison.Ordinal), "不确定性标记须放在曲名前，长标题也不能隐藏它。");
                    var tooltip = (ToolTip)Field(canvas, "backgroundTip");
                    Assert(tooltip.GetToolTip(canvas).Contains(canvas.SongSubtitle), "完整曲目元数据须保留在谱面提示中。");
                    RectangleF roleArea = canvas.ToClient(new RectangleF(398, 10, 90, 24));
                    using (var normal = NewCanvas(compact, 0))
                    {
                        normal.SongTitle = canvas.SongTitle;
                        using (var baseline = Render(normal, dpi))
                            Assert(ChangedPixels(frame, baseline, roleArea) > 20, "候选/估计标记须在实际第一行中绘制。");
                    }
                }
            }
        }
    }

    private static void TestEffectiveSpeedCaption(float dpi, bool compact)
    {
        using (var canvas = NewCanvas(compact, 0))
        {
            canvas.Notes = new List<NoteEvent> { Note(60, 0, .3) };
            using (var normal = Render(canvas, dpi))
            {
                canvas.PlaybackSpeed = .75;
                using (var slower = Render(canvas, dpi))
                    Assert(ChangedPixels(normal, slower, canvas.ToClient(new RectangleF(990, 8, 194, 29))) > 10,
                        "完整与迷你谱面必须可见实际编配速度，不能只在隐藏的完整设置区提示降速。");
            }
            string caption = (string)Call(canvas, "PlaybackCaption");
            Assert(caption.Contains("0.75×") && caption.Contains(compact ? "2秒预览" : "4秒预览"),
                "实际倍率与固定预览时间必须分别说明。");
            using (var frame = Render(canvas, dpi))
            using (var graphics = Graphics.FromImage(frame))
            {
                float available = (float)typeof(ScoreCanvas).GetField("PlaybackStatusWidth", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
                Assert(graphics.MeasureString(caption, (Font)Field(canvas, "labelFont")).Width <= available - 4,
                    "实际倍率与预览时间必须完整显示，不能被省略号裁掉。");
            }
            canvas.Automatic = true;
            Assert(((string)Call(canvas, "PlaybackCaption")).StartsWith("自动"), "实际速度提示保留自动演奏状态。");
        }
    }

    private static void CheckCueFits(ScoreCanvas canvas, Bitmap image, string boundsName, string text)
    {
        RectangleF bounds = Bounds(canvas, boundsName), raw = RawBounds(canvas, boundsName);
        using (Graphics g = Graphics.FromImage(image))
        {
            Font font = (Font)Field(canvas, "smallFont");
            Assert(g.MeasureString(text, font).Width <= raw.Width - 4,
                "当前与下一音的完整组合键不得被省略：" + text);
        }
        Rectangle box = Pixels(image, bounds);
        int left = image.Width, right = -1, top = image.Height, bottom = -1;
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++)
        {
            Color pixel = image.GetPixel(x, y);
            // Header cues use gold, white, or pink; the shared light-text
            // detector intentionally excludes gold and is unsuitable here.
            if (pixel.R < 180 || pixel.G < 140) continue;
            left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        Rectangle ink = right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        Assert(!ink.IsEmpty && ink.Height >= 10 && ink.Top >= bounds.Top && ink.Bottom <= bounds.Bottom + 1,
            "固定指法须保留完整可读字形且不侵入相邻行：" + text);
    }

    private static void TestCurrentMouseLegend(float dpi, bool compact)
    {
        using (var canvas = NewCanvas(compact, 0))
        using (var idle = Render(canvas, dpi))
        {
            int[] pitches = { 60, 61, 48, 49, 74, 75 };
            for (int activeRow = 0; activeRow < pitches.Length; activeRow++)
            {
                canvas.Notes = new List<NoteEvent> { Note(pitches[activeRow], 0, .2) };
                using (var active = Render(canvas, dpi))
                    for (int row = 0; row < pitches.Length; row++)
                    {
                        RectangleF bounds = MouseRowInterior(canvas, row);
                        int changed = ChangedPixels(active, idle, bounds);
                        Assert(row == activeRow ? changed > bounds.Width : changed == 0,
                            "当前音必须精确高亮一个鼠标组合，包含不按鼠标的普通音：" + activeRow + "/" + row);
                    }
            }
            canvas.Notes = new List<NoteEvent> { Note(75, 0, .2), Note(120, 1, .2) };
            foreach (double position in new[] { .3, 1.1, 2.0 })
            {
                canvas.Position = position;
                using (var inactive = Render(canvas, dpi))
                    for (int row = 0; row < pitches.Length; row++)
                        Assert(ChangedPixels(inactive, idle, MouseRowInterior(canvas, row)) == 0,
                            "休止、超域及旋律结束时不得残留鼠标组合高亮。");
            }
        }
    }

    private static RectangleF MouseRowInterior(ScoreCanvas canvas, int row)
    {
        RectangleF bounds = canvas.ToClient((RectangleF)Call(canvas, "LegendRowBounds", row));
        // Fractional scaled boundaries otherwise make neighboring rows count
        // the same antialiased edge pixel twice.
        return RectangleF.FromLTRB((float)Math.Ceiling(bounds.Left), (float)Math.Ceiling(bounds.Top),
            (float)Math.Floor(bounds.Right), (float)Math.Floor(bounds.Bottom));
    }

    private static void TestDenseSameColumn(float dpi, bool compact)
    {
        using (var canvas = NewCanvas(compact, .4))
        {
            canvas.Notes = new List<NoteEvent> {
                Note(52, .95, .07), Note(64, 1.07, .07), Note(76, 1.19, .07) };
            using (var image = Render(canvas, dpi))
            {
                Array cached = (Array)Field(canvas, "visuals");
                object first = cached.GetValue(0), second = cached.GetValue(1), third = cached.GetValue(2);
                Assert((int)Field(first, "KeyIndex") == (int)Field(second, "KeyIndex") &&
                    (int)Field(second, "KeyIndex") == (int)Field(third, "KeyIndex"),
                    "低、中、高八度的同一实际键必须共用一列。");
                Assert((int)Field(first, "Lane") != (int)Field(second, "Lane") &&
                    (int)Field(second, "Lane") != (int)Field(third, "Lane"),
                    "测试样本须确实覆盖不同鼠标八度修饰。");
                Assert((int)Field(first, "NextInColumn") == 1 && (int)Field(second, "NextInColumn") == 2,
                    "同列下一音索引不能被鼠标修饰拆分。");
                RectangleF a = Head(canvas, 0, false), b = Head(canvas, 1, false);
                Assert(a.Height > 0 && b.Height > 0 && a.Top > b.Bottom,
                    "密集同列卡片必须缩短并保留真实时间间隔，不能强制最低卡高。");
                Assert(CountNoteInk(image, CardTextArea(canvas, a, 1)) == 0 && CountNoteInk(image, CardTextArea(canvas, b, 0)) == 0,
                    "密集短条不得出现被压扁或切碎的按键文字。");
                using (var baseline = RenderComponent(canvas, dpi, -1))
                using (var one = RenderComponent(canvas, dpi, 0))
                using (var two = RenderComponent(canvas, dpi, 1))
                {
                    Assert(ChangedPixels(one, baseline, a) > 0 && ChangedPixels(two, baseline, b) > 0,
                        "无文字的短条仍应真实绘制，不能以删掉音符掩盖拥挤。");
                    Assert(OverlappingInk(one, two, baseline, Stage(canvas)) == 0,
                        "不同鼠标修饰的相邻同列短条在实际像素上不得重叠。");
                }
                object before = Field(canvas, "visuals");
                canvas.Position += .01;
                using (var moved = Render(canvas, dpi))
                    Assert(Object.ReferenceEquals(before, Field(canvas, "visuals")),
                        "动画前进不能重新扫描整曲构建同列索引。");
            }
        }
    }

    private static void TestHeldNoteSeparation(float dpi)
    {
        using (var canvas = NewCanvas(false, .88))
        {
            canvas.Notes = new List<NoteEvent> { Note(64, 0, 1), Note(52, 1.02, .2) };
            using (var image = Render(canvas, dpi))
            {
                RectangleF held = Head(canvas, 0, true), future = Head(canvas, 1, false);
                Near(held.Bottom, Value(canvas, "JudgmentY"), "当前长音卡片必须始终停留在真实判定线上方。");
                Assert(held.Top > future.Bottom, "当前长音与临近的未来同列音必须保持实时分离。");
                Assert(CountNoteInk(image, CardTextArea(canvas, held, 0)) == 0, "长音与下一音太近时，卡面应退化为短条而不留下文字碎片。");
                using (var baseline = RenderComponent(canvas, dpi, -1))
                using (var one = RenderComponent(canvas, dpi, 0))
                using (var two = RenderComponent(canvas, dpi, 1))
                    Assert(OverlappingInk(one, two, baseline, Stage(canvas)) == 0,
                        "长音尾条和下一音卡片也不得互相覆盖。");
                float height = held.Height;
                canvas.Position = .9999;
                using (var late = Render(canvas, dpi))
                {
                    RectangleF later = Head(canvas, 0, true);
                    Assert(later.Height < height && later.Top > Head(canvas, 1, false).Bottom,
                        "下一音接近判定线时，停留长音必须按剩余实时距离继续缩短。");
                    Assert(CountNoteInk(late, CardTextArea(canvas, later, 0)) == 0, "长音结束前的极短卡片不得恢复半截标签。");
                }
            }
        }
    }

    private static void TestOnsetAndTopEdge(float dpi)
    {
        using (var canvas = NewCanvas(false, 1))
        {
            canvas.Notes = new List<NoteEvent> { Note(64, 1, .7) };
            using (var onset = Render(canvas, dpi))
            {
                RectangleF head = Head(canvas, 0, true);
                Near(head.Bottom, canvas.TimeToY(1), "起音卡片底边必须与真实起音时间映射一致。");
                Assert(head.Top >= Value(canvas, "StageTop") && CountNoteInk(onset, CardTextArea(canvas, head, 0)) > 15,
                    "空间充足的起音卡片必须保留完整可读的按键和音级。");
                RectangleF gapBelow = new RectangleF(head.X, head.Bottom + 5, head.Width, 3);
                using (var isolated = RenderComponent(canvas, dpi, 0))
                    Assert(CountNoteInk(isolated, gapBelow) == 0,
                        "起音卡片文字不得伸入判定线下方或键盘区域。");
            }
            canvas.Position = .4;
            canvas.Notes = new List<NoteEvent> { Note(64, 4.38, .1) };
            using (var edge = Render(canvas, dpi))
            {
                RectangleF partial = Head(canvas, 0, false);
                Assert(partial.Top < Value(canvas, "StageTop") && partial.Bottom > Value(canvas, "StageTop"),
                    "顶边测试样本须只让卡片的一小部分进入瀑布。");
                Assert(CountNoteInk(edge, RectangleF.Intersect(CardTextArea(canvas, partial, 0), Stage(canvas))) == 0,
                    "顶部半张卡片必须跳过文字，不能留下半截按键或数字。");
            }
            canvas.Notes = new List<NoteEvent> { Note(64, 3.3, .1) };
            using (var full = Render(canvas, dpi))
                Assert(CountNoteInk(full, CardTextArea(canvas, Head(canvas, 0, false), 0)) > 15,
                    "卡片完整进入可视区后必须显示标签，不能永久隐藏全部文字。");
            Near(canvas.TimeToY(canvas.Position + 4), Value(canvas, "StageTop"),
                "DPI和底部留白变化不得改变完整模式的4秒音乐窗口。");
        }
    }

    private static void TestWarningSeparation(float dpi)
    {
        using (var canvas = NewCanvas(false, .4))
        {
            canvas.Notes = new List<NoteEvent> {
                Note(64, .9, .05), Note(127, 1.02, .05), Note(64, 1.14, .05) };
            using (var image = Render(canvas, dpi))
            using (var baseline = RenderComponent(canvas, dpi, -1))
            using (var playable = RenderComponent(canvas, dpi, 0))
            using (var warning = RenderComponent(canvas, dpi, 1))
            using (var next = RenderComponent(canvas, dpi, 2))
            {
                Assert(OverlappingInk(playable, warning, baseline, Stage(canvas)) == 0,
                    "全宽超域警告不能覆盖此前正常列中的卡片。");
                Assert(OverlappingInk(warning, next, baseline, Stage(canvas)) == 0,
                    "全宽超域警告不能侵入下一正常音符。");
                Assert(BrightGlyphPixels(warning, Stage(canvas)) == 0,
                    "密集的超域短提示不得绘制被裁切的警告文字。");
                Assert(ChangedPixels(warning, baseline, Stage(canvas)) > 0,
                    "超域音符仍须留下同步警告标记，不能被静默丢弃。");
            }
        }
    }

    private static void TestFooterInk(float dpi, bool compact)
    {
        using (var canvas = NewCanvas(compact, .4))
        {
            canvas.Notes = new List<NoteEvent> { Note(64, 2, .1) };
            using (var image = Render(canvas, dpi))
            using (var footer = RenderFooter(canvas, dpi))
            {
                RectangleF keyboard = Bounds(canvas, "KeyboardBounds"), hint = Bounds(canvas, "HintBounds"),
                    legend = Bounds(canvas, "MouseLegendBounds"), stage = Stage(canvas), progress = Bounds(canvas, "ProgressBounds");
                Assert(hint.Bottom <= keyboard.Top && legend.Contains(hint) && legend.Contains(keyboard),
                    "鼠标规则和八键必须位于常驻左侧图例中，互不覆盖。");
                Rectangle actualInk = LightTextBounds(footer, hint);
                Assert(!actualInk.IsEmpty && actualInk.Height >= 10,
                    "完整和迷你左侧规则必须保留实际可读字形，不能用空面板替代说明。");
                Assert(actualInk.Top >= hint.Top && actualInk.Bottom <= hint.Bottom,
                    "鼠标规则的真实像素不得侵入其他图例行。");
                RectangleF rightFooter = RectangleF.FromLTRB(stage.Left, stage.Bottom + 4 * canvas.LayoutScale,
                    stage.Right, progress.Bottom);
                Assert(InkBounds(footer, rightFooter, StageColor).IsEmpty,
                    "左右统一布局后，不得在瀑布下方重复绘制字母键或鼠标说明。");
                float future = compact ? 2 : 4;
                Near(canvas.TimeToY(canvas.Position + future), Value(canvas, "StageTop"),
                    "完整和迷你模式在高DPI下必须分别保持4秒与2秒时间窗口。");
                using (var paused = Render(canvas, dpi))
                    Assert(EqualPixels(image, paused), "高DPI下暂停的整个瀑布必须保持像素一致。");
            }
        }
    }

    private static void TestDenseCurrentWindow(float dpi)
    {
        using (var canvas = NewCanvas(false, .90004))
        {
            var notes = new List<NoteEvent>(10000);
            for (int i = 0; i < 10000; i++) notes.Add(Note(64, i * .0001, .00008));
            canvas.Notes = notes;
            using (var image = Render(canvas, dpi))
            {
                Assert((int)Call(canvas, "FirstStartingAfter", canvas.Position) - 1 == 9000,
                    "极密集片段中段须正确找到实际当前音，而非被过去绘制预算截掉。");
                Assert((string)Call(canvas, "CurrentCueText") == "当前 C · 不按鼠标" &&
                    (string)Call(canvas, "NextCueText") == "下一音 C · 不按鼠标",
                    "密集音符退化为短条时，当前与下一音的完整指法仍须保留在固定提示。");
                RectangleF active = Head(canvas, 9000, true);
                Near(active.Bottom, Value(canvas, "JudgmentY"), "极密集段的当前音仍须对齐判定线。");
                using (var noNotes = NewCanvas(false, canvas.Position))
                {
                    noNotes.Notes = new List<NoteEvent> { Note(64, 100, .1) };
                    using (var baseline = Render(noNotes, dpi))
                    {
                        float keyWidth = Value(canvas, "ColumnWidth");
                        float center = Value(canvas, "StageLeft") + 2.5f * keyWidth;
                        float hit = Value(canvas, "JudgmentY");
                        Assert(ChangedPixels(image, baseline, new RectangleF(center - 12, hit - 4, 24, 8)) > 10,
                            "极密集段的当前起音反馈须在真实像素上保留。");
                        float futureTop = canvas.TimeToY(notes[notes.Count - 1].Start) - 1;
                        Assert(ChangedPixels(image, baseline, new RectangleF(center + 25, futureTop, 20, Math.Max(1, hit - 1 - futureTop))) > 0,
                            "过去1600音符不能耗尽绘制预算并隐藏近未来短条。");
                    }
                }
                object before = Field(canvas, "visuals");
                canvas.Position += .00001;
                using (var frame = Render(canvas, dpi))
                    Assert(Object.ReferenceEquals(before, Field(canvas, "visuals")),
                        "极密集片段的下一帧必须复用同列缓存，不能逐帧重建。");
            }
        }
    }

    private static void TestUniformCardGlyphs(float dpi, bool compact)
    {
        using (var canvas = NewCanvas(compact, .4))
        using (var expectedFont = new Font("Microsoft YaHei UI", 22 * canvas.LayoutScale, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var expected = RenderGlyphReference("C", 96, expectedFont, NoteInkColor))
        {
            canvas.Notes = new List<NoteEvent> { Note(64, .9, .1) };
            using (var calibration = Render(canvas, dpi))
            {
                // Give the first card just enough room for the uniformly fitted
                // glyph, and the last card all available space. The rendered
                // glyphs must agree even though their card heights differ.
                float targetHeight = ((float)Math.Ceiling(((Font)Field(canvas, "noteFont")).GetHeight(96)) + 4) * canvas.LayoutScale;
                float spacing = (targetHeight + 6 * canvas.LayoutScale) / Value(canvas, "PixelsPerSecond");
                canvas.Notes = new List<NoteEvent> {
                    Note(64, .9, .1), Note(64, .9 + spacing, .1), Note(64, 1.7, .1) };
            }
            using (var image = Render(canvas, dpi))
            {
                RectangleF narrow = Head(canvas, 0, false), roomy = Head(canvas, 2, false);
                Assert(narrow.Height + .5f < roomy.Height,
                    "字号回归须真实覆盖不同高度卡片，不能只比较完全相同的卡片；DPI=" + dpi +
                    "，迷你=" + compact + "，短卡=" + narrow.Height + "，宽裕卡=" + roomy.Height + "。");
                Rectangle expectedInk = NoteInkBounds(expected, new RectangleF(0, 0, expected.Width, expected.Height));
                Rectangle firstInk = NoteInkBounds(image, KeyGlyphRegion(canvas, narrow));
                Rectangle lastInk = NoteInkBounds(image, KeyGlyphRegion(canvas, roomy));
                Assert(!firstInk.IsEmpty && !lastInk.IsEmpty,
                    "可读高度的短卡和宽裕卡都应显示完整C，不能将整条旋律标签全部隐藏：" + dpi + "/" + compact + "/" + narrow + "/" + roomy + "/" + firstInk + "/" + lastInk);
                Assert(Math.Abs(firstInk.Height - lastInk.Height) <= 1 && Math.Abs(firstInk.Width - lastInk.Width) <= 1,
                    "同一列相邻卡片的实际C字形大小必须一致，不能按卡高切换字号：" + dpi + "/" + compact + "/" + firstInk + "/" + lastInk);
                Assert(Math.Abs(firstInk.Height - expectedInk.Height) <= 1 && Math.Abs(lastInk.Height - expectedInk.Height) <= 1 &&
                    Math.Abs(firstInk.Width - expectedInk.Width) <= 1 && Math.Abs(lastInk.Width - expectedInk.Width) <= 1,
                    "同一等比布局中的字形必须匹配整体缩放后的参考字体，不能根据卡片高度改变字号。");
                RectangleF second = Head(canvas, 1, false);
                Assert(narrow.Top - second.Bottom >= 5.5f * canvas.LayoutScale,
                    "普通可读的连续同列卡片必须留出明显间隔，不能恢复两框紧贴的效果。");
                // Test whole pixel rows in the gap, excluding the legitimate
                // anti-aliased edge pixels immediately beside each card.
                RectangleF separator = RectangleF.FromLTRB(narrow.X + 12 * canvas.LayoutScale,
                    (float)Math.Ceiling(second.Bottom + canvas.LayoutScale), narrow.Right - 12 * canvas.LayoutScale,
                    (float)Math.Floor(narrow.Top - canvas.LayoutScale));
                Assert(separator.Height >= 2,
                    "普通连续卡片之间须至少保留两行真正空白像素，不能只形成亚像素缝隙。");
                using (var baseline = RenderComponent(canvas, dpi, -1))
                    Assert(ChangedPixels(image, baseline, separator) == 0,
                        "连续同列卡片之间的明显间隔必须是真实空白，不能被光晕、描边或文字填回；DPI=" + dpi +
                        "，迷你=" + compact + "，间隔=" + separator + "，不同像素=" + ChangedPixels(image, baseline, separator) + "。");
                canvas.Notes = new List<NoteEvent> { Note(64, .9, .1), Note(64, 1.12, .1) };
                using (var dense = Render(canvas, dpi))
                    Assert(CountNoteInk(dense, CardTextArea(canvas, Head(canvas, 0, false), 0)) == 0,
                        "空间低于统一字号要求时应整段省略字形，不能恢复较小C字号。");
            }
        }
    }

    private static void TestFixedKeysContrast(float dpi, bool compact)
    {
        using (var canvas = NewCanvas(compact, .4))
        using (var keyFont = new Font("Microsoft YaHei UI", 20 * canvas.LayoutScale, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var referenceKey = RenderGlyphReference("C", 96, keyFont))
        {
            canvas.Notes = new List<NoteEvent> { Note(64, 2, .1) };
            using (var paint = Render(canvas, dpi))
            using (var keys = RenderFooter(canvas, dpi))
            {
                for (int key = 0; key < 8; key++)
                {
                    RectangleF cell = KeyCell(canvas, key);
                    RectangleF degree = canvas.ToClient(new RectangleF(RawKeyCell(canvas, key).X + 3, RawKeyCell(canvas, key).Y, 31, 28));
                    RectangleF button = canvas.ToClient(new RectangleF(RawKeyCell(canvas, key).X + 36, RawKeyCell(canvas, key).Y, 41, 28));
                    Rectangle keyInk = key == 7 ? LightTextBounds(keys, button) : WhiteBounds(keys, button);
                    Assert(CountGoldText(keys, degree) >= 12 && !keyInk.IsEmpty && CountLightText(keys, button) >= (key == 7 ? 2 : 12),
                        "左侧八个按键均须显示独立音级和实际键位，不得缩窗后缺失：" + key + "/" + dpi + "/" + compact + "/" + cell);
                    Assert(keyInk.Height >= (key == 7 ? 2 : 9), "默认迷你和完整布局的实际字母键须保留清晰可读的字形。");
                }
                RectangleF cRaw = RawKeyCell(canvas, 2);
                RectangleF cKey = canvas.ToClient(new RectangleF(cRaw.X + 36, cRaw.Y, 41, 28));
                Rectangle cInk = WhiteBounds(keys, cKey);
                Rectangle expectedKey = WhiteBounds(referenceKey, new RectangleF(0, 0, referenceKey.Width, referenceKey.Height));
                Assert(Math.Abs(cInk.Height - expectedKey.Height) <= 1,
                    "完整/迷你和各DPI的字母键须匹配整张参考布局的同比缩放字号。");
                int brightHint = CountLightText(keys, Bounds(canvas, "HintBounds"));
                Assert(brightHint > 40,
                    "左侧鼠标规则须使用清晰浅色文字。");
                canvas.Position = 2;
                using (var active = Render(canvas, dpi))
                {
                    RectangleF activeCell = KeyCell(canvas, 2);
                    Assert(ChangedPixels(active, paint, activeCell) > 30,
                        "当前按键须有清晰卡面或边框反馈，固定白字仍保持可读。");
                    Assert(CountWhite(active, activeCell) >= 12,
                        "当前按键不得把固定白字改回低亮度声轨颜色。");
                }
            }
        }
    }

    private static RectangleF KeyGlyphRegion(ScoreCanvas canvas, RectangleF head)
    { float scale = canvas.LayoutScale; return new RectangleF(head.X + 6 * scale, head.Y + 4 * scale,
        Math.Min(24 * scale, head.Width - 12 * scale), Math.Max(0, head.Height - 8 * scale)); }
    private static RectangleF CardTextArea(ScoreCanvas canvas, RectangleF head, int mask)
    {
        float scale = canvas.LayoutScale;
        float icon = mask == 0 ? 0 : Math.Min(22 * scale, head.Width * .30f);
        return new RectangleF(head.X + icon + 5 * scale, head.Y + 3 * scale,
            Math.Max(0, head.Width - icon - 10 * scale), Math.Max(0, head.Height - 6 * scale));
    }

    private static void TestReferenceRegions(float dpi)
    {
        foreach (Size size in new[] { new Size(868, 256), new Size(868, 340), new Size(1000, 272), new Size(1000, 300),
            new Size(1050, 370), new Size(1248, 520), new Size(1550, 390), new Size(1550, 690), new Size(1032, 900) })
        foreach (bool compact in new[] { false, true })
        using (var canvas = new ScoreCanvas { Size = size, Compact = compact, Position = .4,
            SongTitle = "真实曲名 · 主旋律", SongSubtitle = "多音轨与按键说明" })
        {
            canvas.Notes = new List<NoteEvent> { Note(60, 2, .7) };
            using (var image = Render(canvas, dpi))
            {
                RectangleF waterfall = Bounds(canvas, "WaterfallBounds"), keyboard = Bounds(canvas, "KeyboardBounds"),
                    hints = Bounds(canvas, "HintBounds"), legend = Bounds(canvas, "MouseLegendBounds"),
                    degrees = Bounds(canvas, "DegreeBounds"), progress = Bounds(canvas, "ProgressBounds"), fitted = canvas.FittedBounds;
                foreach (RectangleF area in new[] { waterfall, keyboard, hints, legend, degrees, progress })
                    Assert(area.Left >= 0 && area.Top >= 0 && area.Right <= canvas.Width + .001f && area.Bottom <= canvas.Height + .001f,
                        "等比 Fit 后瀑布和全部图例须完整留在实际窗口内：" + size + "/" + dpi + "/" + area);
                Near(fitted.Width / fitted.Height, 1200.0 / 360, "各种窗口比例下参考布局的宽高比必须完全一致。");
                Near(waterfall.Width / waterfall.Height, 796.0 / 219, "右側瀑布须与整张参考布局保持同一缩放比例。");
                Assert(waterfall.Height >= 150 && waterfall.Width >= 560 && keyboard.Height >= 42,
                    "默认迷你与支持的各比例窗口仍须保留足够大的瀑布和可读图例：" + size + "/" + dpi);
                Assert(!waterfall.IntersectsWith(legend) && !waterfall.IntersectsWith(degrees) && !keyboard.IntersectsWith(hints),
                    "左侧图例、瀑布和判定音级必须是独立区域。");
                Assert(legend.Right + 10 * canvas.LayoutScale <= waterfall.Left && legend.Contains(keyboard) && legend.Contains(hints),
                    "鼠标和键盘解释统一放在左侧，不可遮挡右侧落音。");
                for (int row = 0; row < 6; row++)
                {
                    RectangleF rowBounds = canvas.ToClient((RectangleF)Call(canvas, "LegendRowBounds", row));
                    Rectangle ink = LightTextBounds(image, rowBounds);
                    Assert(legend.Contains(rowBounds) && !ink.IsEmpty && ink.Height >= 10 && CountLightText(image, rowBounds) >= 20,
                        "完整和迷你六条鼠标图例均须真实渲染、可读且完整：" + row + "/" + size + "/" + dpi);
                    Assert(ink.Top >= rowBounds.Top && ink.Bottom <= rowBounds.Bottom + .01f,
                        "每条鼠标图例实际字形不得越界碰到相邻行。");
                }
                for (int i = 0; i < 8; i++)
                {
                    RectangleF cell = KeyCell(canvas, i);
                    Assert(legend.Contains(cell) && CountLightText(image, cell) >= (i == 7 ? 2 : 12),
                        "各比例窗口左侧八键均须保留对应字母或逗号实际字形：" + i + "/" + size + "/" + dpi);
                    RectangleF degree = new RectangleF(degrees.Left + i * waterfall.Width / 8, degrees.Top, waterfall.Width / 8, degrees.Height);
                    Assert(CountWhite(image, degree) >= 12, "判定线处仍保留八列音级以对应左侧键盘。");
                }
            }
        }
    }

    private static void TestModifierLegend(float dpi)
    {
        Type canvasType = typeof(ScoreCanvas);
        var fingers = (Fingering[])canvasType.GetField("LegendFingers", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        Assert(fingers.Length == 6, "图例必须覆盖普通、半音及两种八度与半音的六种组合。");
        int[] pitches = { 60, 61, 48, 49, 74, 75 };
        var masks = new List<int>();
        var icons = new List<Bitmap>();
        try
        {
            for (int index = 0; index < fingers.Length; index++)
            {
                Fingering expected = HarmonicaMap.Map(pitches[index], 60);
                Assert(fingers[index].Key == expected.Key && fingers[index].Number == expected.Number && fingers[index].Modifier == expected.Modifier,
                    "图例每行须依据真实引擎指法，包括原有逗号键和鼠标八度规则。");
                int mask = (int)StaticCall("ModifierMask", expected);
                Assert(!masks.Contains(mask), "六种鼠标组合必须使用不同状态，不能出现左右八度同符号。");
                masks.Add(mask);
                string buttons = (string)StaticCall("MouseButtonsText", mask, false);
                Assert(index == 0 ? buttons.Contains("不按") : buttons.Contains("键"), "图例须明确说明需要按哪个鼠标键。");
                if (expected.Modifier.Contains("左键")) Assert(buttons.Contains("左"), "低八度组合必须说明左键。");
                if (expected.Modifier.Contains("右键")) Assert(buttons.Contains("右"), "高八度组合必须说明右键。");
                if (expected.Modifier.Contains("中键")) Assert(buttons.Contains("中") || buttons.Contains("滚轮"), "升半音必须说明鼠标中键而非滚动滚轮。");
                string meaning = (string)StaticCall("ModifierMeaning", mask);
                Assert(!String.IsNullOrWhiteSpace(meaning) && (!expected.Modifier.Contains("八度") || meaning.Contains("八度")),
                    "音高含义须准确披露低/高八度，不能只写容易误解的降调或升调。");
                Bitmap icon = NewBitmap(64, 40, dpi); icons.Add(icon);
                using (Graphics g = Graphics.FromImage(icon))
                {
                    Prepare(g);
                    StaticCall("DrawModifierIcon", g, new RectangleF(5, 5, 54, 30), mask, Color.FromArgb(255, 248, 221));
                }
                for (int previous = 0; previous < index; previous++)
                    Assert(!EqualPixels(icon, icons[previous]), "实际图标像素须区分所有鼠标组合。");
            }
            using (var canvas = NewCanvas(false, .4))
            {
                canvas.Notes = new List<NoteEvent>();
                for (int i = 0; i < pitches.Length; i++) canvas.Notes.Add(Note(pitches[i], .7 + i * .2, .1));
                using (var image = Render(canvas, dpi))
                {
                    Array visuals = (Array)Field(canvas, "visuals");
                    for (int i = 0; i < pitches.Length; i++)
                        Assert((int)Field(visuals.GetValue(i), "MouseMask") == masks[i],
                            "实际落音缓存的鼠标符号须与左侧说明一致，不能只有静态图例正确。");
                }
            }
        }
        finally { foreach (Bitmap icon in icons) icon.Dispose(); }
    }

    private static object StaticCall(string name, params object[] args)
    { return typeof(ScoreCanvas).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args); }

    private static void TestPersistentFingeringHints(float dpi)
    {
        string text = (string)typeof(ScoreCanvas).GetField("FingeringHintText", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
        Assert(!text.Contains("#") && text.Contains("图标表示鼠标") && text.Contains("字母是键盘") &&
            text.Contains("1′ = 高音1") && text.Contains("逗号键"),
            "主流程应只使用六种图例、基础键位音级和必要高音1说明，不能重复加入#教学。");
        Assert(text.Contains("中键 = 按下滚轮") && text.Contains("+ = 同时按住"),
            "窄迷你与完整参考窗均须常驻解释鼠标中键按下及组合同时按住规则。");
        string rule = (string)typeof(ScoreCanvas).GetField("MouseRuleText", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
        foreach (Size size in new[] { new Size(868, 256), new Size(868, 340), new Size(1000, 300),
            new Size(1050, 370), new Size(1248, 486) })
        using (var canvas = new ScoreCanvas { Size = size, Compact = true, Position = .2 })
        {
            canvas.Notes = new List<NoteEvent> { Note(63, 0, .8) };
            using (var image = Render(canvas, dpi))
            {
                RectangleF area = Bounds(canvas, "FingeringHintBounds"), hints = Bounds(canvas, "HintBounds");
                Assert(hints.Contains(area) && Bounds(canvas, "MouseLegendBounds").Contains(area) && area.Bottom <= Bounds(canvas, "KeyboardBounds").Top,
                    "常驻统一鼠标规则须在左侧拥有独立完整的一行，不得盖住八键。" + size + "/" + dpi);
                using (Graphics g = Graphics.FromImage(image))
                    Assert(g.MeasureString(rule, (Font)Field(canvas, "hintFont")).Width * canvas.LayoutScale <= area.Width - 4 * canvas.LayoutScale,
                        "最窄迷你窗口应完整显示中键按下和同时组合规则，不能通过省略号藏掉说明。" + size + "/" + dpi);
                Rectangle ink = LightTextBounds(image, area);
                Assert(!ink.IsEmpty && ink.Width > area.Width * .55f && ink.Top >= area.Top && ink.Bottom <= area.Bottom,
                    "真实绘制必须出现整行常驻指法说明，不能只增加字符串或空白区域。" + size + "/" + dpi);
                Assert(CountLightText(image, area) > 150,
                    "统一指法解释应以现有可读字号真实渲染，不能藏在悬浮提示或仅留极少文字。" + size + "/" + dpi);
            }
        }
    }

    private static void TestNarrowUnifiedFingering(float dpi)
    {
        using (var canvas = new ScoreCanvas { Size = new Size(1050, 370), Position = .4 })
        using (var font = new Font("Microsoft YaHei UI", 22 * canvas.LayoutScale, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var expected = RenderGlyphReference("2", 96, font, NoteInkColor))
        {
            canvas.Notes = new List<NoteEvent> { Note(75, 1.1, .1) };
            using (var image = Render(canvas, dpi))
            using (var isolated = RenderComponent(canvas, dpi, 0))
            {
                Array visuals = (Array)Field(canvas, "visuals");
                Fingering finger = (Fingering)Field(visuals.GetValue(0), "Finger");
                Assert(finger.Number == "#2'" && finger.Key == "X", "窄列示例须真实覆盖需要右键+中键的升高八度#2。");
                Assert((string)StaticCall("DegreeForKey", (int)Field(visuals.GetValue(0), "KeyIndex")) == "2",
                    "该真实升高八度半音在演奏音符块中必须显示基础音级2，修饰交给实心右箭头。");
                RectangleF head = Head(canvas, 0, false);
                float keyWidth;
                using (Graphics g = Graphics.FromImage(image)) keyWidth = (float)Math.Ceiling(g.MeasureString(finger.Key, font).Width);
                float scale = canvas.LayoutScale;
                float iconWidth = Math.Min(22 * scale, head.Width * .30f);
                RectangleF numberArea = new RectangleF(head.X + keyWidth + iconWidth + 6 * scale, head.Y + 4 * scale,
                    head.Width - keyWidth - iconWidth - 10 * scale, head.Height - 8 * scale);
                Rectangle actualInk = NoteInkBounds(isolated, numberArea);
                Rectangle expectedInk = NoteInkBounds(expected, new RectangleF(0, 0, expected.Width, expected.Height));
                Assert(!actualInk.IsEmpty && Math.Abs(actualInk.Width - expectedInk.Width) <= 1 && Math.Abs(actualInk.Height - expectedInk.Height) <= 1,
                    "窄列也须完整显示统一字号的基础音级2，不能丢掉字形或再次附加#和高音记号。" + dpi + "/" + head + "/" + actualInk + "/" + expectedInk);
                Assert(CountNoteInk(isolated, new RectangleF(head.X + 4 * scale, head.Y + 3 * scale, iconWidth, head.Height - 6 * scale)) > 12,
                    "去掉#和额外八度记号后，窄列实心右箭头必须真实保留，不能把高八度+半音修饰静默删除。");
            }
        }
    }

    private static Bitmap RenderGlyphReference(string text, float dpi, Font font)
    { return RenderGlyphReference(text, dpi, font, Color.FromArgb(255, 248, 221)); }
    private static Bitmap RenderGlyphReference(string text, float dpi, Font font, Color color)
    {
        Bitmap bitmap = NewBitmap(180, 90, dpi);
        using (Graphics g = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(color))
        using (var format = new StringFormat(StringFormat.GenericDefault))
        {
            Prepare(g); format.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(text, font, brush, new RectangleF(10, 10, 160, 65), format);
        }
        return bitmap;
    }

    private static ScoreCanvas NewCanvas(bool compact, double position)
    {
        return new ScoreCanvas { Compact = compact, Position = position,
            Size = compact ? new Size(868, 256) : new Size(1248, 372) };
    }
    private static NoteEvent Note(int pitch, double start, double duration)
    { return new NoteEvent { Pitch = pitch, Start = start, Duration = duration }; }
    private static RectangleF Head(ScoreCanvas canvas, int index, bool active)
    { return canvas.ToClient((RectangleF)Call(canvas, "HeadBounds", ((Array)Field(canvas, "visuals")).GetValue(index), active)); }
    private static RectangleF RawKeyCell(ScoreCanvas canvas, int index)
    { return (RectangleF)Call(canvas, "KeyLegendCell", index); }
    private static RectangleF KeyCell(ScoreCanvas canvas, int index)
    { return canvas.ToClient(RawKeyCell(canvas, index)); }
    private static RectangleF Stage(ScoreCanvas canvas)
    { return Bounds(canvas, "WaterfallBounds"); }

    // DrawToBitmap can acquire a window HDC at the host DPI. Calling the real
    // paint handler with an explicitly configured Graphics tests font scaling.
    private static Bitmap Render(ScoreCanvas canvas, float dpi)
    {
        Bitmap bitmap = NewBitmap(canvas.Width, canvas.Height, dpi);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(StageColor);
            Call(canvas, "OnPaint", new PaintEventArgs(g, new Rectangle(Point.Empty, canvas.Size)));
        }
        return bitmap;
    }
    private static Bitmap RenderComponent(ScoreCanvas canvas, float dpi, int index)
    {
        Bitmap bitmap = NewBitmap(canvas.Width, canvas.Height, dpi);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            Prepare(g);
            canvas.ApplyFit(g);
            Call(canvas, "DrawStage", g, RawValue(canvas, "StageTop"), RawValue(canvas, "JudgmentY"));
            g.SetClip(RawBounds(canvas, "WaterfallBounds"), CombineMode.Intersect);
            if (index >= 0)
            {
                Array cached = (Array)Field(canvas, "visuals");
                int currentIndex = (int)Call(canvas, "FirstStartingAfter", canvas.Position) - 1;
                object current = currentIndex >= 0 && canvas.Position < canvas.Notes[currentIndex].Start + canvas.Notes[currentIndex].Duration
                    ? cached.GetValue(currentIndex) : null;
                Call(canvas, "DrawNote", g, cached.GetValue(index), index, current);
            }
        }
        return bitmap;
    }
    private static Bitmap RenderFooter(ScoreCanvas canvas, float dpi)
    {
        Bitmap bitmap = NewBitmap(canvas.Width, canvas.Height, dpi);
        using (Graphics g = Graphics.FromImage(bitmap))
        { Prepare(g); canvas.ApplyFit(g); Call(canvas, "DrawMouseLegend", g); Call(canvas, "DrawKeyStrip", g, null); }
        return bitmap;
    }
    private static Bitmap NewBitmap(int width, int height, float dpi)
    { var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb); bitmap.SetResolution(dpi, dpi); return bitmap; }
    private static void Prepare(Graphics g)
    { g.Clear(StageColor); g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; }
    private static Rectangle Pixels(Bitmap image, RectangleF region)
    {
        if (region.Width <= 0 || region.Height <= 0) return Rectangle.Empty;
        return Rectangle.Intersect(new Rectangle(0, 0, image.Width, image.Height), Rectangle.FromLTRB(
            (int)Math.Floor(region.Left), (int)Math.Floor(region.Top), (int)Math.Ceiling(region.Right), (int)Math.Ceiling(region.Bottom)));
    }
    private static int CountWhite(Bitmap image, RectangleF region)
    {
        int count = 0; Rectangle box = Pixels(image, region);
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++)
        { Color c = image.GetPixel(x, y); if (IsText(c)) count++; }
        return count;
    }
    private static int CountGoldText(Bitmap image, RectangleF region)
    {
        int count = 0; Rectangle box = Pixels(image, region);
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++)
        { Color c = image.GetPixel(x, y); if (c.R >= 220 && c.G >= 175 && c.B >= 75 && c.B <= 150) count++; }
        return count;
    }
    private static int CountNoteInk(Bitmap image, RectangleF region)
    {
        int count = 0; Rectangle box = Pixels(image, region);
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++)
            if (IsNoteInk(image.GetPixel(x, y))) count++;
        return count;
    }
    private static Rectangle NoteInkBounds(Bitmap image, RectangleF region)
    {
        int left = image.Width, right = -1, top = image.Height, bottom = -1; Rectangle box = Pixels(image, region);
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++)
            if (IsNoteInk(image.GetPixel(x, y)))
            { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }
    private static int CountLightText(Bitmap image, RectangleF region)
    {
        int count = 0; Rectangle box = Pixels(image, region);
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++)
        { Color c = image.GetPixel(x, y); if (c.R >= 180 && c.G >= 180 && c.B >= 170) count++; }
        return count;
    }
    private static Rectangle WhiteBounds(Bitmap image, RectangleF region)
    {
        int left = image.Width, right = -1, top = image.Height, bottom = -1; Rectangle box = Pixels(image, region);
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++)
        {
            Color c = image.GetPixel(x, y);
            if (IsText(c))
            { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }
    private static Rectangle LightTextBounds(Bitmap image, RectangleF region)
    {
        int left = image.Width, right = -1, top = image.Height, bottom = -1; Rectangle box = Pixels(image, region);
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++)
        {
            Color color = image.GetPixel(x, y);
            if (color.R >= 180 && color.G >= 180 && color.B >= 170)
            { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }
    private static int BrightGlyphPixels(Bitmap image, RectangleF region)
    {
        int count = 0; Rectangle box = Pixels(image, region);
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++)
        { Color c = image.GetPixel(x, y); if (c.R > 210 && c.G > 135 && c.B > 180) count++; }
        return count;
    }
    private static int ChangedPixels(Bitmap image, Bitmap baseline, RectangleF region)
    {
        int count = 0; Rectangle box = Pixels(image, region);
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++)
            if (image.GetPixel(x, y) != baseline.GetPixel(x, y)) count++;
        return count;
    }
    private static int OverlappingInk(Bitmap first, Bitmap second, Bitmap baseline, RectangleF region)
    {
        int count = 0; Rectangle box = Pixels(first, region);
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++)
        { Color b = baseline.GetPixel(x, y); if (first.GetPixel(x, y) != b && second.GetPixel(x, y) != b) count++; }
        return count;
    }
    private static Rectangle InkBounds(Bitmap image, RectangleF region, Color background)
    {
        int left = image.Width, right = -1, top = image.Height, bottom = -1; Rectangle box = Pixels(image, region);
        for (int y = box.Top; y < box.Bottom; y++) for (int x = box.Left; x < box.Right; x++)
            if (image.GetPixel(x, y).ToArgb() != background.ToArgb())
            { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }
    private static bool EqualPixels(Bitmap first, Bitmap second)
    { return ChangedPixels(first, second, new RectangleF(0, 0, first.Width, first.Height)) == 0; }
    private static object Field(object target, string name)
    { return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(target); }
    private static object Call(object target, string name, params object[] args)
    { return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args); }
    private static float Value(object target, string name)
    {
        float value = RawValue(target, name);
        ScoreCanvas canvas = target as ScoreCanvas;
        if (canvas == null) return value;
        if (name == "StageTop" || name == "JudgmentY") return canvas.ToClient(new RectangleF(0, value, 0, 0)).Y;
        if (name == "StageLeft" || name == "StageRight") return canvas.ToClient(new RectangleF(value, 0, 0, 0)).X;
        return value * canvas.LayoutScale;
    }
    private static float RawValue(object target, string name)
    { return (float)target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target, null); }
    private static RectangleF Bounds(object target, string name)
    { return ((ScoreCanvas)target).ToClient(RawBounds(target, name)); }
    private static RectangleF RawBounds(object target, string name)
    { return (RectangleF)target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target, null); }
    private static bool IsText(Color color)
    { return color.R >= 230 && color.G >= 220 && color.B >= 180; }
    private static bool IsNoteInk(Color color)
    { return color.R >= 60 && color.R <= 100 && color.G >= 50 && color.G <= 90 && color.B >= 20 && color.B <= 60; }
    private static void Near(double actual, double expected, string message)
    { Assert(Math.Abs(actual - expected) < .001, message); }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new Exception(message); checks++; }
}
