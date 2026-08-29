using Microsoft.Playwright;
using System.Threading.Tasks;

namespace RewardTracker.Infrastructure.Services;

public class RewardAutomationService
{
    // Ovo je samo testna metoda da potvrdimo da pregledac radi
    public async Task RunTestBrowserAsync()
    {
        // Inicijalizujemo Playwright
        using var playwright = await Playwright.CreateAsync();
        
        // Pokrećemo Chromium (Edge/Chrome). Headless = false znači da će se VIDETI prozor.
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions 
        { 
            Headless = false 
        });
        
        var page = await browser.NewPageAsync();
        
        // Odemo na bing rewards
        await page.GotoAsync("https://rewards.bing.com");
        
        // Cekamo 15 sekundi da vidis prozor, pa se zatvara
        await Task.Delay(15000);
    }
}
