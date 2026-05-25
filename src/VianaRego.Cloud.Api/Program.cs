using VianaRego.Cloud.Api.Data;
using VianaRego.Cloud.Api.Services;
using Microsoft.EntityFrameworkCore;
using VianaRego.Shared.Configuration;
using System.Text.Encodings.Web;
using System.Text.Json;

// 1. Load environment
var currentDir = Directory.GetCurrentDirectory();
while (!string.IsNullOrEmpty(currentDir))
{
    var envPath = Path.Combine(currentDir, ".env");
    if (File.Exists(envPath))
    {
        DotNetEnv.Env.Load(envPath);
        break;
    }
    currentDir = Directory.GetParent(currentDir)?.FullName;
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

// 2. Connection String (Standardized Path)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrEmpty(connectionString))
{
    var dbPath = DataPath.GetPath("viana-cloud.db");
    connectionString = $"Data Source={dbPath}";
}

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));

// 3. Services
builder.Services.AddSingleton<SigningService>();
builder.Services.AddSingleton<MqttMessagingService>();
builder.Services.AddHostedService<MqttMessagingService>(sp => sp.GetRequiredService<MqttMessagingService>());

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Prevent HTML encoding of URLs (& should not become &amp;)
        options.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// 4. Ensure Database Created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseAuthorization();
app.MapControllers();

app.Run();
