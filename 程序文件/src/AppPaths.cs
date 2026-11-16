using System;
using System.IO;
using System.Reflection;

namespace HarmonicaStudio
{
    internal static class AppPaths
    {
        internal const string SupportFolderName = "程序文件";
        public static string RootDirectory { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        public static string ContentDirectory
        {
            get
            {
                string support = Path.Combine(RootDirectory, SupportFolderName);
                return Directory.Exists(support) ? support : RootDirectory;
            }
        }
        public static void Initialize()
        {
            string support = Path.Combine(RootDirectory, SupportFolderName);
            Directory.CreateDirectory(support);
            ExtractResources(Assembly.GetExecutingAssembly(), "HarmonicaContent/", support);
        }
        public static string HelpPath()
        {
            string directory = Path.Combine(RootDirectory, "文档中心");
            ExtractResources(Assembly.GetExecutingAssembly(), "HarmonicaDocs/", directory);
            return Path.Combine(directory, "main.html");
        }
        internal static void ExtractResources(Assembly assembly, string prefix, string directory)
        {
            string targetRoot = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string relative = name.Substring(prefix.Length).Replace('/', Path.DirectorySeparatorChar);
                string destination = Path.GetFullPath(Path.Combine(targetRoot, relative));
                if (!destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("内置文件路径无效。");
                if (File.Exists(destination))
                {
                    // Refresh application-owned scripts and browser SDK files before UI startup.
                    // Libraries, browser profiles, settings and recordings remain untouched.
                    if ((prefix == "HarmonicaContent/" && IsManagedAsset(relative)) || prefix == "HarmonicaDocs/")
                        UpdateManagedAsset(assembly, name, destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                WriteAssetAtomically(assembly, name, destination, false);
            }
        }
        private static bool IsManagedAsset(string relative)
        {
            string path = relative.Replace(Path.DirectorySeparatorChar, '/');
            return path == "scripts/setup-audio-runtime.ps1" || path == "scripts/compact-audio-runtime.py" ||
                path == "scripts/audio-to-midi.py" || path == "scripts/audio-requirements.txt" ||
                path == "runtime/webview2/Microsoft.Web.WebView2.Core.dll" ||
                path == "runtime/webview2/Microsoft.Web.WebView2.WinForms.dll" ||
                path == "runtime/webview2/runtimes/win-x64/native/WebView2Loader.dll" ||
                path == "runtime/webview2/runtimes/win-x86/native/WebView2Loader.dll" ||
                path == "runtime/webview2/LICENSE.txt";
        }
        private static void UpdateManagedAsset(Assembly assembly, string name, string destination)
        {
            using (Stream input = assembly.GetManifestResourceStream(name))
            using (var existing = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool same = existing.Length == input.Length;
                var expectedBytes = new byte[8192]; var existingBytes = new byte[8192];
                int count;
                while (same && (count = input.Read(expectedBytes, 0, expectedBytes.Length)) > 0)
                {
                    int read = 0;
                    while (read < count)
                    {
                        int next = existing.Read(existingBytes, read, count - read);
                        if (next == 0) break;
                        read += next;
                    }
                    same = read == count;
                    for (int i = 0; same && i < count; i++) same = expectedBytes[i] == existingBytes[i];
                }
                if (same) return;
            }
            WriteAssetAtomically(assembly, name, destination, true);
        }
        private static void WriteAssetAtomically(Assembly assembly, string name, string destination, bool replace)
        {
            string temporary = destination + ".update-" + Guid.NewGuid().ToString("N");
            try
            {
                using (Stream input = assembly.GetManifestResourceStream(name))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                { input.CopyTo(output); output.Flush(true); }
                if (replace) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
