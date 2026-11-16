using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace HarmonicaStudio
{
    // The music position is the only clock: pausing playback also freezes every visual.
    internal sealed class ScoreCanvas : Control
    {
        public List<NoteEvent> Notes = new List<NoteEvent>();
        public double Position;
        public double TimelineDuration;
        public int BasePitch = 60;
        public bool Compact;
        public bool Automatic;
        public double PlaybackSpeed = 1.0;
        public string CountdownText;
        public string SongTitle;
        public string SongSubtitle;
        public List<ScoreTrack> BackgroundTracks;
        public int SelectedTrackIndex;
        public event Action<double> Seek;

        private static readonly string[] KeyNames = { "Z", "X", "C", "V", "B", "N", "M", "," };
        private static readonly string[] DegreeNames = { "1", "2", "3", "4", "5", "6", "7", "1′" };
        private const string FingeringHintText = "图标表示鼠标指法 · 字母是键盘键 · 中键 = 按下滚轮 · + = 同时按住 · 1′ = 高音1（逗号键）";
        private const string MouseRuleText = "中键 = 按下滚轮 · ＋ = 同时按住";
        private const string KeyboardRuleText = "1′ = 高音1（逗号键）· 长条需按住";
        private const string TrackHintText = "金色音符用于演奏 · 彩色细条仅显示其他音轨 · 点击底部进度定位";
        private static readonly Color StageColor = Color.FromArgb(31, 34, 35);
        private static readonly Color MutedColor = Color.FromArgb(207, 205, 186);
        private static readonly Color TextColor = Color.FromArgb(255, 248, 221);
        private static readonly Color GoldColor = Color.FromArgb(248, 208, 104);
        private static readonly Color NoteInkColor = Color.FromArgb(73, 62, 31);
        private static readonly Color PinkColor = Color.FromArgb(246, 165, 150);
        // All geometry and fonts share this reference coordinate system. The
        // complete score is fitted once, rather than hiding individual panels.
        private const float SceneWidth = 1200, SceneHeight = 360;
        private const float PlaybackStatusWidth = 220;
        private readonly Font smallFont = Theme.PixelFont(13.5f);
        private readonly Font labelFont = Theme.PixelFont(13.5f);
        private readonly Font largeHeaderFont = Theme.PixelFont(16.5f, FontStyle.Bold);
        private readonly Font largeLabelFont = Theme.PixelFont(13.5f);
        private readonly Font noteFont = Theme.PixelFont(16.5f, FontStyle.Bold);
        private readonly Font keyboardFont = Theme.PixelFont(15, FontStyle.Bold);
        private readonly Font degreeFont = Theme.PixelFont(18, FontStyle.Bold);
        private readonly Font hintFont = Theme.PixelFont(12.75f);
        private List<NoteEvent> cachedSource;
        private int cachedCount = -1, cachedBasePitch = -1;
        private NoteVisual[] visuals = new NoteVisual[0];
        private double[] prefixEnd = new double[0];
        private double duration;
        private double PlaybackDuration { get { return !Double.IsNaN(TimelineDuration) && !Double.IsInfinity(TimelineDuration) && TimelineDuration > 0 ? Math.Max(duration, TimelineDuration) : duration; } }
        private float layoutDpi = 96;
        private bool layoutReady;
        private int layoutWidth, layoutHeight;
        private float headerHeight, legendHeight;
        private Font[] headFonts, numberFonts;
        private float[] headTextHeights;
        private float preferredHeadHeight;
        private List<ScoreTrack> cachedBackgroundSource;
        private string cachedBackgroundSubtitle;
        private int cachedBackgroundBase = -1, cachedSelectedTrack = Int32.MinValue;
        private readonly List<BackgroundTrackCache> backgroundCaches = new List<BackgroundTrackCache>();
        private readonly ToolTip backgroundTip = new ToolTip();
        private double backgroundPosition = Double.NaN;
        private double backgroundFutureSeconds = Double.NaN;
        private bool backgroundCompact;
        private int backgroundHiddenNotes;
        private string selectedTrackName = "当前旋律";
        private static readonly Fingering[] LegendFingers = {
            HarmonicaMap.Map(60, 60), HarmonicaMap.Map(61, 60),
            HarmonicaMap.Map(48, 60), HarmonicaMap.Map(49, 60),
            HarmonicaMap.Map(74, 60), HarmonicaMap.Map(75, 60)
        };
        private static readonly Color[] BackgroundColors = {
            Color.FromArgb(96, 182, 214), Color.FromArgb(181, 189, 197),
            Color.FromArgb(138, 163, 236), Color.FromArgb(208, 140, 177),
            Color.FromArgb(110, 190, 158), Color.FromArgb(187, 151, 221)
        };

        private sealed class BackgroundTrackCache
        {
            public int Index, LeafCount;
            public string Name;
            public Color Color;
            public NoteEvent[] Notes;
            public double[] MaximumEnd;
            public readonly List<NoteEvent> Visible = new List<NoteEvent>();
        }

        private sealed class NoteVisual
        {
            public NoteEvent Note;
            public Fingering Finger;
            public int Lane, KeyIndex;
            public int MouseMask;
            public int NextInColumn = -1;
            public int NextWarning = -1;
            public string MouseHint;
        }

        public ScoreCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = StageColor;
            Cursor = Cursors.Hand;
            AccessibleName = "瀑布口琴谱面";
            AccessibleDescription = "金色音符是演奏旋律，彩色细条仅显示其他音轨。音符从上向下落入八列真实键位，到达底部判定线时按显示的键盘和鼠标修饰；底部进度可点击定位。" + FingeringHintText;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                smallFont.Dispose(); labelFont.Dispose(); largeHeaderFont.Dispose(); largeLabelFont.Dispose();
                noteFont.Dispose(); keyboardFont.Dispose(); degreeFont.Dispose(); hintFont.Dispose();
                backgroundTip.Dispose();
            }
            base.Dispose(disposing);
        }

        private void EnsureCache()
        {
            int count = Notes == null ? 0 : Notes.Count;
            if (Object.ReferenceEquals(cachedSource, Notes) && cachedCount == count && cachedBasePitch == BasePitch) return;
            cachedSource = Notes; cachedCount = count; cachedBasePitch = BasePitch;
            visuals = new NoteVisual[count]; prefixEnd = new double[count]; duration = 0;
            for (int i = 0; i < count; i++)
            {
                NoteEvent note = Notes[i];
                Fingering finger = HarmonicaMap.Map(note.Pitch, BasePitch);
                string modifier = finger.Modifier ?? "";
                int lane = modifier.Contains("低") ? 2 : modifier.Contains("高") ? 0 : 1;
                string mouse = modifier.Contains("左") ? "左" : modifier.Contains("右") ? "右" : "";
                if (modifier.Contains("中键")) mouse += mouse.Length == 0 ? "中" : "+中";
                visuals[i] = new NoteVisual { Note = note, Finger = finger, Lane = lane,
                    KeyIndex = Array.IndexOf(KeyNames, finger.Key), MouseHint = mouse,
                    MouseMask = ModifierMask(finger) };
                duration = Math.Max(duration, note.Start + note.Duration);
                prefixEnd[i] = duration;
            }
            // Index the next note in each real key column once. Dense passages
            // must not trigger a score scan on every animation frame.
            int[] nextInColumn = { -1, -1, -1, -1, -1, -1, -1, -1 };
            int nextWarning = -1;
            for (int i = count - 1; i >= 0; i--)
            {
                int key = visuals[i].KeyIndex;
                if (!visuals[i].Finger.Playable || key < 0) { nextWarning = i; continue; }
                visuals[i].NextInColumn = nextInColumn[key];
                visuals[i].NextWarning = nextWarning;
                nextInColumn[key] = i;
            }
        }

        private void EnsureBackgroundCache()
        {
            if (Object.ReferenceEquals(cachedBackgroundSource, BackgroundTracks) &&
                cachedBackgroundBase == BasePitch && cachedSelectedTrack == SelectedTrackIndex &&
                cachedBackgroundSubtitle == SongSubtitle) return;
            cachedBackgroundSource = BackgroundTracks; cachedBackgroundBase = BasePitch;
            cachedBackgroundSubtitle = SongSubtitle;
            cachedSelectedTrack = SelectedTrackIndex;
            backgroundCaches.Clear(); backgroundHiddenNotes = 0;
            selectedTrackName = "当前旋律"; backgroundPosition = Double.NaN;
            if (BackgroundTracks == null) { backgroundTip.SetToolTip(this, SongSubtitle ?? ""); return; }
            var description = new System.Text.StringBuilder("高亮音符为所选吹奏旋律；其他配色细条仅同步显示声轨，不参与自动输入。");
            foreach (ScoreTrack track in BackgroundTracks)
            {
                if (track == null) continue;
                string name = String.IsNullOrWhiteSpace(track.Name) ? "声轨 " + (track.Index + 1) : track.Name;
                if (track.Index == SelectedTrackIndex) { selectedTrackName = name; continue; }
                var playable = new List<NoteEvent>();
                foreach (NoteEvent note in track.Notes)
                {
                    if (note == null || Double.IsNaN(note.Start) || Double.IsInfinity(note.Start) ||
                        Double.IsNaN(note.Duration) || Double.IsInfinity(note.Duration) || note.Duration <= 0)
                    { backgroundHiddenNotes++; continue; }
                    if (!HarmonicaMap.Map(note.Pitch, BasePitch).Playable) { backgroundHiddenNotes++; continue; }
                    playable.Add(note);
                }
                playable.Sort(delegate(NoteEvent left, NoteEvent right) { return left.Start.CompareTo(right.Start); });
                int leafCount = 1;
                while (leafCount < playable.Count) leafCount <<= 1;
                var cache = new BackgroundTrackCache { Index = track.Index, Name = name,
                    Color = BackgroundColors[Math.Abs(track.Index % BackgroundColors.Length)],
                    Notes = playable.ToArray(), LeafCount = leafCount, MaximumEnd = new double[leafCount * 2] };
                for (int i = 0; i < cache.MaximumEnd.Length; i++) cache.MaximumEnd[i] = Double.NegativeInfinity;
                for (int i = 0; i < cache.Notes.Length; i++)
                    cache.MaximumEnd[leafCount + i] = cache.Notes[i].Start + cache.Notes[i].Duration;
                for (int i = leafCount - 1; i > 0; i--)
                    cache.MaximumEnd[i] = Math.Max(cache.MaximumEnd[i * 2], cache.MaximumEnd[i * 2 + 1]);
                backgroundCaches.Add(cache);
                description.Append("\n背景：").Append(name).Append(" · ").Append(track.Notes.Count).Append(" 个源音符");
            }
            description.Insert(0, "吹奏：" + selectedTrackName + "\n");
            if (!String.IsNullOrWhiteSpace(SongSubtitle)) description.Insert(0, SongSubtitle + "\n");
            if (backgroundHiddenNotes > 0) description.Append("\n有 ").Append(backgroundHiddenNotes).Append(" 个背景音符超域或无效，未绘制。");
            backgroundTip.SetToolTip(this, description.ToString());
        }

        // A maximum-end interval tree prunes old completed notes even when one
        // very long note overlaps thousands of later short notes.
        private static void CollectBackground(BackgroundTrackCache cache, int node, int left, int right,
            double begin, double end, int limit)
        {
            if (cache.Visible.Count >= limit || left >= cache.Notes.Length ||
                cache.MaximumEnd[node] < begin || cache.Notes[left].Start > end) return;
            if (right - left == 1) { cache.Visible.Add(cache.Notes[left]); return; }
            int middle = left + (right - left) / 2;
            CollectBackground(cache, node * 2, left, middle, begin, end, limit);
            CollectBackground(cache, node * 2 + 1, middle, right, begin, end, limit);
        }

        private void UpdateBackgroundWindow()
        {
            if (backgroundPosition == MusicPosition && backgroundCompact == Compact && backgroundFutureSeconds == FutureSeconds) return;
            backgroundPosition = MusicPosition; backgroundCompact = Compact; backgroundFutureSeconds = FutureSeconds;
            int limit = Math.Max(1, Math.Min(256, 2500 / Math.Max(1, backgroundCaches.Count)));
            foreach (BackgroundTrackCache cache in backgroundCaches)
            {
                cache.Visible.Clear();
                CollectBackground(cache, 1, 0, cache.LeafCount, MusicPosition - .35,
                    MusicPosition + FutureSeconds + .3, limit);
            }
        }

        // ScoreIO.Melody provides ordered, non-overlapping notes. These searches keep
        // rendering proportional to the visible window, including a held long note.
        private int FirstEndingAtOrAfter(double time)
        {
            int low = 0, high = prefixEnd.Length;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (prefixEnd[mid] < time) low = mid + 1; else high = mid;
            }
            return low;
        }

        private int FirstStartingAfter(double time)
        {
            int low = 0, high = visuals.Length;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (visuals[mid].Note.Start <= time) low = mid + 1; else high = mid;
            }
            return low;
        }

        private double MusicPosition
        {
            get { return Double.IsNaN(Position) || Double.IsInfinity(Position) ? 0 : Math.Max(0, Position); }
        }
        private double FutureSeconds { get { return Compact ? 2 : 4; } }
        private bool LargeLayout { get { return true; } }
        private bool TrackLegendVisible { get { return backgroundCaches.Count > 0; } }
        internal float LayoutScale { get { return Math.Max(.01f, Math.Min(Width / SceneWidth, Height / SceneHeight)); } }
        private float OffsetX { get { return (Width - SceneWidth * LayoutScale) / 2; } }
        private float OffsetY { get { return (Height - SceneHeight * LayoutScale) / 2; } }
        internal RectangleF FittedBounds { get { return ToClient(new RectangleF(0, 0, SceneWidth, SceneHeight)); } }
        internal RectangleF ToClient(RectangleF value)
        { return new RectangleF(OffsetX + value.X * LayoutScale, OffsetY + value.Y * LayoutScale,
            value.Width * LayoutScale, value.Height * LayoutScale); }
        internal void ApplyFit(Graphics g)
        { g.TranslateTransform(OffsetX, OffsetY); g.ScaleTransform(LayoutScale, LayoutScale); }
        private void EnsureLayout(float dpi)
        {
            if (layoutReady && layoutDpi == dpi &&
                layoutWidth == Width && layoutHeight == Height) return;
            layoutReady = true; layoutDpi = dpi;
            layoutWidth = Width; layoutHeight = Height;
            headerHeight = 58;
            legendHeight = 24;
            // A note is always the same size. When timing leaves too little
            // room, retain a short bar instead of introducing tiny letters.
            headFonts = new[] { noteFont };
            numberFonts = new[] { noteFont };
            headTextHeights = new float[headFonts.Length];
            for (int i = 0; i < headFonts.Length; i++)
                headTextHeights[i] = (float)Math.Ceiling(Math.Max(headFonts[i].GetHeight(96), numberFonts[i].GetHeight(96))) + 4;
            preferredHeadHeight = Math.Max(34, headTextHeights[0] + 2);
        }
        private float StageLeft { get { return 388; } }
        private float StageWidth { get { return 796; } }
        private float StageRight { get { return StageLeft + StageWidth; } }
        private float StageTop { get { EnsureLayout(layoutDpi); return 86; } }
        private float JudgmentY { get { return 305; } }
        private float PixelsPerSecond { get { return Math.Max(1, JudgmentY - StageTop) / (float)FutureSeconds; } }
        private float ColumnWidth { get { return StageWidth / 8f; } }
        private RectangleF WaterfallBounds { get { return new RectangleF(StageLeft, StageTop, StageWidth, Math.Max(1, JudgmentY - StageTop)); } }
        private RectangleF DegreeBounds { get { return new RectangleF(StageLeft, JudgmentY + 3, StageWidth, 30); } }
        private RectangleF KeyboardBounds { get { return new RectangleF(20, 274, 336, 60); } }
        private RectangleF HintBounds { get { return new RectangleF(20, 224, 336, 28); } }
        private RectangleF FingeringHintBounds { get { return HintBounds; } }
        private RectangleF MouseLegendBounds { get { return new RectangleF(8, 8, 360, 344); } }
        private RectangleF ProgressBounds { get { return new RectangleF(StageLeft, 340, StageWidth, 18); } }
        private RectangleF CurrentCueBounds { get { return new RectangleF(StageLeft + 10, 34, StageWidth / 2 - 20, 24); } }
        private RectangleF NextCueBounds { get { return new RectangleF(StageLeft + StageWidth / 2 + 10, 34, StageWidth / 2 - 20, 24); } }
        private RectangleF LegendRowBounds(int index)
        {
            RectangleF panel = MouseLegendBounds;
            if (panel.IsEmpty || index < 0 || index >= LegendFingers.Length) return RectangleF.Empty;
            return new RectangleF(panel.X + 12, 54 + index * 28, panel.Width - 24, 28);
        }
        private RectangleF KeyLegendCell(int index)
        { return new RectangleF(KeyboardBounds.X + index % 4 * 84, KeyboardBounds.Y + index / 4 * 30, 80, 28); }
        private float SceneTimeToY(double time) { return JudgmentY - (float)(time - MusicPosition) * PixelsPerSecond; }
        internal float TimeToY(double time) { return OffsetY + SceneTimeToY(time) * LayoutScale; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            EnsureCache();
            RectangleF progress = ToClient(ProgressBounds);
            if (PlaybackDuration <= 0 || e.Button != MouseButtons.Left || Seek == null || !progress.Contains(e.Location)) return;
            double part = (e.X - progress.Left) / Math.Max(1, progress.Width);
            Seek(Math.Max(0, Math.Min(1, part)) * PlaybackDuration);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 150 || Height < 75) return;
            EnsureCache();
            EnsureBackgroundCache(); UpdateBackgroundWindow();
            Graphics g = e.Graphics;
            EnsureLayout(g.DpiY);
            GraphicsState fitted = g.Save();
            ApplyFit(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            double position = MusicPosition;
            int currentIndex = FirstStartingAfter(position) - 1;
            NoteVisual current = currentIndex >= 0 && position < visuals[currentIndex].Note.Start + visuals[currentIndex].Note.Duration
                ? visuals[currentIndex] : null;
            float stageTop = StageTop;
            float hitY = JudgmentY;

            DrawStage(g, stageTop, hitY);
            DrawHeader(g, current);
            DrawTrackLegend(g);
            GraphicsState backgroundClip = g.Save();
            g.SetClip(WaterfallBounds, CombineMode.Intersect);
            DrawBackgroundTracks(g);
            g.Restore(backgroundClip);
            if (visuals.Length == 0)
            {
                string empty = backgroundCaches.Count > 0 ? "该吹奏声轨没有可用旋律 · 背景声轨仍同步显示" : Compact ? "选择歌曲，开始看谱" : "选择一首歌 · 音符自上而下落入对应键位";
                DrawText(g, empty, LargeLayout ? largeLabelFont : labelFont, MutedColor, new RectangleF(StageLeft + 14, stageTop + 12, StageWidth - 28, 25), true);
            }
            else
            {
                GraphicsState clip = g.Save();
                g.SetClip(WaterfallBounds, CombineMode.Intersect);
                // Past notes no longer need card drawing. Keep the held current
                // note separately so a dense past passage cannot consume the
                // future drawing budget and hide the actual current note.
                if (current != null) DrawNote(g, current, currentIndex, current);
                int first = FirstStartingAfter(position);
                int last = FirstStartingAfter(position + FutureSeconds + 15 / PixelsPerSecond);
                // A pathological dense MIDI can contain thousands of events within
                // the preview window. Cap drawing, preserving the actual score.
                int drawLimit = Math.Min(last, first + 1600);
                for (int i = first; i < drawLimit; i++)
                    DrawNote(g, visuals[i], i, current);
                g.Restore(clip);
            }
            DrawJudgment(g, hitY, current);
            DrawDegreeStrip(g, current);
            DrawMouseLegend(g);
            DrawKeyStrip(g, current);
            DrawProgress(g, position);
            g.Restore(fitted);
        }

        private void DrawStage(Graphics g, float top, float bottom)
        {
            using (var fill = new SolidBrush(Color.FromArgb(42, 46, 45)))
            using (var divider = new Pen(Color.FromArgb(69, 73, 65)))
            {
                FillRounded(g, fill, new RectangleF(StageLeft, top, StageWidth, bottom - top), 6);
                for (int i = 0; i < 8; i++)
                {
                    float x = StageLeft + i * ColumnWidth;
                    if (i % 2 == 1)
                        using (var alternate = new SolidBrush(Color.FromArgb(45, 49, 47)))
                            g.FillRectangle(alternate, x, top, ColumnWidth, bottom - top);
                    if (i != 0) g.DrawLine(divider, x, top, x, bottom);
                }
                double now = MusicPosition;
                int firstSecond = (int)Math.Floor(now);
                int lastSecond = (int)Math.Ceiling(now + FutureSeconds);
                using (var grid = new Pen(Color.FromArgb(58, 63, 56)))
                {
                    for (int second = Math.Max(0, firstSecond); second <= lastSecond; second++)
                    {
                        float y = SceneTimeToY(second);
                        if (y < top || y > bottom) continue;
                        g.DrawLine(grid, StageLeft, y, StageRight, y);
                    }
                }
            }
        }

        private void DrawBackgroundTracks(Graphics g)
        {
            float spacing = Math.Min(8, ColumnWidth / Math.Max(8, backgroundCaches.Count * 4));
            for (int rank = 0; rank < backgroundCaches.Count; rank++)
            {
                BackgroundTrackCache cache = backgroundCaches[rank];
                float offsetX = (rank - (backgroundCaches.Count - 1) / 2f) * spacing;
                using (var trail = new SolidBrush(Color.FromArgb(73, cache.Color)))
                using (var cap = new SolidBrush(Color.FromArgb(148, cache.Color)))
                {
                    foreach (NoteEvent note in cache.Visible)
                    {
                        int key = Array.IndexOf(KeyNames, HarmonicaMap.Map(note.Pitch, BasePitch).Key);
                        if (key < 0) continue;
                        float x = StageLeft + (key + .5f) * ColumnWidth + offsetX;
                        float startY = SceneTimeToY(note.Start), endY = SceneTimeToY(note.Start + note.Duration);
                        float top = Math.Max(StageTop - 3, endY);
                        float bottom = Math.Min(JudgmentY + 4, startY);
                        float width = LargeLayout ? 6 : 4;
                        if (bottom > top) FillRounded(g, trail, new RectangleF(x - width / 2, top, width, bottom - top), 3);
                        if (startY >= StageTop - 4 && startY <= JudgmentY + 4)
                            FillRounded(g, cap, new RectangleF(x - (LargeLayout ? 6 : 4), startY - 1.5f,
                                LargeLayout ? 12 : 8, 3), 1.5f);
                    }
                }
            }
        }

        private void DrawTrackLegend(Graphics g)
        {
            Font font = LargeLayout ? largeLabelFont : smallFont;
            float y = StageTop - legendHeight + 1;
            float textHeight = legendHeight - 2;
            if (!TrackLegendVisible)
            {
                DrawText(g, "金色音符用于演奏 · 到达底线时按键 · 点击进度定位", font, MutedColor,
                    new RectangleF(StageLeft + 4, y, StageWidth - 8, textHeight), false);
                return;
            }
            string hidden = backgroundHiddenNotes > 0 ? "隐藏 " + backgroundHiddenNotes + " 背景音（超域/无效）" : "";
            float reserve = hidden.Length == 0 ? 0 : Math.Min(StageWidth * .35f, g.MeasureString(hidden, font).Width + 10);
            float right = StageRight - 4 - reserve;
            float x = StageLeft + 4;
            string selected = "吹奏 " + ShortTrackName(selectedTrackName) + "  ·  背景显示";
            float selectedWidth = Math.Min(Math.Max(135, g.MeasureString(selected, font).Width + 12), Math.Max(135, right - x - 70));
            DrawText(g, selected, font, GoldColor, new RectangleF(x, y, selectedWidth, textHeight), false);
            x += selectedWidth;
            int shown = 0;
            foreach (BackgroundTrackCache cache in backgroundCaches)
            {
                string name = ShortTrackName(cache.Name);
                float width = g.MeasureString(name, font).Width + 27;
                if (x + width > right - 50) break;
                using (var brush = new SolidBrush(cache.Color)) g.FillRectangle(brush, x, y + 8, 10, 5);
                DrawText(g, name, font, MutedColor, new RectangleF(x + 16, y, width - 20, textHeight), false);
                x += width; shown++;
            }
            if (shown < backgroundCaches.Count)
                DrawText(g, "+" + (backgroundCaches.Count - shown) + "轨", font, MutedColor, new RectangleF(x, y, Math.Max(45, right - x), textHeight), false);
            if (reserve > 0)
                DrawText(g, hidden, font, PinkColor, new RectangleF(StageRight - 4 - reserve, y, reserve, textHeight), true);
        }
        private static string ShortTrackName(string value)
        {
            return value.Length > 12 ? value.Substring(0, 11) + "…" : value;
        }

        private void DrawHeader(Graphics g, NoteVisual current)
        {
            RectangleF panel = new RectangleF(StageLeft, 8, StageWidth, headerHeight - 6);
            using (var fill = new SolidBrush(Color.FromArgb(45, 49, 46))) FillRounded(g, fill, panel, 6);
            using (var line = new Pen(Color.FromArgb(114, GoldColor)))
            using (var path = RoundedPath(RectangleF.Inflate(panel, -.5f, -.5f), 6)) g.DrawPath(line, path);
            float rowHeight = (panel.Height - 4) / 2;
            bool countdown = !String.IsNullOrWhiteSpace(CountdownText);
            string countdownLabel = "";
            if (countdown)
            {
                countdownLabel = CountdownCaption(CountdownText);
            }
            float statusWidth = Math.Min(PlaybackStatusWidth, StageWidth * .30f);
            DrawText(g, SongCaption(), largeHeaderFont, TextColor,
                new RectangleF(panel.X + 10, panel.Y + 2, panel.Width - statusWidth - 18, rowHeight), false);
            DrawText(g, countdown ? countdownLabel : PlaybackCaption(),
                labelFont, countdown ? GoldColor : MutedColor,
                new RectangleF(panel.Right - statusWidth - 8, panel.Y + 2, statusWidth, rowHeight), true);
            DrawText(g, CurrentCueText(), smallFont, current != null && !current.Finger.Playable ? PinkColor : GoldColor,
                CurrentCueBounds, false);
            DrawText(g, NextCueText(), smallFont, TextColor, NextCueBounds, false);
            using (var separator = new Pen(Color.FromArgb(82, 84, 69)))
                g.DrawLine(separator, panel.X + panel.Width / 2, 38, panel.X + panel.Width / 2, 54);
        }

        private string SongCaption()
        {
            string title = String.IsNullOrWhiteSpace(SongTitle) ? "选择歌曲，开始看谱" : SongTitle;
            string subtitle = SongSubtitle ?? "";
            if (subtitle.StartsWith("旋律估计 · ", StringComparison.Ordinal)) return "旋律估计 · " + title;
            if (subtitle.StartsWith("旋律候选 · ", StringComparison.Ordinal)) return "旋律候选 · " + title;
            return "当前歌曲：" + title;
        }

        private string PlaybackCaption()
        {
            double speed = Double.IsNaN(PlaybackSpeed) || Double.IsInfinity(PlaybackSpeed) || PlaybackSpeed <= 0 ? 1 : PlaybackSpeed;
            return (Automatic ? "自动" : "跟谱") + " " + speed.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                "× · " + FutureSeconds + "秒预览";
        }

        // The fixed cues remain readable when dense notes become short bars.
        // Reuse the sorted visual cache; no whole-score work belongs in a frame.
        private NoteVisual CurrentVisual()
        {
            int index = FirstStartingAfter(MusicPosition) - 1;
            return index >= 0 && MusicPosition < visuals[index].Note.Start + visuals[index].Note.Duration
                ? visuals[index] : null;
        }
        private static string FingeringCue(NoteVisual item)
        {
            return item.Finger.Playable ? item.Finger.Key + " · " + MouseButtonsText(item.MouseMask, true)
                : "超出音域 · 休止";
        }
        private string CurrentCueText()
        {
            NoteVisual current = CurrentVisual();
            if (current != null) return "当前 " + FingeringCue(current);
            if (visuals.Length == 0) return "当前 等待乐谱";
            int next = FirstStartingAfter(MusicPosition);
            return next == 0 ? "当前 等待首音" : next < visuals.Length ? "当前 休止 · 松开按键" : "当前 旋律已结束";
        }
        private string NextCueText()
        {
            int next = FirstStartingAfter(MusicPosition);
            return next < visuals.Length ? "下一音 " + FingeringCue(visuals[next]) :
                visuals.Length == 0 ? "下一音 等待乐谱" : "下一音 无";
        }

        private RectangleF HeadBounds(NoteVisual item, bool active)
        {
            if (item.Note.Start < MusicPosition && !active) return RectangleF.Empty;
            float bottom = active ? JudgmentY : SceneTimeToY(item.Note.Start);
            float height = preferredHeadHeight;
            int next = item.NextInColumn;
            if (item.NextWarning >= 0 && (next < 0 || item.NextWarning < next)) next = item.NextWarning;
            if (next >= 0)
            {
                float distance = bottom - SceneTimeToY(visuals[next].Note.Start);
                height = Math.Min(height, Math.Max(0, distance - Math.Min(6, distance * .35f)));
            }
            if (height <= 0 || bottom <= StageTop || bottom - height >= JudgmentY) return RectangleF.Empty;
            float width = LargeLayout ? Math.Min(118, ColumnWidth - 10) :
                Math.Min(Compact ? 56 : 66, ColumnWidth - (Compact ? 4 : 8));
            float centerX = StageLeft + (item.KeyIndex + .5f) * ColumnWidth;
            return new RectangleF(centerX - width / 2, bottom - height, width, height);
        }

        private void DrawNote(Graphics g, NoteVisual item, int index, NoteVisual current)
        {
            float centerY = SceneTimeToY(item.Note.Start);
            float endY = SceneTimeToY(item.Note.Start + item.Note.Duration);
            bool active = Object.ReferenceEquals(item, current);
            if (item.Note.Start < MusicPosition && !active) return;
            if (!item.Finger.Playable)
            {
                float bottom = active ? JudgmentY : centerY;
                float height = headTextHeights[headTextHeights.Length - 1];
                if (index + 1 < visuals.Length)
                {
                    float distance = bottom - SceneTimeToY(visuals[index + 1].Note.Start);
                    height = Math.Min(height, Math.Max(0, distance - Math.Min(6, distance * .35f)));
                }
                if (height <= 0) return;
                RectangleF warning = new RectangleF(StageLeft + 8, bottom - height, StageWidth - 16, height);
                using (var brush = new SolidBrush(Color.FromArgb(78, PinkColor))) FillRounded(g, brush, warning, 4);
                if (warning.Top >= StageTop && warning.Bottom <= JudgmentY && height >= smallFont.GetHeight(96) + 4)
                    DrawText(g, "超出口琴音域 · MIDI " + item.Note.Pitch, smallFont, PinkColor, warning, true);
                return;
            }
            float centerX = StageLeft + (item.KeyIndex + .5f) * ColumnWidth;
            Color color = GoldColor;
            RectangleF head = HeadBounds(item, active);
            if (item.Note.Duration >= .45 && Math.Min(JudgmentY, centerY) - endY > 8)
            {
                float tailTop = Math.Max(StageTop, endY);
                float tailBottom = Math.Min(JudgmentY, centerY);
                if (tailBottom > tailTop)
                {
                    float tailWidth = Math.Max(10, Math.Min(LargeLayout ? 118 : 66, ColumnWidth - 10));
                    RectangleF tail = new RectangleF(centerX - tailWidth / 2, tailTop, tailWidth, tailBottom - tailTop);
                    using (var tailBrush = new SolidBrush(Color.FromArgb(active ? 125 : 82, color)))
                        FillRounded(g, tailBrush, tail, 4);
                    using (var tailPen = new Pen(Color.FromArgb(active ? 205 : 140, color), 1))
                    {
                        g.DrawLine(tailPen, tail.Left, tailTop + 3, tail.Left, tailBottom);
                        g.DrawLine(tailPen, tail.Right, tailTop + 3, tail.Right, tailBottom);
                    }
                }
            }
            if (head.IsEmpty) return;
            using (var fill = new SolidBrush(Color.FromArgb(active ? 218 : 177, color)))
            using (var outline = new Pen(Color.FromArgb(active ? 255 : 218, color), active ? 1.7f : 1f))
            {
                if (head.Height < 6) g.FillRectangle(fill, head);
                else
                {
                    RectangleF inset = RectangleF.Inflate(head, -.6f, -.6f);
                    FillRounded(g, fill, inset, 6);
                    using (var path = RoundedPath(inset, 6)) g.DrawPath(outline, path);
                }
            }
            // No partial letters at either viewport edge, and no unreadable
            // overlapping labels during rapid repetitions. Dense notes keep
            // their real timing as short bars; current fingering stays above.
            if (head.Top < StageTop || head.Bottom > JudgmentY) return;
            float iconWidth = item.MouseMask == 0 ? 0 : Math.Min(22, head.Width * .30f);
            if (iconWidth > 0 && head.Height >= 10)
                DrawModifierIcon(g, new RectangleF(head.X + 4, head.Y + 3, iconWidth, head.Height - 6), item.MouseMask, NoteInkColor);
            for (int i = 0; i < headFonts.Length; i++)
            {
                if (head.Height < headTextHeights[i]) continue;
                string suffix = DegreeForKey(item.KeyIndex);
                float keyWidth = (float)Math.Ceiling(g.MeasureString(item.Finger.Key, headFonts[i]).Width);
                float suffixWidth = (float)Math.Ceiling(g.MeasureString(suffix, numberFonts[i]).Width);
                if (keyWidth + suffixWidth + iconWidth + 12 > head.Width)
                {
                    // The icon carries mouse pitch modifiers. Keep this real
                    // keyboard column's degree before omitting a redundant key
                    // label in a narrow card; no second notation system is used.
                    if (suffixWidth + iconWidth + 12 <= head.Width)
                        DrawText(g, suffix, numberFonts[i], NoteInkColor,
                            new RectangleF(head.X + iconWidth + 5, head.Y, head.Width - iconWidth - 10, head.Height), true);
                    break;
                }
                DrawText(g, item.Finger.Key, headFonts[i], NoteInkColor,
                    new RectangleF(head.X + iconWidth + 4, head.Y, keyWidth, head.Height), false);
                DrawText(g, suffix, numberFonts[i], NoteInkColor,
                    new RectangleF(head.X + keyWidth + iconWidth + 6, head.Y, head.Width - keyWidth - iconWidth - 10, head.Height), true);
                break;
            }
        }

        private void DrawJudgment(Graphics g, float y, NoteVisual current)
        {
            using (var halo = new Pen(Color.FromArgb(32, GoldColor), 9))
            using (var main = new Pen(Color.FromArgb(220, GoldColor), 1.5f))
            {
                g.DrawLine(halo, StageLeft, y, StageRight, y);
                g.DrawLine(main, StageLeft, y, StageRight, y);
            }
            if (current == null || current.KeyIndex < 0) return;
            double age = Math.Max(0, MusicPosition - current.Note.Start);
            float pulse = (float)Math.Max(0, 1 - age / .18);
            float x = StageLeft + (current.KeyIndex + .5f) * ColumnWidth;
            float radius = 5 + pulse * 5;
            Color color = GoldColor;
            using (var glow = new SolidBrush(Color.FromArgb(28 + (int)(pulse * 36), color)))
                g.FillEllipse(glow, x - radius, y - radius, radius * 2, radius * 2);
            using (var ring = new Pen(Color.FromArgb(180, color), 1.5f))
                g.DrawEllipse(ring, x - 5, y - 5, 10, 10);
        }

        private void DrawDegreeStrip(Graphics g, NoteVisual current)
        {
            for (int i = 0; i < 8; i++)
            {
                bool active = current != null && current.KeyIndex == i;
                DrawText(g, DegreeNames[i], degreeFont, active ? GoldColor : TextColor,
                    new RectangleF(StageLeft + i * ColumnWidth, DegreeBounds.Y, ColumnWidth, DegreeBounds.Height), true);
            }
        }

        private void DrawKeyStrip(Graphics g, NoteVisual current)
        {
            DrawText(g, "键盘：音级对应字母键", hintFont, GoldColor,
                new RectangleF(20, 252, 336, 22), false);
            for (int i = 0; i < 8; i++)
            {
                bool active = current != null && current.KeyIndex == i;
                RectangleF cell = KeyLegendCell(i);
                using (var fill = new SolidBrush(active ? Color.FromArgb(76, GoldColor) : Color.FromArgb(58, 62, 54)))
                    FillRounded(g, fill, cell, 4);
                if (active)
                    using (var outline = new Pen(GoldColor, 1.5f))
                    using (var path = RoundedPath(RectangleF.Inflate(cell, -.8f, -.8f), 4))
                        g.DrawPath(outline, path);
                DrawText(g, DegreeNames[i], keyboardFont, GoldColor,
                    new RectangleF(cell.X + 3, cell.Y, 31, cell.Height), true);
                DrawText(g, KeyNames[i], keyboardFont, TextColor,
                    new RectangleF(cell.X + 36, cell.Y, cell.Width - 39, cell.Height), true);
            }
            DrawText(g, KeyboardRuleText, hintFont, TextColor,
                new RectangleF(20, 332, 336, 20), false);
        }

        private void DrawFingeringHints(Graphics g)
        {
            DrawText(g, MouseRuleText, hintFont, TextColor, FingeringHintBounds, false);
        }

        private void DrawMouseLegend(Graphics g)
        {
            NoteVisual current = CurrentVisual();
            RectangleF panel = MouseLegendBounds;
            using (var fill = new SolidBrush(Color.FromArgb(46, 49, 45))) FillRounded(g, fill, panel, 7);
            using (var border = new Pen(Color.FromArgb(151, 146, 111)))
            using (var path = RoundedPath(RectangleF.Inflate(panel, -.6f, -.6f), 7)) g.DrawPath(border, path);
            DrawText(g, "按键说明", largeHeaderFont, GoldColor, new RectangleF(panel.X + 12, panel.Y + 2, panel.Width - 24, 26), false);
            float iconWidth = 44;
            float mouseWidth = 170;
            RectangleF first = LegendRowBounds(0);
            DrawText(g, "图示", hintFont, MutedColor, new RectangleF(first.X, 34, iconWidth, 20), true);
            DrawText(g, "鼠标按键", hintFont, MutedColor, new RectangleF(first.X + iconWidth + 5, 34, mouseWidth - 5, 20), false);
            DrawText(g, "音高变化", hintFont, MutedColor, new RectangleF(first.X + iconWidth + mouseWidth + 5,
                34, first.Width - iconWidth - mouseWidth - 5, 20), false);
            for (int i = 0; i < LegendFingers.Length; i++)
            {
                int mask = ModifierMask(LegendFingers[i]);
                RectangleF row = LegendRowBounds(i);
                if (current != null && current.Finger.Playable && current.MouseMask == mask)
                {
                    RectangleF highlight = RectangleF.Inflate(row, -.8f, -2);
                    using (var fill = new SolidBrush(Color.FromArgb(52, GoldColor))) FillRounded(g, fill, highlight, 4);
                    using (var line = new Pen(Color.FromArgb(205, GoldColor), 1.2f))
                    using (var path = RoundedPath(highlight, 4)) g.DrawPath(line, path);
                }
                RectangleF block = new RectangleF(row.X + 2, row.Y + (row.Height - 22) / 2, iconWidth - 5, 22);
                using (var fill = new SolidBrush(Color.FromArgb(190, GoldColor))) FillRounded(g, fill, block, 3);
                using (var line = new Pen(Color.FromArgb(220, GoldColor)))
                using (var path = RoundedPath(RectangleF.Inflate(block, -.5f, -.5f), 3)) g.DrawPath(line, path);
                DrawModifierIcon(g, RectangleF.Inflate(block, -7, -4), mask, NoteInkColor);
                RectangleF mouse = new RectangleF(row.X + iconWidth + 4, row.Y + (row.Height - 26) / 2, 18, 26);
                DrawMouseDiagram(g, mouse, mask);
                DrawText(g, MouseButtonsText(mask, false), hintFont, TextColor,
                    new RectangleF(mouse.Right + 7, row.Y, mouseWidth - 30, row.Height), false);
                DrawText(g, ModifierMeaning(mask).Replace(" + 升半音", "＋半音"), hintFont, GoldColor,
                    new RectangleF(row.X + iconWidth + mouseWidth + 5, row.Y,
                        row.Width - iconWidth - mouseWidth - 5, row.Height), false);
                using (var line = new Pen(Color.FromArgb(76, 82, 66)))
                    g.DrawLine(line, row.Left, row.Bottom, row.Right, row.Bottom);
            }
            DrawFingeringHints(g);
        }

        private static string DegreeForKey(int keyIndex)
        {
            return keyIndex >= 0 && keyIndex < DegreeNames.Length ? DegreeNames[keyIndex] : "—";
        }

        private static string SpokenPitch(Fingering finger)
        {
            if (finger == null || !finger.Playable) return "超出口琴音域";
            string number = finger.Number ?? "";
            bool sharp = number.StartsWith("#", StringComparison.Ordinal);
            int high = 0, low = 0;
            foreach (char symbol in number)
            {
                if (symbol == '\'' || symbol == '′') high++;
                else if (symbol == ',') low++;
            }
            string degree = number.Trim('#', '\'', '′', ',');
            string octave = high > 0 ? high == 1 ? "高八度" : "高" + high + "个八度" :
                low > 0 ? low == 1 ? "低八度" : "低" + low + "个八度" : "";
            return octave + degree + (sharp ? "升半音" : "");
        }

        private static string CountdownCaption(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return "";
            if (text.Contains("暂停") || text.Contains("继续")) return text;
            int secondsEnd = text.IndexOf('秒');
            return (secondsEnd >= 0 ? text.Substring(0, secondsEnd + 1) : "演奏准备") + " · 切回游戏";
        }

        private static int ModifierMask(Fingering finger)
        {
            string modifier = finger == null ? "" : finger.Modifier ?? "";
            return (modifier.Contains("左键") ? 1 : 0) | (modifier.Contains("右键") ? 2 : 0) |
                (modifier.Contains("中键") ? 4 : 0);
        }
        private static string ModifierMeaning(int mask)
        {
            string octave = (mask & 1) != 0 ? "低八度" : (mask & 2) != 0 ? "高八度" : "";
            return octave.Length == 0 ? (mask & 4) != 0 ? "升半音" : "普通音" :
                octave + ((mask & 4) != 0 ? " + 升半音" : "");
        }
        private static string MouseButtonsText(int mask, bool concise)
        {
            if (mask == 0) return "不按鼠标";
            if (mask == 4) return concise ? "中键" : "仅按中键";
            string button = (mask & 1) != 0 ? "左键" : "右键";
            return (mask & 4) != 0 ? button + " + 中键" : concise ? button : "仅按" + button;
        }
        private static void DrawModifierIcon(Graphics g, RectangleF bounds, int mask, Color color)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            float middleX = bounds.X + bounds.Width / 2, middleY = bounds.Y + bounds.Height / 2;
            if ((mask & 3) == 0)
            {
                if ((mask & 4) != 0)
                {
                    float size = Math.Max(3, Math.Min(bounds.Width, bounds.Height) * .48f);
                    using (var fill = new SolidBrush(color)) g.FillEllipse(fill, middleX - size / 2, middleY - size / 2, size, size);
                }
                return;
            }
            float halfWidth = Math.Min(bounds.Width / 2 - 1, 9);
            float halfHeight = Math.Min(bounds.Height / 2 - 1, 7);
            float direction = (mask & 1) != 0 ? -1 : 1;
            PointF[] points = {
                new PointF(middleX + direction * halfWidth, middleY),
                new PointF(middleX, middleY - halfHeight),
                new PointF(middleX, middleY - halfHeight * .40f),
                new PointF(middleX - direction * halfWidth, middleY - halfHeight * .40f),
                new PointF(middleX - direction * halfWidth, middleY + halfHeight * .40f),
                new PointF(middleX, middleY + halfHeight * .40f),
                new PointF(middleX, middleY + halfHeight)
            };
            if ((mask & 4) != 0)
                using (var fill = new SolidBrush(color)) g.FillPolygon(fill, points);
            else using (var line = new Pen(color, 1.4f)) g.DrawPolygon(line, points);
        }
        private static void DrawMouseDiagram(Graphics g, RectangleF bounds, int mask)
        {
            using (var line = new Pen(Color.FromArgb(190, 191, 168), 1.1f))
            using (var path = RoundedPath(bounds, 7))
            {
                g.DrawPath(line, path);
                g.DrawLine(line, bounds.X + bounds.Width / 2, bounds.Y + 3,
                    bounds.X + bounds.Width / 2, bounds.Y + 11);
                g.DrawLine(line, bounds.X + 2, bounds.Y + 12, bounds.Right - 2, bounds.Y + 12);
            }
            using (var fill = new SolidBrush(GoldColor))
            {
                if ((mask & 1) != 0) FillRounded(g, fill, new RectangleF(bounds.X + 3, bounds.Y + 3, 4, 8), 1.5f);
                if ((mask & 2) != 0) FillRounded(g, fill, new RectangleF(bounds.Right - 7, bounds.Y + 3, 4, 8), 1.5f);
                if ((mask & 4) != 0) FillRounded(g, fill, new RectangleF(bounds.X + bounds.Width / 2 - 1.5f, bounds.Y + 3, 3, 7), 1);
            }
        }

        private void DrawProgress(Graphics g, double position)
        {
            float y = ProgressBounds.Y + ProgressBounds.Height / 2;
            float x = StageLeft + (float)(Math.Max(0, Math.Min(1, position / Math.Max(.001, PlaybackDuration))) * StageWidth);
            using (var track = new Pen(Color.FromArgb(84, 86, 72), 2)) g.DrawLine(track, StageLeft, y, StageRight, y);
            using (var progress = new Pen(GoldColor, 2.5f)) g.DrawLine(progress, StageLeft, y, x, y);
            using (var dot = new SolidBrush(TextColor)) g.FillEllipse(dot, x - 3, y - 3, 6, 6);
        }

        private static Color Blend(Color foreground, Color background, float amount)
        {
            return Color.FromArgb((int)(foreground.R * amount + background.R * (1 - amount)),
                (int)(foreground.G * amount + background.G * (1 - amount)),
                (int)(foreground.B * amount + background.B * (1 - amount)));
        }
        private static void DrawText(Graphics g, string text, Font font, Color color, RectangleF rect, bool centered)
        {
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat(StringFormat.GenericDefault))
            {
                format.Alignment = centered ? StringAlignment.Center : StringAlignment.Near;
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags = StringFormatFlags.NoWrap;
                format.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(text, font, brush, rect, format);
            }
        }
        private static GraphicsPath RoundedPath(RectangleF rect, float radius)
        {
            float diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            var path = new GraphicsPath();
            if (diameter <= 0) { path.AddRectangle(rect); return path; }
            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
        private static void FillRounded(Graphics g, Brush brush, RectangleF rect, float radius)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using (var path = RoundedPath(rect, radius)) g.FillPath(brush, path);
        }
    }
}
