using Microsoft.Playwright;
using RewardTracker.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using System;
using System.Text.RegularExpressions;

namespace RewardTracker.Infrastructure.Services;

public class RewardAutomationService
{
    private readonly IServiceProvider _serviceProvider;

    public RewardAutomationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task RunTestBrowserAsync()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
        var page = await browser.NewPageAsync();
        await page.GotoAsync("https://rewards.bing.com");
        await Task.Delay(15000);
    }

    public async Task StartLoginSessionAsync(int accountId)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await dbContext.Accounts.FindAsync(accountId);
        if (account == null) return;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        try 
        {
            await page.GotoAsync("https://login.live.com/");
            await Task.Delay(90000); 

            account.SessionData = await context.StorageStateAsync();
            dbContext.Accounts.Update(account);
            await dbContext.SaveChangesAsync();
        }
        catch (Exception ex) { Console.WriteLine("Greska: " + ex.Message); }
        finally { await browser.CloseAsync(); }
    }

    public async Task RunDailyTasksAsync(int accountId)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await dbContext.Accounts.FindAsync(accountId);
        if (account == null || string.IsNullOrEmpty(account.SessionData)) return;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
        
        var random = new Random();
        var baseWords = new[] { "Srbija", "Beograd", "Vesti", "Sport", "Filmovi", "Recepti", "Tehnologija", "Zanimljivosti", "Istorija", "Automobili", "Kompjuteri", "Muzika" };

        Console.WriteLine("=== START: PC PRETRAGE ===");
        var desktopOptions = new BrowserNewContextOptions { StorageState = account.SessionData };
        var desktopContext = await browser.NewContextAsync(desktopOptions);
        var desktopPage = await desktopContext.NewPageAsync();

        try 
        {
            for(int i = 0; i < 3; i++) // Test
            {
                var term = baseWords[random.Next(baseWords.Length)] + " " + random.Next(1000, 99999);
                await desktopPage.GotoAsync("https://www.bing.com");
                await desktopPage.FillAsync("[name='q']", term);
                await desktopPage.PressAsync("[name='q']", "Enter");
                await Task.Delay(random.Next(3000, 6000)); 
            }

            Console.WriteLine("=== CITANJE UKUPNIH POENA SA BING EKRANA ===");
            await Task.Delay(3000); 

            // Sada NE IDEMO na rewards.bing.com jer te Microsoft tamo izbaci.
            // Ostajemo tu gde jesmo (na www.bing.com) gde si video poene gore desno!
            var pointsText = await desktopPage.EvaluateAsync<string>(@"
                () => {
                    // Trazimo bilo koji link koji vodi ka rewards i ima broj u sebi
                    var aTags = document.querySelectorAll('a');
                    for(var i = 0; i < aTags.length; i++) {
                        if(aTags[i].href.indexOf('rewards.bing.com') > -1 && aTags[i].innerText.match(/\d/)) {
                            return aTags[i].innerText;
                        }
                    }
                    // Ako to ne uspe, trazimo genericke id-jeve koje bing koristi za poene
                    var rh = document.getElementById('id_rh');
                    if (rh && rh.innerText.match(/\d/)) return rh.innerText;
                    
                    var rc = document.getElementById('id_rc');
                    if (rc && rc.innerText.match(/\d/)) return rc.innerText;

                    return '';
                }
            ");

            Console.WriteLine("Tekst izvucen sa ekrana gore desno: '" + pointsText + "'");

            if (!string.IsNullOrWhiteSpace(pointsText))
            {
                var cleanNumber = Regex.Replace(pointsText, "[^0-9]", "");
                if (int.TryParse(cleanNumber, out int pts) && pts > 0)
                {
                    Console.WriteLine("Uspesno prepoznat broj: " + pts);
                    
                    var log = new RewardTracker.Core.Entities.PointLog
                    {
                        AccountId = account.Id,
                        Date = DateTime.UtcNow,
                        TotalPointsAfter = pts,
                        PointsEarned = pts - account.CurrentPoints
                    };
                    dbContext.PointLogs.Add(log);
                    
                    account.CurrentPoints = pts;
                    dbContext.Accounts.Update(account);
                    await dbContext.SaveChangesAsync();
                }
                else
                {
                    Console.WriteLine("Nisam uspeo da pretvorim tekst u broj.");
                }
            }
            else
            {
                Console.WriteLine("Nisam uspeo da pronadjem poene gore desno.");
            }

            account.SessionData = await desktopContext.StorageStateAsync();
            dbContext.Accounts.Update(account);
            await dbContext.SaveChangesAsync();
        }
        catch (Exception ex) { Console.WriteLine("Greska PC: " + ex.Message); }
        finally { await desktopContext.CloseAsync(); }

        Console.WriteLine("=== START: MOBILNE PRETRAGE ===");
        var mobileOptions = playwright.Devices["Pixel 5"];
        mobileOptions.StorageState = account.SessionData; 
        
        var mobileContext = await browser.NewContextAsync(mobileOptions);
        var mobilePage = await mobileContext.NewPageAsync();

        try 
        {
            for(int i = 0; i < 3; i++)
            {
                var term = baseWords[random.Next(baseWords.Length)] + " " + random.Next(1000, 99999);
                await mobilePage.GotoAsync("https://www.bing.com");
                await mobilePage.FillAsync("[name='q']", term);
                await mobilePage.PressAsync("[name='q']", "Enter");
                await Task.Delay(random.Next(3000, 6000));
            }
        }
        catch (Exception ex) { Console.WriteLine("Greska Mobilni: " + ex.Message); }
        finally { await mobileContext.CloseAsync(); }
    }
}
