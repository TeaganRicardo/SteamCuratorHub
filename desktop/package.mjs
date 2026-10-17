import { packager } from '@electron/packager';
import { buildNativeHelper } from './native/build.mjs';
import { resolve } from 'node:path';
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync, existsSync } from 'node:fs';
if (process.platform !== 'darwin' || process.arch !== 'arm64') throw new Error('First MVP package requires macOS arm64');
await buildNativeHelper();
const paths = await packager({ dir: '.', out: 'dist', name: 'Steam Curator Hub', appBundleId: 'com.steamcuratorhub.desktop',
  platform: 'darwin', arch: 'arm64', overwrite: true, prune: true, asar: false,
  ...(existsSync('.local/electron-download/electron-v44.9.0-darwin-arm64.zip') ? { electronZipDir: '.local/electron-download' } : {}),
  appCategoryType: 'public.app-category.productivity', appCopyright: 'Steam Curator Hub contributors',
  ignore: [/^\/\.git(?:\/|$)/, /^\/\.github(?:\/|$)/, /^\/\.local(?:\/|$)/, /^\/\.workbuddy-ai(?:\/|$)/,
    /^\/deliverables(?:\/|$)/, /^\/dist(?:\/|$)/, /^\/artifacts(?:\/|$)/, /^\/reference(?:\/|$)/,
    /^\/tools(?:\/|$)/, /^\/desktop\/test(?:\/|$)/, /^\/desktop\/native\/synthetic-/,
    /\.local\.json$/, /\.DS_Store$/],
});
for (const path of paths) {
  const appPath = resolve(path, 'Steam Curator Hub.app');
  const build = { version: '0.1.0', sourceCommit: execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(),
    sourceTree: execFileSync('git', ['rev-parse', 'HEAD^{tree}'], { encoding: 'utf8' }).trim(),
    sourceDirty: Boolean(execFileSync('git', ['status', '--porcelain'], { encoding: 'utf8' }).trim()), builtAt: new Date().toISOString() };
  writeFileSync(resolve(appPath, 'Contents/Resources/app/build-info.json'), JSON.stringify(build, null, 2));
  execFileSync('codesign', ['--force', '--deep', '--sign', '-', appPath], { stdio: 'inherit' });
  execFileSync('codesign', ['--verify', '--deep', '--strict', appPath], { stdio: 'inherit' });
  mkdirSync('.local', { recursive: true });
  writeFileSync('.local/package.json', JSON.stringify({ path: appPath, arch: 'arm64', signing: 'ad-hoc', notarized: false, ...build }, null, 2));
  console.log(appPath);
}
