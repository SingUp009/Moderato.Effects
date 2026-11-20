import { dotnet } from './_framework/dotnet.js';

const WARMUP_CODE = `using Moderato.Effects;
using Moderato.Results;

Result<string, WarmupError> result = await Effect.Gen(async (Scope<WarmupError, EmptyEnvironment> scope) =>
{
    await Task.Yield();
    return "warm";
}).RunAsync();
Console.WriteLine(result is Ok<string>(var value) ? value : "failed");

public sealed record Missing(string Name);
public sealed record Invalid(string Reason);

[Error]
public partial union WarmupError(Missing, Invalid);
`;

let muted = false;
let latestCompileId = 0;
let compiled = null;
let queue = Promise.resolve();

const ready = (async () => {
  const started = performance.now();
  // The runtime grows its own total as it discovers resources, so count them up front from the boot config.
  let expected = 0;
  postMessage({ type: 'progress', phase: 'download', loaded: 0, total: 1 });
  const { setModuleImports, getAssemblyExports, getConfig } = await dotnet
    .withModuleConfig({
      onConfigLoaded: (config) => {
        for (const [kind, entries] of Object.entries(config.resources ?? {})) {
          if (Array.isArray(entries) && !kind.startsWith('jsModule')) expected += entries.length;
        }
      },
      onDownloadResourceProgress: (loaded, total) =>
        postMessage({ type: 'progress', phase: 'download', loaded, total: Math.max(total, expected) }),
    })
    .create();
  postMessage({ type: 'progress', phase: 'start' });
  setModuleImports('runner', {
    postOutput: (text, isError) => {
      if (!muted) postMessage({ type: 'output', text, isError });
    },
  });
  const exports = await getAssemblyExports(getConfig().mainAssemblyName);
  const runner = exports.Moderato.Effects.WasmRunner.WasmRunner;
  const booted = performance.now();

  const initialize = runner.Initialize();
  postMessage({ type: 'progress', phase: 'warmup' });

  // Pays the first-compile cost while the reader is still on the chapter text.
  muted = true;
  const warmup = JSON.parse(runner.Compile(WARMUP_CODE));
  const warmupExecute = await runner.ExecuteAsync();
  muted = false;

  postMessage({
    type: 'ready',
    timings: {
      boot: booted - started,
      initialize,
      warmupCompile: warmup.timings,
      warmupSuccess: warmup.success,
      warmupDiagnostics: warmup.diagnostics,
      warmupExecute,
    },
  });
  return runner;
})();

function compile(runner, code) {
  const result = JSON.parse(runner.Compile(code));
  compiled = { code, result };
  return result;
}

async function handle(runner, message) {
  switch (message.type) {
    case 'compile': {
      // Only the newest edit matters; older queued compiles would be thrown away anyway.
      if (message.id !== latestCompileId || compiled?.code === message.code) return;
      const result = compile(runner, message.code);
      postMessage({ type: 'compiled', id: message.id, result });
      return;
    }
    case 'run': {
      const hit = compiled?.code === message.code;
      const result = hit ? compiled.result : compile(runner, message.code);
      if (!hit) postMessage({ type: 'compiled', id: message.id, result });
      const execute = result.success ? await runner.ExecuteAsync() : 0;
      postMessage({
        type: 'done',
        id: message.id,
        hit,
        success: result.success,
        compile: hit ? 0 : result.timings.total,
        execute,
      });
      return;
    }
  }
}

onmessage = ({ data }) => {
  if (data.type === 'compile') latestCompileId = data.id;
  queue = queue
    .then(() => ready)
    .then((runner) => handle(runner, data))
    .catch((error) => postMessage({ type: 'error', message: String(error?.stack ?? error) }));
};
