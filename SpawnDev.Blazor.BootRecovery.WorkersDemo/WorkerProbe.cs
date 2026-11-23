using Microsoft.JSInterop;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.WebWorkers;

namespace SpawnDev.Blazor.BootRecovery.WorkersDemo;

/// <summary>What the tests call from the page (DotNet.invokeMethodAsync) to start a web worker and talk to it.</summary>
public static class WorkerProbe
{
    /// <summary>The page's services (set in Program.cs).</summary>
    public static IServiceProvider? Services { get; set; }

    /// <summary>Runs IN the worker: proves its .NET runtime booted and reports the scope it runs in.</summary>
    public static string Ping() => "pong from " + BlazorJSRuntime.JS.GlobalScope;

    /// <summary>
    /// Starts a new dedicated web worker, waits for its .NET runtime to boot, and calls <see cref="Ping"/> in it. Returns the reply,
    /// or "timeout" when the worker did not answer within <paramref name="timeoutMs"/> (a worker whose boot failed never becomes
    /// ready), or "error: ..." when starting or calling it threw.
    /// </summary>
    [JSInvokable]
    public static async Task<string> StartWorkerAndPing(int timeoutMs)
    {
        var call = StartAndPing();
        var done = await Task.WhenAny(call, Task.Delay(timeoutMs));
        if (done != call) return "timeout";
        try { return await call; }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    static async Task<string> StartAndPing()
    {
        var workers = Services!.GetRequiredService<WebWorkerService>();
        var worker = await workers.GetWebWorker();
        if (worker == null) return "error: web workers are not supported here";
        await worker.WhenReady;
        return await worker.Run(() => Ping());
    }
}
