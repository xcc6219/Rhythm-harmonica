using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HarmonicaStudio
{
    internal sealed class ChineseCatalogPanel : UserControl
    {
        private readonly Func<ChineseScore, byte[], SongEntry> import;
        private readonly Action<SongEntry> chosen;
        private readonly Action<bool> busy;
        private readonly Func<ChineseScore, byte[]> midiDownload;
        private readonly TextBox query;
        private readonly ListView results;
        private readonly Label status;
        private readonly SoftButton go, previous, next, download;
        private List<ChineseScore> matches = new List<ChineseScore>();
        private int page;
        private bool loading;
        private const int PageSize = 40;

        public ChineseCatalogPanel(Func<ChineseScore, byte[], SongEntry> importer, Action<SongEntry> onChosen, Action<bool> onBusy)
            : this(importer, onChosen, onBusy, ChineseScoreCatalog.Download) { }
        internal ChineseCatalogPanel(Func<ChineseScore, byte[], SongEntry> importer, Action<SongEntry> onChosen, Action<bool> onBusy, Func<ChineseScore, byte[]> downloader)
        {
            import = importer; chosen = onChosen; busy = onBusy; midiDownload = downloader;
            BackColor = Theme.Panel; ForeColor = Theme.Text; Dock = DockStyle.Fill;
            query = new TextBox { Location = new Point(16, 17), Width = 448, MaxLength = 100, Font = Theme.Font(11), BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
            go = Button("搜索 / 全部", 477, 14, 108, true, Search);
            previous = Button("上一页", 599, 14, 67, false, delegate { ShowPage(page - 1); });
            next = Button("下一页", 676, 14, 67, false, delegate { ShowPage(page + 1); });
            query.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Search(); } };
            status = new Label { Location = new Point(16, 58), Size = new Size(727, 38), Font = Theme.Font(8), ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft };
            results = new ListView { Location = new Point(16, 101), Size = new Size(727, 216), View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.Font(9), BorderStyle = BorderStyle.FixedSingle };
            results.Columns.Add("歌名", 282); results.Columns.Add("数据集演唱 / 编配版本", 242); results.Columns.Add("来源", 178);
            results.SelectedIndexChanged += delegate { if (!IsDisposed && !Disposing) download.Enabled = !loading && results.SelectedItems.Count == 1; };
            results.DoubleClick += delegate { Download(); };
            var hint = new Label { Text = "中文流行歌钢琴编配，可选 MELODY 声轨吹奏；节奏、调性可能与酷狗录音不同。\n保留作品与数据集署名；具体资源以来源页面为准。", Location = new Point(16, 325), Size = new Size(727, 48), ForeColor = Theme.Muted, Font = Theme.Font(8) };
            var source = Button("曲库与许可 ↗", 16, 381, 142, false, OpenSource);
            download = Button("↓ 下载 MIDI 并载入", 557, 381, 186, true, Download);
            Controls.AddRange(new Control[] { query, go, previous, next, status, results, hint, source, download });
        }
        private static SoftButton Button(string text, int x, int y, int width, bool primary, Action action)
        { var button = new SoftButton(text, primary) { Location = new Point(x, y), Width = width }; button.Click += delegate { action(); }; return button; }
        public void Search()
        {
            if (loading || IsDisposed) return;
            try { matches = ChineseScoreCatalog.Search(query.Text.Trim()); ShowPage(0); }
            catch (Exception ex) { status.Text = ex.Message; status.ForeColor = Theme.Warm; results.Items.Clear(); previous.Enabled = next.Enabled = download.Enabled = false; }
        }
        private void ShowPage(int value)
        {
            if (loading || IsDisposed) return;
            page = Math.Max(0, Math.Min(Math.Max(0, (matches.Count - 1) / PageSize), value));
            results.BeginUpdate(); results.Items.Clear();
            foreach (ChineseScore score in matches.Skip(page * PageSize).Take(PageSize))
            {
                var item = new ListViewItem(new string[] { score.Title, score.Artist, "POP909 · MIDI" }); item.Tag = score; results.Items.Add(item);
            }
            results.EndUpdate(); if (results.Items.Count > 0) results.Items[0].Selected = true;
            status.Text = matches.Count == 0 ? "没有匹配的 MIDI，请换歌名、歌手或导入自己取得的谱。" : "找到 " + matches.Count + " 条 · 第 " + (page + 1) + " / " + Math.Max(1, (matches.Count + PageSize - 1) / PageSize) + " 页 · 仓库标注 MIT，作品保留原作者权益";
            status.ForeColor = Theme.Muted; previous.Enabled = page > 0; next.Enabled = (page + 1) * PageSize < matches.Count;
            download.Enabled = results.SelectedItems.Count == 1;
        }
        private async void Download()
        {
            if (loading || results.SelectedItems.Count != 1) return;
            ChineseScore score = (ChineseScore)results.SelectedItems[0].Tag;
            loading = true; busy(true); Enabled = false; status.Text = "正在下载并校验「" + score.Title + "」…";
            try
            {
                byte[] bytes = await Task.Run(delegate { return midiDownload(score); });
                if (IsDisposed) return;
                SongEntry saved = import(score, bytes); loading = false; busy(false);
                if (saved != null) chosen(saved);
            }
            catch (Exception ex) { if (!IsDisposed) { status.Text = "下载未完成：" + ex.Message; status.ForeColor = Theme.Warm; } }
            finally { loading = false; busy(false); if (!IsDisposed) Enabled = true; }
        }
        private void OpenSource()
        {
            string url = results.SelectedItems.Count == 1 ? ((ChineseScore)results.SelectedItems[0].Tag).SourceUrl : "https://github.com/music-x-lab/POP909-Dataset";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { status.Text = "无法打开来源：" + ex.Message; status.ForeColor = Theme.Warm; }
        }
    }
}
