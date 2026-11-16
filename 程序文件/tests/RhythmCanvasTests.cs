using System;
using System.Collections.Generic;
using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using HarmonicaStudio;

public static class RhythmCanvasTests
{
    private static int checks;
    public static int Run()
    {
        checks = 0;
        TestMusicTimeGeometry();
        TestLargeViewportGeometry();
        TestBackgroundTracks();
        TestPausedReferenceResize();
        TestOverlappingBackgroundIndex();
        TestHeldNoteAndCache();
        TestLargeScoreRange();
        TestPausedRendering();
        TestSeek();
        TestSpokenNotationAndCountdown();
        TestUnifiedDisplayDegrees();
        TestUniformFitResize();
        Console.WriteLine("瀑布谱面测试通过，共 " + checks + " 项断言（未发送键鼠输入）。");
        return checks;
    }

    private static void TestMusicTimeGeometry()
    {
        using (var canvas = new ScoreCanvas { Size = new Size(604, 213), Position = 2 })
        {
            Near(canvas.TimeToY(2), Value(canvas, "JudgmentY"), "音符起点到达播放位置时，必须恰好在底部判定线。");
            Near(canvas.TimeToY(6), Value(canvas, "StageTop"), "完整瀑布模式顶边应对应未来4秒。");
            float before = canvas.TimeToY(3);
            float delta = (Value(canvas, "JudgmentY") - Value(canvas, "StageTop")) * .05f / 4;
            canvas.Position = 2.05;
            Near(canvas.TimeToY(3), before + delta, "音符必须按连续音乐时间向下流动，不能逐音符跳格。");
            float frozen = canvas.TimeToY(3);
            Near(canvas.TimeToY(3), frozen, "播放位置不变时，几何位置必须保持不变。");
            canvas.Size = new Size(504, 110); canvas.Compact = true; canvas.Position = 2;
            Near(canvas.TimeToY(2), Value(canvas, "JudgmentY"), "迷你瀑布模式必须保持底部固定判定线。");
            Near(canvas.TimeToY(4), Value(canvas, "StageTop"), "迷你瀑布模式应显示未来2秒，保证小窗口可读。");
            canvas.Position = Double.NaN;
            Near(canvas.TimeToY(0), Value(canvas, "JudgmentY"), "非有限播放位置应回到安全的零位置。");
        }
    }

    private static void TestLargeViewportGeometry()
    {
        using (var canvas = new ScoreCanvas { Size = new Size(1248, 372), Position = 2 })
        {
            Near(canvas.TimeToY(2), Value(canvas, "JudgmentY"), "放大后的完整瀑布仍必须与底部真实演奏线一致。");
            Near(canvas.TimeToY(6), Value(canvas, "StageTop"), "放大的完整瀑布仍应保持4秒时间窗口。");
            canvas.Compact = true; canvas.Size = new Size(868, 256);
            Near(canvas.TimeToY(2), Value(canvas, "JudgmentY"), "放大的长条迷你瀑布应保留底部演奏线。");
            Near(canvas.TimeToY(4), Value(canvas, "StageTop"), "放大的迷你瀑布仍应保留2秒时间窗口。");
        }
    }

    private static void TestBackgroundTracks()
    {
        using (var canvas = new ScoreCanvas { Size = new Size(1248, 372), Position = .35, SelectedTrackIndex = 2 })
        {
            var selected = new ScoreTrack { Index = 2, Name = "主旋律" };
            selected.Notes.Add(new NoteEvent { Pitch = 60, Start = 0, Duration = 2 });
            var bridge = new ScoreTrack { Index = 5, Name = "桥段" };
            var held = new NoteEvent { Pitch = 64, Start = -.5, Duration = 1.7 };
            bridge.Notes.Add(held);
            bridge.Notes.Add(new NoteEvent { Pitch = 67, Start = 1.6, Duration = .4 });
            bridge.Notes.Add(new NoteEvent { Pitch = 127, Start = .1, Duration = 1 });
            var piano = new ScoreTrack { Index = 9, Name = "钢琴" };
            piano.Notes.Add(new NoteEvent { Pitch = 65, Start = .1, Duration = .7 });
            canvas.Notes = ScoreIO.Melody(selected, 0);
            canvas.BackgroundTracks = new List<ScoreTrack> { selected, bridge, piano };
            using (var first = Render(canvas))
            using (var paused = Render(canvas))
            {
                Assert(SamePixels(first, paused), "暂停时背景声轨也必须按同一个Position冻结。");
                canvas.Automatic = true;
                using (var automatic = Render(canvas))
                    Assert(SameRegion(first, automatic, (int)Math.Ceiling(Value(canvas, "StageTop"))), "自动和手动必须共用同一动态瀑布，不能改变背景声轨位置。");
            }
            IList caches = (IList)Field(canvas, "backgroundCaches");
            Assert(caches.Count == 2, "选中轨应保持亮色旋律，其他全部声轨应独立保留，不重复绘制选中轨。");
            Assert((Color)caches[0].GetType().GetField("Color").GetValue(caches[0]) !=
                (Color)caches[1].GetType().GetField("Color").GetValue(caches[1]), "不同来源背景声轨应使用独立配色。");
            Assert((string)Field(canvas, "selectedTrackName") == "主旋律" &&
                (string)caches[0].GetType().GetField("Name").GetValue(caches[0]) == "桥段",
                "图例应引用实际源轨名，不编造轨名。");
            Assert((int)Field(canvas, "backgroundHiddenNotes") == 1, "不可映射的背景音符必须计数并披露隐藏。");
            var visible = (List<NoteEvent>)caches[0].GetType().GetField("Visible").GetValue(caches[0]);
            Assert(visible.Contains(held), "起点在零时间之前但仍持续的背景长音必须保持与所选轨同步。");
            Near(canvas.TimeToY(canvas.Position), Value(canvas, "JudgmentY"), "增加背景声轨图例后，选中轨和背景仍应共享底部判定线。");
            canvas.BackgroundTracks = null;
            using (var cleared = Render(canvas))
                Assert(((IList)Field(canvas, "backgroundCaches")).Count == 0,
                    "换回单声轨时应清理上一曲的背景声轨缓存。");
        }
    }

    private static void TestOverlappingBackgroundIndex()
    {
        using (var canvas = new ScoreCanvas { Size = new Size(1248, 372), Position = 3500.04, SelectedTrackIndex = 2 })
        {
            var background = new ScoreTrack { Index = 7, Name = "重叠压力声轨" };
            var held = new NoteEvent { Pitch = 60, Start = 0, Duration = 7001 };
            background.Notes.Add(held);
            for (int i = 1; i < 100000; i++)
                background.Notes.Add(new NoteEvent { Pitch = 64, Start = i * .07, Duration = .05 });
            canvas.Notes = new List<NoteEvent> { new NoteEvent { Pitch = 67, Start = 0, Duration = 7001 } };
            canvas.BackgroundTracks = new List<ScoreTrack> { background };
            using (var image = Render(canvas))
            {
                IList caches = (IList)Field(canvas, "backgroundCaches");
                var visible = (List<NoteEvent>)caches[0].GetType().GetField("Visible").GetValue(caches[0]);
                Assert(visible.Count < 100 && visible.Contains(held),
                    "极长重叠音不能迫使画面逐帧扫描十万旧音符，区间查询应保留长音及近处音符。");
                Assert(visible.Exists(delegate(NoteEvent note) { return note.Start >= 3500 && note.Start < 3501; }),
                    "重叠长音不能遮断实际当前时间附近的背景音符。");
            }
        }
    }

    private static void TestPausedReferenceResize()
    {
        using (var canvas = new ScoreCanvas { Size = new Size(868, 256), Compact = true, Position = 0, SelectedTrackIndex = 0 })
        {
            var background = new ScoreTrack { Index = 1, Name = "暂停切换参考窗" };
            var later = new NoteEvent { Pitch = 64, Start = 3.5, Duration = .2 };
            background.Notes.Add(later);
            canvas.BackgroundTracks = new List<ScoreTrack> { background };
            using (var small = Render(canvas))
            {
                IList caches = (IList)Field(canvas, "backgroundCaches");
                var visible = (List<NoteEvent>)caches[0].GetType().GetField("Visible").GetValue(caches[0]);
                Assert(!visible.Contains(later), "小迷你窗口的两秒预览不应提前显示3.5秒背景音。");
                object original = caches[0];
                canvas.Size = new Size(1248, 486);
                using (var reference = Render(canvas))
                {
                    caches = (IList)Field(canvas, "backgroundCaches");
                    visible = (List<NoteEvent>)caches[0].GetType().GetField("Visible").GetValue(caches[0]);
                    Assert(!visible.Contains(later), "迷你窗口缩放后仍须保持两秒音乐预览，不能按高度突然改变音符流速。");
                    Assert(Object.ReferenceEquals(original, caches[0]), "暂停缩放应复用声轨区间索引，不重扫整曲。");
                    Near(canvas.TimeToY(2), Value(canvas, "StageTop"), "放大后的迷你参考窗仍应明确显示未来两秒。");
                    canvas.Compact = false;
                    using (var full = Render(canvas))
                    {
                        visible = (List<NoteEvent>)caches[0].GetType().GetField("Visible").GetValue(caches[0]);
                        Assert(visible.Contains(later), "真正切到完整模式时应刷新为四秒背景窗口。");
                        Near(canvas.TimeToY(4), Value(canvas, "StageTop"), "完整模式明确显示未来四秒。");
                    }
                }
            }
        }
    }

    private static void TestSpokenNotationAndCountdown()
    {
        int[] pitches = { 63, 72, 48, 84, 51, 75 };
        string[] expected = { "2升半音", "高八度1", "低八度1", "高2个八度1", "低八度2升半音", "高八度2升半音" };
        for (int i = 0; i < pitches.Length; i++)
        {
            Fingering finger = HarmonicaMap.Map(pitches[i], 60);
            string notation = finger.Number;
            Assert((string)StaticCall("SpokenPitch", finger) == expected[i],
                "当前音的解释须同时准确包含升半音与简谱所写八度，不能将逗号键的高音1误说成普通1。");
            Assert(finger.Number == notation, "人话解释不得更改实际指法或音符块的#和八度记号。");
        }
        Assert((string)StaticCall("CountdownCaption", "3秒后自动开始，切回三角洲") == "3秒 · 切回游戏",
            "普通倒计时应保留秒数并在顶部以短句完整显示。");
        const string paused = "倒计时已暂停 · F11 继续";
        const string resumed = "倒计时继续 · F12 可暂停";
        Assert((string)StaticCall("CountdownCaption", paused) == paused,
            "倒计时暂停必须显示真实暂停状态与自定义继续键，不得继续催用户切回游戏。");
        Assert((string)StaticCall("CountdownCaption", resumed) == resumed,
            "倒计时恢复必须保留真实继续状态与自定义暂停键，不得硬写F5。");
    }

    private static void TestUnifiedDisplayDegrees()
    {
        int[] pitches = { 63, 51, 75, 52, 79, 72, 84 };
        string[] expected = { "2", "2", "2", "3", "5", "1′", "1′" };
        int[] modifiers = { 4, 5, 6, 1, 2, 0, 2 };
        using (var canvas = new ScoreCanvas { Size = new Size(1050, 370), Position = 0 })
        {
            for (int i = 0; i < pitches.Length; i++)
                canvas.Notes.Add(new NoteEvent { Pitch = pitches[i], Start = .2 + i * .4, Duration = .1 });
            Call(canvas, "EnsureCache");
            Array visuals = (Array)Field(canvas, "visuals");
            for (int i = 0; i < pitches.Length; i++)
            {
                object item = visuals.GetValue(i);
                int key = (int)item.GetType().GetField("KeyIndex").GetValue(item);
                Fingering finger = (Fingering)item.GetType().GetField("Finger").GetValue(item);
                string degree = (string)StaticCall("DegreeForKey", key);
                Assert(degree == expected[i], "实际音符块的音级应只使用当前真实键列基础音级，鼠标图标承担升半音与八度说明。");
                Assert(!degree.Contains("#") && !degree.Contains(",") && !degree.Contains("''"),
                    "演奏音符块不得同时混入#、低音逗号或重复高音记号，避免与六种鼠标图例重复。");
                Assert((int)item.GetType().GetField("MouseMask").GetValue(item) == modifiers[i],
                    "移除数字修饰时必须保留圆点或左右箭头真实修饰，不能把半音或八度丢掉。");
                Assert(finger.Number == HarmonicaMap.Map(pitches[i], 60).Number && canvas.Notes[i].Pitch == pitches[i],
                    "统一显示只能改变文字，绝不能更改真实MIDI音高或输入映射。");
                Assert(!((string)StaticCall("SpokenPitch", finger)).Contains("#"), "当前音的辅助文字应使用升半音等人话，不再显示第二套#符号。");
            }
        }
    }

    private static void TestHeldNoteAndCache()
    {
        using (var canvas = new ScoreCanvas { Size = new Size(604, 213), Position = 2 })
        {
            canvas.Notes = new List<NoteEvent>
            {
                new NoteEvent { Pitch = 60, Start = 0, Duration = 3 },
                new NoteEvent { Pitch = 64, Start = 3.4, Duration = .4 },
                new NoteEvent { Pitch = 72, Start = 4.8, Duration = .5 }
            };
            Call(canvas, "EnsureCache");
            Assert((int)Call(canvas, "FirstEndingAtOrAfter", 1.5) == 0,
                "已开始但仍在持续的长音不能被起点查询丢掉。");
            Assert((int)Call(canvas, "FirstEndingAtOrAfter", 3.01) == 1,
                "已经结束的长音应正确离开可见范围。");
            Assert((int)Call(canvas, "FirstStartingAfter", 4.8) == 3,
                "位于查询右边界上的音符起点应包含在预览中。");
            Array firstCache = (Array)Field(canvas, "visuals");
            canvas.Position = 2.1;
            Call(canvas, "EnsureCache");
            Assert(Object.ReferenceEquals(firstCache, Field(canvas, "visuals")),
                "仅移动播放位置时不能重建整曲指法缓存。");
            canvas.BasePitch = 72; Call(canvas, "EnsureCache");
            Array changedCache = (Array)Field(canvas, "visuals");
            object first = changedCache.GetValue(0);
            string hint = (string)first.GetType().GetField("MouseHint").GetValue(first);
            Assert(hint == "左", "改变基准音高后，缓存必须更新真实鼠标修饰提示。");
            canvas.Notes = new List<NoteEvent>(); Call(canvas, "EnsureCache");
            Assert((double)Field(canvas, "duration") == 0 && ((Array)Field(canvas, "visuals")).Length == 0,
                "切换到空曲目时不能保留上一曲的时长或音符。");
        }
    }

    private static void TestLargeScoreRange()
    {
        using (var canvas = new ScoreCanvas { Size = new Size(604, 213), Position = 7000.04 })
        {
            var score = new List<NoteEvent>(200000);
            for (int i = 0; i < 200000; i++)
                score.Add(new NoteEvent { Pitch = 60 + i % 8, Start = i * .07, Duration = .06 });
            canvas.Notes = score; Call(canvas, "EnsureCache");
            int first = (int)Call(canvas, "FirstEndingAtOrAfter", 6999.4);
            int last = (int)Call(canvas, "FirstStartingAfter", 7004.04);
            Assert(first >= 99990 && last - first < 75,
                "20万音符的中段预览应只取得屏幕附近的音符，而非扫描整曲。");
            Assert((int)Call(canvas, "FirstStartingAfter", 50000.0) == 200000,
                "播放位置超过整曲时，二分查找应安全返回末尾。");
            Assert((int)Call(canvas, "FirstEndingAtOrAfter", 50000.0) == 200000,
                "可见窗口超过整曲时，二分结束边界应安全返回末尾。");
        }
    }

    private static void TestPausedRendering()
    {
        using (var canvas = new ScoreCanvas { Size = new Size(604, 213), Position = .12 })
        {
            canvas.Notes = ScoreIO.Melody(ScoreIO.ParseJianpu("1:2 #2 5, 1':2", 100, 60), 0);
            using (var first = Render(canvas))
            using (var paused = Render(canvas))
            {
                Assert(SamePixels(first, paused), "暂停且播放位置不变时，脉冲、滚动和进度必须全部冻结。");
                canvas.Position = .22;
                using (var moved = Render(canvas))
                    Assert(!SamePixels(first, moved), "播放位置前进时，真实谱面渲染应随之改变。");
            }
            canvas.Compact = true; canvas.Size = new Size(504, 110);
            using (var compact = Render(canvas))
                Assert(compact.Width == 504 && compact.Height == 110, "迷你八列瀑布应能在实际窗口尺寸正常渲染。");
            canvas.Notes = new List<NoteEvent>();
            using (var empty = Render(canvas))
                Assert(empty.Width == 504, "无曲目状态也应能正常渲染。");
        }
    }

    private static void TestSeek()
    {
        using (var canvas = new ScoreCanvas { Size = new Size(604, 213) })
        {
            canvas.Notes = new List<NoteEvent> { new NoteEvent { Pitch = 60, Start = 0, Duration = 10 } };
            double sought = -1; int calls = 0;
            canvas.Seek += delegate(double position) { sought = position; calls++; };
            RectangleF progress = ClientBounds(canvas, "ProgressBounds");
            int middle = (int)Math.Round((progress.Left + progress.Right) / 2), y = (int)Math.Round(progress.Top + progress.Height / 2);
            Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, middle, y, 0));
            Assert(Math.Abs(sought - 5) < .02, "缩放后的进度中点应定位到整曲中点。");
            Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, (int)Math.Ceiling(progress.Left), y, 0));
            Assert(sought >= 0 && sought < .04, "进度左边界应定位到曲首。");
            Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, (int)Math.Floor(progress.Right), y, 0));
            Assert(sought > 9.96 && sought <= 10, "进度右边界应定位到曲尾。");
            int before = calls;
            Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 302, 70, 0));
            Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, middle, y, 0));
            Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, -20, 204, 0));
            Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 650, 204, 0));
            Assert(calls == before, "点击谱面或使用右键不应意外跳转歌曲。");
            canvas.Size = new Size(1248, 520);
            progress = ClientBounds(canvas, "ProgressBounds");
            y = (int)Math.Round(progress.Top + progress.Height / 2);
            int stageMiddle = (int)Math.Round((Value(canvas, "StageLeft") + Value(canvas, "StageRight")) / 2);
            Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, stageMiddle, y, 0));
            Assert(Math.Abs(sought - 5) < .02, "居中留白后的进度中点仍须是右侧谱面中点。");
            before = calls;
            Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 120, y, 0));
            Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, stageMiddle, 511, 0));
            Assert(calls == before, "点击左侧说明或等比留白不能跳转曲目进度。");
        }
    }

    private static void TestUniformFitResize()
    {
        using (var canvas = new ScoreCanvas { Size = new Size(868, 256), Compact = true, Position = .4 })
        {
            canvas.Notes = new List<NoteEvent> { new NoteEvent { Pitch = 64, Start = 1, Duration = .2 } };
            RectangleF originalHead;
            using (var original = Render(canvas))
            {
                originalHead = (RectangleF)Call(canvas, "HeadBounds", ((Array)Field(canvas, "visuals")).GetValue(0), false);
                Assert((bool)canvas.GetType().GetProperty("TrackLegendVisible", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(canvas, null) == false,
                    "无背景声轨的说明不能伪造多轨名称。");
            }
            object visualCache = Field(canvas, "visuals"), font = Field(canvas, "noteFont");
            foreach (Size size in new[] { new Size(868, 340), new Size(1000, 300), new Size(1550, 390), new Size(1032, 900) })
            {
                canvas.Size = size;
                using (var resized = Render(canvas))
                {
                    RectangleF head = (RectangleF)Call(canvas, "HeadBounds", ((Array)Field(canvas, "visuals")).GetValue(0), false);
                    Assert(head == originalHead && Object.ReferenceEquals(visualCache, Field(canvas, "visuals")) && Object.ReferenceEquals(font, Field(canvas, "noteFont")),
                        "暂停缩放只能统一改变参考布局的比例，不能重排音符、改变卡面字号或重建曲目缓存。");
                    RectangleF fitted = canvas.FittedBounds;
                    Assert(fitted.Left >= -.001f && fitted.Top >= -.001f && fitted.Right <= size.Width + .001f && fitted.Bottom <= size.Height + .001f,
                        "各种窗口比例下整张谱面必须完整 Fit，允许居中留白，禁止硬裁。");
                    Near(fitted.Width / fitted.Height, 1200.0 / 360, "参考布局宽高比须保持一致。");
                    Near(canvas.TimeToY(canvas.Position + 2), Value(canvas, "StageTop"), "迷你窗口放大时不能改变两秒音乐时间窗。");
                }
            }
        }
    }

    private static Bitmap Render(ScoreCanvas canvas)
    {
        var bitmap = new Bitmap(canvas.Width, canvas.Height);
        // Keep time-geometry regressions independent of the host monitor DPI.
        // Separate readability tests render the real paint path at 96/144/192 DPI.
        bitmap.SetResolution(96, 96);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(canvas.BackColor);
            Call(canvas, "OnPaint", new PaintEventArgs(graphics, new Rectangle(Point.Empty, canvas.Size)));
        }
        return bitmap;
    }
    private static bool SamePixels(Bitmap first, Bitmap second)
    {
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) return false;
        return true;
    }
    private static bool SameRegion(Bitmap first, Bitmap second, int firstRow)
    {
        for (int y = firstRow; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) return false;
        return true;
    }
    private static object Field(object target, string name)
    {
        return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }
    private static float Value(object target, string name)
    {
        float value = (float)target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target, null);
        ScoreCanvas canvas = target as ScoreCanvas;
        if (canvas == null) return value;
        if (name == "StageTop" || name == "JudgmentY") return canvas.ToClient(new RectangleF(0, value, 0, 0)).Y;
        if (name == "StageLeft" || name == "StageRight") return canvas.ToClient(new RectangleF(value, 0, 0, 0)).X;
        return value * canvas.LayoutScale;
    }
    private static RectangleF ClientBounds(ScoreCanvas canvas, string name)
    { return canvas.ToClient((RectangleF)canvas.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(canvas, null)); }
    private static object Call(object target, string name, params object[] arguments)
    {
        return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, arguments);
    }
    private static object StaticCall(string name, params object[] arguments)
    {
        return typeof(ScoreCanvas).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, arguments);
    }
    private static void Near(double actual, double expected, string message)
    {
        Assert(Math.Abs(actual - expected) < .001, message);
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }
}
