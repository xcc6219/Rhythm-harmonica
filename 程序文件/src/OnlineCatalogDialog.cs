using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HarmonicaStudio
{
    internal sealed class OnlineCatalogDialog : Form
    {
        private readonly IList<SongEntry> library;
        private readonly Func<string, string, SongEntry> importMidi, createJianpu;
        private readonly Func<FreeScore, byte[], SongEntry> importFree;
        private readonly Func<string, int, FreeScorePage> freeSearch;
        private readonly Func<FreeScore, byte[]> freeDownload;
        private readonly Func<string, int, List<OnlineSong>> musicSearch;
        private readonly Func<SongEntry, SongEntry> convertScore;
        private readonly Func<ChineseScore, byte[], SongEntry> importChinese;
        private readonly Func<ChineseScore, byte[]> chineseDownload;
        private ChineseCatalogPanel chinesePanel;
        private bool chineseLoaded, freeLoaded;
        private readonly TextBox freeQuery, musicQuery;
        private readonly Label freeStatus, musicStatus, localHint;
        private readonly ListView freeResults, musicResults;
        private readonly ComboBox localScores, musicProvider;
        private readonly SoftButton freeGo, freePrevious, freeNext, download, musicGo, musicPrevious, musicNext, choose;
        private readonly SoftButton songDownload, officialMusic, qqLogin;
        private readonly ProgressBar songProgress;
        private readonly TabControl tabs;
        private readonly TabPage musicTab, freeTab;
        private TabPage chineseTab;
        private int freePage = 1, musicPage = 1, freeGeneration, musicGeneration;
        private string freeTerm = "", musicTerm = "";
        private bool downloading, freeHasNext, searchingFree, searchingMusic, closingDialog, matchesQueued, updatingMatches;
        public SongEntry ChosenScore { get; private set; }
        public OnlineSong ConversionSong { get; private set; }
        public Func<string, string, SongEntry> TranscribeAudio { get; set; }
        public Func<OnlineSong, SongEntry> DownloadAudio { get; set; }
        public Func<string, string, byte[], SongEntry> ImportTranscribedAudio { get; set; }
        internal Func<string, string, int, List<OnlineSong>> SearchSongsByProvider { get; set; }
        internal Func<OnlineSong, string> ResolveSongAudio { get; set; }
        internal Func<string, string, Action<string>, CancellationToken, Task<string>> DownloadSongAudio { get; set; }
        internal Func<string, string, Action<string>, CancellationToken, Task<byte[]>> ConvertLocalAudio { get; set; }
        internal Func<string> SelectAudioFile { get; set; }
        private CancellationTokenSource songCancellation;
        private bool inlineWorking, hasInlineResult;
        private int songOperation;
        private OnlineSong completedSong, downloadedSong, selectedMusic;
        private string downloadedAudioPath, inlineTitle;
        private bool preferMelodyLookup;
        private string melodyLookupTitle, melodyLookupArtist;
        private readonly Dictionary<SongEntry, RemoteScoreChoice> localScoreSources = new Dictionary<SongEntry, RemoteScoreChoice>();
        private readonly Dictionary<Control, bool> inlineControlStates = new Dictionary<Control, bool>();
        private bool CanDownloadAudio { get { return ImportTranscribedAudio != null || DownloadAudio != null; } }
        private bool CanUpdateControls { get { return !closingDialog && !IsDisposed && !Disposing; } }

        // Called before ShowDialog by the library action. This uses the delivered
        // score index immediately; QQ search is an explicit, independent action.
        internal void PreferExistingMelody(string title, string artist)
        {
            if (!CanUpdateControls || downloading || searchingMusic || inlineWorking) return;
            melodyLookupTitle = StripOwnedEdition(title);
            melodyLookupArtist = (artist ?? "").Trim();
            preferMelodyLookup = true;
            musicQuery.Text = melodyLookupTitle.Length > 100 ? melodyLookupTitle.Substring(0, 100) : melodyLookupTitle;
            tabs.SelectedTab = musicTab;
            UpdateLocalMatches();
            musicStatus.Text = "查找「" + musicQuery.Text + "」的现成主旋律谱；原曲目和 MIDI 保留，钢琴编配须先试听。";
            musicStatus.ForeColor = Theme.Muted;
        }

        public OnlineCatalogDialog(string initial, IList<SongEntry> songs,
            Func<string, string, SongEntry> midi, Func<string, string, SongEntry> jianpu,
            Func<FreeScore, byte[], SongEntry> free)
            : this(initial, songs, midi, jianpu, free, FreeScoreCatalog.Search, FreeScoreCatalog.Download, null) { }

        public OnlineCatalogDialog(string initial, IList<SongEntry> songs,
            Func<string, string, SongEntry> midi, Func<string, string, SongEntry> jianpu,
            Func<FreeScore, byte[], SongEntry> free, Func<SongEntry, SongEntry> convert,
            Func<ChineseScore, byte[], SongEntry> chinese)
            : this(initial, songs, midi, jianpu, free, convert, chinese,
                FreeScoreCatalog.Search, FreeScoreCatalog.Download, null, ChineseScoreCatalog.Download) { }

        internal OnlineCatalogDialog(string initial, IList<SongEntry> songs,
            Func<string, string, SongEntry> midi, Func<string, string, SongEntry> jianpu,
            Func<FreeScore, byte[], SongEntry> free, Func<SongEntry, SongEntry> convert,
            Func<ChineseScore, byte[], SongEntry> chinese, Func<string, int, FreeScorePage> searchFree,
            Func<FreeScore, byte[]> downloadFree, Func<string, int, List<OnlineSong>> searchMusic,
            Func<ChineseScore, byte[]> downloadChinese)
            : this(initial, songs, midi, jianpu, free, searchFree, downloadFree, searchMusic)
        {
            convertScore = convert; importChinese = chinese; chineseDownload = downloadChinese;
            chineseTab = new TabPage("备用中文 MIDI") { Name = "ChineseMidi", BackColor = Theme.Panel };
            chinesePanel = new ChineseCatalogPanel(
                delegate(ChineseScore score, byte[] bytes) { return CanUpdateControls ? chinese(score, bytes) : null; },
                delegate(SongEntry entry) { if (CanUpdateControls) { ChosenScore = entry; DialogResult = DialogResult.OK; } }, SetImportBusy, downloadChinese);
            chineseTab.Controls.Add(chinesePanel);
            tabs.TabPages.Remove(freeTab); tabs.TabPages.Add(chineseTab); tabs.TabPages.Add(freeTab);
            tabs.SelectedTab = musicTab;
            choose.Text = "选用所选 MIDI";
            Text = "QQ / 酷狗歌曲搜索与 MIDI 转录";
        }

        internal OnlineCatalogDialog(string initial, IList<SongEntry> songs,
            Func<string, string, SongEntry> midi, Func<string, string, SongEntry> jianpu,
            Func<FreeScore, byte[], SongEntry> free, Func<string, int, FreeScorePage> searchFree,
            Func<FreeScore, byte[]> downloadFree, Func<string, int, List<OnlineSong>> searchMusic)
        {
            library = songs; importMidi = midi; createJianpu = jianpu; importFree = free;
            freeSearch = searchFree; freeDownload = downloadFree; musicSearch = searchMusic;
            ResolveSongAudio = MusicAudioSources.ResolveAudioUrl;
            DownloadSongAudio = SongAudioDownload.DownloadAsync;
            ConvertLocalAudio = ConvertWithRuntime;
            Text = "在线曲库 · 免费乐谱与歌曲搜索";
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(820, 578); FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent; MaximizeBox = MinimizeBox = false;
            Font = Theme.PixelFont(9); ForeColor = Theme.Text; BackColor = Theme.Background;
            Controls.Add(Label("在线曲库", 20, 14, 450, 27, 15, Theme.Text));
            qqLogin = Button("QQ / 微信登录", 610, 10, 190, false, OpenQqLogin);
            Controls.Add(qqLogin);
            UpdateAccountButton();
            Controls.Add(Label("QQ／酷狗搜歌；软件内登录可使用 QQ 音乐账号，外部浏览器登录不会同步到本软件。", 20, 44, 774, 22, 9, Theme.Muted));
            tabs = new TabControl { Location = new Point(20, 79), Size = new Size(780, 456), Font = Theme.PixelFont(10) };
            freeTab = new TabPage("备用免费谱") { Name = "FreeScores", BackColor = Theme.Panel };
            musicTab = new TabPage("QQ / 酷狗搜歌") { Name = "MusicSearch", BackColor = Theme.Panel };
            tabs.TabPages.Add(musicTab); tabs.TabPages.Add(freeTab); tabs.SelectedTab = musicTab; Controls.Add(tabs);
            Controls.Add(Label("导入后回主界面选择旋律声轨，先试听，再调整移调和速度。", 20, 543, 780, 22, 9, Theme.Muted));

            freeQuery = Input("", 16, 17, 448);
            freeQuery.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SubmitFreeSearch(); } };
            freeGo = Button("搜索 / 浏览", 477, 14, 108, true, SubmitFreeSearch);
            freePrevious = Button("上一页", 599, 14, 67, false, delegate { SearchFree(freePage - 1); });
            freeNext = Button("下一页", 676, 14, 67, false, delegate { SearchFree(freePage + 1); });
            freeStatus = Label("正在准备免费曲库…", 16, 58, 727, 38, 8, Theme.Muted);
            freeResults = Results(16, 101, 727, 216, new string[] { "曲名", "作曲家", "乐器", "许可" }, new int[] { 253, 159, 134, 155 });
            freeResults.SelectedIndexChanged += delegate { UpdateFreeChoice(); };
            freeResults.DoubleClick += delegate { DownloadChoice(); };
            freeTab.Controls.AddRange(new Control[] { freeQuery, freeGo, freePrevious, freeNext, freeStatus, freeResults });
            freeTab.Controls.Add(Label("支持曲名、作曲家或乐器；留空浏览。常用中文名称会转换为英文关键词。", 16, 325, 727, 25, 8, Theme.Muted));
            freeTab.Controls.Add(Label("以每首乐谱的许可为准；多声轨作品导入后需选择主旋律。", 16, 349, 727, 24, 8, Theme.Muted));
            freeTab.Controls.Add(Button("来源与许可 ↗", 16, 381, 128, false, OpenFreeSource));
            freeTab.Controls.Add(Button("打开曲库官网 ↗", 155, 381, 138, false, delegate { OpenUrl("https://www.mutopiaproject.org/"); }));
            download = Button("↓ 下载并加入曲库", 570, 381, 173, true, DownloadChoice);
            freeTab.Controls.Add(download);

            musicProvider = new ComboBox { Location = new Point(16, 17), Width = 124, DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat, Font = Theme.PixelFont(10), BackColor = Theme.Input, ForeColor = Theme.Text };
            musicProvider.Items.AddRange(new object[] { "QQ音乐", "酷狗音乐" }); musicProvider.SelectedIndex = 0;
            musicQuery = Input(initial ?? "", 152, 17, 312);
            musicQuery.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SubmitMusicSearch(); } };
            musicGo = Button("搜索歌曲", 477, 14, 108, true, SubmitMusicSearch);
            musicPrevious = Button("上一页", 599, 14, 67, false, delegate { SearchMusic(musicPage - 1); });
            musicNext = Button("下一页", 676, 14, 67, false, delegate { SearchMusic(musicPage + 1); });
            musicStatus = Label("选择平台并搜索版本；优先选用主旋律谱，需要转录时点“下载录音 → MIDI”。", 16, 58, 727, 52, 9, Theme.Muted);
            musicStatus.UseMnemonic = false;
            songProgress = new ProgressBar { Location = new Point(16, 114), Size = new Size(727, 4), Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Visible = false };
            musicResults = Results(16, 125, 727, 164, new string[] { "歌曲", "歌手", "专辑", "时长" }, new int[] { 258, 160, 210, 73 });
            musicResults.SelectedIndexChanged += delegate
            {
                OnlineSong previousSong = selectedMusic;
                UpdateSelectedMusic();
                if (!inlineWorking && hasInlineResult && !Object.ReferenceEquals(SelectedMusic, completedSong))
                { completedSong = null; hasInlineResult = false; ChosenScore = null; }
                if (!inlineWorking && !searchingMusic && SelectedMusic != null && !Object.ReferenceEquals(previousSong, SelectedMusic))
                    SetSongStatus(SelectedMusic, "已选择此歌曲版本。优先查看下方主旋律谱；录音转录是独立操作。", Theme.Muted);
                QueueLocalMatches(); UpdateMusicActions();
            };
            musicResults.DoubleClick += delegate { ActivateRecommendedChoice(); };
            localHint = Label("优先选用明确的主旋律谱；录音转录可使用独立按钮。", 16, 296, 727, 26, 9, Theme.Muted);
            localScores = new ComboBox { Location = new Point(16, 330), Width = 552, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = Theme.PixelFont(9), BackColor = Theme.Input, ForeColor = Theme.Text };
            localScores.DrawMode = DrawMode.OwnerDrawFixed; localScores.ItemHeight = 24;
            localScores.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                if (IsDisposed || Disposing || closingDialog) return;
                using (var fill = new SolidBrush(Theme.Input)) e.Graphics.FillRectangle(fill, e.Bounds);
                if (e.Index < 0 || e.Index >= localScores.Items.Count) return;
                SongEntry entry = localScores.Items[e.Index] as SongEntry;
                RemoteScoreChoice remote = localScores.Items[e.Index] as RemoteScoreChoice;
                string text = entry == null ? (remote == null ? "" : remote.ToString()) : LocalScoreDescription(entry);
                TextRenderer.DrawText(e.Graphics, text, localScores.Font, e.Bounds, Theme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            choose = Button("选用所选 MIDI", 582, 327, 161, false, ChooseLocal);
            localScores.SelectedIndexChanged += delegate { if (!IsDisposed && !Disposing) UpdateMusicActions(); };
            musicTab.Controls.AddRange(new Control[] { musicProvider, musicQuery, musicGo, musicPrevious, musicNext, musicStatus, songProgress, musicResults, localHint, localScores, choose });
            musicTab.Controls.Add(Button("＋ MIDI 谱", 16, 381, 106, false, delegate { AddSongScore(false); }));
            musicTab.Controls.Add(Button("＋ 粘贴简谱", 132, 381, 106, false, delegate { AddSongScore(true); }));
            musicTab.Controls.Add(Button("已有音频转 MIDI", 248, 381, 141, false, AddAudioScore));
            officialMusic = Button("官方歌曲页 ↗", 399, 381, 143, false, OpenMusicSource);
            songDownload = Button("下载录音 → MIDI", 552, 381, 191, true, DownloadSelectedAudio);
            musicTab.Controls.AddRange(new Control[] { officialMusic, songDownload });
            tabs.Selecting += delegate(object sender, TabControlCancelEventArgs e) { if (inlineWorking && e.TabPage != musicTab) e.Cancel = true; };
            tabs.SelectedIndexChanged += delegate { LoadSelectedBackupCatalog(); };
            freePrevious.Enabled = freeNext.Enabled = download.Enabled = musicPrevious.Enabled = musicNext.Enabled = choose.Enabled = songDownload.Enabled = false;
            musicProvider.SelectedIndexChanged += delegate
            {
                if (downloading || searchingMusic || IsDisposed || Disposing) return;
                musicGeneration++; musicPage = 1; selectedMusic = completedSong = null; hasInlineResult = false; ChosenScore = null;
                musicResults.Items.Clear(); localScores.Items.Clear();
                musicPrevious.Enabled = musicNext.Enabled = false;
                musicStatus.Text = "已切换到 " + CurrentMusicProvider + "，点击搜索获取这个平台的歌曲版本。";
                musicStatus.ForeColor = Theme.Muted;
                UpdateLocalMatches(); UpdateMusicActions();
            };
            Shown += delegate
            {
                if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                LoadSelectedBackupCatalog();
                if (preferMelodyLookup) UpdateLocalMatches();
                else if (tabs.SelectedTab == musicTab && !String.IsNullOrWhiteSpace(musicQuery.Text)) SubmitMusicSearch();
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (e.CloseReason != CloseReason.UserClosing || inlineWorking) { BeginClosing(); return; }
                if (downloading) { e.Cancel = true; freeStatus.Text = "正在导入，请稍候完成。"; return; }
                BeginClosing();
            };
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) BeginClosing();
            base.Dispose(disposing);
        }
        private void BeginClosing()
        {
            if (closingDialog) return;
            closingDialog = true; songOperation++; freeGeneration++; musicGeneration++;
            CancelActiveSong();
        }
        private void CancelActiveSong()
        {
            if (songCancellation == null) return;
            try { songCancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
        }

        private void LoadSelectedBackupCatalog()
        {
            if (!IsHandleCreated || closingDialog || IsDisposed || Disposing) return;
            if (tabs.SelectedTab == chineseTab && chinesePanel != null && !chineseLoaded)
            { chineseLoaded = true; chinesePanel.Search(); }
            else if (tabs.SelectedTab == freeTab && !freeLoaded)
            { freeLoaded = true; SubmitFreeSearch(); }
        }

        private static Label Label(string text, int x, int y, int width, int height, float size, Color color)
        { return new Label { Text = text, Location = new Point(x, y), Size = new Size(width, height), Font = Theme.PixelFont(size), ForeColor = color, TextAlign = ContentAlignment.MiddleLeft }; }
        private static TextBox Input(string text, int x, int y, int width)
        { return new TextBox { Text = text.Length > 100 ? text.Substring(0, 100) : text, MaxLength = 100, Location = new Point(x, y), Width = width, Font = Theme.PixelFont(11), BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle }; }
        private static SoftButton Button(string text, int x, int y, int width, bool primary, Action action)
        { var result = new SoftButton(text, primary) { Location = new Point(x, y), Width = width }; result.Click += delegate { action(); }; return result; }
        private static ListView Results(int x, int y, int width, int height, string[] headings, int[] widths)
        {
            var result = new ListView { Location = new Point(x, y), Size = new Size(width, height), View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.PixelFont(9) };
            for (int i = 0; i < headings.Length; i++) result.Columns.Add(headings[i], widths[i]);
            return result;
        }
        private void SubmitFreeSearch()
        {
            if (downloading || searchingFree) return;
            freeTerm = FreeKeyword(freeQuery.Text.Trim()); SearchFree(1);
        }
        private async void SearchFree(int page)
        {
            if (!CanUpdateControls || downloading || searchingFree || page < 1) return;
            EnsureUiContext();
            searchingFree = true;
            string term = freeTerm;
            int generation = ++freeGeneration;
            freeGo.Enabled = freePrevious.Enabled = freeNext.Enabled = download.Enabled = false;
            freeStatus.Text = "正在连接 Mutopia 免费 MIDI 曲库…";
            freeStatus.ForeColor = Theme.Muted;
            try
            {
                FreeScorePage results = await Task.Run(delegate { return freeSearch(term, page); });
                if (!CanUpdateControls || generation != freeGeneration) return;
                freeTerm = term; freePage = page;
                freeHasNext = results.HasNext;
                ShowFreeResults(results);
                string applied = results.AppliedQuery ?? term;
                freeStatus.Text = "第 " + page + " 页 · " + results.Items.Count + " 首可直接导入的 MIDI" + (applied.Length > 0 ? " · 关键词：" + applied : " · 曲库浏览");
                if (results.SkippedArchives > 0) freeStatus.Text += " · " + results.SkippedArchives + " 个多文件包请在官网查看";
                if (results.Items.Count == 0) freeStatus.Text += "。可尝试英文标题或作曲家。";
                freePrevious.Enabled = page > 1; freeNext.Enabled = results.HasNext;
            }
            catch (Exception ex)
            {
                if (!CanUpdateControls || generation != freeGeneration) return;
                freeStatus.Text = ex.Message + " 可打开曲库官网。"; freeStatus.ForeColor = Theme.Warm;
                freeResults.Items.Clear(); freePrevious.Enabled = freeNext.Enabled = false;
            }
            finally { searchingFree = false; if (CanUpdateControls && generation == freeGeneration) { freeGo.Enabled = true; UpdateFreeChoice(); } }
        }
        private void ShowFreeResults(FreeScorePage page)
        {
            freeResults.BeginUpdate(); freeResults.Items.Clear();
            foreach (FreeScore score in page.Items)
            {
                var item = new ListViewItem(new string[] { score.Title, score.Composer, score.Instrument, score.License });
                item.Tag = score; freeResults.Items.Add(item);
            }
            freeResults.EndUpdate();
            if (freeResults.Items.Count > 0) freeResults.Items[0].Selected = true;
        }
        private void UpdateFreeChoice()
        { if (IsDisposed || Disposing || closingDialog) return; download.Enabled = !downloading && freeGo.Enabled && freeResults.SelectedItems.Count > 0; }
        private async void DownloadChoice()
        {
            if (!CanUpdateControls || downloading || !download.Enabled || freeResults.SelectedItems.Count == 0) return;
            EnsureUiContext();
            FreeScore score = (FreeScore)freeResults.SelectedItems[0].Tag;
            downloading = true; tabs.Enabled = false; freeGo.Enabled = freePrevious.Enabled = freeNext.Enabled = download.Enabled = false;
            freeStatus.Text = "正在下载并校验「" + score.Title + "」…"; freeStatus.ForeColor = Theme.Muted;
            try
            {
                byte[] bytes = await Task.Run(delegate { return freeDownload(score); });
                if (!CanUpdateControls) return;
                SongEntry saved = importFree(score, bytes);
                if (!CanUpdateControls) return;
                downloading = false;
                if (saved != null) { ChosenScore = saved; DialogResult = DialogResult.OK; }
            }
            catch (Exception ex)
            {
                if (CanUpdateControls) { freeStatus.Text = "未能导入：" + ex.Message; freeStatus.ForeColor = Theme.Warm; }
            }
            finally
            {
                downloading = false;
                if (CanUpdateControls) { tabs.Enabled = true; freeGo.Enabled = true; freePrevious.Enabled = freePage > 1; freeNext.Enabled = freeHasNext; UpdateFreeChoice(); }
            }
        }
        private void OpenFreeSource()
        {
            FreeScore score = freeResults.SelectedItems.Count > 0 ? (FreeScore)freeResults.SelectedItems[0].Tag : null;
            OpenUrl(score == null ? "https://www.mutopiaproject.org/legal.html" : score.InfoUrl);
        }
        private void SubmitMusicSearch()
        {
            if (downloading || searchingMusic) return;
            preferMelodyLookup = false;
            musicTerm = musicQuery.Text.Trim(); SearchMusic(1);
        }
        private async void SearchMusic(int page)
        {
            if (!CanUpdateControls || downloading || searchingMusic || page < 1) return;
            EnsureUiContext();
            string term = musicTerm;
            string provider = CurrentMusicProvider;
            if (term.Length == 0) { musicStatus.Text = "请输入歌名或歌手。"; musicQuery.Focus(); return; }
            searchingMusic = true;
            qqLogin.Enabled = false;
            completedSong = null; hasInlineResult = false; ChosenScore = null;
            int generation = ++musicGeneration;
            musicGo.Enabled = musicPrevious.Enabled = musicNext.Enabled = choose.Enabled = songDownload.Enabled = musicProvider.Enabled = false;
            selectedMusic = null; musicResults.Items.Clear(); localScores.Items.Clear();
            musicStatus.Text = "正在搜索 " + provider + " 歌曲与版本…"; musicStatus.ForeColor = Theme.Muted;
            try
            {
                List<OnlineSong> results = await Task.Run(delegate
                { return SearchSongsByProvider != null ? SearchSongsByProvider(provider, term, page) : musicSearch != null ? musicSearch(term, page) : MusicAudioSources.Search(provider, term, page); });
                if (!CanUpdateControls || generation != musicGeneration) return;
                foreach (OnlineSong song in results) if (String.IsNullOrWhiteSpace(song.Provider)) song.Provider = provider;
                musicTerm = term; musicPage = page; ShowMusicResults(results);
                musicStatus.Text = provider + " · 第 " + page + " 页 · " + results.Count + " 个版本。优先查看主旋律谱，录音转录另行选择。";
                if (results.Count == 0) musicStatus.Text = provider + " 没有匹配歌曲，可换关键词或打开官方歌曲页。";
                musicPrevious.Enabled = page > 1; musicNext.Enabled = results.Count >= KugouCatalog.PageSize;
            }
            catch (Exception ex)
            {
                if (!CanUpdateControls || generation != musicGeneration) return;
                musicResults.Items.Clear(); QueueLocalMatches();
                musicStatus.Text = ex.Message; musicStatus.ForeColor = Theme.Warm;
            }
            finally { searchingMusic = false; if (CanUpdateControls && generation == musicGeneration) { musicGo.Enabled = musicProvider.Enabled = true; QueueLocalMatches(); UpdateMusicActions(); } }
        }
        private void ShowMusicResults(List<OnlineSong> results)
        {
            selectedMusic = null; musicResults.BeginUpdate(); musicResults.Items.Clear();
            foreach (OnlineSong song in results)
            {
                var item = new ListViewItem(new string[] { song.Title, song.Artist, song.Album, (song.DurationSeconds / 60).ToString("00") + ":" + (song.DurationSeconds % 60).ToString("00") });
                item.Tag = song; musicResults.Items.Add(item);
            }
            musicResults.EndUpdate();
            if (musicResults.Items.Count > 0) musicResults.Items[0].Selected = true;
            UpdateSelectedMusic();
            QueueLocalMatches();
        }
        private OnlineSong SelectedMusic
        { get { return closingDialog || IsDisposed || Disposing ? null : selectedMusic; } }
        private void UpdateSelectedMusic()
        {
            if (closingDialog || IsDisposed || Disposing || musicResults.IsDisposed || musicResults.Disposing) return;
            selectedMusic = null;
            foreach (ListViewItem item in musicResults.Items)
                if (item.Selected) { selectedMusic = item.Tag as OnlineSong; break; }
        }
        private string CurrentMusicProvider
        { get { return Convert.ToString(musicProvider.SelectedItem) == "酷狗音乐" ? "酷狗音乐" : "QQ音乐"; } }
        private string ChosenTitle()
        { OnlineSong song = SelectedMusic; return song == null ? musicQuery.Text.Trim() : song.Title; }
        private void QueueLocalMatches()
        {
            if (matchesQueued || IsDisposed || Disposing || closingDialog || !IsHandleCreated) return;
            matchesQueued = true;
            try { BeginInvoke(new Action(delegate { matchesQueued = false; UpdateLocalMatches(); })); }
            catch (InvalidOperationException) { matchesQueued = false; }
        }
        private void UpdateLocalMatches()
        {
            if (IsDisposed || Disposing || closingDialog || localScores.IsDisposed || localScores.Disposing) return;
            if (updatingMatches) { QueueLocalMatches(); return; }
            updatingMatches = true;
            localScores.BeginUpdate();
            try
            {
                List<SongEntry> matches = FindLocalMatches(library, ChosenTitle());
                string artist = SelectedMusic == null ? (preferMelodyLookup ? melodyLookupArtist : "") : SelectedMusic.Artist;
                bool live = HasLiveEdition(ChosenTitle()) || SelectedMusic != null && HasLiveEdition(SelectedMusic.Album);
                bool edition = live || HasRecordingEdition(ChosenTitle());
                localScores.Items.Clear();
                localScoreSources.Clear();
                localScores.Items.AddRange(matches.Cast<object>().ToArray());
                int recommended = -1;
                if (importChinese != null && !searchingMusic)
                {
                    try
                    {
                        List<ChineseScore> all = ChineseScoreCatalog.FindExact(ChosenTitle(), "");
                        List<ChineseScore> exact = String.IsNullOrWhiteSpace(artist) ? new List<ChineseScore>() : ChineseScoreCatalog.FindExact(ChosenTitle(), artist);
                        var ids = new HashSet<string>(exact.Select(s => s.Id), StringComparer.Ordinal);
                        foreach (ChineseScore candidate in all.OrderBy(s => ids.Contains(s.Id) ? 0 : 1))
                        {
                            var choice = new RemoteScoreChoice { Score = candidate, OtherArtist = !String.IsNullOrWhiteSpace(artist) && !ids.Contains(candidate.Id),
                                ArtistUnconfirmed = String.IsNullOrWhiteSpace(artist), DifferentEdition = edition, LiveEdition = live };
                            SongEntry saved = matches.FirstOrDefault(entry => !String.IsNullOrEmpty(entry.FileName) && SameOfficialSource(entry, candidate));
                            if (saved != null)
                            {
                                localScoreSources[saved] = choice;
                                if (recommended < 0 && choice.Recommended) recommended = matches.IndexOf(saved);
                                continue;
                            }
                            int index = localScores.Items.Count;
                            localScores.Items.Add(choice);
                            if (choice.Recommended && recommended < 0) recommended = index;
                        }
                    }
                    catch (Exception ex) { musicStatus.Text = "中文曲库索引读取失败：" + ex.Message; musicStatus.ForeColor = Theme.Warm; }
                }
                localScores.SelectedIndex = recommended;
                UpdateMusicActions();
                if (recommended >= 0) localHint.Text = "已有同歌名／歌手的主旋律钢琴编配；双击歌曲或点“选用主旋律 MIDI”。";
                else if (live && localScores.Items.Count > 0) localHint.Text = "找到钢琴编配，非所选Live原谱；请在下方明确选用，原录音版本保留。";
                else if (localScores.Items.Count > 0) localHint.Text = "找到待确认的本地谱／其他编配；歌手或版本未核实，请先选择，不自动替换。";
                else localHint.Text = "尚无对应乐谱；双击歌曲或点“下载录音 → MIDI”进行转录。";
            }
            finally { localScores.EndUpdate(); updatingMatches = false; }
        }
        internal static List<SongEntry> FindLocalMatches(IEnumerable<SongEntry> source, string title)
        {
            string key = Normalize(title);
            if (key.Length < 2) return new List<SongEntry>();
            return source.Where(s => Normalize(s.Title) == key).OrderBy(s => s.Builtin ? 1 : 0).Take(50).ToList();
        }
        private static string Normalize(string text)
        {
            text = StripOwnedEdition(text);
            var suffix = Regex.Match(text, @"^(.*?)[（(]([^（）()]+)[）)]$");
            if (suffix.Success && Regex.IsMatch(suffix.Groups[2].Value.Trim(), @"^(?:口琴版|童谣|儿童歌曲)$", RegexOptions.IgnoreCase))
                text = suffix.Groups[1].Value;
            return ChineseScoreCatalog.NormalizeTitle(text);
        }
        private sealed class RemoteScoreChoice
        {
            public ChineseScore Score;
            public bool OtherArtist, ArtistUnconfirmed, DifferentEdition, LiveEdition;
            public bool Recommended { get { return !OtherArtist && !ArtistUnconfirmed && !DifferentEdition; } }
            public override string ToString()
            {
                return "POP909钢琴编配" +
                    (LiveEdition ? "，非所选Live原谱" : DifferentEdition ? "，非所选录音原版" : " · MELODY主旋律") +
                    (OtherArtist ? " · 不同歌手版本" : ArtistUnconfirmed ? " · 歌手待确认" : "") + " · " + Score.Artist + " · " + Score.Title;
            }
        }
        private static string StripOwnedEdition(string title)
        {
            string text = (title ?? "").Trim();
            for (int i = 0; i < 4; i++)
            {
                string previous = text;
                text = Regex.Replace(text, @"\s*·\s*连续旋律版$", "").Trim();
                if (previous == text) break;
            }
            return text;
        }
        private static bool HasLiveEdition(string text)
        { return Regex.IsMatch(text ?? "", @"\blive\b|现场|演唱会", RegexOptions.IgnoreCase); }
        private static bool HasRecordingEdition(string title)
        {
            // Every edition label removed for candidate lookup still needs
            // explicit confirmation before using another arrangement.
            return ChineseScoreCatalog.HasEditionSuffix(StripOwnedEdition(title));
        }
        private static bool SameOfficialSource(SongEntry entry, ChineseScore score)
        {
            return String.Equals(entry.SourceUrl, score.SourceUrl, StringComparison.Ordinal) ||
                String.Equals(entry.SourceUrl, score.MidiUrl, StringComparison.Ordinal);
        }
        private string LocalScoreDescription(SongEntry entry)
        {
            RemoteScoreChoice official;
            if (localScoreSources.TryGetValue(entry, out official)) return official + " · 已在曲库";
            return ((entry.SourceEdition ?? "").Contains("Basic Pitch 自动转录") ? "已有音频转录，需试听" : "本地谱，歌手／版本待确认") + " · " + entry.Title + " · " + entry.Subtitle;
        }
        private void ActivateRecommendedChoice()
        {
            if (!CanUpdateControls || downloading || searchingMusic || inlineWorking) return;
            if (hasInlineResult) DownloadSelectedAudio();
            else if (choose.Enabled) ChooseLocal();
            else if (localScores.Items.Count > 0)
            { localScores.Focus(); musicStatus.Text = "已有编配候选。请先确认下方歌手与版本，再点“采用此编配 MIDI”；录音转录可独立选择。"; }
            else DownloadSelectedAudio();
        }
        private void SetImportBusy(bool value)
        {
            downloading = value;
            if (!CanUpdateControls) return;
            tabs.Enabled = !value;
            UpdateMusicActions();
        }
        private void UpdateMusicActions()
        {
            if (IsDisposed || Disposing || closingDialog || songDownload == null || localScores.IsDisposed || musicResults.IsDisposed) return;
            int selectedIndex = localScores.SelectedIndex;
            choose.Enabled = !downloading && !searchingMusic && selectedIndex >= 0 && selectedIndex < localScores.Items.Count;
            RemoteScoreChoice selected = selectedIndex < 0 || selectedIndex >= localScores.Items.Count ? null : localScores.Items[selectedIndex] as RemoteScoreChoice;
            SongEntry local = selectedIndex < 0 || selectedIndex >= localScores.Items.Count ? null : localScores.Items[selectedIndex] as SongEntry;
            if (selected == null && local != null) localScoreSources.TryGetValue(local, out selected);
            choose.Text = selected != null ? selected.Recommended ? "选用主旋律 MIDI" : "采用此编配 MIDI" : "选用所选 MIDI";
            SetPrimary(choose, choose.Enabled && !hasInlineResult);
            if (!inlineWorking) UpdateAudioIdleAction();
            SetPrimary(songDownload, !choose.Enabled || hasInlineResult);
            officialMusic.Enabled = !downloading && !searchingMusic;
            qqLogin.Enabled = !downloading && !searchingMusic;
            UpdateAccountButton();
        }
        private void UpdateAccountButton()
        {
            if (qqLogin == null || qqLogin.IsDisposed) return;
            QqAccountSession session = QqAccountStore.Current;
            qqLogin.Text = session == null ? "QQ / 微信登录" : session.IsUsable ? "QQ 音乐账号 / 退出" : "确认 QQ 音乐账号";
        }
        private void OpenQqLogin()
        {
            if (downloading || searchingMusic || inlineWorking || closingDialog || IsDisposed) return;
            using (var login = new QqLoginDialog { TopMost = TopMost }) login.ShowDialog(this);
            if (IsDisposed || Disposing || closingDialog) return;
            UpdateAccountButton();
            QqAccountSession session = QqAccountStore.Current;
            if (session != null && session.IsUsable)
                SetSongStatus(SelectedMusic, "QQ 音乐已确认账号：" + session.Nickname + "。可选择版本后下载；平台没有提供完整普通音频时仍会提示受限。", Theme.Accent);
            else SetSongStatus(SelectedMusic, "当前使用匿名搜索。可在软件内登录 QQ / 微信，再由官方接口确认账号。", Theme.Muted);
        }
        private void UpdateAudioIdleAction()
        {
            songDownload.Enabled = !downloading && !searchingMusic && CanDownloadAudio && (SelectedMusic != null || hasInlineResult || downloadedSong == null && File.Exists(downloadedAudioPath));
            songDownload.Text = hasInlineResult ? "立即选用" :
                Object.ReferenceEquals(downloadedSong, SelectedMusic) && File.Exists(downloadedAudioPath) ? "重试转 MIDI" : "下载录音 → MIDI";
        }
        private static void SetPrimary(SoftButton button, bool primary)
        {
            button.Primary = primary; button.BackColor = primary ? Theme.Accent : Theme.Input;
            button.ForeColor = primary ? Theme.Background : Theme.Text;
        }
        private void OpenMusicSource()
        {
            OnlineSong song = SelectedMusic;
            OpenUrl(song == null ? MusicAudioSources.OfficialSearchUrl(CurrentMusicProvider, ChosenTitle()) : MusicAudioSources.OfficialSongUrl(song));
        }
        private async void ChooseLocal()
        {
            if (!CanUpdateControls || downloading || searchingMusic) return;
            EnsureUiContext();
            int index = localScores.SelectedIndex;
            if (index < 0 || index >= localScores.Items.Count) return;
            object choice = localScores.Items[index];
            SongEntry score = choice as SongEntry;
            RemoteScoreChoice remote = choice as RemoteScoreChoice;
            if (score == null && remote == null) return;
            if (score != null && convertScore == null) { ChosenScore = score; DialogResult = DialogResult.OK; return; }
            SetImportBusy(true); musicStatus.ForeColor = Theme.Muted;
            musicStatus.Text = remote == null ? "正在将已选乐谱转换成 MIDI…" : "正在下载「" + remote.Score.Title + "」的 MIDI 编配…";
            try
            {
                SongEntry saved;
                if (remote != null)
                {
                    byte[] bytes = await Task.Run(delegate { return ValidateMelodyDownload(chineseDownload(remote.Score)); });
                    if (!CanUpdateControls) return;
                    saved = importChinese(remote.Score, bytes);
                }
                else
                {
                    await Task.Yield();
                    if (!CanUpdateControls) return;
                    saved = convertScore(score);
                }
                if (!CanUpdateControls) return;
                SetImportBusy(false);
                if (saved != null) { ChosenScore = saved; DialogResult = DialogResult.OK; }
            }
            catch (Exception ex)
            {
                if (CanUpdateControls)
                {
                    musicStatus.Text = (ex is InvalidDataException && ex.Message.Contains("MELODY") ?
                        "取得的 MIDI 没有可用 MELODY 主旋律。" : "主旋律 MIDI 获取未完成。") +
                        "可重试所选编配；若要改用录音，请明确点“下载录音 → MIDI”。";
                    musicStatus.ForeColor = Theme.Warm;
                }
            }
            finally { SetImportBusy(false); }
        }
        internal static byte[] ValidateMelodyDownload(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 14 || bytes.Length > ChineseScoreCatalog.MaximumMidiBytes)
                throw new InvalidDataException("主旋律 MIDI 内容无效。");
            string path = Path.Combine(Path.GetTempPath(), "rhythm-melody-" + Guid.NewGuid().ToString("N") + ".mid");
            try
            {
                File.WriteAllBytes(path, bytes);
                SongScore score = ScoreIO.LoadMidi(path);
                if (!score.Tracks.Any(track => IsMelodyTrackName(track.Name) && track.Notes.Count > 0))
                    throw new InvalidDataException("此编配没有可用的 MELODY 主旋律轨。");
                return bytes;
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        internal static bool IsMelodyTrackName(string name)
        {
            // LoadMidi appends this channel suffix to each original MIDI track name.
            return Regex.IsMatch((name ?? "").Trim(), @"^MELODY(?: · 通道 (?:[1-9]|1[0-6]))?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        private void AddSongScore(bool notation)
        {
            if (!CanUpdateControls || downloading || searchingMusic) return;
            string title = ChosenTitle();
            if (title.Length == 0) { musicStatus.Text = "请先搜索或输入歌曲名称。"; musicQuery.Focus(); return; }
            OnlineSong song = SelectedMusic;
            try
            {
                SongEntry entry = notation ? createJianpu(title, song == null ? "" : song.Artist) : importMidi(title, song == null ? "" : song.Artist);
                if (!CanUpdateControls) return;
                if (entry != null && notation && convertScore != null) entry = convertScore(entry);
                if (!CanUpdateControls) return;
                if (entry != null) { ChosenScore = entry; DialogResult = DialogResult.OK; }
            }
            catch (Exception ex) { if (CanUpdateControls) { musicStatus.Text = "乐谱转换未完成：" + ex.Message; musicStatus.ForeColor = Theme.Warm; } }
        }
        private void AddAudioScore()
        {
            if (!CanUpdateControls || downloading || searchingMusic) return;
            if (ImportTranscribedAudio != null)
            {
                string selected = null;
                if (SelectAudioFile != null) selected = SelectAudioFile();
                else
                    using (var picker = new OpenFileDialog { Title = "选择要转成 MIDI 的歌曲音频", Filter = "普通音频|*.mp3;*.wav;*.flac;*.ogg|所有文件|*.*" })
                        if (picker.ShowDialog(this) == DialogResult.OK) selected = picker.FileName;
                if (!String.IsNullOrWhiteSpace(selected)) StartInlineConversion(SelectedMusic, selected);
                return;
            }
            if (TranscribeAudio == null) return;
            OnlineSong song = SelectedMusic;
            SetImportBusy(true);
            try
            {
                SongEntry entry = TranscribeAudio(ChosenTitle(), song == null ? "" : song.Artist);
                if (!CanUpdateControls) return;
                SetImportBusy(false);
                if (entry != null) { ChosenScore = entry; DialogResult = DialogResult.OK; }
            }
            catch (Exception ex) { if (CanUpdateControls) { musicStatus.Text = "音频转录未完成：" + ex.Message; musicStatus.ForeColor = Theme.Warm; } }
            finally { SetImportBusy(false); }
        }
        private void DownloadSelectedAudio()
        {
            if (!CanUpdateControls) return;
            if (inlineWorking)
            {
                songDownload.Enabled = false; songDownload.Text = "正在取消…";
                CancelActiveSong();
                return;
            }
            if (downloading || searchingMusic || !CanDownloadAudio) return;
            if (hasInlineResult && ChosenScore != null) { DialogResult = DialogResult.OK; return; }
            OnlineSong song = SelectedMusic;
            if (song == null)
            {
                if (ImportTranscribedAudio != null && downloadedSong == null && File.Exists(downloadedAudioPath))
                { StartInlineConversion(null, downloadedAudioPath); return; }
                musicStatus.Text = "请先搜索并选择歌曲版本。";
                return;
            }
            if (ImportTranscribedAudio != null)
            {
                StartInlineConversion(song, downloadedSong != null && Object.ReferenceEquals(song, downloadedSong) && File.Exists(downloadedAudioPath) ? downloadedAudioPath : null);
                return;
            }
            SetImportBusy(true);
            try
            {
                SongEntry entry = DownloadAudio(song);
                if (!CanUpdateControls) return;
                SetImportBusy(false);
                if (entry != null) { ChosenScore = entry; DialogResult = DialogResult.OK; }
            }
            catch (Exception ex) { if (CanUpdateControls) { musicStatus.Text = "歌曲转录未完成：" + ex.Message; musicStatus.ForeColor = Theme.Warm; } }
            finally { SetImportBusy(false); }
        }
        private async void StartInlineConversion(OnlineSong song, string existingAudio)
        { await RunInlineConversionAsync(song, existingAudio); }
        internal async Task RunInlineConversionAsync(OnlineSong song, string existingAudio)
        {
            if (!CanUpdateControls || downloading || searchingMusic || ImportTranscribedAudio == null) return;
            EnsureUiContext();
            inlineTitle = song == null ? ChosenTitle() : song.Title;
            if (String.IsNullOrWhiteSpace(inlineTitle)) inlineTitle = String.IsNullOrWhiteSpace(existingAudio) ? "我的歌曲" : Path.GetFileNameWithoutExtension(existingAudio);
            string title = inlineTitle;
            int operation = ++songOperation;
            var cancellation = new CancellationTokenSource(); songCancellation = cancellation;
            SetInlineWorking(true); completedSong = null; hasInlineResult = false; ChosenScore = null; ConversionSong = song;
            string stage = String.IsNullOrWhiteSpace(existingAudio) ? "下载" : "转录";
            string qualityWarning = "";
            Action<string> progress = delegate(string value)
            {
                if (value != null && value.StartsWith("转录提示：", StringComparison.Ordinal))
                    qualityWarning += " " + value.Substring("转录提示：".Length);
                UpdateInlineProgress(song, value, operation);
            };
            try
            {
                string input = existingAudio;
                if (String.IsNullOrWhiteSpace(input))
                {
                    SetSongStatus(song, "正在确认所选版本是否提供可下载的普通音频…", Theme.Muted);
                    string url = await AwaitCancellation(Task.Run(delegate { return ResolveSongAudio(song); }), cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!IsCurrentSongOperation(operation)) return;
                    if (String.IsNullOrWhiteSpace(url))
                    {
                        string provider = MusicAudioSources.DisplayName(song);
                        SetSongStatus(song, provider + "未提供此版本的直接下载。" +
                            (provider == MusicAudioSources.QqProvider ?
                                (QqAccountStore.Current != null && QqAccountStore.Current.IsUsable ? "当前账号仍未获得此版本的普通完整音频权限。" : "可点右上角“QQ / 微信登录”；外部网页登录不会同步到软件。") : "") +
                            "可换版本，或在官方客户端下载普通音频后点“已有音频转 MIDI”。", Theme.Warm);
                        return;
                    }
                    SetSongStatus(song, "正在下载歌曲音频，完成后会在本页自动转 MIDI…", Theme.Muted);
                    input = await DownloadSongAudio(url, title, progress, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!IsCurrentSongOperation(operation)) return;
                    if (song != null && MusicAudioSources.DisplayName(song) == MusicAudioSources.QqProvider && !song.FreeDownload)
                    {
                        SetSongStatus(song, "正在核对下载音频的完整时长，避免将试听片段作为整曲转换…", Theme.Muted);
                        await Task.Run(delegate { QqCompleteAudio.Validate(input, song.DurationSeconds, cancellation.Token); });
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (!IsCurrentSongOperation(operation)) return;
                    }
                }
                ValidateLocalAudio(input);
                downloadedAudioPath = input; downloadedSong = song; stage = "转录";
                SetSongStatus(song, "音频已保存。正在准备识别组件并转换 MIDI；首次准备可能需要数分钟…", Theme.Muted);
                byte[] bytes = await ConvertLocalAudio(input, title, progress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!IsCurrentSongOperation(operation)) return;
                stage = "保存";
                SongEntry entry = ImportTranscribedAudio(title, input, bytes);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!IsCurrentSongOperation(operation)) return;
                if (entry == null) throw new InvalidOperationException("曲库未返回保存结果。");
                ChosenScore = entry; completedSong = song; hasInlineResult = true;
                if (!library.IsReadOnly && !library.Contains(entry)) library.Add(entry);
                SetSongStatus(song, "已加入曲库，音频与 MIDI 已配对保存。点击“立即选用”返回主界面；请先试听。" + qualityWarning,
                    qualityWarning.Length == 0 ? Theme.Accent : Theme.Warm);
            }
            catch (OperationCanceledException)
            {
                if (IsCurrentSongOperation(operation)) SetSongStatus(song,
                    !String.IsNullOrWhiteSpace(downloadedAudioPath) && Object.ReferenceEquals(downloadedSong, song) && File.Exists(downloadedAudioPath) ? "已取消；已下载的音频保留。点击“重试转 MIDI”继续。" : "已取消下载，歌曲版本保留，可以重新尝试。", Theme.Muted);
            }
            catch (Exception error)
            {
                if (IsCurrentSongOperation(operation)) SetSongStatus(song, FriendlyWorkflowError(error, stage), Theme.Warm);
            }
            finally
            {
                if (Object.ReferenceEquals(songCancellation, cancellation)) songCancellation = null;
                cancellation.Dispose();
                if (!IsDisposed && !Disposing && !closingDialog && operation == songOperation)
                { SetInlineWorking(false); UpdateLocalMatches(); }
                else if (closingDialog || IsDisposed || Disposing) inlineWorking = downloading = false;
            }
        }
        private static async Task<byte[]> ConvertWithRuntime(string input, string title, Action<string> progress, CancellationToken token)
        {
            try { await AudioRuntimeInstaller.EnsureInstalledAsync(progress, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                error.Data["AudioWorkflowStage"] = "安装";
                throw;
            }
            return await AudioMidiConverter.ConvertAsync(input, title, progress, token);
        }
        private static void EnsureUiContext()
        {
            if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }
        private static async Task<T> AwaitCancellation<T>(Task<T> task, CancellationToken token)
        {
            var cancelled = new TaskCompletionSource<bool>();
            using (token.Register(delegate { cancelled.TrySetResult(true); }))
            {
                if (await Task.WhenAny(task, cancelled.Task) != task)
                {
                    Task observation = task.ContinueWith(delegate(Task<T> failed) { var ignored = failed.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    token.ThrowIfCancellationRequested();
                }
                token.ThrowIfCancellationRequested();
                return await task;
            }
        }
        private void SetInlineWorking(bool value)
        {
            if (value)
            {
                inlineWorking = downloading = true;
                qqLogin.Enabled = false;
                inlineControlStates.Clear();
                foreach (Control control in musicTab.Controls)
                    if (control != songDownload && !(control is Label) && control != songProgress)
                    { inlineControlStates[control] = control.Enabled; control.Enabled = false; }
                songDownload.Text = "取消下载 / 转录"; songDownload.Enabled = true;
            }
            else
            {
                foreach (var pair in inlineControlStates) if (!pair.Key.IsDisposed) pair.Key.Enabled = pair.Value;
                inlineControlStates.Clear();
                downloading = false;
                UpdateAudioIdleAction();
            }
            songProgress.Visible = value;
            if (!value) { inlineWorking = false; UpdateMusicActions(); }
        }
        private bool IsCurrentSongOperation(int operation)
        { return operation == songOperation && !closingDialog && !IsDisposed && !Disposing; }
        private void UpdateInlineProgress(OnlineSong song, string value, int operation)
        {
            if (!IsCurrentSongOperation(operation) || !IsHandleCreated) return;
            try { BeginInvoke(new Action(delegate { if (IsCurrentSongOperation(operation) && inlineWorking && songCancellation != null && !songCancellation.IsCancellationRequested) SetSongStatus(song, value, Theme.Muted); })); }
            catch (InvalidOperationException) { }
        }
        private void SetSongStatus(OnlineSong song, string message, Color color)
        {
            if (!CanUpdateControls) return;
            string caption = song == null ? (inlineTitle ?? "") : song.Title + " · " + song.Artist;
            if (caption.Length > 50) caption = caption.Substring(0, 49) + "…";
            musicStatus.Text = (caption.Length == 0 ? "" : caption + "\n") + message;
            musicStatus.ForeColor = color;
            if (songProgress != null && musicResults != null && localHint != null)
            {
                Size text = TextRenderer.MeasureText(musicStatus.Text, musicStatus.Font, new Size(musicStatus.Width, Int32.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                musicStatus.Height = Math.Min(132, Math.Max(52, text.Height + 3));
                songProgress.Top = musicStatus.Bottom + 4;
                musicResults.Top = songProgress.Bottom + 7;
                musicResults.Height = Math.Max(60, localHint.Top - musicResults.Top - 7);
            }
        }
        private static void ValidateLocalAudio(string filename)
        {
            if (String.IsNullOrWhiteSpace(filename) || !File.Exists(filename)) throw new FileNotFoundException("音频文件不存在。");
            if (!SongAudioDownload.IsSupported(filename)) throw new InvalidDataException("请选择普通 MP3、WAV、FLAC 或 OGG；QMC、MGG、KGM 等受保护文件不能直接转录。");
            var info = new FileInfo(filename);
            if (info.Length > SongAudioDownload.MaximumDownloadBytes) throw new InvalidDataException("音频超过 128 MB。");
            using (var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] header = new byte[32]; int read = stream.Read(header, 0, header.Length); Array.Resize(ref header, read);
                if (!SongAudioDownload.ValidateAudioHeader(header)) throw new InvalidDataException("文件不是普通音频。");
            }
        }
        internal static string FriendlyWorkflowError(Exception error, string stage)
        {
            string text = error == null ? "" : error.Message;
            if (error != null && error.Data.Contains("QqIncompleteAudio")) return text;
            if (error != null && Convert.ToString(error.Data["AudioWorkflowStage"]) == "安装")
                return FriendlyRuntimePreparationError(error);
            if (stage == "下载")
            {
                if (error is ArgumentException) return "平台没有返回可用的音频下载地址。可换其他版本，或在官方客户端下载后导入普通音频。";
                if (text.Contains("128 MB")) return "音频超过 128 MB，下载已停止。请换较低音质或导入截取后的普通音频。";
                if (error is TimeoutException || error is WebException) return "连接歌曲来源超时或网络不可用。请稍后重试，也可使用官方客户端取得普通音频。";
                return "此版本的音频下载未完成。请重试或换其他版本；仍无法下载时，可从官方客户端取得普通音频后导入。";
            }
            if (stage == "保存") return "MIDI 已转换，但加入曲库未成功。请确认程序所在文件夹可写，然后重试；原音频仍保留。";
            if (text.Contains("128 MB")) return "音频超过 128 MB，请先截取要演奏的片段再导入。";
            if (text.Contains("受保护") || text.Contains("普通音频")) return "文件无法作为普通音频读取。支持 MP3、WAV、FLAC、OGG；QMC、MGG、KGM 等受保护文件不能直接转录。";
            if (error is FileNotFoundException) return "音频文件不存在或已被移动。请重新选择音频文件。";
            if (error is UnauthorizedAccessException || error is IOException) return "音频暂时无法读取。请等待下载完成，并确认文件夹可访问后重试。";
            if (error is TimeoutException) return "转录时间过长，音频已保留。请截取较短的片段再导入。";
            Match exit = Regex.Match(text, @"(?:退出码|错误代码|exit code)[：: ]*([+-]?\d+)", RegexOptions.IgnoreCase);
            if (text.Contains("组件") || text.Contains("安装") || exit.Success)
                return "识别组件未准备成功" + (exit.Success ? "（退出码 " + exit.Groups[1].Value + "）" : "") + "。已下载音频保留，点击“重试转 MIDI”可重新准备。";
            return "转录未生成可用的 MIDI，音频已保留。可重试或导入更清晰的人声、单乐器音频。";
        }
        private static string FriendlyRuntimePreparationError(Exception error)
        {
            string kind = Convert.ToString(error.Data["AudioRuntimeFailureKind"]);
            string reason;
            if (error is FileNotFoundException) reason = "识别组件安装文件缺失，请重新复制完整软件文件夹";
            else if (error is TimeoutException) reason = "识别组件准备超时，请检查网络";
            else if (kind == "Policy") reason = "识别组件受系统脚本策略限制";
            else if (kind == "Network") reason = "识别组件下载失败，请检查网络";
            else if (kind == "Permission" || error is UnauthorizedAccessException) reason = "识别组件目录没有写入权限，请将整个软件移到可写文件夹";
            else if (kind == "Disk") reason = "磁盘空间不足，识别组件未安装完成，请先清理空间";
            else if (kind == "Runtime") reason = "识别组件或模型初始化失败";
            else reason = "识别组件准备失败";
            int code;
            string exit = Convert.ToString(error.Data["InstallerExitCode"], System.Globalization.CultureInfo.InvariantCulture);
            if (!Int32.TryParse(exit, out code))
            {
                Match match = Regex.Match(error.Message ?? "", @"(?:退出码|错误代码|exit code)[：: ]*([+-]?\d+)", RegexOptions.IgnoreCase);
                exit = match.Success ? match.Groups[1].Value : "";
            }
            else exit = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!String.IsNullOrEmpty(exit)) reason += "（退出码 " + exit + "）";
            reason += "。音频已保留，可重试准备。";
            if (!String.IsNullOrWhiteSpace(Convert.ToString(error.Data["AudioRuntimeLogPath"])))
                reason += "具体原因已记录在程序文件/data/logs/audio-runtime.log。";
            return reason;
        }
        internal static string FreeKeyword(string value)
        {
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "卡农", "Pachelbel" }, { "致爱丽丝", "Elise" }, { "献给爱丽丝", "Elise" }, { "欢乐颂", "Ode to Joy" },
                { "小星星", "Twinkle" }, { "绿袖子", "Greensleeves" }, { "小步舞曲", "Minuet" },
                { "铃儿响叮当", "Jingle Bells" }, { "友谊地久天长", "Auld" }, { "平安夜", "Silent" },
                { "贝多芬", "Beethoven" }, { "巴赫", "Bach" }, { "莫扎特", "Mozart" },
                { "肖邦", "Chopin" }, { "舒伯特", "Schubert" }, { "舒曼", "Schumann" },
                { "维瓦尔第", "Vivaldi" }, { "德彪西", "Debussy" }, { "钢琴", "Piano" },
                { "小提琴", "Violin" }, { "长笛", "Flute" }, { "吉他", "Guitar" }
            };
            string translated; return aliases.TryGetValue(value, out translated) ? translated : value;
        }
        private void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, "无法打开浏览器：" + ex.Message, "打开网页", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
