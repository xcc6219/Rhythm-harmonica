using System;
using System.Collections.Generic;
using System.Linq;
using HarmonicaStudio;

namespace HarmonicaStudio.Tests
{
    public static class PlaybackPlanTests
    {
        private static int checks;

        public static int Run()
        {
            checks = 0;
            ClipAndLoop();
            ExactCycleDoesNotCreatePhantomTail();
            RestsAndTempo();
            PlayableOutputKeepsScoreTime();
            BackgroundSynchronization();
            ValidMelodyTail();
            ConservativeTranscriptionTail();
            ReachableTranscriptionTail();
            ExplicitRangesKeepTheirBoundaries();
            ValidationAndLimits();
            Console.WriteLine("播放区间与循环计划测试通过，共 " + checks + " 项断言。");
            return checks;
        }

        private static void ClipAndLoop()
        {
            NoteEvent[] source = { N(60, 1, 4), N(62, 6, 2) };
            PlaybackPlan plan = PlaybackPlan.Build(source, null, 3, 7, 10, true, 1);
            Check(plan.Notes.Count == 5 && plan.BackgroundTracks == null, "循环按片段时长展开，未出现的背景轨仍为空");
            Near(plan.Duration, 10, "终点为指定十秒");
            Near(plan.Notes[0].Start, 0, "段前已开始的长音裁入零时刻");
            Near(plan.Notes[0].Duration, 2, "长音只保留区间内持续部分");
            Near(plan.Notes[1].Start, 3, "后续音符相对所选开始定位");
            Near(plan.Notes[1].Duration, 1, "片段末尾音符先裁到循环边界");
            Near(plan.Notes[2].Start, 4, "下一遍按整个区间而非末音时刻开始");
            Near(plan.Notes[4].Start + plan.Notes[4].Duration, 10, "最后一遍精确结束且无边界外新音");
            Check(plan.Notes.Zip(plan.Notes.Skip(1), (a, b) => a.Start + a.Duration <= b.Start + 0.0000001).All(x => x), "计划保持自动演奏所需单旋律次序");
            Near(source[0].Start, 1, "不修改原始开始");
            Near(source[1].Duration, 2, "不修改原始长音");
            plan.Notes[0].Pitch = 66;
            Check(source[0].Pitch == 60, "输出音符为独立副本");

            PlaybackPlan shortPlan = PlaybackPlan.Build(new[] { N(60, 0, 10) }, null, 1, 5, 1.5, false, 1);
            Near(shortPlan.Duration, 1.5, "不循环也能缩短目标时长");
            Near(shortPlan.Notes[0].Duration, 1.5, "目标在长音中途结束时裁音");
            PlaybackPlan repeatedHold = PlaybackPlan.Build(new[] { N(60, 0, 10) }, null, 1, 5, 9, true, 1);
            Check(repeatedHold.Notes.Count == 3, "跨区间长音在每个循环独立开始");
            Near(repeatedHold.Notes[2].Duration, 1, "不把末次长音拖过目标终点");
            PlaybackPlan edges = PlaybackPlan.Build(new[] { N(60, 0, 2), N(62, 2, 1), N(64, 5, 1) }, null, 2, 5, 0, false, 1);
            Check(edges.Notes.Count == 1 && edges.Notes[0].Pitch == 62, "开始之前已结束及恰在结束开始的音不进入片段");
        }

        private static void ExactCycleDoesNotCreatePhantomTail()
        {
            NoteEvent[] source = { N(60, 0, .36), N(60, .36, .30) };
            double end = source[1].Start + source[1].Duration;
            PlaybackPlan exact = PlaybackPlan.Build(source, null, .12, end, 1.62, true, 1);
            Check(exact.Notes.Count == 6, "三轮循环在浮点边界处不能生成第七个伪尾音");
            Check(exact.Notes.All(n => n.Duration > .01), "精确循环只含真实音符，不出现机器精度的假截尾");
            Near(exact.Duration, 1.62, "修复伪尾音不改变用户指定的总时长");
            PlaybackPlan partial = PlaybackPlan.Build(source, null, .12, end, 1.62001, true, 1);
            Check(partial.Notes.Count == 7 && partial.Notes[6].Duration > .000009,
                "真实的短截尾必须保留给后续编配判定，不能以宽泛容差静默删除");
            Near(source[0].Duration, .36, "循环边界处理不改源音符");
        }

        private static void RestsAndTempo()
        {
            NoteEvent[] source = { N(60, 2, 1) };
            PlaybackPlan natural = PlaybackPlan.Build(source, null, 0, 8, 0, false, 0.5);
            Near(natural.Notes[0].Start, 2, "不删除片段前导静音");
            Near(natural.Duration, 8, "自然一遍保留末音之后的静音");
            Near(natural.Duration / 0.5, 16, "自然一遍实际时长按速度计算");
            PlaybackPlan loop = PlaybackPlan.Build(source, null, 0, 8, 20, true, 1);
            Check(loop.Notes.Count == 3, "循环保留片段所有休止时间");
            Near(loop.Notes[1].Start, 10, "第二遍带前导休止从第十秒发声");
            Near(loop.Duration, 20, "目标尾静音不会被删除");
            PlaybackPlan faster = PlaybackPlan.Build(new[] { N(60, 0, 4) }, null, 0, 4, 6, true, 1.5);
            Near(faster.Duration, 9, "指定实际六秒在一点五倍速上产生九秒谱面");
            Near(faster.Notes.Last().Duration, 1, "变速目标终点与谱面裁切一致");
            PlaybackPlan slower = PlaybackPlan.Build(new[] { N(60, 0, 4) }, null, 0, 4, 6, false, 0.5);
            Near(slower.Duration, 3, "半速六秒只需要三秒原谱，不误判需要循环");
        }

        private static void BackgroundSynchronization()
        {
            ScoreTrack accompaniment = new ScoreTrack { Name = "PIANO", Index = 9,
                Notes = new List<NoteEvent> { N(40, -2, 5), N(130, 4, 2), N(64, 7, 1) } };
            ScoreTrack melodyTrack = new ScoreTrack { Name = "MELODY", Index = 2,
                Notes = new List<NoteEvent> { N(60, 1, 1) } };
            var tracks = new[] { accompaniment, melodyTrack };
            PlaybackPlan plan = PlaybackPlan.Build(melodyTrack.Notes, tracks, 0, 6, 8, true, 1);
            Check(plan.BackgroundTracks.Count == 2 && plan.BackgroundTracks[0].Index == 9 && plan.BackgroundTracks[1].Index == 2,
                "多轨名称、顺序及原始稀疏轨号保持不变");
            Check(plan.BackgroundTracks[0].Name == "PIANO" && plan.BackgroundTracks[1].Name == "MELODY", "保留真实图例名称");
            Near(plan.BackgroundTracks[0].Notes[0].Start, 0, "负起点伴奏横跨零时刻时正确裁入");
            Near(plan.BackgroundTracks[0].Notes[0].Duration, 3, "负起点背景只保留零之后持续音");
            Check(plan.BackgroundTracks[0].Notes[1].Pitch == 130, "背景域外音保留，绘制端仍能披露隐藏数");
            Near(plan.BackgroundTracks[0].Notes[2].Start, 6, "背景和旋律使用同一六秒循环周期");
            Near(plan.BackgroundTracks[0].Notes[2].Duration, 2, "末次背景长音也裁到八秒终点");
            Near(plan.Notes[1].Start, plan.BackgroundTracks[1].Notes[1].Start, "选中轨背景与实际演奏音同步");
            Check(plan.BackgroundTracks.SelectMany(t => t.Notes).All(n => n.Start >= 0 && n.Start + n.Duration <= plan.Duration),
                "所有背景音均在共同时间轴范围内");
            Near(accompaniment.Notes[0].Start, -2, "不修改负起点源背景");
            Check(!Object.ReferenceEquals(plan.BackgroundTracks[0], accompaniment), "背景轨及音符为独立快照");
        }

        private static void PlayableOutputKeepsScoreTime()
        {
            NoteEvent[] source = { N(40, 0, 0.5), N(60, 0.5, 0.5), N(110, 1, 0.5),
                N(62, 1.5, 0.5), N(110, 2, 0.5) };
            PlaybackPlan output = PlaybackPlan.PreparePlayableOutput(source, 3, 60);
            Check(output.Notes.Count == 2 && output.Notes[0].Pitch == 60 && output.Notes[1].Pitch == 62,
                "输出只保留与自动演奏同一映射可奏的音符");
            Near(output.Notes[0].Start, 0.5, "超域前导音成为休止，不把首个可奏音移到零时刻");
            Near(output.Notes[1].Start, 1.5, "超域中间音成为原时段休止，后续音不提前");
            Near(output.Notes[1].Duration, 0.5, "可奏音时值不被前后休止拉长");
            Near(output.Duration, 3, "超域尾音及原尾休止不缩短指定计划");
            Check(output.Notes[0].Velocity == 90 && source.Length == 5 && source[0].Pitch == 40,
                "保留力度与原始超域音，显示和重新移调仍有原数据");
            Check(!Object.ReferenceEquals(output.Notes[0], source[1]), "可奏输出为独立音符副本");
            output.Notes[0].Start = 9;
            Near(source[1].Start, 0.5, "输出的后续变速不修改显示计划");
            PlaybackPlan silence = PlaybackPlan.PreparePlayableOutput(new[] { N(110, 1, 1) }, 4, 60);
            Check(silence.Notes.Count == 0, "全部超域时输出为空，不伪造静音音符");
            Near(silence.Duration, 4, "纯休止快照仍保留计划终点");
            Check(PlaybackPlan.PreparePlayableOutput(new[] { N(110, 1, 1) }, 4, 96).Notes.Count == 1,
                "根据当前基准音重新判断可奏性，不缓存旧映射");
            var chromatic = Enumerable.Range(0, 128).Select(i => N(i, i, 0.5)).ToArray();
            foreach (int root in new[] { 36, 60, 84 })
                Check(PlaybackPlan.PreparePlayableOutput(chromatic, 128, root).Notes.Select(n => n.Pitch)
                    .SequenceEqual(chromatic.Where(n => HarmonicaMap.Map(n.Pitch, root).Playable).Select(n => n.Pitch)),
                    "全部 MIDI 音高与当前口琴映射一致：" + root);
        }

        private static void ValidMelodyTail()
        {
            NoteEvent[] melody = { N(60, 0, 2), N(64, 3, 2), N(110, 20, 1) };
            ScoreTrack background = new ScoreTrack { Name = "持续背景", Index = 3,
                Notes = new List<NoteEvent> { N(50, 0, 40) } };
            PlaybackPlan plan = PlaybackPlan.Build(melody, new[] { background }, 0, 21, 0, false, 1, TailOptions(false));
            Near(plan.Duration, 5, "末尾超出口琴音域的音不拖长自然演奏");
            Check(plan.Notes.Count == 2 && plan.TailDecision.Applied && plan.TailDecision.Reason == "unplayable-tail", "按音域判定尾音并保存可解释原因");
            Near(plan.TailDecision.SourceEnd, 21, "记录原旋律结束时刻");
            Near(plan.TailDecision.SuggestedEnd, 5, "记录建议有效截止");
            Check(plan.TailDecision.RemovedNotes == 1 && plan.TailDecision.HasPlayableNotes, "记录跳过音数和可吹旋律状态");
            Near(plan.BackgroundTracks[0].Duration, 5, "持续背景只显示到旋律截止而不拖长时长");
            Near(background.Duration, 40, "不改变原背景轨");
            Near(melody[2].Start, 20, "不改变原MIDI尾音位置");

            var shifted = TailOptions(false); shifted.BasePitch = 96;
            PlaybackPlan highBase = PlaybackPlan.Build(melody, null, 0, 21, 0, false, 1, shifted);
            Near(highBase.Duration, 21, "基准音更改后原高尾音可吹时必须保留");
            Check(!highBase.TailDecision.Applied, "不沿用旧基准音的尾音判断");
            PlaybackPlan allOutside = PlaybackPlan.Build(new[] { N(10, 0, 1), N(20, 10, 1) }, null, 0, 11, 0, false, 1, TailOptions(true));
            Check(!allOutside.TailDecision.Applied && !allOutside.TailDecision.HasPlayableNotes && allOutside.TailDecision.Reason == "no-playable-notes", "全无可吹音时不猜测任意截止");
            Near(allOutside.Duration, 11, "全无可吹音保留来源边界供主窗口明确提示");
            Check(allOutside.TailDecision.Message.Contains("没有口琴音域内"), "全无可吹音提示调整声轨和音域");
            Throws<ArgumentException>(delegate { PlaybackPlan.Build(new[] { N(60, 0, 1), N(110, 10, 0) }, null, 0, 11, 0, false, 1, TailOptions(true)); }, "不以自动裁尾隐藏原始无效时长");
            var off = TailOptions(true); off.Enabled = false;
            PlaybackPlan disabled = PlaybackPlan.Build(melody, null, 0, 21, 0, false, 1, off);
            Near(disabled.Duration, 21, "关闭选项恢复完整旋律尾部");
            Check(disabled.Notes.Count == 3 && !disabled.TailDecision.Applied && disabled.TailDecision.Reason == "disabled", "关闭后不自动删尾音");
        }

        private static void ConservativeTranscriptionTail()
        {
            List<NoteEvent> noisy = StrongPhrase();
            noisy.Add(Weak(84, 20, 0.12)); noisy.Add(Weak(85, 21, 0.15));
            PlaybackPlan plan = PlaybackPlan.Build(noisy, null, 0, 21.15, 0, false, 1, TailOptions(true));
            Near(plan.Duration, 11.5, "长静默后短促极弱且偏离旋律的孤立尾音可裁");
            Check(plan.Notes.Count == 12 && plan.TailDecision.Reason == "isolated-weak-tail" && plan.TailDecision.RemovedNotes == 2, "仅删除疑似尾部音并记录数量");
            Check(plan.TailDecision.Message.Contains("疑似") && plan.TailDecision.Message.Contains("可关闭"), "不把尾噪推断写成确定事实且告知恢复选项");
            Check(noisy.Count == 14 && noisy[13].Velocity == 12, "不修改原转录谱或音符力度");
            PlaybackPlan ordinary = PlaybackPlan.Build(noisy, null, 0, 21.15, 0, false, 1, TailOptions(false));
            Near(ordinary.Duration, 21.15, "普通MIDI即便符合弱尾模式也不自动裁噪");
            Check(!ordinary.TailDecision.Applied && ordinary.Notes.Count == 14, "需要音频自动转录来源标识");

            List<NoteEvent> strong = StrongPhrase(); strong.Add(N(84, 20, 0.12)); strong.Add(N(85, 21, 0.15));
            CheckUncut(strong, 21.15, "长静默之后的强尾奏不裁");
            List<NoteEvent> connected = StrongPhrase(); connected.Add(Weak(84, 20, 0.12)); connected.Add(Weak(85, 20.15, 0.15));
            CheckUncut(connected, 20.3, "连贯弱尾奏并非孤立噪声不裁");
            List<NoteEvent> samePhrase = StrongPhrase(); samePhrase.Add(Weak(60, 20, 0.12)); samePhrase.Add(Weak(62, 21, 0.15));
            CheckUncut(samePhrase, 21.15, "间奏之后仍属于近期旋律音域的弱尾奏不裁");
            List<NoteEvent> held = StrongPhrase(); held.Add(Weak(84, 20, 4)); held.Add(Weak(85, 25, 1));
            CheckUncut(held, 26, "弱长音的持续部分不被裁断");
            List<NoteEvent> noSilence = StrongPhrase(); noSilence.Add(Weak(84, 12, 0.12)); noSilence.Add(Weak(85, 13, 0.15));
            CheckUncut(noSilence, 13.15, "无长静默的淡出尾音不裁");
            List<NoteEvent> one = StrongPhrase(); one.Add(Weak(84, 20, 0.12));
            CheckUncut(one, 20.12, "单个弱尾音证据不足不裁");
            List<NoteEvent> many = StrongPhrase(); for (int i = 0; i < 5; i++) many.Add(Weak(84, 20 + i, 0.12));
            CheckUncut(many, 24.12, "较长的弱尾段不按少量孤立尾噪处理");
            List<NoteEvent> allWeak = StrongPhrase(); foreach (NoteEvent note in allWeak) note.Velocity = 20;
            allWeak.Add(Weak(84, 20, 0.12)); allWeak.Add(Weak(85, 21, 0.15));
            CheckUncut(allWeak, 21.15, "全曲弱奏不以固定力度阈值裁尾");
        }

        private static void ReachableTranscriptionTail()
        {
            List<NoteEvent> noisy = TranscriptionTail(80, 32);
            PlaybackPlan plan = PlaybackPlan.Build(noisy, null, 0, 21.15, 0, false, 1, TailOptions(true));
            Near(plan.Duration, 11.5, "v2可产生的力度32孤立弱尾在强主体80后可裁");
            Check(plan.TailDecision.Applied && plan.TailDecision.Reason == "isolated-weak-tail" &&
                plan.TailDecision.RemovedNotes == 2 && plan.Notes.Count == 12, "可达弱尾保留保守原因和准确删除数量");
            Check(noisy.Count == 14 && noisy[0].Velocity == 80 && noisy[13].Velocity == 32,
                "可达性调整只改变演奏计划而不修改原转录谱");

            PlaybackPlan boundary = PlaybackPlan.Build(TranscriptionTail(80, 40), null, 0, 21.15, 0, false, 1, TailOptions(true));
            Near(boundary.Duration, 11.5, "绝对40且主体一半的双阈值边界可达");
            CheckUncut(TranscriptionTail(100, 41), 21.15, "仅相对主体较弱但超过绝对40的真实尾音不裁");
            CheckUncut(TranscriptionTail(64, 35), 21.15, "绝对较弱但超过主体一半的尾音不裁");

            List<NoteEvent> samePhrase = TranscriptionTail(80, 32);
            samePhrase[12].Pitch = 60; samePhrase[13].Pitch = 62;
            CheckUncut(samePhrase, 21.15, "v2弱尾仍在近期旋律音域时保留真实弱结束");
            List<NoteEvent> connected = TranscriptionTail(80, 32); connected[13].Start = 20.15;
            CheckUncut(connected, 20.3, "v2连贯弱尾奏不因新力度阈值而被截断");
            List<NoteEvent> held = TranscriptionTail(80, 32); held[13].Duration = 1;
            CheckUncut(held, 22, "v2真实弱长音结束不被当作短促噪声");
            List<NoteEvent> fade = TranscriptionTail(80, 32);
            fade[12].Start = 12; fade[13].Start = 13;
            CheckUncut(fade, 13.15, "v2无长静默的真实淡出尾音不裁");
            List<NoteEvent> tooEarly = TranscriptionTail(80, 32);
            tooEarly[12].Start = 17.5; tooEarly[13].Start = 18.5;
            CheckUncut(tooEarly, 18.65, "达到六秒但未达到八个起音周期的弱尾仍保留");

            var manual = TailOptions(true); manual.PreserveExplicitRange = true;
            PlaybackPlan range = PlaybackPlan.Build(noisy, null, 0, 21.15, 0, false, 1, manual);
            Near(range.Duration, 21.15, "v2可达弱尾也服从手动指定终点");
            Near(range.TailDecision.SuggestedEnd, 11.5, "手动范围内保留弱尾的建议截止供说明");
            Check(!range.TailDecision.Applied && range.TailDecision.Reason == "manual-range" && range.Notes.Count == 14,
                "手动范围优先且不删除弱尾");
            PlaybackPlan loop = PlaybackPlan.Build(noisy, null, 0, 21.15, 50, true, 1, TailOptions(true));
            Near(loop.Duration, 50, "v2弱尾不改变用户循环总时长");
            Near(loop.Notes[14].Start, 21.15, "下一遍沿用包含弱尾的用户片段边界");
            Check(!loop.TailDecision.Applied && loop.Notes.Count(n => n.Pitch == 84 || n.Pitch == 85) == 4,
                "循环按原边界保留两遍弱尾而不自动缩短周期");
            PlaybackPlan ordinary = PlaybackPlan.Build(noisy, null, 0, 21.15, 0, false, 1, TailOptions(false));
            Near(ordinary.Duration, 21.15, "新弱尾力度阈值仅作用于音频转录来源");
            var off = TailOptions(true); off.Enabled = false;
            PlaybackPlan disabled = PlaybackPlan.Build(noisy, null, 0, 21.15, 0, false, 1, off);
            Near(disabled.Duration, 21.15, "关闭自动处理恢复v2可达弱尾");
            Check(!disabled.TailDecision.Applied && disabled.Notes.Count == 14, "关闭后保留全部v2原音符");
        }

        private static List<NoteEvent> TranscriptionTail(int bodyVelocity, int tailVelocity)
        {
            List<NoteEvent> notes = StrongPhrase();
            foreach (NoteEvent note in notes) note.Velocity = bodyVelocity;
            NoteEvent first = Weak(84, 20, 0.12), second = Weak(85, 21, 0.15);
            first.Velocity = tailVelocity; second.Velocity = tailVelocity;
            notes.Add(first); notes.Add(second);
            return notes;
        }

        private static void ExplicitRangesKeepTheirBoundaries()
        {
            NoteEvent[] source = { N(60, 0, 2), N(64, 3, 2), N(110, 20, 1) };
            var manual = TailOptions(true); manual.PreserveExplicitRange = true;
            PlaybackPlan range = PlaybackPlan.Build(source, null, 0, 21, 0, false, 1, manual);
            Near(range.Duration, 21, "手动指定从零开始的片段仍保留终点");
            Near(range.TailDecision.SuggestedEnd, 5, "手动边界保留时仍有有效末音建议");
            Check(range.TailDecision.Reason == "manual-range" && !range.TailDecision.Applied && range.Notes.Count == 3, "片段优先不会删除用户指定尾音");
            PlaybackPlan shifted = PlaybackPlan.Build(source, null, 1, 21, 0, false, 1, TailOptions(true));
            Near(shifted.Duration, 20, "非零开始的明确片段自动受到保护");
            Near(shifted.TailDecision.SourceEnd, 21, "诊断结束仍使用原谱时钟");
            Near(shifted.TailDecision.SuggestedEnd, 5, "诊断有效截止正确加回片段起点");
            PlaybackPlan timed = PlaybackPlan.Build(source, null, 0, 21, 15, false, 1, TailOptions(true));
            Near(timed.Duration, 15, "自定总时长优先于自然末音");
            Check(!timed.TailDecision.Applied, "自定时长不被自动截尾改短");
            PlaybackPlan loop = PlaybackPlan.Build(source, null, 0, 21, 50, true, 1, TailOptions(true));
            Near(loop.Duration, 50, "循环指定五十秒不会被自动缩短");
            Check(loop.Notes.Count == 8 && loop.Notes.Count(n => n.Pitch == 110) == 2, "循环边界按原片段二十一秒而非有效末音计算");
            Near(loop.Notes[3].Start, 21, "第二遍从用户片段周期起点开始");
            Near(loop.Notes.Last().Start, 45, "末遍不重算原周期");
            PlaybackPlan restLoop = PlaybackPlan.Build(new[] { N(60, 0, 1) }, null, 0, 8, 0, true, 1, TailOptions(true));
            Near(restLoop.Duration, 8, "勾选循环但自然一遍时也保留片段尾休止");
        }

        private static ScoreTailOptions TailOptions(bool transcription)
        { return new ScoreTailOptions { Enabled = true, BasePitch = 60, IsAudioTranscription = transcription }; }
        private static List<NoteEvent> StrongPhrase()
        { return Enumerable.Range(0, 12).Select(i => N(60 + (i % 4) * 2, i, 0.5)).ToList(); }
        private static NoteEvent Weak(int pitch, double start, double duration)
        { return new NoteEvent { Pitch = pitch, Start = start, Duration = duration, Velocity = 12 }; }
        private static void CheckUncut(List<NoteEvent> source, double end, string name)
        {
            PlaybackPlan plan = PlaybackPlan.Build(source, null, 0, end, 0, false, 1, TailOptions(true));
            Near(plan.Duration, end, name); Check(!plan.TailDecision.Applied && plan.Notes.Count == source.Count, name + "，音数不变");
        }

        private static void ValidationAndLimits()
        {
            NoteEvent[] valid = { N(60, 0, 1) };
            Throws<ArgumentNullException>(delegate { PlaybackPlan.Build(null, null, 0, 1, 0, false, 1); }, "拒绝空旋律引用");
            Throws<ArgumentException>(delegate { PlaybackPlan.Build(new NoteEvent[0], null, 0, 1, 0, false, 1); }, "拒绝无音符区间");
            Throws<ArgumentException>(delegate { PlaybackPlan.Build(new[] { N(60, 2, 1) }, null, 0, 4, 1, false, 1); }, "纯静音目标明确提示而非伪造音符");
            Throws<ArgumentException>(delegate { PlaybackPlan.Build(valid, null, 0, 1, 2, false, 1); }, "长于片段必须勾选循环");
            Throws<ArgumentOutOfRangeException>(delegate { PlaybackPlan.Build(valid, null, 1, 1, 0, false, 1); }, "拒绝零长度区间");
            Throws<ArgumentOutOfRangeException>(delegate { PlaybackPlan.Build(valid, null, -1, 1, 0, false, 1); }, "主时间轴拒绝负区间");
            Throws<ArgumentOutOfRangeException>(delegate { PlaybackPlan.Build(valid, null, 0, 1, Double.NaN, true, 1); }, "拒绝非有限目标");
            Throws<ArgumentOutOfRangeException>(delegate { PlaybackPlan.Build(valid, null, 0, 1, 0, false, 0); }, "拒绝零速度");
            Throws<ArgumentOutOfRangeException>(delegate { PlaybackPlan.Build(valid, null, 0, 1, ScoreIO.MaximumDurationSeconds, true, 1.5); }, "保护变速后的谱面总时长");
            Throws<ArgumentException>(delegate { PlaybackPlan.Build(new[] { N(60, -1, 2) }, null, 0, 1, 0, false, 1); }, "负起点仅允许背景轨");
            Throws<ArgumentException>(delegate { PlaybackPlan.Build(new[] { N(60, 0, 0) }, null, 0, 1, 0, false, 1); }, "拒绝无效音符长度");
            Throws<ArgumentException>(delegate { PlaybackPlan.Build(new[] { N(60, 0, 0.00001) }, null, 0, 0.00001, 10, true, 1); }, "循环前预判百万音符拒绝扩张");
            PlaybackPlan boundary = PlaybackPlan.Build(new[] { N(60, 0, 0.001) }, null, 0, 0.001, 200, true, 1);
            Check(boundary.Notes.Count == ScoreIO.MaximumNotes, "二十万音符精确边界可用");
            Throws<ArgumentException>(delegate { PlaybackPlan.Build(new[] { N(60, 0, 0.001) }, null, 0, 0.001, 200.001, true, 1); }, "超过二十万音符明确拒绝");
            ScoreTrack dense = new ScoreTrack { Index = 7, Notes = new List<NoteEvent> { N(50, 0, 0.00009) } };
            Throws<ArgumentException>(delegate { PlaybackPlan.Build(new[] { N(60, 0, 0.00009) }, new[] { dense, dense, dense }, 0, 0.0001, 10, true, 1); },
                "多背景轨循环的合计音符上限同样受控");
        }

        private static NoteEvent N(int pitch, double start, double duration)
        { return new NoteEvent { Pitch = pitch, Start = start, Duration = duration, Velocity = 90 }; }
        private static void Check(bool okay, string name) { checks++; if (!okay) throw new Exception("播放计划测试失败：" + name); }
        private static void Near(double actual, double expected, string name) { Check(Math.Abs(actual - expected) < 0.0000001, name); }
        private static void Throws<T>(Action action, string name) where T : Exception
        { checks++; try { action(); } catch (T) { return; } throw new Exception(name + "：应抛出 " + typeof(T).Name); }
    }
}
