using NUnit.Framework;
using System.Diagnostics;
using Microsoft.Playwright;

namespace SpawnDev.Blazor.BootRecovery.Tests;

/// <summary>
/// dotnet/runtime#135090 in a WEB WORKER (SpawnDev.BlazorJS.WebWorkers), in a real Chromium with its real HTTP cache. A worker boots the
/// .NET runtime again with the same loader, so a boot file the HTTP cache holds as an error stops the worker even though the page is up.
/// The HTTP cache is shared with the page, which normally heals a bad entry first; these tests make the WORKER the first to need it:
/// <list type="number">
/// <item>the page boots normally;</item>
/// <item>during an "outage" the page fetches the boot file with cache: "reload", so the HTTP cache now stores the error (max-age=600,
/// as GitHub Pages sends);</item>
/// <item>the host recovers; the page starts a worker and calls into it.</item>
/// </list>
/// FIX (the app WITH this package): the worker boots and answers, after exactly one re-fetch of the file BY THE WORKER (counted by the
/// worker's Referer). CONTROL (WITHOUT it): the worker never answers and never asks the server for the file (the cached error is served
/// from the cache) while it does make other requests, so the counter cannot read zero vacuously.
/// </summary>
[TestFixture]
[NonParallelizable]
public class WorkerBootRecoveryTests
{
    const string WorkerScript = "spawndev.blazorjs.webworkers.js";
    const string AppAssembly = "SpawnDev.Blazor.BootRecovery.WorkersDemo";

    static string RepoRoot => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
    static string PublishRoot => Path.Combine(TestContext.CurrentContext.TestDirectory, "published");
    static string Wwwroot(bool fix) => Path.Combine(PublishRoot, fix ? "workers-fix" : "workers-control", "wwwroot");

    IPlaywright _playwright = null!;
    IBrowser _browser = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var packageVersion = Environment.GetEnvironmentVariable("BOOTRECOVERY_PACKAGE_VERSION");
        var fixArgs = string.IsNullOrEmpty(packageVersion) ? "" : $"-p:BootRecoveryPackageVersion={packageVersion}";
        TestContext.Progress.WriteLine(string.IsNullOrEmpty(packageVersion) ? "workers fix variant: project reference" : $"workers fix variant: package {packageVersion}");
        Publish(Path.Combine(PublishRoot, "workers-fix"), fixArgs);
        Publish(Path.Combine(PublishRoot, "workers-control"), "-p:BootRecovery=false");
        Assert.That(Directory.GetFiles(Wwwroot(true), "SpawnDev.Blazor.BootRecovery*.lib.module.js", SearchOption.AllDirectories), Is.Not.Empty,
            "a fix publish must contain the JS initializer");
        Assert.That(Directory.GetFiles(Wwwroot(false), "SpawnDev.Blazor.BootRecovery*.lib.module.js", SearchOption.AllDirectories), Is.Empty,
            "a control publish must NOT contain the JS initializer");
        Assert.That(File.Exists(Path.Combine(Wwwroot(true), WorkerScript)), Is.True, "the publish must carry the web worker script");
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_browser != null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    static void Publish(string output, string extraArgs)
    {
        if (Directory.Exists(output)) Directory.Delete(output, true);
        var project = Path.Combine(RepoRoot, AppAssembly, AppAssembly + ".csproj");
        var psi = new ProcessStartInfo("dotnet", $"publish \"{project}\" -c Release -o \"{output}\" {extraArgs}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0) Assert.Fail($"publish {extraArgs} failed ({p.ExitCode}):\n{stdout.Result}\n{stderr.Result}");
    }

    sealed record WorkerRun(string Reply, int WorkerFetchesOfFile, int WorkerRequestsTotal, int CachedStatusAfter, string Console);

    /// <summary>Boot the page, poison the HTTP cache entry of the first boot file matching <paramref name="prefix"/>, recover the host,
    /// then start a worker and ping it.</summary>
    async Task<WorkerRun> RunAsync(bool fix, string prefix, bool poison = true)
    {
        await using var server = await FaultServer.StartAsync(Wwwroot(fix));
        server.WorkerRefererMarker = WorkerScript;
        await using var context = await _browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var console = new List<string>();
        page.Console += (_, m) => { lock (console) console.Add($"[{m.Type}] {m.Text}"); };
        page.PageError += (_, e) => { lock (console) console.Add($"[pageerror] {e}"); };
        string Log() { lock (console) return string.Join("\n", console); }

        await page.GotoAsync(server.BaseUrl);
        await page.Locator("#booted").WaitForAsync(new() { Timeout = 60000 });
        Assert.That(server.WorkerRequests.Values.Sum(), Is.EqualTo(0), "no worker has run yet");

        var target = server.Requests.Keys.Where(k => FaultServer.IsTarget(k, prefix)).ToList();
        Assert.That(target, Has.Count.EqualTo(1), $"the page boot must have fetched exactly one '{prefix}*.wasm' boot file");
        var url = server.BaseUrl.TrimEnd('/') + target[0];

        if (poison)
        {
            // 2. outage: re-fetch with cache: "reload" so the HTTP cache now stores the error
            server.FaultFilePrefix = prefix; server.FaultStatus = 503; server.Outage = true;
            int poisonStatus = await page.EvaluateAsync<int>("u => fetch(u, { cache: 'reload' }).then(r => r.status)", url);
            server.Outage = false;
            Assert.That(poisonStatus, Is.EqualTo(503), "the outage must answer 503");
            int cachedStatus = await page.EvaluateAsync<int>("u => fetch(u, { cache: 'only-if-cached', mode: 'same-origin' }).then(r => r.status)", url);
            Assert.That(cachedStatus, Is.EqualTo(503), "the HTTP cache must now hold the error for the boot file (read without the network)");
        }

        // 3. host recovered: start a worker and call into it
        int before = server.WorkerRequestsMatching(prefix);
        string reply = await page.EvaluateAsync<string>(
            $"t => DotNet.invokeMethodAsync('{AppAssembly}', 'StartWorkerAndPing', t)", 60000);
        int workerFetches = server.WorkerRequestsMatching(prefix) - before;
        int cachedAfter = await page.EvaluateAsync<int>("u => fetch(u, { cache: 'only-if-cached', mode: 'same-origin' }).then(r => r.status)", url);
        return new WorkerRun(reply, workerFetches, server.WorkerRequests.Values.Sum(), cachedAfter, Log());
    }

    /// <summary>The package must never break a worker that has nothing to recover (first found 2026-11-22: the worker's module patcher
    /// turned the initializer's `export function` into a function expression, so its own call threw and the worker never booted).</summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task HealthyStart_WorkerBoots_NoWorkerRefetch(bool fix)
    {
        var r = await RunAsync(fix, "System.Private.CoreLib", poison: false);
        TestContext.Progress.WriteLine($"healthy {(fix ? "fix" : "control")}: reply '{r.Reply}', worker fetches of CoreLib {r.WorkerFetchesOfFile}, worker requests {r.WorkerRequestsTotal}");
        Assert.That(r.Reply, Does.StartWith("pong from"), $"a healthy start must boot the worker{Environment.NewLine}{r.Console}");
        Assert.That(r.WorkerFetchesOfFile, Is.EqualTo(0), "a healthy worker start is served from the cache (no re-fetch)");
        Assert.That(r.Console, Does.Not.Contain("library initializer"), "no library initializer may fail");
    }

    [TestCase("System.Private.CoreLib")]
    [TestCase("dotnet.native")]
    public async Task Fix_WorkerRecoversCachedBootError(string prefix)
    {
        var r = await RunAsync(fix: true, prefix);
        TestContext.Progress.WriteLine($"fix {prefix}: reply '{r.Reply}', worker fetches of the file {r.WorkerFetchesOfFile}, worker requests {r.WorkerRequestsTotal}, cache after {r.CachedStatusAfter}");
        Assert.That(r.Reply, Does.StartWith("pong from"), $"the worker must boot and answer\n{r.Console}");
        Assert.That(r.WorkerFetchesOfFile, Is.EqualTo(1), $"the WORKER must re-fetch the bad boot file exactly once (cache: reload)\n{r.Console}");
        Assert.That(r.CachedStatusAfter, Is.EqualTo(200), "the worker's re-fetch must have replaced the cached error");
    }

    [TestCase("System.Private.CoreLib")]
    [TestCase("dotnet.native")]
    public async Task Control_WithoutFix_WorkerStaysStuck(string prefix)
    {
        var r = await RunAsync(fix: false, prefix);
        TestContext.Progress.WriteLine($"control {prefix}: reply '{r.Reply}', worker fetches of the file {r.WorkerFetchesOfFile}, worker requests {r.WorkerRequestsTotal}, cache after {r.CachedStatusAfter}");
        Assert.That(r.Reply, Does.Not.StartWith("pong from"), $"without the package the worker must NOT boot (the bug)\n{r.Console}");
        Assert.That(r.WorkerRequestsTotal, Is.GreaterThan(0), "the worker must have made requests (else the worker counter proves nothing)");
        Assert.That(r.WorkerFetchesOfFile, Is.EqualTo(0), "without the package the worker never asks the server for the file again (force-cache served the error)");
        Assert.That(r.CachedStatusAfter, Is.EqualTo(503), "the cached error must still be there");
    }
}
