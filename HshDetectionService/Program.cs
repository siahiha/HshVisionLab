using HshDetectionService;
using HshDetectionEngin.Capture;
using Microsoft.Extensions.FileProviders;

ServicePaths paths = new();
ServiceSettingsStore settingsStore = new(paths);
ServiceSettingsDocument startupSettings = settingsStore.Service;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "Hsh Detection Service");
builder.WebHost.UseUrls(startupSettings.Http.ListenUrls);
builder.Services.AddCors(options => options.AddPolicy("configured-ui", policy =>
{
    string[] origins = (startupSettings.Http.CorsOrigins ?? [])
        .Where(origin => !string.IsNullOrWhiteSpace(origin))
        .Select(origin => origin.Trim().TrimEnd('/'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    if (origins.Length > 0)
    {
        policy.WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    }
}));
builder.Services.AddSignalR();
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(settingsStore);
builder.Services.AddSingleton<EventStore>();
builder.Services.AddSingleton<ArtifactStore>();
builder.Services.AddSingleton<WebRtcGateway>();
builder.Services.AddSingleton(MediaMtxRuntime.Shared);
builder.Services.AddSingleton<MediaMtxWebRtcProxy>();
builder.Services.AddSingleton<DetectionRuntimeHost>();
builder.Services.AddHostedService<ServiceWorker>();

WebApplication app = builder.Build();
app.UseCors("configured-ui");
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    ServiceSettingsDocument settings = context.RequestServices.GetRequiredService<ServiceSettingsStore>().Service;
    bool loopback = context.Connection.RemoteIpAddress is null || System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress);
    bool authorized = loopback && settings.Security.AllowLoopbackWithoutApiKey ||
        context.Request.Headers.TryGetValue("X-Hsh-Api-Key", out Microsoft.Extensions.Primitives.StringValues key) && key == settings.Security.ApiKey;
    if (!authorized)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { code = "unauthorized" });
        return;
    }
    await next();
});
string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
bool serveEmbeddedUi = startupSettings.Http.ServeUi && File.Exists(Path.Combine(webRoot, "index.html"));
if (serveEmbeddedUi)
{
    IFileProvider webFiles = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webFiles });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = webFiles });
}
ServiceApi.Map(app);
if (serveEmbeddedUi)
{
    app.MapFallback(async context =>
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(Path.Combine(webRoot, "index.html"));
    });
}
await app.RunAsync();
