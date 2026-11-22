import { useEffect, useState } from 'react'
import * as api from '../api'
import { instant } from '../format'
import { useShell } from '../shell/WindowContext'
import { Field, Modal, Toolbar } from '../shell/ui'

/** The shop's own details, printed at the top of every document. */
export function CompanyInfo() {
  const { me, toast } = useShell(), admin = me.isAdmin
  const [c, setC] = useState<(api.CompanyInfo & { timeZone?: string | null }) | null>(null); const [dirty, setDirty] = useState(false); const [error, setError] = useState('')
  useEffect(() => { api.getCompany().then(setC).catch(e => setError(e.message)) }, [])
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

/** User accounts (administrators): add people, make or remove administrators, deactivate, reset a password. */
export function Users() {
  const { me, toast } = useShell()
  const [rows, setRows] = useState<api.UserRow[]>([]); const [error, setError] = useState('')
  const [add, setAdd] = useState(false); const [reset, setReset] = useState<api.UserRow | null>(null)
  const load = () => api.getUsers().then(setRows).catch(e => setError(e.message))
  useEffect(() => { void load() }, [])
  if (!me.isAdmin) return <div className="form"><p>Administrator access is required.</p></div>
  const upd = async (u: api.UserRow, p: Partial<{ name: string | null; active: boolean; isAdmin: boolean }>) => {
    try { await api.updateUser(u.id, { name: u.name, active: u.active, isAdmin: u.isAdmin, ...p }); setError(''); await load() } catch (e) { setError((e as Error).message) }
  }
  return (
    <div className="form">
      <Toolbar tools={[{ label: 'Add user', icon: 'clear', onClick: () => setAdd(true) }]} />
      {error && <p className="formerr">{error}</p>}
      <table className="grid-table"><thead><tr><th>Sign-in</th><th>Name</th><th>Administrator</th><th>Active</th><th>Devices</th><th /></tr></thead>
        <tbody>{rows.map(u => <tr key={u.id} className={u.active ? '' : 'void'}>
          <td>{u.code}{u.id === me.id && <span className="badge">you</span>}</td>
          <td>{u.name}{!u.hasPassword && <small className="muted"> (no sign-in set up)</small>}{u.lockedUntil && new Date(u.lockedUntil) > new Date() && <small className="muted"> locked until {instant(u.lockedUntil, me.timeZone)}</small>}</td>
          <td><input type="checkbox" checked={u.isAdmin} disabled={u.id === me.id || !u.hasPassword} onChange={e => upd(u, { isAdmin: e.target.checked })} /></td>
          <td><input type="checkbox" checked={u.active} disabled={u.id === me.id} onChange={e => upd(u, { active: e.target.checked })} /></td>
          <td className="num">{u.sessions}</td>
          <td><button onClick={() => setReset(u)}>{u.hasPassword ? 'Reset password' : 'Set password'}</button></td></tr>)}</tbody></table>
      <p className="muted small">An inactive user cannot sign in and is signed out everywhere. Users are never deleted, because their name is kept on the documents they created.</p>
      {add && <NewUser onClose={() => setAdd(false)} onDone={() => { setAdd(false); toast('User added.'); void load() }} />}
      {reset && <ResetPw u={reset} onClose={() => setReset(null)} onDone={() => { setReset(null); toast('Password set. They were signed out everywhere.'); void load() }} />}
    </div>
  )
}
function NewUser({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const [code, setCode] = useState(''); const [name, setName] = useState(''); const [pw, setPw] = useState(''); const [admin, setAdmin] = useState(false); const [error, setError] = useState('')
  async function go() { try { await api.createUser({ code: code.trim(), name: name.trim(), password: pw, isAdmin: admin }); onDone() } catch (e) { setError((e as Error).message) } }
  return (
    <Modal title="Add user" onClose={onClose} width={480}><div className="lookup-body">
      <Field label="Sign-in name"><input autoFocus value={code} maxLength={20} onChange={e => setCode(e.target.value)} style={{ width: '10rem' }} /></Field>
      <Field label="Full name"><input value={name} onChange={e => setName(e.target.value)} style={{ width: '16rem' }} /></Field>
      <Field label="Password"><input type="text" autoComplete="off" value={pw} onChange={e => setPw(e.target.value)} placeholder="at least 12 characters" style={{ width: '16rem' }} /></Field>
      <Field label=""><label className="chk"><input type="checkbox" checked={admin} onChange={e => setAdmin(e.target.checked)} /> Administrator (can change prices, tables and users)</label></Field>
      {error && <p className="formerr">{error}</p>}
      <div className="actions"><button className="primary" onClick={go} disabled={!code || !pw}>Add user</button><button onClick={onClose}>Cancel</button></div>
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
  if (!me.isAdmin) return <div className="form"><p>Administrator access is required.</p></div>
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
