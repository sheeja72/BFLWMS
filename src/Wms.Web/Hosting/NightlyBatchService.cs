using Wms.Data.Lpm;

namespace Wms.Web.Hosting;

/// <summary>
/// Fires once a day at 06:00 GST (Arabian Standard Time = UTC+04:00).
/// For each ACTIVE country in WmsRptCountryConfig, runs
/// MissingExcessSnapshotService.RefreshDayAsync(country, yesterday)
/// and logs each run into WmsRptJobRun. Lives in-process; relies on App
/// Service Always On to be present at 06:00.
/// </summary>
public class NightlyBatchService(IServiceProvider sp, ILogger<NightlyBatchService> log)
    : BackgroundService
{
    private static readonly TimeSpan FireTimeGst = new(6, 0, 0);
    private static readonly TimeZoneInfo GstTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Arabian Standard Time" : "Asia/Dubai");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("NightlyBatchService started. Fire time: 06:00 GST.");
        DateTime? lastFireGstDate = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                var nowGst = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, GstTz);
                var todayFireGst = nowGst.Date.Add(FireTimeGst);
                var nextFireGst  = nowGst >= todayFireGst ? todayFireGst.AddDays(1) : todayFireGst;

                // If we've crossed today's fire time and haven't run today yet, fire now.
                var shouldFireNow = nowGst >= todayFireGst
                    && (lastFireGstDate is null || lastFireGstDate.Value < nowGst.Date);

                if (shouldFireNow)
                {
                    log.LogInformation("NightlyBatchService: firing daily run at {Now}.", nowGst);
                    await RunDailyAsync(stoppingToken);
                    lastFireGstDate = nowGst.Date;
                    continue;
                }

                // Sleep until next fire (or 1 hour, whichever comes first — defensive wakeups).
                var sleep = TimeZoneInfo.ConvertTimeToUtc(nextFireGst, GstTz) - DateTime.UtcNow;
                if (sleep > TimeSpan.FromHours(1)) sleep = TimeSpan.FromHours(1);
                if (sleep < TimeSpan.FromSeconds(30)) sleep = TimeSpan.FromSeconds(30);
                await Task.Delay(sleep, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "NightlyBatchService: unexpected error in loop; retrying in 5 minutes.");
                try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { break; }
            }
        }
    }

    private async Task RunDailyAsync(CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<MissingExcessSnapshotService>();

        var countries = await svc.GetActiveCountriesAsync(ct);
        if (countries.Count == 0)
        {
            log.LogInformation("NightlyBatchService: no active countries — nothing to do.");
            return;
        }

        var yesterday = DateTime.UtcNow.AddDays(-1).Date;
        foreach (var country in countries)
        {
            if (ct.IsCancellationRequested) return;
            var runId = await svc.StartJobRunAsync("Daily", country, "Timer", ct);
            try
            {
                var rows = await svc.RefreshDayAsync(country, yesterday, ct);
                await svc.FinishJobRunAsync(runId, "Success", rows, 1, null, ct);
                log.LogInformation("NightlyBatchService: {Country} {Date:yyyy-MM-dd} done — {Rows} rows.",
                    country, yesterday, rows);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "NightlyBatchService: {Country} {Date:yyyy-MM-dd} FAILED.", country, yesterday);
                await svc.FinishJobRunAsync(runId, "Failed", null, 0, ex.Message, CancellationToken.None);
            }
        }
    }
}
