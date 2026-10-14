using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;

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

        if (args.Length >= 2 && args[0].Equals("--ipc", StringComparison.OrdinalIgnoreCase))
        {
            RunIpcCliAsync(args).GetAwaiter().GetResult();
            return;
        }

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

    private static async Task RunIpcCliAsync(string[] args)
    {
        var command = args[1].ToLowerInvariant();
        if (command is not ("ping" or "status" or "list-profiles" or "apply") ||
            (command == "apply" && args.Length != 3) ||
            (command != "apply" && args.Length != 2))
        {
            Console.Error.WriteLine("Usage: MonitorProfileSwitcher --ipc ping|status|list-profiles|apply \"Profile Name\"");
            Environment.ExitCode = 2;
            return;
        }

        IpcResponse response;
        try
        {
            response = await IpcService.SendAsync(new IpcRequest
            {
                Command = command,
                ProfileName = command == "apply" ? args[2] : null,
            });
        }
        catch (Exception ex)
        {
            AppLog.Error($"IPC CLI command '{command}' failed.", ex);
            response = IpcResponse.Error("ipc_error", ex.Message);
        }

        Console.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        }));
        if (!response.Success)
            Environment.ExitCode = 1;
    }

    private static void RunCli(string[] args)
    {
        var command = args[0].ToLower();
        var name = args[1];

        var pm = new ProfileManager();
        pm.Load();

        switch (command)
        {
            case "layout":
                if (name.Equals("save", StringComparison.OrdinalIgnoreCase))
                {
                    if (!WindowLayoutManager.SaveCurrentLayout(pm.Profiles))
                    {
                        Console.Error.WriteLine("No eligible windows or unique active profile; layout was not saved.");
                        Environment.ExitCode = 1;
                    }
                    else
                        Console.WriteLine("Saved the active profile's window layout.");
                }
                else if (name.Equals("restore", StringComparison.OrdinalIgnoreCase))
                {
                    var result = WindowLayoutManager.RestoreCurrentLayout(pm.Profiles);
                    if (result == null)
                    {
                        Console.Error.WriteLine("No saved layout for the active profile.");
                        Environment.ExitCode = 1;
                    }
                    else
                        Console.WriteLine($"Restored {result.Restored} window(s) for '{result.ProfileName}' (missing={result.Missing}, offscreen={result.Offscreen}, failed={result.Failed}).");
                }
                else if (name.Equals("enable", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("disable", StringComparison.OrdinalIgnoreCase))
                {
                    bool enabled = name.Equals("enable", StringComparison.OrdinalIgnoreCase);
                    WindowLayoutManager.SetEnabled(enabled);
                    Console.WriteLine($"Automatic window-layout restore {(enabled ? "enabled" : "disabled")}.");
                }
                else if (name.Equals("status", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"Automatic window-layout restore is {(WindowLayoutManager.IsEnabled() ? "enabled" : "disabled")}.");
                }
                else
                {
                    Console.Error.WriteLine("Usage: MonitorProfileSwitcher layout save|restore|enable|disable|status");
                    Environment.ExitCode = 2;
                }
                break;

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
                var target = pm.GetProfile(name);
                if (target == null)
                {
                    Console.Error.WriteLine($"Profile '{name}' not found");
                    Environment.Exit(1);
                }
                try
                {
                    bool legacySurround = target.IsNvidiaSurround && target.NvidiaSurround == null;
                    var result = ProfileApplyRunner.Apply(target, pm.Profiles, operationId, "direct CLI");
                    if (legacySurround && target.NvidiaSurround != null)
                        pm.SaveMigratedSurroundGrid(target);
                    Console.WriteLine(result.Outcome == ProfileApplyOutcome.AlreadyActive
                        ? $"'{name}' is already active"
                        : $"Applied and verified '{name}'");
                    if (result.SkippedMonitors.Count > 0)
                        Console.WriteLine($"Not connected, skipped: {string.Join(", ", result.SkippedMonitors.Select(m => m.FriendlyName.Length > 0 ? m.FriendlyName : m.EdidId))}");
                }
                catch (Exception ex)
                {
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
                Console.Error.WriteLine("  MonitorProfileSwitcher --ipc status          (query the running tray app over IPC)");
                Console.Error.WriteLine("  MonitorProfileSwitcher --ipc list-profiles   (list profiles over IPC)");
                Console.Error.WriteLine("  MonitorProfileSwitcher --ipc apply \"Name\"   (apply through the running tray app)");
                Console.Error.WriteLine("  MonitorProfileSwitcher layout save|restore|enable|disable|status");
                Environment.Exit(1);
                break;
        }
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
