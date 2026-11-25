#!/usr/bin/env python3
"""Role-based access test against a running stack: every built-in group is exercised against a matrix of endpoints (allowed = anything but
403, denied = exactly 403), then real flows per role, custom groups, live permission changes and the safety rules. Cleans up after itself.
Usage: scripts/smoke-roles.py            (reads .env like the other smoke scripts; needs the Docker db service for cleanup)"""
import json, os, subprocess, sys, urllib.request, urllib.error, random, pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
env = dict(os.environ)
if (ROOT / '.env').exists():
    for line in (ROOT / '.env').read_text().splitlines():
        if '=' in line and not line.lstrip().startswith('#'):
            k, v = line.split('=', 1); env.setdefault(k.strip(), v.strip().strip('"').strip("'"))
URL = env.get('API_URL', 'http://localhost:8080') + '/api/v1'
ADMIN, ADMIN_PW = env.get('ADMIN_USER', env.get('BOOTSTRAP_ADMIN_USER')), env.get('ADMIN_PASSWORD', env.get('BOOTSTRAP_ADMIN_PASSWORD'))
passed = failed = 0

def call(method, path, body=None, token=None):
    req = urllib.request.Request(URL + path, method=method, data=None if body is None else json.dumps(body).encode(), headers={'content-type': 'application/json', **({'Authorization': 'Bearer ' + token} if token else {})})
    try:
        with urllib.request.urlopen(req) as r:
            raw = r.read()
            try: return r.status, (json.loads(raw) if raw else None)
            except ValueError: return r.status, raw   # a file download
    except urllib.error.HTTPError as e:
        raw = e.read()
        try: return e.code, json.loads(raw)
        except Exception: return e.code, raw
def check(name, ok, detail=''):
    global passed, failed
    if ok: passed += 1; print('  ok  ', name)
    else: failed += 1; print('  FAIL', name, detail)
def login(code, pw):
    s, b = call('POST', '/auth/login', {'userCode': code, 'password': pw, 'deviceName': 'roles-test', 'platform': 'desktop'})
    return b['accessToken'] if s == 200 else None
def sql(q):
    subprocess.run(['docker', 'compose', '--project-directory', str(ROOT), 'exec', '-T', '-e', 'PGPASSWORD=' + env['POSTGRES_PASSWORD'], 'db', 'psql', '-X', '-q', '-U', env.get('POSTGRES_USER', 'reflectopatronus_admin'), '-d', env.get('POSTGRES_DB', 'reflectopatronus'), '-c', q], capture_output=True)

admin = login(ADMIN, ADMIN_PW)
LOSS_BACKUP = call('GET', '/catalog/loss', None, admin)[1]   # restored at the end, whatever happens
A = lambda m, p, b=None: call(m, p, b, admin)
tag = str(random.randint(1000, 9999)); PW = 'role test password ' + tag
groups = {g['code']: g for g in A('GET', '/groups')[1]}
users = {}      # group code -> (id, token)
cust = None; created_groups = []

def mkuser(code, group_ids):
    s, b = A('POST', '/users', {'code': code, 'name': code, 'password': PW, 'groupIds': group_ids, 'active': True})
    assert s == 201, (s, b)
    return b['id'], login(code, PW)

try:
    cust = A('POST', '/customers', {'number': 'ZZR' + tag, 'type': 'A', 'lastName': 'Roles Test', 'taxExempt': True, 'phones': []})[1]['id']
    for code in ['managers', 'counter', 'sales', 'shop', 'readonly']:
        users[code] = mkuser(f'zzr{code[:4]}{tag}', [groups[code]['id']])
    users['administrators'] = (None, admin)
    users['nogroup'] = (None, None)
    s, b = A('POST', '/users', {'code': f'zzrnone{tag}', 'name': 'none', 'password': PW, 'groupIds': [], 'active': True})
    check('a user must be given at least one group', s == 400, (s, b))
    uid, _ = mkuser(f'zzrnone{tag}', [groups['shop']['id']])
    A('PUT', f'/users/{uid}', {'name': 'none', 'active': True, 'groupIds': []})   # now strip the group: signs in but has nothing
    users['nogroup'] = (uid, login(f'zzrnone{tag}', PW))

    # matrix: (label, method, path, body, permissions of which ANY allows it)
    M = [
        ('list customers', 'GET', '/customers?limit=1', None, ['customers.read']),
        ('add customer', 'POST', '/customers', {}, ['customers.write']),
        ('delete customer', 'DELETE', '/customers/999999999', None, ['customers.delete']),
        ('special prices read', 'GET', f'/customers/{cust}/prices', None, ['customers.read']),
        ('special prices write', 'PUT', '/customers/999999999/prices', [], ['customers.write']),
        ('list documents', 'GET', '/documents?limit=1', None, ['documents.read']),
        ('open document', 'GET', '/documents/999999999', None, ['documents.read']),
        ('mark printed', 'POST', '/documents/999999999/printed', None, ['documents.read']),
        ('price a draft', 'POST', '/documents/calculate', {}, ['quotes.write', 'workorders.write', 'invoices.write']),
        ('create quote', 'POST', '/documents', {}, ['quotes.write']),
        ('void document', 'POST', '/documents/999999999/void', None, ['documents.void']),
        ('make work order', 'POST', '/documents/999999999/convert', {'to': 'W'}, ['workorders.write']),
        ('make invoice', 'POST', '/documents/999999999/convert', {'to': 'I'}, ['invoices.write']),
        ('make credit memo', 'POST', '/documents/999999999/convert', {'to': 'C'}, ['credits.write']),
        ('receivables list', 'GET', '/receivables', None, ['receivables.read']),
        ('customer account', 'GET', f'/customers/{cust}/account', None, ['receivables.read']),
        ('payments list', 'GET', '/payments', None, ['receivables.read']),
        ('receive payment', 'POST', '/payments', {}, ['payments.write']),
        ('void payment', 'POST', '/payments/999999999/void', None, ['payments.void']),
        ('apply credits', 'POST', '/customers/999999999/apply-credits', None, ['payments.write']),
        ('add part', 'POST', '/catalog/items', {}, ['catalog.write']),
        ('change waste table', 'PUT', '/catalog/loss', [{'thickNum': 1, 'thickDen': 0, 'lossInt': 0, 'lossNum': 0, 'lossDen': 1}], ['catalog.write']),
        ('add category', 'POST', '/maintenance/price-categories', {}, ['maintenance.write']),
        ('add tax group', 'POST', '/tax-groups', {}, ['maintenance.write']),
        ('supplier pricing', 'GET', '/pricing/suppliers', None, ['pricing.manage']),
        ('run a report', 'GET', f'/reports/list-users', None, ['reports.view']),
        ('list reports', 'GET', '/reports', None, ['reports.view']),
        ('change company', 'PUT', '/company', {}, ['company.write']),
        ('list users', 'GET', '/users', None, ['users.manage']),
        ('list groups', 'GET', '/groups', None, ['users.manage']),
        ('permission list', 'GET', '/permissions', None, ['users.manage']),
        ('clean-up tool', 'POST', '/utilities/delete-quotes', {'dryRun': True}, ['utilities.cleanup']),
        ('update status', 'GET', '/updates', None, ['users.manage']),
        ('check for updates', 'POST', '/updates/dismiss', None, ['users.manage']),
        ('accounting overview', 'GET', '/accounting', None, ['accounting.manage']),
        ('accounting queue', 'GET', '/accounting/queue', None, ['accounting.manage']),
        ('accounting export', 'GET', '/accounting/export/iif?mark=false', None, ['accounting.manage']),
        ('inventory items', 'GET', '/inventory/items', None, ['inventory.manage']),
        ('inventory orders', 'GET', '/inventory/orders', None, ['inventory.manage']),
        ('inventory switch', 'PUT', '/inventory/status', {'on': False}, ['inventory.manage']),
        ('transfer files', 'GET', '/admin/transfer/files', None, ['transfer.manage']),
        ('start export', 'POST', '/admin/transfer/ticket', {'file': 'nope.rpb'}, ['transfer.manage']),
    ]
    OPEN = [('who am I', 'GET', '/me'), ('tax codes', 'GET', '/tax-codes'), ('tax groups', 'GET', '/tax-groups'), ('customer lookups', 'GET', '/lookups/customer'), ('company details', 'GET', '/company'),
            ('parts picker', 'GET', '/catalog/parts?q=a'), ('glass list', 'GET', '/catalog/items?q=a&source=G'), ('waste table', 'GET', '/catalog/loss'), ('salesmen', 'GET', '/maintenance/staff'), ('time zones', 'GET', '/timezones')]
    perms = {code: set(b for b in (A('GET', '/groups')[1] and [x for x in A('GET', '/groups')[1] if x['code'] == code][0]['permissions'])) for code in groups}
    perms['nogroup'] = set()

    print('permission matrix')
    for role in ['administrators', 'managers', 'counter', 'sales', 'shop', 'readonly', 'nogroup']:
        tok = users[role][1]
        have = perms[role]
        bad = []
        for label, m, p, body, need in M:
            s, _ = call(m, p, body, tok)
            allowed = '*' in have or any(n in have for n in need)
            if allowed and s in (403, 401): bad.append(f'{label}: expected allowed, got {s}')
            if not allowed and s != 403: bad.append(f'{label}: expected 403, got {s}')
        check(f'{role}: all {len(M)} gated endpoints behave', not bad, bad)
        ob = [l for l, m, p in OPEN if call(m, p, None, tok)[0] != 200]
        check(f'{role}: open reference data is readable', not ob, ob)
        s, me = call('GET', '/me', None, tok)
        check(f'{role}: /me lists exactly its permissions', sorted(me['permissions']) == sorted(have), (me['permissions'], have))
        check(f'{role}: isAdmin only for Administrators', me['isAdmin'] == (role == 'administrators'))

    print('flows by role')
    S, C, SH, R, MG = (users[k][1] for k in ['sales', 'counter', 'shop', 'readonly', 'managers'])
    line = {'source': 'labor', 'qty': 1, 'description': 'role test', 'amount': 100}
    s, q = call('POST', '/documents', {'customerId': cust, 'lines': [line]}, S); check('Sales writes a quote', s == 201, (s, q))
    qid = q['id']
    s, w = call('POST', f'/documents/{qid}/convert', {'to': 'W'}, S); check('Sales turns it into a work order', s == 200, (s, w))
    wid = w['id']
    s, _ = call('POST', f'/documents/{wid}/convert', {'to': 'I'}, S); check('Sales cannot make an invoice', s == 403)
    s, d = call('GET', f'/documents/{wid}', None, S)
    edit = {'customerId': cust, 'lines': [dict(line, amount=120)], 'updatedAt': d['updatedAt']}
    s, _ = call('PUT', f'/documents/{wid}', edit, S); check('Sales can edit a work order', s == 200, s)
    s, _ = call('POST', f'/documents/{wid}/void', None, S); check('Sales cannot void', s == 403)
    s, _ = call('POST', '/payments', {'customerId': cust, 'amount': 10}, S); check('Sales cannot take a payment', s == 403)
    s, _ = call('GET', '/receivables', None, S); check('Sales cannot see receivables', s == 403)

    s, i = call('POST', f'/documents/{wid}/convert', {'to': 'I'}, C); check('Counter makes the invoice', s == 200, (s, i))
    s, d = call('GET', f'/documents/{wid}', None, S)
    s, _ = call('PUT', f'/documents/{wid}', {'customerId': cust, 'lines': [dict(line, amount=130)], 'updatedAt': d['updatedAt']}, S); check('Sales cannot edit an invoice', s == 403, s)
    s, d = call('GET', f'/documents/{wid}', None, C)
    s, _ = call('PUT', f'/documents/{wid}', {'customerId': cust, 'lines': [dict(line, amount=130)], 'updatedAt': d['updatedAt']}, C); check('Counter can edit an invoice', s == 200, s)
    s, _ = call('PUT', f'/documents/{wid}', {'customerId': cust, 'lines': [line], 'updatedAt': d['updatedAt']}, SH); check('Shop cannot edit an invoice', s == 403, s)
    s, p = call('POST', '/payments', {'customerId': cust, 'amount': 50, 'applications': [{'documentId': wid, 'amount': 50}]}, C); check('Counter takes a payment', s == 201, (s, p))
    s, _ = call('POST', f'/payments/{p["id"]}/void', None, C); check('Counter cannot void a payment', s == 403)
    s, _ = call('POST', f'/payments/{p["id"]}/void', None, MG); check('Manager voids the payment', s == 204, s)
    s, cm = call('POST', f'/documents/{wid}/convert', {'to': 'C', 'lineSeqs': [1]}, C); check('Counter writes a credit memo', s == 200, (s, cm))
    s, _ = call('POST', f'/documents/{cm["id"]}/void', None, C); check('Counter cannot void a credit memo', s == 403)
    s, _ = call('POST', f'/documents/{cm["id"]}/void', None, MG); check('Manager voids the credit memo', s == 204, s)
    s, _ = call('POST', f'/documents/{wid}/void', None, MG); check('Manager voids the invoice', s == 204, s)
    s, _ = call('GET', f'/documents/{wid}', None, SH); check('Shop can look at it', s == 200)
    s, _ = call('POST', '/documents', {'customerId': cust, 'lines': [line]}, SH); check('Shop cannot write a quote', s == 403)
    s, _ = call('POST', '/customers', {'number': 'ZZR' + tag + 'x', 'type': 'A', 'lastName': 'x', 'phones': []}, SH); check('Shop cannot add a customer', s == 403)
    s, _ = call('PUT', f'/customers/{cust}', {}, R); check('Read only cannot change a customer', s == 403)
    s, r = call('GET', '/reports/customer-list', None, R); check('Read only runs reports', s == 200)
    s, _ = call('GET', '/reports/customer-list', None, S); check('Sales cannot run reports', s == 403)
    s, _ = call('PUT', '/catalog/loss', [{'thickNum': 1, 'thickDen': 0, 'lossInt': 0, 'lossNum': 0, 'lossDen': 1}], MG); check('Manager reaches the waste table (a bad row is a 400, not a 403)', s == 400, s)
    s, _ = call('POST', '/users', {'code': 'zzx', 'password': PW, 'groupIds': [1], 'active': True}, MG); check('Manager cannot add users', s == 403)
    s, _ = call('POST', '/groups', {'name': 'x', 'permissions': []}, MG); check('Manager cannot add groups', s == 403)

    print('groups: custom, live changes, safety rules')
    s, g = A('POST', '/groups', {'name': f'Estimators {tag}', 'description': 'test', 'permissions': ['customers.read', 'quotes.write', 'documents.read']}); check('create a custom group', s == 201, (s, g))
    gid = g['id']; created_groups.append(gid)
    check('duplicate group name refused', A('POST', '/groups', {'name': f'Estimators {tag}', 'permissions': []})[0] == 409)
    check('unknown permission refused', A('POST', '/groups', {'name': 'Bad ' + tag, 'permissions': ['made.up']})[0] == 400)
    check('"*" cannot be given to a custom group', A('POST', '/groups', {'name': 'Star ' + tag, 'permissions': ['*']})[0] == 400)
    check('a group needs a name', A('POST', '/groups', {'name': ' ', 'permissions': []})[0] == 400)
    eid, etok = mkuser(f'zzrest{tag}', [gid])
    s, me = call('GET', '/me', None, etok); check('member gets the custom group permissions', sorted(me['permissions']) == ['customers.read', 'documents.read', 'quotes.write'] and me['groups'] == [f'Estimators {tag}'], me)
    check('member can write a quote', call('POST', '/documents', {'customerId': cust, 'lines': [line]}, etok)[0] == 201)
    check('member cannot take a payment', call('POST', '/payments', {}, etok)[0] == 403)
    check('member cannot make a work order', call('POST', '/documents/999999999/convert', {'to': 'W'}, etok)[0] == 403)
    s, _ = A('PUT', f'/groups/{gid}', {'name': f'Estimators {tag}', 'description': 'test', 'permissions': ['customers.read', 'documents.read', 'workorders.write']}); check('change the group', s == 204, s)
    check('change applies at once: quote writing is gone', call('POST', '/documents', {'customerId': cust, 'lines': [line]}, etok)[0] == 403)
    check('change applies at once: work orders now allowed', call('POST', '/documents/999999999/convert', {'to': 'W'}, etok)[0] != 403)
    check('cannot delete a group that has members', A('DELETE', f'/groups/{gid}')[0] == 409)
    # two groups: union
    A('PUT', f'/users/{eid}', {'name': 'e', 'active': True, 'groupIds': [gid, groups['counter']['id']]})
    s, me = call('GET', '/me', None, etok); check('two groups give the union', 'payments.write' in me['permissions'] and 'workorders.write' in me['permissions'], me['permissions'])
    A('PUT', f'/users/{eid}', {'name': 'e', 'active': True, 'groupIds': [groups['shop']['id']]})
    check('removing the group removes the rights', call('POST', '/payments', {}, etok)[0] == 403)
    check('built-in group cannot be deleted', A('DELETE', f'/groups/{groups["shop"]["id"]}')[0] == 409)
    check('Administrators group cannot be edited', A('PUT', f'/groups/{groups["administrators"]["id"]}', {'name': 'x', 'permissions': []})[0] == 409)
    check('Administrators group cannot be deleted', A('DELETE', f'/groups/{groups["administrators"]["id"]}')[0] == 409)
    check('unknown group id refused for a user', A('PUT', f'/users/{eid}', {'name': 'e', 'active': True, 'groupIds': [32000]})[0] == 400)
    s, _ = A('PUT', f'/groups/{groups["readonly"]["id"]}', {'name': 'Read only', 'description': 'x', 'permissions': ['customers.read']}); check('a built-in group can be adjusted', s == 204)
    A('PUT', f'/groups/{groups["readonly"]["id"]}', {'name': 'Read only', 'description': groups['readonly']['description'], 'permissions': groups['readonly']['permissions']})   # put it back
    check('custom group deletes once empty', A('DELETE', f'/groups/{gid}')[0] == 204); created_groups.clear()

    me_id = call('GET', '/me', None, admin)[1]['id']
    s, ul = A('GET', '/users'); me_row = [u for u in ul if u['id'] == me_id][0]
    check('the administrator row shows isAdmin and the Administrators group', me_row['isAdmin'] and groups['administrators']['id'] in me_row['groupIds'])
    check('a non-admin row does not', not [u for u in ul if u['id'] == users['sales'][0]][0]['isAdmin'])
    check('you cannot remove your own Administrators membership', A('PUT', f'/users/{me_id}', {'name': 'x', 'active': True, 'groupIds': []})[0] == 409)
    check('you cannot deactivate yourself', A('PUT', f'/users/{me_id}', {'name': 'x', 'active': False, 'groupIds': [groups['administrators']['id']]})[0] == 409)
    # promote and demote: is_admin follows the group, last-admin rule holds
    A('PUT', f'/users/{users["sales"][0]}', {'name': 'x', 'active': True, 'groupIds': [groups['administrators']['id']]})
    check('promoting to Administrators grants everything at once', call('GET', '/users', None, S)[0] == 200 and call('GET', '/me', None, S)[1]['isAdmin'])
    A('PUT', f'/users/{users["sales"][0]}', {'name': 'x', 'active': True, 'groupIds': [groups['sales']['id']]})
    check('demoting takes it away at once', call('GET', '/users', None, S)[0] == 403)
    A('PUT', f'/users/{users["sales"][0]}', {'name': 'x', 'active': False, 'groupIds': [groups['sales']['id']]})
    check('a deactivated user is signed out', call('GET', '/me', None, S)[0] == 401)
    check('and cannot sign back in', login(f'zzrsale{tag}', PW) is None)
    check('no token at all is refused', call('GET', '/customers', None, None)[0] == 401)
finally:
    call('PUT', '/catalog/loss', LOSS_BACKUP, admin)
    sql("DELETE FROM ar_entry WHERE customer_id IN (SELECT id FROM customer WHERE number LIKE 'ZZR%'); DELETE FROM payment WHERE customer_id IN (SELECT id FROM customer WHERE number LIKE 'ZZR%'); "
        "DELETE FROM document WHERE customer_id IN (SELECT id FROM customer WHERE number LIKE 'ZZR%'); DELETE FROM customer WHERE number LIKE 'ZZR%'; "
        "DELETE FROM device_session WHERE user_id IN (SELECT id FROM app_user WHERE code LIKE 'zzr%'); DELETE FROM app_user WHERE code LIKE 'zzr%'; DELETE FROM system_state WHERE key = 'update.dismissed'; DELETE FROM app_group WHERE name LIKE 'Estimators %' OR name LIKE 'Bad %' OR name LIKE 'Star %'")

print(f'\npassed {passed}, failed {failed}'); sys.exit(1 if failed else 0)
