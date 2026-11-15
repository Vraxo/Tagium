// Rebuilds wwwroot/js/tagGraphVendors.js, the PixiJS + d3-force bundle used by the tag graph.
// Only needed to change a library version: edit `packages` below, then run
//     node Tagium.Photino.Blazor/build-tag-graph-vendors.mjs
// and commit the regenerated bundle. Requires Node.js with npm.
import { execSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const packages = ['pixi.js@7.4.3', 'd3-force@3.0.0', 'esbuild@0.28.2'];
const outfile = fileURLToPath(new URL('./wwwroot/js/tagGraphVendors.js', import.meta.url));
const entry = `
    import * as d3Force from 'd3-force';
    import * as PIXI from 'pixi.js';
    window.tagGraphVendors = { d3Force, PIXI };
`;

const workDir = mkdtempSync(join(tmpdir(), 'tag-graph-vendors-'));
try {
    execSync(`npm install --no-save --no-package-lock --no-audit --no-fund ${packages.join(' ')}`, { cwd: workDir, stdio: 'inherit' });
    const { build } = await import(pathToFileURL(join(workDir, 'node_modules', 'esbuild', 'lib', 'main.js')));
    await build({
        stdin: { contents: entry, resolveDir: workDir },
        bundle: true,
        format: 'iife',
        platform: 'browser',
        target: ['es2020'],
        minify: true,
        legalComments: 'linked',
        outfile
    });
    console.log(`Wrote ${outfile}`);
} finally {
    rmSync(workDir, { recursive: true, force: true });
}
