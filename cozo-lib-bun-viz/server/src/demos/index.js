const procurement = require('./procurement');
const hr = require('./hr');
const crm = require('./crm');
const itAsset = require('./it-asset');
const approvalFlow = require('./approval-flow');
const orgTimeline = require('./org-timeline');

const allDemos = [procurement, hr, crm, itAsset, approvalFlow, orgTimeline];
const demoMap = Object.fromEntries(allDemos.map(d => [d.demoId, d]));

module.exports = { allDemos, demoMap };
