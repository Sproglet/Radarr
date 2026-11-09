// Run with `node docs/development/verify-edition-manual-state.cjs` after locked frontend dependencies are installed.
// Exercises the actual Redux reducers/thunks; only the HTTP transport is replaced.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const Module = require('node:module');
const root = process.cwd();
const babel = require(path.join(root, 'node_modules/@babel/core'));
process.env.NODE_PATH = path.join(root, 'frontend/src');
Module._initPaths();
const originalJs = Module._extensions['.js'];
for (const extension of ['.js', '.ts', '.tsx']) {
  Module._extensions[extension] = (module, filename) => {
    if (!filename.startsWith(path.join(root, 'frontend/src/'))) {
      return originalJs(module, filename);
    }
    const { code } = babel.transformSync(fs.readFileSync(filename, 'utf8'), {
      filename, babelrc: false, configFile: false,
      presets: [require.resolve(path.join(root, 'node_modules/@babel/preset-typescript'))],
      plugins: [require.resolve(path.join(root, 'node_modules/@babel/plugin-transform-modules-commonjs'))],
    });
    module._compile(code, filename);
  };
}
const requests = [];
const originalLoad = Module._load;
Module._load = function(request, parent, isMain) {
  if (request === 'Utilities/createAjaxRequest') {
    return (options) => {
      assert.equal(options.url, '/manualimport');
      requests.push(options);
      const deferred = { done() { return deferred; }, fail() { return deferred; } };
      return { request: deferred, abortRequest() {} };
    };
  }
  return originalLoad.call(this, request, parent, isMain);
};
const actions = require(path.join(root, 'frontend/src/Store/Actions/interactiveImportActions.js'));
const reducer = actions.reducers;
const initial = { ...actions.defaultState, items: [
  { id: 101, movie: { id: 1 }, movieEditionId: 11, downloadClientId: 41, downloadId: 'synthetic-shared-id', path: '/synthetic/theatrical-imax.mkv' },
  { id: 102, movie: { id: 1 }, movieEditionId: 12, downloadClientId: 42, downloadId: 'synthetic-shared-id', path: '/synthetic/directors-3d.mkv' },
] };
let state = reducer(initial, actions.updateInteractiveImportItem({ id: 101, releaseGroup: 'SyntheticGroup' }));
state = reducer(state, actions.updateInteractiveImportItems({ ids: [101, 102], movie: { id: 1 } }));
assert.deepEqual(state.items.map(i => [i.movieEditionId, i.downloadClientId]), [[11, 41], [12, 42]]);
const dispatch = () => {};
actions.fetchInteractiveImportItems({ downloadId: 'synthetic-shared-id', downloadClientId: 42 })(dispatch, () => ({ interactiveImport: state }));
assert.equal(requests.at(-1).data.downloadClientId, 42);
actions.reprocessInteractiveImportItems({ ids: [101, 102] })(dispatch, () => ({ interactiveImport: state }));
assert.deepEqual(JSON.parse(requests.at(-1).data).map(i => [i.movieEditionId, i.downloadClientId, i.downloadId]), [
  [11, 41, 'synthetic-shared-id'], [12, 42, 'synthetic-shared-id'],
]);
console.log('PASS: single/bulk edits and actual reprocess requests preserve distinct client/edition identities with a shared download ID');
const single = reducer(state, actions.updateInteractiveImportItem({ id: 101, movie: { id: 2 } }));
assert.equal(single.items[0].movieEditionId, undefined);
assert.equal(single.items[0].downloadClientId, 41);
assert.equal(single.items[1].movieEditionId, 12);
const bulk = reducer(state, actions.updateInteractiveImportItems({ ids: [101, 102], movie: { id: 2 } }));
assert.deepEqual(bulk.items.map(i => i.movieEditionId), [undefined, undefined]);
assert.deepEqual(state.items.map(i => i.movieEditionId), [11, 12]);
console.log('PASS: changing movies clears stale edition IDs per item without losing the saved client or mutating prior state');
