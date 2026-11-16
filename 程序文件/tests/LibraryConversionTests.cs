using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using HarmonicaStudio;

public static class LibraryConversionTests
{
    private static int checks;
    public static int Run()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("转换验证须在 STA 运行。");
        checks = 0;
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        if (File.Exists(Path.Combine(root, "library.json")) && File.ReadAllText(Path.Combine(root,"library.json")).Trim() != "[]")
            throw new InvalidOperationException("测试仅运行在空的隔离曲库中。");
        var form = new MainForm();
        try
        {
            var source = new SongEntry { Title = "中文节奏验证", Jianpu = "0:2 1:1.5 2:0.5 3:2 0 5", Bpm = 90, Chinese = true,
                SourceUrl = "https://example.org/score", License = "自有测试旋律", LicenseUrl = "https://example.org/license", Attribution = "测试作者" };
            var expected = ScoreIO.ParseJianpu(source.Jianpu,source.Bpm,60).Notes;
            SongEntry result = (SongEntry)Call(form,"ConvertScoreToMidi",source);
            Assert(!Object.ReferenceEquals(source,result) && !String.IsNullOrEmpty(result.FileName),"简谱生成独立MIDI曲目。");
            string path = SongFileLibrary.ResolveMidi(root,result.FileName);
            byte[] bytes = File.ReadAllBytes(path);
            Assert(bytes[0]=='M' && bytes[1]=='T' && bytes[2]=='h' && bytes[3]=='d',"生成真实MIDI文件头。");
            var loaded = ScoreIO.LoadMidi(path).Tracks.SelectMany(t=>t.Notes).ToList();
            Assert(loaded.Count==expected.Count,"休止符不会生成假音符。");
            for(int i=0;i<loaded.Count;i++)
            {
                Assert(loaded[i].Pitch==expected[i].Pitch,"音高保留。");
                Assert(Math.Abs(loaded[i].Start-expected[i].Start)<.002,"首音静默和音间休止保留。");
                Assert(Math.Abs(loaded[i].Duration-expected[i].Duration)<.002,"附点及半拍时值保留。");
            }
            Assert(result.SourceUrl==source.SourceUrl && result.License==source.License && result.Attribution==source.Attribution && result.Chinese,"生成MIDI保留谱源和中文标志。");
            Assert(Object.ReferenceEquals(result,Call(form,"ConvertScoreToMidi",result)),"已有真实MIDI复用原项。");
            Assert(Object.ReferenceEquals(result,Call(form,"ConvertScoreToMidi",source)),"同一简谱重复转换不创建重复文件。");
            ChineseScore chinese = ChineseScoreCatalog.Search("晴天")[0];
            SongEntry remote = (SongEntry)Call(form,"ImportChineseScore",chinese,MidiWriter.ToBytes(new [] { new NoteEvent { Pitch=67,Start=0,Duration=.5,Velocity=80 } },"中文来源验证"));
            Assert(remote.Chinese && remote.SourceUrl==chinese.SourceUrl && remote.LicenseUrl==ChineseScoreCatalog.LicenseUrl,"在线中文MIDI保存真实来源及许可证链接。");
            Assert(remote.Subtitle.Contains("POP909") && remote.Subtitle.Contains("周杰伦") && remote.SourceEdition.Contains("钢琴编配"),"编配版本及歌手在本地曲库保留。");
            Assert(remote.Attribution.Contains("Music X Lab"),"原数据集署名保留。");
            string saved = File.ReadAllText(Path.Combine(root,"library.json"));
            Assert(saved.Contains("POP909") && saved.Contains("LicenseUrl"),"重启所用索引持久化来源信息。");
            var songs = Field<ListBox>(form,"songs"); songs.SelectedItem=remote;
            Assert(Field<ScoreCanvas>(form,"canvas").BackgroundTracks.Count==1,"选择歌曲后完整瀑布绑定所有源声轨。");
            Assert(Object.ReferenceEquals(Field<ScoreCanvas>(form,"canvas").BackgroundTracks,Field<ScoreCanvas>(form,"compactCanvas").BackgroundTracks),"完整与迷你共用同一多声轨数据。");
            Assert(AudioMidiConverter.Quote("a b\\")=="\"a b\\\\\"","转换路径尾部反斜线正确引用。");
            Assert(AudioMidiConverter.Quote("a\"b")=="\"a\\\"b\"","转换参数内双引号正确引用。");
            string audioPath = Path.Combine(root, "官方下载.wav"); byte[] wave = new byte[48];
            System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(wave, 0); System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(wave, 8);
            File.WriteAllBytes(audioPath, wave);
            byte[] converted = MidiWriter.ToBytes(new[] { new NoteEvent { Pitch = 71, Start = 0, Duration = 1, Velocity = 90 } }, "音频配对验证");
            SongEntry paired = (SongEntry)Call(form,"ImportFreeScore",new FreeScore { Title = "音频配对验证", Composer = "测试歌手", AudioFilePath = audioPath },converted);
            string directory = SongFileLibrary.DirectoryFor(root);
            Assert(File.Exists(Path.Combine(directory,paired.FileName)) && File.Exists(Path.Combine(directory,paired.AudioFileName)), "实际导入流程将音频和转换MIDI保存到统一曲目文件夹。");
            Assert(Path.GetFileNameWithoutExtension(paired.FileName) == Path.GetFileNameWithoutExtension(paired.AudioFileName), "对应录音和MIDI保留同一文件名前缀。");
            Assert(File.ReadAllText(Path.Combine(root,"library.json")).Contains(paired.AudioFileName), "重启曲库索引保留实际音频对应关系。");
            Assert(File.ReadAllBytes(audioPath).SequenceEqual(wave), "官方或手选原音频不会被移动或删除。");
            foreach (string provider in new[] { "QQ音乐", "酷狗音乐" })
            {
                var song = new OnlineSong { Title = provider + "同页导入验证", Artist = "指定版本歌手", Provider = provider, Id = "version-check" };
                byte[] inlineMidi = MidiWriter.ToBytes(new[] { new NoteEvent { Pitch = provider == "QQ音乐" ? 60 : 62, Start = .5, Duration = 1, Velocity = 85 } }, song.Title);
                Field<TextBox>(form, "search").Text = "之前的筛选不匹配新歌";
                SongEntry inline = (SongEntry)Call(form, "ImportTranscribedSong", song.Title, audioPath, inlineMidi, song);
                Assert(Field<TextBox>(form, "search").Text == "" && songs.Items.Contains(inline) && Object.ReferenceEquals(songs.SelectedItem, inline),
                    "转录完成主动清除旧筛选并立即刷新选中新歌，无需手工搜索。");
                Assert(inline.Title == song.Title && inline.Attribution == song.Artist, "同页导入保留所选歌曲与歌手版本。");
                Assert(inline.SourceUrl == song.SourceUrl && inline.Chinese && inline.SourceEdition.Contains("需试听"), "同页导入记录真实平台来源及自动转录性质。");
                Assert(File.ReadAllBytes(Path.Combine(directory, inline.FileName)).SequenceEqual(inlineMidi), "主窗保存实际转录MIDI原字节。");
                Assert(File.ReadAllBytes(Path.Combine(directory, inline.AudioFileName)).SequenceEqual(wave) &&
                    Path.GetFileNameWithoutExtension(inline.FileName) == Path.GetFileNameWithoutExtension(inline.AudioFileName), "主窗为同页转录结果保存配对录音。");
                string inlineIndex = File.ReadAllText(Path.Combine(root, "library.json"));
                Assert(inlineIndex.Contains(inline.Title) && inlineIndex.Contains(inline.AudioFileName), "同页结果与录音关系写入重启使用的索引。");
                Assert(Field<ScoreCanvas>(form, "canvas").SongTitle == inline.Title, "立即选用的真实曲库项能载入瀑布。");
            }
            TestRemoval(form, root, directory, paired, audioPath, wave);
            TestManualPitchAndArtist(form);
            TestMainMelodyClock(form, directory);
            Console.WriteLine("曲库转换与来源测试通过，共 "+checks+" 项断言（隔离曲库，未发送键鼠输入）。");
            return checks;
        }
        finally
        {
            Field<System.Windows.Forms.Timer>(form,"timer").Dispose(); Field<AudioPreview>(form,"audio").Dispose(); Field<AutoPlayer>(form,"autoPlayer").Dispose();
            Field<ToolTip>(form,"sourceTip").Dispose(); form.Dispose();
        }
    }
    private static void TestManualPitchAndArtist(MainForm form)
    {
        byte[] bytes = MidiWriter.ToBytes(new[] {
            new NoteEvent { Pitch = 60, Start = 0, Duration = .4, Velocity = 90 },
            new NoteEvent { Pitch = 64, Start = .7, Duration = .4, Velocity = 90 },
            new NoteEvent { Pitch = 75, Start = 1.4, Duration = .4, Velocity = 90 }
        }, "manual transpose source");
        SongEntry entry = (SongEntry)Call(form, "ImportTranscribedSong", "手动移调不可抵消", null, bytes, null);
        var transpose = Field<NumericUpDown>(form, "transposeInput");
        transpose.Value = 12;
        var notes = Field<List<NoteEvent>>(form, "notes");
        Assert(notes.Count == 3 && notes[0].Pitch == 72 && notes[1].Pitch == 76,
            "主窗手动升八度实际生效，自动全曲适配不能反向抵消。");
        Assert(notes[2].Pitch == 85 && Math.Abs(notes[1].Start - .7) < .002,
            "手动高边缘仅救相邻半音，原休止保持。");
        transpose.Value = 24;
        notes = Field<List<NoteEvent>>(form, "notes");
        Assert(notes.Count==1 && notes[0].Pitch==84,
            "默认按手动移调后的最后可奏音结束，残余高音不能变成无声长尾。");
        var trim = Field<CheckBox>(form,"trimTailCheck"); trim.Checked=false;
        notes = Field<List<NoteEvent>>(form,"notes");
        Assert(notes[0].Pitch == 84 && notes[1].Pitch == 88 && notes[2].Pitch == 99,
            "手动移调的残余超域音保留音高和时间，不能跨八度折回。");
        transpose.Value = 0; trim.Checked=true;
        Assert(Field<ListBox>(form, "songs").ContextMenuStrip.Items.Cast<ToolStripItem>()
            .Any(item => item.Text == "查找主旋律谱（保留原谱）"), "曲库可直接进入已有主旋律谱入口。");
        MethodInfo artist = typeof(MainForm).GetMethod("KnownSongArtist", BindingFlags.NonPublic | BindingFlags.Static);
        Assert((string)artist.Invoke(null, new object[] { new SongEntry { SourceEdition = "POP909 钢琴编配 · 筷子兄弟", Attribution = "Music X Lab · 筷子兄弟 · URL" } }) == "筷子兄弟",
            "查谱使用编配中明确的歌手，不把数据集署名当歌手。");
        Assert((string)artist.Invoke(null, new object[] { new SongEntry { SourceEdition = "Basic Pitch 自动转录 · 需试听确认", Attribution = "毛华锋" } }) == "毛华锋",
            "查谱保留平台转录时记录的歌手版本。");
        Assert((string)artist.Invoke(null, new object[] { new SongEntry { SourceEdition = "自己导入", Attribution = "作者备注" } }) == "",
            "无法确认歌手的自选谱不伪造精准版本。");
        Assert(!String.IsNullOrEmpty(entry.FileName), "手动移调验证使用实际隔离导入MIDI。");
    }
    private static void TestMainMelodyClock(MainForm form, string directory)
    {
        // A real three-track file: accompaniment lasts well beyond a melodic
        // phrase containing a long rest. The main UI must use one melody clock.
        var parts = new[] {
            MidiWriter.ToBytes(new[] { new NoteEvent { Pitch=40, Start=0, Duration=120, Velocity=100 } }, "PIANO"),
            MidiWriter.ToBytes(new[] {
                new NoteEvent { Pitch=64, Start=5, Duration=.5, Velocity=90 },
                new NoteEvent { Pitch=67, Start=35, Duration=.5, Velocity=90 },
                new NoteEvent { Pitch=69, Start=36, Duration=.5, Velocity=90 } }, "MELODY"),
            MidiWriter.ToBytes(new[] { new NoteEvent { Pitch=77, Start=0, Duration=90, Velocity=90 } }, "BRIDGE") };
        var bytes = new List<byte>(parts[0].Take(14)); bytes[9]=1; bytes[11]=3;
        foreach (byte[] part in parts) bytes.AddRange(part.Skip(14));
        string file = "main-role-clock.mid";
        File.WriteAllBytes(Path.Combine(directory, file), bytes.ToArray());
        Call(form, "LoadSong", new SongEntry { Title="主旋律时钟验证", FileName=file, SourceEdition="用户 MIDI" });
        var tracks = Field<ComboBox>(form, "tracks");
        Assert(tracks.Items.Count == 1 && !tracks.Enabled && ((ScoreTrack)tracks.SelectedItem).Name.StartsWith("MELODY"),
            "真实多轨文件只将主旋律放入吹奏选择，伴奏与副旋律不误吹。");
        var notes = Field<List<NoteEvent>>(form, "notes");
        Assert(notes.Count == 3 && notes[0].Pitch==64 && Math.Abs(notes[1].Start-30)<.002,
            "主旋律音高和曲中正常长休止完整保留。");
        double duration = (double)typeof(MainForm).GetProperty("Duration", BindingFlags.NonPublic|BindingFlags.Instance).GetValue(form,null);
        Assert(Math.Abs(duration-31.5)<.002, "主旋律最后音结束即为自然终点，120秒伴奏不拖长。");
        foreach (ScoreCanvas canvas in new[] { Field<ScoreCanvas>(form,"canvas"),Field<ScoreCanvas>(form,"compactCanvas") })
        {
            Assert(canvas.BackgroundTracks.Count==3 && canvas.SelectedTrackIndex==((ScoreTrack)tracks.SelectedItem).Index,
                "完整与迷你模式均保留所有背景轨，突出真实主旋律。");
            Assert(canvas.BackgroundTracks.SelectMany(t=>t.Notes).All(n=>n.Start+n.Duration<=duration+.00001),
                "多轨瀑布与演奏结束使用同一个主旋律终点。");
        }
        Call(form,"SeekTo",16.0); Call(form,"ToggleManualPlayback"); Call(form,"TickPlayback");
        Assert(Field<bool>(form,"playing"), "曲中长休止仍继续，不把歌曲正常停顿当作结尾。");
        Call(form,"SeekTo",duration); Call(form,"TickPlayback");
        Assert(!Field<bool>(form,"playing") && Math.Abs(Field<double>(form,"offset")-duration)<.00001,
            "手动跟谱到主旋律终点立即停止并保持末尾位置。");
        Set(form,"autoActive",true); Set(form,"playing",true); Set(form,"listening",true);
        Call(form,"TickPlayback");
        Assert(!Field<bool>(form,"autoActive")&&!Field<bool>(form,"playing")&&!Field<bool>(form,"listening"),
            "自动后端已结束时，计时器可独立收敛界面状态，不依赖延迟窗口通知。");
        Assert(!Field<AutoPlaybackWindowGuard>(form,"autoWindowGuard").Enabled,
            "演奏结束同步恢复软件窗口的鼠标操作。");
        AutoPlayer original=Field<AutoPlayer>(form,"autoPlayer");
        try
        {
            foreach (bool lost in new[] {false,true})
                using (AutoPlayer ended=HarmonicaStudio.Tests.AutoPlayerTests.CompletedBackendForUiTest(lost))
                {
                    Set(form,"autoPlayer",ended); Set(form,"autoActive",true); Set(form,"playing",true); Set(form,"listening",true);
                    Call(form,"ApplyAutoPlayerState","自动演奏完成。",ended.SessionId-1);
                    Assert(Field<bool>(form,"autoActive")&&Field<bool>(form,"playing"),
                        "上一轮延迟完成通知不能提前结束新轮界面状态。");
                    Call(form,"ApplyAutoPlayerState","倒计时 3 秒，请切回游戏。",ended.SessionId);
                    Assert(!Field<bool>(form,"autoActive")&&!Field<bool>(form,"playing")&&!Field<bool>(form,"listening"),
                        "延迟旧通知在后台已结束后仍收敛所有播放状态。");
                    Assert(Field<Label>(form,"statusLabel").Text==ended.LastCompletionMessage,
                        "延迟倒计时通知不能覆盖真实完成或前台丢失原因。");
                    Call(form,"ApplyAutoPlayerState","已开始发送演奏输入。",ended.SessionId);
                    Assert(Field<Label>(form,"statusLabel").Text==ended.LastCompletionMessage,
                        "后续旧进度通知不能吞掉已确认结束状态。");
                }
        }
        finally { Set(form,"autoPlayer",original); }
        File.WriteAllBytes(Path.Combine(directory,"silent-role.mid"),MidiWriter.ToBytes(new[] {
            new NoteEvent { Pitch=0,Start=0,Duration=.5,Velocity=90 },
            new NoteEvent { Pitch=127,Start=10,Duration=.5,Velocity=90 } },"MELODY"));
        Call(form,"LoadSong",new SongEntry { Title="全域外不能空转",FileName="silent-role.mid" });
        Assert(Field<List<NoteEvent>>(form,"notes").Count==0 && Field<double>(form,"noteDuration")==0,
            "全部无可奏音的谱不保留无声演奏时间，仍可调整来源轨移调。");
        Call(form,"TogglePlayback");
        Assert(!Field<bool>(form,"autoActive")&&!Field<bool>(form,"playing"), "空旋律不能假装自动开始并一直空转。");
    }
    private static void Set(object target,string name,object value) { target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value); }
    private static void TestRemoval(MainForm form, string root, string directory, SongEntry paired, string originalAudio, byte[] wave)
    {
        var library = Field<List<SongEntry>>(form, "library");
        using (var dialog = new LibraryRemovalDialog(paired)) Assert(!dialog.RemoveFiles, "删除曲目默认保留本机文件。");
        Call(form, "DeleteLibraryEntry", paired, false);
        Assert(!library.Contains(paired) && !File.ReadAllText(Path.Combine(root, "library.json")).Contains(paired.FileName), "删除曲目立即移除并持久化索引。");
        Assert(File.Exists(Path.Combine(directory, paired.FileName)) && File.Exists(Path.Combine(directory, paired.AudioFileName)), "仅删列表保留音频和MIDI。");
        Assert(File.ReadAllBytes(originalAudio).SequenceEqual(wave), "删列表不修改原始音频。");
        byte[] replacement = MidiWriter.ToBytes(new[] { new NoteEvent { Pitch = 64, Start = .2, Duration = .8, Velocity = 80 } }, "重转录独立版本");
        string pairedAudio = Path.Combine(directory, paired.AudioFileName);
        SongEntry newVersion = (SongEntry)Call(form, "ImportTranscribedSong", "重转录独立版本", pairedAudio, replacement, null);
        Assert(newVersion.FileName != paired.FileName && File.ReadAllBytes(Path.Combine(directory, paired.FileName)).Length > 14, "重转录已有配对音频生成新MIDI，不覆盖旧谱。");
        var alias = new SongEntry { Title = "共享音频引用", FileName = newVersion.FileName, AudioFileName = newVersion.AudioFileName };
        library.Add(alias); Call(form, "SaveLibrary");
        Assert(SongFileLibrary.RemovalPaths(root, newVersion, library.Where(x => !Object.ReferenceEquals(x, newVersion))).Count == 0, "共享MIDI与音频不会被另一曲目删除。");
        Call(form, "DeleteLibraryEntry", newVersion, true);
        Assert(File.Exists(Path.Combine(directory, alias.FileName)) && File.Exists(Path.Combine(directory, alias.AudioFileName)), "实际勾选删除同样保留共享文件。");
        Call(form, "DeleteLibraryEntry", alias, true);
        Assert(!File.Exists(Path.Combine(directory, alias.FileName)) && !File.Exists(Path.Combine(directory, alias.AudioFileName)), "删除最后引用时只移除独占的程序曲目文件。");
        Assert(File.Exists(pairedAudio) && File.ReadAllBytes(originalAudio).SequenceEqual(wave), "删除新版本保留旧版录音与外部原件。");
        bool invalid = false;
        try { SongFileLibrary.RemovalPaths(root, new SongEntry { FileName = "..\\library.json" }, new SongEntry[0]); }
        catch (InvalidDataException) { invalid = true; }
        Assert(invalid, "无效越界文件名拒绝删除。");
        SongEntry builtin = library.First(x => x.Builtin); string builtinTitle = builtin.Title;
        Call(form, "DeleteLibraryEntry", builtin, false);
        Assert(AppSettings.Load(Path.Combine(root, "settings.json")).HiddenBuiltinTitles.Contains(builtinTitle), "内置曲目删除状态跨重启保留。");
        using (var reopened = new MainForm())
            Assert(!Field<List<SongEntry>>(reopened, "library").Any(x => x.Builtin && x.Title == builtinTitle), "新主窗不重新显示已删除内置曲目。");
        Call(form, "RestoreBuiltinSongs");
        Assert(library.Count(x => x.Builtin && x.Title == builtinTitle) == 1 && AppSettings.Load(Path.Combine(root, "settings.json")).HiddenBuiltinTitles.Count == 0, "恢复内置曲目一次且清除持久删除状态。");
        var settings = new AppSettings { TrimInvalidTail = false, AdaptNearbyPitch = false,
            HiddenBuiltinTitles = new List<string> { builtinTitle, builtinTitle, "", null } };
        string settingsTest = Path.Combine(root, "tail-adaptation-settings.json"); settings.Save(settingsTest);
        var loaded = AppSettings.Load(settingsTest);
        Assert(!loaded.TrimInvalidTail && !loaded.AdaptNearbyPitch && loaded.HiddenBuiltinTitles.SequenceEqual(new[] { builtinTitle }), "新开关显式false及去重曲目删除状态可持久化。");
        File.WriteAllText(settingsTest, "{}"); loaded = AppSettings.Load(settingsTest);
        Assert(loaded.TrimInvalidTail && loaded.AdaptNearbyPitch && loaded.HiddenBuiltinTitles.Count == 0, "旧设置缺少新字段时采用自动适配与尾部检查默认值。");
        SongEntry rollback = library.First(x => !x.Builtin); string indexPath = Path.Combine(root, "library.json");
        using (var locked = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            bool failed = false;
            try { Call(form, "DeleteLibraryEntry", rollback, false); } catch (TargetInvocationException) { failed = true; }
            Assert(failed && library.Contains(rollback), "索引保存失败恢复曲目，避免列表假删除。");
        }
    }
    private static T Field<T>(object target,string name) { return (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target); }
    private static object Call(object target,string name,params object[] arguments) { return target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(target,arguments); }
    private static void Assert(bool value,string message) { if(!value) throw new Exception(message); checks++; }
}
