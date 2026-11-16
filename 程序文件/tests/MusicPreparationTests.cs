using System;
using System.Collections.Generic;
using System.Drawing;
using HarmonicaStudio;

namespace HarmonicaStudio.Tests
{
    public static class MusicPreparationTests
    {
        private static int checks;

        public static int Run()
        {
            checks = 0;
            WholeSongOctavesKeepMusic();
            AdaptationOffAndExtremeNotes();
            IndividualOctaveKeepsMainRegister();
            CrossRegisterIntervalsStayIntact();
            ResidualOutliersDoNotReverseContour();
            NearbyPitchOnlyRescuesRangeEdges();
            ManualTransposeIsNotAutomaticallyUndone();
            NormalMelodyKeepsEveryNote();
            PitchValidation();
            WeakAccompanimentKeepsHeldMelody();
            StrongAndRepeatedAttacksStaySeparate();
            FastRepeatedAttacksStaySeparate();
            MicroGapsAndRealRests();
            ExplicitScoresKeepTheirTiming();
            ContinuousEstimatesKeepTheirTiming();
            MelodyValidationPrecedesFiltering();
            PreferredNamedTracks();
            PrimaryRolePriorityAndExclusions();
            UnnamedMelodyCandidates();
            InferredQualityLabelsAreNotRoles();
            EightResizeAnchors();
            ResizeLimitsAndScreenBounds();
            PortraitRoundingKeepsLimits();
            ResizeValidation();
            Console.WriteLine("旋律准备与等比缩放算法测试通过，共 " + checks + " 项断言（纯算法，无窗口、网络或键鼠输入）。");
            return checks;
        }

        private static void WholeSongOctavesKeepMusic()
        {
            NoteEvent[][] examples = {
                new[] { N(90, 3, .45, 92), N(94, 3.7, .6, 105), N(97, 4.6, .3, 90) },
                new[] { N(30, 2, .5, 100), N(34, 2.8, .4, 84), N(37, 3.5, .6, 94) }
            };
            int[] expectedShifts = { -12, 24 };
            for (int example = 0; example < examples.Length; example++)
            {
                NoteEvent[] source = examples[example];
                NoteEvent[] snapshot = Copy(source);
                PitchAdaptationResult result = PitchAdaptationPolicy.Apply(source, 60, true);
                Check(result.WholeSongShift == expectedShifts[example], "整曲使用同一个合适八度，不能逐音改变原有音程");
                Check(result.Notes.Count == source.Length && result.SkippedCount == 0, "可整体移入音域的旋律全部可奏");
                for (int i = 0; i < source.Length; i++)
                {
                    Check(result.Notes[i].Pitch == source[i].Pitch + expectedShifts[example] && HarmonicaMap.Map(result.Notes[i].Pitch, 60).Playable,
                        "每个输出音均遵守整曲八度且被真实口琴映射接受");
                    Check(result.Notes[i].Start == source[i].Start && result.Notes[i].Duration == source[i].Duration && result.Notes[i].Velocity == source[i].Velocity,
                        "八度适配保留起音、长短、休止间隔与力度");
                    Check(!Object.ReferenceEquals(source[i], result.Notes[i]), "适配输出为独立音符，不能回写原曲");
                    if (i > 0) Check(result.Notes[i].Pitch - result.Notes[i - 1].Pitch == source[i].Pitch - source[i - 1].Pitch,
                        "上行与下行音程保持原曲关系");
                }
                result.Notes[0].Pitch = 60; result.Notes[0].Duration = 8;
                Unchanged(source, snapshot, "整曲适配和后续修改均不能改变源音符");
            }
            PitchAdaptationResult alreadyPlayable = PitchAdaptationPolicy.Apply(new[] { N(60, 0, .4), N(64, .7, .4), N(67, 1.5, .4) }, 60, true);
            Check(alreadyPlayable.WholeSongShift == 0 && alreadyPlayable.AdaptedCount == 0, "原音域已全部可奏时保留原调，不为同样覆盖率随意移八度");
        }

        private static void AdaptationOffAndExtremeNotes()
        {
            NoteEvent[] source = { N(0, .5, .3), N(60, 1.3, .4), N(127, 3, .2) };
            NoteEvent[] snapshot = Copy(source);
            foreach (bool enabled in new[] { false, true })
            {
                PitchAdaptationResult result = PitchAdaptationPolicy.Apply(source, 60, enabled);
                Check(result.WholeSongShift == 0 && result.AdaptedCount == 0 && result.SkippedCount == 2,
                    "极端音高不能靠多次任意折叠伪造成可奏音，关闭适配时也保持明确略过计数");
                Check(result.Notes.Count == 3 && result.Notes[0].Pitch == 0 && result.Notes[2].Pitch == 127,
                    "超域音保留原时间和音高，供演奏计划按休止处理，不压缩后续节奏");
                Near(result.Notes[1].Start, 1.3, "中间正常旋律不能因前方超域休止而提前");
                Unchanged(source, snapshot, "关闭适配及无法修复的极端音均不改源文件");
            }
            PitchAdaptationResult off = PitchAdaptationPolicy.Apply(new[] { N(96, 0, .5), N(60, 1, .5) }, 60, false);
            Check(off.Notes[0].Pitch == 96 && off.SkippedCount == 1 && off.WholeSongShift == 0 && off.AdaptedCount == 0,
                "用户关闭适配时，即使一八度即可救回的音也不自动修改");
        }

        private static void IndividualOctaveKeepsMainRegister()
        {
            NoteEvent[] source = { N(48, 0, 2, 110), N(96, 2.5, .1, 20) };
            NoteEvent[] snapshot = Copy(source);
            PitchAdaptationResult result = PitchAdaptationPolicy.Apply(source, 60, true);
            Check(result.WholeSongShift == 0 && result.Notes[0].Pitch == 48, "零星高音不应迫使主要低声旋律整体移调");
            Check(result.Notes[1].Pitch == 96 && result.OctaveAdjustedCount == 0 && result.SkippedCount == 1,
                "残余96不能逐音折成84伪造旋律；保留高音时刻并明确略过");
            Near(result.Notes[1].Start, 2.5, "残余超域音不删掉音前真实休止");
            Near(result.Notes[1].Duration, .1, "残余超域音不扩展短音");
            Unchanged(source, snapshot, "残余超域音处理不修改原曲");
        }

        private static void CrossRegisterIntervalsStayIntact()
        {
            NoteEvent[] source = { N(49, .3, .4, 102), N(61, 1.2, .35, 95), N(49, 2.1, .55, 104) };
            NoteEvent[] snapshot = Copy(source);
            foreach (bool allowWholeSongShift in new[] { true, false })
            {
                PitchAdaptationResult result = PitchAdaptationPolicy.Apply(source, 60, true, allowWholeSongShift);
                Check(result.WholeSongShift == 0 && result.AdaptedCount == 0 && result.SkippedCount == 0,
                    "49到61再到49均可奏，不能为凑同一键盘音区而逐音升降八度");
                Unchanged(result.Notes, snapshot, "原本可奏的低中音区往返逐项保留音高与节奏");
                Check(result.Notes[1].Pitch - result.Notes[0].Pitch == 12 && result.Notes[2].Pitch - result.Notes[1].Pitch == -12,
                    "跨音区上行十二半音再下行十二半音不能被压成同音或反转");
                IndependentTiming(source, result.Notes, "跨音区旋律");
                result.Notes[1].Pitch = 55; result.Notes[0].Start = 19;
                Unchanged(source, snapshot, "修改跨音区演奏副本不会写回源旋律");
            }
        }

        private static void ResidualOutliersDoNotReverseContour()
        {
            NoteEvent[][] examples = {
                new[] { N(85, .4, .3), N(96, 1.1, .2, 25), N(85, 1.8, .35) },
                new[] { N(49, .4, .3), N(37, 1.1, .2, 25), N(49, 1.8, .35) }
            };
            foreach (NoteEvent[] source in examples)
            {
                NoteEvent[] snapshot = Copy(source);
                PitchAdaptationResult result = PitchAdaptationPolicy.Apply(source, 60, true, false);
                Check(result.WholeSongShift == 0 && result.SkippedCount == 1 && result.OctaveAdjustedCount == 0 && result.NearbyAdjustedCount == 0,
                    "禁用全曲八度后，仅残余超域音略过，不以局部八度把杂音塞回主旋律");
                Unchanged(result.Notes, snapshot, "超域音仍留原时刻和音高供计划作休止，不能拉前后续音符");
                for (int i = 1; i < source.Length; i++)
                    Check(result.Notes[i].Pitch - result.Notes[i - 1].Pitch == source[i].Pitch - source[i - 1].Pitch,
                        "85到96不能折成85到84的反向旋律，49到37也不能折成同音");
                IndependentTiming(source, result.Notes, "残余超域旋律");
                result.Notes[1].Duration = 10;
                Unchanged(source, snapshot, "修改略过音的副本仍不改变源长短或节奏");
            }
        }

        private static void NearbyPitchOnlyRescuesRangeEdges()
        {
            foreach (int basePitch in new[] { 36, 60, 84 })
            {
                int low = basePitch - 12, high = basePitch + 25;
                NoteEvent[] source = { N(low - 1, .2, .3), N(high + 1, .8, .2), N(low - 2, 1.4, .35),
                    N(high + 2, 2.2, .4), N(low - 3, 3.1, .1), N(high + 3, 3.8, .2) };
                NoteEvent[] snapshot = Copy(source);
                PitchAdaptationResult result = PitchAdaptationPolicy.Apply(source, basePitch, true, false);
                Check(result.WholeSongShift == 0 && result.OctaveAdjustedCount == 0 && result.NearbyAdjustedCount == 4 && result.SkippedCount == 2,
                    "仅音域边缘一或二半音允许就近救音，距离三半音不能偷偷折八度");
                Check(result.Notes[0].Pitch == low && result.Notes[1].Pitch == high && result.Notes[2].Pitch == low && result.Notes[3].Pitch == high,
                    "基准60时47和46救到48，86和87救到85；其它基准保持同样边缘规则");
                Check(result.Notes[4].Pitch == low - 3 && result.Notes[5].Pitch == high + 3,
                    "救音范围之外保留原音高和明确略过，而非造一个新的可奏旋律");
                for (int i = 0; i < result.Notes.Count; i++)
                    Check(HarmonicaMap.Map(result.Notes[i].Pitch, basePitch).Playable == (i < 4), "边缘救音与实际口琴映射相符");
                IndependentTiming(source, result.Notes, "音域边缘救音");
                Unchanged(source, snapshot, "边缘救音不修改原曲");
                PitchAdaptationResult off = PitchAdaptationPolicy.Apply(source, basePitch, false, false);
                Check(off.WholeSongShift == 0 && off.AdaptedCount == 0 && off.SkippedCount == 6,
                    "关闭适配时，边缘近音也按用户选择保持超域");
                Unchanged(off.Notes, snapshot, "关闭适配时边缘音逐项保持原始值");
            }
        }

        private static void ManualTransposeIsNotAutomaticallyUndone()
        {
            ScoreTrack track = Track("MELODY", 0, N(63, 5, .4), N(71, 6.1, .5), N(80, 7.7, .3));
            NoteEvent[] snapshot = Copy(track.Notes);
            foreach (int transpose in new[] { 12, -12 })
            {
                PreparedMelody prepared = MelodyPreparation.Build(track, transpose, false);
                NoteEvent[] preparedSnapshot = Copy(prepared.Notes);
                PitchAdaptationResult result = PitchAdaptationPolicy.Apply(prepared.Notes, 60, true, false);
                Check(result.WholeSongShift == 0 && result.OctaveAdjustedCount == 0 && result.NearbyAdjustedCount == 0,
                    "用户已移调时不再反向补一个全曲八度来抵消手动选择");
                Check(result.SkippedCount == (transpose == 12 ? 1 : 0),
                    "手动升八度后的92保留为超域休止；不能被折回原调或变成80");
                for (int i = 0; i < result.Notes.Count; i++)
                    Check(result.Notes[i].Pitch == snapshot[i].Pitch + transpose,
                        "手动正负十二半音在每个最终音高上都实际生效");
                IndependentTiming(prepared.Notes, result.Notes, "手动移调输出");
                Near(result.Notes[1].Start, 1.1, "手动移调后原真实休止与相对源时间保持不变");
                result.Notes[0].Pitch = 0; result.Notes[2].Velocity = 1;
                Unchanged(prepared.Notes, preparedSnapshot, "修改手动移调演奏结果不改准备阶段的源副本");
                Unchanged(track.Notes, snapshot, "手动移调及音域适配不回写原MIDI旋律");
            }
            PreparedMelody high = MelodyPreparation.Build(track, 12, false);
            PitchAdaptationResult defaults = PitchAdaptationPolicy.Apply(high.Notes, 60, true);
            PitchAdaptationResult explicitAutomatic = PitchAdaptationPolicy.Apply(high.Notes, 60, true, true);
            Check(defaults.WholeSongShift == -12 && explicitAutomatic.WholeSongShift == defaults.WholeSongShift,
                "调用未声明手动移调时仍保留原默认覆盖率规则与可选参数兼容性");
            Unchanged(defaults.Notes, explicitAutomatic.Notes, "默认参数与显式允许全曲自动八度具有同一音乐结果");
        }

        private static void NormalMelodyKeepsEveryNote()
        {
            NoteEvent[] source = { N(56, 2, .4), N(61, 2.8, .2), N(65, 3.5, .7),
                N(68, 5, .35), N(73, 5.7, .3), N(75, 6.6, .5) };
            NoteEvent[] snapshot = Copy(source);
            PitchAdaptationResult result = PitchAdaptationPolicy.Apply(source, 60, true);
            Check(result.WholeSongShift == 0 && result.AdaptedCount == 0 && result.SkippedCount == 0,
                "正规MELODY本来覆盖完整音域时不统一抬高或拆分低音");
            Unchanged(result.Notes, snapshot, "正常旋律每个音高、起音、长短和力度全部不变");
            IndependentTiming(source, result.Notes, "正规MELODY");
            Unchanged(source, snapshot, "正常旋律源文件保持不变");
        }

        private static void PitchValidation()
        {
            Throws<ArgumentNullException>(delegate { PitchAdaptationPolicy.Apply(null, 60, true); }, "空音符集合明确拒绝");
            Throws<ArgumentOutOfRangeException>(delegate { PitchAdaptationPolicy.Apply(new NoteEvent[0], -1, true); }, "非法低基准音明确拒绝");
            Throws<ArgumentOutOfRangeException>(delegate { PitchAdaptationPolicy.Apply(new NoteEvent[0], 128, true); }, "非法高基准音明确拒绝");
            Throws<ArgumentException>(delegate { PitchAdaptationPolicy.Apply(new NoteEvent[] { null }, 60, true); }, "空音符不能被覆盖率选择悄悄跳过");
            PitchAdaptationResult empty = PitchAdaptationPolicy.Apply(new NoteEvent[0], 60, true);
            Check(empty.Notes.Count == 0 && empty.SkippedCount == 0 && empty.WholeSongShift == 0, "空旋律保留空结果，无凭空音符或移调");
        }

        private static void WeakAccompanimentKeepsHeldMelody()
        {
            var track = Track("转录主旋律", 7, N(67, 7.4, .6, 95), N(40, 5.5, .1, 30), N(64, 5.02, 2, 100), N(40, 5, .1, 30));
            NoteEvent[] originalOrder = track.Notes.ToArray(), snapshot = Copy(originalOrder);
            PreparedMelody melody = MelodyPreparation.Build(track, 0, true);
            Check(melody.Notes.Count == 2 && melody.SuppressedOverlaps == 2, "相邻弱伴奏及长旋律中的弱低音被抑制，而非切碎主旋律");
            Check(melody.Notes[0].Pitch == 64 && melody.Notes[1].Pitch == 67, "同一起音组选强主音，不能被弱伴奏的先后顺序主导");
            Near(melody.SourceOrigin, 5.02, "源起点对应真正选中的主旋律，而不是已略过的伴奏");
            Near(melody.Notes[0].Start, 0, "主旋律起点归零");
            Near(melody.Notes[0].Duration, 2, "弱伴奏不能截断两秒长旋律");
            Near(melody.Notes[1].Start, 2.38, "保留长音后真实停顿及原始起音关系");
            Check(melody.JoinedMicroGaps == 0, "真实较长休止不能被连接成持续发声");
            Check(track.Notes.Count == originalOrder.Length && Object.ReferenceEquals(track.Notes[0], originalOrder[0]), "旋律排序使用副本，不重排曲库声轨");
            Unchanged(track.Notes, snapshot, "旋律准备不改原有音高、起点、长音或力度");
            melody.Notes[0].Duration = .01;
            Unchanged(track.Notes, snapshot, "修改演奏计划不会写回长音源");
        }

        private static void StrongAndRepeatedAttacksStaySeparate()
        {
            var track = Track("人声", 2, N(64, 1, 2, 100), N(76, 1.7, .2, 75), N(76, 1.85, .1, 20), N(76, 1.98, .2, 60));
            NoteEvent[] snapshot = Copy(track.Notes);
            PreparedMelody melody = MelodyPreparation.Build(track, 0, true);
            Check(melody.Notes.Count == 4 && melody.SuppressedOverlaps == 0, "强远音和较弱同音重复均保留独立起音，不一律当作弱伴奏");
            Near(melody.Notes[0].Duration, .7, "明确新强起音可结束旧长音，不能为长音强行吞掉旋律变化");
            Near(melody.Notes[1].Duration, .15, "新同音起音切分前音，保留真实重复节奏");
            Near(melody.Notes[2].Start, .85, "较弱同音重复仍在原时刻发生");
            Near(melody.Notes[2].Duration, .1, "同音重复之间的短休止不被改成长条");
            Near(melody.Notes[3].Start - melody.Notes[2].Start - melody.Notes[2].Duration, .03, "连续同音的真实离键间隔保留");
            Check(melody.JoinedMicroGaps == 0, "同音重复不使用不同音高的微空隙连接策略");
            Unchanged(track.Notes, snapshot, "保留真实起音时也不能截短原始曲库音符");
        }

        private static void FastRepeatedAttacksStaySeparate()
        {
            var track = Track("连续同音", 0, N(64, 0, .02, 90), N(64, .03, .02, 90), N(67, .3, .2, 90));
            NoteEvent[] snapshot = Copy(track.Notes);
            PreparedMelody melody = MelodyPreparation.Build(track, 0, true);
            Check(melody.Notes.Count == 3 && melody.SuppressedOverlaps == 0,
                "先音已经结束的两次强同音起音，即使相差30ms也不能合并为伴奏候选");
            Near(melody.Notes[1].Start, .03, "快速重复保留第二次起音");
            Near(melody.Notes[1].Start - melody.Notes[0].Start - melody.Notes[0].Duration, .01, "快速重复中的10ms真实离键停顿保留");
            Unchanged(track.Notes, snapshot, "快速重复准备不改源音符");
        }

        private static void MicroGapsAndRealRests()
        {
            var track = Track("主旋律", 0, N(64, 5, .5), N(67, 5.52, .4), N(69, 6.2, .4), N(69, 6.63, .4));
            NoteEvent[] snapshot = Copy(track.Notes);
            PreparedMelody melody = MelodyPreparation.Build(track, 2, true);
            Check(melody.Notes.Count == 4 && melody.JoinedMicroGaps == 1, "只连接不同音高的估计微空隙，保留真实停顿和同音重复");
            Check(melody.Notes[0].Pitch == 66 && melody.Notes[1].Pitch == 69 && melody.Notes[2].Pitch == 71, "转录准备准确应用用户移调");
            Near(melody.SourceOrigin, 5, "记录原录音中的旋律起点");
            Near(melody.Notes[0].Duration, .52, "20ms不同音高估计空隙可连接，避免长旋律卡顿");
            Near(melody.Notes[1].Duration, .4, "280ms真实休止不连接");
            Near(melody.Notes[2].Duration, .4, "30ms同音重新起音的离键间隔不连接");
            Near(melody.Notes[3].Start, 1.63, "微空隙处理不移动后续起音");
            Unchanged(track.Notes, snapshot, "填补估计微空隙不改变录音对应原始谱");
        }

        private static void ExplicitScoresKeepTheirTiming()
        {
            var track = Track("原始MIDI", 0, N(64, 5, .5), N(67, 5.52, .4), N(69, 6.2, .4));
            NoteEvent[] snapshot = Copy(track.Notes);
            PreparedMelody melody = MelodyPreparation.Build(track, 0, false);
            Check(melody.Notes.Count == 3 && melody.SuppressedOverlaps == 0 && melody.JoinedMicroGaps == 0, "明确MIDI旋律不应用转录伴奏过滤或空隙填补");
            Near(melody.Notes[0].Duration, .5, "明确谱的20ms休止同样保留");
            Near(melody.Notes[1].Start, .52, "明确谱只整体归零，不改相对起音");
            Near(melody.SourceOrigin, 5, "明确谱保存原首音时间以便同步背景轨");
            Unchanged(track.Notes, snapshot, "明确谱准备不改源");
            PreparedMelody empty = MelodyPreparation.Build(Track("空轨", 3), 0, true);
            Check(empty.Notes.Count == 0 && empty.SourceOrigin == 0 && empty.JoinedMicroGaps == 0, "空音轨无伪造旋律或起点");
        }

        private static void ContinuousEstimatesKeepTheirTiming()
        {
            ScoreTrack track = Track("连续旋律估计 · 音频自动转录", 0,
                N(64, 1, .2), N(67, 1.22, .15), N(67, 1.4, .1));
            NoteEvent[] snapshot = Copy(track.Notes);
            PreparedMelody melody = MelodyPreparation.Build(track, 0, true);
            Check(melody.Notes.Count == 3 && melody.SuppressedOverlaps == 0 && melody.JoinedMicroGaps == 0,
                "新版连续转录结果不再应用旧复音启发式清理");
            Near(melody.Notes[0].Duration, .2, "连续估计的不同音高短休止不被桌面二次填满");
            Near(melody.Notes[1].Start, .22, "连续估计保存第二音真实起点");
            Near(melody.Notes[2].Start - melody.Notes[1].Start - melody.Notes[1].Duration, .03,
                "连续估计保存重复音间真实停顿");
            Unchanged(track.Notes, snapshot, "连续估计原谱时序保持不变");
        }

        private static void MelodyValidationPrecedesFiltering()
        {
            Throws<FormatException>(delegate { MelodyPreparation.Build(Track("弱错误音", 0, N(60, 0, 1, 100), N(40, .2, -1, 1)), 0, true); },
                "弱伴奏过滤不能隐藏非法负时长");
            Throws<FormatException>(delegate { MelodyPreparation.Build(Track("无效音高", 0, N(60, 0, 1), N(128, .01, .2, 1)), 0, true); },
                "起音组筛选不能绕过原始MIDI音高校验");
            Throws<FormatException>(delegate { MelodyPreparation.Build(Track("移调越界", 0, N(127, 0, 1)), 1, true); },
                "用户移调越过MIDI上限时明确拒绝");
        }

        private static void PreferredNamedTracks()
        {
            foreach (string name in new[] { "主旋律", "人声", "vOcAl main", "MELODY", "Lead guitar" })
            {
                var tracks = new List<ScoreTrack> { Track("鼓和伴奏", 18, N(36, 0, 10)), Track(name, 57, N(64, 2, .2)), Track("第二主旋律", 99, N(72, 0, 1)) };
                Check(MelodyPreparation.PreferredTrack(tracks) == 1, "优先按明确旋律角色选列表位置，不把源轨号或最早伴奏当旋律：" + name);
            }
            Check(MelodyPreparation.PreferredTrack(new[] { Track("钢琴", 31), Track("弦乐", 5) }) == -1, "非空列表的空轨不竞争主旋律候选");
            Check(MelodyPreparation.PreferredTrack(new ScoreTrack[0]) == 0, "空轨列表使用稳定的默认位置，由调用端处理无曲目");
        }

        private static void PrimaryRolePriorityAndExclusions()
        {
            ScoreTrack vocal = Track("VOCAL · 通道 2", 99, N(60, 0, .4));
            ScoreTrack melody = Track("MELODY · 通道 2", 31, N(52, 3, .3, 12), N(55, 3.6, .2, 18));
            ScoreTrack lead = Track("Lead guitar", 77, N(80, 0, 1, 127));
            Check(MelodyPreparation.PreferredTrack(new[] { vocal, melody, lead }) == 1,
                "明确MELODY优先于更早更响更高的人声和LEAD，通道2不误判第二旋律");
            Check(MelodyPreparation.IsPrimaryRole(vocal) && MelodyPreparation.IsPrimaryRole(melody) && MelodyPreparation.IsPrimaryRole(lead),
                "明确角色所选轨必在UI角色白名单中");
            ScoreTrack chinese = Track("主旋律", 8, N(40, 0, .5, 10));
            Check(MelodyPreparation.PreferredTrack(new[] { vocal, chinese }) == 1, "弱且低的明确主旋律不被强人声取代");
            NoteEvent[] snapshot = Copy(melody.Notes);
            PreparedMelody prepared = MelodyPreparation.Build(melody, 0, false);
            Near(prepared.SourceOrigin, 3, "角色选择后准备仍记录原曲起点");
            Near(prepared.Notes[1].Start, .6, "明确主旋律保持原始相邻起音间隔");
            Check(prepared.Notes[0].Pitch == 52 && prepared.Notes[0].Duration == .3 && prepared.Notes[0].Velocity == 12,
                "明确主旋律不因角色选择而改变音高、长短或奏法力度");
            Unchanged(melody.Notes, snapshot, "明确角色选择与准备不写回原MIDI");

            foreach (string name in new[] { "第二主旋律", "副旋律", "次旋律", "MELODY 2", "MELODY II",
                "Second Lead", "secondlead", "SECONDARY VOCALS", "LEAD 2", "BRIDGE", "伴奏", "伴唱", "和声", "BASS", "DRUMS", "鼓" })
            {
                ScoreTrack excluded = Track(name, 3, N(72, 0, .5));
                Check(!MelodyPreparation.IsPrimaryRole(excluded), "第二旋律或明确伴奏不进入主旋律下拉：" + name);
                Check(MelodyPreparation.PreferredTrack(new[] { excluded, melody }) == 1,
                    "前置第二旋律或明确伴奏不抢主旋律：" + name);
                Check(MelodyPreparation.PreferredTrack(new[] { excluded }) == -1, "只有明确排除角色时拒绝冒充主旋律：" + name);
            }
            ScoreTrack empty = Track("MELODY", 0);
            Check(!MelodyPreparation.IsPrimaryRole(empty) && MelodyPreparation.PreferredTrack(new[] { empty, vocal }) == 1,
                "空MELODY不遮挡真实非空人声轨");
            Check(!MelodyPreparation.IsPrimaryRole(Track("Melodyne", 0, N(60, 0, .5))),
                "普通单词内MELODY片段不伪造明确角色");
        }

        private static void UnnamedMelodyCandidates()
        {
            ScoreTrack melody = Track("轨道 2", 73, N(39, 2, .4, 12), N(41, 2.5, .4, 15),
                N(43, 3, .4, 10), N(42, 3.5, .4, 14), N(39, 4, .4, 13));
            NoteEvent[] snapshot = Copy(melody.Notes);
            ScoreTrack bass = Track("BASS", 0, N(36, 0, 12, 127));
            ScoreTrack heldHigh = Track("轨道 1", 99, N(90, 0, 12, 127));
            Check(MelodyPreparation.PreferredTrack(new[] { bass, melody }) == 1, "首轨bass明确排除后选择弱低旋律候选");
            Check(MelodyPreparation.PreferredTrack(new[] { heldHigh, melody }) == 1,
                "无角色候选依连贯变化而不是最高音或最响轨选择低音独奏");
            Check(!MelodyPreparation.IsPrimaryRole(melody), "算法候选不伪造明确主旋律标签");
            ScoreTrack chord = Track("钢琴", 0, N(60, 0, .4), N(64, 0, .4), N(67, 0, .4),
                N(62, .5, .4), N(65, .5, .4), N(69, .5, .4));
            NoteEvent[] chordSnapshot = Copy(chord.Notes);
            Check(MelodyPreparation.PreferredTrack(new[] { chord, melody }) == 1, "首轨复音钢琴和弦不盲选为单旋律");
            Check(MelodyPreparation.PreferredTrack(new[] { chord }) == -1, "只有复音和弦且无明确旋律时拒绝猜测主旋律");
            ScoreTrack piano = Track("PIANO", 7, N(48, 0, .4), N(50, .5, .4), N(53, 1, .4));
            Check(MelodyPreparation.PreferredTrack(new[] { chord, piano }) == 1,
                "单旋律PIANO可作独奏候选，不因乐器名称一概排除");
            Check(MelodyPreparation.PreferredTrack(new[] { Track("钢琴", 0, N(37, 0, .5), N(40, .8, .5), N(43, 1.5, .5)) }) == 0,
                "真实低音区钢琴独奏仍可载入，不硬塞中高音区");
            Check(MelodyPreparation.PreferredTrack(new[] { Track("Bassoon", 0, N(48, 0, .4), N(50, .5, .4)) }) == 0,
                "BASSOON不是明确BASS角色，单旋律木管可作候选");
            Check(MelodyPreparation.PreferredTrack(new[] { Track("空MELODY", 0), melody }) == 1,
                "空角色轨不参与无角色候选竞争");
            Unchanged(melody.Notes, snapshot, "候选打分不重排或修改源旋律起音、时长、音高与力度");
            Unchanged(chord.Notes, chordSnapshot, "候选选择不清理或改写背景和弦");
            Throws<ArgumentNullException>(delegate { MelodyPreparation.PreferredTrack(null); }, "空候选集合明确拒绝");
            Throws<FormatException>(delegate { MelodyPreparation.PreferredTrack(new[] { Track("未知轨", 0, N(60, 0, -1)) }); },
                "候选打分不以选择策略掩盖非法源时长");
        }

        private static void InferredQualityLabelsAreNotRoles()
        {
            foreach (string warning in new[] { "伴奏难区分", "低音可能干扰", "短音较多", "候选需核对", "伴奏难区分 / 低音可能干扰 / 短音未定" })
            {
                ScoreTrack candidate = Track("歌曲 · 连续旋律估计 · " + warning + " · 需试听 · 音频自动转录", 4,
                    N(60, .3, .3, 32), N(62, .8, .3, 36), N(65, 1.3, .4, 42));
                Check(MelodyPreparation.PreferredTrack(new[] { Track("BASS", 0, N(36, 0, 4)), candidate }) == 1,
                    "转录质量警示不能误判为明确伴奏角色并拒载：" + warning);
                Check(!MelodyPreparation.IsPrimaryRole(candidate), "转录候选的质量警示不变成明确角色：" + warning);
            }
            foreach (string title in new[] { "MELODY", "主旋律", "BASS", "BRIDGE", "伴奏", "第二主旋律" })
            {
                ScoreTrack estimate = Track(title + " · 连续旋律估计 · 伴奏难区分 · 音频自动转录", 0,
                    N(60, 0, .4), N(62, .5, .4), N(65, 1, .4));
                Check(!MelodyPreparation.IsPrimaryRole(estimate), "估计歌曲title不冒充人工明确角色：" + title);
                Check(MelodyPreparation.PreferredTrack(new[] { estimate }) == 0, "估计歌曲title不冒充明确伴奏而排除候选：" + title);
                Check(MelodyPreparation.PreferredTrack(new[] { estimate, Track("人声", 1, N(52, 2, .5, 15)) }) == 1,
                    "真实明确人声角色仍优先于title碰巧像角色的估计轨：" + title);
            }
        }

        private static void EightResizeAnchors()
        {
            var proposal = new Rectangle(-300, -200, 1100, 760);
            const double ratio = 16.0 / 9;
            for (int edge = 1; edge <= 8; edge++)
            {
                Rectangle fit = AspectResize.Fit(proposal, edge, ratio, new Size(900, 500), new Size(2000, 1100));
                Check(fit.Width > 0 && fit.Height > 0, "八个缩放方向均输出有效矩形");
                Ratio(fit, ratio, "八个方向的尺寸均保持原窗口比例");
                switch (edge)
                {
                    case 1: Check(fit.Right == proposal.Right && fit.Top == proposal.Top, "拖左边固定右边和顶部"); break;
                    case 2: Check(fit.Left == proposal.Left && fit.Top == proposal.Top, "拖右边固定左边和顶部"); break;
                    case 3: Check(fit.Bottom == proposal.Bottom && fit.Left == proposal.Left, "拖上边固定底部和左边"); break;
                    case 4: Check(fit.Right == proposal.Right && fit.Bottom == proposal.Bottom, "拖左上角固定右下角"); break;
                    case 5: Check(fit.Left == proposal.Left && fit.Bottom == proposal.Bottom, "拖右上角固定左下角"); break;
                    case 6: Check(fit.Top == proposal.Top && fit.Left == proposal.Left, "拖下边固定顶部和左边"); break;
                    case 7: Check(fit.Right == proposal.Right && fit.Top == proposal.Top, "拖左下角固定右上角"); break;
                    case 8: Check(fit.Left == proposal.Left && fit.Top == proposal.Top, "拖右下角固定左上角"); break;
                }
                Check(edge == 3 || edge == 6 ? fit.Height == 760 : fit.Width == 1100,
                    "边拖动使用当前实际改动轴，角拖动依宽度完成等比约束");
            }
        }

        private static void ResizeLimitsAndScreenBounds()
        {
            foreach (int edge in new[] { 1, 3, 4, 6, 8 })
            {
                Rectangle small = AspectResize.Fit(new Rectangle(40, 50, 70, 40), edge, 2, new Size(900, 390), new Size(1450, 700));
                Check(small.Size == new Size(900, 450), "最小宽度优先时连带保持等比高度");
                Rectangle tallMin = AspectResize.Fit(new Rectangle(40, 50, 70, 40), edge, 2, new Size(900, 520), new Size(1450, 700));
                Check(tallMin.Size == new Size(1040, 520), "最小高度也须完整生效，不能只限制宽度");
                Rectangle maxHeight = AspectResize.Fit(new Rectangle(40, 50, 2500, 1700), edge, 2, new Size(900, 390), new Size(1450, 600));
                Check(maxHeight.Size == new Size(1200, 600), "屏幕最大高度限制整个窗口，而非把底部说明挤出屏幕");
                Rectangle maxWidth = AspectResize.Fit(new Rectangle(40, 50, 2500, 1700), edge, 2, new Size(900, 390), new Size(1100, 700));
                Check(maxWidth.Size == new Size(1100, 550), "屏幕最大宽度也须保持比例");
            }
            Rectangle constrained = AspectResize.Fit(new Rectangle(50, 70, 900, 390), 8, 900.0 / 390, new Size(900, 390), new Size(700, 300));
            Check(constrained.Width <= 700 && constrained.Height <= 300 && constrained.Width > 0 && constrained.Height > 0,
                "工作区小于常规最小窗时优先留在可用屏幕，不能输出负尺寸或超屏窗口");
            Ratio(constrained, 900.0 / 390, "工作区兜底仍保留窗口比例");
        }

        private static void PortraitRoundingKeepsLimits()
        {
            Rectangle minimum = AspectResize.Fit(new Rectangle(20, 30, 190, 600), 6, .334, new Size(100, 600), new Size(300, 1000));
            Check(minimum.Width >= 100 && minimum.Height >= 600 && minimum.Width <= 300 && minimum.Height <= 1000,
                "合法竖向比例的整数取整不能把可满足的最小高度600变成599");
            Ratio(minimum, .334, "最小高度夹取后竖向比例最多有半像素宽度误差");
            Rectangle maximum = AspectResize.Fit(new Rectangle(20, 30, 290, 1000), 3, .333, new Size(100, 200), new Size(300, 600));
            Check(maximum.Width >= 100 && maximum.Height >= 200 && maximum.Width <= 300 && maximum.Height <= 600,
                "合法竖向比例的整数取整不能把屏幕最大高度600变成601");
            Check(maximum.Bottom == 1030 && maximum.Left == 20, "取整守界仍保留上边拖动的底部锚点");
            Ratio(maximum, .333, "最大高度夹取后也保持最接近可表示的比例");
        }

        private static void ResizeValidation()
        {
            foreach (double ratio in new[] { 0.0, -1.0, Double.NaN, Double.PositiveInfinity })
                Throws<ArgumentOutOfRangeException>(delegate { AspectResize.Fit(new Rectangle(0, 0, 900, 390), 8, ratio, new Size(900, 390), new Size(1400, 800)); },
                    "非法宽高比在调整前明确拒绝");
        }

        private static ScoreTrack Track(string name, int index, params NoteEvent[] notes)
        { return new ScoreTrack { Name = name, Index = index, Notes = new List<NoteEvent>(notes) }; }
        private static NoteEvent N(int pitch, double start, double duration, int velocity = 90)
        { return new NoteEvent { Pitch = pitch, Start = start, Duration = duration, Velocity = velocity }; }
        private static NoteEvent[] Copy(IList<NoteEvent> source)
        {
            var copy = new NoteEvent[source.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = N(source[i].Pitch, source[i].Start, source[i].Duration, source[i].Velocity);
            return copy;
        }
        private static void IndependentTiming(IList<NoteEvent> source, IList<NoteEvent> output, string name)
        {
            Check(source.Count == output.Count, name + "保留音符数量，略过也不删除时间节点");
            for (int i = 0; i < source.Count; i++)
            {
                Check(!Object.ReferenceEquals(source[i], output[i]), name + "输出音符是独立副本");
                Check(source[i].Start == output[i].Start && source[i].Duration == output[i].Duration && source[i].Velocity == output[i].Velocity,
                    name + "保留起音、持续、力度及所有真实休止间隔");
            }
        }
        private static void Unchanged(IList<NoteEvent> actual, IList<NoteEvent> original, string name)
        {
            bool same = actual.Count == original.Count;
            for (int i = 0; same && i < actual.Count; i++) same = actual[i].Pitch == original[i].Pitch && actual[i].Start == original[i].Start &&
                actual[i].Duration == original[i].Duration && actual[i].Velocity == original[i].Velocity;
            Check(same, name);
        }
        private static void Ratio(Rectangle rectangle, double ratio, string name)
        { Check(Math.Abs(rectangle.Width - rectangle.Height * ratio) <= Math.Max(1, ratio) * .51, name); }
        private static void Near(double actual, double expected, string name)
        { Check(Math.Abs(actual - expected) < .0000001, name); }
        private static void Throws<T>(Action action, string name) where T : Exception
        {
            try { action(); } catch (T) { Check(true, name); return; }
            throw new Exception("旋律准备测试失败：" + name + " 未抛出 " + typeof(T).Name);
        }
        private static void Check(bool okay, string name)
        { if (!okay) throw new Exception("旋律准备测试失败：" + name); checks++; }
    }
}
