using System;
using System.Windows.Forms;
using HarmonicaStudio.Tests;

public static class AllTests
{
    [STAThread]
    public static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            if (args.Length == 1 && args[0] == "--packaging-only") { PackagingTests.Run(); return 0; }
            if (args.Length == 1 && args[0] == "--installer-only") { AudioRuntimeInstallerTests.Run(); return 0; }
            if (args.Length == 1 && args[0] == "--details-only")
            {
                SettingsAudioTests.Run(); HotkeyBindingTests.Run();
                RegisteredHotkeyControllerTests.Run(); RuntimeControlTests.Run();
                Console.WriteLine("模拟后端自动演奏测试通过，共 " + AutoPlayerTests.Run() + " 项断言。");
                OnlineDialogTests.Run(); AutoScoreWorkflowTests.Run();
                SongFileLibraryTests.Run(); LibraryConversionTests.Run(); SongLibraryStoreTests.Run(); MaintenanceWorkflowTests.Run(); return 0;
            }
            if (args.Length == 1 && args[0] == "--song-flow-only")
            { AutoScoreWorkflowTests.Run(); SongFileLibraryTests.Run(); LibraryConversionTests.Run(); SongLibraryStoreTests.Run(); MaintenanceWorkflowTests.Run(); PackagingTests.Run(); return 0; }
            if (args.Length == 1 && args[0] == "--waterfall-only")
            {
                RhythmCanvasTests.Run(); WaterfallReadabilityTests.Run(); LandscapeLayoutTests.Run();
                WindowPresentationTests.Run(); PlaybackWindowTests.Run(); return 0;
            }
            if (EngineTests.Main() != 0 || MidiWriterTests.Main() != 0) return 1;
            bool withoutForeground = args.Length == 1 && args[0] == "--without-foreground";
            if (withoutForeground) Console.WriteLine("本次明确排除需要真实前台的热键与鼠标穿透断言；不能代替完整原生交互验证。");
            SettingsAudioTests.Run(); PlaybackPlanTests.Run(); MusicPreparationTests.Run();
            if (!withoutForeground) HotkeyBindingTests.Run();
            RegisteredHotkeyControllerTests.Run(); RuntimeControlTests.Run(); QqMusicCatalogTests.Run(); MusicAudioSourcesTests.Run(); SongAudioDownloadTests.Run();
            Console.WriteLine("模拟后端自动演奏测试通过，共 " + AutoPlayerTests.Run(!withoutForeground) + " 项断言。");
            OnlineCatalogTests.Run(); ChineseCatalogTests.Run(); RhythmCanvasTests.Run(); WaterfallReadabilityTests.Run();
            OnlineDialogTests.Run(); LandscapeLayoutTests.Run(); WindowPresentationTests.Run(); PlaybackWindowTests.Run(); AutoScoreWorkflowTests.Run();
            SongFileLibraryTests.Run(); LibraryConversionTests.Run(); SongLibraryStoreTests.Run(); MaintenanceWorkflowTests.Run(); PackagingTests.Run(); AudioRuntimeInstallerTests.Run();
            Console.WriteLine(withoutForeground ? "不依赖真实前台的检查通过；前台交互仍待验证，未向游戏发送输入。" : "全部检查通过；没有向游戏发送键鼠输入。"); return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (error.Data.Contains("InstallerDiagnostic")) Console.Error.WriteLine(error.Data["InstallerDiagnostic"]);
            if (error.Data.Contains("InstallerExitCode")) Console.Error.WriteLine("后台程序退出码：" + error.Data["InstallerExitCode"]);
            return 1;
        }
    }
}
