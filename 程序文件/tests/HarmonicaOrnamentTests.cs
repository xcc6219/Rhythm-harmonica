using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonicaStudio;

namespace HarmonicaStudio.Tests
{
    internal static class HarmonicaOrnamentTests
    {
        private static int checks;
        private static Type core;

        public static int Run()
        {
            checks = 0;
            core = typeof(PlaybackPlan).Assembly.GetType("HarmonicaStudio.HarmonicaOrnaments");
            Check(core != null, "提供独立的保守装饰音简化模块");
            NecessaryNeighbors();
            DisabledCopies();
            OnlyWhenNecessary();
            ProtectedMusic();
            OriginalAnchorsAndTiming();
            Validation();
            Console.WriteLine("保守装饰音简化测试通过，共 " + checks + " 项断言（合成音符、无声音或键鼠输入）。");
            return checks;
        }

        private static void NecessaryNeighbors()
        {
            foreach (int step in new[] { -2, -1, 1, 2 })
            {
                var source = new[] { N(60, .4, .2, 95), N(60 + step, .6, .03, 60), N(60, .63, .24, 90) };
                var before = Copy(source);
                var originalTiming = HarmonicaArrangement.Analyze(source, .87, false, 60, 1);
                Check(originalTiming.EffectiveSpeed < 1, "原三音确实不能保持请求速度");
                object result = Simplify(source, 60, 1, true);
                List<NoteEvent> notes = Notes(result);
                Check(Removed(result) == 1 && notes.Count == 2, "仅省去一或二半音的短弱回返候选");
                Check(notes[0].Pitch == 60 && notes[1].Pitch == 60, "保留两侧主音与重复起音");
                Near(notes[0].Start, .4, "不移动前导时间与前锚点起音");
                Near(notes[0].Duration, .23, "仅把前锚点延长到后锚点起音");
                Near(notes[1].Start, .63, "后锚点起音不提前");
                Near(notes[1].Duration, .24, "后锚点音长不变");
                Check(notes[0].Velocity == 95 && notes[1].Velocity == 90, "两侧主音力度不变");
                Near(notes[1].Start + notes[1].Duration, .87, "总时长不变");
                Same(source, before, "源对象不变");
                Check(!Object.ReferenceEquals(notes[0], source[0]) && !Object.ReferenceEquals(notes[1], source[2]), "保留音符也必须是独立副本");
                var timing = HarmonicaArrangement.Analyze(notes, .87, false, 60, 1);
                Check(timing.CanPerform && timing.EffectiveSpeed == 1, "简化后仍通过统一编配的请求速度分析");
                var plan = PlaybackPlan.Build(notes, null, 0, .87, 0, false, 1);
                var arranged = HarmonicaArrangement.Prepare(plan, 60, timing.EffectiveSpeed);
                Check(arranged.Notes.Count == 2 && arranged.Notes[0].Duration >= .045, "后续编配仍保留两次主音且检查持音");
                Check(arranged.Notes[1].Start - arranged.Notes[0].Start - arranged.Notes[0].Duration >= 20.0 / 960,
                    "后续编配仍为重复主音预留安全重触间隔");
            }
        }

        private static void DisabledCopies()
        {
            var source = Triplet(60, 61, 60, .2, .03, .2);
            object result = Simplify(source, 60, 1, false);
            Check(Removed(result) == 0, "关闭选项时不删除任何音符");
            Same(Notes(result), source, "关闭选项完全保留音符字段");
            for (int i = 0; i < source.Length; i++) Check(!Object.ReferenceEquals(Notes(result)[i], source[i]), "关闭时也返回深复制");
            Notes(result)[0].Duration = 9;
            Near(source[0].Duration, .2, "后续编辑输出不会改写关闭时的源谱");
        }

        private static void OnlyWhenNecessary()
        {
            Keep(Triplet(60, 62, 60, .3, .05, .3), 1, "可奏的五十毫秒自然邻音保留");
            Keep(Triplet(60, 61, 60, .3, .083, .3), 1, "已有足够换修饰间隔的短邻音保留");
            Check(Removed(Simplify(Triplet(60, 61, 60, .3, .08, .3), 60, 1, true)) == 1,
                "门长足够但八十毫秒仍不足量化安全换指时才省去候选");
            Check(Removed(Simplify(Triplet(60, 62, 60, .2, .0455, .2), 60, 1, true)) == 1,
                "简化必要性使用四十四ticks安全门长而非粗略四十五毫秒");
            Keep(Triplet(60, 62, 60, .2, .046, .2), 1, "达到量化安全门长的自然邻音不省略");
            var accelerated = Triplet(60, 62, 60, .3, .06, .3);
            Keep(accelerated, 1, "原速可奏时不为偏好而删音");
            Check(Removed(Simplify(accelerated, 60, 1.5, true)) == 1, "必要性根据当前请求速度计算");
            Keep(accelerated, .5, "慢速时完整保留同一短音");
            Keep(new[] { N(60, 0, .2, 90), N(62, .2, .05, 50), N(60, .25, .2, 90), N(65, .45, .01, 90) },
                1, "其他位置不可奏也不能成为删除本来可奏邻音的理由");
            var clipped = PlaybackPlan.Build(new[] { N(60, 0, .3, 90), N(61, .3, .06, 35), N(60, .36, .3, 90) },
                null, .12, .66, 0, false, 1);
            Check(Removed(Simplify(clipped.Notes, 60, 1, true)) == 1, "片段裁切后恰好三倍长的前锚点仍可保护主音并简化邻音");
            foreach (double origin in new[] { 0.0, 100.001, 14300.123 })
            {
                var boundary = PlaybackPlan.Build(new[] { N(60, origin, .12, 90), N(61, origin + .12, .04, 35),
                    N(60, origin + .16, .12, 90) }, null, origin, origin + .28, 0, false, 1);
                Check(Removed(Simplify(boundary.Notes, 60, 1, true)) == 1,
                    "不同源时钟位置恰好三倍且一百二十毫秒的锚点不受浮点端点相减影响");
            }
        }

        private static void ProtectedMusic()
        {
            var strong = Triplet(60, 61, 60, .2, .03, .2); strong[1].Velocity = 100;
            Keep(strong, 1, "强音不能按装饰音删除");
            var oneWeakAnchor = Triplet(60, 61, 60, .2, .03, .2); oneWeakAnchor[0].Velocity = 40;
            Keep(oneWeakAnchor, 1, "候选必须不强于左右任一锚点");
            Keep(Triplet(60, 61, 62, .2, .03, .2), 1, "上行旋律串保留");
            Keep(Triplet(62, 61, 60, .2, .03, .2), 1, "下行旋律串保留");
            Keep(Triplet(60, 60, 60, .2, .03, .2), 1, "同音重复节奏保留");
            Keep(Triplet(60, 63, 60, .2, .03, .2), 1, "超过二半音的回返主音保留");
            Keep(Triplet(48, 46, 48, .2, .03, .2), 1, "超域音不得借装饰音规则改写");
            Keep(Triplet(60, 61, 60, .3, .091, .3), 1, "超过九十毫秒的邻音保留");
            Keep(Triplet(60, 61, 60, .119, .03, .2), 1, "前锚点不足一百二十毫秒时保留");
            Keep(Triplet(60, 61, 60, .2, .03, .119), 1, "后锚点不足一百二十毫秒时保留");
            Keep(Triplet(60, 61, 60, .2, .07, .3), 1, "前锚点不足候选三倍时保留");
            Keep(Triplet(60, 61, 60, .3, .07, .2), 1, "后锚点不足候选三倍时保留");
            var restBefore = Triplet(60, 61, 60, .2, .03, .2);
            restBefore[1].Start += 1.0 / 960; restBefore[2].Start += 1.0 / 960;
            Keep(restBefore, 1, "一个MIDI tick的前休止也不能填平");
            var restAfter = Triplet(60, 61, 60, .2, .03, .2); restAfter[2].Start += .01;
            Keep(restAfter, 1, "候选后的真实休止不能填平");
            var trill = new List<NoteEvent>();
            for (int i = 0; i < 12; i++) trill.Add(N(60 + i % 2, i * .06, .06, 80));
            Keep(trill, 1, "长段连续颤音不能逐音删除");
            Keep(new[] { N(60, 0, .03, 50), N(61, .03, .2, 90), N(60, .23, .03, 50) }, 1, "首尾短音不可删除");
            var equal = Triplet(60, 61, 60, .2, .03, .2); equal[1].Velocity = 90;
            Check(Removed(Simplify(equal, 60, 1, true)) == 1, "同力度仍须满足短邻音与必要性条件才允许候选");
        }

        private static void OriginalAnchorsAndTiming()
        {
            var source = new[] { N(67, 0, .2, 93), N(60, .2, .2, 90), N(61, .4, .03, 60),
                N(60, .43, .2, 91), N(61, .63, .03, 50), N(60, .66, .2, 94), N(65, .86, .2, 88) };
            object result = Simplify(source, 60, 1, true); var notes = Notes(result);
            Check(Removed(result) == 2 && notes.Count == 5, "一次原始序列扫描可处理两个独立短邻音");
            int[] retained = { 0, 1, 3, 5, 6 };
            for (int i = 0; i < retained.Length; i++)
            {
                Check(notes[i].Pitch == source[retained[i]].Pitch && notes[i].Velocity == source[retained[i]].Velocity, "锚点与其余音高力度保留");
                Near(notes[i].Start, source[retained[i]].Start, "受保护锚点不能再次删除或移动");
            }
            Near(notes[0].Duration, .2, "候选前无关音不延长");
            Near(notes[1].Duration, .23, "第一锚点只延至下一原锚点");
            Near(notes[2].Duration, .23, "共享原锚点仍保留独立起音");
            Near(notes[3].Duration, .2, "最后锚点原音长保留");
            Near(notes[4].Start + notes[4].Duration, 1.06, "整个序列总时长不变");
            Check(notes[1].Pitch == notes[2].Pitch && notes[2].Pitch == notes[3].Pitch && notes[1].Start != notes[2].Start,
                "简化不迭代合并为一个长主音");
            var micro = Triplet(60, 61, 60, .2, .03, .2);
            micro[1].Start += .00000005; micro[2].Start += .00000005;
            Check(Removed(Simplify(micro, 60, 1, true)) == 1, "只容许浮点量级连续性微差");
            var remainingShort = new[] { N(60, 0, .2, 90), N(61, .2, .03, 50), N(60, .23, .2, 90), N(65, .43, .01, 90) };
            var partial = Notes(Simplify(remainingShort, 60, 1, true));
            Check(partial.Count == 3 && partial[2].Pitch == 65 && partial[2].Duration == .01,
                "只省略获准候选，不删除剩余极短末音来制造通过");
            Check(!HarmonicaArrangement.Analyze(partial, .44, false, 60, 1).CanPerform,
                "简化之后仍必须由统一编配拒绝剩余不满足要求的音符");
        }

        private static void Validation()
        {
            Check(Notes(Simplify(new NoteEvent[0], 60, 1, true)).Count == 0, "空序列保留为空");
            Throws<ArgumentNullException>(delegate { Simplify(null, 60, 1, true); }, "拒绝空集合引用");
            foreach (int pitch in new[] { -1, 128 }) Throws<ArgumentOutOfRangeException>(delegate { Simplify(new NoteEvent[0], pitch, 1, true); }, "拒绝非法基准音");
            foreach (double speed in new[] { .49, 1.51, Double.NaN, Double.PositiveInfinity })
                Throws<ArgumentOutOfRangeException>(delegate { Simplify(new NoteEvent[0], 60, speed, true); }, "拒绝非法请求速度");
            foreach (NoteEvent note in new[] { null, N(128, 0, .1, 90), N(60, 0, .1, 0), N(60, -.1, .1, 90), N(60, 0, Double.NaN, 90) })
                Throws<ArgumentException>(delegate { Simplify(new[] { note }, 60, 1, true); }, "简化之前验证全部音符");
            Throws<ArgumentException>(delegate { Simplify(new[] { N(60, 0, .2, 90), N(61, .1, .2, 90) }, 60, 1, true); }, "不以简化掩盖复音重叠");
        }

        private static object Simplify(IList<NoteEvent> notes, int basePitch, double speed, bool enabled)
        {
            try { return core.GetMethod("Simplify", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { notes, basePitch, speed, enabled }); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        private static List<NoteEvent> Notes(object result) { return (List<NoteEvent>)result.GetType().GetField("Notes", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(result); }
        private static int Removed(object result) { return (int)result.GetType().GetField("RemovedCount", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(result); }
        private static void Keep(IList<NoteEvent> source, double speed, string message)
        {
            var before = Copy(source); object result = Simplify(source, 60, speed, true);
            Check(Removed(result) == 0, message); Same(Notes(result), before, message); Same(source, before, "保留路径不修改源对象");
        }
        private static NoteEvent[] Triplet(int first, int middle, int last, double a, double b, double c)
        { return new[] { N(first, 0, a, 90), N(middle, a, b, 60), N(last, a + b, c, 90) }; }
        private static NoteEvent N(int pitch, double start, double duration, int velocity)
        { return new NoteEvent { Pitch = pitch, Start = start, Duration = duration, Velocity = velocity }; }
        private static List<NoteEvent> Copy(IList<NoteEvent> source)
        { var result = new List<NoteEvent>(); foreach (var note in source) result.Add(N(note.Pitch, note.Start, note.Duration, note.Velocity)); return result; }
        private static void Same(IList<NoteEvent> actual, IList<NoteEvent> expected, string message)
        {
            Check(actual.Count == expected.Count, message + "音数");
            for (int i = 0; i < actual.Count; i++) Check(actual[i].Pitch == expected[i].Pitch && actual[i].Start == expected[i].Start &&
                actual[i].Duration == expected[i].Duration && actual[i].Velocity == expected[i].Velocity, message + "字段");
        }
        private static void Near(double actual, double expected, string message) { Check(Math.Abs(actual - expected) < .000000001, message); }
        private static void Check(bool value, string message) { checks++; if (!value) throw new Exception("装饰音简化：" + message); }
        private static void Throws<T>(Action action, string message) where T : Exception
        { checks++; try { action(); } catch (T) { return; } throw new Exception(message); }
    }
}
