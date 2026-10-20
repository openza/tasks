using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed partial class PersonalCloudDispatchBudgetTests
{
    [Fact]
    public async Task Normal_fresh_provider_day_does_not_become_catchup_from_envelope_count()
    {
        var pending = Enumerable.Range(1, 550).Select(sequence => Entry("source.refreshSnapshot") with
            { DeviceSequence = sequence, MutationId = $"mutation_normal_{sequence}" }).ToArray();
        var original = pending.ToArray();
        var report = await Policy().ReserveCatchupReportAsync(Connection(), pending, false, false, 8800, CancellationToken.None);
        Assert.Equal("idle", report!.State);
        Assert.Equal(550, report.Pending);
        Assert.Equal(original, pending);
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("recovery")]
    [InlineData("legacy")]
    [InlineData("cost")]
    [InlineData("hold")]
    public async Task Captured_backlog_stays_busy_until_entire_current_queue_and_initial_recovery_work_are_done(string trigger)
    {
        var pending = Entry() with { CreatedAt = trigger == "legacy" ? _clock.Utc.AddDays(-1) : _clock.Utc };
        var policy = Policy();
        if (trigger == "hold") await policy.PauseBudgetAsync(Origin, 45001, CancellationToken.None);
        var first = await policy.ReserveCatchupReportAsync(Connection(), [pending], trigger == "seed", trigger == "recovery",
            trigger == "cost" ? 45001 : 50, CancellationToken.None);
        Assert.Equal("catching_up", first!.State);
        var newer = pending with { DeviceSequence = 2, MutationId = "mutation_newer", CreatedAt = _clock.Utc };
        _clock.Utc = _clock.Utc.AddMinutes(5);
        var second = await Policy().ReserveCatchupReportAsync(Connection(), [newer], false, false, 50, CancellationToken.None);
        Assert.Equal("catching_up", second!.State); // Older watermark drained; newer work is still pending.
        var drained = await Policy().ReserveCatchupReportAsync(Connection(), [], false, false, 0, CancellationToken.None);
        Assert.Equal("idle", drained!.State);
        Assert.True(drained.ReportSequence > second.ReportSequence);
    }

    [Fact]
    public async Task Report_sequences_survive_restart_day_reset_and_server_counter_recovery_without_replaying_idle()
    {
        var policy = Policy();
        var first = await policy.ReserveCatchupReportAsync(Connection(), [], false, false, 0, CancellationToken.None);
        Assert.Equal(1, first!.ReportSequence);
        Assert.Null(await policy.ReserveCatchupReportAsync(Connection(), [], false, false, 0, CancellationToken.None));
        await policy.ConfirmCatchupSequenceAsync(Connection(), 9000, CancellationToken.None);
        _clock.Utc = _clock.Utc.AddDays(1);
        var next = await Policy().ReserveCatchupReportAsync(Connection(), [], false, false, 0, CancellationToken.None);
        Assert.Equal(9001, next!.ReportSequence);
        var journal = Assert.Single(Directory.GetFiles(_directory, "*.catchup"));
        File.WriteAllText(journal, "{");
        var conservative = await Policy().ReserveCatchupReportAsync(Connection(), [], false, false, 0, CancellationToken.None);
        Assert.Equal("catching_up", conservative!.State);
        Assert.Equal("{", File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.quarantine"))));
        await policy.ConfirmCatchupSequenceAsync(Connection(), 9001, CancellationToken.None);
        _clock.Utc = _clock.Utc.AddMinutes(5);
        var refreshed = await Policy().ReserveCatchupReportAsync(Connection(), [], false, false, 0, CancellationToken.None);
        Assert.Equal("idle", refreshed!.State);
        Assert.Equal(9002, refreshed.ReportSequence);
    }

    [Fact]
    public async Task Global_gates_suppress_reports_but_local_upload_hold_can_publish_truthful_pending_status()
    {
        var policy = Policy();
        await policy.PauseBudgetAsync(Origin, 45001, CancellationToken.None);
        Assert.Equal("catching_up", (await policy.ReserveCatchupReportAsync(Connection(), [Entry()], false, false, 50, CancellationToken.None))!.State);
        using (var lease = await policy.BeginAsync(Origin, 0, 0, CancellationToken.None))
            lease.Pause("daily_quota_exceeded", "d1_rows_written", _clock.Utc,
                PersonalCloudDispatchPolicy.NextMidnight(_clock.Utc), PersonalCloudDispatchPolicy.NextMidnight(_clock.Utc));
        Assert.Null(await policy.ReserveCatchupReportAsync(Connection(), [Entry()], false, false, 50, CancellationToken.None));
    }
}
