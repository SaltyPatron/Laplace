import { build } from 'esbuild';
import { fileURLToPath } from 'node:url';

// Bundle the actual production dependency graph. A transpiled data URL alone
// cannot resolve imports when a production module gains another dependency.
export async function loadSourceModule(url) {
  const result = await build({
    entryPoints: [fileURLToPath(url)], bundle: true, write: false,
    format: 'esm', platform: 'browser', target: 'es2022', logLevel: 'silent',
  });
  return import(`data:text/javascript;base64,${Buffer.from(result.outputFiles[0].contents).toString('base64')}`);
}
