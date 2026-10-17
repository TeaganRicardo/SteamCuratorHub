#!/usr/bin/env node
import { readFileSync, mkdirSync, openSync, closeSync, writeFileSync, renameSync, unlinkSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { createInterface } from 'node:readline/promises';
import { Writable } from 'node:stream';
import os from 'node:os';
import { createReport, runProbe, validatePlan, ProbeError } from './probe.mjs';
import { createFixture, fixturePlan } from './fixture.mjs';

function usage() {
  console.log('Offline: node tools/steam-validation/cli.mjs --fixture [--output report.local.json]');
  console.log('Operator only: node tools/steam-validation/cli.mjs --live --plan plan.local.json [--output report.local.json]');
}

function parseArgs(args) {
  const options = {};
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (['--fixture', '--live', '--help'].includes(arg)) {
      if (options[arg]) throw new ProbeError('invalid_arguments');
      options[arg] = true;
    } else if (['--plan', '--output'].includes(arg) && args[i + 1] && !args[i + 1].startsWith('--')) {
      if (options[arg]) throw new ProbeError('invalid_arguments');
      options[arg] = args[++i];
    } else throw new ProbeError('invalid_arguments');
  }
  if (options['--help']) return options;
  if (Boolean(options['--fixture']) === Boolean(options['--live']) ||
      (options['--live'] && !options['--plan']) || (options['--fixture'] && options['--plan'])) {
    throw new ProbeError('invalid_arguments');
  }
  return options;
}

async function ask(label, signal, hidden = false) {
  if (!process.stdin.isTTY) throw new ProbeError('interactive_terminal_required');
  signal.throwIfAborted();
  process.stderr.write(label);
  const muted = new Writable({ write(_chunk, _encoding, callback) { callback(); } });
  const rl = createInterface({ input: process.stdin, output: hidden ? muted : process.stderr, terminal: true });
  const onSigint = () => process.emit('SIGINT');
  rl.on('SIGINT', onSigint);
  try {
    const value = await rl.question('', { signal });
    return hidden ? value : value.trim();
  }
  finally { rl.close(); if (hidden) process.stderr.write('\n'); }
}

function reportWriter(path) {
  mkdirSync(dirname(path), { recursive: true, mode: 0o700 });
  closeSync(openSync(path, 'wx', 0o600)); // Refuse overwriting any existing report.
  return report => {
    const temp = `${path}.${randomUUID()}.tmp`;
    try {
      writeFileSync(temp, JSON.stringify(report, null, 2) + '\n', { mode: 0o600, flag: 'wx' });
      renameSync(temp, path);
    } finally {
      try { unlinkSync(temp); } catch { /* Already renamed, or creation failed. */ }
    }
  };
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options['--help']) { usage(); return; }
  const mode = options['--fixture'] ? 'fixture' : 'live';
  const plan = validatePlan(mode === 'fixture' ? fixturePlan : JSON.parse(readFileSync(options['--plan'], 'utf8')));
  const output = resolve(options['--output'] ?? `.local/steam-validation/${randomUUID()}.json`);
  const save = reportWriter(output);
  const repoRoot = fileURLToPath(new URL('../../', import.meta.url));
  let sourceHead = 'unavailable';
  let sourceTree = 'unavailable';
  try {
    sourceHead = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repoRoot, encoding: 'utf8' }).trim();
    sourceTree = execFileSync('git', ['status', '--porcelain', '--untracked-files=normal'],
      { cwd: repoRoot, encoding: 'utf8' }).trim() ? 'dirty' : 'clean';
  } catch {}
  const report = createReport(plan, mode, { platform: process.platform, arch: process.arch,
    osRelease: os.release(), node: process.version, sourceHead, sourceTree, steamUser: mode === 'live' ? '5.3.0' : 'not_loaded' });
  save(report);
  const controller = new AbortController();
  const cancel = () => controller.abort(new ProbeError('operator_cancelled'));
  process.once('SIGINT', cancel);
  process.once('SIGTERM', cancel);
  try {
    let transport;
    let timing = {};
    if (mode === 'fixture') {
      const fixture = createFixture();
      transport = fixture.transport;
      timing = { now: fixture.now, wait: fixture.wait };
    } else {
      console.error('Live probe: sends a two-AppID playing request; does not launch games.');
      console.error('Target installation/platform flags are operator assertions. Steam readback remains required.');
      console.error('Credentials and Steam Guard stay in memory; no token/session storage.');
      const { createLiveTransport } = await import('./live.mjs');
      transport = await createLiveTransport({ plan, signal: controller.signal,
        askSecret: (label, signal) => ask(label, signal, true), askAccount: ask,
        onFault: error => controller.abort(error) });
    }
    await runProbe({ plan, transport, report, signal: controller.signal, save, ...timing });
  } catch (error) {
    report.state = 'interrupted';
    report.failure = error instanceof ProbeError ? error.code : 'probe_failed';
  } finally {
    process.off('SIGINT', cancel);
    process.off('SIGTERM', cancel);
    save(report);
  }
  console.log(JSON.stringify({ mode, state: report.state, report: output,
    acceptance: report.acceptance, observations: report.observations }, null, 2));
  if (report.state !== 'collected' || report.cleanup !== 'logged_off') process.exitCode = 1;
}

main().catch(error => {
  // Never print raw upstream errors or login input.
  console.error(error instanceof ProbeError ? error.code : 'validation_command_failed');
  usage();
  process.exitCode = 1;
});
