import { useEffect, useState } from 'react'
import * as api from '../api'
import { instant } from '../format'
import { useShell } from '../shell/WindowContext'
import { Field, Modal, Toolbar } from '../shell/ui'

/** The shop's own details, printed at the top of every document. */
export function CompanyInfo() {
  const { me, toast } = useShell(), admin = api.can(me, 'company.write')
  const [c, setC] = useState<(api.CompanyInfo & { timeZone?: string | null }) | null>(null); const [dirty, setDirty] = useState(false); const [error, setError] = useState('')
  const [logo, setLogo] = useState<string | null>(null)
  useEffect(() => { api.getCompany().then(setC).catch(e => setError(e.message)); api.getLogoDataUrl().then(setLogo) }, [])
  async function pickLogo(f: File | undefined) {
    if (!f) return
    if (f.size > 1024 * 1024) return setError('The logo is larger than 1 MB. Use a smaller image.')
    try { await api.putLogo(f); setLogo(await api.getLogoDataUrl()); setError(''); toast('Logo saved.') } catch (e) { setError((e as Error).message) }
  }
  async function dropLogo() { try { await api.deleteLogo(); setLogo(null); toast('Logo removed.') } catch (e) { setError((e as Error).message) } }
  if (!c) return <div className="form">{error ? <p className="formerr">{error}</p> : <p className="muted">Loading…</p>}</div>
  const set = (p: Partial<api.CompanyInfo>) => { setC({ ...c, ...p }); setDirty(true) }
  const txt = (k: keyof api.CompanyInfo, label: string, w = '20rem') => <Field label={label}><input value={(c[k] as string | null) ?? ''} onChange={e => set({ [k]: e.target.value || null })} style={{ width: w }} disabled={!admin} /></Field>
  async function save() { try { await api.putCompany(c!); setDirty(false); setError(''); toast('Saved.') } catch (e) { setError((e as Error).message) } }
  return (
    <div className="form">
      <Toolbar tools={[{ label: 'Save', icon: 'save', onClick: save, disabled: !admin || !dirty }]} />
      {error && <p className="formerr">{error}</p>}
      {txt('name', 'Company name')}{txt('address1', 'Address')}{txt('address2', '')}{txt('city', 'City', '14rem')}{txt('state', 'State', '4rem')}{txt('zip', 'ZIP', '8rem')}
      {txt('phone', 'Phone', '12rem')}{txt('fax', 'Fax', '12rem')}{txt('email', 'E-mail', '20rem')}
      <Field label="Logo"><span className="logo-pick">
        <span className="logo-box">{logo ? <img src={logo} alt="Company logo" /> : <small className="muted">No logo</small>}</span>
        {admin && <><label className="btn-file"><input type="file" accept="image/png,image/jpeg,image/webp,image/svg+xml" hidden onChange={e => { void pickLogo(e.target.files?.[0]); e.target.value = '' }} />{logo ? 'Replace…' : 'Upload…'}</label>
          {logo && <button type="button" onClick={dropLogo}>Remove</button>}</>}
        <small className="muted">PNG, JPEG, WebP or SVG, up to 1 MB. A light or transparent image works best as a watermark.</small></span></Field>
      <Field label="Invoice note" wide><textarea className="edit" value={c.invoiceNote ?? ''} disabled={!admin} onChange={e => set({ invoiceNote: e.target.value || null })} /></Field>
      <Field label="Disclaimer" wide><textarea className="edit" value={c.disclaimer ?? ''} disabled={!admin} onChange={e => set({ disclaimer: e.target.value || null })} /></Field>
      <Field label=""><label className="chk"><input type="checkbox" checked={c.useDisclaimer} disabled={!admin} onChange={e => set({ useDisclaimer: e.target.checked })} /> Print the disclaimer on quotes and invoices</label></Field>
      <p className="muted small">Shown at the top of printed quotes, work orders, invoices and statements. The invoice note prints at the bottom of invoices and statements.</p>
    </div>
  )
}

/** Change your own password. Your other devices are signed out afterwards. */
export function ChangePassword() {
  const { toast } = useShell()
  const [cur, setCur] = useState(''); const [a, setA] = useState(''); const [b, setB] = useState(''); const [error, setError] = useState(''); const [busy, setBusy] = useState(false)
  async function go() {
    setError('')
    if (a.length < 12) return setError('The new password must be at least 12 characters.')
    if (a !== b) return setError('The two new passwords do not match.')
    setBusy(true)
    try { await api.changeOwnPassword(cur, a); toast('Password changed. Your other devices were signed out.'); setCur(''); setA(''); setB('') } catch (e) { setError((e as Error).message) } finally { setBusy(false) }
  }
  return (
    <div className="form">
      <p className="muted">Use at least 12 characters: a few unrelated words is a good, easy to remember choice. Changing it signs you out on every other device.</p>
      <Field label="Current password"><input type="password" autoComplete="current-password" value={cur} onChange={e => setCur(e.target.value)} style={{ width: '16rem' }} /></Field>
      <Field label="New password"><input type="password" autoComplete="new-password" value={a} onChange={e => setA(e.target.value)} style={{ width: '16rem' }} /></Field>
      <Field label="Type it again"><input type="password" autoComplete="new-password" value={b} onChange={e => setB(e.target.value)} onKeyDown={e => e.key === 'Enter' && go()} style={{ width: '16rem' }} /></Field>
      {error && <p className="formerr">{error}</p>}
      <p><button className="primary" onClick={go} disabled={busy || !cur || !a}>Change password</button></p>
    </div>
  )
}

/** User accounts: who can sign in and which groups (roles) they are in. A person may do whatever any of their groups allows. */
export function Users() {
  const { me, toast } = useShell()
  const [rows, setRows] = useState<api.UserRow[]>([]); const [groups, setGroups] = useState<api.GroupRow[]>([]); const [error, setError] = useState('')
  const [add, setAdd] = useState(false); const [reset, setReset] = useState<api.UserRow | null>(null)
  const load = () => { api.getUsers().then(setRows).catch(e => setError(e.message)); api.getGroups().then(setGroups).catch(() => {}) }
  useEffect(load, [])
  if (!api.can(me, 'users.manage')) return <div className="form"><p>You do not have permission to manage users.</p></div>
  const upd = async (u: api.UserRow, p: Partial<{ name: string | null; active: boolean; groupIds: number[] }>) => {
    try { await api.updateUser(u.id, { name: u.name, active: u.active, groupIds: u.groupIds, ...p }); setError(''); load() } catch (e) { setError((e as Error).message) }
  }
  const toggle = (u: api.UserRow, gid: number, on: boolean) => upd(u, { groupIds: on ? [...u.groupIds, gid] : u.groupIds.filter(x => x !== gid) })
  return (
    <div className="form">
      <Toolbar tools={[{ label: 'Add user', icon: 'clear', onClick: () => setAdd(true) }]} />
      {error && <p className="formerr">{error}</p>}
      <table className="grid-table"><thead><tr><th>Sign-in</th><th>Name</th><th>Groups</th><th>Active</th><th>Devices</th><th /></tr></thead>
        <tbody>{rows.map(u => <tr key={u.id} className={u.active ? '' : 'void'}>
          <td>{u.code}{u.id === me.id && <span className="badge">you</span>}</td>
          <td>{u.name}{!u.hasPassword && <small className="muted"> (no sign-in set up)</small>}{u.lockedUntil && new Date(u.lockedUntil) > new Date() && <small className="muted"> locked until {instant(u.lockedUntil, me.timeZone)}</small>}</td>
          <td className="groupcell">{groups.map(g => <label key={g.id} className="chk" title={g.description ?? ''}><input type="checkbox" checked={u.groupIds.includes(g.id)} disabled={!u.hasPassword} onChange={e => toggle(u, g.id, e.target.checked)} /> {g.name}</label>)}
            {u.hasPassword && u.groupIds.length === 0 && <small className="muted">no group: can sign in but do nothing</small>}</td>
          <td><input type="checkbox" checked={u.active} disabled={u.id === me.id} onChange={e => upd(u, { active: e.target.checked })} /></td>
          <td className="num">{u.sessions}</td>
          <td><button onClick={() => setReset(u)}>{u.hasPassword ? 'Reset password' : 'Set password'}</button></td></tr>)}</tbody></table>
      <p className="muted small">Groups decide what a person can do (see File &gt; Groups). An inactive user cannot sign in and is signed out everywhere. Users are never deleted, because their name is kept on the documents they created. People with no sign-in set up appear on old documents only.</p>
      {add && <NewUser groups={groups} onClose={() => setAdd(false)} onDone={() => { setAdd(false); toast('User added.'); load() }} />}
      {reset && <ResetPw u={reset} onClose={() => setReset(null)} onDone={() => { setReset(null); toast('Password set. They were signed out everywhere.'); load() }} />}
    </div>
  )
}
function NewUser({ groups, onClose, onDone }: { groups: api.GroupRow[]; onClose: () => void; onDone: () => void }) {
  const [code, setCode] = useState(''); const [name, setName] = useState(''); const [pw, setPw] = useState(''); const [ids, setIds] = useState<number[]>([]); const [error, setError] = useState('')
  async function go() { try { await api.createUser({ code: code.trim(), name: name.trim(), password: pw, groupIds: ids }); onDone() } catch (e) { setError((e as Error).message) } }
  return (
    <Modal title="Add user" onClose={onClose} width={520}><div className="lookup-body">
      <Field label="Sign-in name"><input autoFocus value={code} maxLength={20} onChange={e => setCode(e.target.value)} style={{ width: '10rem' }} /></Field>
      <Field label="Full name"><input value={name} onChange={e => setName(e.target.value)} style={{ width: '16rem' }} /></Field>
      <Field label="Password"><input type="text" autoComplete="off" value={pw} onChange={e => setPw(e.target.value)} placeholder="at least 12 characters" style={{ width: '16rem' }} /></Field>
      <fieldset><legend>Groups (what they can do)</legend>
        {groups.map(g => <label key={g.id} className="chk"><input type="checkbox" checked={ids.includes(g.id)} onChange={e => setIds(x => e.target.checked ? [...x, g.id] : x.filter(i => i !== g.id))} /> <b>{g.name}</b> <span className="muted small">{g.description}</span></label>)}</fieldset>
      {error && <p className="formerr">{error}</p>}
      <div className="actions"><button className="primary" onClick={go} disabled={!code || !pw || ids.length === 0}>Add user</button><button onClick={onClose}>Cancel</button></div>
    </div></Modal>
  )
}
function ResetPw({ u, onClose, onDone }: { u: api.UserRow; onClose: () => void; onDone: () => void }) {
  const [pw, setPw] = useState(''); const [error, setError] = useState('')
  async function go() { try { await api.resetUserPassword(u.id, pw); onDone() } catch (e) { setError((e as Error).message) } }
  return (
    <Modal title={`Password for ${u.code}`} onClose={onClose} width={460}><div className="lookup-body">
      <p className="muted">Give them this password in person; they can change it themselves under File &gt; Change Password.</p>
      <Field label="New password"><input autoFocus type="text" autoComplete="off" value={pw} onChange={e => setPw(e.target.value)} placeholder="at least 12 characters" style={{ width: '16rem' }} /></Field>
      {error && <p className="formerr">{error}</p>}
      <div className="actions"><button className="primary" onClick={go} disabled={!pw}>Set password</button><button onClick={onClose}>Cancel</button></div>
    </div></Modal>
  )
}

/** Clean-up tools: old quotes and customers nobody has used. Always shows how many would go first. */
export function Cleanup() {
  const { me, toast } = useShell()
  const [before, setBefore] = useState(''); const [custBefore, setCustBefore] = useState('')
  const [counts, setCounts] = useState<{ q?: number; c?: number }>({}); const [error, setError] = useState('')
  if (!api.can(me, 'utilities.cleanup')) return <div className="form"><p>You do not have permission to use the clean-up tools.</p></div>
  const run = async (what: 'delete-quotes' | 'delete-inactive-customers', date: string | null, dry: boolean) => {
    try {
      const r = await api.cleanup(what, date, dry); setError('')
      if (dry) setCounts(c => what === 'delete-quotes' ? { ...c, q: r.count } : { ...c, c: r.count })
      else { toast(`${r.count} deleted.`); setCounts({}) }
    } catch (e) { setError((e as Error).message) }
  }
  const sure = (what: string, n: number) => window.confirm(`Permanently delete ${n} ${what}? This cannot be undone. Export a backup first if you are unsure.`)
  return (
    <div className="form">
      {error && <p className="formerr">{error}</p>}
      <h3>Delete old quotes</h3>
      <p className="muted">Removes quotes (not work orders or invoices) dated before the day you choose.</p>
      <Field label="Before"><input type="date" value={before} onChange={e => { setBefore(e.target.value); setCounts(c => ({ ...c, q: undefined })) }} /></Field>
      <p className="row-actions"><button onClick={() => run('delete-quotes', before, true)} disabled={!before}>Count them</button>
        <button onClick={() => counts.q && sure('quotes', counts.q) && run('delete-quotes', before, false)} disabled={!before || !counts.q}>Delete{counts.q != null ? ` ${counts.q.toLocaleString()} quotes` : ''}</button></p>
      <h3>Delete inactive customers</h3>
      <p className="muted">Removes customers who have no documents, payments or balance and are not marked permanent. Optionally only those not used since a date.</p>
      <Field label="Not used since"><input type="date" value={custBefore} onChange={e => { setCustBefore(e.target.value); setCounts(c => ({ ...c, c: undefined })) }} /></Field>
      <p className="row-actions"><button onClick={() => run('delete-inactive-customers', custBefore || null, true)}>Count them</button>
        <button onClick={() => counts.c && sure('customers', counts.c) && run('delete-inactive-customers', custBefore || null, false)} disabled={!counts.c}>Delete{counts.c != null ? ` ${counts.c.toLocaleString()} customers` : ''}</button></p>
      <p className="muted small">Voiding an invoice, work order or credit memo is done from the document itself; payments are voided in Receivables.</p>
    </div>
  )
}

/** Groups (roles): named sets of permissions. Built-in groups can be adjusted; you can add your own, such as "Estimators". */
export function Groups() {
  const { me, toast } = useShell()
  const [groups, setGroups] = useState<api.GroupRow[]>([]); const [perms, setPerms] = useState<api.PermissionDef[]>([])
  const [sel, setSel] = useState<number | 'new' | null>(null)
  const [name, setName] = useState(''); const [desc, setDesc] = useState(''); const [have, setHave] = useState<string[]>([]); const [dirty, setDirty] = useState(false); const [error, setError] = useState('')
  const load = () => api.getGroups().then(setGroups).catch(e => setError(e.message))
  useEffect(() => { void load(); api.getPermissions().then(setPerms).catch(() => {}) }, [])
  if (!api.can(me, 'users.manage')) return <div className="form"><p>You do not have permission to manage groups.</p></div>
  const g = typeof sel === 'number' ? groups.find(x => x.id === sel) : null
  const locked = !!g?.locked
  const choose = (x: api.GroupRow | 'new') => { if (dirty && !window.confirm('Discard your unsaved changes?')) return; setError(''); setDirty(false)
    if (x === 'new') { setSel('new'); setName(''); setDesc(''); setHave([]) } else { setSel(x.id); setName(x.name); setDesc(x.description ?? ''); setHave(x.permissions) } }
  async function save() {
    try {
      if (sel === 'new') { const r = await api.createGroup({ name, description: desc || null, permissions: have }); await load(); setSel(r.id) } else if (g) { await api.updateGroup(g.id, { name, description: desc || null, permissions: have }); await load() }
      setDirty(false); setError(''); toast('Saved.')
    } catch (e) { setError((e as Error).message) }
  }
  async function remove() {
    if (!g || !window.confirm(`Delete the group "${g.name}"?`)) return
    try { await api.deleteGroup(g.id); setSel(null); await load(); toast('Group deleted.') } catch (e) { setError((e as Error).message) }
  }
  const areas = [...new Set(perms.map(p => p.area))]
  return (
    <>
      <Toolbar tools={[{ label: 'Save', icon: 'save', onClick: save, disabled: !dirty || locked || !name.trim() }, { label: 'New group', icon: 'clear', onClick: () => choose('new') }, { label: 'Delete', icon: 'delete', onClick: remove, disabled: !g || g.isSystem || g.members > 0, title: g?.isSystem ? 'Built-in groups cannot be deleted' : g && g.members > 0 ? 'Move its members to another group first' : 'Delete this group' }]} />
      {error && <p className="formerr">{error}</p>}
      <div className="maint wide">
        <div className="maint-list"><table className="grid-table"><thead><tr><th>Group</th><th className="num">People</th><th className="num">Permissions</th></tr></thead>
          <tbody>{groups.map(x => <tr key={x.id} className={x.id === sel ? 'sel' : ''} onClick={() => choose(x)} style={{ cursor: 'pointer' }}><td>{x.name}{x.isSystem && <small className="muted"> built-in</small>}</td><td className="num">{x.members}</td><td className="num">{x.locked ? 'all' : x.permissions.length}</td></tr>)}</tbody></table></div>
        <div className="maint-form">
          {sel == null && <p className="muted">Choose a group to see what it allows, or click New group.</p>}
          {sel != null && <fieldset disabled={locked} style={{ border: 0, padding: 0 }}>
            <Field label="Name"><input value={name} maxLength={40} onChange={e => { setName(e.target.value); setDirty(true) }} style={{ width: '18rem' }} /></Field>
            <Field label="Description"><input value={desc} maxLength={200} onChange={e => { setDesc(e.target.value); setDirty(true) }} style={{ width: '30rem' }} /></Field>
            {locked && <p className="muted">Administrators can do everything, always. This group cannot be changed.</p>}
            {areas.map(a => <fieldset key={a} className="thick"><legend>{a}</legend>
              {perms.filter(p => p.area === a).map(p => <label key={p.key} className="chk"><input type="checkbox" checked={locked || have.includes(p.key)} onChange={e => { setHave(h => e.target.checked ? [...h, p.key] : h.filter(k => k !== p.key)); setDirty(true) }} /> {p.label}</label>)}</fieldset>)}
            <p className="muted small">Everyone who is signed in can look up tax codes, categories and price lists, because every screen needs them. Writing quotes also lets a person pick customers and parts. A person with several groups gets everything any of them allows.</p>
          </fieldset>}
        </div>
      </div>
    </>
  )
}

/** Credentials for other software (OAuth 2.0 client credentials). The secret is shown once, when it is made or replaced. */
export function ApiClients() {
  const { me, toast } = useShell()
  const [clients, setClients] = useState<api.ApiClient[]>([]); const [groups, setGroups] = useState<api.GroupRow[]>([])
  const [sel, setSel] = useState<string | 'new' | null>(null)
  const [name, setName] = useState(''); const [note, setNote] = useState(''); const [gids, setGids] = useState<number[]>([]); const [active, setActive] = useState(true); const [expires, setExpires] = useState('')
  const [dirty, setDirty] = useState(false); const [error, setError] = useState('')
  const [shown, setShown] = useState<{ title: string; clientId: string; secret: string } | null>(null)
  const load = () => api.getApiClients().then(setClients).catch(e => setError(e.message))
  useEffect(() => { void load(); api.getGroups().then(setGroups).catch(() => {}) }, [])
  if (!api.can(me, 'users.manage')) return <div className="form"><p>You do not have permission to manage API clients.</p></div>
  const c = clients.find(x => x.id === sel)
  const usable = groups.filter(g => !g.locked && !g.permissions.includes('users.manage'))
  const url = `${window.location.origin}/api/v1/auth/token`
  const choose = (x: api.ApiClient | 'new') => {
    if (dirty && !window.confirm('Discard your unsaved changes?')) return
    setError(''); setDirty(false)
    if (x === 'new') { setSel('new'); setName(''); setNote(''); setGids([]); setActive(true); setExpires('') }
    else { setSel(x.id); setName(x.name); setNote(x.note ?? ''); setGids(x.groupIds); setActive(x.active); setExpires(x.expiresAt ? x.expiresAt.slice(0, 10) : '') }
  }
  const body = (): api.ApiClientInput => ({ name, note: note || null, groupIds: gids, active, expiresAt: expires ? new Date(expires + 'T23:59:59').toISOString() : null })
  async function save() {
    try {
      if (sel === 'new') { const r = await api.createApiClient(body()); await load(); setSel(r.id); setShown({ title: 'API client created', clientId: r.clientId, secret: r.clientSecret }) }
      else if (c) { await api.updateApiClient(c.id, body()); await load(); toast('Saved.') }
      setDirty(false); setError('')
    } catch (e) { setError((e as Error).message) }
  }
  async function rotate() {
    if (!c || !window.confirm(`Make a new secret for "${c.name}"? The old secret stops working immediately, and so does every token made with it.`)) return
    try { const r = await api.rotateApiClientSecret(c.id); await load(); setShown({ title: 'New secret', clientId: c.clientId, secret: r.clientSecret }) } catch (e) { setError((e as Error).message) }
  }
  async function remove() {
    if (!c || !window.confirm(`Delete the API client "${c.name}"? Anything using it stops working.`)) return
    try { await api.deleteApiClient(c.id); setSel(null); setDirty(false); await load(); toast('API client deleted.') } catch (e) { setError((e as Error).message) }
  }
  const copy = (t: string) => { void navigator.clipboard?.writeText(t); toast('Copied.') }
  return (
    <>
      <Toolbar tools={[{ label: 'Save', icon: 'save', onClick: save, disabled: !dirty || !name.trim() || gids.length === 0 }, { label: 'New client', icon: 'clear', onClick: () => choose('new') },
        { label: 'New secret', icon: 'notes', onClick: rotate, disabled: !c, title: 'Replace the secret; the old one stops working at once' }, { label: 'Delete', icon: 'delete', onClick: remove, disabled: !c }]} />
      {error && <p className="formerr">{error}</p>}
      <div className="maint wide">
        <div className="maint-list"><table className="grid-table"><thead><tr><th>Client</th><th>Last used</th></tr></thead>
          <tbody>{clients.map(x => <tr key={x.id} className={x.id === sel ? 'sel' : ''} onClick={() => choose(x)} style={{ cursor: 'pointer' }}>
            <td>{x.name}{!x.active && <small className="muted"> off</small>}</td><td>{x.lastUsedAt ? instant(x.lastUsedAt, me.timeZone) : <span className="muted">never</span>}</td></tr>)}</tbody></table>
          {clients.length === 0 && <p className="muted small" style={{ padding: '0.5rem' }}>No API clients yet.</p>}</div>
        <div className="maint-form">
          {sel == null && <>
            <p className="muted">An API client lets other software (a website, a purchasing system, a script) use ReflectoPatronus without a person's password. Choose a client, or click New client.</p>
            <p className="small">Token address: <code>{url}</code> <button type="button" onClick={() => copy(url)}>Copy</button></p></>}
          {sel != null && <>
            <Field label="Name"><input value={name} maxLength={60} placeholder="What uses this? e.g. Purchasing system" onChange={e => { setName(e.target.value); setDirty(true) }} style={{ width: '24rem' }} /></Field>
            <Field label="Note"><input value={note} maxLength={200} onChange={e => { setNote(e.target.value); setDirty(true) }} style={{ width: '30rem' }} /></Field>
            {c && <Field label="Client ID"><input readOnly value={c.clientId} style={{ width: '24rem' }} onFocus={e => e.target.select()} /> <button type="button" onClick={() => copy(c.clientId)}>Copy</button></Field>}
            <Field label="Active"><input type="checkbox" checked={active} onChange={e => { setActive(e.target.checked); setDirty(true) }} /> {active ? 'On' : 'Off: it cannot get tokens'}</Field>
            <Field label="Secret expires"><input type="date" value={expires} onChange={e => { setExpires(e.target.value); setDirty(true) }} /> <small className="muted">{expires ? '' : 'Never'}</small></Field>
            <fieldset className="thick"><legend>What it may do (groups)</legend>
              {usable.map(g => <label key={g.id} className="chk" title={g.description ?? ''}><input type="checkbox" checked={gids.includes(g.id)} onChange={e => { setGids(h => e.target.checked ? [...h, g.id] : h.filter(k => k !== g.id)); setDirty(true) }} /> {g.name}</label>)}
              <p className="muted small">Same groups as people. For supplier and stock work, make a group with only "Inventory, stock counts and purchase orders" (Groups), and pick it here. Administrators and groups that manage users cannot be used.</p></fieldset>
            {c && <p className="small muted">Created {instant(c.createdAt, me.timeZone)}. Secret made {instant(c.secretRotatedAt, me.timeZone)}. {c.tokens} token{c.tokens === 1 ? '' : 's'} in use.</p>}
          </>}
        </div>
      </div>
      {shown && <Modal title={shown.title} onClose={() => setShown(null)} width={640}>
        <div className="form">
          <p><b>Copy the secret now.</b> It is not stored and cannot be shown again; if it is lost, make a new one.</p>
          <Field label="Client ID"><input readOnly value={shown.clientId} style={{ width: '26rem' }} onFocus={e => e.target.select()} /> <button type="button" onClick={() => copy(shown.clientId)}>Copy</button></Field>
          <Field label="Client secret"><input readOnly value={shown.secret} style={{ width: '26rem' }} onFocus={e => e.target.select()} /> <button type="button" onClick={() => copy(shown.secret)}>Copy</button></Field>
          <p className="small">Get a token (valid one hour), then send it as <code>Authorization: Bearer …</code>:</p>
          <pre style={{ whiteSpace: 'pre-wrap', userSelect: 'all', fontSize: '0.8em' }}>{`curl -s ${url} \\\n  -d grant_type=client_credentials \\\n  -d client_id=${shown.clientId} \\\n  -d client_secret=${shown.secret}`}</pre>
          <p style={{ textAlign: 'right' }}><button type="button" onClick={() => setShown(null)}>I have copied it</button></p>
        </div>
      </Modal>}
    </>
  )
}
