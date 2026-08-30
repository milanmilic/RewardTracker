using Microsoft.Playwright;
using RewardTracker.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Hangfire;
using System.Threading.Tasks;
using System;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;

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

    public void ScheduleRandomDailyRuns()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        
        var activeAccounts = dbContext.Accounts.Where(a => a.IsActive && a.SessionData != null).ToList();
        var random = new Random();

        foreach (var account in activeAccounts)
        {
            var randomDelayMinutes = random.Next(5, 120);
            _backgroundJobs.Schedule<RewardAutomationService>(
                s => s.RunDailyTasksAsync(account.Id), 
                TimeSpan.FromMinutes(randomDelayMinutes)
            );
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

        var desktopOptions = new BrowserNewContextOptions { StorageState = account.SessionData };
        var desktopContext = await browser.NewContextAsync(desktopOptions);
        var desktopPage = await desktopContext.NewPageAsync();

        try 
        {
            // 1. PRVO KLIKANJE DAILY SET KARTICA (Za brze testiranje)
            Console.WriteLine("=== START: KLIKANJE DAILY SET KARTICA ===");
            try
            {
                Console.WriteLine("Ulazak na Dashboard preko Bing pretrage (anti-bot tehnika)...");
                await desktopPage.GotoAsync("https://www.bing.com");
                await Task.Delay(2000);
                
                var searchInput = desktopPage.Locator("[name='q']").First;
                await searchInput.FillAsync("Microsoft Rewards", new() { Force = true });
                await searchInput.PressAsync("Enter");
                await Task.Delay(4000);
                
                var rewardsLink = desktopPage.Locator("a[href*='rewards.bing.com']").First;
                await rewardsLink.ClickAsync();
                
                Console.WriteLine("Cekamo ucitavanje Rewards Dashboard-a...");
                await Task.Delay(10000); 

                var taskLinks = await desktopPage.EvaluateAsync<List<string>>(@"() => {
                    var selectors = [
                        'mee-rewards-daily-set-item-content a',
                        'mee-rewards-more-activities-card-item a',
                        '.ds-card-sec a',
                        '.c-card'
                    ];
                    var links = [];
                    selectors.forEach(sel => {
                        document.querySelectorAll(sel).forEach(el => {
                            if (el.href && el.href.startsWith('http') && links.indexOf(el.href) === -1) {
                                links.push(el.href);
                            }
                        });
                    });
                    return links;
                }");

                Console.WriteLine("Pronasao sam " + taskLinks.Count + " zadataka (kartica) na dashboardu.");

                foreach (var link in taskLinks)
                {
                    try
                    {
                        Console.WriteLine("Otvaram zadatak u novom tabu...");
                        var taskPage = await desktopContext.NewPageAsync();
                        await taskPage.GotoAsync(link);
                        await Task.Delay(random.Next(6000, 10000));
                        await taskPage.CloseAsync();
                        await Task.Delay(2000);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Greska pri otvaranju zadatka: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Nisam uspeo da zavrsim Daily Set zadatke: " + ex.Message);
            }

            // 2. ONDA BING PRETRAGE
            Console.WriteLine("=== START: BING PRETRAGE (Optimizovano) ===");
            for(int i = 0; i < 25; i++)
            {
                var term = baseWords[random.Next(baseWords.Length)] + " " + baseWords[random.Next(baseWords.Length)] + " " + random.Next(100, 9999);
                await desktopPage.GotoAsync("https://www.bing.com");
                await Task.Delay(2000);
                var searchInput = desktopPage.Locator("[name='q']").First;
                await searchInput.FillAsync(term, new() { Force = true });
                await searchInput.PressAsync("Enter");
                await Task.Delay(random.Next(4000, 10000)); 
            }

            // 3. CITANJE POENA
            Console.WriteLine("=== CITANJE UKUPNIH POENA SA BING EKRANA ===");
            await desktopPage.GotoAsync("https://www.bing.com");
            await Task.Delay(4000); 

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
            
            account.SessionData = await desktopContext.StorageStateAsync();
            dbContext.Accounts.Update(account);
            await dbContext.SaveChangesAsync();
        }
        catch (Exception ex) { Console.WriteLine("Greska PC: " + ex.Message); }
        finally { await desktopContext.CloseAsync(); }
        
        Console.WriteLine("=== BOT JE ZAVRSIO SA RADOM ===");
    }
}
