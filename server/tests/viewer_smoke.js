// Run with node server/tests/viewer_smoke.js.
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const root = path.resolve(__dirname, '..', '..');
const html = fs.readFileSync(path.join(root, 'viewer', 'index.html'), 'utf8');
const script = html.match(/<script>\s*([\s\S]*?)<\/script>/);
assert(script, 'inline viewer script missing');
new vm.Script(script[1], { filename: 'viewer/index.html' });
assert(html.includes('reconstruct?method=colmap'), 'viewer must call the implemented reconstruction method');

const THREE = require(path.join(root, 'viewer', 'js', 'three.min.js'));
global.THREE = THREE;
require(path.join(root, 'viewer', 'js', 'STLExporter.js'));
const source = script[1];
const start = source.indexOf('function exportModel(format)');
const end = source.indexOf('function downloadBlob(', start);
assert(start >= 0 && end > start, 'actual STL export function missing');
const exportFunction = source.slice(start, end);

function boundsFromSTL(binary) {
    const triangleCount = binary.getUint32(80, true);
    assert(triangleCount > 0);
    const minimum = [Infinity, Infinity, Infinity];
    const maximum = [-Infinity, -Infinity, -Infinity];
    for (let i = 0; i < triangleCount; i++) {
        const offset = 84 + i * 50 + 12;
        for (let vertex = 0; vertex < 3; vertex++) {
            for (let axis = 0; axis < 3; axis++) {
                const value = binary.getFloat32(offset + vertex * 12 + axis * 4, true);
                minimum[axis] = Math.min(minimum[axis], value);
                maximum[axis] = Math.max(maximum[axis], value);
            }
        }
    }
    return { minimum, maximum, sizes: maximum.map((v, i) => Math.round(v - minimum[i])) };
}

function exportActualViewerModel(model, units, upAxis) {
    let saved;
    const context = {
        THREE, currentModel: model, currentUnits: units, currentUpAxis: upAxis,
        Blob: class { constructor(parts) { this.parts = parts; } },
        downloadBlob: (blob, name) => { saved = { binary: blob.parts[0], name }; },
        toast: () => {},
    };
    vm.runInNewContext(exportFunction + "\nexportModel('stl');", context);
    assert(saved, 'viewer did not export a file');
    assert.strictEqual(saved.name, 'metascan_model_mm.stl');
    return boundsFromSTL(saved.binary);
}

const metricModel = new THREE.Group();
metricModel.add(new THREE.Mesh(new THREE.BoxGeometry(0.2, 0.3, 0.4)));
const metric = exportActualViewerModel(metricModel, 'm', 'y');
assert.deepStrictEqual(metric.sizes, [200, 400, 300]);
assert(Math.abs(metric.minimum[2]) < 1e-4, 'metric export must sit on print bed');
assert.deepStrictEqual(metricModel.scale.toArray(), [1, 1, 1], 'source model was mutated');

const stlModel = new THREE.Mesh(new THREE.BoxGeometry(200, 400, 300));
stlModel.position.z = 150;
const existingSTL = exportActualViewerModel(stlModel, 'mm', 'z');
assert.deepStrictEqual(existingSTL.sizes, [200, 400, 300], 'loaded STL was rotated or scaled twice');
assert(Math.abs(existingSTL.minimum[2]) < 1e-4);
console.log('viewer actual STL export: Z-up, millimetres, existing STL unchanged');
