// SpawnDev.Blazor.BootRecovery
//
// WORKAROUND: dotnet/runtime#135090 (https://github.com/dotnet/runtime/issues/135090). TEMPORARY: remove this package once the
// .NET runtime retries failed boot resources with cache: "reload" itself.
//
// THE BUG: the .NET WebAssembly loader fetches every fingerprinted boot resource (assemblies, dotnet.native.wasm, ICU, pdb) with
// fetch(url, { cache: "force-cache", integrity }). If the browser's HTTP cache ever stored an error for one of those URLs (a 503 from
// the host during a deploy or outage, a 404, an empty 200), "force-cache" returns that stored response on every later load, without
// asking the server again. The integrity check rejects it, the app fails to boot, and it keeps failing after the server is healthy:
// a hard reload does not help (the explicit cache mode wins over the reload's cache bypass), and Blazor sets
// enableDownloadRetry: false, so nothing retries. A visitor is stuck until the browser evicts the entry.
//
// THE FIX: while this initializer is loaded, a fetch that was sent with cache: "force-cache" and failed (rejected, e.g. the
// integrity check, or answered with a non-2xx status) is sent ONCE more with cache: "reload". Everything else the runtime chose
// (integrity, credentials, headers) is kept, so a resource is still verified against its hash. "reload" goes to the network and
// replaces the bad cache entry, so the next load is a normal cache hit again. A healthy cached start does not change at all: a
// successful force-cache response is returned untouched. If the retry fails too (the server really is down) its result is
// returned and the app fails exactly as it would have.
//
// HOW IT LOADS: Blazor imports every referenced package's <AssemblyName>.lib.module.js ("JS initializer") from onConfigLoaded,
// before the runtime starts downloading its boot resources, and the runtime calls globalThis.fetch at call time. So referencing
// this package is enough: no Program.cs or index.html change. The wrapper is installed when this module is evaluated, and the
// initializer hooks below only make sure of it.

const STATE_KEY = Symbol.for('SpawnDev.Blazor.BootRecovery');

function urlOf(input) {
    if (typeof input === 'string') return input;
    if (input && typeof input.url === 'string') return input.url;   // Request
    return String(input);                                            // URL or anything with toString()
}

// ⚠️ The installer is a plain, NON-exported function declaration, and nothing in this module calls an exported function by name.
// SpawnDev.BlazorJS.WebWorkers (when a worker cannot use dynamic import, or the app has an import map, i.e. every .NET 10 template app)
// rewrites this module's source: `export function X()` becomes `_exportsOverride.X = function X()`, a function EXPRESSION, which
// leaves no `X` binding in the module scope. 1.0.0 called its exported installBootRecovery() at the top level, so in such a worker the
// module threw "ReferenceError: installBootRecovery is not defined" and the worker failed to boot (found by Data, 2026-11-22). A
// plain declaration is not rewritten and stays hoisted, so this works with that patcher and without it.
function install() {
    const g = globalThis;
    if (g[STATE_KEY]) return g[STATE_KEY];
    const originalFetch = g.fetch;
    if (typeof originalFetch !== 'function') return null;
    const state = { originalFetch, recovered: [] };

    const recoveringFetch = async function (input, init) {
        // Only requests that explicitly asked for "force-cache" can be pinned to a stored error. Everything else goes straight
        // through, unchanged (same arguments, same this-binding as a plain fetch call).
        if (!init || init.cache !== 'force-cache') return originalFetch.call(g, input, init);
        let reason;
        try {
            const response = await originalFetch.call(g, input, init);
            if (response.ok) return response;
            reason = `HTTP ${response.status}`;
        } catch (e) {
            reason = (e && e.message) || String(e);   // integrity mismatch / network failure: TypeError "Failed to fetch"
        }
        const url = urlOf(input);
        // One retry, from the network, keeping every other option (integrity included). Its outcome is final either way.
        const retried = await originalFetch.call(g, input, { ...init, cache: 'reload' });
        state.recovered.push({ url, reason, status: retried.status });
        if (retried.ok)
            console.warn(`[SpawnDev.Blazor.BootRecovery] recovered '${url}' (cached response failed: ${reason}); re-fetched with cache: "reload" (dotnet/runtime#135090).`);
        return retried;
    };
    g.fetch = recoveringFetch;
    g[STATE_KEY] = state;
    return state;
}

/**
 * Installs the recovering fetch wrapper on globalThis once (idempotent, also across copies of this module).
 * @returns {{ originalFetch: Function, recovered: Array<{url: string, reason: string, status: number}> } | null}
 */
export function installBootRecovery() { return install(); }

// Install as soon as Blazor imports this module.
install();

// Blazor initializer hooks. Blazor calls the WebAssembly-specific hook when present, the classic one otherwise; both only ensure
// the wrapper is installed (for example if something replaced fetch between the import and the start).
export function beforeWebAssemblyStart() { install(); }
export function beforeStart() { install(); }
