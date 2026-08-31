using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using RewardTracker.Infrastructure.Data;

var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
optionsBuilder.UseNpgsql("Host=localhost;Database=reward_tracker;Username=postgres;Password=admin");

using var dbContext = new AppDbContext(optionsBuilder.Options);
var sites = dbContext.RewardSites.ToList();

Console.WriteLine("--- SITOVI U BAZI ---");
foreach (var s in sites)
{
    Console.WriteLine($"ID: {s.Id}, Name: {s.Name}");
}
