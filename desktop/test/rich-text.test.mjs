import test from 'node:test';
import assert from 'node:assert/strict';
import { parseHTML } from 'linkedom';
import { createRichEditor, renderRichText, sanitizeRichHTML, serializeRichSelection, serializeRichText } from '../renderer/rich-text.mjs';

function surface() {
  const { document } = parseHTML('<html><body><div id="body"></div></body></html>');
  return { document, root: document.querySelector('#body') };
}

test('legacy long reviews retain every supported formatting tag when opened and saved', () => {
  const { root } = surface();
  const source = '[h1]Title[/h1]\n[h2]Section[/h2]\n[h3]Detail[/h3]\n[b]bold [i]nested[/i][/b] [u]underline[/u] [s]strike[/s]\n'
    + '[list]\n[*]one\n[*][b]two[/b]\n[/list]\n[olist][*]first[*]second[/olist]\n'
    + '[quote]quoted [spoiler]hidden[/spoiler][/quote]\n[code]if (x < 2) { print("[b]literal[/b]"); }\nnext[/code]\n'
    + '[table][tr][th]Name[/th][th]Value[/th][/tr][tr][td]A[/td][td]1[/td][/tr][/table]\n'
    + '[url=https://example.test/review?a=1&b=2]source[/url][hr]\n';
  renderRichText(source, root);
  assert.equal(serializeRichText(root), source.replace('[s]strike[/s]', '[strike]strike[/strike]'));
  assert.equal(root.querySelector('pre').textContent, 'if (x < 2) { print("[b]literal[/b]"); }\nnext');
  assert.equal(root.querySelector('a').getAttribute('href'), 'https://example.test/review?a=1&b=2');
  assert.equal(root.querySelector('[data-rich-type="spoiler"]').textContent, 'hidden');
});

test('plain drafts and native paragraph edits do not acquire gratuitous ending newlines', () => {
  const { root } = surface();
  for (const value of ['', 'plain author text', 'one\ntwo', 'one\n\ntwo\n', 'unknown [tag]kept[/tag]']) {
    renderRichText(value, root);
    assert.equal(serializeRichText(root), value);
  }
  root.innerHTML = '<p>one</p><p>two</p>';
  assert.equal(serializeRichText(root), 'one\ntwo');
  root.innerHTML = 'one<div><br></div><div><br></div>';
  assert.equal(serializeRichText(root), 'one\n\n');
  root.innerHTML = '<div><br></div><div>second line</div>';
  assert.equal(serializeRichText(root), '\nsecond line');
});

test('formatted HTML paste preserves semantics while removing active content and arbitrary attributes', () => {
  const { document, root } = surface();
  root.innerHTML = sanitizeRichHTML('<p onclick="evil()">Author <b style="color:red">bold</b> <i>italic</i></p>'
    + '<script>secret()</script><style>body{display:none}</style><iframe src="https://evil.test">embedded</iframe>'
    + '<svg><script>svg()</script></svg><img src="https://evil.test/pixel" onerror="evil()">'
    + '<span style="font-weight:700;font-style:italic;background:url(https://evil.test/pixel)">styled</span>'
    + '<a href="https://safe.test/path?q=1" onclick="evil()" target="_blank">safe</a>'
    + '<a href="javascript:alert(1)">script link</a><a href="https://user:password@evil.test">credential link</a>', document);
  assert.equal(root.querySelectorAll('script,style,iframe,svg,img').length, 0);
  assert.equal(root.querySelectorAll('a').length, 1);
  assert.equal(root.querySelectorAll('[onclick],[style],[target]').length, 0);
  const serialized = serializeRichText(root);
  assert.match(serialized, /Author \[b\]bold\[\/b\] \[i\]italic\[\/i\]/);
  assert.match(serialized, /\[b\]\[i\]styled\[\/i\]\[\/b\]/);
  assert.match(serialized, /\[url=https:\/\/safe\.test\/path\?q=1\]safe\[\/url\]/);
  assert.ok(serialized.endsWith('script linkcredential link'));
  assert.doesNotMatch(serialized, /secret|embedded|svg\(\)|password|javascript:/);
});

test('unsafe legacy links remain inert and safe bare links normalize to a durable destination', () => {
  const { root } = surface();
  const unsafe = '[url=javascript:alert(1)]unsafe[/url]';
  renderRichText(unsafe, root);
  assert.equal(root.querySelector('a'), null);
  const encoded = serializeRichText(root);
  renderRichText(encoded, root);
  assert.equal(root.textContent, unsafe);
  assert.equal(root.querySelector('a'), null);
  renderRichText('[url]https://example.test/path[/url]', root);
  assert.equal(serializeRichText(root), '[url=https://example.test/path]https://example.test/path[/url]');
});

test('literal formatting syntax remains visible after saving and reopening author text', () => {
  const { root } = surface();
  for (const value of ['Explain [b]literal[/b] notation', '[h2]Heading[/h2] [*] [hr]', '[noparse]already literal[/noparse]', 'A [/NOPARSE] B']) {
    root.textContent = value;
    const serialized = serializeRichText(root);
    assert.match(serialized, /\[noparse\]/);
    renderRichText(serialized, root);
    assert.equal(root.textContent, value);
    assert.equal(root.querySelector('strong,h2,hr,li'), null);
    assert.equal(serializeRichText(root), serialized);
  }
  root.innerHTML = '[<span>b</span>]literal[<span>/b</span>]';
  const acrossNodes = serializeRichText(root);
  renderRichText(acrossNodes, root);
  assert.equal(root.textContent, '[b]literal[/b]');
  assert.equal(root.querySelector('strong'), null);
});

test('code copied from a pre/code block does not acquire nested BBCode delimiters', () => {
  const { root, document } = surface();
  root.innerHTML = sanitizeRichHTML('<pre><code>const x=1;\n[b]literal[/b]</code></pre>', document);
  assert.equal(root.querySelectorAll('pre').length, 1);
  const saved = serializeRichText(root);
  assert.equal(saved, '[code]const x=1;\n[b]literal[/b][/code]');
  renderRichText(saved, root);
  assert.equal(root.querySelector('pre').textContent, 'const x=1;\n[b]literal[/b]');
  root.querySelector('pre').textContent = 'literal [/code] closing syntax';
  const literalClosing = serializeRichText(root);
  renderRichText(literalClosing, root);
  assert.equal(root.textContent, 'literal [/code] closing syntax');
});

test('copying selected nested text retains its inline formatting and safe HTML round-trip', () => {
  const { root, document } = surface();
  renderRichText('[b]prefix [i]selected[/i] suffix[/b]', root);
  const text = root.querySelector('em').firstChild;
  const range = { commonAncestorContainer: text, cloneRange() { return this; }, cloneContents() { const f = document.createDocumentFragment(); f.append(document.createTextNode('selected')); return f; } };
  const copied = serializeRichSelection(root, range);
  assert.equal(copied.text, '[b][i]selected[/i][/b]');
  assert.equal(copied.html, '<strong><em>selected</em></strong>');
  const destination = document.createElement('div'); destination.innerHTML = sanitizeRichHTML(copied.html, document);
  assert.equal(serializeRichText(destination), copied.text);
});

test('a selection inside a list retains a complete list rather than an orphan item marker', () => {
  const { root, document } = surface();
  renderRichText('[list][*][b]first[/b][*]second[/list]', root);
  const text = root.querySelector('strong').firstChild;
  const range = { commonAncestorContainer: text, cloneRange() { return this; }, cloneContents() { const f = document.createDocumentFragment(); f.append(document.createTextNode('first')); return f; } };
  const copied = serializeRichSelection(root, range);
  assert.equal(copied.text, '[list][*][b]first[/b][/list]');
  assert.equal(copied.html, '<ul><li><strong>first</strong></li></ul>');
});

test('selection at the end of a preceding item does not copy a phantom empty bullet', () => {
  const { root, document } = surface();
  renderRichText('[list][*]first[*][u]second[/u][/list]', root);
  const list = root.querySelector('ul'), firstText = list.firstElementChild.firstChild, secondText = list.lastElementChild.firstChild.firstChild;
  const range = {
    startContainer: firstText, startOffset: 5, endContainer: secondText, endOffset: 6, commonAncestorContainer: list,
    cloneRange() { return { ...this }; },
    setStartAfter(node) { this.startContainer = node.parentNode; this.startOffset = Array.from(node.parentNode.childNodes).indexOf(node) + 1; },
    cloneContents() {
      const fragment = document.createDocumentFragment();
      if (this.startContainer !== list) fragment.append(document.createElement('li'));
      fragment.append(list.lastElementChild.cloneNode(true)); return fragment;
    },
  };
  const copied = serializeRichSelection(root, range);
  assert.equal(copied.text, '[list][*][u]second[/u][/list]');
  assert.equal(copied.html, '<ul><li><u>second</u></li></ul>');
  assert.equal(range.startContainer, firstText);
});

test('IME emits only committed author text and loading a version never emits an edit', t => {
  const { document } = surface(), previous = globalThis.document;
  globalThis.document = document; t.after(() => { globalThis.document = previous; });
  const changes = [], editor = createRichEditor({ text: 'old', onChange: text => changes.push(text) });
  document.body.append(editor.toolbar, editor.element);
  assert.equal(editor.element.getAttribute('contenteditable'), 'true');
  editor.element.dispatchEvent(new document.defaultView.Event('compositionstart'));
  editor.element.textContent = '中'; editor.element.dispatchEvent(new document.defaultView.Event('input'));
  assert.deepEqual(changes, []);
  editor.element.textContent = '中文'; editor.element.dispatchEvent(new document.defaultView.Event('compositionend'));
  editor.element.dispatchEvent(new document.defaultView.Event('input'));
  assert.deepEqual(changes, ['中文']);
  editor.setText('[h2]loaded[/h2]');
  assert.equal(editor.getText(), '[h2]loaded[/h2]');
  assert.deepEqual(changes, ['中文']);
});
