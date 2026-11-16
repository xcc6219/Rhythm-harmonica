using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonicaStudio;

namespace HarmonicaStudio.Tests
{
    internal static class HarmonicaArrangementTests
    {
        private static int checks;
        private static Type core;
        public static int Run()
        {
            checks = 0;
            core = typeof(PlaybackPlan).Assembly.GetType("HarmonicaStudio.HarmonicaArrangement");
            Check(core != null, "必须提供统一口琴编配核心");
            AnalyzeSpeeds();
            TransitionAndLoopLimits();
            PreserveRestsAndSource();
            ArticulationAndQuantization();
            ContinuousKeysAtRoundingBoundary();
            ExplicitTailAndValidation();
            Console.WriteLine("统一口琴编配测试通过，共 " + checks + " 项断言（纯算法、未发送输入）。");
            return checks;
        }

        private static void AnalyzeSpeeds()
        {
            NoteEvent[] ordinary = { N(60, 0, .3), N(62, .3, .3) };
            object normal = Analyze(ordinary, 1, false, 60, 1.234);
            Check(Get<bool>(normal, "CanPerform"), "正常旋律可以编配");
            Near(Get<double>(normal, "EffectiveSpeed"), 1.234, "无需降速时精确保留用户请求值");
            Near(Get<double>(normal, "MaximumSpeed"), 1.5, "安全速度同时受界面最高一点五倍约束");
            object shortNote = Analyze(new[] { N(60, 0, .04) }, .2, false, 60, 1);
            Check(Get<bool>(shortNote, "CanPerform"), "四十毫秒单音可整曲降速保留");
            Near(Get<double>(shortNote, "EffectiveSpeed"), .87, "按量化安全持音下限计算并向下取百分位");
            Check(Get<double>(shortNote, "EffectiveSpeed") <= Get<double>(shortNote, "MaximumSpeed"), "实际速度不超过安全上限");
            object tooShort = Analyze(new[] { N(60, 0, .02) }, .2, false, 60, 1);
            Check(!Get<bool>(tooShort, "CanPerform") && Get<double>(tooShort, "MaximumSpeed") < .5,
                "最低半速仍不能持音时拒绝可奏结论");
            Check(Get<string>(tooShort, "Message").Contains("0.5") && Get<string>(tooShort, "Message").Contains("短"),
                "不能演奏的提示解释最低速度与短音原因");
            object outside = Analyze(new[] { N(110, .2, .3) }, 1, false, 60, 1);
            Check(!Get<bool>(outside, "CanPerform") && Get<string>(outside, "Message").Contains("音域"), "全超域明确提示换轨或移调");
            Check(!Get<bool>(Analyze(new NoteEvent[0], 1, false, 60, 1), "CanPerform"), "空片段不伪造可奏音");
        }

        private static void TransitionAndLoopLimits()
        {
            object same = Analyze(new[] { N(60, 0, .0504), N(60, .0504, .2) }, .4, false, 60, 1);
            Near(Get<double>(same, "EffectiveSpeed"), .75, "同实体键需持音加重触间隔");
            object modifier = Analyze(new[] { N(60, 0, .05), N(61, .05, .2) }, .4, false, 60, 1);
            Near(Get<double>(modifier, "EffectiveSpeed"), .61, "同键换半音取三十五毫秒准备而非二十加三十五");
            object octave = Analyze(new[] { N(48, 0, .05), N(60, .05, .2) }, .4, false, 60, 1);
            Near(Get<double>(octave, "EffectiveSpeed"), .61, "不同音高仍比较实体键和鼠标组合");
            object different = Analyze(new[] { N(60, 0, .05), N(62, .05, .2) }, .4, false, 60, 1);
            Near(Get<double>(different, "EffectiveSpeed"), 1, "不同键且不换修饰无需额外准备");
            NoteEvent[] single = { N(60, 0, .05) };
            Near(Get<double>(Analyze(single, .0603, false, 60, 1), "EffectiveSpeed"), 1, "单次演奏不虚构循环接缝");
            Near(Get<double>(Analyze(single, .0603, true, 60, 1), "EffectiveSpeed"), .9, "单音循环也检查末音到自身重触");
            NoteEvent[] cycle = { N(60, 0, .05), N(61, .1, .05) };
            Near(Get<double>(Analyze(cycle, .18, false, 60, 1), "EffectiveSpeed"), 1, "片段内部足够宽时保持请求速度");
            Near(Get<double>(Analyze(cycle, .18, true, 60, 1), "EffectiveSpeed"), .98, "多音循环接缝换修饰可成为最紧约束");
            object skipped = Analyze(new[] { N(60, 0, .2), N(110, .2, .001), N(61, .4, .2) }, 1, true, 60, 1.5);
            Near(Get<double>(skipped, "EffectiveSpeed"), 1.5, "超域短音作为休止，不降低可奏序列速度");
        }

        private static void PreserveRestsAndSource()
        {
            NoteEvent[] source = { N(40, 0, .1), N(61, .1, .2), N(110, .3, .05), N(61, .35, .2), N(62, .6, .2) };
            PlaybackPlan plan = Build(source, 1);
            object output = Prepare(plan, 60, 1);
            var notes = Get<List<NoteEvent>>(output, "Notes");
            Check(notes.Count == 3 && Get<int>(output, "SkippedCount") == 2, "超域音过滤为原时段休止并披露数量");
            Check(Get<int>(output, "ArticulatedCount") == 0, "已有休止足够时不缩短旋律");
            Near(notes[0].Start, .1, "保留超域前导音所占时间");
            Near(notes[1].Start, .35, "过滤中间超域音后续旋律不提前");
            Near(notes[0].Duration, .2, "真实休止优先用于同键准备");
            Near(notes[1].Duration, .2, "真实休止优先用于修饰切换");
            Near(Get<double>(output, "Duration"), 1, "保留完整尾部休止和计划时长");
            Near(Get<double>(output, "EffectiveSpeed"), 1, "输出报告实际速度");
            Check(notes[0].Velocity == 91 && notes[0].Pitch == 61 && !Object.ReferenceEquals(notes[0], plan.Notes[1]),
                "音高力度保留且可奏输出是独立副本");
            notes[0].Duration = 9;
            Near(source[1].Duration, .2, "输出编辑不写回原始声轨");
            Near(plan.Notes[1].Duration, .2, "输出编辑不写回片段计划");
            Check(plan.Notes.Count == 5 && plan.Notes[2].Pitch == 110, "原显示计划仍保留不可奏音");
        }

        private static void ArticulationAndQuantization()
        {
            PlaybackPlan plan = Build(new[] { N(60, 0, .3), N(60, .3, .3), N(61, .6, .3) }, 1.2);
            object output = Prepare(plan, 60, 1);
            var notes = Get<List<NoteEvent>>(output, "Notes");
            Check(Get<int>(output, "ArticulatedCount") == 2, "只统计确实需收短的前两音");
            Check(notes[0].Duration <= .3 - 20.0 / 960 && notes[1].Duration <= .3 - 34.0 / 960,
                "重复键和换修饰按各自量化门限预留");
            Near(notes[2].Duration, .3, "末音不额外收短");
            Near(notes[1].Start, .3, "换指不挪动后续起音");
            Near(Get<double>(output, "Duration"), 1.2, "换指不缩短整体计划");
            for (int phase = 0; phase < 64; phase++)
                foreach (double speed in new[] { .5, 1.0, 1.5 })
                {
                    double origin = phase * .000137;
                    var original = new[] { N(61, origin, .18), N(61, origin + .18, .18),
                        N(49, origin + .36, .18), N(60, origin + .54, .18) };
                    object arranged = Prepare(Build(original, 1), 60, speed);
                    var prepared = Get<List<NoteEvent>>(arranged, "Notes");
                    VerifyQuantizedMidi(prepared, speed, 1);
                    for (int i = 0; i < prepared.Count; i++)
                    {
                        Near(prepared[i].Start, original[i].Start, "各量化相位均保留原起音");
                        Check(prepared[i].Duration <= original[i].Duration + 1e-10 && prepared[i].Duration / speed >= .045,
                            "各量化相位只收短且真实持音满足四十五毫秒");
                    }
                }
            PlaybackPlan loop = PlaybackPlan.Build(new[] { N(60, 0, .2), N(61, .25, .2) }, null, 0, .5, 2, true, 1);
            object loopOutput = Prepare(loop, 60, 1);
            Check(Get<List<NoteEvent>>(loopOutput, "Notes").Count == 8, "已展开循环保留每次旋律起音");
            VerifyQuantizedMidi(Get<List<NoteEvent>>(loopOutput, "Notes"), 1, 2);
        }

        private static void ExplicitTailAndValidation()
        {
            var original = new[] { N(60, 0, .5), N(62, .5, .5) };
            PlaybackPlan clipped = PlaybackPlan.Build(original, null, 0, 1, .52, false, 1);
            Throws<InvalidOperationException>(delegate { Prepare(clipped, 60, 1); }, "指定终点截出二十毫秒尾音时拒绝，不偷偷延长或删音", "时长");
            Near(clipped.Duration, .52, "尾音拒绝后用户指定终点不变");
            Check(clipped.Notes.Count == 2 && clipped.Notes[1].Start == .5, "尾音拒绝后主音没有被删除");
            Throws<InvalidOperationException>(delegate { Prepare(Build(new[] { N(110, 0, .2) }, 1), 60, 1); }, "纯超域输出明确拒绝", "音域");
            Throws<ArgumentNullException>(delegate { Analyze(null, 1, false, 60, 1); }, "拒绝空旋律引用", null);
            Throws<ArgumentNullException>(delegate { Prepare(null, 60, 1); }, "拒绝空计划引用", null);
            foreach (double value in new[] { 0.0, -1.0, Double.NaN, Double.PositiveInfinity, 14401.0 })
                Throws<ArgumentOutOfRangeException>(delegate { Analyze(original, value, false, 60, 1); }, "拒绝无效周期", null);
            foreach (double value in new[] { .49, 1.51, Double.NaN, Double.PositiveInfinity })
            {
                Throws<ArgumentOutOfRangeException>(delegate { Analyze(original, 1, false, 60, value); }, "拒绝无效请求速度", null);
                Throws<ArgumentOutOfRangeException>(delegate { Prepare(Build(original, 1), 60, value); }, "拒绝无效实际速度", null);
            }
            foreach (int root in new[] { -1, 128 })
                Throws<ArgumentOutOfRangeException>(delegate { Analyze(original, 1, false, root, 1); }, "拒绝无效基准音", null);
            foreach (NoteEvent note in new[] { null, N(128, 0, .1), N(60, -.1, .1), N(60, 0, 0),
                N(60, Double.NaN, .1), N(110, 0, Double.PositiveInfinity), N(60, .9, .2) })
                Throws<ArgumentException>(delegate { Analyze(new[] { note }, 1, false, 60, 1); }, "过滤之前验证原音符完整性", null);
            var quiet = N(60, 0, .1); quiet.Velocity = 0;
            Throws<ArgumentException>(delegate { Analyze(new[] { quiet }, 1, false, 60, 1); }, "拒绝零力度音符", null);
            Throws<ArgumentException>(delegate { Analyze(new[] { N(60, .2, .2), N(62, .1, .1) }, 1, false, 60, 1); }, "拒绝未排序旋律", null);
            Throws<ArgumentException>(delegate { Analyze(new[] { N(60, 0, .2), N(62, .1, .2) }, 1, false, 60, 1); }, "拒绝重叠声部", null);
            var excessive = Enumerable.Range(0, ScoreIO.MaximumNotes + 1).Select(i => N(60, i * .05, .05)).ToArray();
            Throws<ArgumentException>(delegate { Analyze(excessive, 11000, false, 60, 1); }, "数量上限在分配编配副本前检查", null);
        }

        private static void ContinuousKeysAtRoundingBoundary()
        {
            const double speed = .73;
            NoteEvent[] source = { N(60, .052078124999999975, .2), N(62, .252078125, .2) };
            Check(Get<bool>(Analyze(source, 1, false, 60, speed), "CanPerform"), "半tick临界相位的连续异键本身可演奏");
            object output = Prepare(Build(source, 1), 60, speed);
            var notes = Get<List<NoteEvent>>(output, "Notes");
            VerifyQuantizedMidi(notes, speed, 1);
            Check(notes[0].Duration <= source[0].Duration && source[0].Duration - notes[0].Duration < .000001,
                "浮点假重叠仅在必要时微量收尾，不添加整tick休止");
            Near(notes[0].Start, source[0].Start, "半tick修正不移动前音起点");
            Near(notes[1].Start, source[1].Start, "半tick修正不移动后音起点");
            Near(source[0].Duration, .2, "半tick修正不回写源音长");
            Near(Get<double>(output, "Duration"), 1, "半tick修正不改变总时长");
            foreach (double origin in new[] { 10511.052078124999, 10511.052078125, 10511.052078125001 })
            {
                NoteEvent[] late = { N(60, origin, .2), N(62, origin + .2, .2) };
                var prepared = Get<List<NoteEvent>>(Prepare(Build(late, 10511.8), 60, speed), "Notes");
                VerifyQuantizedMidi(prepared, speed, 10511.8);
                Check(late[0].Duration - prepared[0].Duration < .000001,
                    "接近四小时实际终点的零间隔修正仍只调整浮点尾差");
            }
            Throws<ArgumentException>(delegate { Analyze(new[] { N(60, 0, .2), N(62, .19999, .2) }, 1, false, 60, speed); },
                "不把真正的负间隙当作浮点问题放过", null);
        }

        private static void VerifyQuantizedMidi(List<NoteEvent> notes, double speed, double duration)
        {
            var exported = notes.Select(n => new NoteEvent { Pitch = n.Pitch, Velocity = n.Velocity,
                Start = n.Start / speed, Duration = n.Duration / speed }).ToList();
            byte[] bytes = MidiWriter.ToBytes(exported, "编配量化验证", duration / speed);
            var starts = new List<int>(); var ends = new List<int>();
            int cursor = 22, tick = 0;
            while (cursor < bytes.Length)
            {
                tick += ReadVariable(bytes, ref cursor);
                int status = bytes[cursor++];
                if (status == 0xff) { int type = bytes[cursor++], length = ReadVariable(bytes, ref cursor); if (type == 0x2f) break; cursor += length; }
                else if (status == 0x90) { starts.Add(tick); cursor += 2; }
                else if (status == 0x80) { ends.Add(tick); cursor += 2; }
                else if ((status & 0xf0) == 0xc0) cursor++;
                else throw new Exception("编配导出含未预期事件。");
            }
            Check(starts.Count == notes.Count && ends.Count == notes.Count, "实际 MIDI 写出保留全部编配音符");
            for (int i = 0; i < notes.Count; i++)
            {
                Check(ends[i] - starts[i] >= 44, "实际 MIDI 量化后持音至少四十四tick");
                if (i + 1 == notes.Count) continue;
                Fingering first = HarmonicaMap.Map(notes[i].Pitch, 60), next = HarmonicaMap.Map(notes[i + 1].Pitch, 60);
                int gap = first.Modifier != next.Modifier ? 34 : first.Key == next.Key ? 20 : 0;
                Check(starts[i + 1] - ends[i] >= gap, "实际 MIDI 量化后同键或修饰准备间隔达标");
                Check((notes[i + 1].Start - notes[i].Start - notes[i].Duration) / speed >= (gap == 34 ? .035 : gap == 20 ? .020 : -1e-10),
                    "真实秒数也满足同键或修饰准备间隔");
            }
        }
        private static int ReadVariable(byte[] bytes, ref int cursor) { int value = 0, part; do { part = bytes[cursor++]; value = (value << 7) | (part & 0x7f); } while ((part & 0x80) != 0); return value; }
        private static NoteEvent N(int pitch, double start, double duration) { return new NoteEvent { Pitch = pitch, Start = start, Duration = duration, Velocity = 91 }; }
        private static PlaybackPlan Build(IList<NoteEvent> notes, double duration) { return PlaybackPlan.Build(notes, null, 0, duration, 0, false, 1); }
        private static object Analyze(IList<NoteEvent> notes, double duration, bool repeat, int root, double speed) { return Invoke("Analyze", notes, duration, repeat, root, speed); }
        private static object Prepare(PlaybackPlan plan, int root, double speed) { return Invoke("Prepare", plan, root, speed); }
        private static object Invoke(string method, params object[] values)
        {
            try { return core.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, values); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        private static T Get<T>(object target, string field) { return (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(target); }
        private static void Check(bool okay, string message) { checks++; if (!okay) throw new Exception("口琴编配测试失败：" + message); }
        private static void Near(double actual, double expected, string message) { Check(Math.Abs(actual - expected) < 1e-8, message); }
        private static void Throws<T>(Action action, string message, string fragment) where T : Exception
        { checks++; try { action(); } catch (T error) { if (fragment == null || error.Message.Contains(fragment)) return; throw new Exception(message + "：错误信息缺少" + fragment); } throw new Exception(message + "：应抛出" + typeof(T).Name); }
    }
}
