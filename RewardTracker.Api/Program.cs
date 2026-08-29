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
app.MapGet("/api/test-hangfire", (IBackgroundJobClient backgroundJobs) => 
{
    // Predajemo posao Hangfire-u. On će ga odmah preuzeti i izvršiti u pozadini!
    backgroundJobs.Enqueue<RewardTracker.Infrastructure.Services.RewardAutomationService>(service => service.RunTestBrowserAsync());
    
    return Results.Ok("Posao uspešno predat Hangfire-u! Prebaci se na /hangfire tab da pratiš izvršavanje.");
});

app.Run();
