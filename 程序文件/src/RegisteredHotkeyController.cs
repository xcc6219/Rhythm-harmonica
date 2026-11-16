using System;
using System.Collections.Generic;

namespace HarmonicaStudio
{
    // Combines normal Windows hotkey notifications with key-state sampling.
    // Only keys successfully registered by this application are sampled.
    internal sealed class RegisteredHotkeyController
    {
        private readonly Func<string, bool> isDown;
        private readonly Func<bool> modifiersDown;
        private readonly Func<uint> tickCount;
        private readonly Action<int> trigger;
        private readonly List<KeyState> keys = new List<KeyState>();
        private int generation;
        private uint configuredAt;
        internal string LastTriggerSource { get; private set; }

        internal RegisteredHotkeyController(Func<string, bool> isDown, Func<bool> modifiersDown,
            Func<uint> tickCount, Action<int> trigger)
        {
            if (isDown == null) throw new ArgumentNullException("isDown");
            if (modifiersDown == null) throw new ArgumentNullException("modifiersDown");
            if (tickCount == null) throw new ArgumentNullException("tickCount");
            if (trigger == null) throw new ArgumentNullException("trigger");
            this.isDown = isDown; this.modifiersDown = modifiersDown;
            this.tickCount = tickCount; this.trigger = trigger;
            LastTriggerSource = "";
        }

        internal void Configure(HotkeyBindingResult bindings)
        {
            Reset();
            if (bindings == null) return;
            for (int index = 0; index < bindings.Registered.Length; index++)
                if (bindings.Registered[index] && String.IsNullOrEmpty(bindings.Names[index]))
                    throw new ArgumentException("已注册的控制键必须有键名。", "bindings");
            configuredAt = tickCount();
            for (int index = 0; index < bindings.Registered.Length; index++)
            {
                if (!bindings.Registered[index]) continue;
                bool down = isDown(bindings.Names[index]);
                keys.Add(new KeyState { Id = index + 1, Name = bindings.Names[index],
                    WaitForRelease = down, LastUpTick = configuredAt });
            }
        }

        internal void Reset()
        { generation++; keys.Clear(); LastTriggerSource = ""; }

        internal void Poll()
        {
            if (keys.Count == 0) return;
            int current = generation; uint now = tickCount(); bool modified = modifiersDown();
            // A callback can recreate the window and configure a new generation.
            KeyState[] snapshot = keys.ToArray();
            foreach (KeyState key in snapshot)
            {
                Sample(key, isDown(key.Name), modified, now, current);
                if (generation != current) return;
            }
        }

        internal void Receive(int id, uint messageTick)
        {
            KeyState key = keys.Find(item => item.Id == id);
            if (key == null || Before(messageTick, configuredAt) ||
                (key.HasRetiredInterval && !After(messageTick, key.RetiredThrough))) return;
            // Sampling may have observed the whole press before its WM_HOTKEY
            // was dispatched. Consume one matching notification for that press.
            if (ConsumeFallback(key, messageTick)) return;
            uint now = tickCount(); bool down = isDown(key.Name), modified = modifiersDown();
            if (key.WaitForRelease || modified)
            {
                ClearPending(key); key.WaitForRelease = down;
                if (!down) key.LastUpTick = now;
                return;
            }
            // Released keys can have multiple queued genuine notifications with
            // the same millisecond timestamp. Preserve each such notification.
            if (down && key.Messages.Count > 0 && key.Messages[key.Messages.Count - 1] == messageTick) return;
            key.Pending = true; key.Messages.Add(messageTick);
            Sample(key, down, false, now, generation);
        }

        private void Sample(KeyState key, bool down, bool modified, uint now, int current)
        {
            if (key.WaitForRelease || modified)
            {
                ClearPending(key); key.WaitForRelease = down;
                if (!down) key.LastUpTick = now;
                return;
            }
            if (down)
            { key.Pending = true; return; }
            if (key.Pending)
            {
                int count = key.Messages.Count;
                string source = count > 0 ? "Windows 热键消息" : "按键状态监测";
                if (count == 0)
                {
                    count = 1;
                    key.Fallbacks.Add(new ReleaseInterval { Start = key.LastUpTick, End = now });
                    // Bound the history; notifications older than retired
                    // intervals must not become fresh commands later.
                    if (key.Fallbacks.Count > 64)
                    {
                        key.HasRetiredInterval = true; key.RetiredThrough = key.Fallbacks[0].End;
                        key.Fallbacks.RemoveAt(0);
                    }
                }
                ClearPending(key); key.LastUpTick = now;
                for (int index = 0; index < count && generation == current; index++)
                { LastTriggerSource = source; trigger(key.Id); }
            }
            else key.LastUpTick = now;
        }

        private static bool ConsumeFallback(KeyState key, uint messageTick)
        {
            for (int index = 0; index < key.Fallbacks.Count; index++)
            {
                ReleaseInterval interval = key.Fallbacks[index];
                if (Before(messageTick, interval.Start) || After(messageTick, interval.End)) continue;
                key.Fallbacks.RemoveAt(index); return true;
            }
            return false;
        }
        private static void ClearPending(KeyState key) { key.Pending = false; key.Messages.Clear(); }
        private static bool Before(uint first, uint second) { return unchecked((int)(first - second)) < 0; }
        private static bool After(uint first, uint second) { return unchecked((int)(first - second)) > 0; }

        private sealed class KeyState
        {
            internal int Id;
            internal string Name;
            internal bool WaitForRelease, Pending, HasRetiredInterval;
            internal uint LastUpTick, RetiredThrough;
            internal readonly List<uint> Messages = new List<uint>();
            internal readonly List<ReleaseInterval> Fallbacks = new List<ReleaseInterval>();
        }
        private struct ReleaseInterval { internal uint Start, End; }
    }
}
