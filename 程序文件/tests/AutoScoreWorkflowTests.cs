using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HarmonicaStudio;

public static class AutoScoreWorkflowTests
{
    private static int checks;

    [STAThread]
    public static int Main()
    {
        try { Run(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    public static int Run()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("自动配谱流程验证须在 STA 运行。");
        checks = 0;
        Case("搜歌默认页与两个备用页懒加载", TestDefaultCatalogAndLazyFree);
        Case("中文页指定版本下载", TestChineseDownload);
        Case("在线晴天对应版本", TestExactRemoteSong);
        Case("同名不同歌手必须明确选择", TestOtherArtistNeedsChoice);
        Case("无谱不伪造与明确音频转录", TestMissingScoreAndAudioChoice);
        Case("本地简谱转换并选回MIDI", TestLocalNotationConversion);
        Case("粘贴简谱转换并选回MIDI", TestPastedNotationConversion);
        Case("无谱明确下载原歌曲音频并关闭", TestMissingScoreDownloadsSelectedAudio);
        Case("主旋律谱失败后须明确选择录音转录", TestFailedRemoteRequiresExplicitAudio);
        Case("取消歌曲音频后保留原歌并重试", TestCancelledAudioCanRetry);
        Case("其他歌手谱失败不可偷换当前音频", TestOtherArtistFailureKeepsOriginalVersion);
        Case("完整远程谱优先内置短片段", TestFullRemotePreferredOverBuiltin);
        Case("未知歌手本地MIDI保留供明确选择", TestLocalUserMidiRemainsPreferred);
        Case("多个准确匹配歌曲主旋律优先旧转录", TestMelodyPreferredToAudioEstimate);
        Case("曲库查主旋律不依赖QQ歌曲请求", TestLibraryMelodyLookup);
        Case("未知歌手与Live编配须明确选择", TestUnconfirmedAndLiveMelody);
        Case("同名不同歌手本地谱不可默认替代", TestOtherArtistLocalScore);
        Case("官方来源去重与任意文件名不冒充来源", TestOfficialSourceReuse);
        Case("缺少MELODY的编配拒绝导入且不转录", TestMissingMelodyDoesNotTranscribe);
        Case("无谱双击准确使用独立录音动作", TestNoScoreDoubleClickUsesAudio);
        Case("双平台选歌与独立音频按钮", TestProviderSelectionAndSeparateAudio);
        Case("两平台下载完成自动转录并关闭", TestAudioDownloadConvertsOnCompletion);
        Case("平台受限不虚报成功", TestRestrictedAudioKeepsOfficialFlow);
        Case("本地音频入口不依赖线上歌曲", TestManualAudioWithoutSong);
        Case("双平台窗口布局不重叠", TestSongDialogLayout);
        Case("双平台同页下载转录与明确选用", TestInlineSongConversion);
        Case("同页受限版本不请求空下载地址", TestInlineRestrictedVersion);
        Case("受限QQ音频先核对全曲再转换", TestInlineIncompleteAudio);
        Case("取消解析不启动迟到下载", TestInlineCancelResolve);
        Case("取消下载后保留版本并可重试", TestInlineCancelDownload);
        Case("转录失败保留音频并直接重试", TestInlineRetryConversion);
        Case("完成后换平台清除旧选用状态", TestInlineChangeSourceAfterSuccess);
        Case("关闭同页转录不导入迟到结果", TestInlineCloseDuringConversion);
        Case("释放同页转录取消任务且拒绝迟到导入", TestInlineDisposeDuringConversion);
        Case("音频窗口关闭后拒绝迟到转换结果", TestAudioLateConversionAfterClose);
        Case("音频窗口释放后不转录迟到下载", TestAudioLateDownloadAfterDispose);
        Case("本地音频同页转录与格式说明", TestInlineLocalAudio);
        Case("同页错误不暴露URL和技术参数", TestFriendlyInlineErrors);
        Case("组件安装失败原因与歌曲转录准确区分", TestRuntimePreparationMessages);
        Console.WriteLine("自动配谱流程测试通过，共 " + checks + " 项断言（真实中文索引、模拟网络与导入回调，未发送键鼠输入）。");
        return checks;
    }

    private static void TestDefaultCatalogAndLazyFree()
    {
        var catalog = ChineseScoreCatalog.Search("");
        Assert(catalog.Count == 909, "已交付中文索引须有909条记录。");
        int freeCalls = 0, musicCalls = 0;
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry>(),
            delegate(string term, int page)
            {
                Interlocked.Increment(ref freeCalls);
                return new FreeScorePage { AppliedQuery = term, Items = new List<FreeScore> { new FreeScore { Title = "免费测试谱", License = "测试" } } };
            },
            delegate { Interlocked.Increment(ref musicCalls); return new List<OnlineSong>(); },
            NoConvert, NoChineseImport, NoChineseDownload, NoJianpu))
        {
            TabControl tabs = Field<TabControl>(dialog, "tabs");
            TabPage music = Field<TabPage>(dialog, "musicTab"), chinese = Field<TabPage>(dialog, "chineseTab"), free = Field<TabPage>(dialog, "freeTab");
            Assert(tabs.TabPages.Count == 3 && tabs.TabPages[0] == music && tabs.TabPages[1] == chinese && tabs.TabPages[2] == free,
                "构造完成后已有三个页面且依次为搜歌、中文MIDI、免费谱；实际=" + String.Join(",", tabs.TabPages.Cast<TabPage>().Select(page => page.Name)));
            Assert(tabs.SelectedTab == music && freeCalls == 0 && musicCalls == 0,
                "构造期间保留搜歌默认页，不提前请求任一在线曲库。");
            Show(dialog);
            object panel = Field<object>(dialog, "chinesePanel");
            ListView rows = Field<ListView>(panel, "results");
            Assert(tabs.TabPages.Count == 3 && tabs.TabPages[0] == music && tabs.TabPages[1] == chinese && tabs.TabPages[2] == free,
                "窗口显示后仍按QQ/酷狗搜歌、备用中文MIDI、备用免费谱排序；实际=" + String.Join(",", tabs.TabPages.Cast<TabPage>().Select(page => page.Name)));
            Assert(tabs.SelectedTab == music && rows.Items.Count == 0 && freeCalls == 0 && musicCalls == 0,
                "默认打开搜歌页，两个备用页均等用户切入后加载。");
            tabs.SelectedTab = chinese; Pump();
            Assert(rows.Items.Count == 40 && rows.Items[0].Text == catalog[0].Title, "默认第一页显示前40首。");
            Assert(Field<Label>(panel, "status").Text.Contains("909") && Field<Label>(panel, "status").Text.Contains("1 / 23"), "总数和页码准确。");
            Assert(!Field<Button>(panel, "previous").Enabled && Field<Button>(panel, "next").Enabled, "第一页翻页状态准确。");
            Assert(freeCalls == 0 && musicCalls == 0, "切入中文索引不提前请求其他曲库。");
            Field<Button>(panel, "next").PerformClick(); Pump();
            Assert(rows.Items[0].Text == catalog[40].Title && Field<Button>(panel, "previous").Enabled, "下一页按40条推进。");
            for (int i = 0; i < 21; i++) { Field<Button>(panel, "next").PerformClick(); Pump(); }
            Assert(rows.Items.Count == 29 && Field<Label>(panel, "status").Text.Contains("23 / 23"), "最后一页显示剩余29首。");
            Assert(((ChineseScore)rows.Items[28].Tag).Id == catalog[908].Id && !Field<Button>(panel, "next").Enabled, "最后一条不丢失且末页禁用下一页。");
            Assert(dialog.ChosenScore == null, "浏览分页不会擅自导入。");
            tabs.SelectedTab = free;
            Wait(delegate { return freeCalls == 1 && Field<Button>(dialog, "freeGo").Enabled && Field<ListView>(dialog, "freeResults").Items.Count == 1; }, "免费页第一次检索未完成。");
            tabs.SelectedTab = chinese; Pump(); tabs.SelectedTab = free; Pump();
            Assert(freeCalls == 1 && musicCalls == 0, "免费页首次切入加载，再次切换不重复请求。");
            Assert(rows.Items.Count == 29 && Field<Label>(panel, "status").Text.Contains("23 / 23"),
                "备用页切换保留中文MIDI当前分页，不重新加载第一页。");
        }
    }

    private static void TestChineseDownload()
    {
        ChineseScore downloaded = null, imported = null;
        byte[] received = null, payload = MidiWriter.ToBytes(new[] { new NoteEvent { Pitch = 60, Start = 0, Duration = 1, Velocity = 90 } }, "回调测试");
        SongEntry saved = MidiEntry("中文导入结果");
        int calls = 0;
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry>(), EmptyFree, EmptyMusic, NoConvert,
            delegate(ChineseScore score, byte[] bytes) { imported = score; received = bytes; calls++; return saved; },
            delegate(ChineseScore score) { downloaded = score; return payload; }, NoJianpu))
        {
            Show(dialog);
            Field<TabControl>(dialog, "tabs").SelectedTab = Field<TabPage>(dialog, "chineseTab"); Pump();
            object panel = Field<object>(dialog, "chinesePanel");
            ListView rows = Field<ListView>(panel, "results");
            Select(rows, 1); Pump();
            ChineseScore chosen = (ChineseScore)rows.Items[1].Tag;
            Assert(calls == 0 && dialog.ChosenScore == null, "选择中文条目不会立即下载。");
            Field<Button>(panel, "download").PerformClick();
            Wait(delegate { return dialog.DialogResult == DialogResult.OK; }, "中文条目下载回调未完成。");
            Assert(calls == 1 && Object.ReferenceEquals(downloaded, chosen) && Object.ReferenceEquals(imported, chosen), "只下载并导入用户明确选中的版本。");
            Assert(Object.ReferenceEquals(received, payload) && Object.ReferenceEquals(dialog.ChosenScore, saved), "原始MIDI数据及导入结果完整返回。");
        }
    }

    private static void TestExactRemoteSong()
    {
        int downloadCalls = 0, importCalls = 0;
        ChineseScore downloaded = null, imported = null;
        byte[] payload = MelodyPayload();
        byte[] received = null;
        SongEntry saved = MidiEntry("晴天");
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry>(), EmptyFree, SongSearch("晴天", "周杰伦"), NoConvert,
            delegate(ChineseScore score, byte[] bytes) { importCalls++; imported = score; received = bytes; return saved; },
            delegate(ChineseScore score) { Interlocked.Increment(ref downloadCalls); downloaded = score; return payload; }, NoJianpu))
        {
            Show(dialog); SearchMusic(dialog, "晴天");
            ComboBox choices = Field<ComboBox>(dialog, "localScores");
            Wait(delegate { return choices.Items.Count > 0 && choices.SelectedIndex >= 0; }, "晴天对应编配未出现。");
            Assert(choices.SelectedItem.ToString().Contains("周杰伦") && !choices.SelectedItem.ToString().Contains("不同歌手"), "默认所选晴天编配须对应周杰伦。");
            Assert(downloadCalls == 0 && importCalls == 0 && dialog.ChosenScore == null, "搜索后仍等待用户确认下载。");
            Field<Button>(dialog, "choose").PerformClick();
            Wait(delegate { return dialog.DialogResult == DialogResult.OK; }, "晴天自动配谱回调未完成。");
            Assert(downloadCalls == 1 && importCalls == 1 && Object.ReferenceEquals(downloaded, imported), "确认后只下载同一份匹配谱一次。");
            Assert(imported.Title == "晴天" && imported.Artist == "周杰伦", "导入回调的曲名与歌手都精确对应。");
            Assert(Object.ReferenceEquals(received, payload) && Object.ReferenceEquals(dialog.ChosenScore, saved) && !String.IsNullOrEmpty(saved.FileName), "选择返回MIDI导入结果。");
        }
    }

    private static void TestOtherArtistNeedsChoice()
    {
        int downloadCalls = 0, importCalls = 0;
        ChineseScore downloaded = null;
        SongEntry saved = MidiEntry("七里香 · 刘瑞琦");
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry>(), EmptyFree, SongSearch("七里香", "周杰伦"), NoConvert,
            delegate { importCalls++; return saved; },
            delegate(ChineseScore score) { Interlocked.Increment(ref downloadCalls); downloaded = score; return MelodyPayload(); }, NoJianpu))
        {
            Show(dialog); SearchMusic(dialog, "七里香");
            ComboBox choices = Field<ComboBox>(dialog, "localScores");
            Wait(delegate { return choices.Items.Count > 0; }, "七里香其他歌手编配未出现。");
            Assert(choices.Items[0].ToString().Contains("刘瑞琦") && choices.Items[0].ToString().Contains("不同歌手版本"), "同名不同歌手要显示实际版本。");
            Assert(choices.SelectedIndex == -1 && !Field<Button>(dialog, "choose").Enabled, "不同歌手不得默认选中或直接确认。");
            Field<Button>(dialog, "choose").PerformClick(); Pump();
            Assert(downloadCalls == 0 && importCalls == 0 && dialog.ChosenScore == null, "没有明确选择时不下载或伪造匹配谱。");
            choices.SelectedIndex = 0;
            Field<Button>(dialog, "choose").PerformClick();
            Wait(delegate { return dialog.DialogResult == DialogResult.OK; }, "手动选择其他歌手版本后未导入。");
            Assert(downloadCalls == 1 && importCalls == 1 && downloaded.Artist == "刘瑞琦", "手动选择后须下载标注的真实刘瑞琦版本。");
        }
    }

    private static void TestMissingScoreAndAudioChoice()
    {
        const string title = "未收录的独立流程验证歌曲987654";
        int audioCalls = 0;
        string audioTitle = null, audioArtist = null;
        SongEntry saved = MidiEntry(title);
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry>(), EmptyFree, SongSearch(title, "测试歌手"), NoConvert, NoChineseImport, NoChineseDownload, NoJianpu))
        {
            dialog.TranscribeAudio = delegate(string name, string artist) { audioCalls++; audioTitle = name; audioArtist = artist; return saved; };
            Show(dialog); SearchMusic(dialog, title);
            Wait(delegate { return Field<Label>(dialog, "localHint").Text.Contains("尚无对应乐谱"); }, "未收录曲未提示缺谱。");
            Assert(Field<ComboBox>(dialog, "localScores").Items.Count == 0 && !Field<Button>(dialog, "choose").Enabled, "没有谱时不借用别的旋律。");
            Field<Button>(dialog, "choose").PerformClick(); Pump();
            Assert(dialog.ChosenScore == null && audioCalls == 0, "搜索选歌和尝试确认不会偷偷执行音频转录。");
            FindButton(Field<TabPage>(dialog, "musicTab"), "已有音频转 MIDI").PerformClick(); Pump();
            Assert(audioCalls == 1 && audioTitle == title && audioArtist == "测试歌手", "音频转录仅在明确点击后收到所选曲名与歌手。");
            Assert(Object.ReferenceEquals(dialog.ChosenScore, saved) && dialog.DialogResult == DialogResult.OK, "音频转录成功后选回其MIDI结果。");
        }
    }

    private static void TestLocalNotationConversion()
    {
        SongEntry source = new SongEntry { Title = "本地简谱流程验证987654", Jianpu = "0:2 1 2:0.5 3:1.5", Bpm = 90 };
        SongEntry saved = MidiEntry(source.Title);
        SongEntry received = null;
        int conversions = 0;
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry> { source }, EmptyFree, SongSearch(source.Title, "本地歌手"),
            delegate(SongEntry entry) { conversions++; received = entry; return saved; }, NoChineseImport, NoChineseDownload, NoJianpu))
        {
            Show(dialog); SearchMusic(dialog, source.Title);
            ComboBox choices = Field<ComboBox>(dialog, "localScores");
            Wait(delegate { return choices.Items.Count == 1; }, "本地简谱候选未出现。");
            Assert(choices.SelectedIndex == -1, "未记录歌手的同名本地谱不自动假称对应版本。");
            choices.SelectedItem = source;
            Assert(conversions == 0 && dialog.ChosenScore == null, "本地简谱仍需明确确认转换。");
            Field<Button>(dialog, "choose").PerformClick();
            Wait(delegate { return dialog.DialogResult == DialogResult.OK; }, "本地简谱转换回调未完成。");
            Assert(conversions == 1 && Object.ReferenceEquals(received, source), "将真正选中的简谱传给转换回调一次。");
            Assert(Object.ReferenceEquals(dialog.ChosenScore, saved) && !String.IsNullOrEmpty(dialog.ChosenScore.FileName), "选择返回新MIDI条目，不返回原简谱。");
        }
    }

    private static void TestPastedNotationConversion()
    {
        const string title = "粘贴简谱流程验证987654";
        SongEntry source = new SongEntry { Title = title, Jianpu = "1:2 0 3 5", Bpm = 100 };
        SongEntry saved = MidiEntry(title);
        int created = 0, converted = 0;
        string receivedTitle = null, receivedArtist = null;
        SongEntry received = null;
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry>(), EmptyFree, SongSearch(title, "录入歌手"),
            delegate(SongEntry score) { converted++; received = score; return saved; }, NoChineseImport, NoChineseDownload,
            delegate(string name, string artist) { created++; receivedTitle = name; receivedArtist = artist; return source; }))
        {
            Show(dialog); SearchMusic(dialog, title);
            Assert(created == 0 && converted == 0, "歌曲元数据本身不创建简谱。");
            FindButton(Field<TabPage>(dialog, "musicTab"), "＋ 粘贴简谱").PerformClick(); Pump();
            Assert(created == 1 && receivedTitle == title && receivedArtist == "录入歌手", "明确粘贴简谱时传入所选歌曲信息。");
            Assert(converted == 1 && Object.ReferenceEquals(received, source), "新建简谱调用MIDI转换一次。");
            Assert(Object.ReferenceEquals(dialog.ChosenScore, saved) && dialog.DialogResult == DialogResult.OK, "粘贴后的最终选择是转换MIDI条目。");
        }
    }

    private static void TestMissingScoreDownloadsSelectedAudio()
    {
        OnlineSong selected = MockSong("无谱音频下载流程987654", "指定测试歌手", "qq-selected-001");
        SongEntry saved = MidiEntry(selected.Title);
        int audioCalls = 0;
        OnlineSong received = null;
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry>(), EmptyFree, SongSearch(selected), NoConvert,
            NoChineseImport, NoChineseDownload, NoJianpu))
        {
            dialog.DownloadAudio = delegate(OnlineSong song) { audioCalls++; received = song; return saved; };
            DialogResult result = RunModal(dialog, delegate
            {
                SearchMusic(dialog, selected.Title);
                Wait(delegate { return Field<Label>(dialog, "localHint").Text.Contains("尚无对应乐谱"); }, "无谱状态未完成。");
                Assert(Field<ComboBox>(dialog, "localScores").Items.Count == 0 && !Field<Button>(dialog, "choose").Enabled &&
                    Field<Button>(dialog, "songDownload").Enabled, "无谱时只启用独立下载音频按钮，不混淆MIDI导入。");
                Assert(audioCalls == 0 && dialog.ChosenScore == null, "查询歌曲元数据不主动调用音频或创建乐谱。");
                Field<Button>(dialog, "songDownload").PerformClick();
                Assert(audioCalls == 1 && Object.ReferenceEquals(received, selected), "点击后下载回调收到原歌曲对象一次。");
                Assert(received.Title == selected.Title && received.Artist == selected.Artist &&
                    received.Provider == "QQ音乐" && received.Id == "qq-selected-001", "音频下载准确保留曲名、歌手、平台和ID。");
                Assert(Object.ReferenceEquals(dialog.ChosenScore, saved), "音频结果作为最终选择，不编造其他旋律。");
            });
            Assert(result == DialogResult.OK && !dialog.Visible, "音频导入后真实模态窗口正常关闭。");
        }
    }

    private static void TestFailedRemoteRequiresExplicitAudio()
    {
        OnlineSong selected = MockSong("晴天", "周杰伦", "qq-qingtian");
        SongEntry saved = MidiEntry("晴天 · 音频自动转录");
        int midiCalls = 0, audioCalls = 0;
        ChineseScore attempted = null;
        OnlineSong received = null;
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry>(), EmptyFree, SongSearch(selected), NoConvert,
            NoChineseImport, delegate(ChineseScore score)
            { attempted = score; Interlocked.Increment(ref midiCalls); throw new InvalidOperationException("模拟谱源连接中断"); }, NoJianpu))
        {
            dialog.DownloadAudio = delegate(OnlineSong song) { audioCalls++; received = song; return saved; };
            Show(dialog); SearchMusic(dialog, selected.Title);
            ComboBox choices = Field<ComboBox>(dialog, "localScores");
            Wait(delegate { return choices.SelectedIndex >= 0 && choices.SelectedItem.ToString().Contains("周杰伦"); }, "同歌手完整MIDI未匹配。");
            Assert(midiCalls == 0 && audioCalls == 0, "选择歌曲不会主动下载MIDI或音频。");
            Field<Button>(dialog, "choose").PerformClick();
            Wait(delegate { return midiCalls == 1 && !Field<bool>(dialog, "downloading"); }, "MIDI失败后操作未恢复。");
            Assert(midiCalls == 1 && attempted.Id == "516" && attempted.Artist == "周杰伦", "先尝试真实索引中的同歌手MIDI。");
            for (int i = 0; i < 5; i++) Pump();
            Assert(audioCalls == 0 && dialog.ChosenScore == null && dialog.Visible && dialog.DialogResult == DialogResult.None,
                "谱获取失败后没有排队或后台自动转录，保留窗口让用户选择。");
            Assert(Field<Label>(dialog, "musicStatus").Text.Contains("明确点") && Field<Button>(dialog, "choose").Enabled,
                "错误明确说明独立录音操作，并保留重试当前谱的入口。");
            Field<Button>(dialog, "songDownload").PerformClick(); Pump();
            Assert(audioCalls == 1 && Object.ReferenceEquals(received, selected), "用户明确点击后才转录原所选录音一次。");
            Assert(Object.ReferenceEquals(dialog.ChosenScore, saved), "明确选择录音后返回实际转录结果。");
        }
    }

    private static void TestCancelledAudioCanRetry()
    {
        OnlineSong selected = MockSong("音频取消保留歌曲987654", "保留的原歌手", "qq-cancel-retry");
        SongEntry saved = MidiEntry(selected.Title);
        int audioCalls = 0;
        OnlineSong first = null, second = null;
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry>(), EmptyFree, SongSearch(selected), NoConvert,
            NoChineseImport, NoChineseDownload, NoJianpu))
        {
            dialog.DownloadAudio = delegate(OnlineSong song)
            {
                audioCalls++;
                if (audioCalls == 1) { first = song; return null; }
                second = song; return saved;
            };
            Show(dialog); SearchMusic(dialog, selected.Title);
            Wait(delegate { return Field<Button>(dialog, "songDownload").Enabled; }, "无谱音频按钮未启用。");
            Field<Button>(dialog, "songDownload").PerformClick(); Pump();
            Assert(audioCalls == 1 && dialog.ChosenScore == null && dialog.DialogResult == DialogResult.None && dialog.Visible,
                "取消音频返回null时保持窗口与空选择。");
            Assert(Field<TabControl>(dialog, "tabs").Enabled && Field<Button>(dialog, "songDownload").Enabled && !Field<bool>(dialog, "downloading"),
                "取消后恢复操作，允许重新选择音频。");
            ListView rows = Field<ListView>(dialog, "musicResults");
            Assert(rows.SelectedItems.Count == 1 && Object.ReferenceEquals(rows.SelectedItems[0].Tag, selected), "取消不改歌曲或歌手版本。");
            Field<Button>(dialog, "songDownload").PerformClick(); Pump();
            Assert(audioCalls == 2 && Object.ReferenceEquals(first, selected) && Object.ReferenceEquals(second, selected), "重试仍传入同一完整歌曲信息。");
            Assert(Object.ReferenceEquals(dialog.ChosenScore, saved) && dialog.DialogResult == DialogResult.OK, "重试成功选入返回的MIDI。");
        }
    }

    private static void TestOtherArtistFailureKeepsOriginalVersion()
    {
        OnlineSong selected = MockSong("七里香", "周杰伦", "qq-original-version");
        int midiCalls = 0, audioCalls = 0;
        ChineseScore attempted = null;
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry>(), EmptyFree, SongSearch(selected), NoConvert,
            NoChineseImport, delegate(ChineseScore score)
            { attempted = score; Interlocked.Increment(ref midiCalls); throw new InvalidOperationException("模拟其他歌手谱下载失败"); }, NoJianpu))
        {
            dialog.DownloadAudio = delegate { audioCalls++; return MidiEntry("不应偷偷选入的周杰伦音频"); };
            Show(dialog); SearchMusic(dialog, selected.Title);
            ComboBox choices = Field<ComboBox>(dialog, "localScores");
            Wait(delegate { return choices.Items.Count > 0; }, "不同歌手谱候选未出现。");
            Assert(choices.SelectedIndex == -1, "不同歌手的候选仍需要明确选择。");
            choices.SelectedIndex = 0;
            Assert(choices.SelectedItem.ToString().Contains("刘瑞琦") && choices.SelectedItem.ToString().Contains("不同歌手"), "明确选择真实刘瑞琦版本。");
            Field<Button>(dialog, "choose").PerformClick();
            Wait(delegate { return midiCalls == 1 && !Field<bool>(dialog, "downloading"); }, "其他歌手下载失败后界面未恢复。");
            Assert(attempted.Artist == "刘瑞琦" && audioCalls == 0 && dialog.ChosenScore == null, "其他歌手谱失败不拿QQ当前原唱录音自动替代。");
            Assert(dialog.Visible && dialog.DialogResult == DialogResult.None && Field<TabControl>(dialog, "tabs").Enabled, "保留窗口让用户决定后续版本。");
            Assert(Object.ReferenceEquals(Field<ListView>(dialog, "musicResults").SelectedItems[0].Tag, selected), "失败不会悄悄改掉QQ选中的版本。");
        }
    }

    private static void TestFullRemotePreferredOverBuiltin()
    {
        var shortTheme = new SongEntry { Title = "晴天", Builtin = true, Subtitle = "内置短主题", Bpm = 120,
            Jianpu = "1 2 3 4 5 6 7 1 2 3 4 5 6 7 1 2 3 4 5 6 7 1 2 3 4 5 6 7 1 2 3 4 5 6 7 1 2 3 4 5" };
        OnlineSong selected = MockSong("晴天", "周杰伦", "qq-full-score");
        int conversions = 0, midiCalls = 0, audioCalls = 0;
        SongEntry saved = MidiEntry("晴天 · 完整MIDI");
        ChineseScore imported = null;
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry> { shortTheme }, EmptyFree, SongSearch(selected),
            delegate { conversions++; return MidiEntry("错误短主题"); },
            delegate(ChineseScore score, byte[] bytes) { imported = score; return saved; },
            delegate { Interlocked.Increment(ref midiCalls); return MelodyPayload(); }, NoJianpu))
        {
            dialog.DownloadAudio = delegate { audioCalls++; return null; };
            Show(dialog); SearchMusic(dialog, selected.Title);
            ComboBox choices = Field<ComboBox>(dialog, "localScores");
            Wait(delegate { return choices.Items.Count == 2 && choices.SelectedIndex >= 0; }, "完整与短主题候选未匹配。");
            Assert(Object.ReferenceEquals(choices.Items[0], shortTheme) && !Object.ReferenceEquals(choices.SelectedItem, shortTheme), "短主题保留可选，默认选择对应完整远谱。");
            Assert(choices.SelectedItem.ToString().Contains("周杰伦") && midiCalls == 0 && conversions == 0, "默认完整候选仍等待确认。");
            Field<Button>(dialog, "choose").PerformClick();
            Wait(delegate { return dialog.DialogResult == DialogResult.OK; }, "完整MIDI选择未完成。");
            Assert(midiCalls == 1 && conversions == 0 && audioCalls == 0 && imported.Id == "516", "只获取完整MIDI，不转换40音符主题或下载音频。");
            Assert(Object.ReferenceEquals(dialog.ChosenScore, saved), "返回完整谱导入结果。");
        }
    }

    private static void TestLocalUserMidiRemainsPreferred()
    {
        var shortTheme = new SongEntry { Title = "晴天", Builtin = true, Jianpu = "1 2 3 4" };
        SongEntry userMidi = MidiEntry("晴天"); userMidi.FileName = "user-qingtian.mid";
        int audioCalls = 0, conversions = 0;
        SongEntry received = null;
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry> { shortTheme, userMidi }, EmptyFree,
            SongSearch(MockSong("晴天", "周杰伦", "qq-user-midi")), delegate(SongEntry score)
            { conversions++; received = score; return score; }, NoChineseImport, NoChineseDownload, NoJianpu))
        {
            dialog.DownloadAudio = delegate { audioCalls++; return null; };
            Show(dialog); SearchMusic(dialog, "晴天");
            ComboBox choices = Field<ComboBox>(dialog, "localScores");
            Wait(delegate { return choices.Items.Count == 3 && choices.SelectedIndex >= 0; }, "本地用户MIDI与对应编配未同时出现。");
            Assert(!Object.ReferenceEquals(choices.SelectedItem, userMidi) && choices.SelectedItem.ToString().Contains("钢琴编配"),
                "歌手未核实的本地MIDI不默认替代明确同歌手的主旋律编配。");
            Assert(conversions == 0 && dialog.ChosenScore == null, "本地MIDI也等用户确认后才选入。");
            choices.SelectedItem = userMidi;
            Field<Button>(dialog, "choose").PerformClick();
            Wait(delegate { return dialog.DialogResult == DialogResult.OK; }, "本地用户MIDI确认未完成。");
            Assert(conversions == 1 && Object.ReferenceEquals(received, userMidi) && audioCalls == 0, "只处理已选用户MIDI，不下载其他来源。");
            Assert(Object.ReferenceEquals(dialog.ChosenScore, userMidi) && dialog.ChosenScore.FileName == "user-qingtian.mid", "保留同一个用户MIDI条目和文件。");
        }
    }

    private static void TestMelodyPreferredToAudioEstimate()
    {
        foreach (var version in new[] { new[] { "父亲", "筷子兄弟", "622" }, new[] { "晴天", "周杰伦", "516" } })
        {
            var estimate = MidiEntry(version[0]); estimate.SourceEdition = "Basic Pitch 自动转录 · 需试听确认";
            estimate.Attribution = version[1]; estimate.SourceHash = new string('B', 64); estimate.AudioFileName = "old-recording.mp3";
            SongEntry saved = MidiEntry(version[0] + " · POP909钢琴编配");
            int audioCalls = 0, downloads = 0, imports = 0;
            using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry> { estimate }, EmptyFree, SongSearch(version[0], version[1]),
                delegate { throw new Exception("不得把旧音频转录当作推荐主谱。"); },
                delegate(ChineseScore choice, byte[] bytes)
                { imports++; Assert(choice.Id == version[2] && choice.Artist == version[1], "下载与导入保留实际索引版本。"); return saved; },
                delegate { downloads++; return MelodyPayload(); }, NoJianpu))
            {
                dialog.DownloadAudio = delegate { audioCalls++; return null; };
                Show(dialog); SearchMusic(dialog, version[0]);
                ComboBox choices = Field<ComboBox>(dialog, "localScores");
                Wait(delegate { return choices.Items.Count == 2 && choices.SelectedIndex >= 0; }, "准确主旋律候选未优先旧转录。");
                Assert(!Object.ReferenceEquals(choices.SelectedItem, estimate) && choices.SelectedItem.ToString().Contains("MELODY"), "实际主旋律优先已知音频转录。");
                Assert(Field<Button>(dialog, "choose").Text == "选用主旋律 MIDI" && Field<Button>(dialog, "songDownload").Text == "下载录音 → MIDI",
                    "现成主谱与录音动作明确分开。");
                Assert(downloads == 0 && imports == 0 && audioCalls == 0, "显示候选不自动获取任何文件。");
                DoubleClickMusic(dialog);
                Wait(delegate { return dialog.DialogResult == DialogResult.OK; }, "歌曲双击未执行可见主谱动作。");
                Assert(downloads == 1 && imports == 1 && audioCalls == 0 && Object.ReferenceEquals(dialog.ChosenScore, saved), "双击准确导入主谱，零录音调用。");
                Assert(estimate.SourceEdition == "Basic Pitch 自动转录 · 需试听确认" && estimate.SourceHash == new string('B', 64) && estimate.AudioFileName == "old-recording.mp3",
                    "推荐和导入不会改写旧谱条目或录音配对。");
            }
        }
    }

    private static void TestLibraryMelodyLookup()
    {
        int musicCalls = 0, downloads = 0;
        ChineseScore received = null;
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry>(), EmptyFree,
            delegate { musicCalls++; throw new Exception("查库主旋律不应先等QQ网络。"); }, NoConvert,
            delegate(ChineseScore choice, byte[] bytes) { received = choice; return MidiEntry(choice.Title); },
            delegate { downloads++; return MelodyPayload(); }, NoJianpu))
        {
            dialog.PreferExistingMelody("父亲 · 连续旋律版", "筷子兄弟");
            Show(dialog); Pump();
            ComboBox choices = Field<ComboBox>(dialog, "localScores");
            Assert(musicCalls == 0 && downloads == 0 && choices.Items.Count == 1 && choices.SelectedIndex == 0,
                "库内查谱立即显示确证候选，打开时不请求QQ或下载。");
            Assert(Field<TextBox>(dialog, "musicQuery").Text == "父亲" && Field<Label>(dialog, "musicStatus").Text.Contains("原曲目和 MIDI 保留"),
                "仅剥除本软件生成的版名，说明旧谱保留。");
            Field<Button>(dialog, "choose").PerformClick();
            Wait(delegate { return dialog.DialogResult == DialogResult.OK; }, "查库候选未选入。");
            Assert(downloads == 1 && received.Id == "622" && musicCalls == 0, "查库确认使用官方622来源，无歌曲网络依赖。");
        }
    }

    private static void TestUnconfirmedAndLiveMelody()
    {
        string[] editionTitles = { "父亲", "父亲（Live）", "父亲", "父亲（钢琴版）", "父亲（粤语版）",
            "父亲（国语版）", "父亲（原声版）", "父亲 (piano version)", "父亲（粤语版） · 连续旋律版" };
        for (int edition = 0; edition < editionTitles.Length; edition++)
        {
            bool live = edition == 1 || edition == 2;
            int audio = 0, downloads = 0, imports = 0;
            var albumLive = MockSong("父亲", "筷子兄弟", "live-album-version"); albumLive.Album = "现场演唱会 Live";
            using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry>(), EmptyFree, edition == 2 ? SongSearch(albumLive) : (Func<string, int, List<OnlineSong>>)EmptyMusic, NoConvert,
                delegate { imports++; return MidiEntry("父亲 · 明确选用的钢琴编配"); },
                delegate { downloads++; return MelodyPayload(); }, NoJianpu))
            {
                dialog.DownloadAudio = delegate { audio++; return null; };
                if (edition != 2) dialog.PreferExistingMelody(editionTitles[edition], edition == 0 ? "" : "筷子兄弟");
                Show(dialog); Pump();
                if (edition == 2) SearchMusic(dialog, "父亲");
                ComboBox choices = Field<ComboBox>(dialog, "localScores");
                Wait(delegate { return choices.Items.Count == 1; }, "待确认编配候选未准备。");
                Assert(choices.Items.Count == 1 && choices.SelectedIndex == -1 && !Field<Button>(dialog, "choose").Enabled,
                    "未知歌手或不同录音版的候选不默认选用。");
                string warning = live ? "非所选Live原谱" : edition >= 3 ? "非所选录音原版" : "歌手待确认";
                string description = choices.Items[0].ToString();
                Assert(description.IndexOf(warning, StringComparison.Ordinal) >= 0 && description.IndexOf(warning, StringComparison.Ordinal) < 40,
                    "歌名或专辑标注Live、其他录音版及歌手待确认的差异均放在候选文字前部。");
                int windows = Application.OpenForms.Count;
                DoubleClickMusic(dialog); Pump();
                Assert(audio == 0 && downloads == 0 && imports == 0, "有待确认候选时双击不偷换谱或隐式转录。");
                Assert(Application.OpenForms.Count == windows, "版本确认留在在线曲库原页，不打开第二窗口。");
                choices.SelectedIndex = 0;
                Assert(Field<Button>(dialog, "choose").Text == "采用此编配 MIDI", "明确选择不同版本后动作仍说明采用编配。");
                Field<Button>(dialog, "choose").PerformClick();
                Wait(delegate { return dialog.DialogResult == DialogResult.OK; }, "明确确认编配后未导入。");
                Assert(downloads == 1 && imports == 1 && audio == 0, "明确确认后只获取该编配，录音保持零调用。");
            }
        }
    }

    private static void TestOtherArtistLocalScore()
    {
        ChineseScore arrangement = ChineseScoreCatalog.FindExact("七里香", "刘瑞琦").Single();
        SongEntry local = MidiEntry("七里香"); local.SourceUrl = arrangement.SourceUrl; local.SourceEdition = "POP909 钢琴编配 · 刘瑞琦";
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry> { local }, EmptyFree, SongSearch("七里香", "周杰伦"), NoConvert,
            NoChineseImport, NoChineseDownload, NoJianpu))
        {
            int audio = 0; dialog.DownloadAudio = delegate { audio++; return null; };
            Show(dialog); SearchMusic(dialog, "七里香");
            ComboBox choices = Field<ComboBox>(dialog, "localScores");
            Wait(delegate { return choices.Items.Count == 1; }, "已入库其他歌手谱未去重。");
            Assert(choices.SelectedIndex == -1 && !Field<Button>(dialog, "choose").Enabled, "已入库同名其他歌手谱不默认替代周杰伦版本。");
            DoubleClickMusic(dialog); Pump();
            Assert(audio == 0 && dialog.ChosenScore == null, "其他歌手本地谱等待明确选择，不自动下载或关闭。");
        }
    }

    private static void TestOfficialSourceReuse()
    {
        ChineseScore arrangement = ChineseScoreCatalog.FindExact("父亲", "筷子兄弟").Single();
        foreach (string url in new[] { arrangement.SourceUrl, arrangement.MidiUrl, "https://example.test/622.mid" })
        {
            bool official = url != "https://example.test/622.mid";
            SongEntry local = MidiEntry("父亲"); local.FileName = "pop909-622.mid"; local.SourceUrl = url;
            int conversions = 0, downloads = 0;
            using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry> { local }, EmptyFree, SongSearch("父亲", "筷子兄弟"),
                delegate(SongEntry selected) { conversions++; Assert(Object.ReferenceEquals(selected, local), "复用实际本地条目。"); return selected; },
                delegate { return MidiEntry("父亲 · 官方主谱"); }, delegate { downloads++; return MelodyPayload(); }, NoJianpu))
            {
                Show(dialog); SearchMusic(dialog, "父亲");
                ComboBox choices = Field<ComboBox>(dialog, "localScores");
                Wait(delegate { return choices.Items.Count == (official ? 1 : 2) && choices.SelectedIndex >= 0; }, "官方来源候选去重不准确。");
                Assert(Object.ReferenceEquals(choices.SelectedItem, local) == official, "以确切官方来源去重，文件名中的622不构成来源证明。");
                Field<Button>(dialog, "choose").PerformClick();
                Wait(delegate { return dialog.DialogResult == DialogResult.OK; }, "来源选择未完成。");
                Assert(conversions == (official ? 1 : 0) && downloads == (official ? 0 : 1), "官方来源本地复用，伪来源不阻止实际谱获取。");
            }
        }
    }

    private static void TestMissingMelodyDoesNotTranscribe()
    {
        int audio = 0, imports = 0, downloads = 0;
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry>(), EmptyFree, SongSearch("父亲", "筷子兄弟"), NoConvert,
            delegate { imports++; return MidiEntry("不得导入的伴奏"); },
            delegate { downloads++; return MidiWriter.ToBytes(new[] { new NoteEvent { Pitch = 48, Start = 0, Duration = 1, Velocity = 90 } }, "PIANO"); }, NoJianpu))
        {
            dialog.DownloadAudio = delegate { audio++; return null; };
            Show(dialog); SearchMusic(dialog, "父亲");
            Wait(delegate { return Field<Button>(dialog, "choose").Enabled; }, "父亲主谱候选不可用。");
            Field<Button>(dialog, "choose").PerformClick();
            Wait(delegate { return downloads == 1 && !Field<bool>(dialog, "downloading"); }, "缺少MELODY校验未结束。");
            for (int i = 0; i < 5; i++) Pump();
            Assert(imports == 0 && audio == 0 && dialog.ChosenScore == null && dialog.Visible, "仅伴奏MIDI不冒充主旋律，失败无后台转录。");
            Assert(Field<Label>(dialog, "musicStatus").Text.Contains("没有可用 MELODY"), "用户能看到实际缺少主旋律轨的原因。");
        }
    }

    private static void TestNoScoreDoubleClickUsesAudio()
    {
        OnlineSong original = MockSong("没有主谱的双击验证987654", "原始歌手", "double-audio");
        int audio = 0;
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry>(), EmptyFree, SongSearch(original), NoConvert, NoChineseImport, NoChineseDownload, NoJianpu))
        {
            dialog.DownloadAudio = delegate(OnlineSong song) { audio++; Assert(Object.ReferenceEquals(original, song), "无谱双击保留确切录音版本。"); return null; };
            Show(dialog); SearchMusic(dialog, original.Title);
            Wait(delegate { return Field<ComboBox>(dialog, "localScores").Items.Count == 0 && Field<Button>(dialog, "songDownload").Enabled; }, "无谱录音动作未准备。");
            Assert(Field<Button>(dialog, "songDownload").Text == "下载录音 → MIDI" && Field<Label>(dialog, "localHint").Text.Contains("双击歌曲"), "无谱双击与可见录音动作一致。");
            DoubleClickMusic(dialog); Pump();
            Assert(audio == 1 && dialog.ChosenScore == null, "无谱明确双击只触发一次原录音操作。");
        }
    }

    private static byte[] MelodyPayload()
    { return MidiWriter.ToBytes(new[] { new NoteEvent { Pitch = 67, Start = .5, Duration = 1, Velocity = 85 } }, "MELODY"); }
    private static void DoubleClickMusic(OnlineCatalogDialog dialog)
    { typeof(Control).GetMethod("OnDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Field<ListView>(dialog, "musicResults"), new object[] { EventArgs.Empty }); }

    private static void TestProviderSelectionAndSeparateAudio()
    {
        const string title = "双平台独立下载流程987654";
        SongEntry local = MidiEntry(title);
        OnlineSong received = null;
        int downloads = 0;
        List<string> searches = new List<string>();
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry> { local }, EmptyFree, EmptyMusic,
            NoConvert, NoChineseImport, NoChineseDownload, NoJianpu))
        {
            dialog.SearchSongsByProvider = delegate(string provider, string term, int page)
            {
                lock (searches) searches.Add(provider + "/" + term + "/" + page);
                return new List<OnlineSong> { new OnlineSong { Title = title, Artist = "同一演唱者", Provider = provider,
                    Id = provider == "QQ音乐" ? "qq-test-version" : "", FileHash = provider == "酷狗音乐" ? "0123456789ABCDEF0123456789ABCDEF" : "" } };
            };
            dialog.DownloadAudio = delegate(OnlineSong song) { downloads++; received = song; return null; };
            Show(dialog);
            ComboBox providerChoice = Field<ComboBox>(dialog, "musicProvider");
            Assert((string)providerChoice.SelectedItem == "QQ音乐" && providerChoice.Items.Count == 2, "平台默认QQ，提供QQ和酷狗两项。");
            SearchMusic(dialog, title);
            ComboBox choices = Field<ComboBox>(dialog, "localScores");
            Wait(delegate { return choices.Items.Contains(local); }, "已有MIDI没有保留供明确选择。");
            Assert(choices.SelectedIndex == -1, "歌手未核实的本地谱不默认冒充所选演唱者版本。");
            choices.SelectedItem = local;
            Assert(Field<Button>(dialog, "choose").Enabled && Field<Button>(dialog, "songDownload").Enabled,
                "有匹配MIDI时仍可独立下载所选录音转录。");
            Field<Button>(dialog, "songDownload").PerformClick(); Pump();
            Assert(downloads == 1 && received.Provider == "QQ音乐" && dialog.ChosenScore == null, "点击音频主按钮进入QQ录音回调，不先导入已有MIDI。");
            providerChoice.SelectedIndex = 1; Pump();
            Assert(Field<ListView>(dialog, "musicResults").Items.Count == 0 && !Field<Button>(dialog, "songDownload").Enabled,
                "切换平台立即清除前一个平台的歌曲选择，防止下载错源。");
            SearchMusic(dialog, title);
            OnlineSong selected = (OnlineSong)Field<ListView>(dialog, "musicResults").SelectedItems[0].Tag;
            Field<Button>(dialog, "songDownload").PerformClick(); Pump();
            Assert(searches.Count == 2 && searches[0] == "QQ音乐/" + title + "/1" && searches[1] == "酷狗音乐/" + title + "/1",
                "搜索始终将所选来源与关键词传入统一来源接口。");
            Assert(downloads == 2 && Object.ReferenceEquals(received, selected) && received.Provider == "酷狗音乐" &&
                received.FileHash == "0123456789ABCDEF0123456789ABCDEF", "酷狗下载保留所选结果、来源和文件hash。");
        }
    }

    private static void TestAudioDownloadConvertsOnCompletion()
    {
        string fixture = Path.Combine(AppPaths.ContentDirectory, "audio-flow-" + Guid.NewGuid().ToString("N") + ".wav");
        byte[] wave = new byte[44];
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(wave, 0);
        System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(wave, 8);
        File.WriteAllBytes(fixture, wave);
        try
        {
            foreach (string provider in new string[] { "QQ音乐", "酷狗音乐" })
            {
                OnlineSong song = new OnlineSong { Title = "两平台完整下载顺序", Artist = "演唱者", Provider = provider };
                var downloadDone = new TaskCompletionSource<string>();
                int resolves = 0, downloads = 0, conversions = 0;
                byte[] expected = MidiWriter.ToBytes(new[] { new NoteEvent { Pitch = 60, Start = 0, Duration = 1, Velocity = 90 } }, song.Title);
                using (AudioMidiDialog dialog = new AudioMidiDialog(song.Title, song, "", null))
                {
                    dialog.AutoDownload = true;
                    dialog.ResolveSongAudio = delegate(OnlineSong selected)
                    { Interlocked.Increment(ref resolves); Assert(Object.ReferenceEquals(selected, song), "解析得到原选中的歌曲对象。"); return "https://audio.example.test/song.mp3"; };
                    dialog.DownloadSongAudio = delegate(string url, string title, Action<string> progress, CancellationToken token)
                    { downloads++; Assert(url == "https://audio.example.test/song.mp3" && title == song.Title, "实际解析地址和原歌名传入下载。"); return downloadDone.Task; };
                    dialog.ConvertLocalAudio = delegate(string input, string title, Action<string> progress, CancellationToken token)
                    { conversions++; Assert(input == fixture && title == song.Title, "下载落地的实际音频路径和原歌名直接转录。"); return Task.FromResult(expected); };
                    DialogResult result = RunModal(dialog, delegate
                    {
                        Wait(delegate { return downloads == 1; }, "自动选歌下载未开始。");
                        Assert(resolves == 1 && conversions == 0 && dialog.MidiBytes == null, "下载尚未完成时不提前转换或伪报MIDI。");
                        Assert(!Field<Button>(dialog, "remoteConvert").Enabled && !Field<Button>(dialog, "officialButton").Enabled,
                            "下载过程中禁用重复请求和平台跳转。");
                        downloadDone.SetResult(fixture);
                        Wait(delegate { return dialog.MidiBytes != null; }, "歌曲下载完成后没有自动转录。");
                    });
                    Assert(result == DialogResult.OK && conversions == 1 && Object.ReferenceEquals(dialog.MidiBytes, expected),
                        provider + " 下载完成自动转录一次，并返回MIDI供主窗导入。");
                }
            }
        }
        finally { if (File.Exists(fixture)) File.Delete(fixture); }
    }

    private static void TestRestrictedAudioKeepsOfficialFlow()
    {
        foreach (string provider in new string[] { "QQ音乐", "酷狗音乐" })
        {
            OnlineSong song = new OnlineSong { Title = "平台受限的版本", Artist = "原演唱者", Provider = provider };
            int resolves = 0, downloads = 0, conversions = 0;
            using (AudioMidiDialog dialog = new AudioMidiDialog(song.Title, song, "", null))
            {
                dialog.AutoDownload = true;
                dialog.ResolveSongAudio = delegate { Interlocked.Increment(ref resolves); return null; };
                dialog.DownloadSongAudio = delegate { downloads++; throw new Exception("受限歌曲不得编造下载链接。"); };
                dialog.ConvertLocalAudio = delegate { conversions++; throw new Exception("受限歌曲不得伪造转换结果。"); };
                dialog.Show(); Pump();
                Wait(delegate { return resolves == 1 && !Field<bool>(dialog, "working"); }, "受限版本查询未结束。");
                Assert(downloads == 0 && conversions == 0 && dialog.MidiBytes == null && dialog.DialogResult == DialogResult.None,
                    "平台未授予普通音频地址时，不下载和虚报转换成功。");
                Assert(Field<Label>(dialog, "status").Text.Contains("未提供直接下载") && Field<Label>(dialog, "status").Text.Contains(provider),
                    "真实限制说明和官方客户端入口针对当前平台。");
                Assert(Field<bool>(dialog, "monitoringDownloads") && Field<Button>(dialog, "officialButton").Enabled,
                    "受限版本保持下载目录监听，并允许用户自行打开官方入口。");
                Assert(Field<Button>(dialog, "officialButton").Text.Contains(provider) && Field<Button>(dialog, "clientButton").Text.Contains(provider),
                    "酷狗流程不会显示或打开QQ入口。");
                dialog.Close(); Pump();
            }
        }
    }

    private static void TestSongDialogLayout()
    {
        using (AudioMidiDialog localAudio = new AudioMidiDialog(""))
            Assert(localAudio.SongName == "我的音频转录" && localAudio.ClientSize == new Size(700, 592), "未选线上歌曲的本地音频入口仍可打开。");
        using (OnlineCatalogDialog dialog = Dialog(new List<SongEntry>(), EmptyFree, EmptyMusic,
            NoConvert, NoChineseImport, NoChineseDownload, NoJianpu))
        {
            Show(dialog); Field<TabControl>(dialog, "tabs").SelectedTab = Field<TabPage>(dialog, "musicTab"); Pump();
            Assert(dialog.AutoScaleMode == AutoScaleMode.None && dialog.ClientSize == new Size(820, 578), "加入两平台选择不扩大窗口或按DPI重复放大。");
            Assert(dialog.Controls.OfType<Label>().Any(label => label.Text.Contains("QQ／酷狗搜歌") && label.Text.Contains("软件内登录") && label.Text.Contains("外部浏览器登录不会同步")),
                "在线窗口顶部明确区分真实软件内登录与外部浏览器会话。");
            CheckChildrenBounds(Field<TabPage>(dialog, "musicTab"));
            foreach (Button button in new[] { Field<Button>(dialog, "songDownload"), Field<Button>(dialog, "choose"), Field<Button>(dialog, "officialMusic") })
                Assert(TextRenderer.MeasureText(button.Text, button.Font).Width + 16 <= button.Width, "操作按钮宽度能完整显示文本：" + button.Text);
        }
        foreach (string provider in new string[] { "QQ音乐", "酷狗音乐" })
            using (AudioMidiDialog dialog = new AudioMidiDialog("布局验证", new OnlineSong { Title = "布局验证", Provider = provider }, "", null))
            {
                Assert(dialog.ClientSize == new Size(700, 592), "音频下载窗口保持原尺寸。");
                CheckChildrenBounds(dialog);
                foreach (string name in new[] { "officialButton", "clientButton", "remoteConvert" })
                {
                    Button button = Field<Button>(dialog, name);
                    Assert(TextRenderer.MeasureText(button.Text, button.Font).Width + 16 <= button.Width, "平台按钮文字完整可读：" + button.Text);
                }
            }
    }

    private static void TestManualAudioWithoutSong()
    {
        foreach (bool requestAutomatic in new bool[] { false, true })
            using (AudioMidiDialog dialog = new AudioMidiDialog(""))
            {
                int requests = 0;
                dialog.AutoDownload = requestAutomatic;
                dialog.ResolveSongAudio = delegate { requests++; throw new Exception("本地音频入口没有所选线上歌曲。"); };
                dialog.DownloadSongAudio = delegate { requests++; throw new Exception("没有音频地址时不能下载。"); };
                dialog.ConvertLocalAudio = delegate { requests++; throw new Exception("没有选择音频文件时不能转换。"); };
                dialog.Show(); Pump();
                Assert(dialog.Visible && requests == 0 && dialog.MidiBytes == null && !Field<bool>(dialog, "working"),
                    "真实本地音频窗口无线上歌曲、无链接时可以打开，且不下载或转换。");
                Assert(Field<Button>(dialog, "browse").Enabled && !Field<Button>(dialog, "convert").Enabled,
                    "本地音频入口保留选择文件按钮，选择前转换按钮不可用。");
                if (requestAutomatic) Assert(Field<Label>(dialog, "status").Text.Contains("请输入"), "空链接的自动请求给出输入说明。");
                dialog.Close(); Pump();
            }
    }

    private static void TestInlineSongConversion()
    {
        WithWave(delegate(string fixture)
        {
            foreach (string provider in new[] { "QQ音乐", "酷狗音乐" })
            {
                var selected = new OnlineSong { Title = "同页完整录音流程987654", Artist = "明确的原演唱者", Album = "指定专辑", Provider = provider, Id = "same-page-version", FreeDownload = true };
                var audioDone = new TaskCompletionSource<string>();
                var midiDone = new TaskCompletionSource<byte[]>();
                byte[] midi = MidiWriter.ToBytes(new[] { new NoteEvent { Pitch = 60, Start = 0, Duration = 1, Velocity = 90 } }, selected.Title);
                int resolves = 0, downloads = 0, conversions = 0, imports = 0;
                SongEntry saved = MidiEntry(selected.Title);
                using (var dialog = InlineDialog(selected))
                {
                    dialog.DownloadAudio = delegate { throw new Exception("同页生产入口不得回退为第二转换窗。"); };
                    dialog.ResolveSongAudio = delegate(OnlineSong song) { Interlocked.Increment(ref resolves); Assert(Object.ReferenceEquals(song, selected), "同页解析保留指定平台的原版本对象。"); return "https://fixture.test/song.mp3"; };
                    dialog.DownloadSongAudio = delegate(string url, string title, Action<string> progress, CancellationToken token)
                    { downloads++; Assert(title == selected.Title && url == "https://fixture.test/song.mp3", "正确歌名与解析后的非空地址传入下载。"); progress("正在下载测试音频…"); return audioDone.Task; };
                    dialog.ConvertLocalAudio = delegate(string path, string title, Action<string> progress, CancellationToken token)
                    { conversions++; Assert(path == fixture && title == selected.Title, "使用实际落地的完整音频开始转换。"); return midiDone.Task; };
                    dialog.ImportTranscribedAudio = delegate(string title, string path, byte[] bytes)
                    { imports++; Assert(path == fixture && title == selected.Title && Object.ReferenceEquals(bytes, midi), "原录音、原歌名及真实转换结果完整传给保存回调。"); Assert(Object.ReferenceEquals(dialog.ConversionSong, selected), "导入时仍提供原歌曲版本元数据。"); return saved; };
                    Show(dialog); SearchMusic(dialog, selected.Title);
                    int visibleForms = Application.OpenForms.Count;
                    Field<Button>(dialog, "songDownload").PerformClick();
                    Wait(delegate { return downloads == 1; }, "同页歌曲下载未开始。");
                    Assert(resolves == 1 && conversions == 0 && imports == 0 && dialog.ChosenScore == null, "下载完成前不转换、保存或虚报完成。");
                    Assert(!Field<ComboBox>(dialog, "musicProvider").Enabled && !Field<ListView>(dialog, "musicResults").Enabled &&
                        !Field<Button>(dialog, "officialMusic").Enabled && Field<Button>(dialog, "songDownload").Enabled, "忙碌期间不能改平台或版本，取消按钮仍能操作。");
                    Field<TabControl>(dialog, "tabs").SelectedTab = Field<TabPage>(dialog, "chineseTab"); Pump();
                    Assert(Field<TabControl>(dialog, "tabs").SelectedTab == Field<TabPage>(dialog, "musicTab"), "处理当前歌时不能切到其他曲库并误导状态。");
                    Assert(Application.OpenForms.Count == visibleForms && Field<ProgressBar>(dialog, "songProgress").Visible, "状态和进度显示在原在线窗口，没有第二转录窗口。");
                    audioDone.SetResult(fixture);
                    Wait(delegate { return conversions == 1; }, "下载完成后未直接进入同页转换。");
                    Assert(imports == 0 && dialog.ChosenScore == null, "转换尚未完成时仍不保存乐谱。");
                    midiDone.SetResult(midi);
                    Wait(delegate { return !Field<bool>(dialog, "inlineWorking"); }, "同页转换完成未恢复窗口。");
                    Assert(imports == 1 && Object.ReferenceEquals(dialog.ChosenScore, saved) && dialog.Visible && dialog.DialogResult == DialogResult.None,
                        "转换只保存一次，成功保留在线页便于用户确认。");
                    Assert(Field<Label>(dialog, "musicStatus").Text.Contains("已加入曲库") && Field<Button>(dialog, "songDownload").Text == "立即选用", "成功状态与下一步按钮明确。");
                    Assert(!Field<ProgressBar>(dialog, "songProgress").Visible && Field<ComboBox>(dialog, "musicProvider").Enabled,
                        "结束后移除忙碌进度并恢复来源选择。");
                    Assert(Application.OpenForms.Count == visibleForms, "完成过程始终没有弹第二窗口。");
                    if (provider == "QQ音乐") Field<Button>(dialog, "songDownload").PerformClick();
                    else DoubleClickMusic(dialog);
                    Pump();
                    Assert(dialog.DialogResult == DialogResult.OK && downloads == 1 && imports == 1,
                        "完成后的立即选用按钮和歌曲双击都返回主窗口，不会重复下载或导入。");
                }
            }
        });
    }
    private static void TestInlineRestrictedVersion()
    {
        foreach (string provider in new[] { "QQ音乐", "酷狗音乐" })
        {
            var song = new OnlineSong { Title = "像我这样的人 (Live)", Artist = "测试演唱者", Provider = provider };
            int resolves = 0, downloads = 0, conversions = 0, imports = 0;
            using (var dialog = InlineDialog(song))
            {
                dialog.ResolveSongAudio = delegate { Interlocked.Increment(ref resolves); return null; };
                dialog.DownloadSongAudio = delegate { downloads++; throw new Exception("空地址不能调用下载器。"); };
                dialog.ConvertLocalAudio = delegate { conversions++; throw new Exception("受限歌曲不能虚构转录结果。"); };
                dialog.ImportTranscribedAudio = delegate { imports++; return MidiEntry("不应保存"); };
                Show(dialog); SearchMusic(dialog, song.Title); Field<Button>(dialog, "songDownload").PerformClick();
                Wait(delegate { return resolves == 1 && !Field<bool>(dialog, "inlineWorking"); }, "受限版本的查询未结束。");
                string status = Field<Label>(dialog, "musicStatus").Text;
                Assert(downloads == 0 && conversions == 0 && imports == 0 && dialog.ChosenScore == null, "未授予地址时直接停在同页，不调用空下载地址。");
                Assert(status.Contains(song.Title) && status.Contains(song.Artist) && status.Contains(provider) && status.Contains("未提供此版本的直接下载"), "说明准确对应当前歌曲、歌手、平台和限制。");
                Assert(status.Contains("已有音频转 MIDI") && Field<Button>(dialog, "officialMusic").Enabled && Field<Button>(dialog, "songDownload").Enabled,
                    "给出同页的官方来源及普通音频导入下一步，仍允许换版本重试。");
                Assert(!status.Contains("参数") && !status.Contains("HTTPS") && dialog.DialogResult == DialogResult.None, "受限提示不要求用户填写技术下载参数。");
                Assert(Field<Button>(dialog, "officialMusic").Text == "官方歌曲页 ↗", "官方歌曲页与真实软件内账号登录分别命名。");
                if (provider == "QQ音乐") Assert(status.Contains("网页登录不会同步到软件"), "QQ受限版本明确说明浏览器登录不能授权软件匿名下载。");
                Label label = Field<Label>(dialog, "musicStatus");
                Size measured = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                Assert(measured.Height <= label.Height, "受限说明完整可读，不裁掉操作指引。");
            }
        }
    }
    private static void TestInlineIncompleteAudio()
    {
        WithWave(delegate(string fixture)
        {
            var song = new OnlineSong { Title = "需账号授权的版本", Artist = "指定演唱者", Provider = "QQ音乐", FreeDownload = false, DurationSeconds = 100 };
            using (var dialog = InlineDialog(song))
            {
                int conversions = 0, imports = 0;
                dialog.ResolveSongAudio = delegate { return "https://fixture.test/full.mp3"; };
                dialog.DownloadSongAudio = delegate { return Task.FromResult(fixture); };
                dialog.ConvertLocalAudio = delegate { conversions++; return Task.FromResult(new byte[] { 1 }); };
                dialog.ImportTranscribedAudio = delegate { imports++; return MidiEntry(song.Title); };
                Show(dialog); SearchMusic(dialog, song.Title);
                Task workflow = dialog.RunInlineConversionAsync(song, null);
                Wait(delegate { return workflow.IsCompleted; }, "受限音频时长核对没有结束。");
                Assert(conversions == 0 && imports == 0 && dialog.ChosenScore == null, "无法认证为完整MP3的受限录音不能转换或误导入。");
                Assert(Field<Label>(dialog, "musicStatus").Text.Contains("完整时长不符") && !Field<bool>(dialog, "inlineWorking"), "试听/非整曲失败有清楚说明并恢复操作。");
                Assert(Field<string>(dialog, "downloadedAudioPath") == null && Field<Button>(dialog, "songDownload").Text == "下载录音 → MIDI", "未核验的录音不能进入可直接重试转录缓存。");
                Assert(Field<Button>(dialog, "qqLogin").Enabled, "核对失败后软件内登录入口恢复可用。");
            }
        });
    }
    private static void TestInlineCancelResolve()
    {
        var song = MockSong("解析取消987654", "原歌手", "cancel-resolve");
        using (var started = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var finished = new ManualResetEvent(false))
        using (var dialog = InlineDialog(song))
        {
            int downloads = 0, conversions = 0, imports = 0;
            dialog.ResolveSongAudio = delegate { started.Set(); release.WaitOne(5000); finished.Set(); return "https://late.test/song.mp3"; };
            dialog.DownloadSongAudio = delegate { downloads++; throw new Exception("取消后不得启动迟到的下载。"); };
            dialog.ConvertLocalAudio = delegate { conversions++; throw new Exception("取消解析后不得开始转录。"); };
            dialog.ImportTranscribedAudio = delegate { imports++; return MidiEntry("不应保存"); };
            try
            {
                Show(dialog); SearchMusic(dialog, song.Title); Field<Button>(dialog, "songDownload").PerformClick();
                Wait(delegate { return started.WaitOne(0); }, "取消测试的地址解析未开始。");
                Field<Button>(dialog, "songDownload").PerformClick();
                Wait(delegate { return !Field<bool>(dialog, "inlineWorking"); }, "取消解析应立即恢复同页操作。");
                Assert(Field<Label>(dialog, "musicStatus").Text.Contains("已取消") && Field<Button>(dialog, "songDownload").Enabled && dialog.Visible,
                    "取消无需等地址查询完成，也不会锁死窗口。");
                release.Set(); Wait(delegate { return finished.WaitOne(0); }, "后台解析未结束。"); Pump();
                Assert(downloads == 0 && conversions == 0 && imports == 0 && dialog.ChosenScore == null, "解析的迟到结果被取消令牌丢弃。");
                Assert(Object.ReferenceEquals(Field<ListView>(dialog, "musicResults").SelectedItems[0].Tag, song), "取消保留原歌曲和歌手选择。");
            }
            finally { release.Set(); }
        }
    }
    private static void TestInlineRetryConversion()
    {
        WithWave(delegate(string fixture)
        {
            var song = MockSong("失败直接重试987654", "原演唱者", "retry-inline");
            int downloads = 0, resolves = 0, conversions = 0, imports = 0;
            using (var dialog = InlineDialog(song))
            {
                dialog.ResolveSongAudio = delegate { Interlocked.Increment(ref resolves); return "https://fixture.test/audio.mp3"; };
                dialog.DownloadSongAudio = delegate { downloads++; return Task.FromResult(fixture); };
                dialog.ConvertLocalAudio = delegate
                {
                    conversions++;
                    if (conversions == 1)
                    {
                        var failure = new InvalidOperationException("组件安装失败（错误代码 99）。技术堆栈不该呈现。");
                        failure.Data["AudioWorkflowStage"] = "安装";
                        failure.Data["AudioRuntimeFailureKind"] = "Network";
                        failure.Data["InstallerExitCode"] = -1;
                        failure.Data["AudioRuntimeLogPath"] = "C:\\Temp\\private-fixture\\audio-runtime.log";
                        throw failure;
                    }
                    return Task.FromResult(new byte[] { 1, 2, 3 });
                };
                dialog.ImportTranscribedAudio = delegate { imports++; return MidiEntry(song.Title); };
                Show(dialog); SearchMusic(dialog, song.Title); Field<Button>(dialog, "songDownload").PerformClick();
                Wait(delegate { return conversions == 1 && !Field<bool>(dialog, "inlineWorking"); }, "转录首装失败未回到可操作状态。");
                Assert(Field<Button>(dialog, "songDownload").Text == "重试转 MIDI" && File.Exists(fixture), "识别准备失败保留已下载音频并提供直接重试。按钮=" + Field<Button>(dialog, "songDownload").Text + "，状态=" + Field<Label>(dialog, "musicStatus").Text + "，选择=" + Field<ListView>(dialog, "musicResults").SelectedItems.Count);
                Assert(Field<Label>(dialog, "musicStatus").Text.Contains("退出码 -1") && !Field<Label>(dialog, "musicStatus").Text.Contains("技术堆栈"), "保留有意义的退出码与行动说明，隐藏程序堆栈。");
                Assert(Field<Label>(dialog, "musicStatus").Text.Contains("组件下载失败") && Field<Label>(dialog, "musicStatus").Text.Contains("程序文件/data/logs/audio-runtime.log") &&
                    !Field<Label>(dialog, "musicStatus").Text.Contains("private-fixture") && !Field<Label>(dialog, "musicStatus").Text.Contains("99"), "安装原因及固定日志入口正确，结构化退出码优先，绝对路径不暴露。");
                Field<Button>(dialog, "songDownload").PerformClick();
                Wait(delegate { return conversions == 2 && !Field<bool>(dialog, "inlineWorking"); }, "重试转录未完成。");
                Assert(downloads == 1 && resolves == 1 && imports == 1 && Field<Button>(dialog, "songDownload").Text == "立即选用",
                    "再次转换只复用原音频，不重新解析和下载同一歌曲。");
            }
        });
    }
    private static void TestInlineCancelDownload()
    {
        WithWave(delegate(string fixture)
        {
            var song = MockSong("下载取消后重试987654", "保留原版本", "cancel-download");
            int downloads = 0, conversions = 0, imports = 0;
            CancellationToken firstToken = CancellationToken.None;
            using (var dialog = InlineDialog(song))
            {
                dialog.ResolveSongAudio = delegate { return "https://fixture.test/audio.mp3"; };
                dialog.DownloadSongAudio = delegate(string url, string title, Action<string> progress, CancellationToken token)
                {
                    downloads++;
                    if (downloads > 1) return Task.FromResult(fixture);
                    firstToken = token; var pending = new TaskCompletionSource<string>();
                    token.Register(delegate { pending.TrySetCanceled(); }); return pending.Task;
                };
                dialog.ConvertLocalAudio = delegate { conversions++; return Task.FromResult(new byte[] { 1 }); };
                dialog.ImportTranscribedAudio = delegate { imports++; return MidiEntry(song.Title); };
                Show(dialog); SearchMusic(dialog, song.Title); Field<Button>(dialog, "songDownload").PerformClick();
                Wait(delegate { return downloads == 1; }, "可取消的下载没有开始。");
                Field<Button>(dialog, "songDownload").PerformClick();
                Wait(delegate { return !Field<bool>(dialog, "inlineWorking"); }, "取消下载没有恢复页面。");
                Assert(firstToken.IsCancellationRequested && conversions == 0 && imports == 0 && dialog.ChosenScore == null,
                    "取消信号传到下载器，未完成的音频不继续转换或入库。");
                Assert(Field<Button>(dialog, "songDownload").Enabled && Field<Button>(dialog, "songDownload").Text == "下载录音 → MIDI" &&
                    Object.ReferenceEquals(dialog.ConversionSong, song), "取消后保留原版本并恢复下载动作。");
                Field<Button>(dialog, "songDownload").PerformClick();
                Wait(delegate { return imports == 1 && !Field<bool>(dialog, "inlineWorking"); }, "取消后的重新下载未完成。");
                Assert(downloads == 2 && conversions == 1 && Field<Button>(dialog, "songDownload").Text == "立即选用", "用户重试后正常下载与转换各一次。");
            }
        });
    }
    private static void TestInlineChangeSourceAfterSuccess()
    {
        WithWave(delegate(string fixture)
        {
            var song = MockSong("成功后换平台987654", "正确的歌手", "completed-source");
            int imports = 0;
            using (var dialog = InlineDialog(song))
            {
                dialog.ConvertLocalAudio = delegate { return Task.FromResult(new byte[] { 1 }); };
                dialog.ImportTranscribedAudio = delegate { imports++; return MidiEntry(song.Title); };
                Show(dialog); SearchMusic(dialog, song.Title);
                Task pending = dialog.RunInlineConversionAsync(song, fixture);
                Wait(delegate { return pending.IsCompleted; }, "换平台场景的首次转换未完成。");
                Assert(Field<Button>(dialog, "songDownload").Text == "立即选用" && dialog.ChosenScore != null, "原版本完成后提供明确选用状态。");
                Field<ComboBox>(dialog, "musicProvider").SelectedIndex = 1; Pump();
                Assert(!Field<Button>(dialog, "songDownload").Enabled && Field<Button>(dialog, "songDownload").Text == "下载录音 → MIDI" &&
                    dialog.ChosenScore == null && Field<ListView>(dialog, "musicResults").Items.Count == 0, "换来源后清除旧歌曲选用及列表，防止误用前一平台版本。");
                Assert(imports == 1 && Field<Label>(dialog, "musicStatus").Text.Contains("酷狗音乐"), "已保存原曲不重复导入，新平台提示对应酷狗。");
            }
        });
    }
    private static void TestInlineCloseDuringConversion()
    {
        WithWave(delegate(string fixture)
        {
            var song = MockSong("关闭转录987654", "同一歌手", "close-inline");
            int conversions = 0, imports = 0;
            var conversion = new TaskCompletionSource<byte[]>();
            CancellationToken token = CancellationToken.None;
            using (var dialog = InlineDialog(song))
            {
                dialog.ConvertLocalAudio = delegate(string path, string title, Action<string> progress, CancellationToken cancellation)
                { conversions++; token = cancellation; return conversion.Task; };
                dialog.ImportTranscribedAudio = delegate { imports++; return MidiEntry("关闭后不能导入"); };
                Show(dialog); SearchMusic(dialog, song.Title);
                Task pending = dialog.RunInlineConversionAsync(song, fixture);
                Wait(delegate { return conversions == 1; }, "关闭场景的转换未开始。");
                dialog.Close(); Pump();
                Assert(dialog.IsDisposed && token.IsCancellationRequested, "关闭在线窗口取消正在转换的同页任务，不阻止窗口关闭。");
                conversion.SetResult(new byte[] { 1, 2, 3 });
                Wait(delegate { return pending.IsCompleted; }, "关闭后后台转录未正常结束。");
                Assert(!pending.IsFaulted && imports == 0 && dialog.ChosenScore == null, "关闭后迟到的转换结果不保存、不弹窗、不访问已释放控件。");
            }
        });
    }
    private static void TestInlineDisposeDuringConversion()
    {
        WithWave(delegate(string fixture)
        {
            var song = MockSong("释放转录987654", "同一歌手", "dispose-inline");
            int imports = 0;
            var conversion = new TaskCompletionSource<byte[]>();
            CancellationToken token = CancellationToken.None;
            Action<string> report = null;
            Task pending = null;
            using (var dialog = InlineDialog(song))
            {
                dialog.ConvertLocalAudio = delegate(string path, string title, Action<string> progress, CancellationToken cancellation)
                { token = cancellation; report = progress; return conversion.Task; };
                dialog.ImportTranscribedAudio = delegate { imports++; return MidiEntry("释放后不能导入"); };
                try
                {
                    Show(dialog);
                    pending = dialog.RunInlineConversionAsync(song, fixture);
                    var source = Field<CancellationTokenSource>(dialog, "songCancellation");
                    Label status = Field<Label>(dialog, "musicStatus"); int statusChanges = 0;
                    status.TextChanged += delegate { statusChanges++; };
                    status.ForeColorChanged += delegate { statusChanges++; };
                    dialog.Dispose();
                    int changesAfterDispose = statusChanges;
                    Assert(token.IsCancellationRequested && Object.ReferenceEquals(source, Field<CancellationTokenSource>(dialog, "songCancellation")), "直接释放窗口应取消任务，由工作 finally 保留取消源释放权。");
                    Assert(source.Token.IsCancellationRequested && !pending.IsCompleted, "任务尚未收尾时取消源仍须可用。");
                    report("迟到进度不能更新窗口"); Pump();
                    conversion.SetResult(new byte[] { 1, 2, 3 });
                    Wait(delegate { return pending.IsCompleted; }, "释放窗口后后台转录未收尾。");
                    Assert(!pending.IsFaulted && imports == 0 && dialog.ChosenScore == null, "释放后迟到转换结果不得保存或返回成功，工作须正常收尾。");
                    Assert(statusChanges == changesAfterDispose, "释放后迟到转换结果和进度不得修改控件属性。");
                    Assert(Field<CancellationTokenSource>(dialog, "songCancellation") == null, "工作收尾须解除取消源引用。");
                }
                finally
                {
                    dialog.Dispose(); conversion.TrySetResult(new byte[] { 1 });
                    if (pending != null) Wait(delegate { return pending.IsCompleted; }, "释放场景的模拟任务未清理。");
                }
            }
        });
    }
    private static void TestAudioLateConversionAfterClose()
    {
        WithWave(delegate(string fixture)
        {
            foreach (CloseReason reason in new[] { CloseReason.UserClosing, CloseReason.FormOwnerClosing, CloseReason.ApplicationExitCall, CloseReason.WindowsShutDown, CloseReason.None })
            {
                var conversion = new TaskCompletionSource<byte[]>();
                CancellationToken token = CancellationToken.None;
                Action<string> report = null;
                Task pending = null;
                using (var dialog = new AudioMidiDialog("关闭后不得返回 MIDI")
                    { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), Opacity = 0 })
                {
                    dialog.ConvertLocalAudio = delegate(string path, string title, Action<string> progress, CancellationToken cancellation)
                    { token = cancellation; report = progress; return conversion.Task; };
                    try
                    {
                        Field<TextBox>(dialog, "path").Text = fixture; dialog.Show(); Pump();
                        pending = dialog.RunConversionAsync();
                        var source = Field<CancellationTokenSource>(dialog, "cancellation");
                        if (reason == CloseReason.UserClosing)
                        {
                            dialog.Close(); Pump();
                            Assert(!dialog.IsDisposed && dialog.Visible, "单独关闭音频窗口可等待取消收尾。");
                        }
                        else if (reason == CloseReason.None) dialog.Dispose();
                        else
                        {
                            var closing = new FormClosingEventArgs(reason, false);
                            typeof(Form).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(dialog, new object[] { closing });
                            Assert(!closing.Cancel, "主程序退出或系统关闭不能被音频任务阻挡：" + reason);
                            dialog.Dispose();
                        }
                        Label status = Field<Label>(dialog, "status"); int textChanges = 0, colorChanges = 0;
                        string lastStatus = null;
                        status.TextChanged += delegate { textChanges++; lastStatus = status.Text; };
                        status.ForeColorChanged += delegate { colorChanges++; };
                        Assert(token.IsCancellationRequested && Object.ReferenceEquals(source, Field<CancellationTokenSource>(dialog, "cancellation")), "关闭须同步取消并将取消源留到工作 finally：" + reason);
                        Assert(source.Token.IsCancellationRequested && !pending.IsCompleted, "取消后未收尾的转换仍持有可用取消源：" + reason);
                        report("迟到进度不能覆盖关闭状态"); Pump();
                        Assert(textChanges == 0 && colorChanges == 0, "关闭请求后迟到进度不得修改控件属性：" + reason);
                        conversion.SetResult(new byte[] { 1, 2, 3 });
                        Wait(delegate { return pending.IsCompleted; }, "关闭后模拟音频转换未收尾。");
                        Assert(!pending.IsFaulted && dialog.MidiBytes == null && dialog.DialogResult != DialogResult.OK, "关闭后迟到 MIDI 不得返回成功：" + reason);
                        Assert(colorChanges == 0 && (reason == CloseReason.UserClosing ? textChanges == 1 && lastStatus == "已取消转录。" : textChanges == 0), "已关闭窗口不能接收迟到结果，仅单独关闭收尾允许显示取消完成：" + reason);
                        Assert(dialog.IsDisposed && Field<CancellationTokenSource>(dialog, "cancellation") == null, "取消收尾须关闭窗口并解除取消源引用：" + reason);
                    }
                    finally
                    {
                        dialog.Dispose(); conversion.TrySetResult(new byte[] { 1 });
                        if (pending != null) Wait(delegate { return pending.IsCompleted; }, "音频关闭场景的模拟任务未清理。");
                    }
                }
            }
        });
    }
    private static void TestAudioLateDownloadAfterDispose()
    {
        WithWave(delegate(string fixture)
        {
            var download = new TaskCompletionSource<string>();
            CancellationToken token = CancellationToken.None;
            int conversions = 0;
            Task pending = null;
            using (var dialog = new AudioMidiDialog("释放后不得转录")
                { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), Opacity = 0 })
            {
                dialog.DownloadSongAudio = delegate(string url, string title, Action<string> progress, CancellationToken cancellation)
                { token = cancellation; return download.Task; };
                dialog.ConvertLocalAudio = delegate { conversions++; return Task.FromResult(new byte[] { 1 }); };
                try
                {
                    Field<TextBox>(dialog, "audioUrl").Text = "https://example.test/late.wav"; dialog.Show(); Pump();
                    pending = dialog.RunRemoteConversionAsync();
                    TextBox path = Field<TextBox>(dialog, "path"); string original = path.Text;
                    dialog.Dispose();
                    Assert(token.IsCancellationRequested && !pending.IsCompleted, "释放下载窗口须立即取消阻塞下载。");
                    download.SetResult(fixture);
                    Wait(delegate { return pending.IsCompleted; }, "释放后模拟音频下载未收尾。");
                    Assert(!pending.IsFaulted && conversions == 0 && path.Text == original && dialog.MidiBytes == null, "释放后迟到下载不更新路径、不启动转录、不返回 MIDI。");
                    Assert(Field<CancellationTokenSource>(dialog, "cancellation") == null, "下载收尾须解除取消源引用。");
                }
                finally
                {
                    dialog.Dispose(); download.TrySetResult(fixture);
                    if (pending != null) Wait(delegate { return pending.IsCompleted; }, "下载释放场景的模拟任务未清理。");
                }
            }
        });
    }
    private static void TestInlineLocalAudio()
    {
        WithWave(delegate(string fixture)
        {
            int picks = 0, conversions = 0, imports = 0;
            using (var dialog = InlineDialog(null))
            {
                dialog.SelectAudioFile = delegate { picks++; return fixture; };
                dialog.ConvertLocalAudio = delegate(string path, string title, Action<string> progress, CancellationToken token)
                { conversions++; Assert(path == fixture && title == Path.GetFileNameWithoutExtension(fixture), "无线上歌曲时用实际本地文件名转录。"); return Task.FromResult(new byte[] { 1 }); };
                dialog.ImportTranscribedAudio = delegate { imports++; return MidiEntry("本地结果"); };
                dialog.TranscribeAudio = delegate { throw new Exception("同页本地入口不得调用旧第二转录窗。"); };
                Show(dialog); Field<TabControl>(dialog, "tabs").SelectedTab = Field<TabPage>(dialog, "musicTab"); Pump();
                Assert(picks == 0 && conversions == 0, "仅打开在线页不会自动弹文件选择器。");
                FindButton(dialog, "已有音频转 MIDI").PerformClick();
                Wait(delegate { return !Field<bool>(dialog, "inlineWorking"); }, "本地音频同页处理未完成。");
                Assert(picks == 1 && conversions == 1 && imports == 1 && dialog.Visible && Field<Button>(dialog, "songDownload").Text == "立即选用",
                    "用户点击选择本地音频后，在原在线页转换并显示选用按钮。");
                Field<Button>(dialog, "songDownload").PerformClick(); Pump();
                Assert(dialog.DialogResult == DialogResult.OK, "没有线上选歌的本地音频结果也能明确选用返回。");
            }
        });
    }
    private static void TestFriendlyInlineErrors()
    {
        string message = OnlineCatalogDialog.FriendlyWorkflowError(new ArgumentException("请输入普通音频的公开 HTTPS 下载地址。", "url"), "下载");
        Assert(!message.Contains("url") && !message.Contains("HTTPS") && !message.Contains("参数") && message.Contains("官方客户端"), "原生url参数错误转换为无需手填地址的用户操作提示。");
        message = OnlineCatalogDialog.FriendlyWorkflowError(new InvalidOperationException("Python Traceback user secret C:\\private"), "转录");
        Assert(!message.Contains("Traceback") && !message.Contains("secret") && !message.Contains("private") && message.Contains("音频已保留"), "转录错误不泄漏程序堆栈及本机路径。");
    }
    private static void TestRuntimePreparationMessages()
    {
        string[] kinds = { "Policy", "Network", "Permission", "Disk", "Runtime", "Unknown" };
        string[] reasons = { "系统脚本策略", "组件下载失败", "目录没有写入权限", "磁盘空间不足", "模型初始化失败", "组件准备失败" };
        for (int index = 0; index < kinds.Length; index++)
        {
            var error = new InvalidOperationException("Python Traceback: secret https://private.test/path，错误代码 19。");
            error.Data["AudioWorkflowStage"] = "安装";
            error.Data["AudioRuntimeFailureKind"] = kinds[index];
            error.Data["InstallerExitCode"] = -1;
            error.Data["AudioRuntimeLogPath"] = "C:\\Temp\\secret-fixture\\audio-runtime.log";
            string message = OnlineCatalogDialog.FriendlyWorkflowError(error, "转录");
            Assert(message.Contains(reasons[index]) && message.Contains("音频已保留") && message.Contains("重试准备"), "安装失败按真实分类说明原因并保留音频：" + kinds[index]);
            Assert(message.Contains("退出码 -1") && message.Contains("程序文件/data/logs/audio-runtime.log") && !message.Contains("19") &&
                !message.Contains("Traceback") && !message.Contains("secret") && !message.Contains("https://"), "显示结构化退出码与固定日志说明，不公开堆栈、下载URL和临时绝对路径：" + kinds[index]);
            var song = MockSong("测试歌曲", "原演唱者", "runtime-message-layout");
            using (var dialog = InlineDialog(song))
            {
                typeof(OnlineCatalogDialog).GetMethod("SetSongStatus", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(dialog, new object[] { song, message, Theme.Warm });
                Label label = Field<Label>(dialog, "musicStatus");
                Size measured = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                Assert(measured.Height <= label.Height, "具体安装说明和日志位置完整显示：" + kinds[index]);
                Assert(!label.Bounds.IntersectsWith(Field<ProgressBar>(dialog, "songProgress").Bounds) &&
                    !Field<ListView>(dialog, "musicResults").Bounds.IntersectsWith(Field<Label>(dialog, "localHint").Bounds),
                    "展开具体原因时进度条、歌曲列表和底部匹配说明仍无重叠：" + kinds[index]);
            }
        }
        var timeout = new TimeoutException("音频组件下载超过 30 分钟。"); timeout.Data["AudioWorkflowStage"] = "安装";
        string result = OnlineCatalogDialog.FriendlyWorkflowError(timeout, "转录");
        Assert(result.Contains("组件准备超时") && !result.Contains("歌曲") && !result.Contains("截取"), "安装等待超时不再误说歌曲转录太长。");
        var missing = new FileNotFoundException("安装脚本不存在。", "C:\\private\\setup.ps1"); missing.Data["AudioWorkflowStage"] = "安装";
        result = OnlineCatalogDialog.FriendlyWorkflowError(missing, "转录");
        Assert(result.Contains("安装文件缺失") && result.Contains("完整软件文件夹") && !result.Contains("音频文件不存在") && !result.Contains("private"), "缺安装脚本与缺歌曲文件准确区分。");
        var denied = new UnauthorizedAccessException("目录无法写入。"); denied.Data["AudioWorkflowStage"] = "安装";
        Assert(OnlineCatalogDialog.FriendlyWorkflowError(denied, "转录").Contains("目录没有写入权限"), "安装目录权限错误不误说录音暂时不能读取。");
        var early = new InvalidOperationException("音频组件安装程序提前退出（错误代码 -1，未返回原因）。"); early.Data["AudioWorkflowStage"] = "安装";
        result = OnlineCatalogDialog.FriendlyWorkflowError(early, "转录");
        Assert(result.Contains("退出码 -1") && !result.Contains("logs/"), "没有结构化退出码时兼容错误代码文本；没有日志时不虚报已记录。");
    }
    private static OnlineCatalogDialog InlineDialog(OnlineSong song)
    {
        return Dialog(new List<SongEntry>(), EmptyFree, song == null ? (Func<string, int, List<OnlineSong>>)EmptyMusic : SongSearch(song),
            NoConvert, NoChineseImport, NoChineseDownload, NoJianpu);
    }
    private static void WithWave(Action<string> action)
    {
        string fixture = Path.Combine(AppPaths.ContentDirectory, "inline-audio-" + Guid.NewGuid().ToString("N") + ".wav");
        byte[] header = new byte[44];
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0); System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
        File.WriteAllBytes(fixture, header);
        try { action(fixture); }
        finally { if (File.Exists(fixture)) File.Delete(fixture); }
    }

    private static void CheckChildrenBounds(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            Assert(parent.ClientRectangle.Contains(child.Bounds), "控件完全位于窗口内部：" + child.Text);
            foreach (Control other in parent.Controls)
                if (other != child && parent.Controls.IndexOf(other) > parent.Controls.IndexOf(child))
                    Assert(!child.Bounds.IntersectsWith(other.Bounds), "控件互不遮挡：" + child.Text + " / " + other.Text);
        }
    }

    private static OnlineCatalogDialog Dialog(IList<SongEntry> songs, Func<string, int, FreeScorePage> free,
        Func<string, int, List<OnlineSong>> music, Func<SongEntry, SongEntry> convert,
        Func<ChineseScore, byte[], SongEntry> import, Func<ChineseScore, byte[]> download,
        Func<string, string, SongEntry> jianpu)
    {
        return new OnlineCatalogDialog("", songs, delegate { throw new Exception("此场景不得弹出本地MIDI导入。"); }, jianpu,
            delegate { throw new Exception("此场景不得导入Mutopia谱。"); }, convert, import, free,
            delegate { throw new Exception("此场景不得下载Mutopia谱。"); }, music, download);
    }
    private static Func<string, int, List<OnlineSong>> SongSearch(string title, string artist)
    { return SongSearch(MockSong(title, artist, "mock")); }
    private static Func<string, int, List<OnlineSong>> SongSearch(OnlineSong song)
    { return delegate { return new List<OnlineSong> { song }; }; }
    private static OnlineSong MockSong(string title, string artist, string id)
    { return new OnlineSong { Title = title, Artist = artist, Id = id, Provider = "QQ音乐", FreeDownload = true }; }
    private static FreeScorePage EmptyFree(string term, int page) { return new FreeScorePage { AppliedQuery = term }; }
    private static List<OnlineSong> EmptyMusic(string term, int page) { return new List<OnlineSong>(); }
    private static SongEntry NoConvert(SongEntry entry) { throw new Exception("此场景不得转换本地谱。"); }
    private static SongEntry NoChineseImport(ChineseScore score, byte[] bytes) { throw new Exception("此场景不得导入中文谱。"); }
    private static byte[] NoChineseDownload(ChineseScore score) { throw new Exception("此场景不得下载中文谱。"); }
    private static SongEntry NoJianpu(string title, string artist) { throw new Exception("此场景不得新建简谱。"); }
    private static SongEntry MidiEntry(string title) { return new SongEntry { Title = title, FileName = "mock-ui-workflow.mid", Chinese = true }; }
    private static void Show(OnlineCatalogDialog dialog) { dialog.Show(); Pump(); }
    private static void SearchMusic(OnlineCatalogDialog dialog, string term)
    {
        Field<TabControl>(dialog, "tabs").SelectedTab = Field<TabPage>(dialog, "musicTab");
        Field<TextBox>(dialog, "musicQuery").Text = term;
        Field<Button>(dialog, "musicGo").PerformClick();
        Wait(delegate { return Field<Button>(dialog, "musicGo").Enabled && Field<ListView>(dialog, "musicResults").Items.Count == 1; }, "模拟在线歌曲搜索未完成。");
        Select(Field<ListView>(dialog, "musicResults"), 0); Pump();
    }
    private static T Field<T>(object target, string name)
    { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    private static Button FindButton(Control root, string text)
    {
        foreach (Control child in root.Controls)
        {
            Button match = child as Button;
            if (match != null && match.Text == text) return match;
            if (child.HasChildren) { match = FindButton(child, text); if (match != null) return match; }
        }
        return null;
    }
    private static void Select(ListView list, int index)
    {
        IntPtr handle = list.Handle; list.Focus();
        foreach (ListViewItem item in list.Items) item.Selected = false;
        list.Items[index].Selected = true; list.Items[index].Focused = true;
    }
    private static void Pump() { Application.DoEvents(); }
    private static DialogResult RunModal(Form dialog, Action action)
    {
        Exception failure = null;
        bool expired = false;
        using (var watchdog = new System.Windows.Forms.Timer { Interval = 4800 })
        {
            watchdog.Tick += delegate { expired = true; dialog.Dispose(); };
            dialog.Shown += delegate
            {
                dialog.BeginInvoke(new Action(delegate
                {
                    try { action(); }
                    catch (Exception error) { failure = error; dialog.Dispose(); }
                }));
            };
            watchdog.Start();
            DialogResult result = dialog.ShowDialog();
            watchdog.Stop();
            if (failure != null) throw failure;
            if (expired) throw new Exception("音频导入后模态窗口未正常关闭。");
            return result;
        }
    }
    private static void Wait(Func<bool> condition, string message)
    {
        Stopwatch timer = Stopwatch.StartNew();
        for (;;)
        {
            Pump(); if (condition()) return;
            if (timer.ElapsedMilliseconds > 4000) throw new Exception(message);
            Thread.Sleep(5);
        }
    }
    private static void Assert(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static void Case(string name, Action action)
    {
        try { action(); }
        catch (Exception error) { throw new Exception(name + "：" + error.Message, error); }
    }
}
