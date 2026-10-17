const TAGS = { b: 'strong', i: 'em', u: 'u', s: 's', h1: 'h1', h2: 'h2', h3: 'h3', quote: 'blockquote', code: 'pre', list: 'ul', olist: 'ol', table: 'table', tr: 'tr', th: 'th', td: 'td', spoiler: 'span', url: 'a' };
export function renderBBCode(value, container, onLink) {
  container.replaceChildren();
  const stack = [{ tag: 'root', node: container }];
  const appendText = text => {
    const parent = stack.at(-1).node;
    if (stack.at(-1).tag === 'code') { parent.append(document.createTextNode(text)); return; }
    const lines = text.split('\n');
    lines.forEach((line, index) => { if (index) parent.append(document.createElement('br')); parent.append(document.createTextNode(line)); });
  };
  const tokens = /\[(\/?)([a-z0-9*]+)(?:=([^\]]*))?\]/gi;
  let offset = 0; let match;
  while ((match = tokens.exec(value))) {
    const [, closing, raw, argument] = match; const tag = raw.toLowerCase();
    if (stack.at(-1).tag === 'code' && !(closing && tag === 'code')) continue;
    appendText(value.slice(offset, match.index)); offset = tokens.lastIndex;
    if (closing) {
      const index = stack.findLastIndex(frame => frame.tag === tag);
      if (index <= 0) { appendText(match[0]); continue; }
      stack.splice(index); continue;
    }
    if (tag === 'hr') { stack.at(-1).node.append(document.createElement('hr')); continue; }
    if (tag === '*') {
      const listIndex = stack.findLastIndex(frame => ['list', 'olist'].includes(frame.tag));
      if (listIndex < 0) { appendText(match[0]); continue; }
      stack.splice(listIndex + 1);
      const node = document.createElement('li'); stack.at(-1).node.append(node); stack.push({ tag: 'li', node }); continue;
    }
    if (!TAGS[tag] || stack.length > 40) { appendText(match[0]); continue; }
    const node = document.createElement(TAGS[tag]);
    if (tag === 'spoiler') { node.className = 'spoiler'; node.tabIndex = 0; node.addEventListener('click', () => node.classList.toggle('revealed')); }
    if (tag === 'url') {
      node.addEventListener('click', event => {
        event.preventDefault();
        try {
          const target = new URL(argument || node.textContent.trim());
          if (target.protocol === 'https:' && !target.username && !target.password) onLink(target.href);
        } catch {}
      });
      node.href = '#';
    }
    stack.at(-1).node.append(node); stack.push({ tag, node });
  }
  appendText(value.slice(offset));
}
