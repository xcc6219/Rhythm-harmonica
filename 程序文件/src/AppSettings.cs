using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security;

namespace HarmonicaStudio
{
    public sealed class AppSettings
    {
        public double Opacity = 0.9;
        public bool AlwaysOnTop = true;
        public int Transpose = 0;
        public int BasePitch = 60;
        public double Speed = 1.0;
        public bool Compact = false;
        public int X = -1;
        public int Y = -1;
        public bool AutoMode = false;
        public int CountdownSeconds = 5;
        public string StartHotkey = "F5";
        // Legacy setting retained for old configuration files. Playback uses
        // StartHotkey for start, pause and resume; this field reserves no key.
        public string PauseHotkey = "F6";
        public string StopHotkey = "F7";
        public string ToggleWindowHotkey = "F8";
        public string CompactHotkey = "F9";
        public int FullWidth = 1280, FullHeight = 720;
        public int CompactWidth = 900, CompactHeight = 390;
        public string AudioDownloadDirectory = "";
        public bool TrimInvalidTail = true;
        public bool AdaptNearbyPitch = true;
        public List<string> HiddenBuiltinTitles = new List<string>();

        public static AppSettings Load(string path)
        {
            AppSettings result = new AppSettings();
            if (String.IsNullOrWhiteSpace(path)) return result;
            try
            {
                if (!File.Exists(path)) return result;
                SettingsRecord record;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length == 0 || stream.Length > 65536) return result;
                    // The .NET Framework JSON reader does not consume a UTF-8
                    // BOM, which text editors and Encoding.UTF8 may write.
                    byte[] prefix = new byte[3];
                    int prefixLength = stream.Read(prefix, 0, prefix.Length);
                    stream.Position = prefixLength == 3 && prefix[0] == 0xef &&
                        prefix[1] == 0xbb && prefix[2] == 0xbf ? 3 : 0;
                    record = (SettingsRecord)new DataContractJsonSerializer(typeof(SettingsRecord)).ReadObject(stream);
                }
                if (record == null) return result;
                if (record.Opacity.HasValue) result.Opacity = record.Opacity.Value;
                if (record.AlwaysOnTop.HasValue) result.AlwaysOnTop = record.AlwaysOnTop.Value;
                if (record.Transpose.HasValue) result.Transpose = record.Transpose.Value;
                if (record.BasePitch.HasValue) result.BasePitch = record.BasePitch.Value;
                if (record.Speed.HasValue) result.Speed = record.Speed.Value;
                if (record.Compact.HasValue) result.Compact = record.Compact.Value;
                if (record.X.HasValue) result.X = record.X.Value;
                if (record.Y.HasValue) result.Y = record.Y.Value;
                if (record.AutoMode.HasValue) result.AutoMode = record.AutoMode.Value;
                if (record.CountdownSeconds.HasValue) result.CountdownSeconds = record.CountdownSeconds.Value;
                if (record.StartHotkey != null) result.StartHotkey = record.StartHotkey;
                if (record.PauseHotkey != null) result.PauseHotkey = record.PauseHotkey;
                if (record.StopHotkey != null) result.StopHotkey = record.StopHotkey;
                if (record.ToggleWindowHotkey != null) result.ToggleWindowHotkey = record.ToggleWindowHotkey;
                if (record.CompactHotkey != null) result.CompactHotkey = record.CompactHotkey;
                if (record.FullWidth.HasValue) result.FullWidth = record.FullWidth.Value;
                if (record.FullHeight.HasValue) result.FullHeight = record.FullHeight.Value;
                if (record.CompactWidth.HasValue) result.CompactWidth = record.CompactWidth.Value;
                if (record.CompactHeight.HasValue) result.CompactHeight = record.CompactHeight.Value;
                if (record.AudioDownloadDirectory != null) result.AudioDownloadDirectory = record.AudioDownloadDirectory;
                if (record.TrimInvalidTail.HasValue) result.TrimInvalidTail = record.TrimInvalidTail.Value;
                if (record.AdaptNearbyPitch.HasValue) result.AdaptNearbyPitch = record.AdaptNearbyPitch.Value;
                if (record.HiddenBuiltinTitles != null) result.HiddenBuiltinTitles = record.HiddenBuiltinTitles;
                result.Normalize();
            }
            catch (IOException) { return new AppSettings(); }
            catch (UnauthorizedAccessException) { return new AppSettings(); }
            catch (SerializationException) { return new AppSettings(); }
            catch (ArgumentException) { return new AppSettings(); }
            catch (SecurityException) { return new AppSettings(); }
            catch (FormatException) { return new AppSettings(); }
            return result;
        }

        public void Save(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("设置文件路径不能为空。", "path");
            Normalize();
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            Directory.CreateDirectory(directory);
            string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                SettingsRecord record = new SettingsRecord
                {
                    Opacity = Opacity,
                    AlwaysOnTop = AlwaysOnTop,
                    Transpose = Transpose,
                    BasePitch = BasePitch,
                    Speed = Speed,
                    Compact = Compact,
                    X = X,
                    Y = Y,
                    AutoMode = AutoMode,
                    CountdownSeconds = CountdownSeconds,
                    StartHotkey = StartHotkey,
                    PauseHotkey = PauseHotkey,
                    StopHotkey = StopHotkey,
                    ToggleWindowHotkey = ToggleWindowHotkey,
                    CompactHotkey = CompactHotkey,
                    FullWidth = FullWidth, FullHeight = FullHeight,
                    CompactWidth = CompactWidth, CompactHeight = CompactHeight,
                    AudioDownloadDirectory = AudioDownloadDirectory,
                    TrimInvalidTail = TrimInvalidTail,
                    AdaptNearbyPitch = AdaptNearbyPitch,
                    HiddenBuiltinTitles = HiddenBuiltinTitles
                };
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(SettingsRecord)).WriteObject(stream, record);
                    stream.Flush(true);
                }
                if (File.Exists(fullPath))
                    File.Replace(temporaryPath, fullPath, null, true);
                else
                    File.Move(temporaryPath, fullPath);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public void Normalize()
        {
            Opacity = Clamp(Opacity, 0.65, 1.0, 0.9);
            Speed = Clamp(Speed, 0.5, 1.5, 1.0);
            Transpose = Math.Max(-24, Math.Min(24, Transpose));
            BasePitch = Math.Max(36, Math.Min(84, BasePitch));
            if (X < -32000 || X > 32000) X = -1;
            if (Y < -32000 || Y > 32000) Y = -1;
            CountdownSeconds = Math.Max(3, Math.Min(10, CountdownSeconds));
            FullWidth = Math.Max(1032, Math.Min(7680, FullWidth)); FullHeight = Math.Max(620, Math.Min(4320, FullHeight));
            CompactWidth = Math.Max(900, Math.Min(7680, CompactWidth)); CompactHeight = Math.Max(390, Math.Min(4320, CompactHeight));
            if (AudioDownloadDirectory == null || AudioDownloadDirectory.Length > 4096) AudioDownloadDirectory = "";
            var hidden = new List<string>();
            foreach (string title in HiddenBuiltinTitles ?? new List<string>())
                if (!String.IsNullOrWhiteSpace(title) && title.Length <= 160 && !hidden.Contains(title) && hidden.Count < 256)
                    hidden.Add(title);
            HiddenBuiltinTitles = hidden;
            NormalizeHotkeys();
        }

        private void NormalizeHotkeys()
        {
            string[] keys = { StartHotkey, StopHotkey, ToggleWindowHotkey, CompactHotkey };
            string[] defaults = { "F5", "F7", "F8", "F9" };
            HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
            // Reserve all valid unique choices before repairing bad entries, so
            // a fallback does not take a key belonging to a later valid entry.
            for (int i = 0; i < keys.Length; i++)
            {
                string key = (keys[i] ?? "").Trim().ToUpperInvariant();
                int number;
                bool valid = key.Length >= 2 && key[0] == 'F' &&
                    Int32.TryParse(key.Substring(1), out number) && number >= 5 && number <= 12 &&
                    key == "F" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                keys[i] = valid && used.Add(key) ? key : null;
            }
            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i] != null) continue;
                if (used.Add(defaults[i])) keys[i] = defaults[i];
                else
                {
                    for (int number = 5; number <= 12; number++)
                    {
                        string fallback = "F" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (used.Add(fallback)) { keys[i] = fallback; break; }
                    }
                }
            }
            StartHotkey = keys[0];
            StopHotkey = keys[1];
            ToggleWindowHotkey = keys[2];
            CompactHotkey = keys[3];
        }

        private static double Clamp(double value, double min, double max, double fallback)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value)) return fallback;
            return Math.Max(min, Math.Min(max, value));
        }

        // Nullable fields distinguish an omitted key from a deliberate zero or
        // false, preserving defaults when a settings file gains new options.
        [DataContract]
        private sealed class SettingsRecord
        {
            [DataMember(Order = 0)] public double? Opacity;
            [DataMember(Order = 1)] public bool? AlwaysOnTop;
            [DataMember(Order = 2)] public int? Transpose;
            [DataMember(Order = 3)] public int? BasePitch;
            [DataMember(Order = 4)] public double? Speed;
            [DataMember(Order = 5)] public bool? Compact;
            [DataMember(Order = 6)] public int? X;
            [DataMember(Order = 7)] public int? Y;
            [DataMember(Order = 8)] public bool? AutoMode;
            [DataMember(Order = 9)] public int? CountdownSeconds;
            [DataMember(Order = 10)] public string StartHotkey;
            [DataMember(Order = 11)] public string PauseHotkey;
            [DataMember(Order = 12)] public string StopHotkey;
            [DataMember(Order = 13)] public string ToggleWindowHotkey;
            [DataMember(Order = 14)] public string CompactHotkey;
            [DataMember(Order = 15)] public int? FullWidth;
            [DataMember(Order = 16)] public int? FullHeight;
            [DataMember(Order = 17)] public int? CompactWidth;
            [DataMember(Order = 18)] public int? CompactHeight;
            [DataMember(Order = 19)] public string AudioDownloadDirectory;
            [DataMember(Order = 20)] public bool? TrimInvalidTail;
            [DataMember(Order = 21)] public List<string> HiddenBuiltinTitles;
            [DataMember(Order = 22)] public bool? AdaptNearbyPitch;
        }
    }
}
