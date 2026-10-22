using PrismOS.Application.Repositories;
using PrismOS.Infrastructure;
using PrismOS.Web;

var builder = WebApplication.CreateBuilder(args);
BackendSettings settings = BackendSettings.Load(builder.Configuration,
    builder.Environment.ContentRootPath);

builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = settings.MaxUploadBytes);
builder.Services.AddBackendServices();
builder.Services.AddSingleton(settings);
builder.Services.AddInfrastructure(settings.Infrastructure);
IPackageRepository packages = await InfrastructureBootstrapper.LoadPackagesAsync(settings.Infrastructure);
builder.Services.AddSingleton(packages);
builder.Services.AddSingleton<IPackageRepository>(packages);

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    await InfrastructureBootstrapper.InitializeDatabaseAsync(scope.ServiceProvider,
        settings.Infrastructure.LegacyAccountsPath);
}
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();
app.UseRateLimiter();
app.MapControllers();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
