# SpawnDev.Blazor.BootRecovery

[![NuGet](https://img.shields.io/nuget/v/SpawnDev.Blazor.BootRecovery.svg?label=SpawnDev.Blazor.BootRecovery)](https://www.nuget.org/packages/SpawnDev.Blazor.BootRecovery)
[![Tests](https://github.com/LostBeard/SpawnDev.Blazor.BootRecovery/actions/workflows/tests.yml/badge.svg)](https://github.com/LostBeard/SpawnDev.Blazor.BootRecovery/actions/workflows/tests.yml)

**A temporary fix for [dotnet/runtime#135090](https://github.com/dotnet/runtime/issues/135090): a Blazor WebAssembly app whose browser cached an
error for one of its boot files never starts again, not even after a hard reload.** Add the package and the app recovers on the next load.
No code changes.

```
dotnet add package SpawnDev.Blazor.BootRecovery
```

[Demo](https://lostbeard.github.io/SpawnDev.Blazor.BootRecovery/)

## The bug

The .NET WebAssembly loader fetches every fingerprinted boot file (the assemblies, `dotnet.native.wasm`, ICU data) with
`fetch(url, { cache: "force-cache", integrity })`. `force-cache` means "use whatever the HTTP cache has for this URL, however old, without
asking the server".

If the host ever answered one of those URLs with an error that carries cache headers (a GitHub Pages `503` during an outage or deploy, a
`404` while files are being uploaded, an empty `200`), the browser stores it. From then on, every load of the app gets that stored error,
the integrity check rejects it, and the app fails to start:

```
download '.../_framework/System.Reflection.Emit.Lightweight.<fingerprint>.wasm' ... failed 0 TypeError: Failed to fetch
```

- The server is fine again. The browser never asks it.
- A **hard reload does not help**: the loader's explicit `cache: "force-cache"` wins over the reload's cache bypass.
- Blazor starts the runtime with `enableDownloadRetry: false`, so nothing retries.
- "Clear site data" works, but on a `*.github.io` site every repository shares one origin, so it wipes every app's storage on that origin.

A visitor without developer tools is stuck until the browser evicts the entry. We hit this in production on 2026-11-21.

## The fix

The package contains one Blazor JS initializer (`SpawnDev.Blazor.BootRecovery.lib.module.js`). Blazor loads JS initializers of referenced
packages automatically, before the runtime downloads its boot files. The initializer wraps `fetch` so that:

- a request sent with `cache: "force-cache"` that **fails** (rejected, for example by the integrity check, or answered with a non-2xx
  status) is sent **once** more with `cache: "reload"`;
- everything else about the request is kept, **integrity included**, so the file is still verified against its hash;
- `cache: "reload"` goes to the network and **replaces** the bad cache entry, so the load after that is a normal cache hit;
- a successful response is returned untouched, so a healthy cached start is exactly as fast as before (tested: no extra requests);
- if the retry fails too (the server really is down), its result is returned and the app fails exactly as it would have.

When a file is recovered, the console shows:

```
[SpawnDev.Blazor.BootRecovery] recovered '.../System.Private.CoreLib.<fingerprint>.wasm' (cached response failed: Failed to fetch); re-fetched with cache: "reload" (dotnet/runtime#135090).
```

Only requests that explicitly ask for `force-cache` are affected; every other `fetch` goes straight through.

## Tests

`SpawnDev.Blazor.BootRecovery.Tests` runs the demo app in a real Chromium with its real HTTP cache, against a local server that behaves
like GitHub Pages (`Cache-Control: max-age=600` on every response, errors included). During an "outage" the server answers one boot file
with a `503` or `404`; then the outage ends and the visitor reloads.

| Case | Without the package (control) | With the package |
|---|---|---|
| `System.Private.CoreLib.wasm` 503 / 404 | stays broken, never asks the server again | boots on the first reload (one re-fetch), next load is a plain cache hit |
| `dotnet.native.wasm` 503 / 404 | stays broken, never asks the server again | boots on the first reload (one re-fetch), next load is a plain cache hit |
| healthy start | | boots, no recovery, a reload adds no requests |

The control is the same demo app published without the package (`-p:BootRecovery=false`), so the tests reproduce the bug itself, not
only the fix.

```
dotnet test SpawnDev.Blazor.BootRecovery.Tests -c Release
```

## Scope and limits

- Tested with .NET 10 standalone Blazor WebAssembly in Chromium. The package targets net8.0, net9.0 and net10.0; the initializer only acts on
  `force-cache` requests, so it is inert where the loader does not use them.
- The JavaScript modules the runtime imports (`dotnet.js`, `dotnet.native.js`, `dotnet.runtime.js`) are loaded with `import()`, not
  `fetch`. They follow normal HTTP caching (an error expires with its `max-age`), so they are not pinned the same way and are not covered.
- Blazor Web Apps (interactive WebAssembly) load JS initializers the same way, but this package has not been tested there yet.

## Removal

This is a **temporary** workaround. Remove the package once the .NET runtime retries failed boot resources with `cache: "reload"` itself
([dotnet/runtime#135090](https://github.com/dotnet/runtime/issues/135090)). The tracking issue in this repository stays open until then.

## License

MIT
