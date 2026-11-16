using System;
using System.Collections.Generic;
using System.Globalization;

namespace HarmonicaStudio
{
    public sealed class HotkeyBindingResult
    {
        public readonly string[] Names = new string[4];
        public readonly bool[] Registered = new bool[4];
        public readonly int[] Errors = new int[4];
        public readonly List<string> Changes = new List<string>();
    }

    public static class HotkeyBindings
    {
        public const int AlreadyRegisteredError = 1409;

        // The caller owns window handles and unregisters previous bindings.
        // A registrar returns zero for success or the actual Win32 error.
        public static HotkeyBindingResult Register(string[] desired, Func<int, string, int> tryRegister)
        {
            if (desired == null || desired.Length != 4)
                throw new ArgumentException("需要四个控制热键。", "desired");
            if (tryRegister == null) throw new ArgumentNullException("tryRegister");
            var result = new HotkeyBindingResult();
            var checkedKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int slot = 0; slot < desired.Length; slot++)
            {
                string key = desired[slot];
                int number;
                if (String.IsNullOrEmpty(key) || key[0] != 'F' ||
                    !Int32.TryParse(key.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out number) ||
                    number < 5 || number > 12 || key != "F" + number.ToString(CultureInfo.InvariantCulture) || !checkedKeys.Add(key))
                    throw new ArgumentException("热键应为不重复的F5至F12。", "desired");
                result.Names[slot] = key;
            }
            // Reserve all successful preferences before considering fallback;
            // a later preferred key must never be taken by an earlier failure.
            for (int slot = 0; slot < desired.Length; slot++)
            {
                int error = tryRegister(slot + 1, result.Names[slot]);
                result.Errors[slot] = error;
                result.Registered[slot] = error == 0;
            }
            int[] priority = { 1, 0, 2, 3 };
            foreach (int slot in priority)
            {
                if (result.Registered[slot] || result.Errors[slot] != AlreadyRegisteredError) continue;
                for (int number = 5; number <= 12; number++)
                {
                    string candidate = "F" + number.ToString(CultureInfo.InvariantCulture);
                    // Every function key is attempted at most once per bind,
                    // including a preference already reported unavailable.
                    if (!checkedKeys.Add(candidate)) continue;
                    int error = tryRegister(slot + 1, candidate);
                    result.Errors[slot] = error;
                    if (error == 0)
                    {
                        result.Registered[slot] = true;
                        result.Changes.Add(result.Names[slot] + " → " + candidate);
                        result.Names[slot] = candidate;
                        break;
                    }
                    if (error != AlreadyRegisteredError) break;
                }
            }
            return result;
        }
    }
}
