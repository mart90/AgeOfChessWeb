// `npm run build:engine`: builds the Rust engine (../engine) for the browser, twice:
//   goldrush_engine.wasm       — baseline WebAssembly (every browser)
//   goldrush_engine_simd.wasm  — with SIMD (simd128); ~2.5x faster with the evaluation network
// The worker picks the SIMD build when the browser supports it (lib/engine/engineWasm.js).
// Optional argument: a .ggnn network to ship as lib/engine/network.ggnn.
import { execFileSync } from 'node:child_process';
import { copyFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const engineDir = path.resolve(root, '../engine');
const outDir = path.join(root, 'src/lib/engine');

function build(targetDir, rustflags) {
  execFileSync('cargo', ['build', '--release', '--lib', '--target', 'wasm32-unknown-unknown', '--target-dir', targetDir], {
    cwd: engineDir,
    stdio: 'inherit',
    env: { ...process.env, RUSTFLAGS: rustflags },
  });
  return path.join(engineDir, targetDir, 'wasm32-unknown-unknown/release/goldrush_engine.wasm');
}

copyFileSync(build('target', ''), path.join(outDir, 'goldrush_engine.wasm'));
copyFileSync(build('target/simd', '-C target-feature=+simd128'), path.join(outDir, 'goldrush_engine_simd.wasm'));
if (process.argv[2]) copyFileSync(path.resolve(process.argv[2]), path.join(outDir, 'network.ggnn'));
console.log('engine built' + (process.argv[2] ? ` (network: ${process.argv[2]})` : ''));
