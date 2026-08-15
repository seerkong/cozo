'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('path');
const packageJson = require('../package.json');
const { platformKey, resolveNativePath } = require('../lib/native-path');

test('advertises macOS arm64 as the sole install target', () => {
    assert.deepEqual(packageJson.os, ['darwin']);
    assert.deepEqual(packageJson.cpu, ['arm64']);
});

test('maps the macOS arm64 release target to a package-local binary', () => {
    assert.equal(platformKey('darwin', 'arm64'), 'darwin-arm64');
    assert.equal(
        resolveNativePath('/package', 'darwin', 'arm64'),
        path.join('/package', 'native', 'darwin-arm64', 'depa_cozo.node')
    );
});

test('rejects all targets other than macOS arm64', () => {
    for (const [platform, arch] of [
        ['darwin', 'x64'],
        ['linux', 'arm64'],
        ['linux', 'x64'],
        ['win32', 'arm64'],
        ['win32', 'x64'],
    ]) {
        assert.throws(
            () => resolveNativePath('/package', platform, arch),
            /does not provide a native binary/
        );
    }
});
