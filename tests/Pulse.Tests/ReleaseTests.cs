using System.IO;
using Pulse.Services;
using Pulse.Services.Updates;

namespace Pulse.Tests;

public class ReleaseTests : PulseTest
{
    // ---- Elevating itself ---------------------------------------------------------------------

    [Fact]
    public void An_admin_with_a_limited_token_elevates()
    {
        Assert.Equal(Elevation.Plan.Elevate, Elevation.Decide([], isElevated: false, canElevate: true, debuggerAttached: false));
        Assert.Equal(Elevation.Plan.Elevate, Elevation.Decide(["--tray"], false, true, false));
    }

    [Theory]
    [InlineData(true, true, false)]    // already elevated
    [InlineData(false, false, false)]  // a standard user: a prompt would ask for someone else's password
    [InlineData(false, true, true)]    // under the debugger: keep the session
    public void Otherwise_pulse_runs_as_it_was_started(bool elevated, bool canElevate, bool debugger) =>
        Assert.Equal(Elevation.Plan.RunAsIs, Elevation.Decide([], elevated, canElevate, debugger));

    [Theory]
    [InlineData("--no-elevate")]
    [InlineData("--elevated")]           // the elevated copy never tries again
    [InlineData("--veloapp-install")]    // installer and updater hooks run as they are, quickly, with no UI
    [InlineData("--veloapp-uninstall")]
    [InlineData("--dump-snapshot")]
    public void Some_launches_never_elevate(string flag) =>
        Assert.Equal(Elevation.Plan.RunAsIs, Elevation.Decide([flag, "x"], false, true, false));

    // ---- One copy, across elevation -----------------------------------------------------------

    [Fact]
    public void A_second_launch_finds_the_first_and_wakes_it()
    {
        var name = SingleInstance.Name;
        SingleInstance.Name = "PulseTests." + Guid.NewGuid().ToString("N");
        try
        {
            Assert.False(SingleInstance.SignalIfRunning());   // nothing running yet

            using var first = SingleInstance.TryAcquire();
            Assert.NotNull(first);
            using var woken = new ManualResetEventSlim();
            first.ListenForActivation(woken.Set);

            Assert.True(SingleInstance.SignalIfRunning());
            Assert.True(woken.Wait(TimeSpan.FromSeconds(5)));

            woken.Reset();
            Assert.Null(SingleInstance.TryAcquire());          // a second copy steps aside and signals
            Assert.True(woken.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SingleInstance.WaitThenActivate(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            SingleInstance.Name = name;
        }
    }

    // ---- Updates ------------------------------------------------------------------------------

    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Updates_are_checked_once_a_day_when_automatic()
    {
        Assert.True(UpdatePolicy.ShouldCheck(true, null, Now));
        Assert.False(UpdatePolicy.ShouldCheck(true, Now.AddHours(-23), Now));
        Assert.True(UpdatePolicy.ShouldCheck(true, Now.AddHours(-25), Now));
        Assert.True(UpdatePolicy.ShouldCheck(true, Now.AddDays(3), Now));   // a clock that jumped back
        Assert.False(UpdatePolicy.ShouldCheck(false, null, Now));
    }

    [Fact]
    public void An_update_never_restarts_pulse_during_a_critical_alert()
    {
        Assert.False(UpdatePolicy.MayRestartNow(criticalAlert: true));
        Assert.True(UpdatePolicy.MayRestartNow(criticalAlert: false));
    }

    [Fact]
    public void Updates_are_automatic_and_uninstalling_keeps_data_by_default()
    {
        var updates = NewSettings().Current.Updates;

        Assert.True(updates.AutoInstall);
        Assert.True(updates.KeepDataOnUninstall);
        Assert.Null(updates.LastCheck);
    }

    [Fact]
    public void A_copy_not_installed_with_the_installer_never_updates()
    {
        var service = new UpdateService(NewSettings(), () => false);

        Assert.Equal(UpdateState.NotInstalled, service.State);
        Assert.False(service.RestartToUpdate());
    }

    // ---- Uninstalling -------------------------------------------------------------------------

    private sealed class FakeSystem : ICleanupSystem
    {
        public bool Task = true, Rule, Keep = true, TaskNeedsAdmin = true, ElevationAllowed = true, DataLocked;
        public int Elevations;
        public bool ToastRemoved, DataRemoved;

        public bool HasStartupTask() => Task;
        public bool RemoveStartupTask() => !TaskNeedsAdmin && !(Task = false);
        public bool HasFirewallRule() => Rule;
        public bool RemoveFirewallRule() => !(Rule = false);
        public void RemoveToastIdentity() => ToastRemoved = true;
        public bool KeepData() => Keep;

        public void RemoveData()
        {
            if (DataLocked) throw new IOException("in use");
            DataRemoved = true;
        }

        public bool RunElevated(TimeSpan timeout)
        {
            Elevations++;
            if (!ElevationAllowed) return false;
            Task = Rule = false;
            return true;
        }
    }

    [Fact]
    public void Uninstalling_removes_the_task_and_rule_with_one_prompt_and_keeps_data()
    {
        var system = new FakeSystem { Rule = true };

        var left = UninstallCleanup.Run(system);

        Assert.Empty(left);
        Assert.Equal(1, system.Elevations);
        Assert.False(system.Task || system.Rule);
        Assert.True(system.ToastRemoved);
        Assert.False(system.DataRemoved);
    }

    [Fact]
    public void Nothing_that_needs_admin_means_no_prompt()
    {
        var system = new FakeSystem { TaskNeedsAdmin = false };

        UninstallCleanup.Run(system);

        Assert.Equal(0, system.Elevations);
        Assert.False(system.Task);
    }

    [Fact]
    public void A_declined_prompt_reports_what_was_left()
    {
        var system = new FakeSystem { Rule = true, ElevationAllowed = false };

        var left = UninstallCleanup.Run(system);

        Assert.Equal(["startup task \"Pulse\"", "firewall rule \"Pulse web view\""], left);
    }

    [Fact]
    public void Data_goes_only_when_the_user_chose_that()
    {
        var remove = new FakeSystem { Task = false, Keep = false };
        UninstallCleanup.Run(remove);
        Assert.True(remove.DataRemoved);

        var locked = new FakeSystem { Task = false, Keep = false, DataLocked = true };
        Assert.Equal(["data folder"], UninstallCleanup.Run(locked));
    }
}
