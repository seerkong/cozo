'use strict';

const path = require('path');

const supportedPlatforms = new Set([
    'darwin-arm64',
]);

function platformKey(platform = process.platform, arch = process.arch) {
    return `${platform}-${arch}`;
}

function resolveNativePath(rootDirectory, platform = process.platform, arch = process.arch) {
    const key = platformKey(platform, arch);
    if (!supportedPlatforms.has(key)) {
        throw new Error(
            `depa-cozo does not provide a native binary for ${key}. ` +
            `Supported targets: ${[...supportedPlatforms].join(', ')}.`
        );
    }
    return path.join(rootDirectory, 'native', key, 'depa_cozo.node');
}

module.exports = { platformKey, resolveNativePath, supportedPlatforms };
