const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const vm = require('node:vm');

class Element {
  constructor(tag = 'div') {
    this.tag = tag;
    this.children = [];
    this.events = new Map();
    this.attributes = new Map();
    this.style = {};
    this.value = '';
    this.dir = '';
    this.className = '';
    this.hidden = false;
    this.disabled = false;
    this._text = '';
  }
  set textContent(value) { this._text = String(value); this.children = []; }
  get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
  appendChild(child) { this.children.push(child); return child; }
  append(...nodes) { for (const node of nodes) this.children.push(node); }
  remove() {}
  addEventListener(name, handler) { this.events.set(name, handler); }
  setAttribute(name, value) { this.attributes.set(name, value); }
  getAttribute(name) { return this.attributes.get(name); }
  getBoundingClientRect() { return { left: 0, top: 0, bottom: 0 }; }
  showPopover() {}
  hidePopover() {}
  focus() {}
  querySelectorAll() { return []; }
  all() { return this.children.flatMap(child => [child, ...child.all()]); }
  byClass(name) { return this.all().filter(node => node.className.split(' ').includes(name)); }
}

function load(file) {
  const elements = new Map();
  const root = { clientWidth: 1200, clientHeight: 800, dir: '', lang: 'en' };
  const element = id => {
    if (!elements.has(id)) elements.set(id, new Element());
    return elements.get(id);
  };
  let receive;
  const context = vm.createContext({
    document: {
      documentElement: root,
      getElementById: element,
      querySelectorAll: () => [],
      createElement: tag => new Element(tag),
      createTextNode: text => { const node = new Element('#text'); node.textContent = text; return node; },
    },
    parent: { postMessage: () => {} },
    window: { addEventListener: (_, handler) => { receive = handler; } },
    requestAnimationFrame: callback => { callback(); return 1; },
    cancelAnimationFrame: () => {},
  });
  const html = readFileSync(join(__dirname, '../web', file), 'utf8');
  vm.runInContext(html.match(/<script>([\s\S]*?)<\/script>/)[1], context);
  return {
    root, element,
    message: message => receive({ data: { novalistHost: JSON.stringify(message) } }),
  };
}

const arabic = { pageDir: 'rtl', pageLang: 'ar', send: 'إرسال' };
const english = { pageDir: 'ltr', pageLang: 'en', send: 'Send' };
const ARABIC_TEXT = 'مرحبا بك في القصة';

const PAGES = [
  ['chat.html', { type: 'history', messages: [] }],
  ['character-chat.html', { type: 'setup', characters: [], scenes: [], turns: [] }],
  ['story-analysis.html', { type: 'setup', chapters: [] }],
  ['knowledge.html', { type: 'characters', characters: [] }],
];

for (const [file, message] of PAGES) {
  test(`${file} follows the reading direction its locale declares, in both directions`, () => {
    const page = load(file);
    page.message({ ...message, strings: arabic });
    assert.equal(page.root.dir, 'rtl');
    assert.equal(page.root.lang, 'ar');
    page.message({ ...message, strings: english });
    assert.equal(page.root.dir, 'ltr');
    assert.equal(page.root.lang, 'en');
  });

  test(`${file} treats an unknown direction as left-to-right`, () => {
    const page = load(file);
    page.message({ ...message, strings: { pageDir: 'sideways', pageLang: 'xx' } });
    assert.equal(page.root.dir, 'ltr');
  });

  test(`${file} leaves the direction alone when the message carries no strings`, () => {
    const page = load(file);
    page.message({ ...message, strings: arabic });
    page.message({ ...message, strings: undefined });
    assert.equal(page.root.dir, 'rtl');
  });
}

test('chat messages follow their own script, and a message without letters keeps the page direction', () => {
  const page = load('chat.html');
  page.message({ type: 'history', strings: arabic, messages: [
    { role: 'user', text: ARABIC_TEXT },
    { role: 'assistant', text: '12345', thinking: '' },
    { role: 'assistant', text: 'Hello there', thinking: ARABIC_TEXT },
  ] });
  const [user, digits, mixed] = page.element('messages').children;
  assert.match(user.className, /\bbidi\b/);
  assert.doesNotMatch(digits.className, /\bbidi\b/);
  assert.match(mixed.className, /\bbidi\b/);
  assert.equal(mixed.children[0].dir, 'rtl');
});

test('the chat input takes the direction of what is typed and gives it back when emptied', () => {
  const page = load('chat.html');
  const input = page.element('input');
  const type = value => { input.value = value; input.events.get('input')(); };
  type(ARABIC_TEXT);
  assert.equal(input.dir, 'rtl');
  type('Hello');
  assert.equal(input.dir, 'ltr');
  type('123');
  assert.equal(input.dir, '');
  type('');
  assert.equal(input.dir, '');
});

test('character chat turns follow their own script', () => {
  const page = load('character-chat.html');
  page.message({ type: 'setup', strings: arabic, characters: [], scenes: [], turns: [
    { speaker: 'أنت', text: ARABIC_TEXT, isCharacter: false },
    { speaker: 'Mira', text: 'Hello', isCharacter: true },
    { speaker: 'Mira', text: '...', isCharacter: true },
  ] });
  const [first, second, third] = page.element('turns').children;
  assert.equal(first.dir, 'rtl');
  assert.equal(second.dir, 'ltr');
  assert.equal(third.dir, '');
});

test('findings show a translated kind and each piece of content follows its own script', () => {
  const page = load('story-analysis.html');
  page.message({ type: 'setup', chapters: [], strings: { ...arabic, 'findingType.reference': 'إشارة', addToCodex: 'إضافة إلى الموسوعة' } });
  page.message({ type: 'findings', findings: [{
    findingType: 'reference', title: ARABIC_TEXT, chapter: 'Chapter 3', excerpt: '"..."', description: ARABIC_TEXT,
    entityName: 'Mira Vance', canAddToCodex: true,
  }] });
  const finding = page.element('findings').children[0];
  const [kind, title, meta, add, excerpt, description] = finding.children;
  assert.equal(kind.textContent, 'إشارة');
  assert.equal(title.dir, 'rtl');
  assert.equal(meta.dir, 'ltr');
  assert.equal(excerpt.dir, '');
  assert.equal(description.dir, 'rtl');
  assert.equal(add.textContent, 'إضافة إلى الموسوعة: ⁨Mira Vance⁩');
});

test('an unknown finding kind is shown as it arrived', () => {
  const page = load('story-analysis.html');
  page.message({ type: 'setup', chapters: [], strings: english });
  page.message({ type: 'findings', findings: [{ findingType: 'novel_kind', title: 'T', chapter: 'C' }] });
  assert.equal(page.element('findings').children[0].children[0].textContent, 'novel_kind');
});

test('knowledge entries follow their own script', () => {
  const page = load('knowledge.html');
  page.message({ type: 'characters', strings: arabic, characters: [
    { id: 'a', name: 'ليلى', scenesRecorded: 0, scenesKnown: 0 },
    { id: 'b', name: 'Mira', scenesRecorded: 2, scenesKnown: 1 },
  ] });
  const [first, second] = page.element('people').children;
  assert.equal(first.children[0].dir, '');
  assert.equal(first.children[0].children[0].dir, 'rtl');
  assert.equal(second.children[0].children[0].dir, 'ltr');
  page.message({ type: 'scenes', scenes: [{
    sceneTitle: ARABIC_TEXT, chapterTitle: '', present: true, modelId: 'qwen3',
    fields: [{ label: 'ما لاحظه', values: [ARABIC_TEXT, '42'] }],
  }] });
  const scene = page.element('detail').children[0];
  const values = scene.all().filter(node => node.tag === 'li').map(node => node.children[0]);
  assert.equal(values[0].tag, 'bdi');
  assert.equal(values[0].dir, 'rtl');
  assert.equal(values[1].dir, '');
});
