using Microsoft.EntityFrameworkCore;
using RewardTracker.Infrastructure.Data;
using Hangfire;
using Hangfire.PostgreSql;

var builder = WebApplication.CreateBuilder(args);

// Baza
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Hangfire konfiguracija
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(builder.Configuration.GetConnectionString("DefaultConnection"))));

// Pokrećemo server koji vrti poslove u pozadini
builder.Services.AddHangfireServer();

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowBlazorClient", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

builder.Services.AddOpenApi();
builder.Services.AddControllers();

// Podesavanja bota (headless, putanja za snimke ekrana, tajmauti)
builder.Services.Configure<RewardTracker.Infrastructure.Services.BotOptions>(
    builder.Configuration.GetSection(RewardTracker.Infrastructure.Services.BotOptions.SectionName));

// Registrujemo Playwright servis
builder.Services.AddScoped<RewardTracker.Infrastructure.Services.RewardAutomationService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    
    // Automatska migracija
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseHttpsRedirection();
app.UseCors("AllowBlazorClient");
app.MapControllers();

// Ukljucujemo Hangfire Dashboard (UI)
app.UseHangfireDashboard("/hangfire");

// Testna ruta koja koristi Hangfire da zakaže Playwright posao u pozadini
app.MapGet("/api/test-hangfire", (IBackgroundJobClient backgroundJobs) => { backgroundJobs.Enqueue(() => Console.WriteLine("Hangfire test!")); return Results.Ok("Test job enqueued"); });

// Dodajemo zakazivanje na sistemskom nivou
var recurringJobManager = app.Services.GetRequiredService<IRecurringJobManager>();
recurringJobManager.AddOrUpdate<RewardTracker.Infrastructure.Services.RewardAutomationService>(
    "dnevni-okidac-pretraga",
    service => service.ScheduleRandomDailyRuns(),
    "0 6 * * *" // Svaki dan u 06:00
);

app.Run();

