function cozoResultToTable(result) {
  const columns = Array.isArray(result?.headers) ? result.headers.map((h) => String(h)) : [];
  const rows = Array.isArray(result?.rows)
    ? result.rows.map((row) => Object.fromEntries(columns.map((c, i) => [c, row?.[i]])))
    : [];
  return { columns, rows };
}

function objectRowTable(obj) {
  const columns = Object.keys(obj || {});
  return {
    columns,
    rows: columns.length > 0 ? [obj] : [],
  };
}

function tableRowsToObjects(table) {
  const cols = Array.isArray(table?.columns) ? table.columns : [];
  const rows = Array.isArray(table?.rows) ? table.rows : [];
  return rows.map((row) => {
    if (Array.isArray(row)) {
      return Object.fromEntries(cols.map((c, i) => [c, row[i]]));
    }
    return row || {};
  });
}

function parseSchemaTypes(schema) {
  const typeMap = Object.create(null);
  if (typeof schema !== 'string') return typeMap;
  const re = /([A-Za-z_][A-Za-z0-9_]*)\s*:\s*([A-Za-z][A-Za-z0-9_]*)/g;
  let m = re.exec(schema);
  while (m) {
    typeMap[m[1]] = m[2];
    m = re.exec(schema);
  }
  return typeMap;
}

function parseBool(v, defaultValue = false) {
  if (typeof v === 'boolean') return v;
  if (typeof v === 'number') return v !== 0;
  if (typeof v !== 'string') return defaultValue;
  const s = v.trim().toLowerCase();
  if (s === 'true' || s === '1' || s === 'yes' || s === 'y') return true;
  if (s === 'false' || s === '0' || s === 'no' || s === 'n') return false;
  return defaultValue;
}

function coerceByType(typeName, value) {
  const t = String(typeName || '');
  if (value === null || value === undefined) return null;
  if (value === '') {
    return t === 'String' ? '' : null;
  }

  switch (t) {
    case 'Int': {
      if (typeof value === 'number') return Math.trunc(value);
      const n = Number(value);
      return Number.isFinite(n) ? Math.trunc(n) : null;
    }
    case 'Float':
    case 'Number': {
      if (typeof value === 'number') return value;
      const n = Number(value);
      return Number.isFinite(n) ? n : null;
    }
    case 'Bool':
      return parseBool(value, false);
    case 'Json': {
      if (typeof value === 'object') return value;
      try {
        return JSON.parse(String(value));
      } catch (_) {
        return value;
      }
    }
    case 'Validity': {
      if (Array.isArray(value)) {
        const dateInt = Number(value[0]);
        const valid = parseBool(value[1], true);
        return [Number.isFinite(dateInt) ? Math.trunc(dateInt) : 0, valid];
      }
      if (typeof value === 'number') {
        return [Math.trunc(value), true];
      }
      if (typeof value === 'string') {
        const trimmed = value.trim();
        if (!trimmed) return null;
        try {
          const parsed = JSON.parse(trimmed);
          if (Array.isArray(parsed)) {
            const dateInt = Number(parsed[0]);
            const valid = parseBool(parsed[1], true);
            return [Number.isFinite(dateInt) ? Math.trunc(dateInt) : 0, valid];
          }
        } catch (_) {
          const n = Number(trimmed);
          if (Number.isFinite(n)) return [Math.trunc(n), true];
        }
        return null;
      }
      if (typeof value === 'object') {
        const dateInt = Number(value.date ?? value.effective_date ?? value.effectiveDate);
        const valid = parseBool(value.valid ?? value.is_valid ?? value.isValid, true);
        if (!Number.isFinite(dateInt)) return null;
        return [Math.trunc(dateInt), valid];
      }
      return null;
    }
    case 'String':
    default:
      return String(value);
  }
}

/**
 * Write user-provided tables into CozoDb via :replace.
 * Each table: { name, columns, rows, schema }
 */
async function updateData(db, tables) {
  for (const table of Array.isArray(tables) ? tables : []) {
    if (!table || !table.name || !table.schema) continue;
    const cols = Array.isArray(table.columns) ? table.columns : [];
    if (cols.length === 0) continue;
    const typeMap = parseSchemaTypes(table.schema);
    const rowObjs = tableRowsToObjects(table);
    const data = rowObjs.map((row) => cols.map((c) => coerceByType(typeMap[c], row[c])));

    const query = `?[${cols.join(', ')}] <- $data\n:replace ${table.name} ${table.schema}`;
    await db.run(query, { data });
  }
}

async function seedRbac(db, tables) {
  await updateData(db, tables || rbacDefaultTables);
}

async function seedAbac(db, tables) {
  await updateData(db, tables || abacDefaultTables);
}

const rbacDefaultTables = [
  {
    name: 'users',
    schema: '{ user_id: String => username: String, email: String, created_at: Int }',
    columns: ['user_id', 'username', 'email', 'created_at'],
    rows: [
      ['u001', 'admin', 'admin@example.com', 20240101],
      ['u002', 'alice', 'alice@example.com', 20240102],
      ['u003', 'bob', 'bob@example.com', 20240103],
      ['u004', 'charlie', 'charlie@example.com', 20240104],
      ['u005', 'david', 'david@example.com', 20240105],
    ],
  },
  {
    name: 'roles',
    schema: '{ role_id: String => role_name: String, description: String }',
    columns: ['role_id', 'role_name', 'description'],
    rows: [
      ['r001', 'SuperAdmin', '超级管理员，拥有所有权限'],
      ['r002', 'Admin', '管理员，拥有大部分管理权限'],
      ['r003', 'Editor', '编辑者，可以编辑内容'],
      ['r004', 'Viewer', '查看者，只能查看内容'],
      ['r005', 'Guest', '访客，受限访问'],
    ],
  },
  {
    name: 'permissions',
    schema: '{ permission_id: String => resource: String, action: String, description: String }',
    columns: ['permission_id', 'resource', 'action', 'description'],
    rows: [
      ['p001', 'user', 'create', '创建用户'],
      ['p002', 'user', 'read', '查看用户'],
      ['p003', 'user', 'update', '更新用户'],
      ['p004', 'user', 'delete', '删除用户'],
      ['p005', 'role', 'create', '创建角色'],
      ['p006', 'role', 'read', '查看角色'],
      ['p007', 'role', 'update', '更新角色'],
      ['p008', 'role', 'delete', '删除角色'],
      ['p009', 'article', 'create', '创建文章'],
      ['p010', 'article', 'read', '查看文章'],
      ['p011', 'article', 'update', '更新文章'],
      ['p012', 'article', 'delete', '删除文章'],
      ['p013', 'system', 'manage', '系统管理'],
    ],
  },
  {
    name: 'user_roles',
    schema: '{ user_id: String, role_id: String => }',
    columns: ['user_id', 'role_id'],
    rows: [
      ['u001', 'r001'],
      ['u002', 'r002'],
      ['u002', 'r003'],
      ['u003', 'r003'],
      ['u004', 'r004'],
      ['u005', 'r005'],
    ],
  },
  {
    name: 'role_permissions',
    schema: '{ role_id: String, permission_id: String => }',
    columns: ['role_id', 'permission_id'],
    rows: [
      ['r001', 'p001'], ['r001', 'p002'], ['r001', 'p003'], ['r001', 'p004'],
      ['r001', 'p005'], ['r001', 'p006'], ['r001', 'p007'], ['r001', 'p008'],
      ['r001', 'p009'], ['r001', 'p010'], ['r001', 'p011'], ['r001', 'p012'],
      ['r001', 'p013'],
      ['r002', 'p002'], ['r002', 'p003'], ['r002', 'p006'], ['r002', 'p007'],
      ['r002', 'p009'], ['r002', 'p010'], ['r002', 'p011'], ['r002', 'p012'],
      ['r003', 'p002'], ['r003', 'p009'], ['r003', 'p010'], ['r003', 'p011'],
      ['r004', 'p002'], ['r004', 'p010'],
      ['r005', 'p010'],
    ],
  },
];

const abacDefaultTables = [
  {
    name: 'users',
    schema: '{ user_id: String => username: String, department: String, level: Int, job_title: String, location: String }',
    columns: ['user_id', 'username', 'department', 'level', 'job_title', 'location'],
    rows: [
      ['u001', 'alice', 'Engineering', 5, 'Senior Engineer', 'Beijing'],
      ['u002', 'bob', 'Engineering', 3, 'Engineer', 'Shanghai'],
      ['u003', 'charlie', 'Finance', 4, 'Accountant', 'Beijing'],
      ['u004', 'david', 'HR', 3, 'HR Specialist', 'Shanghai'],
      ['u005', 'eve', 'Engineering', 2, 'Junior Engineer', 'Guangzhou'],
      ['u006', 'frank', 'Finance', 5, 'CFO', 'Beijing'],
    ],
  },
  {
    name: 'resources',
    schema: '{ resource_id: String => resource_name: String, resource_type: String, classification: String, owner_dept: String, sensitivity: String }',
    columns: ['resource_id', 'resource_name', 'resource_type', 'classification', 'owner_dept', 'sensitivity'],
    rows: [
      ['r001', 'Salary Database', 'database', 'confidential', 'Finance', 'high'],
      ['r002', 'Source Code Repo', 'repository', 'internal', 'Engineering', 'medium'],
      ['r003', 'Employee Records', 'database', 'confidential', 'HR', 'high'],
      ['r004', 'Marketing Materials', 'files', 'public', 'Marketing', 'low'],
      ['r005', 'Financial Reports', 'documents', 'confidential', 'Finance', 'high'],
      ['r006', 'Project Documents', 'documents', 'internal', 'Engineering', 'medium'],
      ['r007', 'Public Website', 'website', 'public', 'Marketing', 'low'],
    ],
  },
  {
    name: 'policies',
    schema: '{ policy_id: String => policy_name: String, description: String, enabled: Bool }',
    columns: ['policy_id', 'policy_name', 'description', 'enabled'],
    rows: [
      ['p001', 'High-Level Access', '5级及以上员工可访问所有内部资源', true],
      ['p002', 'Department Match', '员工可访问本部门的资源', true],
      ['p003', 'Public Access', '所有人可访问公开资源', true],
      ['p004', 'Finance Confidential', '财务部4级以上可访问财务机密', true],
      ['p005', 'Engineering Internal', '工程部员工可访问工程内部资源', true],
      ['p006', 'Location Restriction', '只有北京员工可访问高敏感资源', true],
    ],
  },
  {
    name: 'policy_conditions',
    schema: '{ policy_id: String, condition_type: String, attribute_name: String, operator: String, attribute_value: String, priority: Int => }',
    columns: ['policy_id', 'condition_type', 'attribute_name', 'operator', 'attribute_value', 'priority'],
    rows: [
      ['p001', 'user', 'level', '>=', '5', 1],
      ['p001', 'resource', 'classification', '==', 'internal', 1],
      ['p002', 'user', 'department', '==', 'same_as_resource', 2],
      ['p003', 'resource', 'classification', '==', 'public', 3],
      ['p004', 'user', 'department', '==', 'Finance', 4],
      ['p004', 'user', 'level', '>=', '4', 4],
      ['p004', 'resource', 'owner_dept', '==', 'Finance', 4],
      ['p004', 'resource', 'classification', '==', 'confidential', 4],
      ['p005', 'user', 'department', '==', 'Engineering', 5],
      ['p005', 'resource', 'owner_dept', '==', 'Engineering', 5],
      ['p006', 'user', 'location', '==', 'Beijing', 6],
      ['p006', 'resource', 'sensitivity', '==', 'high', 6],
    ],
  },
  {
    name: 'actions',
    schema: '{ action_id: String => action_name: String, description: String }',
    columns: ['action_id', 'action_name', 'description'],
    rows: [
      ['a001', 'read', '读取'],
      ['a002', 'write', '写入'],
      ['a003', 'delete', '删除'],
      ['a004', 'execute', '执行'],
    ],
  },
  {
    name: 'policy_actions',
    schema: '{ policy_id: String, action_id: String => }',
    columns: ['policy_id', 'action_id'],
    rows: [
      ['p001', 'a001'],
      ['p001', 'a002'],
      ['p002', 'a001'],
      ['p003', 'a001'],
      ['p004', 'a001'],
      ['p004', 'a002'],
      ['p005', 'a001'],
      ['p005', 'a002'],
      ['p005', 'a004'],
      ['p006', 'a001'],
    ],
  },
];

async function runRbacUserPermissions(db, params) {
  const username = String(params?.username || '').trim();
  const bind = {};
  const filters = [];
  if (username) {
    bind.username = username;
    filters.push('username == $username');
  }
  const filterSql = filters.length ? `,\n      ${filters.join(',\n      ')}` : '';

  const detailQuery = `
?[user_id, username, role_id, role_name, permission_id, resource, action, description] :=
  *users[user_id, username, _, _],
  *user_roles[user_id, role_id],
  *roles[role_id, role_name, _],
  *role_permissions[role_id, permission_id],
  *permissions[permission_id, resource, action, description]
  ${filterSql}
  `;

  const statQuery = `
?[user_id, username, count(permission_id)] :=
  *users[user_id, username, _, _],
  *user_roles[user_id, role_id],
  *role_permissions[role_id, permission_id]
  ${filterSql}
  `;

  const detail = await db.run(detailQuery, bind);
  const stats = await db.run(statQuery, bind);

  return {
    sections: [
      { id: 'user-permissions', label: '用户权限详情', query: detailQuery.trim(), table: cozoResultToTable(detail) },
      { id: 'user-permission-count', label: '用户权限统计', query: statQuery.trim(), table: cozoResultToTable(stats) },
    ],
  };
}

async function runRbacRolePermissions(db, params) {
  const roleName = String(params?.roleName || '').trim();
  const bind = {};
  const filters = [];
  if (roleName) {
    bind.roleName = roleName;
    filters.push('role_name == $roleName');
  }
  const filterSql = filters.length ? `,\n      ${filters.join(',\n      ')}` : '';

  const detailQuery = `
?[role_id, role_name, role_description, permission_id, resource, action, permission_description] :=
  *roles[role_id, role_name, role_description],
  *role_permissions[role_id, permission_id],
  *permissions[permission_id, resource, action, permission_description]
  ${filterSql}
  `;

  const statQuery = `
?[role_id, role_name, count(permission_id)] :=
  *roles[role_id, role_name, _],
  *role_permissions[role_id, permission_id]
  ${filterSql}
  `;

  const detail = await db.run(detailQuery, bind);
  const stats = await db.run(statQuery, bind);

  return {
    sections: [
      { id: 'role-permissions', label: '角色权限详情', query: detailQuery.trim(), table: cozoResultToTable(detail) },
      { id: 'role-permission-count', label: '角色权限统计', query: statQuery.trim(), table: cozoResultToTable(stats) },
    ],
  };
}

async function runRbacResourcePermissions(db, params) {
  const resource = String(params?.resource || '').trim();
  const action = String(params?.action || '').trim();
  const bind = {};
  const filters = [];
  if (resource) {
    bind.resource = resource;
    filters.push('resource == $resource');
  }
  if (action) {
    bind.action = action;
    filters.push('action == $action');
  }
  const filterSql = filters.length ? `,\n      ${filters.join(',\n      ')}` : '';

  const detailQuery = `
?[resource, action, user_id, username, role_id, role_name] :=
  *permissions[permission_id, resource, action, _],
  *role_permissions[role_id, permission_id],
  *roles[role_id, role_name, _],
  *user_roles[user_id, role_id],
  *users[user_id, username, _, _]
  ${filterSql}
  `;

  const statQuery = `
?[resource, action, count(user_id)] :=
  *permissions[permission_id, resource, action, _],
  *role_permissions[role_id, permission_id],
  *user_roles[user_id, role_id]
  ${filterSql}
  `;

  const detail = await db.run(detailQuery, bind);
  const stats = await db.run(statQuery, bind);

  return {
    sections: [
      { id: 'resource-access', label: '资源访问详情', query: detailQuery.trim(), table: cozoResultToTable(detail) },
      { id: 'resource-stats', label: '资源访问统计', query: statQuery.trim(), table: cozoResultToTable(stats) },
    ],
  };
}

async function runRbacCheckPermission(db, params) {
  const username = String(params?.username || '').trim();
  const resource = String(params?.resource || '').trim();
  const action = String(params?.action || '').trim();
  if (!username || !resource || !action) {
    throw new Error('username/resource/action 为必填参数');
  }

  const pathQuery = `
?[user_id, username, role_id, role_name, permission_id, resource, action] :=
  *users[user_id, username, _, _],
  username == $username,
  *user_roles[user_id, role_id],
  *roles[role_id, role_name, _],
  *role_permissions[role_id, permission_id],
  *permissions[permission_id, resource, action, _],
  resource == $resource,
  action == $action
  `;

  const pathResult = await db.run(pathQuery, { username, resource, action });
  const hasPermission = Array.isArray(pathResult.rows) && pathResult.rows.length > 0;

  return {
    sections: [
      {
        id: 'check-result',
        label: '权限检查结果',
        query: 'hasPermission = permissionPath.rows.length > 0',
        table: objectRowTable({ username, resource, action, hasPermission }),
      },
      {
        id: 'permission-path',
        label: '权限授予路径',
        query: pathQuery.trim(),
        table: cozoResultToTable(pathResult),
      },
    ],
  };
}

async function runAbacUserAccess(db, params) {
  const username = String(params?.username || '').trim();
  const actionName = String(params?.actionName || '').trim();
  const bind = {};
  const filters = [];
  if (username) {
    bind.username = username;
    filters.push('username == $username');
  }
  if (actionName) {
    bind.actionName = actionName;
    filters.push('action_name == $actionName');
  }
  const filterSql = filters.length ? `,\n      ${filters.join(',\n      ')}` : '';

  const detailQuery = `
?[user_id, username, resource_id, resource_name, policy_id, policy_name, matched_conditions] :=
  *users[user_id, username, department, level, job_title, location],
  *resources[resource_id, resource_name, resource_type, classification, owner_dept, sensitivity],
  *policies[policy_id, policy_name, _, enabled],
  enabled == true,
  *policy_actions[policy_id, action_id],
  *actions[action_id, action_name, _],
  matched_conditions = 'See policy conditions for details'
  ${filterSql}
  `;

  const statQuery = `
?[username, department, count(resource_id)] :=
  *users[user_id, username, department, _, _, _],
  *resources[resource_id, _, _, classification, owner_dept, _],
  or(department == owner_dept, classification == 'public')
  ${username ? ',\n      username == $username' : ''}
  `;

  const detail = await db.run(detailQuery, bind);
  const stats = await db.run(statQuery, username ? { username } : {});

  return {
    sections: [
      { id: 'user-access', label: '用户可访问资源', query: detailQuery.trim(), table: cozoResultToTable(detail) },
      { id: 'access-stats', label: '访问统计', query: statQuery.trim(), table: cozoResultToTable(stats) },
    ],
  };
}

async function runAbacResourcePolicies(db, params) {
  const resourceName = String(params?.resourceName || '').trim();
  const bind = {};
  const filterSql = resourceName ? ',\n      resource_name == $resourceName' : '';
  if (resourceName) bind.resourceName = resourceName;

  const detailQuery = `
?[resource_id, resource_name, policy_id, policy_name, condition_type, attribute_name, operator, attribute_value] :=
  *resources[resource_id, resource_name, resource_type, classification, owner_dept, sensitivity],
  *policy_conditions[policy_id, condition_type, attribute_name, operator, attribute_value, _],
  *policies[policy_id, policy_name, _, enabled],
  enabled == true
  ${filterSql}
  `;

  const statQuery = `
?[resource_name, resource_type, count(policy_id)] :=
  *resources[resource_id, resource_name, resource_type, _, _, _],
  *policy_conditions[policy_id, _, _, _, _, _]
  ${filterSql}
  `;

  const detail = await db.run(detailQuery, bind);
  const stats = await db.run(statQuery, bind);

  return {
    sections: [
      { id: 'resource-policies', label: '资源访问策略', query: detailQuery.trim(), table: cozoResultToTable(detail) },
      { id: 'policy-stats', label: '策略统计', query: statQuery.trim(), table: cozoResultToTable(stats) },
    ],
  };
}

async function runAbacPolicyDetails(db, params) {
  const policyName = String(params?.policyName || '').trim();
  const bind = {};
  const filterSql = policyName ? ',\n      policy_name == $policyName' : '';
  if (policyName) bind.policyName = policyName;

  const conditionsQuery = `
?[policy_id, policy_name, condition_type, attribute_name, operator, attribute_value, priority] :=
  *policies[policy_id, policy_name, description, enabled],
  *policy_conditions[policy_id, condition_type, attribute_name, operator, attribute_value, priority]
  ${filterSql}
  `;

  const actionsQuery = `
?[policy_id, policy_name, action_id, action_name, description] :=
  *policies[policy_id, policy_name, _, enabled],
  *policy_actions[policy_id, action_id],
  *actions[action_id, action_name, description]
  ${filterSql}
  `;

  const conditions = await db.run(conditionsQuery, bind);
  const actions = await db.run(actionsQuery, bind);

  return {
    sections: [
      { id: 'policy-conditions', label: '策略条件', query: conditionsQuery.trim(), table: cozoResultToTable(conditions) },
      { id: 'policy-actions', label: '策略操作', query: actionsQuery.trim(), table: cozoResultToTable(actions) },
    ],
  };
}

async function runAbacCheckAccess(db, params) {
  const username = String(params?.username || '').trim();
  const resourceName = String(params?.resourceName || '').trim();
  const actionName = String(params?.actionName || '').trim();
  if (!username || !resourceName || !actionName) {
    throw new Error('username/resourceName/actionName 为必填参数');
  }

  const userQuery = `
?[user_id, username, department, level, job_title, location] :=
  *users[user_id, username, department, level, job_title, location],
  username == $username
  `;

  const resourceQuery = `
?[resource_id, resource_name, resource_type, classification, owner_dept, sensitivity] :=
  *resources[resource_id, resource_name, resource_type, classification, owner_dept, sensitivity],
  resource_name == $resourceName
  `;

  const userInfo = await db.run(userQuery, { username });
  const resourceInfo = await db.run(resourceQuery, { resourceName });

  if (!userInfo.rows?.length || !resourceInfo.rows?.length) {
    return {
      sections: [
        {
          id: 'check-result',
          label: '访问检查结果',
          query: '如果用户或资源不存在，则拒绝访问',
          table: objectRowTable({ username, resourceName, actionName, hasAccess: false, reason: 'User or resource not found' }),
        },
      ],
    };
  }

  const user = {
    department: userInfo.rows[0][2],
    level: userInfo.rows[0][3],
    location: userInfo.rows[0][5],
  };

  const resource = {
    classification: resourceInfo.rows[0][3],
    owner_dept: resourceInfo.rows[0][4],
    sensitivity: resourceInfo.rows[0][5],
  };

  let hasAccess = false;
  let reason = 'No matching policy';

  if (resource.classification === 'public' && actionName === 'read') {
    hasAccess = true;
    reason = 'Public Access (p003)';
  }
  if (user.department === resource.owner_dept) {
    hasAccess = true;
    reason = 'Department Match (p002)';
  }
  if (user.level >= 5 && resource.classification === 'internal') {
    hasAccess = true;
    reason = 'High-Level Access (p001)';
  }
  if (resource.sensitivity === 'high' && user.location !== 'Beijing') {
    hasAccess = false;
    reason = 'Location Restriction Failed (p006)';
  }

  const matchedPoliciesQuery = `
?[policy_id, policy_name, description] :=
  *policies[policy_id, policy_name, description, enabled],
  enabled == true,
  *policy_actions[policy_id, action_id],
  *actions[action_id, action_name, _],
  action_name == $actionName
  `;

  const matchedPolicies = await db.run(matchedPoliciesQuery, { actionName });

  return {
    sections: [
      {
        id: 'check-result',
        label: '访问检查结果',
        query: '规则计算（public/dept/high-level/location）',
        table: objectRowTable({ username, resourceName, actionName, hasAccess, reason }),
      },
      {
        id: 'user-attributes',
        label: '用户属性',
        query: userQuery.trim(),
        table: objectRowTable({ username, ...user }),
      },
      {
        id: 'resource-attributes',
        label: '资源属性',
        query: resourceQuery.trim(),
        table: objectRowTable({ resourceName, ...resource }),
      },
      {
        id: 'matched-policies',
        label: '适用策略',
        query: matchedPoliciesQuery.trim(),
        table: cozoResultToTable(matchedPolicies),
      },
    ],
  };
}

// ── Timeline (时间轴/时态) model ──────────────────────────────────────

const timelineDefaultTables = [
  {
    name: 'dept_permissions',
    schema: '{ dept_code: String, member_id: String, effective_date: Validity => name: String }',
    columns: ['dept_code', 'member_id', 'effective_date', 'name'],
    rows: [
      ['D01', 'm:zhang', [20230101, true], '张三'],
      ['D01', 'm:li', [20230601, true], '李四'],
      ['D01', 'm:zhang', [20240101, false], '张三'],
      ['D01', 'm:wang', [20240101, true], '王五'],
      ['D01', 'm:li', [20240601, true], '李四'],
      ['D01', 'm:qian', [20240601, true], '钱九'],
      ['D02', 'm:zhao', [20230101, true], '赵六'],
      ['D02', 'm:sun', [20230601, true], '孙七'],
      ['D02', 'm:zhao', [20240101, false], '赵六'],
      ['D02', 'm:zhou', [20240101, true], '周八'],
      ['D02', 'm:sun', [20240601, false], '孙七'],
      ['D02', 'm:wu', [20240601, true], '吴十'],
    ],
  },
];

async function seedTimeline(db, tables) {
  await updateData(db, tables || timelineDefaultTables);
}

async function runTimelinePointQuery(db, params) {
  const queryDate = parseInt(params?.queryDate, 10) || 20240101;
  const deptCode = String(params?.deptCode || '').trim();

  const bind = { queryDate };
  const deptFilter = deptCode ? ',\n  dept_code == $deptCode' : '';
  if (deptCode) bind.deptCode = deptCode;

  const query = `
?[dept_code, member_id, date, name] :=
  *dept_permissions{ dept_code, member_id, effective_date, name @ $queryDate },
  date = to_int(effective_date)
  ${deptFilter}

:order dept_code, member_id, date
  `;

  const statQuery = `
?[dept_code, count(member_id)] :=
  *dept_permissions{ dept_code, member_id, effective_date, name @ $queryDate }
  ${deptFilter}

:order dept_code
  `;

  const result = await db.run(query, bind);
  const statResult = await db.run(statQuery, bind);

  return {
    sections: [
      { id: 'point-permissions', label: '时间点权限快照', query: query.trim(), table: cozoResultToTable(result) },
      { id: 'point-stats', label: '部门权限人数统计', query: statQuery.trim(), table: cozoResultToTable(statResult) },
    ],
  };
}

async function runTimelineHistory(db, params) {
  const nameKeyword = String(params?.nameKeyword || '').trim();
  const deptCode = String(params?.deptCode || '').trim();

  const bind = {};
  const filters = [];
  if (nameKeyword) {
    bind.nameKeyword = nameKeyword;
    filters.push('name == $nameKeyword');
  }
  if (deptCode) {
    bind.deptCode = deptCode;
    filters.push('dept_code == $deptCode');
  }
  const filterSql = filters.length ? ',\n  ' + filters.join(',\n  ') : '';

  const historyQuery = `
?[dept_code, member_id, date, name] :=
  *dept_permissions{ dept_code, member_id, effective_date, name },
  date = to_int(effective_date)
  ${filterSql}

:order dept_code, member_id, date
  `;

  const summaryQuery = `
?[dept_code, member_id, name, count(date)] :=
  *dept_permissions{ dept_code, member_id, effective_date, name },
  date = to_int(effective_date)
  ${filterSql}

:order dept_code, member_id
  `;

  const history = await db.run(historyQuery, bind);
  const summary = await db.run(summaryQuery, bind);

  return {
    sections: [
      { id: 'history-detail', label: '权限变更历史', query: historyQuery.trim(), table: cozoResultToTable(history) },
      { id: 'history-summary', label: '变更次数统计', query: summaryQuery.trim(), table: cozoResultToTable(summary) },
    ],
  };
}

async function runTimelineCompare(db, params) {
  const date1 = parseInt(params?.date1, 10) || 20230101;
  const date2 = parseInt(params?.date2, 10) || 20240101;
  const deptCode = String(params?.deptCode || '').trim();

  const bind = { date1, date2 };
  const deptFilter = deptCode ? ',\n  dept_code == $deptCode' : '';
  if (deptCode) bind.deptCode = deptCode;

  const snap1Query = `
?[dept_code, member_id, date, name] :=
  *dept_permissions{ dept_code, member_id, effective_date, name @ $date1 },
  date = to_int(effective_date)
  ${deptFilter}

:order dept_code, member_id, date
  `;

  const snap2Query = `
?[dept_code, member_id, date, name] :=
  *dept_permissions{ dept_code, member_id, effective_date, name @ $date2 },
  date = to_int(effective_date)
  ${deptFilter}

:order dept_code, member_id, date
  `;

  const snap1 = await db.run(snap1Query, bind);
  const snap2 = await db.run(snap2Query, bind);

  return {
    sections: [
      { id: 'snapshot-date1', label: `快照 @ ${date1}`, query: snap1Query.trim(), table: cozoResultToTable(snap1) },
      { id: 'snapshot-date2', label: `快照 @ ${date2}`, query: snap2Query.trim(), table: cozoResultToTable(snap2) },
    ],
  };
}

const timelineModel = {
  modelId: 'timeline',
  label: '时间轴/时态权限',
  description: '基于生效日期的部门人员权限时态模型，支持时间点查询、历史追溯和快照对比',
  defaultTables: timelineDefaultTables,
  setup: seedTimeline,
  queries: [
    {
      queryId: 'pointQuery',
      label: '时间点权限查询',
      meaning: '查询某个时间点各部门的有效权限人员（最新快照）',
      cozo: `
?[dept_code, member_id, date, name] :=
  *dept_permissions{ dept_code, member_id, effective_date, name @ $queryDate },
  date = to_int(effective_date)
      `.trim(),
      params: [
        { key: 'queryDate', label: '查询日期', placeholder: '20240101', required: false },
        { key: 'deptCode', label: '部门代码', placeholder: 'D01', required: false },
      ],
      run: runTimelinePointQuery,
    },
    {
      queryId: 'history',
      label: '权限变更历史',
      meaning: '追溯某人或某部门的权限变更全过程',
      cozo: `
?[dept_code, member_id, date, name] :=
  *dept_permissions{ dept_code, member_id, effective_date, name },
  date = to_int(effective_date),
  name == $nameKeyword

:order dept_code, member_id, date
      `.trim(),
      params: [
        { key: 'nameKeyword', label: '姓名', placeholder: '张三', required: false },
        { key: 'deptCode', label: '部门代码', placeholder: 'D01', required: false },
      ],
      run: runTimelineHistory,
    },
    {
      queryId: 'compare',
      label: '快照对比',
      meaning: '对比两个时间点的有效权限人员差异',
      cozo: `
?[dept_code, member_id, date, name] :=
  *dept_permissions{ dept_code, member_id, effective_date, name @ $date },
  date = to_int(effective_date)
      `.trim(),
      params: [
        { key: 'date1', label: '日期1', placeholder: '20230101', required: false },
        { key: 'date2', label: '日期2', placeholder: '20240101', required: false },
        { key: 'deptCode', label: '部门代码', placeholder: 'D01', required: false },
      ],
      run: runTimelineCompare,
    },
  ],
};

const rbacModel = {
  modelId: 'rbac',
  label: 'RBAC（基于角色）',
  description: '用户-角色-权限三层关系模型',
  defaultTables: rbacDefaultTables,
  setup: seedRbac,
  queries: [
    {
      queryId: 'userPermissions',
      label: '用户权限查询',
      meaning: '查询指定用户（或全部用户）的角色与权限明细和统计',
      cozo: `
?[user_id, username, role_id, role_name, permission_id, resource, action, description] :=
  *users[user_id, username, _, _],
  *user_roles[user_id, role_id],
  *roles[role_id, role_name, _],
  *role_permissions[role_id, permission_id],
  *permissions[permission_id, resource, action, description],
  username == $username
      `.trim(),
      params: [
        { key: 'username', label: '用户名', placeholder: 'admin / alice', required: false },
      ],
      run: runRbacUserPermissions,
    },
    {
      queryId: 'rolePermissions',
      label: '角色权限查询',
      meaning: '查询指定角色（或全部角色）的权限明细和统计',
      cozo: `
?[role_id, role_name, role_description, permission_id, resource, action, permission_description] :=
  *roles[role_id, role_name, role_description],
  *role_permissions[role_id, permission_id],
  *permissions[permission_id, resource, action, permission_description],
  role_name == $roleName
      `.trim(),
      params: [
        { key: 'roleName', label: '角色名', placeholder: 'Admin / Editor', required: false },
      ],
      run: runRbacRolePermissions,
    },
    {
      queryId: 'resourcePermissions',
      label: '资源权限查询',
      meaning: '查询指定资源/操作可被哪些用户通过哪些角色访问',
      cozo: `
?[resource, action, user_id, username, role_id, role_name] :=
  *permissions[permission_id, resource, action, _],
  *role_permissions[role_id, permission_id],
  *roles[role_id, role_name, _],
  *user_roles[user_id, role_id],
  *users[user_id, username, _, _],
  resource == $resource,
  action == $action
      `.trim(),
      params: [
        { key: 'resource', label: '资源', placeholder: 'user / article', required: false },
        { key: 'action', label: '操作', placeholder: 'read / create', required: false },
      ],
      run: runRbacResourcePermissions,
    },
    {
      queryId: 'checkPermission',
      label: '权限检查',
      meaning: '检查用户是否对指定资源具备指定操作权限，并展示授予路径',
      cozo: `
?[user_id, username, role_id, role_name, permission_id, resource, action] :=
  *users[user_id, username, _, _],
  username == $username,
  *user_roles[user_id, role_id],
  *roles[role_id, role_name, _],
  *role_permissions[role_id, permission_id],
  *permissions[permission_id, resource, action, _],
  resource == $resource,
  action == $action
      `.trim(),
      params: [
        { key: 'username', label: '用户名', placeholder: 'admin', required: true },
        { key: 'resource', label: '资源', placeholder: 'article', required: true },
        { key: 'action', label: '操作', placeholder: 'create', required: true },
      ],
      run: runRbacCheckPermission,
    },
  ],
};

const abacModel = {
  modelId: 'abac',
  label: 'ABAC（基于属性）',
  description: '用户属性 + 资源属性 + 策略条件的动态访问控制',
  defaultTables: abacDefaultTables,
  setup: seedAbac,
  queries: [
    {
      queryId: 'userAccess',
      label: '用户访问查询',
      meaning: '按用户与操作筛选可访问资源和统计',
      cozo: `
?[user_id, username, resource_id, resource_name, policy_id, policy_name, matched_conditions] :=
  *users[user_id, username, department, level, job_title, location],
  *resources[resource_id, resource_name, resource_type, classification, owner_dept, sensitivity],
  *policies[policy_id, policy_name, _, enabled],
  enabled == true,
  *policy_actions[policy_id, action_id],
  *actions[action_id, action_name, _],
  username == $username,
  action_name == $actionName
      `.trim(),
      params: [
        { key: 'username', label: '用户名', placeholder: 'alice / bob', required: false },
        { key: 'actionName', label: '操作', placeholder: 'read / write', required: false },
      ],
      run: runAbacUserAccess,
    },
    {
      queryId: 'resourcePolicies',
      label: '资源策略查询',
      meaning: '查看某个资源可匹配的策略条件',
      cozo: `
?[resource_id, resource_name, policy_id, policy_name, condition_type, attribute_name, operator, attribute_value] :=
  *resources[resource_id, resource_name, resource_type, classification, owner_dept, sensitivity],
  *policy_conditions[policy_id, condition_type, attribute_name, operator, attribute_value, _],
  *policies[policy_id, policy_name, _, enabled],
  enabled == true,
  resource_name == $resourceName
      `.trim(),
      params: [
        { key: 'resourceName', label: '资源名', placeholder: 'Salary Database', required: false },
      ],
      run: runAbacResourcePolicies,
    },
    {
      queryId: 'policyDetails',
      label: '策略详情',
      meaning: '查看策略条件与策略绑定的操作',
      cozo: `
?[policy_id, policy_name, condition_type, attribute_name, operator, attribute_value, priority] :=
  *policies[policy_id, policy_name, description, enabled],
  *policy_conditions[policy_id, condition_type, attribute_name, operator, attribute_value, priority],
  policy_name == $policyName
      `.trim(),
      params: [
        { key: 'policyName', label: '策略名', placeholder: 'High-Level Access', required: false },
      ],
      run: runAbacPolicyDetails,
    },
    {
      queryId: 'checkAccess',
      label: '访问权限检查',
      meaning: '基于用户属性和资源属性评估访问许可，并展示适用策略',
      cozo: `
?[user_id, username, department, level, job_title, location] :=
  *users[user_id, username, department, level, job_title, location],
  username == $username

?[resource_id, resource_name, resource_type, classification, owner_dept, sensitivity] :=
  *resources[resource_id, resource_name, resource_type, classification, owner_dept, sensitivity],
  resource_name == $resourceName

?[policy_id, policy_name, description] :=
  *policies[policy_id, policy_name, description, enabled],
  enabled == true,
  *policy_actions[policy_id, action_id],
  *actions[action_id, action_name, _],
  action_name == $actionName
      `.trim(),
      params: [
        { key: 'username', label: '用户名', placeholder: 'alice', required: true },
        { key: 'resourceName', label: '资源名', placeholder: 'Salary Database', required: true },
        { key: 'actionName', label: '操作', placeholder: 'read', required: true },
      ],
      run: runAbacCheckAccess,
    },
  ],
};

const permissionModels = [rbacModel, abacModel, timelineModel];
const permissionModelMap = Object.fromEntries(permissionModels.map((m) => [m.modelId, m]));

module.exports = {
  permissionModels,
  permissionModelMap,
};
