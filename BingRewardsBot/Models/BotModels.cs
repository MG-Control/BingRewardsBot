using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BingRewardsBot.Models;

public class StartRequest
{
    public string? Email { get; set; }
    public string? Password { get; set; }

    [JsonPropertyName("desktop_count")]
    public int DesktopCount { get; set; } = 30;

    [JsonPropertyName("mobile_count")]
    public int MobileCount { get; set; } = 20;

    public bool Headless { get; set; }

    public (bool Valid, string Error) Validate()
    {
        var email = Email?.Trim() ?? "";
        if (string.IsNullOrEmpty(email))
            return (false, "Email is required.");

        if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            return (false, "Invalid email format.");

        if (DesktopCount < 0 || MobileCount < 0 || DesktopCount > 999 || MobileCount > 999)
            return (false, "Search counts must be between 0 and 999.");

        if (DesktopCount == 0 && MobileCount == 0)
            return (false, "At least one desktop or mobile search is required.");

        var profilePath = Services.BingRewardsBotRunner.GetProfileDir(email);
        if (!Directory.Exists(profilePath) || !Directory.EnumerateFileSystemEntries(profilePath).Any())
        {
            if (string.IsNullOrEmpty(Password))
                return (false, "Password is required for first login (no saved session).");
        }

        return (true, "");
    }
}

public class ProgressData
{
    public int DesktopDone { get; set; }
    public int DesktopTotal { get; set; }
    public int MobileDone { get; set; }
    public int MobileTotal { get; set; }
    public string Phase { get; set; } = "idle";
    public string Status { get; set; } = "idle";
}

public class StatusResponse
{
    public bool Running { get; set; }
    public ProgressData Progress { get; set; } = new();
    public string? LastError { get; set; }
}
