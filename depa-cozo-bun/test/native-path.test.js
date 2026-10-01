'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('path');
const packageJson = require('../package.json');
const { platformKey, resolveNativePath } = require('../lib/native-path');

test('does not restrict install to one os or cpu — the loader picks the artifact', () => {
    assert.equal(packageJson.os, undefined);
    assert.equal(packageJson.cpu, undefined);
});

test('maps each platform-arch pair to its own package-local binary', () => {
    const cases = [
        ['darwin', 'arm64', 'darwin-arm64'],
        ['darwin', 'x64', 'darwin-x64'],
        ['linux', 'x64', 'linux-x64'],
        ['linux', 'arm64', 'linux-arm64'],
        ['win32', 'x64', 'win32-x64'],
        ['win32', 'arm64', 'win32-arm64'],
    ];
    for (const [platform, arch, key] of cases) {
        assert.equal(platformKey(platform, arch), key);
        assert.equal(
            resolveNativePath('/package', platform, arch),
            path.join('/package', 'native', key, 'depa_cozo.node')
        );
    }
});

test('does not reject a target that has no binary yet', () => {
    // Absence of the file is the unsupported state. resolveNativePath only names the slot.
    assert.equal(
        resolveNativePath('/package', 'win32', 'x64'),
        path.join('/package', 'native', 'win32-x64', 'depa_cozo.node')
    );
});
