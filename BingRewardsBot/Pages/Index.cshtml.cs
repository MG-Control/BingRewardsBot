using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BingRewardsBot.Pages;

public class IndexModel : PageModel
{
    private readonly IConfiguration _config;

    public bool DefaultHeadless { get; private set; }

    public IndexModel(IConfiguration config)
    {
        _config = config;
    }

    public void OnGet()
    {
        DefaultHeadless = _config.GetValue<bool>("BotOptions:DefaultHeadless", false);
    }
}
