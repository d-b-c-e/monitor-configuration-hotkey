using System.Diagnostics;

namespace MonitorProfileSwitcher;

internal enum ProfileApplyOutcome
{
    AlreadyActive,
    Applied,
    VerifiedAfterApiError,
}

/// <summary>What an apply did. <see cref="Applied"/> is the profile as actually applied: the
/// saved one, or a re-laid-out copy without the monitors in <see cref="SkippedMonitors"/>.</summary>
internal sealed record ProfileApplyResult(
    ProfileApplyOutcome Outcome,
    DisplayProfile Applied,
    IReadOnlyList<MonitorInfo> SkippedMonitors);

internal sealed class ProfileApplyBusyException : InvalidOperationException
{
    public ProfileApplyBusyException() : base("A display profile switch is already in progress. Wait for it to finish before trying another.") { }
}

/// <summary>One verified profile transition, shared by the tray, the arrangement editor and direct CLI.</summary>
internal static class ProfileApplyRunner
{
    public static ProfileApplyResult Apply(DisplayProfile profile, IReadOnlyList<DisplayProfile> profiles,
        string operationId, string trigger, bool manageWindowLayouts = true)
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

        try { return ApplyCore(profile, profiles, operationId, trigger, manageWindowLayouts); }
        finally { mutex.ReleaseMutex(); }
    }

    private static ProfileApplyResult ApplyCore(DisplayProfile saved, IReadOnlyList<DisplayProfile> profiles,
        string operationId, string trigger, bool manageWindowLayouts)
    {
        var started = Stopwatch.StartNew();
        AppLog.Info($"[{operationId}] Apply started. Profile='{saved.Name}' Trigger='{trigger}' Type='{saved.DisplaySummary}' Before:{Environment.NewLine}{SafeDescribeCurrentConfig()}");

        try
        {
            // Never ask the driver to rebuild a topology that is already correct. Besides
            // being faster, this avoids a second mode change after an earlier call returned
            // an error even though Windows actually completed the transition. "Correct" allows
            // for unplugged monitors; which ones are missing for the real apply is decided
            // inside ApplyProfile, after any Surround transition.
            IpcDisplayInspector? before = null;
            try
            {
                var preview = WithConnectedMonitors(saved, out var previewSkipped);
                before = IpcDisplayInspector.Capture();
                if ((preview.NvidiaSurround != null || !preview.IsNvidiaSurround) && before.Matches(preview))
                {
                    AppLog.Info($"[{operationId}] Apply skipped: '{saved.Name}' already matches the live layout.");
                    return new ProfileApplyResult(ProfileApplyOutcome.AlreadyActive, preview, previewSkipped);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[{operationId}] Pre-apply verification unavailable; proceeding with the switch: {ex.Message}");
            }

            if (before != null && manageWindowLayouts)
                WindowLayoutManager.BeforeSwitch(before, saved, profiles, operationId);

            DisplayProfile profile;
            IReadOnlyList<MonitorInfo> skipped;
            try
            {
                (profile, skipped) = DisplayManager.ApplyProfile(saved, before?.ActiveSurround);
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
                var expected = WithConnectedMonitors(saved, out var expectedSkipped);
                if (WaitForMatch(expected, TimeSpan.FromSeconds(3), out _))
                {
                    AppLog.Warn($"[{operationId}] Display API returned '{apiError.Message}', but '{saved.Name}' is now fully verified. No retry was issued.");
                    if (manageWindowLayouts)
                        WindowLayoutManager.AfterSwitch(expected, operationId);
                    AppLog.Info($"[{operationId}] Apply verified after API error in {started.ElapsedMilliseconds}ms. After:{Environment.NewLine}{SafeDescribeCurrentConfig()}");
                    return new ProfileApplyResult(ProfileApplyOutcome.VerifiedAfterApiError, expected, expectedSkipped);
                }
                throw;
            }

            // Monitors that were off can take 10s+ to finish coming up (DisplayPort link training,
            // the driver re-creating the monitor devices), during which the layout is briefly
            // something else. Matching returns as soon as it holds, so a long limit costs nothing.
            if (!WaitForMatch(profile, TimeSpan.FromSeconds(15), out var mismatch))
                throw new InvalidOperationException(
                    $"The switch finished, but the layout did not settle as saved: {mismatch}.");

            if (manageWindowLayouts)
                WindowLayoutManager.AfterSwitch(profile, operationId);
            AppLog.Info($"[{operationId}] Apply completed in {started.ElapsedMilliseconds}ms. Profile='{profile.Name}' After:{Environment.NewLine}{SafeDescribeCurrentConfig()}");
            return new ProfileApplyResult(ProfileApplyOutcome.Applied, profile, skipped);
        }
        catch (Exception ex)
        {
            AppLog.Error($"[{operationId}] Apply failed after {started.ElapsedMilliseconds}ms. Profile='{saved.Name}' Current state:{Environment.NewLine}{SafeDescribeCurrentConfig()}", ex);
            throw;
        }
    }

    /// <summary>The profile as it would apply with the monitors connected right now. Only valid
    /// for comparing against the live state: while Surround is on, its panels look unplugged.</summary>
    private static DisplayProfile WithConnectedMonitors(DisplayProfile saved, out IReadOnlyList<MonitorInfo> skipped)
    {
        try
        {
            return DisplayLayout.WithoutMissingMonitors(saved, DisplayManager.GetConnectedMonitorDevicePaths(), out skipped);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not check which monitors are connected; using the profile as saved: {ex.Message}");
            skipped = [];
            return saved;
        }
    }

    private static bool WaitForMatch(DisplayProfile profile, TimeSpan timeout, out string mismatch)
    {
        var elapsed = Stopwatch.StartNew();
        var consecutiveMatches = 0;
        string? lastLogged = null;
        mismatch = "the display state could not be read";
        do
        {
            try
            {
                var difference = IpcDisplayInspector.Capture().Mismatch(profile);
                if (difference == null)
                {
                    if (++consecutiveMatches >= 2)
                    {
                        if (lastLogged != null)
                            AppLog.Info($"Layout settled as saved after {elapsed.ElapsedMilliseconds}ms.");
                        return true;
                    }
                }
                else
                {
                    consecutiveMatches = 0;
                    mismatch = difference;
                    if (difference != lastLogged)
                    {
                        AppLog.Info($"Waiting for the layout to settle ({elapsed.ElapsedMilliseconds}ms): {difference}.");
                        lastLogged = difference;
                    }
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
