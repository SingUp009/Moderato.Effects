const SAMPLE = `using Moderato.Effects;
using Moderato.Results;

foreach (var quantity in new[] { 2, 0 })
{
    Result<string, OrderError> result = await PrepareOrder(quantity).RunAsync();

    if (result is Ok<string>(var message))
        Console.WriteLine(message);
    else if (result is Err<OrderError>(var error))
        Console.WriteLine($"Invalid order: {error}");
}

static Effect<string, OrderError, EmptyEnvironment> PrepareOrder(int quantity) =>
    Effect.Gen(async (Scope<OrderError, EmptyEnvironment> scope) =>
    {
        var accepted = scope.Get(ValidateQuantity(quantity));
        await Console.Out.WriteLineAsync("Preparing the order.");
        return $"Order ready (quantity: {accepted}).";
    });

static Result<int, OrderError> ValidateQuantity(int quantity) =>
    quantity > 0 ? new Ok<int>(quantity) : new Err<OrderError>(new InvalidQuantity(quantity));

public sealed record InvalidQuantity(int Quantity);
public sealed record OutOfStock(string Item);

[Error]
public partial union OrderError(InvalidQuantity, OutOfStock);
`;

const DEBOUNCE_MS = 300;

const $ = (id) => document.getElementById(id);
const editor = $('editor');
const output = $('output');
const diagnostics = $('diagnostics');
const status = $('status');
const metrics = $('metrics').tBodies[0];

editor.value = SAMPLE;

let worker;
let nextId = 1;
let debounce;
const pending = new Map();

function metric(name, ...values) {
  const row = metrics.insertRow();
  row.insertCell().textContent = name;
  for (const value of values) {
    row.insertCell().textContent = typeof value === 'number' ? value.toFixed(1) : String(value);
  }
}

function showDiagnostics(result) {
  diagnostics.textContent = result.diagnostics
    .map((d) => `${d.path}(${d.line},${d.column}): ${d.severity} ${d.id}: ${d.message}`)
    .join('\n');
  diagnostics.className = result.success ? '' : 'err';
}

function startWorker() {
  const started = performance.now();
  status.textContent = 'booting…';
  worker = new Worker('./worker.js', { type: 'module' });
  worker.onmessage = ({ data }) => {
    switch (data.type) {
      case 'ready': {
        const t = data.timings;
        status.textContent = 'ready';
        metric('A: page → ready (ms since navigation)', performance.now());
        metric('A: worker start → ready', performance.now() - started);
        metric('  runtime boot', t.boot);
        metric('  Initialize (refs + driver)', t.initialize);
        metric('B: warm-up compile total / parse / generate / emit',
          t.warmupCompile.total, t.warmupCompile.parse, t.warmupCompile.generate, t.warmupCompile.emit);
        metric('B: warm-up execute', t.warmupExecute);
        if (!t.warmupSuccess) {
          diagnostics.className = 'err';
          diagnostics.textContent = 'Warm-up failed:\n' + JSON.stringify(t.warmupDiagnostics, null, 2);
        } else {
          scheduleCompile();
        }
        break;
      }
      case 'output': {
        const span = document.createElement('span');
        span.textContent = data.text;
        if (data.isError) span.className = 'err';
        output.append(span);
        break;
      }
      case 'compiled':
      case 'done': {
        if (data.type === 'compiled') showDiagnostics(data.result);
        const waiter = pending.get(data.id);
        if (waiter?.until === data.type) {
          pending.delete(data.id);
          waiter.resolve(data);
        }
        break;
      }
      case 'error':
        diagnostics.className = 'err';
        diagnostics.textContent = data.message;
        break;
    }
  };
}

function scheduleCompile() {
  clearTimeout(debounce);
  debounce = setTimeout(() => {
    const id = nextId++;
    worker.postMessage({ type: 'compile', id, code: editor.value });
  }, DEBOUNCE_MS);
}

async function run() {
  clearTimeout(debounce);
  output.textContent = '';
  const clicked = performance.now();
  const done = await runOnce(editor.value);
  metric(`C: Run (${done.hit ? 'hit' : 'miss'}) click→done / compile / execute`,
    performance.now() - clicked, done.compile, done.execute);
}

function send(type, code, until) {
  const id = nextId++;
  return new Promise((resolve) => {
    pending.set(id, { until, resolve });
    worker.postMessage({ type, id, code });
  });
}

const runOnce = (code) => send('run', code, 'done');
const compileOnce = (code) => send('compile', code, 'compiled');

function stats(values) {
  const sorted = [...values].sort((a, b) => a - b);
  const at = (q) => sorted[Math.min(sorted.length - 1, Math.floor(q * sorted.length))];
  return [at(0.5), at(0.95)];
}

async function bench() {
  const ITERATIONS = 20;
  const compiles = [];
  const executes = [];
  const misses = [];
  for (let i = 0; i < ITERATIONS; i++) {
    // A changed comment defeats the "same code" shortcut, like a real edit would.
    const edited = `// edit ${Date.now()}-${i}\n${editor.value}`;
    const result = await compileOnce(edited);
    compiles.push(result.result.timings.total);
    output.textContent = '';
    const done = await runOnce(edited);
    executes.push(done.execute);
    output.textContent = '';
    const clicked = performance.now();
    await runOnce(`// miss ${Date.now()}-${i}\n${editor.value}`);
    misses.push(performance.now() - clicked);
  }
  metric('C: compile median / p95', ...stats(compiles));
  metric('C: execute median / p95', ...stats(executes));
  metric('C: Run without pre-compile, click→done median / p95', ...stats(misses));
}

editor.addEventListener('input', scheduleCompile);
editor.addEventListener('keydown', (event) => {
  if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
    event.preventDefault();
    run();
  }
});
$('run').addEventListener('click', run);
$('bench').addEventListener('click', bench);
$('stop').addEventListener('click', () => {
  worker.terminate();
  pending.clear();
  startWorker();
});

startWorker();
