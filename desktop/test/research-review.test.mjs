import test from 'node:test';
import assert from 'node:assert/strict';
import { ResearchService, ResearchError } from '../services/research.mjs';

const game = { appId: 4444, name: 'Fixture Quest', language: 'english', requestId: 'independent-review' };
const settings = { apiKey: 'synthetic-ai-secret', searchApiKey: 'synthetic-search-secret', model: 'models/fixture-model', maxSources: 1, maxOutputTokens: 3000 };
const json = value => new Response(JSON.stringify(value), { headers: { 'content-type': 'application/json' } });
const generated = value => json({ candidates: [{ finishReason: 'STOP', content: { parts: [{ text: JSON.stringify(value) }] } }],
  usageMetadata: { promptTokenCount: 17, candidatesTokenCount: 23, thoughtsTokenCount: 11, totalTokenCount: 51 } });
const html = extra => new Response(`<title>Fixture Quest review</title><article>${'Fixture Quest offers exploration with deliberate combat. '.repeat(8)}${extra}</article>`,
  { headers: { 'content-type': 'text/html' } });
function fixture({ steam = () => json({ response: { reviews: [] } }), media = () => html('<a href="https://store.steampowered.com/app/4444/">Steam</a>'),
  ai = () => generated({ summary: 'Evidence [S1]', draft: 'Analysis [S1]' }) } = {}) {
  const calls = [];
  const service = new ResearchService({ fetchImpl: async (url, options) => {
    calls.push({ url, options });
    if (url.startsWith('https://api.steampowered.com/')) return steam(url, options);
    if (url.startsWith('https://api.search.brave.com/')) return json({ web: { results: [{ url: 'https://media.example.com/review' }] } });
    if (url.endsWith(':generateContent')) return ai(url, options);
    return media(url, options);
  } });
  return { service, calls };
}

test('independent review checks production Gemini/Brave routes, bounded token request and credential destinations', async () => {
  const s = fixture(); const result = await s.service.research(game, settings);
  const ai = s.calls.find(call => call.url.endsWith(':generateContent'));
  assert.equal(ai.url, 'https://generativelanguage.googleapis.com/v1beta/models/fixture-model:generateContent');
  const body = JSON.parse(ai.options.body);
  assert.equal(ai.options.method, 'POST'); assert.equal(ai.options.redirect, 'error');
  assert.equal(ai.options.headers['x-goog-api-key'], settings.apiKey);
  assert.equal(body.generationConfig.maxOutputTokens, 3000);
  assert.equal(body.generationConfig.responseMimeType, 'application/json');
  assert.deepEqual(body.generationConfig.responseJsonSchema.required, ['summary', 'draft']);
  assert.deepEqual(body.tools, []);
  const brave = s.calls.find(call => call.url.startsWith('https://api.search.brave.com/'));
  assert.equal(new URL(brave.url).pathname, '/res/v1/web/search');
  assert.equal(brave.options.headers['X-Subscription-Token'], settings.searchApiKey);
  assert.equal(new URL(brave.url).searchParams.get('count'), '2');
  const media = s.calls.find(call => call.url.startsWith('https://media.'));
  assert.equal(media.options.credentials, 'omit'); assert.deepEqual(Object.keys(media.options.headers), ['Accept']);
  const record = result.usage.calls.find(call => call.provider === 'gemini');
  assert.deepEqual([record.inputTokens, record.outputTokens, record.thoughtTokens, record.totalTokens], [17, 23, 11, 51]);
  for (const secret of [settings.apiKey, settings.searchApiKey]) {
    assert.equal(s.calls.some(call => call.url.includes(secret)), false);
    assert.equal(JSON.stringify(result).includes(secret), false);
  }
});

test('malformed Steam JSON or review items preserve independently acquired media research instead of aborting', async () => {
  for (const value of [null, [], { response: null }, { response: { reviews: [null, 1, [], {}] } }]) {
    const s = fixture({ steam: () => json(value) });
    const result = await s.service.research(game, settings);
    assert.equal(result.reviews.length, 0); assert.equal(result.sources[0].kind, 'media');
    assert.ok(result.warnings.some(warning => warning.startsWith('steam_reviews_')));
    assert.equal(result.draft, 'Analysis [S1]');
  }
});

test('null or non-object structured Gemini output exposes a sanitized ResearchError instead of a raw TypeError', async () => {
  for (const value of [null, [], 'not an object', 42, true]) {
    const s = fixture({ ai: () => generated(value) });
    await assert.rejects(s.service.translate({ text: 'Original', language: 'english' }, settings),
      error => error instanceof ResearchError && error.code === 'invalid_response');
  }
});

test('an AppID-looking substring on a forged host cannot override an explicit different Steam AppID', async () => {
  const s = fixture({ media: () => html('<a href="https://notstore.steampowered.com/app/4444/">Fake</a><a href="https://store.steampowered.com/app/5555/">Actual Steam target</a>') });
  const result = await s.service.research(game, { ...settings, apiKey: '' });
  assert.equal(result.sources[0].identity.status, 'mismatch'); assert.equal(result.sources[0].identity.eligible, false);
});

test('hidden AppID text in comments does not count as independent Steam identity evidence', async () => {
  const s = fixture({ media: () => html('<!-- https://store.steampowered.com/app/4444/ -->') });
  const result = await s.service.research(game, { ...settings, apiKey: '' });
  assert.equal(result.sources[0].identity.status, 'name_match');
  assert.ok(result.warnings.includes('media_edition_unverified:S1'));
});

test('script/template text, unrelated attributes and embedded or credentialed Steam URLs never upgrade identity', async () => {
  const anchor = '<a href="https://store.steampowered.com/app/4444/">Steam</a>';
  const cases = [
    `<script>const example='${anchor}';</script>`, `<script>const example='${anchor}';`,
    `<textarea>${anchor}</textarea>`, `<template><template>hidden</template>${anchor}</template>`,
    `<div data-example='${anchor}'></div>`, '<a data-href="https://store.steampowered.com/app/4444/">Data only</a>',
    '<a title=\'href="https://store.steampowered.com/app/4444/"\' href="https://other.example.com/">Example</a>',
    '<a href="https://store.steampowered.com.evil.example.com/app/4444/">Forged host</a>',
    '<a href="https://other.example.com/?target=https://store.steampowered.com/app/4444/">Embedded query</a>',
    '<a href="https://store.steampowered.com/app/4444suffix">Wrong path</a>',
    '<a href="https://user:password@store.steampowered.com/app/4444/">Credentials</a>'
  ];
  for (const extra of cases) {
    const s = fixture({ media: () => html(extra) });
    const result = await s.service.research(game, { ...settings, apiKey: '' });
    assert.equal(result.sources[0].identity.status, 'name_match', extra);
  }
});

test('real Steam anchor href values support entities, unquoted attributes and explicit Steam run links', async () => {
  for (const href of ['href="https://store.steampowered.com/app/4444/?l=english&amp;utm_source=fixture"',
    'href=https://steamcommunity.com/app/4444/', 'href="steam://run/4444"']) {
    const s = fixture({ media: () => html(`<a ${href}>Steam</a>`) });
    const result = await s.service.research(game, { ...settings, apiKey: '' });
    assert.equal(result.sources[0].identity.status, 'matched');
  }
});

test('cancelling while media transport ignores abort prevents a late response from reaching paid generation', async () => {
  let resolveMedia; let mediaStarted;
  const started = new Promise(resolve => { mediaStarted = resolve; });
  const s = fixture({ media: () => { mediaStarted(); return new Promise(resolve => { resolveMedia = resolve; }); } });
  const controller = new AbortController(); const pending = s.service.research(game, settings, controller.signal);
  await started; controller.abort(new Error('synthetic private cancellation'));
  await assert.rejects(pending, error => error.code === 'cancelled' && !error.message.includes('private'));
  resolveMedia(html('<a href="https://store.steampowered.com/app/4444/">Steam</a>'));
  await new Promise(setImmediate);
  assert.equal(s.calls.some(call => call.url.endsWith(':generateContent')), false);
});

test('Gemini streamed body stalled after headers is cancelled within the API request deadline', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const service = new ResearchService({ fetchImpl: async () => new Response(new ReadableStream({ pull() { return new Promise(() => {}); } })) });
  const pending = service.translate({ text: 'Original' }, settings);
  const rejected = assert.rejects(pending, { code: 'timeout' });
  await new Promise(setImmediate); t.mock.timers.tick(60001); await rejected;
});
