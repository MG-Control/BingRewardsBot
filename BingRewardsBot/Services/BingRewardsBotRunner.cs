using BingRewardsBot.Models;
using Microsoft.Playwright;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BingRewardsBot.Services;

public class BingRewardsBotRunner
{
    private static readonly string[] DesktopUserAgents =
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
    };

    private static readonly string[] MobileUserAgents =
    {
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1",
        "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36",
        "Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Mobile Safari/537.36",
    };

    private static readonly string[] Timezones =
        { "America/New_York", "America/Chicago", "America/Los_Angeles", "Europe/London" };

    private const string StealthScript = @"
Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
window.chrome = { runtime: {} };
Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
";

    private const string PasskeyBlockScript = @"
(() => {
  const reject = () => Promise.reject(new DOMException('NotAllowedError', 'NotAllowedError'));
  if (window.PublicKeyCredential) {
    PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable = () => Promise.resolve(false);
    PublicKeyCredential.isConditionalMediationAvailable = () => Promise.resolve(false);
  }
  if (navigator.credentials) {
    navigator.credentials.get = reject;
    navigator.credentials.create = reject;
  }
})();
";

    private const string PermissionBlockScript = @"
(() => {
  // Auto-deny notification permission requests
  if ('Notification' in window) {
    try {
      Object.defineProperty(Notification, 'permission', { get: () => 'denied', configurable: true });
      Notification.requestPermission = () => Promise.resolve('denied');
    } catch(e) {}
  }
  // Auto-deny all Permissions API queries
  if (navigator.permissions && navigator.permissions.query) {
    const _orig = navigator.permissions.query.bind(navigator.permissions);
    navigator.permissions.query = (params) =>
      _orig(params)
        .then(r => { try { Object.defineProperty(r, 'state', { get: () => 'denied' }); } catch(e) {} return r; })
        .catch(() => Promise.resolve({ state: 'denied', onchange: null }));
  }
  // Auto-deny geolocation
  if (navigator.geolocation) {
    navigator.geolocation.getCurrentPosition = (s, e) => { if (e) e({ code: 1, message: 'Permission denied.' }); };
    navigator.geolocation.watchPosition    = (s, e) => { if (e) e({ code: 1, message: 'Permission denied.' }); return 0; };
  }
  // Auto-deny microphone / camera via getUserMedia
  if (navigator.mediaDevices && navigator.mediaDevices.getUserMedia) {
    navigator.mediaDevices.getUserMedia = () => Promise.reject(new DOMException('Permission denied', 'NotAllowedError'));
  }
})();
";

    private static readonly string[] PasskeyTextHints =
    {
        "passkey", "security key", "windows hello", "fingerprint",
        "face, fingerprint, or pin", "use your face", "webauthn", "fido",
    };

    private const string RewardsSigninUrl =
        "https://rewards.bing.com/createuser?idru=%2F&userScenarioId=anonsignin";
    private const string RewardsHomeUrl = "https://rewards.bing.com/";

    private static readonly string[] DefaultSearchTerms =
    {
        "weather forecast today", "healthy dinner recipes", "latest technology news",
        "budget travel destinations", "home workout routines", "electric car reviews",
        "meditation for beginners", "gardening tips spring", "national parks usa",
        "best programming languages", "space exploration news", "cooking tips beginners",
    };

    private readonly string _email;
    private readonly string _password;
    private readonly int _desktopCount;
    private readonly int _mobileCount;
    private readonly bool _headless;
    private readonly IConfiguration _config;
    private readonly Action<string, string> _logCallback;
    private readonly Action<ProgressData> _progressCallback;
    private readonly CancellationToken _ct;
    private readonly Random _rng = new();
    private readonly string[] _searchTerms;

    private int _desktopDone;
    private int _mobileDone;
    private int _dailySetsDone;

    public string? LastError { get; private set; }

    private double MinDelay => _config.GetValue<double>("BotOptions:MinDelay", 2.0);
    private double MaxDelay => _config.GetValue<double>("BotOptions:MaxDelay", 6.0);
    private int ManualLoginTimeout => _config.GetValue<int>("BotOptions:ManualLoginTimeout", 180);
    private bool EnableDailySets => _config.GetValue<bool>("BotOptions:EnableDailySets", true);
    private string ProfilesDir => _config.GetValue<string>("BotOptions:ProfilesDir", "profiles")!;
    private string SearchTermsFile => _config.GetValue<string>("BotOptions:SearchTermsFile", "search_terms.txt")!;

    public BingRewardsBotRunner(
        string email, string password, int desktopCount, int mobileCount, bool headless,
        IConfiguration config,
        Action<string, string> logCallback,
        Action<ProgressData> progressCallback,
        CancellationToken cancellationToken)
    {
        _email = email.Trim();
        _password = password;
        _desktopCount = Math.Max(0, desktopCount);
        _mobileCount = Math.Max(0, mobileCount);
        // Force headless mode on Linux since X server may not be available
        _headless = headless || RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        _config = config;
        _logCallback = logCallback;
        _progressCallback = progressCallback;
        _ct = cancellationToken;
        _searchTerms = LoadSearchTerms(SearchTermsFile);
    }

    public static string GetProfileDir(string email)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var digest = Convert.ToHexString(bytes).ToLowerInvariant()[..16];
        var path = Path.Combine("profiles", digest);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string[] LoadSearchTerms(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var lines = File.ReadAllLines(filePath)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith('#'))
                    .ToArray();
                if (lines.Length > 0) return lines;
            }
        }
        catch { }
        return DefaultSearchTerms;
    }

    private void Log(string message, string level = "info") => _logCallback(message, level);

    private bool Stopped => _ct.IsCancellationRequested;

    private async Task<bool> SleepAsync(double seconds)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), _ct); return false; }
        catch (OperationCanceledException) { return true; }
    }

    private async Task<bool> HumanDelayAsync(double multiplier = 1.0)
    {
        var delay = (_rng.NextDouble() * (MaxDelay - MinDelay) + MinDelay) * multiplier;
        return await SleepAsync(delay);
    }

    private void EmitProgress(string phase, string status = "running")
    {
        _progressCallback(new ProgressData
        {
            DesktopDone = _desktopDone,
            DesktopTotal = _desktopCount,
            MobileDone = _mobileDone,
            MobileTotal = _mobileCount,
            Phase = phase,
            Status = status,
        });
    }

    private string PickSearchTerm() => _searchTerms[_rng.Next(_searchTerms.Length)];

    private string ProfileDir => GetProfileDir(_email);

    public async Task RunAsync()
    {
        if (_desktopCount == 0 && _mobileCount == 0)
        {
            Log("No searches configured.", "warning");
            LastError = "No searches configured";
            EmitProgress("idle", "error");
            return;
        }

        IPlaywright? playwright = null;
        IBrowserContext? context = null;

        try
        {
            Log($"Installing Playwright browsers if needed...");
            try { Microsoft.Playwright.Program.Main(["install", "chromium"]); } catch { }

            Log($"Using profile: {Path.GetFileName(ProfileDir)}");
            playwright = await Playwright.CreateAsync();
            context = await LaunchContextAsync(playwright, mobile: false);
            var page = GetPage(context);

            if (!await EnsureLoggedInAsync(page))
            {
                LastError = "Login failed";
                EmitProgress("login", "error");
                return;
            }

            await VisitRewardsAsync(page);
            await CompleteDailySetsAsync(page);

            if (_desktopCount > 0)
                await RunDesktopSearchesAsync(page);

            if (_mobileCount > 0 && !Stopped)
                await RunMobileSearchesAsync(playwright);

            if (Stopped)
            {
                Log("Bot stopped by user.", "warning");
                EmitProgress("done", "stopped");
            }
            else
            {
                Log($"Completed. Daily set: {_dailySetsDone}, Desktop: {_desktopDone}/{_desktopCount}, Mobile: {_mobileDone}/{_mobileCount}");
                EmitProgress("done", "completed");
            }
        }
        catch (Exception ex)
        {
            Log($"Bot error: {ex.Message}", "error");
            LastError = ex.Message;
            EmitProgress("error", "error");
        }
        finally
        {
            if (context != null) try { await context.CloseAsync(); } catch { }
            if (playwright != null) try { playwright.Dispose(); } catch { }
        }
    }

    private async Task<IBrowserContext> LaunchContextAsync(IPlaywright playwright, bool mobile)
    {
        var ua = _rng.Next(mobile ? MobileUserAgents.Length : DesktopUserAgents.Length);
        var userAgent = mobile ? MobileUserAgents[ua] : DesktopUserAgents[ua];
        var timezone = Timezones[_rng.Next(Timezones.Length)];

        ViewportSize viewport;
        if (mobile)
        {
            var widths = new[] { 390, 393, 412, 414 };
            var heights = new[] { 844, 852, 896, 915 };
            viewport = new ViewportSize { Width = widths[_rng.Next(widths.Length)], Height = heights[_rng.Next(heights.Length)] };
        }
        else
        {
            var widths = new[] { 1280, 1366, 1440, 1536, 1920 };
            var heights = new[] { 720, 768, 800, 900, 1080 };
            viewport = new ViewportSize { Width = widths[_rng.Next(widths.Length)], Height = heights[_rng.Next(heights.Length)] };
        }

        var args = new[]
        {
            "--disable-blink-features=AutomationControlled",
            "--disable-dev-shm-usage",
            "--no-sandbox",
            "--disable-notifications",
            "--disable-geolocation",
        };

        IBrowserContext context;
        if (mobile)
        {
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _headless,
                SlowMo = _rng.Next(50, 150),
                Args = args,
                IgnoreDefaultArgs = new[] { "--enable-automation" },
            });
            context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = userAgent,
                ViewportSize = viewport,
                Locale = "en-US",
                TimezoneId = timezone,
                IsMobile = true,
                HasTouch = true,
            });
        }
        else
        {
            context = await playwright.Chromium.LaunchPersistentContextAsync(
                ProfileDir,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = _headless,
                    UserAgent = userAgent,
                    ViewportSize = viewport,
                    Locale = "en-US",
                    TimezoneId = timezone,
                    SlowMo = _rng.Next(50, 150),
                    Args = args,
                    IgnoreDefaultArgs = new[] { "--enable-automation" },
                }
            );
        }

        await context.AddInitScriptAsync(StealthScript);
        await context.AddInitScriptAsync(PasskeyBlockScript);
        await context.AddInitScriptAsync(PermissionBlockScript);
        return context;
    }

    private static IPage GetPage(IBrowserContext context)
    {
        return context.Pages.Count > 0 ? context.Pages[0] : context.NewPageAsync().GetAwaiter().GetResult();
    }

    private async Task<bool> EnsureLoggedInAsync(IPage page)
    {
        Log("Checking Bing Rewards account session...");
        if (await IsLoggedInOnRewardsAsync(page))
        {
            Log("Reusing saved Rewards session for this profile.");
            return true;
        }
        if (string.IsNullOrEmpty(_password))
        {
            Log("Password required for first Rewards login (no saved session).", "error");
            return false;
        }
        return await LoginAsync(page);
    }

    private async Task<bool> IsLoggedInOnRewardsAsync(IPage page)
    {
        try
        {
            await page.GotoAsync(RewardsSigninUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            await HumanDelayAsync(1.0);
            return await RewardsPageShowsAccountAsync(page);
        }
        catch (Exception ex)
        {
            Log($"Rewards session check failed: {ex.Message}", "warning");
            return false;
        }
    }

    private async Task<bool> RewardsPageShowsAccountAsync(IPage page)
    {
        var url = page.Url.ToLower();
        if (url.Contains("login.live.com") || url.Contains("createuser") || url.Contains("anonsignin"))
            return false;

        try
        {
            var content = await page.ContentAsync();
            content = content.ToLower();
            if (content.Contains("sign out") || content.Contains("signout"))
                return true;

            var signOutLinks = page.Locator("a[href*='SignOut'], a[href*='signout'], a[href*='logout']");
            if (await signOutLinks.CountAsync() > 0)
                return true;
        }
        catch { }

        return false;
    }

    private async Task<bool> LoginAsync(IPage page)
    {
        Log("Opening Bing Rewards sign-in (points will link to this account)...");
        EmitProgress("login");
        try
        {
            await page.GotoAsync(RewardsSigninUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            await HumanDelayAsync(1.2);
            await ClickRewardsSignInAsync(page);

            if (!page.Url.ToLower().Contains("login.live.com"))
            {
                await ClickRewardsSignInAsync(page);
                await SleepAsync(0.8);
            }

            bool credentialsOk;
            try { credentialsOk = await FillMicrosoftCredentialsAsync(page); }
            catch (Exception ex)
            {
                Log($"Auto login error: {ex.Message}. Switching to manual sign-in...", "warning");
                credentialsOk = await WaitForManualLoginAsync(page);
            }

            if (!credentialsOk)
            {
                if (!await WaitForManualLoginAsync(page))
                    return false;
            }

            if (await NeedsManualVerificationAsync(page))
            {
                Log("2FA or extra verification required — complete it in the browser window.", "warning");
                if (!await WaitForManualLoginAsync(page))
                    return false;
            }

            await page.GotoAsync(RewardsHomeUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            await SleepAsync(1.0);
            if (await IsLoggedInOnRewardsAsync(page))
            {
                Log("Rewards login successful. Points will count for this account.");
                return true;
            }

            Log("Verifying login — you can still complete sign-in in the browser...", "warning");
            if (await WaitForManualLoginAsync(page))
                return await IsLoggedInOnRewardsAsync(page);

            Log("Login failed. Use headless OFF and complete passkey/password manually.", "error");
            return false;
        }
        catch (Exception ex)
        {
            Log($"Login error: {ex.Message}.", "error");
            if (!_headless && await WaitForManualLoginAsync(page))
                return await IsLoggedInOnRewardsAsync(page);
            return false;
        }
    }

    private async Task ClickRewardsSignInAsync(IPage page)
    {
        if (page.Url.ToLower().Contains("login.live.com")) return;

        var selectors = new[]
        {
            "a[href*='login.live.com']", "a[href*='signin']", "a[href*='SignIn']",
            "button:has-text('Sign in')", "a:has-text('Sign in')",
            "button:has-text('Sign In')", "a:has-text('Sign In')", "#id_s",
        };

        foreach (var sel in selectors)
        {
            try
            {
                var loc = page.Locator(sel).First;
                if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync())
                {
                    await loc.ClickAsync(new LocatorClickOptions { Timeout = 10000 });
                    await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 30000 });
                    await SleepAsync(0.8);
                    return;
                }
            }
            catch { }
        }
    }

    private async Task<bool> FillMicrosoftCredentialsAsync(IPage page)
    {
        const string emailSel = "input[type='email'], input[name='loginfmt']";
        await page.WaitForSelectorAsync(emailSel, new PageWaitForSelectorOptions { Timeout = 45000 });
        await TypeHumanAsync(page, emailSel, _email);

        const string nextBtn = "input[type='submit'], button[type='submit'], #idSIButton9";
        await page.ClickAsync(nextBtn, new PageClickOptions { Timeout = 15000 });
        await SleepAsync(1.0);

        if (!await NavigateToPasswordEntryAsync(page))
        {
            Log("Auto passkey bypass did not reach password field.", "warning");
            return await WaitForManualLoginAsync(page);
        }

        if (string.IsNullOrEmpty(_password))
        {
            Log("No password provided — finish sign-in manually.", "warning");
            return await WaitForManualLoginAsync(page);
        }

        const string pwdSel = "input[type='password'], input[name='passwd']";
        await TypeHumanAsync(page, pwdSel, _password);
        await page.ClickAsync(nextBtn, new PageClickOptions { Timeout = 15000 });
        await SleepAsync(1.5);

        const string staySignedSel = "#idSIButton9, input[type='submit'][value='Yes']";
        if (await page.Locator(staySignedSel).CountAsync() > 0)
        {
            try { await page.ClickAsync(staySignedSel, new PageClickOptions { Timeout = 5000 }); await SleepAsync(0.8); }
            catch { }
        }
        return true;
    }

    private async Task<bool> NavigateToPasswordEntryAsync(IPage page)
    {
        Log("Closing passkey popup and switching to password sign-in...");
        await SleepAsync(1.5);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (Stopped) return false;
            if (await PasswordFieldVisibleAsync(page))
            {
                Log("Password field is ready.");
                return true;
            }
            if (await DismissPasskeyPopupAsync(page))
                Log($"Passkey handled automatically (step {attempt + 1}).");
            else if (await SwitchToPasswordSignInAsync(page))
                Log($"Chose other sign-in / password (step {attempt + 1}).");
            await SleepAsync(1.2);
        }
        return await PasswordFieldVisibleAsync(page);
    }

    private async Task<bool> PasswordFieldVisibleAsync(IPage page)
    {
        try
        {
            var loc = page.Locator("input[type='password'], input[name='passwd']").First;
            return await loc.CountAsync() > 0 && await loc.IsVisibleAsync();
        }
        catch { return false; }
    }

    private static bool TextLooksLikePasskeyPrompt(string text)
    {
        var lower = text.ToLower();
        return PasskeyTextHints.Any(h => lower.Contains(h));
    }

    private async Task<bool> DismissPasskeyPopupAsync(IPage page)
    {
        if (await SwitchToPasswordSignInAsync(page))
        {
            Log("Chose password instead of passkey.");
            return true;
        }

        var dialogRoots = new[]
        {
            "div[role='dialog']", "div[role='alertdialog']", "[data-testid='modal']",
            "#modalDiv", ".modal-dialog", "div.lightbox",
        };
        var closeBtns = new[]
        {
            "button[aria-label='Close']", "button[aria-label='close']", "button[title='Close']",
            "button:has-text('Cancel')", "button:has-text('Not now')", "button:has-text('Skip')",
            "a:has-text('Cancel')", "button.close", "[data-testid='dismissIcon']",
            "i[data-icon-name='Cancel']", "#idBtn_Back", "button#idBtn_Back",
        };

        foreach (var frame in page.Frames)
        {
            foreach (var rootSel in dialogRoots)
            {
                try
                {
                    var dialogs = frame.Locator(rootSel);
                    var count = await dialogs.CountAsync();
                    for (int i = 0; i < count; i++)
                    {
                        var dlg = dialogs.Nth(i);
                        if (!await dlg.IsVisibleAsync()) continue;

                        string dlgText;
                        try { dlgText = await dlg.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 2000 }); }
                        catch { dlgText = ""; }

                        if (!string.IsNullOrEmpty(dlgText) && !TextLooksLikePasskeyPrompt(dlgText)
                            && !dlgText.ToLower().Contains("sign in"))
                            continue;

                        foreach (var btnSel in closeBtns)
                        {
                            try
                            {
                                var btn = dlg.Locator(btnSel).First;
                                if (await btn.CountAsync() > 0 && await btn.IsVisibleAsync())
                                {
                                    await btn.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                                    Log("Closed passkey popup automatically.");
                                    await HumanDelayAsync(0.8);
                                    return true;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        try
        {
            var clicked = await page.EvaluateAsync<bool>(@"() => {
                const labels = [
                  'use your password', 'password', 'sign in another way',
                  'other ways to sign in', 'cancel', 'not now',
                ];
                const nodes = document.querySelectorAll('button, a, input[type=submit], span[role=button]');
                for (const node of nodes) {
                  const t = (node.innerText || node.value || '').trim().toLowerCase();
                  if (!t) continue;
                  if (labels.some(l => t.includes(l))) { node.click(); return true; }
                }
                return false;
            }");
            if (clicked)
            {
                Log("Clicked passkey dismiss / password option via page script.");
                await HumanDelayAsync(0.8);
                return true;
            }
        }
        catch { }

        return false;
    }

    private async Task<bool> SwitchToPasswordSignInAsync(IPage page)
    {
        var selectors = new[]
        {
            "#idA_PWD_SwitchToPassword", "a#idA_PWD_SwitchToPassword",
            "button:has-text('Use your password')", "a:has-text('Use your password')",
            "button:has-text('Use your password instead')", "a:has-text('Use your password instead')",
            "button:has-text('Sign in another way')", "a:has-text('Sign in another way')",
            "button:has-text('Other ways to sign in')", "a:has-text('Other ways to sign in')",
            "button:has-text('Use a password')", "a:has-text('Use a password')",
            "button:has-text('Password')", "a:has-text('Password')",
        };

        foreach (var frame in page.Frames)
        {
            foreach (var sel in selectors)
            {
                try
                {
                    var loc = frame.Locator(sel).First;
                    if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync())
                    {
                        await loc.ClickAsync(new LocatorClickOptions { Timeout = 8000 });
                        await SleepAsync(0.8);
                        return true;
                    }
                }
                catch { }
            }
        }

        foreach (var label in new[] { "Use your password", "Use your password instead", "Sign in another way", "Other ways to sign in", "Use a password", "Password" })
        {
            foreach (var role in new[] { AriaRole.Link, AriaRole.Button })
            {
                try
                {
                    var loc = page.GetByRole(role, new PageGetByRoleOptions { Name = label });
                    if (await loc.CountAsync() > 0 && await loc.First.IsVisibleAsync())
                    {
                        await loc.First.ClickAsync(new LocatorClickOptions { Timeout = 8000 });
                        await SleepAsync(0.8);
                        return true;
                    }
                }
                catch { }
            }
        }

        return false;
    }

    private async Task<bool> WaitForManualLoginAsync(IPage page)
    {
        if (_headless)
        {
            Log("Headless mode cannot complete passkey/password manually. Turn off headless.", "error");
            return false;
        }

        var timeout = ManualLoginTimeout;
        Log($"Complete sign-in in the browser if needed. Waiting up to {timeout}s...", "warning");

        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        int polls = 0;
        int autoClicks = 0;

        while (DateTime.UtcNow < deadline)
        {
            if (Stopped) return false;

            if (await PasswordFieldVisibleAsync(page) && !string.IsNullOrEmpty(_password))
            {
                try
                {
                    const string pwdSel = "input[type='password'], input[name='passwd']";
                    await TypeHumanAsync(page, pwdSel, _password);
                    await page.ClickAsync("input[type='submit'], button[type='submit'], #idSIButton9",
                        new PageClickOptions { Timeout = 15000 });
                    await SleepAsync(1.5);
                }
                catch { }
            }

            if (await IsLoggedInCurrentPageAsync(page))
            {
                Log("Manual sign-in detected on current page.");
                return true;
            }

            polls++;
            if (polls % 8 == 0)
            {
                if (await IsLoggedInOnRewardsAsync(page)) return true;
            }
            else if ((DateTime.UtcNow - deadline.AddSeconds(-timeout)).TotalSeconds < 25 && autoClicks < 4)
            {
                if (await DismissPasskeyPopupAsync(page) || await SwitchToPasswordSignInAsync(page))
                    autoClicks++;
            }

            await SleepAsync(2.5);
        }

        Log("Manual login timeout.", "error");
        return false;
    }

    private async Task<bool> IsLoggedInCurrentPageAsync(IPage page)
    {
        var url = page.Url.ToLower();
        if (url.Contains("rewards.bing.com") && !url.Contains("createuser") && !url.Contains("anonsignin"))
            return await RewardsPageShowsAccountAsync(page);
        if (url.Contains("login.live.com") || url.Contains("login.microsoftonline.com"))
            return false;
        try
        {
            var content = (await page.ContentAsync()).ToLower();
            if (content.Contains("sign out") || content.Contains("signout"))
                return true;
        }
        catch { }
        return false;
    }

    private async Task<bool> NeedsManualVerificationAsync(IPage page)
    {
        var url = page.Url.ToLower();
        if (new[] { "proofs", "confirm", "challenge" }.Any(x => url.Contains(x)))
            return true;
        try
        {
            var content = (await page.ContentAsync()).ToLower();
            return new[] { "identity/confirm", "proofs/add", "two-step", "verify your identity",
                           "enter code", "captcha", "hcaptcha", "security info" }
                   .Any(x => content.Contains(x));
        }
        catch { return false; }
    }

    private async Task VisitRewardsAsync(IPage page)
    {
        Log("Opening Bing Rewards dashboard...");
        EmitProgress("rewards");
        try
        {
            await page.GotoAsync(RewardsHomeUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            await SleepAsync(1.0);
            await RandomScrollAsync(page);
        }
        catch (Exception ex) { Log($"Could not open Rewards page: {ex.Message}", "warning"); }
    }

    private async Task CompleteDailySetsAsync(IPage page)
    {
        if (!EnableDailySets) return;

        Log("Checking Daily Set...");
        EmitProgress("daily_sets");
        int completed = 0;

        try
        {
            await page.GotoAsync(RewardsHomeUrl + "dashboard", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            await SleepAsync(1.5);
            await EnsureLoggedInAsync(page);
            await RandomScrollAsync(page);

            if (await page.Locator("#dailyset").CountAsync() == 0)
            {
                Log("Daily set section not found (may not be available today).");
                return;
            }

            for (int pass = 0; pass < 12; pass++)
            {
                if (Stopped) break;

                var pendingCount = await DailySetsPendingCountAsync(page);
                if (pendingCount == 0)
                {
                    Log(completed > 0
                        ? $"Daily set finished: {completed} card(s) completed."
                        : "Daily set: all cards already completed or none pending.");
                    break;
                }

                Log($"Daily set: {pendingCount} pending card(s)...");
                var pendingCard = page.Locator("#dailyset .react-aria-DisclosurePanel a[href]")
                    .Filter(new LocatorFilterOptions { Has = page.Locator(".bg-statusInformativeTintBg") })
                    .First;

                string cardTitle = "task";
                try
                {
                    var titleEl = pendingCard.Locator("p.text-globalBody2Strong");
                    if (await titleEl.CountAsync() > 0)
                        cardTitle = (await titleEl.First.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 3000 })).Trim()[..Math.Min(60, (await titleEl.First.InnerTextAsync()).Trim().Length)];
                }
                catch { }

                Log($"Daily set: clicking '{cardTitle.Trim()}'...");
                var activityPage = await ClickDailySetCardAsync(page, pendingCard);
                bool openedNewTab = activityPage != page;

                try { await CompleteActivityPageAsync(activityPage); }
                catch (Exception ex) { Log($"Daily set activity error: {ex.Message}", "warning"); }

                if (openedNewTab)
                {
                    try { await activityPage.CloseAsync(); } catch { }
                }
                await page.GotoAsync(RewardsHomeUrl + "dashboard", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                await SleepAsync(2.0);
                await EnsureLoggedInAsync(page);

                if (await DailySetsPendingCountAsync(page) < pendingCount)
                {
                    completed++;
                    _dailySetsDone = completed;
                    Log($"Daily set card completed ({completed}).");
                }
                else
                {
                    Log("Daily set card may need more time; refreshing...", "warning");
                    await SleepAsync(2.0);
                    await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                    if (await DailySetsPendingCountAsync(page) < pendingCount)
                    {
                        completed++;
                        _dailySetsDone = completed;
                    }
                }
            }
        }
        catch (Exception ex) { Log($"Daily set error: {ex.Message}", "warning"); }
    }

    private async Task<int> DailySetsPendingCountAsync(IPage page)
    {
        var section = page.Locator("#dailyset");
        if (await section.CountAsync() == 0) return 0;
        return await section.Locator(".react-aria-DisclosurePanel a[href]")
            .Filter(new LocatorFilterOptions { Has = page.Locator(".bg-statusInformativeTintBg") })
            .CountAsync();
    }

    private async Task<IPage> ClickDailySetCardAsync(IPage page, ILocator cardLink)
    {
        try { await cardLink.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = 10000 }); } catch { }
        await SleepAsync(0.5);

        var popupWaiter = page.WaitForPopupAsync(new PageWaitForPopupOptions { Timeout = 8000 });
        try
        {
            await cardLink.ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        }
        catch
        {
            return page;
        }

        try
        {
            var popup = await popupWaiter;
            await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 60000 });
            return popup;
        }
        catch
        {
            try { await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded); } catch { }
            return page;
        }
    }

    private async Task CompleteActivityPageAsync(IPage activityPage)
    {
        await SleepAsync(1.0);
        const string searchBox = "textarea[name='q'], input[name='q'], #sb_form_q";
        var term = ExtractBingQuery(activityPage.Url);

        try
        {
            if (await activityPage.Locator(searchBox).CountAsync() > 0)
            {
                await activityPage.WaitForSelectorAsync(searchBox, new PageWaitForSelectorOptions { Timeout = 15000 });
                var existing = await activityPage.Locator(searchBox).First.InputValueAsync();
                existing = existing.Trim();
                if (!string.IsNullOrEmpty(existing)) term = existing;
                if (string.IsNullOrEmpty(term)) term = PickSearchTerm();
                if (string.IsNullOrEmpty(existing))
                    await TypeHumanAsync(activityPage, searchBox, term);
                else
                    Log($"Daily set search term: {term}");
                await activityPage.Keyboard.PressAsync("Enter");
                await activityPage.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 30000 });
                await SleepAsync(1.0);
                await RandomScrollAsync(activityPage);
                return;
            }
        }
        catch (Exception ex) { Log($"Daily set Bing search: {ex.Message}", "warning"); }

        await RandomScrollAsync(activityPage);
        await SleepAsync(_rng.NextDouble() * 2 + 2);

        foreach (var sel in new[] { "button:has-text('Start')", "a:has-text('Start')", "button:has-text('Continue')" })
        {
            try
            {
                var btn = activityPage.Locator(sel);
                if (await btn.CountAsync() > 0 && await btn.First.IsVisibleAsync())
                {
                    await btn.First.ClickAsync(new LocatorClickOptions { Timeout = 8000 });
                    await SleepAsync(1.5);
                    break;
                }
            }
            catch { }
        }
    }

    private static string? ExtractBingQuery(string url)
    {
        try
        {
            var uri = new Uri(url);
            foreach (var part in uri.Query.TrimStart('?').Split('&'))
            {
                var eq = part.IndexOf('=');
                if (eq > 0 && part[..eq] == "q")
                    return Uri.UnescapeDataString(part[(eq + 1)..]).Trim();
            }
        }
        catch { }
        return null;
    }

    private async Task RunDesktopSearchesAsync(IPage page)
    {
        EmitProgress("desktop");
        for (int i = 0; i < _desktopCount; i++)
        {
            if (Stopped) { Log("Stop requested during desktop searches.", "warning"); break; }
            var term = PickSearchTerm();
            if (await PerformSearchAsync(page, term, mobile: false))
            {
                _desktopDone++;
                EmitProgress("desktop");
            }
            await HumanDelayAsync();
        }
    }

    private async Task RunMobileSearchesAsync(IPlaywright playwright)
    {
        EmitProgress("mobile");
        IBrowser? mobileBrowser = null;
        IBrowserContext? mobileContext = null;
        IPage? mobilePage = null;

        try
        {
            mobileBrowser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _headless,
                SlowMo = _rng.Next(50, 150),
                Args = new[] { "--disable-blink-features=AutomationControlled", "--no-sandbox" },
                IgnoreDefaultArgs = new[] { "--enable-automation" },
            });

            var mobileWidths = new[] { 390, 393, 412, 414 };
            var mobileHeights = new[] { 844, 852, 896, 915 };

            mobileContext = await mobileBrowser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = MobileUserAgents[_rng.Next(MobileUserAgents.Length)],
                ViewportSize = new ViewportSize
                {
                    Width = mobileWidths[_rng.Next(mobileWidths.Length)],
                    Height = mobileHeights[_rng.Next(mobileHeights.Length)],
                },
                Locale = "en-US",
                TimezoneId = Timezones[_rng.Next(Timezones.Length)],
                IsMobile = true,
                HasTouch = true,
            });

            await mobileContext.AddInitScriptAsync(StealthScript);
            await mobileContext.AddInitScriptAsync(PasskeyBlockScript);
            mobilePage = await mobileContext.NewPageAsync();

            for (int i = 0; i < _mobileCount; i++)
            {
                if (Stopped) { Log("Stop requested during mobile searches.", "warning"); break; }
                var term = PickSearchTerm();
                if (await PerformSearchAsync(mobilePage, term, mobile: true))
                {
                    _mobileDone++;
                    EmitProgress("mobile");
                }
                await HumanDelayAsync();
            }
        }
        catch (Exception ex) { Log($"Mobile search session error: {ex.Message}", "error"); }
        finally
        {
            if (mobilePage != null) try { await mobilePage.CloseAsync(); } catch { }
            if (mobileContext != null) try { await mobileContext.CloseAsync(); } catch { }
            if (mobileBrowser != null) try { await mobileBrowser.CloseAsync(); } catch { }
        }
    }

    private async Task<bool> PerformSearchAsync(IPage page, string term, bool mobile)
    {
        if (Stopped) return false;
        var label = mobile ? "mobile" : "desktop";
        Log($"[{label}] Searching: {term}");
        try
        {
            await page.GotoAsync("https://www.bing.com/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            await SleepAsync(0.6);
            await RandomMouseMoveAsync(page);

            const string searchBox = "textarea[name='q'], input[name='q'], #sb_form_q";
            await page.WaitForSelectorAsync(searchBox, new PageWaitForSelectorOptions { Timeout = 20000 });
            await TypeHumanAsync(page, searchBox, term);
            await SleepAsync(_rng.NextDouble() * 0.6 + 0.4);
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 30000 });
            await SleepAsync(0.8);
            await RandomScrollAsync(page);

            if (_rng.NextDouble() < 0.35)
            {
                var results = page.Locator("li.b_algo h2 a, #b_results h2 a");
                var count = await results.CountAsync();
                if (count > 0)
                {
                    var idx = _rng.Next(0, Math.Min(count, 5));
                    try
                    {
                        await results.Nth(idx).ClickAsync(new LocatorClickOptions { Timeout = 10000 });
                        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 20000 });
                        await SleepAsync(1.0);
                        await RandomScrollAsync(page);
                        await page.GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
                        await SleepAsync(0.5);
                    }
                    catch { }
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Log($"Search failed ({label}): {ex.Message}", "error");
            return false;
        }
    }

    private async Task TypeHumanAsync(IPage page, string selector, string text)
    {
        await page.ClickAsync(selector, new PageClickOptions { Timeout = 15000 });
        await page.FillAsync(selector, "");
        foreach (var ch in text)
        {
            if (Stopped) return;
            await page.Keyboard.TypeAsync(ch.ToString(), new KeyboardTypeOptions { Delay = _rng.Next(40, 120) });
        }
        await SleepAsync(_rng.NextDouble() * 0.5 + 0.3);
    }

    private async Task RandomScrollAsync(IPage page)
    {
        if (Stopped) return;
        var amount = _rng.Next(200, 600);
        var direction = _rng.Next(4) == 0 ? -1 : 1;
        try { await page.Mouse.WheelAsync(0, amount * direction); }
        catch { }
        await SleepAsync(_rng.NextDouble() + 0.5);
    }

    private async Task RandomMouseMoveAsync(IPage page)
    {
        if (Stopped) return;
        try
        {
            var vp = page.ViewportSize;
        var viewport = vp != null ? new ViewportSize { Width = vp.Width, Height = vp.Height } : new ViewportSize { Width = 1280, Height = 720 };
            var x = _rng.Next(50, Math.Max(51, viewport.Width - 50));
            var y = _rng.Next(50, Math.Max(51, viewport.Height - 50));
            await page.Mouse.MoveAsync(x, y, new MouseMoveOptions { Steps = _rng.Next(5, 15) });
        }
        catch { }
    }
}
