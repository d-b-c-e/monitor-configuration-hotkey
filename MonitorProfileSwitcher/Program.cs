using System.Threading;

namespace MonitorProfileSwitcher;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        AppLog.Initialize(args.Length == 0 ? "tray" : string.Join(' ', args));
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) => AppLog.Error("Unhandled UI-thread exception.", eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            AppLog.Error("Unhandled application exception.", eventArgs.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AppLog.Error("Unobserved task exception.", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        // CLI mode: mps capture "Profile Name" or mps apply "Profile Name"
        if (args.Length >= 2)
        {
            RunCli(args);
            return;
        }

        if (args.Length == 1 && args[0] == "--list")
        {
            var pm = new ProfileManager();
            pm.Load();
            foreach (var p in pm.Profiles)
            {
                var hotkey = p.Hotkey != null ? $" ({p.Hotkey})" : "";
                Console.WriteLine($"  {p.Name}{hotkey} — {p.DisplaySummary}");
            }
            return;
        }

        if (args.Length == 1 && args[0] == "--all-paths")
        {
            DebugDump.DumpAllPaths();
            return;
        }

        if (args.Length == 1 && args[0] == "--debug")
        {
            DebugDump.DumpConfig();
            return;
        }

        if (args.Length == 1 && args[0] == "--status")
        {
            Console.WriteLine(DisplayManager.DescribeCurrentConfig());
            return;
        }

        if (args.Length == 1 && args[0] == "--validate-surround")
        {
            try
            {
                Console.WriteLine(NvidiaSurroundManager.ValidateActive());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed: {ex.Message}");
                Environment.Exit(1);
            }
            return;
        }

        // GUI tray mode (default) — enforce a single tray instance so two copies don't
        // fight over global hotkeys or diverge on in-memory profile state.
        using var singleInstance = new Mutex(
            initiallyOwned: true, @"Local\MonitorProfileSwitcher_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            AppLog.Info("Tray startup ended because another instance is already running.");
            return; // another tray instance is already running
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApplication());
    }

    private static void RunCli(string[] args)
    {
        var command = args[0].ToLower();
        var name = args[1];

        var pm = new ProfileManager();
        pm.Load();

        switch (command)
        {
            case "capture":
                AppLog.Info($"CLI capture requested for profile '{name}'.");
                HotkeyBinding? hotkey = null;
                if (args.Length >= 3)
                    hotkey = ParseHotkeyArg(args[2]);
                var profile = pm.CaptureProfile(name, hotkey);
                Console.WriteLine($"Captured '{name}' — {profile.DisplaySummary}");
                foreach (var m in profile.Monitors)
                {
                    var primary = m.IsPrimary ? " [PRIMARY]" : "";
                    Console.WriteLine($"  {m.FriendlyName} {m.Resolution.Width}x{m.Resolution.Height} @ {m.RefreshRate}{primary}");
                }
                break;

            case "apply":
                var operationId = Guid.NewGuid().ToString("N")[..8];
                AppLog.Info($"[{operationId}] CLI apply requested for profile '{name}'. Before:{Environment.NewLine}{SafeDescribeCurrentConfig()}");
                var target = pm.GetProfile(name);
                if (target == null)
                {
                    Console.Error.WriteLine($"Profile '{name}' not found");
                    Environment.Exit(1);
                }
                try
                {
                    bool legacySurround = target.IsNvidiaSurround && target.NvidiaSurround == null;
                    DisplayManager.ApplyProfile(target);
                    if (legacySurround && target.NvidiaSurround != null)
                        pm.Save();
                    AppLog.Info($"[{operationId}] CLI apply completed for profile '{name}'. After:{Environment.NewLine}{SafeDescribeCurrentConfig()}");
                    Console.WriteLine($"Applied '{name}'");
                }
                catch (Exception ex)
                {
                    AppLog.Error($"[{operationId}] CLI apply failed for profile '{name}'. Current state:{Environment.NewLine}{SafeDescribeCurrentConfig()}", ex);
                    Console.Error.WriteLine($"Failed: {ex.Message}");
                    Environment.Exit(1);
                }
                break;

            case "delete":
                AppLog.Info($"CLI delete requested for profile '{name}'.");
                pm.DeleteProfile(name);
                Console.WriteLine($"Deleted '{name}'");
                break;

            default:
                Console.Error.WriteLine($"Unknown command: {command}");
                Console.Error.WriteLine("Usage:");
                Console.Error.WriteLine("  MonitorProfileSwitcher                      (tray mode)");
                Console.Error.WriteLine("  MonitorProfileSwitcher capture \"Name\"        (capture current config)");
                Console.Error.WriteLine("  MonitorProfileSwitcher apply \"Name\"          (apply a profile)");
                Console.Error.WriteLine("  MonitorProfileSwitcher delete \"Name\"         (delete a profile)");
                Console.Error.WriteLine("  MonitorProfileSwitcher --list                (list profiles)");
                Console.Error.WriteLine("  MonitorProfileSwitcher --status              (show current config)");
                Console.Error.WriteLine("  MonitorProfileSwitcher --validate-surround   (validate active NVIDIA Surround grid)");
                Environment.Exit(1);
                break;
        }
    }

    private static string SafeDescribeCurrentConfig()
    {
        try { return DisplayManager.DescribeCurrentConfig(); }
        catch (Exception ex) { return $"Unable to read current display configuration: {ex.Message}"; }
    }

    private static HotkeyBinding? ParseHotkeyArg(string arg)
    {
        // Format: "Ctrl+Alt+1"
        var parts = arg.Split('+');
        var hotkey = new HotkeyBinding();
        foreach (var part in parts)
        {
            switch (part.Trim().ToLower())
            {
                case "ctrl": hotkey.Ctrl = true; break;
                case "alt": hotkey.Alt = true; break;
                case "shift": hotkey.Shift = true; break;
                case "win": hotkey.Win = true; break;
                default: hotkey.Key = part.Trim(); break;
            }
        }
        return string.IsNullOrEmpty(hotkey.Key) ? null : hotkey;
    }
}
