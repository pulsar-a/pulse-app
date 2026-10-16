using System.Diagnostics;
using Pulse.Controls;
using Pulse.Services;
using Pulse.Services.Processes;
using Pulse.ViewModels;
using Xunit.Abstractions;

namespace Pulse.Tests;

public class ProcessInsightsTests(ITestOutputHelper output) : PulseTest
{
    private const long Second = 10_000_000;   // FILETIME ticks
    private const long T0 = 133_000_000_000_000_000;

    private static ProcessSample P(int pid, long cpu, long created = T0 - 100 * Second, string name = "app.exe", int session = 1, long memory = 0) =>
        new(pid, created, name, session, cpu, memory);

    // ---- CPU deltas ---------------------------------------------------------------------------

    [Fact]
    public void The_first_sample_is_only_a_baseline()
    {
        var tracker = new ProcessUsageTracker();

        var usage = tracker.Update([P(10, 50 * Second)], T0, 4);

        Assert.False(usage[0].Cpu > 0);
        Assert.True(tracker.HasBaseline);
    }

    [Fact]
    public void Usage_is_the_share_of_all_cores_over_the_interval()
    {
        var tracker = new ProcessUsageTracker();
        tracker.Update([P(10, 0), P(11, 0)], T0, 4);

        // Over 2 s on 4 cores: one core's worth for the whole interval is 25%; half a core-second is 6.25%.
        var usage = tracker.Update([P(10, 2 * Second), P(11, Second / 2)], T0 + 2 * Second, 4);

        Assert.Equal(25, usage.Single(u => u.Pid == 10).Cpu, 3);
        Assert.Equal(6.25, usage.Single(u => u.Pid == 11).Cpu, 3);
    }

    [Fact]
    public void A_process_that_started_since_counts_all_its_time()
    {
        var tracker = new ProcessUsageTracker();
        tracker.Update([P(10, 0)], T0, 2);

        var usage = tracker.Update([P(10, 0), P(20, Second, created: T0 + Second / 2)], T0 + 2 * Second, 2);

        Assert.Equal(25, usage.Single(u => u.Pid == 20).Cpu, 3);
    }

    [Fact]
    public void A_reused_pid_does_not_inherit_the_old_process_time()
    {
        var tracker = new ProcessUsageTracker();
        tracker.Update([P(10, 900 * Second)], T0, 1);

        // PID 10 exited and Windows gave the number to a new process that has used 0.2 s so far.
        var usage = tracker.Update([P(10, Second / 5, created: T0 + Second)], T0 + 2 * Second, 1);

        Assert.Equal(10, usage.Single().Cpu, 3);
    }

    [Fact]
    public void Exited_processes_drop_out_and_odd_readings_stay_in_range()
    {
        var tracker = new ProcessUsageTracker();
        tracker.Update([P(10, 0), P(11, 10 * Second)], T0, 1);

        var usage = tracker.Update([P(10, 5 * Second), P(11, 9 * Second)], T0 + Second, 1);

        Assert.Equal(100, usage.Single(u => u.Pid == 10).Cpu);   // more than the wall clock: capped
        Assert.Equal(0, usage.Single(u => u.Pid == 11).Cpu);     // went backwards: ignored

        usage = tracker.Update([P(11, 9 * Second)], T0 + 2 * Second, 1);
        Assert.Single(usage);
    }

    [Fact]
    public void After_a_reset_the_next_sample_is_a_baseline_again()
    {
        var tracker = new ProcessUsageTracker();
        tracker.Update([P(10, 0)], T0, 1);
        tracker.Reset();

        var usage = tracker.Update([P(10, 60 * Second)], T0 + 120 * Second, 1);

        Assert.Equal(0, usage.Single().Cpu);
    }

    [Fact]
    public void Gpu_use_is_attached_by_pid_and_missing_counters_stay_null()
    {
        var tracker = new ProcessUsageTracker();
        var withGpu = tracker.Update([P(10, 0), P(11, 0)], T0, 1, new Dictionary<int, double> { [10] = 42 });
        var without = tracker.Update([P(10, 0)], T0 + Second, 1);

        Assert.Equal(42, withGpu.Single(u => u.Pid == 10).Gpu);
        Assert.Equal(0, withGpu.Single(u => u.Pid == 11).Gpu);
        Assert.Null(without.Single().Gpu);
    }

    // ---- GPU engine counters --------------------------------------------------------------------

    [Fact]
    public void Gpu_use_is_each_process_busiest_engine()
    {
        var result = GpuEngineCounters.Aggregate(
        [
            ("pid_100_luid_0x00000000_0x0000D1F6_phys_0_eng_0_engtype_3D", 78),
            ("pid_100_luid_0x00000000_0x0000D1F6_phys_0_eng_3_engtype_Copy", 12),
            ("pid_100_luid_0x00000000_0x0000D1F6_phys_0_eng_0_engtype_3D", 4),   // same engine twice: adds up
            ("pid_200_luid_0x00000000_0x0000D1F6_phys_0_eng_5_engtype_VideoDecode", 30),
            ("pid_300_luid_0x00000000_0x0000D1F6_phys_0_eng_0_engtype_3D", 0),     // idle: left out
            ("pid_400_luid_0x00000000_0x0000AAAA_phys_0_eng_0_engtype_3D", 60),    // second GPU
            ("pid_400_luid_0x00000000_0x0000D1F6_phys_0_eng_0_engtype_3D", 60),
            ("_Total", 99),
            ("garbage", 50),
        ]);

        Assert.Equal(82, result[100]);
        Assert.Equal(30, result[200]);
        Assert.Equal(60, result[400]);
        Assert.False(result.ContainsKey(300));
        Assert.Equal(3, result.Count);
    }

    // ---- Grouping by program ------------------------------------------------------------------

    private static ProcessUsage U(int pid, string name, double cpu, double? gpu = null, long memory = 0, int session = 1) =>
        new(pid, T0, name, session, cpu, gpu, memory);

    [Fact]
    public void Processes_of_one_program_add_up_into_one_row()
    {
        var apps = ProcessApps.Group(
        [
            U(10, "chrome.exe", 3, 1, 100), U(11, "Chrome.exe", 4, 2, 200), U(12, "chrome.exe", 1, 0, 50),
            U(20, "game.exe", 20, 85, 4000),
        ], session: 1, self: 999);

        var chrome = apps.Single(a => a.Key == "chrome.exe");
        Assert.Equal(3, chrome.Count);
        Assert.Equal(8, chrome.Cpu, 3);
        Assert.Equal(3, chrome.Gpu);
        Assert.Equal(350, chrome.Memory);
        Assert.Equal(2, apps.Count);
    }

    [Fact]
    public void Only_the_users_own_programs_can_be_ended()
    {
        Assert.True(ProcessApps.CanEnd(U(10, "game.exe", 0), session: 1, self: 999));
        Assert.True(ProcessApps.CanEnd(U(10, "explorer.exe", 0), session: 1, self: 999));

        Assert.False(ProcessApps.CanEnd(U(10, "game.exe", 0, session: 0), session: 1, self: 999));   // a service
        Assert.False(ProcessApps.CanEnd(U(10, "game.exe", 0, session: 2), session: 1, self: 999));   // someone else's
        Assert.False(ProcessApps.CanEnd(U(10, "dwm.exe", 0), session: 1, self: 999));                // Windows itself
        Assert.False(ProcessApps.CanEnd(U(10, "CSRSS.EXE", 0), session: 1, self: 999));
        Assert.False(ProcessApps.CanEnd(U(999, "Pulse.exe", 0), session: 1, self: 999));             // Pulse

        // One protected member makes the whole group protected.
        var apps = ProcessApps.Group([U(10, "svc.exe", 0), U(11, "svc.exe", 0, session: 0)], session: 1, self: 999);
        Assert.False(apps.Single().CanEnd);
    }

    // ---- The dashboard tile ---------------------------------------------------------------------

    private static ProcessList List(bool hasGpu = true, bool baseline = false, params ProcessUsage[] processes) =>
        new(processes, ProcessApps.Group(processes, session: 1, self: 999), hasGpu, baseline, DateTime.Now);

    private static ProcessUsage[] Busy() =>
    [
        U(10, "game.exe", 22, 88, 6_000_000_000), U(11, "chrome.exe", 9, 4, 900_000_000), U(12, "chrome.exe", 3, 1, 600_000_000),
        U(20, "compiler.exe", 40, 0, 300_000_000), U(30, "idle1.exe", 0), U(31, "idle2.exe", 0), U(32, "idle3.exe", 0),
        U(33, "idle4.exe", 0.1),
    ];

    private static AppInfo? Info(string key) => key == "game.exe" ? new AppInfo("Space Game", @"C:\Games\game.exe", null) : null;

    [Fact]
    public void The_tile_lists_the_top_five_by_cpu_gpu_and_memory()
    {
        var card = new ProcessesCardViewModel();

        card.Apply(List(processes: Busy()), Info);

        Assert.True(card.HasData);
        Assert.Equal([card.Cpu, card.Gpu, card.Memory], card.Lists);
        Assert.Equal(["compiler.exe", "Space Game", "chrome.exe", "idle4.exe", "idle1.exe"], card.Cpu.Items.Select(i => i.Name));
        Assert.Equal("40%", card.Cpu.Items[0].ValueText);
        Assert.Equal(1, card.Cpu.Items[0].Fraction);              // the busiest fills its bar
        Assert.Equal("12%", card.Cpu.Items[2].ValueText);         // chrome's two processes added up
        Assert.Equal("chrome.exe · 2 processes", card.Cpu.Items[2].ToolTip);
        Assert.Equal("Space Game", card.Gpu.Items[0].Name);
        Assert.Equal("88%", card.Gpu.Items[0].ValueText);
        Assert.Equal("5.6 GB", card.Memory.Items[0].ValueText);
        Assert.Equal("8 processes running", card.Subtitle);
    }

    [Fact]
    public void Without_gpu_counters_the_tile_shows_cpu_and_memory()
    {
        var card = new ProcessesCardViewModel();

        card.Apply(List(hasGpu: false, processes: Busy()), Info);

        Assert.Equal([card.Cpu, card.Memory], card.Lists);
    }

    [Fact]
    public void The_tile_waits_for_real_figures_and_keeps_them_through_a_restart()
    {
        var card = new ProcessesCardViewModel();

        card.Apply(List(baseline: true, processes: Busy()), Info);
        Assert.False(card.HasData);
        Assert.Equal("Measuring…", card.Subtitle);

        card.Apply(List(processes: Busy()), Info);
        // Sampling paused and resumed: the baseline's zeros don't replace what's on screen.
        card.Apply(List(baseline: true, processes: [U(10, "game.exe", 0)]), Info);

        Assert.True(card.HasData);
        Assert.Equal("compiler.exe", card.Cpu.Items[0].Name);
    }

    [Fact]
    public void A_hot_cpu_turns_the_tile_into_whats_heating_your_cpu()
    {
        var card = new ProcessesCardViewModel();
        card.Apply(List(processes: Busy()), Info);

        card.SetHeat(HeatLevel.Hot, "96°", HeatLevel.None, "60°");
        Assert.Equal("What's heating your CPU", card.Title);
        Assert.Equal("Processor at 96°", card.Subtitle);
        Assert.Equal(HeatLevel.Hot, card.Heat);
        Assert.Equal(HeatLevel.Hot, card.Cpu.Heat);
        Assert.Equal(HeatLevel.None, card.Gpu.Heat);

        card.SetHeat(HeatLevel.None, "70°", HeatLevel.Warm, "82°");
        Assert.Equal("What's heating your GPU", card.Title);
        Assert.Equal(HeatLevel.Warm, card.Gpu.Heat);

        card.SetHeat(HeatLevel.None, "60°", HeatLevel.None, "60°");
        Assert.Equal("Top processes", card.Title);
        Assert.Equal(HeatLevel.None, card.Heat);
    }

    [Fact]
    public void A_hot_gpu_without_gpu_figures_does_not_claim_to_explain_it()
    {
        var card = new ProcessesCardViewModel();
        card.Apply(List(hasGpu: false, processes: Busy()), Info);

        card.SetHeat(HeatLevel.None, "60°", HeatLevel.Hot, "95°");

        Assert.Equal("Top processes", card.Title);
        Assert.Equal(HeatLevel.None, card.Heat);
    }

    [Fact]
    public void The_dashboard_has_the_tile_and_says_when_it_is_hidden()
    {
        var dashboard = new DashboardViewModel(true, NewCatalog(out var settings), new SensorStream());
        var tile = dashboard.Tiles.Single(t => t.Kind == TileKind.Processes);

        Assert.Same(dashboard.ProcessesCard, tile.Card);
        Assert.True(dashboard.ShowsProcesses);

        dashboard.HideTileCommand.Execute(tile);
        Assert.False(dashboard.ShowsProcesses);
        Assert.Contains(settings.Current.Dashboard.Tiles!, t => t.Kind == "Processes" && t.Hidden);
    }

    // ---- The Processes page -------------------------------------------------------------------

    private ProcessesViewModel Page(List<AppUsage>? ended = null, int refuse = 0, SettingsService? settings = null) =>
        new(settings ?? NewSettings(), Info, app => { ended?.Add(app); return refuse; }) { IsActive = true };

    // ---- Sections, like Task Manager ------------------------------------------------------------

    private static AppUsage App(string name, params ProcessUsage[] members) =>
        ProcessApps.Group(members.Select(m => m with { Name = name }).ToList(), session: 1, self: 999).Single();

    [Fact]
    public void Programs_with_a_window_are_apps_and_the_windows_folder_is_windows()
    {
        var apps = ProcessApps.Classify(
        [
            App("chrome.exe", U(10, "", 5), U(11, "", 1)),          // one of its processes has the window
            App("notepad.exe", U(20, "", 0)),                        // from the Windows folder, but open: an app
            App("updater.exe", U(30, "", 0)),
            App("RuntimeBroker.exe", U(40, "", 0)),
            App("MsMpEng.exe", U(50, "", 0, session: 0)),            // protected service, no path
            App("System", U(4, "", 0, session: 0)),
            App("dropbox.exe", U(60, "", 0)),                        // no path, but in the user's session
        ],
            hasWindow: pid => pid is 11 or 20,
            pathOf: key => key switch
            {
                "notepad.exe" => @"C:\Windows\System32\notepad.exe",
                "updater.exe" => @"C:\Program Files\Thing\updater.exe",
                "runtimebroker.exe" => @"c:\windows\system32\RuntimeBroker.exe",
                _ => null,
            },
            windowsFolder: @"C:\Windows");

        var kinds = apps.ToDictionary(a => a.ImageName, a => a.Kind);
        Assert.Equal(ProcessKind.App, kinds["chrome.exe"]);
        Assert.Equal(ProcessKind.App, kinds["notepad.exe"]);
        Assert.Equal(ProcessKind.Background, kinds["updater.exe"]);
        Assert.Equal(ProcessKind.Windows, kinds["RuntimeBroker.exe"]);
        Assert.Equal(ProcessKind.Windows, kinds["MsMpEng.exe"]);
        Assert.Equal(ProcessKind.Windows, kinds["System"]);
        Assert.Equal(ProcessKind.Background, kinds["dropbox.exe"]);
    }

    private static ProcessList Sorted() =>
        Classified(
        [
            U(10, "game.exe", 22, 88, 6_000_000_000), U(11, "chrome.exe", 9, 4, 900_000_000), U(12, "chrome.exe", 3, 1, 600_000_000),
            U(20, "updater.exe", 2), U(30, "dwm.exe", 1, 3),
        ],
        app: [10, 11], windows: ["dwm.exe"]);

    /// <param name="app">PIDs with a window.</param>
    /// <param name="windows">Programs that run from the Windows folder.</param>
    private static ProcessList Classified(ProcessUsage[] processes, int[] app, string[] windows)
    {
        var list = List(processes: processes);
        return list with
        {
            Apps = ProcessApps.Classify(list.Apps, app.Contains,
                key => windows.Contains(key) ? @"C:\Windows\System32\" + key : null, @"C:\Windows"),
        };
    }

    [Fact]
    public void The_page_groups_programs_into_apps_background_and_windows()
    {
        var page = Page();
        page.Apply(Sorted());

        Assert.Equal([ProcessKind.App, ProcessKind.Background, ProcessKind.Windows], page.Sections.Select(s => s.Kind));
        Assert.Equal(["Apps", "Space Game", "chrome.exe", "Background processes", "updater.exe", "Windows processes", "dwm.exe"],
            page.Items.Select(i => i is ProcessSectionViewModel s ? s.Title : ((ProcessRowViewModel)i).Name));
        var apps = page.Sections[0];
        Assert.Equal(2, apps.Count);
        Assert.Equal("34%", apps.CpuText);   // game 22 + chrome 12
    }

    [Fact]
    public void Grouping_by_type_can_be_turned_off_and_is_remembered()
    {
        var settings = NewSettings();
        var page = Page(settings: settings);
        page.Apply(Sorted());

        page.GroupByType = false;

        Assert.Empty(page.Sections);
        Assert.Equal(4, page.Items.Count);
        Assert.Equal("Space Game", page.Rows[0].Name);
        Assert.False(settings.Current.Processes.GroupByType);
        Assert.False(Page(settings: settings).GroupByType);
    }

    [Fact]
    public void A_folded_section_keeps_its_heading_and_stays_folded()
    {
        var settings = NewSettings();
        var page = Page(settings: settings);
        page.Apply(Sorted());

        page.Sections[0].ToggleCommand.Execute(null);

        Assert.False(page.Sections[0].IsExpanded);
        Assert.Equal(["updater.exe", "dwm.exe"], page.Rows.Select(r => r.Key));
        Assert.Equal([ProcessKind.App], settings.Current.Processes.Collapsed);

        // Opening the page again: still folded.
        var again = Page(settings: settings);
        again.Apply(Sorted());
        Assert.False(again.Sections[0].IsExpanded);

        // A search looks inside it anyway.
        page.Query = "chrome";
        Assert.Equal(["chrome.exe"], page.Rows.Select(r => r.Key));

        page.Query = "";
        page.Sections[0].ToggleCommand.Execute(null);
        Assert.Empty(settings.Current.Processes.Collapsed);
        Assert.Equal(4, page.Rows.Count);
    }

    [Fact]
    public void With_combining_off_every_process_has_its_own_row_in_its_programs_section()
    {
        var settings = NewSettings();
        var ended = new List<AppUsage>();
        var page = Page(ended, settings: settings);
        page.Apply(Sorted());
        page.Confirm = _ => Task.FromResult(true);

        page.CombinePrograms = false;

        var chrome = page.Rows.Where(r => r.App!.ImageName == "chrome.exe").ToList();
        Assert.Equal(2, chrome.Count);
        Assert.Equal("chrome.exe · PID 11", chrome[0].Detail);
        Assert.Equal("9.0%", chrome[0].CpuText);
        // Chrome's helper without a window still sits under Apps, with its program.
        Assert.All(chrome, r => Assert.Equal(ProcessKind.App, r.App!.Kind));
        Assert.Equal(3, page.Sections[0].Count);
        Assert.False(settings.Current.Processes.CombinePrograms);

        // End task then ends just that one process.
        page.EndTaskCommand.Execute(chrome[1]);
        Assert.Equal(12, ended.Single().Members.Single().Pid);
    }

    [Fact]
    public void Settings_start_grouped_and_combined()
    {
        var settings = NewSettings().Current.Processes;

        Assert.True(settings.GroupByType);
        Assert.True(settings.CombinePrograms);
        Assert.Empty(settings.Collapsed);
    }

    [Fact]
    public void Real_desktop_windows_are_found_without_error()
    {
        var sw = Stopwatch.StartNew();
        var pids = DesktopWindows.Pids();
        output.WriteLine($"{pids.Count} processes with a desktop window, found in {sw.Elapsed.TotalMilliseconds:0.0} ms");
        Assert.DoesNotContain(0, pids);
    }

    [Fact]
    public void The_page_lists_programs_busiest_first_and_sorts_by_any_column()
    {
        var page = Page();
        page.Apply(List(processes: Busy()));

        Assert.Equal("8 processes · 7 programs", page.Summary);
        Assert.Equal(["compiler.exe", "Space Game", "chrome.exe"], page.Rows.Take(3).Select(r => r.Name));
        Assert.Equal(" ▾", page.CpuArrow);

        page.SortByCommand.Execute(ProcessSort.Memory);
        Assert.Equal("Space Game", page.Rows[0].Name);

        page.SortByCommand.Execute(ProcessSort.Name);
        Assert.Equal("chrome.exe", page.Rows[0].Name);
        Assert.Equal(" ▴", page.NameArrow);
        Assert.Equal("", page.CpuArrow);

        page.SortByCommand.Execute(ProcessSort.Name);
        Assert.Equal("Space Game", page.Rows[0].Name);
    }

    [Fact]
    public void Rows_are_kept_and_moved_as_readings_change()
    {
        var page = Page();
        page.Apply(List(processes: Busy()));
        var game = page.Rows.Single(r => r.Key == "game.exe");

        page.Apply(List(processes: [U(10, "game.exe", 70, 90), U(20, "compiler.exe", 5)]));

        Assert.Same(game, page.Rows[0]);
        Assert.Equal("70%", game.CpuText);
        Assert.Equal(2, page.Rows.Count);
    }

    [Fact]
    public void Search_matches_the_friendly_name_or_the_file_name()
    {
        var page = Page();
        page.Apply(List(processes: Busy()));

        page.Query = "space";
        Assert.Equal(["Space Game"], page.Rows.Select(r => r.Name));

        page.Query = "GAME.EXE";
        Assert.Single(page.Rows);

        page.Query = "nothing like it";
        Assert.Empty(page.Rows);
        Assert.True(page.HasNoMatches);
    }

    [Fact]
    public void The_page_only_updates_while_it_is_open()
    {
        var page = Page();
        page.IsActive = false;

        page.Apply(List(processes: Busy()));
        Assert.Empty(page.Rows);

        page.IsActive = true;
        Assert.Equal(7, page.Rows.Count);
    }

    [Fact]
    public async Task End_task_asks_first_and_never_touches_system_programs()
    {
        var ended = new List<AppUsage>();
        var page = Page(ended);
        page.Apply(List(processes: [.. Busy(), U(40, "dwm.exe", 2), U(41, "service.exe", 1, session: 0)]));
        var answer = false;
        var asked = 0;
        page.Confirm = _ => { asked++; return Task.FromResult(answer); };

        // Declined: nothing happens.
        await page.EndTaskCommand.ExecuteAsync(page.Rows.Single(r => r.Key == "chrome.exe"));
        Assert.Empty(ended);

        // Windows' own programs and services can't be ended at all, and aren't even asked about.
        Assert.False(page.Rows.Single(r => r.Key == "dwm.exe").CanEnd);
        Assert.False(page.Rows.Single(r => r.Key == "service.exe").CanEnd);
        await page.EndTaskCommand.ExecuteAsync(page.Rows.Single(r => r.Key == "dwm.exe"));
        Assert.Equal(1, asked);

        answer = true;
        await page.EndTaskCommand.ExecuteAsync(page.Rows.Single(r => r.Key == "chrome.exe"));
        Assert.Equal(2, ended.Single().Count);   // both of chrome's processes
        Assert.Equal("Ended chrome.exe.", page.Message);
    }

    [Fact]
    public async Task End_task_says_when_windows_refuses()
    {
        var page = Page(refuse: 1);
        page.Apply(List(processes: Busy()));
        page.Confirm = _ => Task.FromResult(true);

        await page.EndTaskCommand.ExecuteAsync(page.Rows.Single(r => r.Key == "chrome.exe"));

        Assert.Equal("Ended chrome.exe, but Windows wouldn't end 1 of its 2 processes.", page.Message);
    }

    [Fact]
    public void Without_a_confirmation_nothing_is_ever_ended()
    {
        var ended = new List<AppUsage>();
        var page = Page(ended);
        page.Apply(List(processes: Busy()));

        page.EndTaskCommand.Execute(page.Rows[0]);

        Assert.Empty(ended);
    }

    [Theory]
    [InlineData(0.0, "0%")]
    [InlineData(0.04, "0%")]
    [InlineData(0.4, "0.4%")]
    [InlineData(9.94, "9.9%")]
    [InlineData(37.4, "37%")]
    public void Percentages_show_a_decimal_only_where_it_matters(double value, string text) =>
        Assert.Equal(text, ProcessFormat.Percent(value));

    [Fact]
    public void Columns_fit_the_tile_width()
    {
        Assert.Equal(3, ColumnsPanel.Fit(3, 1200, 240, 28));
        Assert.Equal(2, ColumnsPanel.Fit(3, 560, 240, 28));
        Assert.Equal(1, ColumnsPanel.Fit(3, 300, 240, 28));
        Assert.Equal(1, ColumnsPanel.Fit(3, 100, 240, 28));
        Assert.Equal(2, ColumnsPanel.Fit(2, 1200, 240, 28));
    }

    // ---- The real thing -----------------------------------------------------------------------

    [Fact]
    public void Real_processes_are_read_in_one_fast_call()
    {
        NativeProcesses.Snapshot();   // warm up (buffer, JIT)

        var sw = Stopwatch.StartNew();
        const int rounds = 20;
        List<ProcessSample> samples = [];
        for (var i = 0; i < rounds; i++)
            samples = NativeProcesses.Snapshot();
        var native = sw.Elapsed.TotalMilliseconds / rounds;

        // The alternative: the Process class, which opens each process to ask for its times.
        sw.Restart();
        var readable = 0;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                _ = process.TotalProcessorTime;
                _ = process.WorkingSet64;
                readable++;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        var managed = sw.Elapsed.TotalMilliseconds;
        output.WriteLine($"NtQuerySystemInformation: {samples.Count} processes in {native:0.00} ms; " +
                         $"Process class: {readable} readable in {managed:0.00} ms");

        var me = samples.Single(s => s.Pid == Environment.ProcessId);
        using var self = Process.GetCurrentProcess();
        Assert.Equal(self.ProcessName + ".exe", me.Name, ignoreCase: true);
        Assert.Equal(self.StartTime.ToUniversalTime().ToFileTimeUtc(), me.CreateTime, Second / 10.0);
        Assert.True(me.CpuTime > 0 && me.Memory > 0);
        Assert.Contains(samples, s => s.Pid == 4 && s.Name == "System");
        Assert.DoesNotContain(samples, s => s.Pid == 0);
        Assert.True(native < 50, $"sampling took {native:0.0} ms");
    }

    [Fact]
    public void Real_gpu_counters_read_or_report_unavailable()
    {
        using var counters = GpuEngineCounters.TryOpen();
        if (counters is null)
        {
            output.WriteLine("No GPU Engine counters on this machine");
            return;
        }

        Thread.Sleep(300);
        var sw = Stopwatch.StartNew();
        var gpu = counters.Read();
        output.WriteLine($"GPU counters: {gpu?.Count} busy processes in {sw.Elapsed.TotalMilliseconds:0.0} ms");

        Assert.NotNull(gpu);
        Assert.All(gpu!.Values, v => Assert.InRange(v, 0, 100));
    }

    [Fact]
    public void Ending_a_process_ends_it_and_leaves_a_pid_reuse_alone()
    {
        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        var sample = NativeProcesses.Snapshot().Single(s => s.Pid == child.Id);

        // A wrong start time means "not the process we sampled": nothing happens to it.
        Assert.True(NativeProcesses.Terminate(child.Id, sample.CreateTime + 1));
        Assert.False(child.WaitForExit(200));

        Assert.True(NativeProcesses.Terminate(child.Id, sample.CreateTime));
        Assert.True(child.WaitForExit(5000));
        // Gone already: still a success.
        Assert.True(NativeProcesses.Terminate(child.Id, sample.CreateTime));
    }
}
