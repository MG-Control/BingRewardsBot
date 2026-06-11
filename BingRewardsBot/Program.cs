using BingRewardsBot.Models;
using BingRewardsBot.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSignalR();
builder.Services.AddSingleton<BotState>();
builder.Services.AddSingleton<BingRewardsService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapHub<BingRewardsHub>("/bingRewardsHub");
app.MapGet("/api/status", (BotState state) =>
{
    var s = state.GetStatus();
    return Results.Ok(new
    {
        running = s.Running,
        progress = ProgressToAnon(s.Progress),
        last_error = s.LastError,
    });
});

app.MapPost("/api/start", async (HttpContext ctx, BingRewardsService svc, BotState state) =>
{
    if (state.IsRunning)
        return Results.Json(new { ok = false, error = "A job is already running." }, statusCode: 409);

    StartRequest? req;
    try { req = await ctx.Request.ReadFromJsonAsync<StartRequest>(); }
    catch { req = null; }

    if (req is null)
        return Results.Json(new { ok = false, error = "Invalid request body." }, statusCode: 400);

    var (valid, error) = req.Validate();
    if (!valid)
        return Results.Json(new { ok = false, error }, statusCode: 400);

    svc.Start(req);
    return Results.Ok(new { ok = true, message = "Bot started." });
});

app.MapPost("/api/stop", (BingRewardsService svc, BotState state) =>
{
    if (!state.IsRunning)
        return Results.Json(new { ok = false, error = "No job is running." }, statusCode: 400);

    svc.Stop();
    return Results.Ok(new { ok = true, message = "Stop signal sent." });
});

app.Run();

static object ProgressToAnon(ProgressData p) => new
{
    desktop_done = p.DesktopDone,
    desktop_total = p.DesktopTotal,
    mobile_done = p.MobileDone,
    mobile_total = p.MobileTotal,
    phase = p.Phase,
    status = p.Status,
};
