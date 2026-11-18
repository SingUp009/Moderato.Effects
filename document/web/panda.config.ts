import { defineConfig } from '@pandacss/dev';

export default defineConfig({
    presets: ['@pandacss/preset-base', '@pandacss/preset-panda'],
    preflight: false,
    include: ['./src/**/*.{astro,js,jsx,ts,tsx}'],
    outdir: 'styled-system',
});
