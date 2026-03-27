namespace MonitorProfileSwitcher;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
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
                Console.WriteLine($"  {p.Name}{hotkey} — {p.Monitors.Length} monitor(s)");
            }
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

        // GUI tray mode (default)
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
                HotkeyBinding? hotkey = null;
                if (args.Length >= 3)
                    hotkey = ParseHotkeyArg(args[2]);
                var profile = pm.CaptureProfile(name, hotkey);
                Console.WriteLine($"Captured '{name}' — {profile.Monitors.Length} monitor(s)");
                foreach (var m in profile.Monitors)
                {
                    var primary = m.IsPrimary ? " [PRIMARY]" : "";
                    Console.WriteLine($"  {m.FriendlyName} {m.Resolution.Width}x{m.Resolution.Height} @ {m.RefreshRate}{primary}");
                }
                break;

            case "apply":
                var target = pm.GetProfile(name);
                if (target == null)
                {
                    Console.Error.WriteLine($"Profile '{name}' not found");
                    Environment.Exit(1);
                }
                try
                {
                    DisplayManager.ApplyProfile(target);
                    Console.WriteLine($"Applied '{name}'");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed: {ex.Message}");
                    Environment.Exit(1);
                }
                break;

            case "delete":
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
