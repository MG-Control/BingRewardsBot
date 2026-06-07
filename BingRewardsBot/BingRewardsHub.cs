using BingRewardsBot.Services;
using Microsoft.AspNetCore.SignalR;

public class BingRewardsHub : Hub
{
    private readonly BotState _state;

    public BingRewardsHub(BotState state)
    {
        _state = state;
    }

    public override async Task OnConnectedAsync()
    {
        var s = _state.GetStatus();
        await Clients.Caller.SendAsync("status", new
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

        foreach (var entry in _state.GetLogs())
        {
            await Clients.Caller.SendAsync("log", new
            {
                level = entry.Level,
                message = entry.Message,
                timestamp = entry.Timestamp,
            });
        }

        await base.OnConnectedAsync();
    }
}
