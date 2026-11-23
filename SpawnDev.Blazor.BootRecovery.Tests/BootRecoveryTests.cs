using NUnit.Framework;
using System.Diagnostics;
using Microsoft.Playwright;

namespace SpawnDev.Blazor.BootRecovery.Tests;

/// <summary>
/// dotnet/runtime#135090 end to end, in a real Chromium with its real HTTP cache: a fingerprinted boot resource answered with a
/// cacheable error during an "outage", then the host recovers and the visitor reloads.
/// <list type="bullet">
/// <item>CONTROL (the demo published WITHOUT this package): must stay broken after the host recovered, and must not even ask the
/// server for the resource again. That is the bug, reproduced, and it proves these tests can fail.</item>
/// <item>FIX (the demo published WITH this package): must boot on the first reload after the host recovered (one network re-fetch
/// of the bad resource), and the load after that must be a plain cache hit again (no request at all for that resource).</item>
/// </list>
/// Faulted resources: System.Private.CoreLib (an assembly, the "assembly" fetch path) and dotnet.native (the runtime's own .wasm,
/// fetched for streaming instantiation), each with a 503 and a 404.
/// </summary>
[TestFixture]
[NonParallelizable]
public class BootRecoveryTests
{
    static string RepoRoot => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
    static string PublishRoot => Path.Combine(TestContext.CurrentContext.TestDirectory, "published");

    /// <summary>The published wwwroot for a variant. Service-worker cases use the "-plain" publishes
    /// (OverrideHtmlAssetPlaceholders=false: plain blazor.webassembly.js / dotnet.js names, exactly how SpawnDev.GameUI.Demo and VoxelEngine
    /// publish), because the COI script injects the plain _framework/blazor.webassembly.js. The others use the template default (fingerprinted
    /// script + import map, like AssistZ and the ML demo). Boot assemblies are fingerprinted and fetched with force-cache in both.</summary>
    static string Wwwroot(bool fix, bool serviceWorker) =>
        Path.Combine(PublishRoot, (fix ? "fix" : "control") + (serviceWorker ? "-plain" : ""), "wwwroot");

    IPlaywright _playwright = null!;
    IBrowser _browser = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // Every variant is published from source every run, so the tests always measure the current library.
        // BOOTRECOVERY_PACKAGE_VERSION=X: the fix variants consume the packed nupkg from the local feed (release check).
        var packageVersion = Environment.GetEnvironmentVariable("BOOTRECOVERY_PACKAGE_VERSION");
        var fixArgs = string.IsNullOrEmpty(packageVersion) ? "" : $"-p:BootRecoveryPackageVersion={packageVersion}";
        TestContext.Progress.WriteLine(string.IsNullOrEmpty(packageVersion) ? "fix variants: project reference" : $"fix variants: package {packageVersion}");
        Publish(Path.Combine(PublishRoot, "fix"), fixArgs);
        Publish(Path.Combine(PublishRoot, "control"), "-p:BootRecovery=false");
        Publish(Path.Combine(PublishRoot, "fix-plain"), fixArgs + " -p:OverrideHtmlAssetPlaceholders=false");
        Publish(Path.Combine(PublishRoot, "control-plain"), "-p:BootRecovery=false -p:OverrideHtmlAssetPlaceholders=false");
        foreach (var sw in new[] { false, true })
        {
            Assert.That(Directory.GetFiles(Wwwroot(true, sw), "SpawnDev.Blazor.BootRecovery*.lib.module.js", SearchOption.AllDirectories), Is.Not.Empty,
                "a fix publish must contain the JS initializer");
            Assert.That(Directory.GetFiles(Wwwroot(false, sw), "SpawnDev.Blazor.BootRecovery*.lib.module.js", SearchOption.AllDirectories), Is.Empty,
                "a control publish must NOT contain the JS initializer");
        }
        Assert.That(File.Exists(Path.Combine(Wwwroot(true, true), "_framework", "blazor.webassembly.js")), Is.True,
            "the -plain publish must carry the plain blazor.webassembly.js the COI script injects");
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
        var project = Path.Combine(RepoRoot, "SpawnDev.Blazor.BootRecovery.Demo", "SpawnDev.Blazor.BootRecovery.Demo.csproj");
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

    enum Outcome { Booted, Failed }

    sealed record LoadResult(Outcome Outcome, int Recovered, string Console, bool ServiceWorkerControlled);

    /// <summary>"" = index.html (default loader, no service worker). "coi.html" = the same app booted behind the COI service worker
    /// most SpawnDev demos use (copied verbatim from SpawnDev.GameUI.Demo): every boot fetch then goes through its fetch handler.</summary>
    static string PagePath(bool serviceWorker) => serviceWorker ? "coi.html" : "";

    /// <summary>A service-worker case that is not actually controlled would pass while testing nothing.</summary>
    static void AssertController(LoadResult r, bool serviceWorker, string when) =>
        Assert.That(r.ServiceWorkerControlled, Is.EqualTo(serviceWorker), $"{when}: service worker control must be {serviceWorker}\n{r.Console}");

    /// <summary>Navigate (or reload) and wait for the app to either render #booted or fail to start.</summary>
    static async Task<LoadResult> LoadAsync(IPage page, string url, bool reload)
    {
        var console = new List<string>();
        void OnConsole(object? _, IConsoleMessage m) { lock (console) console.Add($"[{m.Type}] {m.Text}"); }
        void OnPageError(object? _, string e) { lock (console) console.Add($"[pageerror] {e}"); }
        page.Console += OnConsole;
        page.PageError += OnPageError;
        try
        {
            if (reload) await page.ReloadAsync(); else await page.GotoAsync(url);
            // Booted = the page rendered. Failed = Blazor showed its error UI, or a boot download failed (console).
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (await page.Locator("#booted").CountAsync() > 0)
                    {
                        // Let OnAfterRenderAsync read the recovery state.
                        await page.Locator("#recovered").Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(".+") }).WaitForAsync(new() { Timeout = 15000 });
                        break;
                    }
                    if (await page.Locator("#blazor-error-ui").IsVisibleAsync()) break;
                }
                catch (PlaywrightException)
                {
                    // The COI service worker page reloads itself once on a first visit; a query racing that navigation just retries.
                }
                bool downloadFailed;
                lock (console) downloadFailed = console.Any(c => c.StartsWith("[error]") && c.Contains("failed", StringComparison.OrdinalIgnoreCase));
                if (downloadFailed) { await page.WaitForTimeoutAsync(1000); break; }
                await page.WaitForTimeoutAsync(100);
            }
            bool booted = await page.Locator("#booted").CountAsync() > 0;
            int recovered = await page.EvaluateAsync<int>("(globalThis[Symbol.for('SpawnDev.Blazor.BootRecovery')]?.recovered ?? []).length");
            bool controlled = await page.EvaluateAsync<bool>("!!(navigator.serviceWorker && navigator.serviceWorker.controller)");
            string log; lock (console) log = string.Join("\n", console);
            return new LoadResult(booted ? Outcome.Booted : Outcome.Failed, recovered, log, controlled);
        }
        finally
        {
            page.Console -= OnConsole;
            page.PageError -= OnPageError;
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task HealthyStart_NoRecovery_NoExtraRequests(bool serviceWorker)
    {
        await using var server = await FaultServer.StartAsync(Wwwroot(fix: true, serviceWorker));
        await using var context = await _browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var first = await LoadAsync(page, server.BaseUrl + PagePath(serviceWorker), reload: false);
        Assert.That(first.Outcome, Is.EqualTo(Outcome.Booted), first.Console);
        AssertController(first, serviceWorker, "first load");
        Assert.That(first.Recovered, Is.EqualTo(0), first.Console);
        int coreLib = server.RequestsMatching("System.Private.CoreLib.");
        Assert.That(coreLib, Is.EqualTo(1), "a healthy start fetches each boot resource once");

        var second = await LoadAsync(page, server.BaseUrl, reload: true);
        Assert.That(second.Outcome, Is.EqualTo(Outcome.Booted), second.Console);
        AssertController(second, serviceWorker, "reload");
        Assert.That(second.Recovered, Is.EqualTo(0), second.Console);
        Assert.That(server.RequestsMatching("System.Private.CoreLib."), Is.EqualTo(coreLib),
            "a healthy reload is served from the HTTP cache (force-cache): the fix must not add requests");
    }

    static IEnumerable<TestCaseData> Faults()
    {
        foreach (var serviceWorker in new[] { false, true })
            foreach (var prefix in new[] { "System.Private.CoreLib.", "dotnet.native." })
                foreach (var status in new[] { 503, 404 })
                    yield return new TestCaseData(prefix, status, serviceWorker)
                        .SetArgDisplayNames(prefix.TrimEnd('.'), status.ToString(), serviceWorker ? "COI service worker" : "no service worker");
    }

    [TestCaseSource(nameof(Faults))]
    public async Task Control_WithoutFix_StaysBrokenAfterHostRecovers(string prefix, int status, bool serviceWorker)
    {
        await using var server = await FaultServer.StartAsync(Wwwroot(fix: false, serviceWorker));
        server.FaultFilePrefix = prefix; server.FaultStatus = status; server.Outage = true;
        await using var context = await _browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var during = await LoadAsync(page, server.BaseUrl + PagePath(serviceWorker), reload: false);
        Assert.That(during.Outcome, Is.EqualTo(Outcome.Failed), "during the outage the app cannot boot\n" + during.Console);
        AssertController(during, serviceWorker, "outage load");
        Assert.That(server.RequestsMatching(prefix), Is.GreaterThanOrEqualTo(1), "the faulted resource was requested");

        server.Outage = false;
        int before = server.RequestsMatching(prefix);
        var after = await LoadAsync(page, server.BaseUrl, reload: true);
        AssertController(after, serviceWorker, "reload after recovery");
        Assert.That(after.Outcome, Is.EqualTo(Outcome.Failed),
            $"BUG NOT REPRODUCED: without the fix the app booted after the host recovered ({prefix} {status}). If Chromium stopped caching " +
            $"this error, the control proves nothing for this case.\n{after.Console}");
        Assert.That(server.RequestsMatching(prefix), Is.EqualTo(before),
            "without the fix the browser serves the cached error and never asks the server again");
    }

    [TestCaseSource(nameof(Faults))]
    public async Task Fix_RecoversOnFirstReloadAfterHostRecovers(string prefix, int status, bool serviceWorker)
    {
        await using var server = await FaultServer.StartAsync(Wwwroot(fix: true, serviceWorker));
        server.FaultFilePrefix = prefix; server.FaultStatus = status; server.Outage = true;
        await using var context = await _browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var during = await LoadAsync(page, server.BaseUrl + PagePath(serviceWorker), reload: false);
        Assert.That(during.Outcome, Is.EqualTo(Outcome.Failed), "during the outage the app cannot boot, fix or not\n" + during.Console);
        AssertController(during, serviceWorker, "outage load");

        server.Outage = false;
        int before = server.RequestsMatching(prefix);
        var after = await LoadAsync(page, server.BaseUrl, reload: true);
        AssertController(after, serviceWorker, "reload after recovery");
        Assert.That(after.Outcome, Is.EqualTo(Outcome.Booted), $"the fix must boot once the host recovered ({prefix} {status})\n{after.Console}");
        Assert.That(after.Recovered, Is.EqualTo(1), "exactly the one faulted resource was re-fetched\n" + after.Console);
        Assert.That(server.RequestsMatching(prefix), Is.EqualTo(before + 1), "one network re-fetch of the faulted resource");

        int healed = server.RequestsMatching(prefix);
        var next = await LoadAsync(page, server.BaseUrl, reload: true);
        Assert.That(next.Outcome, Is.EqualTo(Outcome.Booted), next.Console);
        AssertController(next, serviceWorker, "load after the heal");
        Assert.That(next.Recovered, Is.EqualTo(0), "the cache entry was replaced: nothing to recover any more\n" + next.Console);
        Assert.That(server.RequestsMatching(prefix), Is.EqualTo(healed), "the healed entry is a plain cache hit again");
    }
}
