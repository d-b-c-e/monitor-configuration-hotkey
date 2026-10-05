using System.Diagnostics;

namespace MonitorProfileSwitcher;

internal enum ProfileApplyOutcome
{
    AlreadyActive,
    Applied,
    VerifiedAfterApiError,
}

internal sealed class ProfileApplyBusyException : InvalidOperationException
{
    public ProfileApplyBusyException() : base("A display profile switch is already in progress. Wait for it to finish before trying another.") { }
}

/// <summary>One verified profile transition, shared by the tray and direct CLI.</summary>
internal static class ProfileApplyRunner
{
    public static ProfileApplyOutcome Apply(DisplayProfile profile, IReadOnlyList<DisplayProfile> profiles,
        string operationId, string trigger)
    {
        // Direct CLI processes must not race the tray application's hotkeys or IPC calls.
        using var mutex = new Mutex(false, @"Local\MonitorProfileSwitcher_ProfileApply");
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException)
        {
            acquired = true;
            AppLog.Warn($"[{operationId}] Recovered an abandoned profile-apply mutex.");
        }
        if (!acquired)
        {
            AppLog.Warn($"[{operationId}] Apply rejected because another process is switching profiles.");
            throw new ProfileApplyBusyException();
        }

        try { return ApplyCore(profile, profiles, operationId, trigger); }
        finally { mutex.ReleaseMutex(); }
    }

    private static ProfileApplyOutcome ApplyCore(DisplayProfile profile, IReadOnlyList<DisplayProfile> profiles,
        string operationId, string trigger)
    {
        var started = Stopwatch.StartNew();
        AppLog.Info($"[{operationId}] Apply started. Profile='{profile.Name}' Trigger='{trigger}' Type='{profile.DisplaySummary}' Before:{Environment.NewLine}{SafeDescribeCurrentConfig()}");

        try
        {
            // Never ask the driver to rebuild a topology that is already correct. Besides
            // being faster, this avoids a second mode change after an earlier call returned
            // an error even though Windows actually completed the transition.
            IpcDisplayInspector? before = null;
            try
            {
                before = IpcDisplayInspector.Capture();
                if ((profile.NvidiaSurround != null || !profile.IsNvidiaSurround) && before.Matches(profile))
                {
                    AppLog.Info($"[{operationId}] Apply skipped: '{profile.Name}' already matches the live layout.");
                    return ProfileApplyOutcome.AlreadyActive;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[{operationId}] Pre-apply verification unavailable; proceeding with the switch: {ex.Message}");
            }

            if (before != null)
                WindowLayoutManager.BeforeSwitch(before, profile, profiles, operationId);

            try
            {
                DisplayManager.ApplyProfile(profile, before?.ActiveSurround);
            }
            catch (Exception apiError)
            {
                // A failed Surround teardown cannot be considered successful merely
                // because the destination's unrelated monitor happens to be active.
                // The hidden physical outputs must return first.
                if (apiError is SurroundTransitionException)
                    throw;
                // Display drivers can return an error after changing the topology. Do not
                // retry blindly: observe the real state first, and only report success when
                // the entire saved profile (including refresh rate) is verified.
                if (WaitForMatch(profile, TimeSpan.FromSeconds(3)))
                {
                    AppLog.Warn($"[{operationId}] Display API returned '{apiError.Message}', but '{profile.Name}' is now fully verified. No retry was issued.");
                    WindowLayoutManager.AfterSwitch(profile, operationId);
                    AppLog.Info($"[{operationId}] Apply verified after API error in {started.ElapsedMilliseconds}ms. After:{Environment.NewLine}{SafeDescribeCurrentConfig()}");
                    return ProfileApplyOutcome.VerifiedAfterApiError;
                }
                throw;
            }

            if (!WaitForMatch(profile, TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException(
                    "The driver finished the switch, but the live display layout does not match the saved profile. See Open Logs for details.");

            WindowLayoutManager.AfterSwitch(profile, operationId);
            AppLog.Info($"[{operationId}] Apply completed in {started.ElapsedMilliseconds}ms. Profile='{profile.Name}' After:{Environment.NewLine}{SafeDescribeCurrentConfig()}");
            return ProfileApplyOutcome.Applied;
        }
        catch (Exception ex)
        {
            AppLog.Error($"[{operationId}] Apply failed after {started.ElapsedMilliseconds}ms. Profile='{profile.Name}' Current state:{Environment.NewLine}{SafeDescribeCurrentConfig()}", ex);
            throw;
        }
    }

    private static bool WaitForMatch(DisplayProfile profile, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        var consecutiveMatches = 0;
        do
        {
            try
            {
                if (IpcDisplayInspector.Capture().Matches(profile))
                {
                    if (++consecutiveMatches >= 2)
                        return true;
                }
                else
                {
                    consecutiveMatches = 0;
                }
            }
            catch (Exception ex)
            {
                consecutiveMatches = 0;
                AppLog.Warn($"Live profile verification could not read the display state: {ex.Message}");
            }

            if (elapsed.Elapsed >= timeout)
                return false;
            Thread.Sleep(250);
        } while (true);
    }

    private static string SafeDescribeCurrentConfig()
    {
        try { return DisplayManager.DescribeCurrentConfig(); }
        catch (Exception ex) { return $"Unable to read current display configuration: {ex.Message}"; }
    }
}
