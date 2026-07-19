const procurement = require('./procurement');
const hr = require('./hr');
const crm = require('./crm');
const resourceGraph = require('./resource-graph');
const approvalFlow = require('./approval-flow');
const orgTimeline = require('./org-timeline');

const allDemos = [procurement, hr, crm, resourceGraph, approvalFlow, orgTimeline];
const demoMap = Object.fromEntries(allDemos.map(d => [d.demoId, d]));

module.exports = { allDemos, demoMap };
