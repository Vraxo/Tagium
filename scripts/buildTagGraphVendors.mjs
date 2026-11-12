import { build } from 'esbuild';

await build({
    entryPoints: ['scripts/tagGraphVendors.js'],
    bundle: true,
    format: 'iife',
    platform: 'browser',
    target: ['es2020'],
    minify: true,
    legalComments: 'linked',
    outfile: 'Tagium.Photino.Blazor/wwwroot/js/tagGraphVendors.js'
});
