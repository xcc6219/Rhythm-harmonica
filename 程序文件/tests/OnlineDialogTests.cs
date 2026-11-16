using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using HarmonicaStudio;

public static class OnlineDialogTests
{
    private static int checks;

    public static int Run()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            int result = 0;
            Exception failure = null;
            Thread thread = new Thread(delegate()
            {
                try { result = RunOnSta(); }
                catch (Exception error) { failure = error; }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(45000)) throw new Exception("在线窗口测试超时。");
            if (failure != null) throw new Exception("在线窗口测试失败。", failure);
            return result;
        }
        return RunOnSta();
    }

    private static int RunOnSta()
    {
        checks = 0;
        RunCase("关键词与曲名", TestKeywordsAndExactTitles);
        RunCase("主旋律MIDI内容验证", TestMelodyPayloadValidation);
        RunCase("搜歌默认页与备用页懒加载", TestMusicDefaultAndLazyFree);
        RunCase("登录确认说明与可读布局", TestLoginConfirmationMessages);
        RunCase("未选歌曲时的账号状态返回", TestAccountStatusWithoutSong);
        RunCase("免费分页", TestFreePagingAndQuery);
        RunCase("歌曲版本选择", TestMusicSelectionAndConfirmation);
        RunCase("检索错误恢复", TestSearchFailureAndRecovery);
        RunCase("重复Enter", TestEnterDoesNotQueueSearches);
        RunCase("下载谱选择与许可", TestDownloadChoiceAndAttribution);
        RunCase("下载错误恢复", TestDownloadFailureRestoresPaging);
        RunCase("检索时关闭", TestCloseDuringSearch);
        RunCase("主窗口退出或释放后拒绝迟到免费谱", TestLateDownloadAfterOwnerCloseOrDispose);
        Console.WriteLine("在线窗口测试通过，共 " + checks + " 项断言（模拟网络，未发送键鼠输入）。");
        return checks;
    }

    private static void TestKeywordsAndExactTitles()
    {
        Assert(OnlineCatalogDialog.FreeKeyword("贝多芬") == "Beethoven", "作曲家中文关键词应转换。");
        Assert(OnlineCatalogDialog.FreeKeyword("钢琴") == "Piano", "乐器中文关键词应转换。");
        Assert(OnlineCatalogDialog.FreeKeyword("Ode to Joy") == "Ode to Joy", "英文曲名应保持原词。");
        Assert(OnlineCatalogDialog.FreeKeyword("欢乐颂") == "Ode to Joy", "界面关键词应使用已实测的欢乐颂查询。");
        SongEntry shortTitle = new SongEntry { Title = "夜曲" };
        SongEntry longerTitle = new SongEntry { Title = "小夜曲" };
        SongEntry plain = new SongEntry { Title = "小星星" };
        SongEntry version = new SongEntry { Title = "小星星（口琴版）" };
        List<SongEntry> library = new List<SongEntry> { longerTitle, shortTitle, plain, version };
        List<SongEntry> matches = OnlineCatalogDialog.FindLocalMatches(library, "夜曲");
        Assert(matches.Count == 1 && Object.ReferenceEquals(matches[0], shortTitle), "夜曲不能误匹配小夜曲。");
        matches = OnlineCatalogDialog.FindLocalMatches(library, "小星星(伴奏)");
        Assert(matches.Count == 2 && Object.ReferenceEquals(matches[0], plain), "常用版本后缀应匹配对应曲名。");
        Assert(OnlineCatalogDialog.FindLocalMatches(library, "小星星(其他曲名)").Count == 0, "未知括号内容不得擅自剥除。");
        Assert(OnlineCatalogDialog.FindLocalMatches(library, "小星星(其他伴奏版本)").Count == 0, "未知标签仅含伴奏词也不能视为已知版本。");
        SongEntry generated = new SongEntry { Title = "父亲 · 连续旋律版" };
        Assert(OnlineCatalogDialog.FindLocalMatches(new[] { generated }, "父亲").Single() == generated, "库内查谱识别本软件明确生成的版本尾缀。");
    }

    private static void TestMelodyPayloadValidation()
    {
        var notes = new[] { new NoteEvent { Pitch = 67, Start = .5, Duration = 1, Velocity = 90 } };
        byte[] original = MidiWriter.ToBytes(notes, "MELODY"), copy = (byte[])original.Clone();
        Assert(Object.ReferenceEquals(OnlineCatalogDialog.ValidateMelodyDownload(original), original) && original.SequenceEqual(copy),
            "真实非空MELODY经过完整MIDI解析后保留原字节，不重新编造旋律。");
        Assert(OnlineCatalogDialog.IsMelodyTrackName("MELODY · 通道 1") && OnlineCatalogDialog.IsMelodyTrackName("MELODY · 通道 16"),
            "接受真实POP909角色名经过现有MIDI读取器追加的通道后缀，不误拒官方主谱。");
        Assert(!OnlineCatalogDialog.IsMelodyTrackName("MELODY imitation · 通道 1") &&
            !OnlineCatalogDialog.IsMelodyTrackName("MELODY · 通道 17") && !OnlineCatalogDialog.IsMelodyTrackName("BRIDGE · 通道 1"),
            "伪似轨名、非法通道和次旋律不得冒充MELODY角色。");
        foreach (byte[] payload in new[] { (byte[])null, new byte[0], new byte[] { 1, 2, 3 },
            MidiWriter.ToBytes(notes, "PIANO"), MidiWriter.ToBytes(notes, "MELODY imitation"), MidiWriter.ToBytes(notes, "MELODY · 通道 1") })
        {
            bool rejected = false;
            try { OnlineCatalogDialog.ValidateMelodyDownload(payload); }
            catch (System.IO.InvalidDataException) { rejected = true; }
            catch (FormatException) { rejected = true; }
            Assert(rejected, "空内容、坏MIDI、仅伴奏或伪似轨名不能当官方MELODY主谱。");
        }
    }

    private static void TestMusicDefaultAndLazyFree()
    {
        int freeCalls = 0, musicCalls = 0;
        using (OnlineCatalogDialog dialog = MakeDialog(new List<SongEntry>(),
            delegate(string term, int page) { Interlocked.Increment(ref freeCalls); return EmptyFree(term, page); },
            delegate(string term, int page) { Interlocked.Increment(ref musicCalls); return EmptyMusic(term, page); },
            NoDownload, NoImport, false))
        {
            TabControl tabs = Field<TabControl>(dialog, "tabs");
            TabPage music = Field<TabPage>(dialog, "musicTab"), free = Field<TabPage>(dialog, "freeTab");
            Assert(tabs.TabPages[0] == music && tabs.TabPages[tabs.TabPages.Count - 1] == free && tabs.SelectedTab == music,
                "双页入口默认打开QQ/酷狗搜歌，备用免费谱置于最后。");
            Show(dialog);
            Assert(freeCalls == 0 && musicCalls == 0, "空曲名默认搜歌页不提前加载备用曲库。");
            tabs.SelectedTab = free;
            WaitUntil(delegate { return freeCalls == 1 && Field<Button>(dialog, "freeGo").Enabled; }, "备用免费谱首次切入未加载。");
            tabs.SelectedTab = music; Pump(); tabs.SelectedTab = free; Pump();
            Assert(freeCalls == 1 && musicCalls == 0, "备用页由页面引用加载一次，不触发默认歌曲检索。");
        }
    }

    private static void TestLoginConfirmationMessages()
    {
        string missing = QqLoginDialog.AccountConfirmationMessage(null);
        Assert(missing.Contains("尚未取得可确认") && missing.Contains("官方页已显示账号头像") && !missing.Contains("尚未登录"),
            "读取不到可确认会话不直接否认官方网页中的登录状态。");
        QqAccountSession session = QqAccountSession.FromBrowserCookies(new[]
        {
            new QqMusicCookie { Name = "uin", Value = "12345678", Domain = "y.qq.com", IsSession = true },
            new QqMusicCookie { Name = "qm_keyst", Value = "ui-session-secret-do-not-print", Domain = "y.qq.com", IsSession = true }
        });
        string unconfirmed = QqLoginDialog.AccountConfirmationMessage(session);
        Assert(unconfirmed.Contains("账号接口尚未确认") && !unconfirmed.Contains("会话已过期") && !unconfirmed.Contains(session.MusicKey) && !session.IsUsable,
            "未分类的确认失败不假称过期，不展示票据，也不启用未确认账号。");
        var messages = new List<string> { missing, unconfirmed };
        foreach (var expected in new Dictionary<string, string>
        {
            { "Expired", "会话已失效" }, { "Network", "网络" }, { "Format", "格式暂不兼容" },
            { "Identity", "身份" }, { "Permission", "接口未接受" }, { "Unknown", "接口尚未确认" }
        })
        {
            session.MarkValidationFailure(expected.Key);
            string message = QqLoginDialog.AccountConfirmationMessage(session);
            Assert(message.Contains(expected.Value) && !message.Contains(session.MusicKey) && !message.Contains(session.Uin),
                "账号确认说明按实际失败分类给出原因，不展示会话票据或账号编号。");
            if (expected.Key == "Format" || expected.Key == "Network" || expected.Key == "Permission")
                Assert(!message.Contains("重新登录") && !message.Contains("会员"), "接口、网络或账号确认拒绝不误导为重新登录或购买会员。");
            messages.Add(message);
        }
        foreach (Exception error in new Exception[]
        {
            new WebException("https://fixture.test/private?token=ui-session-secret-do-not-print"),
            new TimeoutException("ui-session-secret-do-not-print"), new System.IO.InvalidDataException("ui-session-secret-do-not-print"),
            new FormatException("ui-session-secret-do-not-print"), new InvalidOperationException("ui-session-secret-do-not-print")
        })
        {
            string message = QqLoginDialog.AccountConfirmationExceptionMessage(error);
            string keyword = error is WebException || error is TimeoutException ? "网络" :
                error is System.IO.InvalidDataException || error is FormatException ? "格式暂不兼容" : "尚未在本软件中完成确认";
            Assert(message.Contains(keyword) && !message.Contains("ui-session-secret-do-not-print") && !message.Contains("https://"),
                "异常提示保留实际类别，原始响应与敏感URL不会显示在登录窗口。");
            messages.Add(message);
        }
        using (var login = new QqLoginDialog())
        {
            Label status = Field<Label>(login, "status");
            login.Size = login.MinimumSize; login.PerformLayout();
            foreach (string message in messages)
            {
                Size required = TextRenderer.MeasureText(message, status.Font, new Size(status.Width, Int32.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                Assert(required.Height <= status.Height, "最小登录窗口也能完整显示确认失败说明，不裁切多行文字。");
            }
            Panel page = login.Controls.OfType<Panel>().Single();
            Assert(status.Bottom < page.Top && page.Bottom < login.Controls.OfType<Label>().Single(label => label.Text.StartsWith("密码和扫码")).Top,
                "登录状态、官方网页和说明区域不重叠。");
        }
    }

    private static void TestAccountStatusWithoutSong()
    {
        using (OnlineCatalogDialog dialog = MakeDialog(new List<SongEntry>(), EmptyFree, EmptyMusic, NoDownload, NoImport, false))
        {
            const string message = "当前使用匿名搜索。可在软件内登录 QQ / 微信，再由官方接口确认账号。";
            MethodInfo update = typeof(OnlineCatalogDialog).GetMethod("SetSongStatus", BindingFlags.Instance | BindingFlags.NonPublic);
            update.Invoke(dialog, new object[] { null, message, Theme.Muted });
            Label status = Field<Label>(dialog, "musicStatus");
            Assert(status.Text == message && dialog.ChosenScore == null,
                "空曲库首次打开账号页后返回也能显示状态，不依赖曾选歌或转录，不出现空标题行。");
            Assert(status.Bottom < Field<ProgressBar>(dialog, "songProgress").Top &&
                Field<ListView>(dialog, "musicResults").Bottom < Field<Label>(dialog, "localHint").Top,
                "没有歌曲标题时状态、进度和检索结果仍保持分离。");
        }
    }

    private static void TestFreePagingAndQuery()
    {
        List<string> requests = new List<string>();
        FreeScore one = MakeScore("Ode to Joy", "A");
        FreeScore two = MakeScore("第二页", "B");
        Func<string, int, FreeScorePage> search = delegate(string term, int page)
        {
            lock (requests) requests.Add(term + "/" + page);
            return Page(page == 1 ? one : two, page == 1, page == 1 ? 9 : 0, term);
        };
        using (OnlineCatalogDialog dialog = MakeDialog(new List<SongEntry>(), search, EmptyMusic, NoDownload, NoImport))
        {
            Field<TextBox>(dialog, "freeQuery").Text = "欢乐颂";
            Show(dialog);
            WaitUntil(delegate { return Field<Button>(dialog, "freeGo").Enabled && Field<ListView>(dialog, "freeResults").Items.Count == 1; }, "免费第一页未完成。");
            Assert(requests[0] == "Ode to Joy/1", "实际搜索应转发标准中文曲名关键词。");
            Assert(Field<Button>(dialog, "freeNext").Enabled && !Field<Button>(dialog, "freePrevious").Enabled, "过滤九个ZIP后仍应根据源HasNext翻页。");
            Assert(Field<Label>(dialog, "freeStatus").Text.Contains("9 个多文件包"), "跳过压缩包应向用户说明。");
            Assert(dialog.ChosenScore == null && Field<Button>(dialog, "download").Enabled, "列出免费谱不能直接替用户导入。");
            Field<TextBox>(dialog, "freeQuery").Text = "尚未提交的新关键词";
            Field<Button>(dialog, "freeNext").PerformClick();
            WaitUntil(delegate { return Field<Button>(dialog, "freeGo").Enabled && Field<ListView>(dialog, "freeResults").Items[0].Text == "第二页"; }, "免费第二页未完成。");
            Assert(requests[1] == "Ode to Joy/2", "翻页须沿用已提交关键词，避免页码对应不同搜索。");
            Assert(!Field<Button>(dialog, "freeNext").Enabled && Field<Button>(dialog, "freePrevious").Enabled, "末页应禁下一页并保留上一页。");
            Field<Button>(dialog, "freePrevious").PerformClick();
            WaitUntil(delegate { return Field<Button>(dialog, "freeGo").Enabled && Field<ListView>(dialog, "freeResults").Items[0].Text == "Ode to Joy"; }, "免费返回上一页未完成。");
            Assert(requests[2] == "Ode to Joy/1", "上一页不能悄悄使用未提交的文本。");
        }
    }

    private static void TestMusicSelectionAndConfirmation()
    {
        SongEntry first = new SongEntry { Title = "小星星", Subtitle = "版本一" };
        SongEntry second = new SongEntry { Title = "小星星（口琴版）", Subtitle = "版本二" };
        List<SongEntry> library = new List<SongEntry> { new SongEntry { Title = "小夜曲" }, first, second };
        Func<string, int, List<OnlineSong>> music = delegate
        {
            return new List<OnlineSong>
            {
                new OnlineSong { Title = "小星星 (伴奏)", Artist = "歌手甲", Id = "A" },
                new OnlineSong { Title = "夜曲", Artist = "歌手乙", Id = "B" }
            };
        };
        using (OnlineCatalogDialog dialog = MakeDialog(library, EmptyFree, music, NoDownload, NoImport))
        {
            Show(dialog); WaitForInitialFree(dialog);
            Field<TabControl>(dialog, "tabs").SelectedTab = Field<TabPage>(dialog, "musicTab");
            Field<TextBox>(dialog, "musicQuery").Text = "小星星";
            Field<Button>(dialog, "musicGo").PerformClick();
            WaitUntil(delegate { return Field<Button>(dialog, "musicGo").Enabled && Field<ListView>(dialog, "musicResults").Items.Count == 2; }, "歌曲搜索未完成。");
            ListView results = Field<ListView>(dialog, "musicResults");
            ComboBox choices = Field<ComboBox>(dialog, "localScores");
            Select(results, 0); Pump();
            try { WaitUntil(delegate { return choices.Items.Count == 2; }, "小星星曲谱未刷新。"); }
            catch (Exception error) { throw new Exception(error.Message + " " + MatchStatus(dialog)); }
            Assert(dialog.ChosenScore == null, "多份本地谱须待用户确认，不能自动返回。");
            Select(results, 1); Pump();
            WaitUntil(delegate { return choices.Items.Count == 0 && !Field<Button>(dialog, "choose").Enabled; }, "夜曲选择后旧谱未清空。");
            Assert(choices.Items.Count == 0 && !Field<Button>(dialog, "choose").Enabled,
                "选夜曲时不可使用小夜曲的谱（选中项=" + results.SelectedItems.Count +
                "，曲名=" + (results.SelectedItems.Count > 0 ? results.SelectedItems[0].Text : "无") +
                "，乐谱数=" + choices.Items.Count + "）。");
            Select(results, 0); Pump();
            WaitUntil(delegate { return choices.Items.Count == 2; }, "重新选择小星星未刷新曲谱。");
            choices.SelectedIndex = 1;
            Assert(dialog.ChosenScore == null, "切换曲谱选择仍需点击确认。");
            Field<Button>(dialog, "choose").PerformClick(); Pump();
            Assert(Object.ReferenceEquals(dialog.ChosenScore, second) && dialog.DialogResult == DialogResult.OK, "确认须返回用户所选的第二份谱。");
        }
    }

    private static void TestSearchFailureAndRecovery()
    {
        int freeCalls = 0, musicCalls = 0;
        Func<string, int, FreeScorePage> free = delegate(string term, int page)
        {
            if (Interlocked.Increment(ref freeCalls) == 1) throw new InvalidOperationException("模拟曲库断网");
            return Page(MakeScore("恢复的谱", "R"), false, 0, term);
        };
        Func<string, int, List<OnlineSong>> music = delegate
        {
            if (Interlocked.Increment(ref musicCalls) == 1) throw new InvalidOperationException("模拟歌曲断网");
            return new List<OnlineSong> { new OnlineSong { Title = "恢复的歌", Id = "R" } };
        };
        using (OnlineCatalogDialog dialog = MakeDialog(new List<SongEntry>(), free, music, NoDownload, NoImport))
        {
            Show(dialog);
            WaitUntil(delegate { return Field<Button>(dialog, "freeGo").Enabled && Field<Label>(dialog, "freeStatus").Text.Contains("模拟曲库断网"); }, "曲库错误未显示。");
            Assert(!Field<Button>(dialog, "download").Enabled && Field<ListView>(dialog, "freeResults").Items.Count == 0, "曲库失败须清空不可用结果。");
            Field<Button>(dialog, "freeGo").PerformClick();
            WaitUntil(delegate { return Field<Button>(dialog, "freeGo").Enabled && Field<ListView>(dialog, "freeResults").Items.Count == 1; }, "曲库重试未恢复。");
            Select(Field<ListView>(dialog, "freeResults"), 0);
            WaitUntil(delegate { return Field<Button>(dialog, "download").Enabled; }, "重试后所选乐谱不可下载。");
            Assert(Field<Button>(dialog, "download").Enabled, "曲库错误后应能重试并选择下载。");
            Field<TabControl>(dialog, "tabs").SelectedTab = Field<TabPage>(dialog, "musicTab");
            Field<TextBox>(dialog, "musicQuery").Text = "恢复的歌";
            Field<Button>(dialog, "musicGo").PerformClick();
            WaitUntil(delegate { return Field<Button>(dialog, "musicGo").Enabled && Field<Label>(dialog, "musicStatus").Text.Contains("模拟歌曲断网"); }, "歌曲错误未显示。");
            Assert(!Field<Button>(dialog, "choose").Enabled && Field<ListView>(dialog, "musicResults").Items.Count == 0, "歌曲失败不得保留旧的可确认项。");
            Field<Button>(dialog, "musicGo").PerformClick();
            WaitUntil(delegate { return Field<Button>(dialog, "musicGo").Enabled && Field<ListView>(dialog, "musicResults").Items.Count == 1; }, "歌曲重试未恢复。");
            Assert(freeCalls == 2 && musicCalls == 2, "两类失败恢复只能各触发一次明确重试。");
        }
    }

    private static void TestEnterDoesNotQueueSearches()
    {
        using (Gate freeGate = new Gate())
        using (Gate musicGate = new Gate())
        using (OnlineCatalogDialog dialog = MakeDialog(new List<SongEntry>(),
            delegate(string term, int page) { freeGate.Wait(); return Page(MakeScore("测试谱", "G"), false, 0, term); },
            delegate { musicGate.Wait(); return new List<OnlineSong> { new OnlineSong { Title = "测试歌", Id = "G" } }; },
            NoDownload, NoImport))
        {
            try
            {
                Show(dialog); WaitUntil(delegate { return freeGate.Started.WaitOne(0); }, "免费模拟请求未开始。");
                TextBox freeText = Field<TextBox>(dialog, "freeQuery");
                for (int i = 0; i < 15; i++) Enter(freeText);
                Assert(freeGate.Calls == 1 && !Field<Button>(dialog, "freeGo").Enabled, "免费搜索忙时Enter不得堆积后台请求。");
                freeGate.Release.Set(); WaitForInitialFree(dialog);
                Field<TabControl>(dialog, "tabs").SelectedTab = Field<TabPage>(dialog, "musicTab");
                TextBox musicText = Field<TextBox>(dialog, "musicQuery"); musicText.Text = "测试歌";
                Enter(musicText); WaitUntil(delegate { return musicGate.Started.WaitOne(0); }, "歌曲模拟请求未开始。");
                for (int i = 0; i < 15; i++) Enter(musicText);
                Assert(musicGate.Calls == 1 && !Field<Button>(dialog, "musicGo").Enabled, "歌曲搜索忙时Enter不得堆积后台请求。");
                musicGate.Release.Set();
                WaitUntil(delegate { return Field<Button>(dialog, "musicGo").Enabled && Field<ListView>(dialog, "musicResults").Items.Count == 1; }, "歌曲模拟请求未完成。");
                Assert(freeGate.Calls == 1 && musicGate.Calls == 1, "完成后不得补发之前按Enter的请求。");
            }
            finally { freeGate.Release.Set(); musicGate.Release.Set(); Pump(); }
        }
    }

    private static void TestDownloadChoiceAndAttribution()
    {
        FreeScore first = MakeScore("第一份免费谱", "A"), second = MakeScore("第二份免费谱", "B");
        FreeScore received = null, downloadReceived = null;
        byte[] payload = new byte[] { 7, 8, 9 };
        int imported = 0;
        SongEntry saved = null;
        using (Gate gate = new Gate())
        using (OnlineCatalogDialog dialog = MakeDialog(new List<SongEntry>(),
            delegate(string term, int page) { return new FreeScorePage { AppliedQuery = term, HasNext = true, Items = new List<FreeScore> { first, second } }; },
            EmptyMusic,
            delegate(FreeScore score) { downloadReceived = score; gate.Wait(); return payload; },
            delegate(FreeScore score, byte[] bytes)
            {
                imported++; received = score;
                Assert(Object.ReferenceEquals(bytes, payload), "导入回调须收到同一个下载结果。");
                return saved = new SongEntry { Title = score.Title, SourceUrl = score.InfoUrl, License = score.License,
                    Attribution = score.Attribution, SourceEdition = score.SourceEdition, LicenseUrl = score.LicenseUrl };
            }))
        {
            try
            {
                Show(dialog); WaitForInitialFree(dialog);
                ListView results = Field<ListView>(dialog, "freeResults"); Select(results, 1); Pump();
                Assert(dialog.ChosenScore == null && imported == 0, "多结果选择后须显式下载确认。");
                Field<Button>(dialog, "download").PerformClick();
                WaitUntil(delegate { return gate.Started.WaitOne(0); }, "下载模拟请求未开始。");
                Assert(!Field<TabControl>(dialog, "tabs").Enabled && !results.Enabled && !Field<Button>(dialog, "download").Enabled, "下载时须禁切页、选谱及重复下载。");
                dialog.Close(); Pump();
                Assert(!dialog.IsDisposed && dialog.Visible && dialog.ChosenScore == null, "下载处理中关闭必须等待导入完成。");
                // Even if a programmatic selection update occurs, the in-flight
                // request must retain the exact score the user confirmed.
                Select(results, 0); Enter(Field<TextBox>(dialog, "freeQuery"));
                gate.Release.Set();
                WaitUntil(delegate { return dialog.DialogResult == DialogResult.OK; }, "下载导入未完成。");
                Assert(imported == 1 && Object.ReferenceEquals(received, second) && Object.ReferenceEquals(downloadReceived, second), "下载完成只能导入最初确认的第二份谱一次。");
                Assert(Object.ReferenceEquals(dialog.ChosenScore, saved) && saved.Title == second.Title, "关闭尝试之后仍须返回唯一正确谱。");
                Assert(saved.License == second.License && saved.Attribution == second.Attribution &&
                    saved.SourceEdition == second.SourceEdition && saved.LicenseUrl == second.LicenseUrl && saved.SourceUrl == second.InfoUrl,
                    "许可、署名、版本及来源须完整传递回调。");
            }
            finally { gate.Release.Set(); Pump(); }
        }
    }

    private static void TestDownloadFailureRestoresPaging()
    {
        int imported = 0;
        using (Gate gate = new Gate())
        using (OnlineCatalogDialog dialog = MakeDialog(new List<SongEntry>(),
            delegate(string term, int page) { return Page(MakeScore("可重试的谱", "F"), page == 1, 0, term); }, EmptyMusic,
            delegate { gate.Wait(); throw new InvalidOperationException("模拟下载失败"); },
            delegate { imported++; return new SongEntry(); }))
        {
            try
            {
                Show(dialog); WaitForInitialFree(dialog); Field<Button>(dialog, "download").PerformClick();
                WaitUntil(delegate { return gate.Started.WaitOne(0); }, "失败下载未开始。");
                Assert(!Field<Button>(dialog, "freeNext").Enabled, "下载处理中须暂停翻页。");
                gate.Release.Set();
                try
                {
                    WaitUntil(delegate { return Field<TabControl>(dialog, "tabs").Enabled &&
                        Field<Button>(dialog, "freeGo").Enabled && Field<Button>(dialog, "freeNext").Enabled &&
                        Field<Label>(dialog, "freeStatus").Text.Contains("模拟下载失败"); }, "下载失败状态未恢复。");
                }
                catch (Exception error)
                {
                    throw new Exception(error.Message + " tabs=" + Field<TabControl>(dialog, "tabs").Enabled +
                        " go=" + Field<Button>(dialog, "freeGo").Enabled + " next=" + Field<Button>(dialog, "freeNext").Enabled +
                        " hasNext=" + Field<object>(dialog, "freeHasNext") + " busy=" + Field<object>(dialog, "downloading"));
                }
                Assert(imported == 0 && dialog.ChosenScore == null, "下载失败不能触发导入或选择返回。");
                Assert(Field<Button>(dialog, "freeNext").Enabled && Field<Button>(dialog, "freeGo").Enabled, "失败后应恢复源HasNext和搜索按钮。");
                Select(Field<ListView>(dialog, "freeResults"), 0); Pump();
                Assert(Field<Button>(dialog, "download").Enabled, "失败后重新选择应能再次下载。");
                Field<Button>(dialog, "freeNext").PerformClick();
                WaitUntil(delegate { return Field<Button>(dialog, "freeGo").Enabled && Field<Button>(dialog, "freePrevious").Enabled; }, "下载失败后翻页未恢复。");
                Assert(!Field<Button>(dialog, "freeNext").Enabled, "重试翻页后仍应遵循末页状态。");
            }
            finally { gate.Release.Set(); Pump(); }
        }
    }

    private static void TestCloseDuringSearch()
    {
        int imports = 0;
        using (Gate gate = new Gate())
        using (OnlineCatalogDialog dialog = MakeDialog(new List<SongEntry>(),
            delegate(string term, int page) { gate.Wait(); return Page(MakeScore("关闭后的旧结果", "C"), false, 0, term); },
            EmptyMusic, NoDownload, delegate { imports++; return new SongEntry(); }))
        {
            try
            {
                Show(dialog); WaitUntil(delegate { return gate.Started.WaitOne(0); }, "关闭场景模拟搜索未开始。");
                dialog.Close(); Pump(); Assert(dialog.IsDisposed, "仅检索期间允许关闭窗口。");
                gate.Release.Set();
                WaitUntil(delegate { return !(bool)Field<object>(dialog, "searchingFree"); }, "关闭后旧检索未退出。");
                Assert(imports == 0 && dialog.ChosenScore == null, "关闭后过期搜索不得导入或返回曲谱。");
            }
            finally { gate.Release.Set(); Pump(); }
        }
    }

    private static void TestLateDownloadAfterOwnerCloseOrDispose()
    {
        foreach (bool closeOwner in new[] { true, false })
        {
            int imports = 0;
            using (Gate gate = new Gate())
            using (var owner = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), Opacity = 0 })
            using (OnlineCatalogDialog dialog = MakeDialog(new List<SongEntry>(),
                delegate(string term, int page) { return Page(MakeScore("迟到免费谱", "late-close"), false, 0, term); },
                EmptyMusic, delegate { gate.Wait(); return new byte[] { 1 }; },
                delegate { imports++; return new SongEntry { Title = "退出后不可导入" }; }))
            {
                bool started = false;
                try
                {
                    owner.Show(); dialog.Show(owner); Pump(); WaitForInitialFree(dialog);
                    Select(Field<ListView>(dialog, "freeResults"), 0); Pump();
                    Field<Button>(dialog, "download").PerformClick();
                    WaitUntil(delegate { return gate.Started.WaitOne(0); }, "迟到关闭场景的模拟下载未开始。"); started = true;
                    if (closeOwner) owner.Close(); else dialog.Dispose();
                    Pump();
                    Assert(dialog.IsDisposed && (!closeOwner || owner.IsDisposed), "主窗口退出和直接释放不得等待正在下载的免费谱。");
                    gate.Release.Set();
                    WaitUntil(delegate { return !(bool)Field<object>(dialog, "downloading"); }, "已关闭窗口的模拟下载未收尾。");
                    Assert(imports == 0 && dialog.ChosenScore == null && dialog.DialogResult != DialogResult.OK, "退出或释放后迟到下载不得调用曲库导入或返回成功。");
                }
                finally
                {
                    dialog.Dispose(); gate.Release.Set();
                    if (started) WaitUntil(delegate { return !(bool)Field<object>(dialog, "downloading"); }, "迟到关闭场景的模拟下载未清理。");
                }
            }
        }
    }

    private static OnlineCatalogDialog MakeDialog(IList<SongEntry> library,
        Func<string, int, FreeScorePage> freeSearch, Func<string, int, List<OnlineSong>> musicSearch,
        Func<FreeScore, byte[]> freeDownload, Func<FreeScore, byte[], SongEntry> freeImport, bool openFree = true)
    {
        var dialog = new OnlineCatalogDialog("", library,
            delegate { throw new Exception("测试不应打开文件选择器。"); },
            delegate { throw new Exception("测试不应打开简谱编辑器。"); },
            freeImport, freeSearch, freeDownload, musicSearch)
        {
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new Point(-30000, -30000), Opacity = 0
        };
        if (openFree) Field<TabControl>(dialog, "tabs").SelectedTab = Field<TabPage>(dialog, "freeTab");
        return dialog;
    }

    private static FreeScore MakeScore(string title, string id)
    {
        return new FreeScore { Title = title, Composer = "作曲家 " + id, Instrument = "Flute",
            License = "Creative Commons Attribution 4.0", MidiUrl = "https://www.mutopiaproject.org/ftp/test/" + id + ".mid",
            InfoUrl = "https://www.mutopiaproject.org/cgibin/piece-info.cgi?id=" + id,
            SourceEdition = "版本 " + id, LicenseUrl = "https://www.mutopiaproject.org/legal.html#cca",
            Attribution = "Mutopia Project；Maintainer: 测试 " + id };
    }

    private static FreeScorePage Page(FreeScore score, bool next, int archives, string term)
    {
        return new FreeScorePage { AppliedQuery = term, HasNext = next, SkippedArchives = archives,
            Items = new List<FreeScore> { score } };
    }
    private static FreeScorePage EmptyFree(string term, int page)
    { return new FreeScorePage { AppliedQuery = term }; }
    private static List<OnlineSong> EmptyMusic(string term, int page) { return new List<OnlineSong>(); }
    private static byte[] NoDownload(FreeScore score) { throw new Exception("此场景不应触发下载。"); }
    private static SongEntry NoImport(FreeScore score, byte[] bytes) { throw new Exception("此场景不应触发导入。"); }

    private static void Show(OnlineCatalogDialog dialog) { dialog.Show(); Pump(); }
    private static void WaitForInitialFree(OnlineCatalogDialog dialog)
    { WaitUntil(delegate { return Field<Button>(dialog, "freeGo").Enabled; }, "免费初始搜索未完成。"); }
    private static T Field<T>(object instance, string name)
    { return (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance); }
    private static void Select(ListView list, int index)
    {
        IntPtr handle = list.Handle;
        list.Focus();
        foreach (ListViewItem item in list.Items) item.Selected = false;
        list.Items[index].Selected = true;
        list.Items[index].Focused = true;
    }
    private static void Enter(TextBox text)
    {
        // Raise only the local control event; never call SendKeys/SendInput.
        typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(text, new object[] { new KeyEventArgs(Keys.Enter) });
        Pump();
    }
    private static void Pump() { Application.DoEvents(); }
    private static void WaitUntil(Func<bool> condition, string failure)
    {
        Stopwatch watch = Stopwatch.StartNew();
        for (;;)
        {
            Pump();
            if (condition()) return;
            if (watch.ElapsedMilliseconds > 4000) throw new Exception(failure);
            Thread.Sleep(5);
        }
    }
    private static void Assert(bool condition, string message)
    { checks++; if (!condition) throw new Exception(message); }

    private static void RunCase(string name, Action action)
    {
        try { action(); }
        catch (Exception error)
        { throw new Exception(name + "：" + error.Message + "\n" + error.StackTrace); }
    }

    private static string MatchStatus(OnlineCatalogDialog dialog)
    {
        ListView results = Field<ListView>(dialog, "musicResults");
        OnlineSong song = results.SelectedItems.Count > 0 ? results.SelectedItems[0].Tag as OnlineSong : null;
        IList<SongEntry> library = Field<IList<SongEntry>>(dialog, "library");
        return "选中=" + (song == null ? "无" : song.Title) + "；本地=" + library.Count +
            "；精确匹配=" + OnlineCatalogDialog.FindLocalMatches(library, song == null ? "" : song.Title).Count +
            "；列表=" + Field<ComboBox>(dialog, "localScores").Items.Count +
            "；关闭=" + Field<object>(dialog, "closingDialog") + "；待刷新=" + Field<object>(dialog, "matchesQueued") +
            "；已销毁=" + dialog.IsDisposed + "；销毁中=" + dialog.Disposing;
    }

    private sealed class Gate : IDisposable
    {
        public readonly ManualResetEvent Started = new ManualResetEvent(false);
        public readonly ManualResetEvent Release = new ManualResetEvent(false);
        private int calls;
        public int Calls { get { return Interlocked.CompareExchange(ref calls, 0, 0); } }
        public void Wait()
        {
            Interlocked.Increment(ref calls); Started.Set();
            if (!Release.WaitOne(3000)) throw new TimeoutException("模拟请求等待超时。");
        }
        public void Dispose() { Release.Set(); Started.Dispose(); Release.Dispose(); }
    }
}
