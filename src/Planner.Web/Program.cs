using System.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Planner.Web.Components;
using Planner.Web.Data;
using Planner.Web.Services;

var builder = WebApplication.CreateBuilder(args);
var databasePath = DatabaseInitialization.GetDatabasePath();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.Configure<MicrosoftIntegrationOptions>(builder.Configuration.GetSection(MicrosoftIntegrationOptions.SectionName));
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.SectionName));
builder.Services.AddSingleton<IBackupService>(services => new BackupService(databasePath, services.GetRequiredService<Microsoft.Extensions.Options.IOptions<BackupOptions>>()));
builder.Services.AddSingleton<ISystemFolderService, SystemFolderService>();
builder.Services.AddHttpClient(nameof(OutlookGraphAdapter), client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddDbContextFactory<PlannerDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddScoped<ITaskService, TaskService>();
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddScoped<IMetadataService, MetadataService>();
builder.Services.AddScoped<IBoardService, BoardService>();
builder.Services.AddScoped<IConflictDetector, ConflictDetector>();
builder.Services.AddScoped<IScheduleService, ScheduleService>();
builder.Services.AddScoped<ITodayQueryService, TodayQueryService>();
builder.Services.AddSingleton<IOutlookGraphAdapter, OutlookGraphAdapter>();
builder.Services.AddSingleton<MicrosoftAccountService>();
builder.Services.AddSingleton<IMicrosoftAccountService>(services => services.GetRequiredService<MicrosoftAccountService>());
builder.Services.AddSingleton<IMicrosoftTokenProvider>(services => services.GetRequiredService<MicrosoftAccountService>());
builder.Services.AddScoped<IOutlookSyncService, OutlookSyncService>();
builder.Services.AddScoped<IOutlookCalendarService, OutlookCalendarService>();
builder.Services.AddHostedService<OutlookPeriodicSyncService>();

var app = builder.Build();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
await app.InitializeDatabaseAsync();
if (app.Configuration.GetValue("Planner:OpenBrowserOnStart", app.Environment.IsProduction()))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault(value => value.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
            ?? addresses?.FirstOrDefault();
        if (address is not null)
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    });
}
app.Run();

public partial class Program;
