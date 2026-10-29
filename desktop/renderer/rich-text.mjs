const TAGS = { b: 'strong', i: 'em', u: 'u', s: 's', strike: 's', h1: 'h1', h2: 'h2', h3: 'h3', quote: 'blockquote', code: 'pre', list: 'ul', olist: 'ol', table: 'table', tr: 'tr', th: 'th', td: 'td', spoiler: 'span', url: 'a' };
const MARKUP = { STRONG: 'b', B: 'b', EM: 'i', I: 'i', U: 'u', S: 'strike', STRIKE: 'strike', DEL: 'strike', H1: 'h1', H2: 'h2', H3: 'h3', BLOCKQUOTE: 'quote', PRE: 'code', UL: 'list', OL: 'olist', TABLE: 'table', TR: 'tr', TH: 'th', TD: 'td' };
const ACTIVE = new Set(['SCRIPT', 'STYLE', 'IFRAME', 'OBJECT', 'EMBED', 'SVG', 'MATH', 'FORM', 'INPUT', 'TEXTAREA', 'SELECT', 'BUTTON', 'META', 'LINK', 'BASE', 'IMG', 'VIDEO', 'AUDIO']);
const ALLOWED = new Set(['STRONG', 'EM', 'U', 'S', 'H1', 'H2', 'H3', 'BLOCKQUOTE', 'PRE', 'UL', 'OL', 'LI', 'TABLE', 'TBODY', 'THEAD', 'TFOOT', 'TR', 'TH', 'TD', 'P', 'DIV', 'BR', 'HR', 'A']);

function safeHref(value) {
  try {
    const url = new URL(value);
    if (url.protocol === 'https:' && !url.username && !url.password) return url.href.replaceAll('[', '%5B').replaceAll(']', '%5D');
  } catch {}
  return null;
}

export function renderRichText(value, container, onLink) {
  const doc = container.ownerDocument;
  container.replaceChildren();
  const stack = [{ tag: 'root', node: container }];
  const appendText = text => {
    const parent = stack.at(-1).node;
    if (stack.at(-1).tag === 'code') { parent.append(doc.createTextNode(text)); return; }
    text.split('\n').forEach((line, index) => {
      if (index) parent.append(doc.createElement('br'));
      parent.append(doc.createTextNode(line));
    });
  };
  const finishLink = frame => {
    if (frame.tag !== 'url') return;
    const url = safeHref(frame.argument || frame.node.textContent.trim());
    if (url) {
      frame.node.setAttribute('href', url);
      if (onLink) frame.node.addEventListener('click', event => { event.preventDefault(); onLink(url); });
    }
  };
  const tokens = /\[(\/?)([a-z0-9*]+)(?:=([^\]]*))?\]/gi;
  let offset = 0, match;
  while ((match = tokens.exec(value))) {
    const [, closing, raw, argument] = match, tag = raw.toLowerCase();
    const literal = stack.at(-1).tag;
    if (['code', 'noparse'].includes(literal) && !(closing && tag === literal)) continue;
    appendText(value.slice(offset, match.index)); offset = tokens.lastIndex;
    if (closing) {
      if (tag === 'hr') continue;
      const index = stack.findLastIndex(frame => frame.tag === tag);
      if (index <= 0) { appendText(match[0]); continue; }
      stack.splice(index).forEach(finishLink); continue;
    }
    if (tag === 'noparse') { stack.push({ tag, node: stack.at(-1).node }); continue; }
    if (tag === 'hr') { stack.at(-1).node.append(doc.createElement('hr')); continue; }
    if (tag === '*') {
      const index = stack.findLastIndex(frame => frame.tag === 'list' || frame.tag === 'olist');
      if (index < 0) { appendText(match[0]); continue; }
      stack.splice(index + 1).forEach(finishLink);
      const node = doc.createElement('li'); stack.at(-1).node.append(node); stack.push({ tag: 'li', node }); continue;
    }
    if (!TAGS[tag] || stack.length > 40 || (tag === 'url' && argument && !safeHref(argument))) { appendText(match[0]); continue; }
    const node = doc.createElement(TAGS[tag]);
    if (tag === 'spoiler') node.setAttribute('data-rich-type', 'spoiler');
    stack.at(-1).node.append(node); stack.push({ tag, node, argument });
  }
  appendText(value.slice(offset)); stack.forEach(finishLink);
}

function escapeLiteralText(value) {
  return value.replace(/\[(\/?)([a-z0-9*]+)(?:=[^\]]*)?\]/gi, (token, closing, tag) => {
    tag = tag.toLowerCase();
    if (!TAGS[tag] && !['noparse', 'hr', '*'].includes(tag)) return token;
    if (closing && tag === 'noparse') return `[noparse][[/noparse][noparse]${token.slice(1)}[/noparse]`;
    return `[noparse]${token}[/noparse]`;
  });
}

function codeText(node) {
  if (node.nodeType === 3) return node.textContent;
  if (node.nodeType !== 1 || ACTIVE.has(node.tagName)) return '';
  if (node.tagName === 'BR') return '\n';
  return Array.from(node.childNodes, codeText).join('');
}

function serializeChildren(parent) {
  let result = '', literal = '', previousBlock = false, previousNode = false;
  const flush = () => { result += escapeLiteralText(literal); literal = ''; };
  const consume = node => {
    if (node.nodeType === 3) {
      if (!node.textContent) return;
      if (previousBlock) literal += '\n';
      literal += node.textContent; previousBlock = false; previousNode = true; return;
    }
    if (node.nodeType !== 1 || ACTIVE.has(node.tagName)) return;
    const tag = node.tagName, block = ['DIV', 'P'].includes(tag);
    const semantic = MARKUP[tag] || ['A', 'LI', 'BR', 'HR'].includes(tag) || (tag === 'SPAN' && node.getAttribute('data-rich-type') === 'spoiler');
    if (!block && !semantic) { for (const child of node.childNodes) consume(child); return; }
    if (previousNode && (block || previousBlock)) literal += '\n';
    flush();
    let text = serializeNode(node);
    if (block && text === '\n') text = '';
    result += text; previousBlock = block; previousNode = true;
  };
  for (const node of parent.childNodes) consume(node);
  flush(); return result;
}

function serializeNode(node) {
  if (node.nodeType === 3) return escapeLiteralText(node.textContent);
  if (node.nodeType !== 1) return '';
  const tag = node.tagName;
  if (ACTIVE.has(tag)) return '';
  if (tag === 'BR') return '\n';
  if (tag === 'HR') return '[hr]';
  if (tag === 'PRE') {
    const content = codeText(node).replace(/\[\/code\]/gi, token => `[/code]${escapeLiteralText(token)}[code]`);
    return `[code]${content}[/code]`;
  }
  const text = serializeChildren(node);
  if (tag === 'LI') return `[*]${text}`;
  if (tag === 'A') {
    const href = safeHref(node.getAttribute('href'));
    return href ? `[url=${href}]${text}[/url]` : text;
  }
  const markup = tag === 'SPAN' && node.getAttribute('data-rich-type') === 'spoiler' ? 'spoiler' : MARKUP[tag];
  return markup ? `[${markup}]${text}[/${markup}]` : text;
}

export function serializeRichText(container) { return serializeChildren(container); }

function cleanChildren(source, target, doc) {
  for (const node of source.childNodes) {
    if (node.nodeType === 3) { target.append(doc.createTextNode(node.textContent)); continue; }
    if (node.nodeType !== 1 || ACTIVE.has(node.tagName)) continue;
    const tag = ({ B: 'STRONG', I: 'EM', DEL: 'S', STRIKE: 'S', CODE: 'PRE' })[node.tagName] || node.tagName;
    if (tag === 'PRE' && target.closest('pre')) { cleanChildren(node, target, doc); continue; }
    let clean;
    if (tag === 'A') {
      const href = safeHref(node.getAttribute('href'));
      if (href) { clean = doc.createElement('a'); clean.setAttribute('href', href); }
    } else if (tag === 'SPAN' && (node.getAttribute('data-rich-type') === 'spoiler' || node.classList.contains('spoiler'))) {
      clean = doc.createElement('span'); clean.setAttribute('data-rich-type', 'spoiler');
    } else if (tag === 'SPAN') {
      const marks = [];
      if (/^(?:bold|[6-9]00)$/.test(node.style.fontWeight)) marks.push('strong');
      if (node.style.fontStyle === 'italic') marks.push('em');
      if (/\bunderline\b/.test(node.style.textDecoration)) marks.push('u');
      if (/\bline-through\b/.test(node.style.textDecoration)) marks.push('s');
      if (marks.length) {
        clean = doc.createElement(marks.shift());
        let inner = clean;
        for (const mark of marks) { const wrapper = doc.createElement(mark); inner.append(wrapper); inner = wrapper; }
        cleanChildren(node, inner, doc); target.append(clean); continue;
      }
    } else if (ALLOWED.has(tag)) clean = doc.createElement(tag.toLowerCase());
    if (!clean) { cleanChildren(node, target, doc); continue; }
    cleanChildren(node, clean, doc); target.append(clean);
  }
}

export function sanitizeRichHTML(value, doc = document) {
  const template = doc.createElement('template'), clean = doc.createElement('div');
  template.innerHTML = value;
  cleanChildren(template.content, clean, doc);
  return clean.innerHTML;
}

export function serializeRichSelection(container, range) {
  range = range.cloneRange();
  for (const edge of ['start', 'end']) {
    let node = range[`${edge}Container`], offset = range[`${edge}Offset`];
    while (node && node !== container) {
      const length = node.nodeType === 3 ? node.textContent.length : node.childNodes.length;
      if (!length || offset !== (edge === 'start' ? length : 0)) break;
      range[edge === 'start' ? 'setStartAfter' : 'setEndBefore'](node);
      node = range[`${edge}Container`]; offset = range[`${edge}Offset`];
    }
  }
  const doc = container.ownerDocument, fragment = range.cloneContents(), wrapper = doc.createElement('div');
  let content = fragment;
  let ancestor = range.commonAncestorContainer;
  if (ancestor.nodeType === 3) ancestor = ancestor.parentNode;
  while (ancestor && ancestor !== container) {
    if (MARKUP[ancestor.tagName] || ['A', 'LI', 'SPAN'].includes(ancestor.tagName)) {
      const parent = ancestor.cloneNode(false); parent.append(content); content = parent;
    }
    ancestor = ancestor.parentNode;
  }
  wrapper.append(content);
  const html = sanitizeRichHTML(wrapper.innerHTML, doc), clean = doc.createElement('div');
  clean.innerHTML = html;
  return { text: serializeRichText(clean), html };
}

export function createRichEditor({ text = '', onChange = () => {}, onLink = () => {}, label = '个人长评正文' } = {}) {
  const element = document.createElement('div'), toolbar = document.createElement('div');
  element.className = 'rich-editor';
  element.setAttribute('contenteditable', 'true'); element.setAttribute('role', 'textbox');
  element.setAttribute('aria-multiline', 'true'); element.setAttribute('aria-label', label);
  element.setAttribute('spellcheck', 'true');
  toolbar.className = 'toolbar'; toolbar.setAttribute('role', 'toolbar'); toolbar.setAttribute('aria-label', '正文格式');
  renderRichText(text, element);
  let previous = serializeRichText(element), composing = false, savedRange;
  const currentRange = () => {
    const selection = document.getSelection();
    if (selection?.rangeCount && element.contains(selection.anchorNode) && element.contains(selection.focusNode)) return selection.getRangeAt(0);
    return null;
  };
  const commit = () => {
    const value = serializeRichText(element);
    if (value !== previous) { previous = value; onChange(value); }
  };
  const restoreRange = () => {
    element.focus();
    const selection = document.getSelection();
    if (!savedRange || !element.contains(savedRange.commonAncestorContainer)) {
      savedRange = document.createRange(); savedRange.selectNodeContents(element); savedRange.collapse(false);
    }
    selection.removeAllRanges(); selection.addRange(savedRange);
  };
  // Chromium's editing commands keep typing, paste and toolbar changes in its native undo history.
  const command = (name, value = null) => {
    savedRange = currentRange()?.cloneRange() || savedRange;
    restoreRange(); document.execCommand(name, false, value); savedRange = currentRange()?.cloneRange(); commit();
  };
  const addButton = (title, action) => {
    const button = document.createElement('button'); button.type = 'button'; button.textContent = title;
    button.addEventListener('mousedown', event => { savedRange = currentRange()?.cloneRange() || savedRange; event.preventDefault(); });
    button.addEventListener('click', action); toolbar.append(button); return button;
  };
  for (const [title, name, value] of [
    ['加粗', 'bold'], ['斜体', 'italic'], ['下划线', 'underline'], ['删除线', 'strikeThrough'],
    ['标题', 'formatBlock', 'h2'], ['正文', 'formatBlock', 'p'], ['列表', 'insertUnorderedList'],
    ['编号', 'insertOrderedList'], ['引用', 'formatBlock', 'blockquote'], ['代码', 'formatBlock', 'pre'],
    ['分隔线', 'insertHorizontalRule'], ['撤销', 'undo'], ['重做', 'redo'],
  ]) addButton(title, () => command(name, value));
  addButton('剧透', () => {
    const range = currentRange() || savedRange;
    const html = range && !range.collapsed ? serializeRichSelection(element, range).html : '剧透文字';
    command('insertHTML', `<span data-rich-type="spoiler">${html}</span>`);
  });
  addButton('表格', () => command('insertHTML', '<table><tbody><tr><th>项目</th><th>内容</th></tr><tr><td>项目</td><td>内容</td></tr></tbody></table>'));
  const linkFields = document.createElement('span'), linkInput = document.createElement('input'), linkError = document.createElement('span');
  linkFields.className = 'rich-link-fields'; linkFields.hidden = true;
  linkInput.type = 'url'; linkInput.placeholder = 'https://…'; linkInput.setAttribute('aria-label', '链接地址');
  linkError.setAttribute('role', 'alert'); linkError.className = 'error';
  addButton('链接', () => {
    linkFields.hidden = !linkFields.hidden;
    if (!linkFields.hidden) {
      const node = (currentRange() || savedRange)?.commonAncestorContainer;
      linkInput.value = (node?.nodeType === 3 ? node.parentElement : node)?.closest('a')?.getAttribute('href') || '';
      linkError.textContent = ''; linkInput.focus();
    }
  });
  const applyLink = () => {
    const url = safeHref(linkInput.value);
    if (!url) { linkError.textContent = '请输入不含账号密码的 HTTPS 链接。'; return; }
    linkError.textContent = ''; command('createLink', url); linkFields.hidden = true;
  };
  const apply = document.createElement('button'); apply.type = 'button'; apply.textContent = '应用链接';
  apply.addEventListener('mousedown', event => event.preventDefault()); apply.addEventListener('click', applyLink);
  linkInput.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); applyLink(); } });
  linkFields.append(linkInput, apply, linkError); toolbar.append(linkFields);
  element.addEventListener('input', () => { if (!composing) commit(); });
  element.addEventListener('compositionstart', () => { composing = true; });
  element.addEventListener('compositionend', () => { composing = false; commit(); });
  element.addEventListener('click', event => {
    const anchor = event.target.closest('a');
    if (!anchor) return;
    event.preventDefault();
    const url = safeHref(anchor.getAttribute('href'));
    if (url && (event.ctrlKey || event.metaKey)) onLink(url);
  });
  const insertTransfer = (event, transfer) => {
    event.preventDefault();
    savedRange = currentRange()?.cloneRange();
    const html = sanitizeRichHTML(transfer.getData('text/html')), plain = transfer.getData('text/plain');
    if (!html && !plain) return;
    command(html ? 'insertHTML' : 'insertText', html || plain);
  };
  element.addEventListener('paste', event => insertTransfer(event, event.clipboardData));
  element.addEventListener('dragover', event => event.preventDefault());
  element.addEventListener('drop', event => {
    const range = document.caretRangeFromPoint(event.clientX, event.clientY);
    if (range && element.contains(range.commonAncestorContainer)) {
      const selection = document.getSelection(); selection.removeAllRanges(); selection.addRange(range);
    }
    insertTransfer(event, event.dataTransfer);
  });
  for (const type of ['copy', 'cut']) element.addEventListener(type, event => {
    const range = currentRange();
    if (!range || range.collapsed) return;
    const value = serializeRichSelection(element, range);
    event.preventDefault(); event.clipboardData.setData('text/plain', value.text); event.clipboardData.setData('text/html', value.html);
    if (type === 'cut') { savedRange = range.cloneRange(); command('delete'); }
  });
  return { element, toolbar, getText: () => serializeRichText(element), focus: () => element.focus(),
    setText(value) { renderRichText(value, element); previous = serializeRichText(element); savedRange = null; } };
}
