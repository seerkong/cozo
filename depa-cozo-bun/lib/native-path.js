'use strict';

const path = require('path');

/**
 * One directory per Node platform-arch key. The loader does not keep an allowlist:
 * a missing file is the unsupported state, so a new target (win32-x64, linux-arm64, …)
 * is added by dropping `native/<platform>-<arch>/depa_cozo.node` in place.
 *
 * Keys match `process.platform` + `process.arch` (Windows is `win32`, not `windows`).
 */
function platformKey(platform = process.platform, arch = process.arch) {
    return `${platform}-${arch}`;
}

function resolveNativePath(rootDirectory, platform = process.platform, arch = process.arch) {
    return path.join(rootDirectory, 'native', platformKey(platform, arch), 'depa_cozo.node');
}

module.exports = { platformKey, resolveNativePath };
