'use strict';

const fs = require('fs');
const path = require('path');
const { resolveNativePath } = require('../lib/native-path');

const nativePath = resolveNativePath(path.resolve(__dirname, '..'));
if (!fs.existsSync(nativePath)) {
    throw new Error(`Missing host native artifact: ${nativePath}`);
}
console.log(`Verified host native artifact: ${nativePath}`);
