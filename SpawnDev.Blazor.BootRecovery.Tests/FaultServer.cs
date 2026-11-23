using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SpawnDev.Blazor.BootRecovery.Tests;

/// <summary>
/// A real HTTP server (Kestrel, loopback) for a published Blazor WebAssembly app's wwwroot, behaving like GitHub Pages: every
/// response carries <c>Cache-Control: max-age=600</c>, error pages included. While <see cref="Outage"/> is on, requests whose file
/// name starts with <see cref="FaultFilePrefix"/> get <see cref="FaultStatus"/> with an HTML error body (the host having a bad
/// moment during a deploy). The browser caches that error like any other response, which is the whole bug.
/// No request interception in the browser: Playwright's page.route disables the HTTP cache, which would hide the bug.
/// </summary>
public sealed class FaultServer : IAsyncDisposable
{
    readonly WebApplication _app;
    public string BaseUrl { get; }
    public volatile bool Outage;
    public string FaultFilePrefix { get; set; } = "";
    public int FaultStatus { get; set; } = 503;
    /// <summary>Requests per path (any status), to prove whether the browser went to the network.</summary>
    public ConcurrentDictionary<string, int> Requests { get; } = new();

    FaultServer(WebApplication app, string baseUrl) { _app = app; BaseUrl = baseUrl; }

    /// <summary>The faulted files: .wasm files (assemblies, dotnet.native.wasm) whose name starts with the prefix. The .wasm filter
    /// keeps "dotnet.native." from also matching the dotnet.native JS module, which is imported, not fetched with force-cache.</summary>
    public static bool IsTarget(string path, string fileNamePrefix)
    {
        var name = Path.GetFileName(path);
        return fileNamePrefix.Length > 0 && name.StartsWith(fileNamePrefix, StringComparison.Ordinal) && name.EndsWith(".wasm", StringComparison.Ordinal);
    }

    public int RequestsMatching(string fileNamePrefix) => Requests.Where(kv => IsTarget(kv.Key, fileNamePrefix)).Sum(kv => kv.Value);

    public static async Task<FaultServer> StartAsync(string wwwroot)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FaultServer? self = null;
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "/";
            self!.Requests.AddOrUpdate(path, 1, (_, n) => n + 1);
            ctx.Response.Headers.CacheControl = "max-age=600";
            if (self.Outage && IsTarget(path, self.FaultFilePrefix))
            {
                ctx.Response.StatusCode = self.FaultStatus;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync($"<!DOCTYPE html><html><body><h1>{self.FaultStatus}</h1><p>Simulated host error page.</p></body></html>");
                return;
            }
            if (path == "/") ctx.Request.Path = "/index.html";
            await next();
        });
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".wasm"] = "application/wasm";
        types.Mappings[".dat"] = "application/octet-stream";
        types.Mappings[".blat"] = "application/octet-stream";
        types.Mappings[".webcil"] = "application/octet-stream";
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(wwwroot),
            ContentTypeProvider = types,
            ServeUnknownFileTypes = true,
            DefaultContentType = "application/octet-stream",
            OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "max-age=600",
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new FaultServer(app, address.TrimEnd('/') + "/");
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
