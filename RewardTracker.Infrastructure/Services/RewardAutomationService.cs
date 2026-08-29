using Microsoft.Playwright;
using RewardTracker.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Hangfire;
using System.Threading.Tasks;
using System;
using System.Text.RegularExpressions;
using System.Linq;

namespace RewardTracker.Infrastructure.Services;

public class RewardAutomationService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IBackgroundJobClient _backgroundJobs;

    public RewardAutomationService(IServiceProvider serviceProvider, IBackgroundJobClient backgroundJobs)
    {
        _serviceProvider = serviceProvider;
        _backgroundJobs = backgroundJobs;
    }

    // Glavni zakazivac (okida se npr. svako jutro u 06:00 preko Hangfire-a)
    public void ScheduleRandomDailyRuns()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        
        var activeAccounts = dbContext.Accounts.Where(a => a.IsActive && a.SessionData != null).ToList();
        var random = new Random();

        foreach (var account in activeAccounts)
        {
            // Nasumicno vreme pokretanja izmedju 5 i 120 minuta od trenutka zakazivanja
            // Da Microsoft ne bi primetio fiksno okidanje u minut
            var randomDelayMinutes = random.Next(5, 120);
            
            _backgroundJobs.Schedule<RewardAutomationService>(
                s => s.RunDailyTasksAsync(account.Id), 
                TimeSpan.FromMinutes(randomDelayMinutes)
            );
            
            Console.WriteLine($"Nalog ID {account.Id} zakazan za pokretanje za {randomDelayMinutes} minuta.");
        }
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
        var baseWords = new[] { "Srbija", "Beograd", "Vesti", "Sport", "Filmovi", "Recepti", "Tehnologija", "Zanimljivosti", "Istorija", "Automobili", "Kompjuteri", "Muzika", "Klima", "Putovanja", "Fizika", "Astronomija", "Planete", "Ekonomija", "Zdravlje", "Trening", "Ishrana", "Programiranje", "Arhitektura" };

        Console.WriteLine("=== START: PC PRETRAGE ===");
        var desktopOptions = new BrowserNewContextOptions { StorageState = account.SessionData };
        var desktopContext = await browser.NewContextAsync(desktopOptions);
        var desktopPage = await desktopContext.NewPageAsync();

        try 
        {
            // Radimo 35 pretraga za PC (Maksimum za poene je obicno 30)
            for(int i = 0; i < 35; i++)
            {
                var term = baseWords[random.Next(baseWords.Length)] + " " + baseWords[random.Next(baseWords.Length)] + " " + random.Next(100, 9999);
                await desktopPage.GotoAsync("https://www.bing.com");
                
                // Cekamo malo da strana bude spremna
                await Task.Delay(2000);
                
                var searchInput = desktopPage.Locator("[name='q']").First;
                await searchInput.FillAsync(term, new() { Force = true });
                await searchInput.PressAsync("Enter");
                
                // Random pauza izmedju pretraga (od 4 do 12 sekundi - zvuci sasvim prirodno)
                await Task.Delay(random.Next(4000, 12000)); 
            }

            Console.WriteLine("=== CITANJE UKUPNIH POENA SA BING EKRANA ===");
            await Task.Delay(3000); 

            var pointsText = await desktopPage.EvaluateAsync<string>(@"
                () => {
                    var el = document.querySelector('span.points-container');
                    if (el) return el.innerText;
                    
                    var backup = document.querySelector('[data-tag=""RewardsHeader.Counter""]');
                    if (backup) return backup.innerText;

                    return '';
                }
            ");

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
            }
            
            // Cuvamo osvezenu sesiju sa novim kolacicima
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
            // Radimo 25 pretraga za Mobilni (Maksimum za poene je 20)
            for(int i = 0; i < 25; i++)
            {
                var term = baseWords[random.Next(baseWords.Length)] + " " + baseWords[random.Next(baseWords.Length)] + " " + random.Next(100, 9999);
                await mobilePage.GotoAsync("https://www.bing.com");
                
                await Task.Delay(2000);
                
                // Mobilna verzija cesto ima drugaciji ID za pretragu, ali name='q' obicno radi. 
                // Koristimo Force = true da ignorisemo eventualne popup-ove koji ga prekrivaju
                var searchInput = mobilePage.Locator("[name='q']").First;
                await searchInput.FillAsync(term, new() { Force = true });
                await searchInput.PressAsync("Enter");
                
                await Task.Delay(random.Next(4000, 12000));
            }
        }
        catch (Exception ex) { Console.WriteLine("Greska Mobilni: " + ex.Message); }
        finally { await mobileContext.CloseAsync(); }
        
        Console.WriteLine("=== BOT JE ZAVRSIO SA RADOM ===");
    }
}
