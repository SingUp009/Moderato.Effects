import { execFileSync } from 'node:child_process';
import { cpSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const web = fileURLToPath(new URL('..', import.meta.url));
const project = join(web, '..', 'tour', 'wasm-runner');
const target = join(web, 'public', 'runner');
const published = mkdtempSync(join(tmpdir(), 'runner-'));

try {
  execFileSync('dotnet', ['publish', project, '-c', 'Release', '-o', published], { stdio: 'inherit' });

  rmSync(target, { recursive: true, force: true });
  const wwwroot = join(published, 'wwwroot');
  // Cloudflare compresses on the fly, so the precompressed copies would only add files.
  cpSync(join(wwwroot, '_framework'), join(target, '_framework'), {
    recursive: true,
    filter: (source) => !/\.(br|gz)$/.test(source),
  });
} finally {
  rmSync(published, { recursive: true, force: true });
}
