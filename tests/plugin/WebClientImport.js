// Runs the web client's own export and import code from odrive-wheel.html, as it is, against a values file of
// the plugin. Only the page around that code is stood in for: the inputs, built from its SCHEMA the way its renderer
// builds them, and the calls it makes to the page. Nothing is written anywhere: confirm() answers no, so the import
// only loads the values into the inputs, which is where the web client stops before a user applies them.
//
//   node WebClientImport.js <odrive-wheel.html> <values.json>
//
// Prints one JSON object. Used by tests/plugin/SnapshotTests.cs, which does the checking.
'use strict';

const fs = require('fs');
const vm = require('vm');

const [htmlPath, valuesPath] = process.argv.slice(2);
const lines = fs.readFileSync(htmlPath, 'utf8').split(/\r?\n/);

function indexOf(test, from) {
  for (let i = from || 0; i < lines.length; i++) if (test(lines[i])) return i;
  throw new Error('not found in ' + htmlPath + ': ' + test);
}

// From the first line that starts with `start` up to the first line after it that is exactly `end`.
function block(start, end) {
  const first = indexOf(l => l.startsWith(start));
  const last = indexOf(l => l === end, first + 1);
  return lines.slice(first, last + 1).join('\n');
}

const schemaEnd = indexOf(l => l === '];', indexOf(l => l === 'const SCHEMA = ['));
const code = [
  lines.slice(indexOf(l => l.startsWith('const ENUM_MOTOR_TYPE')), schemaEnd + 1).join('\n'),
  block('const READONLY_EXPORT_PATHS', ']);'),
  block('function setInputValue(', '}'),
  block('function markDirty(', '}'),
  block('function exportJSON(', '}'),
  block('async function doImport(', '}'),
  'globalThis.__page = { SCHEMA, READONLY_EXPORT_PATHS, setInputValue, exportJSON, doImport };',
].join('\n');

const IMPORT_SELECTOR = '#content input[data-path], #content select[data-path]';
let inputs = [];
let exported = null;
const logs = [], toasts = [], states = {};

const page = vm.createContext({
  console,
  logLine: m => logs.push(String(m)),
  toast: m => toasts.push(String(m)),
  toastSticky() {},
  toastStickyClose() {},
  t: key => key,
  confirm: () => false,
  writer: null,
  writeOne: async () => { throw new Error('the check must not write'); },
  _applyFieldState: (input, state) => { states[input.dataset.path] = state; },
  _updateHzHint() {},
  _updateControllerInert() {},
  Blob: class { constructor(parts) { this.text = parts.join(''); } },
  URL: { createObjectURL: blob => { exported = blob.text; return 'blob:'; }, revokeObjectURL() {} },
  document: {
    querySelectorAll: selector => {
      if (selector !== IMPORT_SELECTOR) throw new Error('unexpected selector: ' + selector);
      return inputs;
    },
    createElement: () => ({ click() {} }),
  },
});
vm.runInContext(code, page, { filename: 'odrive-wheel.html' });
const { SCHEMA, READONLY_EXPORT_PATHS, setInputValue, exportJSON, doImport } = page.__page;

// The renderer's path rule and element kinds (the loop over tab.fields in odrive-wheel.html). A select keeps only
// a value one of its options has, like a real one.
function buildInputs() {
  const result = [];
  for (const tab of SCHEMA) {
    if (!Array.isArray(tab.fields)) continue;
    for (const fld of tab.fields) {
      if (fld.group) continue;
      const path = (fld._path !== undefined) ? fld._path : (tab.prefix + fld.name);
      const options = fld.type === 'enum' ? Object.keys(fld.opts) : null;
      let value = options ? options[0] : '';
      result.push({
        tagName: options ? 'SELECT' : 'INPUT',
        type: fld.type === 'bool' ? 'checkbox' : (options ? 'select-one' : 'text'),
        dataset: { path, type: fld.type, protocol: fld._protocol || tab.protocol || 'odrv', currentServerValue: '' },
        checked: false,
        get value() { return value; },
        set value(v) { v = String(v); value = options ? (options.includes(v) ? v : '') : v; },
      });
    }
  }
  return result;
}

(async () => {
  const text = fs.readFileSync(valuesPath, 'utf8');

  inputs = buildInputs();
  await doImport({ text: async () => text });
  const imported = {};
  for (const input of inputs)
    if (states[input.dataset.path] !== undefined)
      imported[input.dataset.path] = { type: input.dataset.type, value: input.value, checked: input.checked };

  // The same replies as the web client would have read them, then its own export.
  inputs = buildInputs();
  const values = JSON.parse(text);
  for (const input of inputs)
    if (Object.prototype.hasOwnProperty.call(values, input.dataset.path)) setInputValue(input, values[input.dataset.path], false);
  exportJSON();

  process.stdout.write(JSON.stringify({
    schemaPaths: buildInputs().map(i => i.dataset.path),
    readonlyExportPaths: Array.from(READONLY_EXPORT_PATHS),
    imported,
    logs,
    toasts,
    webExport: exported === null ? null : JSON.parse(exported),
  }));
})().catch(e => {
  process.stderr.write(String(e && e.stack || e));
  process.exit(2);
});
