using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SpawnDev.Blazor.BootRecovery.WorkersDemo;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.WebWorkers;

// The same Program runs in the page AND in every web worker (SpawnDev.BlazorJS.WebWorkers boots the app again inside the worker).
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Services.AddBlazorJSRuntime();
builder.Services.AddWebWorkerService();
if (BlazorJSRuntime.JS.IsWindow) builder.RootComponents.Add<Home>("#app");
var host = builder.Build();
WorkerProbe.Services = host.Services;
await host.BlazorJSRunAsync();
