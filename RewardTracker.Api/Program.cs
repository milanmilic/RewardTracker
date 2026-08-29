using Microsoft.EntityFrameworkCore;
using RewardTracker.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// Dodajemo konfiguraciju baze iz appsettings.json
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Dodajemo CORS kako bi Blazor klijent (koji radi na drugom portu) mogao da gadja ovaj API
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowBlazorClient", policy =>
    {
        policy.AllowAnyOrigin() // U produkciji ovde stavljamo tacan URL klijenta
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddOpenApi();
builder.Services.AddControllers();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    
    // Automatsko pokretanje migracija (kreira bazu i tabele na startu)
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseHttpsRedirection();
app.UseCors("AllowBlazorClient");
app.MapControllers();

app.Run();
