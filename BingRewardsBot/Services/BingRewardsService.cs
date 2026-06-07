using BingRewardsBot.Models;
using Microsoft.AspNetCore.SignalR;

namespace BingRewardsBot.Services;

public class BingRewardsService
{
    private readonly IHubContext<BingRewardsHub> _hub;
    private readonly BotState _state;
    private readonly IConfiguration _config;
    private CancellationTokenSource? _cts;

    public BingRewardsService(IHubContext<BingRewardsHub> hub, BotState state, IConfiguration config)
    {
        _hub = hub;
        _state = state;
        _config = config;
    }

    public void Start(StartRequest req)
    {
        _cts = new CancellationTokenSource();
        _state.Reset(req.DesktopCount, req.MobileCount);

        AppendLog($"Starting bot for {req.Email?.Trim()} (headless={req.Headless})");
        EmitStatus();

        _ = Task.Run(() => RunBotAsync(req, _cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        AppendLog("Stop signal sent.", "warning");
        var p = _state.Progress;
        _state.UpdateProgress(new ProgressData
        {
            DesktopDone = p.DesktopDone,
            DesktopTotal = p.DesktopTotal,
            MobileDone = p.MobileDone,
            MobileTotal = p.MobileTotal,
            Phase = p.Phase,
            Status = "stopping",
        });
        EmitStatus();
    }

    private async Task RunBotAsync(StartRequest req, CancellationToken ct)
    {
        var bot = new BingRewardsBotRunner(
            email: req.Email!.Trim(),
            password: req.Password ?? "",
            desktopCount: req.DesktopCount,
            mobileCount: req.MobileCount,
            headless: req.Headless,
            config: _config,
            logCallback: AppendLog,
            progressCallback: EmitProgress,
            cancellationToken: ct
        );

        try
        {
            await bot.RunAsync();
            if (bot.LastError != null)
                _state.SetLastError(bot.LastError);
        }
        catch (Exception ex)
        {
            AppendLog($"Worker crashed: {ex.Message}", "error");
            _state.SetLastError(ex.Message);
        }
        finally
        {
            _state.SetRunning(false);
            var p = _state.Progress;
            if (p.Status == "running" || p.Status == "stopping")
            {
                _state.UpdateProgress(new ProgressData
                {
                    DesktopDone = p.DesktopDone,
                    DesktopTotal = p.DesktopTotal,
                    MobileDone = p.MobileDone,
                    MobileTotal = p.MobileTotal,
                    Phase = p.Phase,
                    Status = "idle",
                });
            }
            EmitStatus();
        }
    }

    public void AppendLog(string message, string level = "info")
    {
        var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var entry = new LogEntry(level, message, ts);
        _state.AddLog(entry);
        _ = _hub.Clients.All.SendAsync("log", new { level, message, timestamp = ts });
    }

    private void EmitStatus()
    {
        var s = _state.GetStatus();
        _ = _hub.Clients.All.SendAsync("status", new
        {
            running = s.Running,
            progress = new
            {
                desktop_done = s.Progress.DesktopDone,
                desktop_total = s.Progress.DesktopTotal,
                mobile_done = s.Progress.MobileDone,
                mobile_total = s.Progress.MobileTotal,
                phase = s.Progress.Phase,
                status = s.Progress.Status,
            },
            last_error = s.LastError,
        });
    }

    private void EmitProgress(ProgressData data)
    {
        _state.UpdateProgress(data);
        _ = _hub.Clients.All.SendAsync("progress", new
        {
            desktop_done = data.DesktopDone,
            desktop_total = data.DesktopTotal,
            mobile_done = data.MobileDone,
            mobile_total = data.MobileTotal,
            phase = data.Phase,
            status = data.Status,
        });
        EmitStatus();
    }
}
