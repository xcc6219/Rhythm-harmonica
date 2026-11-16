using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HarmonicaStudio;

public static class MaintenanceWorkflowTests
{
    private static int checks;
    public static int Run()
    {
        checks = 0;
        using (var form = new MainForm(false))
        {
            string data = Field<string>(form, "dataDirectory");
            string audioPath = Path.Combine(data, "maintenance-source.wav");
            byte[] wave = new byte[48];
            System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(wave, 0);
            System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(wave, 8);
            File.WriteAllBytes(audioPath, wave);
            var original = new SongEntry { Title = "原演唱版本", Artist = "原歌手", Attribution = "原署名机构",
                License = "原授权说明", LicenseUrl = "https://example.org/license", SourceUrl = "https://example.org/song", Chinese = true };
            byte[] midi = MidiWriter.ToBytes(new[] { new NoteEvent { Pitch = 66, Start = .35, Duration = .8, Velocity = 85 } }, "maintenance original");
            var copied = (SongEntry)Call(form, "ImportRetranscribedSong", "重新转录版本", audioPath, midi, original);
            Assert(copied.Artist == original.Artist && copied.Attribution == original.Attribution, "重新转录分开保留歌手与原署名。");
            Assert(copied.SourceUrl == original.SourceUrl && copied.LicenseUrl == original.LicenseUrl && copied.License == original.License, "重新转录保留来源与授权信息。");
            Assert(copied.SourceEdition.Contains("自动转录") && copied.SourceEdition.Contains("需试听"), "重新转录仍如实标明估计性质。");
            Assert(File.ReadAllBytes(audioPath).SequenceEqual(wave), "原始音频保持不变。");
            var unknown = new SongEntry { Title = "未知歌手原谱", Attribution = "资料机构", SourceEdition = "自行导入", SourceUrl = "https://example.org/unknown" };
            byte[] unknownMidi = MidiWriter.ToBytes(new[] { new NoteEvent { Pitch = 67, Start = .2, Duration = .8, Velocity = 85 } }, "unknown singer");
            var unknownCopy = (SongEntry)Call(form, "ImportRetranscribedSong", "未知歌手重新转录", null, unknownMidi, unknown);
            var artistMethod = typeof(MainForm).GetMethod("KnownSongArtist", BindingFlags.NonPublic | BindingFlags.Static);
            Assert(unknownCopy.Artist == "" && (string)artistMethod.Invoke(null, new object[] { unknownCopy }) == "" && unknownCopy.Attribution == "资料机构", "新记录明确未知歌手，不把独立机构署名当歌手。");
            var enriched = (SongEntry)Call(form, "ImportFreeScore", new FreeScore { Title = "确认歌手", Composer = "后来确认的歌手" }, unknownMidi);
            Assert(Object.ReferenceEquals(enriched, unknownCopy) && enriched.Artist == "后来确认的歌手", "重复MIDI可补齐可靠歌手且不重复入库。");
            Call(form, "LoadSong", copied);
            Field<ListBox>(form, "songs").SelectedItem = copied;
            var search = Field<TextBox>(form, "search");
            var selected = Field<SongEntry>(form, "selected");
            Set(form, "playing", true); Set(form, "listening", true); Set(form, "offset", .2);
            search.Text = copied.Title;
            Assert(Field<bool>(form, "playing") && Object.ReferenceEquals(Field<SongEntry>(form, "selected"), selected), "仍匹配当前歌曲的筛选保留播放。");
            Set(form, "rangeStart", .1); Set(form, "rangeEnd", .9); Set(form, "targetSeconds", 30d); Set(form, "repeatRange", true);
            search.Text = "不存在的曲目-unique-no-match";
            Assert(!Field<bool>(form, "playing") && !Field<bool>(form, "listening") && !Field<bool>(form, "autoActive"), "空筛选同步停止所有播放状态。");
            Assert(Field<SongEntry>(form, "selected") == null && Field<List<NoteEvent>>(form, "notes").Count == 0, "空筛选清空选曲和音符。");
            Assert(Field<double>(form, "sourceDuration") == 0 && Field<double>(form, "offset") == 0 && Field<double>(form, "targetSeconds") == 0 && !Field<bool>(form, "repeatRange"), "空筛选清除旧进度和循环范围。");
            Assert(!Field<ComboBox>(form, "tracks").Enabled, "空筛选禁用声轨选择。");
            search.Text = "";
            Call(form, "LoadSong", copied);
            Set(form, "rangeStart", .1); Set(form, "targetSeconds", 40d); Set(form, "repeatRange", true);
            Call(form, "LoadSong", new SongEntry { Title = "缺失文件", FileName = "maintenance-missing.mid" });
            Assert(Field<double>(form, "sourceDuration") == 0 && Field<double>(form, "targetSeconds") == 0 && !Field<bool>(form, "repeatRange"), "载入失败不遗留旧源时长或循环。");
            Assert(!Field<ComboBox>(form, "tracks").Enabled && Field<SongEntry>(form, "selected") == null, "载入失败与空声轨状态一致。");
            var high = (SongEntry)Call(form, "ImportFreeScore", new FreeScore { Title = "移调超界验证" },
                MidiWriter.ToBytes(new[] { new NoteEvent { Pitch = 72, Start = 0, Duration = .5, Velocity = 80 }, new NoteEvent { Pitch = 120, Start = 1, Duration = .4, Velocity = 80 } }, "MELODY"));
            Call(form, "LoadSong", copied);
            Field<NumericUpDown>(form, "transposeInput").Value = 24;
            Call(form, "LoadSong", high);
            Assert(Field<double>(form, "sourceDuration") == 0 && Field<double>(form, "targetSeconds") == 0 && Field<List<NoteEvent>>(form, "notes").Count == 0, "内部移调失败清理旧时长与音符。");
            Assert(!Field<Label>(form, "statusLabel").Text.Contains("已载入") && Object.ReferenceEquals(Field<SongEntry>(form, "selected"), high), "移调错误不被成功提示覆盖，仍可调整原轨。");
            Field<NumericUpDown>(form, "transposeInput").Value = 0;
            Assert(Field<List<NoteEvent>>(form, "notes").Count > 0, "调整移调后原轨可以恢复。");
            string folder = SongFileLibrary.DirectoryFor(data);
            string[] before = Directory.GetFiles(folder).OrderBy(x => x).ToArray();
            int count = Field<List<SongEntry>>(form, "library").Count;
            using (var locked = new FileStream(Path.Combine(data, "library.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool failed = false;
                try { Call(form, "ImportTranscribedSong", "保存失败测试", audioPath,
                    MidiWriter.ToBytes(new[] { new NoteEvent { Pitch = 69, Start = 1, Duration = .7, Velocity = 82 } }, "failed import"), null); }
                catch (TargetInvocationException error) { failed = error.InnerException is IOException || error.InnerException is InvalidOperationException; }
                Assert(failed, "曲库被占用时报告保存失败。");
            }
            Assert(Directory.GetFiles(folder).OrderBy(x => x).SequenceEqual(before) && Field<List<SongEntry>>(form, "library").Count == count, "导入失败不留下新配对音频、MIDI或假条目。");
            Assert(File.ReadAllBytes(audioPath).SequenceEqual(wave), "导入回滚保留原音频。");
            IntPtr handle = form.Handle;
            var preview = Field<AudioPreview>(form, "audio");
            MethodInfo render = typeof(AudioPreview).GetMethod("RenderAndPlay", BindingFlags.Instance | BindingFlags.NonPublic);
            render.Invoke(preview, new object[] { null, 0d, 1d, 0d, preview.SessionId, new System.Threading.CancellationTokenSource() });
            preview.Stop(); Set(form, "listening", true);
            Application.DoEvents();
            Assert(Field<bool>(form, "listening"), "实际主窗消息队列丢弃旧会话试听错误。");
            render.Invoke(preview, new object[] { null, 0d, 1d, 0d, preview.SessionId, new System.Threading.CancellationTokenSource() });
            Application.DoEvents();
            Assert(!Field<bool>(form, "listening") && Field<Label>(form, "statusLabel").Text.Contains("试听失败"), "实际主窗消息队列仍展示当前试听错误。");
        }
        Console.WriteLine("维护流程测试通过，共 " + checks + " 项断言（隔离曲库，无键鼠输入）。");
        return checks;
    }
    private static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value); }
    private static void Set(object value, string name, object data) { value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(value, data); }
    private static object Call(object value, string name, params object[] args) { return value.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(value, args); }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
}
