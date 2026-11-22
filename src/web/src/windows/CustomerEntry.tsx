import { useCallback, useEffect, useState } from 'react'
import * as api from '../api'
import { instant, money, plainDate } from '../format'
import { useShell, useWinId } from '../shell/WindowContext'
import { Audit, Field, RO, Tabs, Toolbar } from '../shell/ui'
import { CustomerLookup } from './CustomerLookup'
import { DocumentBrowser } from './DocumentBrowser'

// the legacy system labelled its five phone slots like this (Customer Entry screen).
const PHONE_LABELS = ['Cell', 'Work', 'Home', 'Pager', 'Other']

interface Form {
  number: string; type: string; lastName: string; firstName: string; address1: string; address2: string; city: string; state: string; zip: string
  email: string; contactName: string; taxGroup: string; taxExempt: boolean; taxId: string; priceCategory: string; category: string
  printStatement: boolean; noPrices: boolean; creditLimit: string; notePrinted: string; noteInternal: string
  phones: { number: string; ext: string }[]
}
const blank = (): Form => ({
  number: '', type: 'A', lastName: '', firstName: '', address1: '', address2: '', city: '', state: '', zip: '', email: '', contactName: '',
  taxGroup: '', taxExempt: false, taxId: '', priceCategory: '', category: '', printStatement: false, noPrices: false, creditLimit: '',
  notePrinted: '', noteInternal: '', phones: PHONE_LABELS.map(() => ({ number: '', ext: '' })),
})
const fromDetail = (d: api.CustomerDetail): Form => ({
  number: d.number, type: d.type, lastName: d.lastName, firstName: d.firstName ?? '', address1: d.address1 ?? '', address2: d.address2 ?? '',
  city: d.city ?? '', state: d.state ?? '', zip: d.zip ?? '', email: d.email ?? '', contactName: d.contactName ?? '', taxGroup: d.taxGroup ?? '',
  taxExempt: d.taxExempt, taxId: d.taxId ?? '', priceCategory: d.priceCategory ?? '', category: d.category ?? '',
  printStatement: d.printStatement, noPrices: d.noPrices, creditLimit: d.creditLimit != null ? String(d.creditLimit) : '',
  notePrinted: d.notePrinted ?? '', noteInternal: d.noteInternal ?? '',
  phones: PHONE_LABELS.map((_, i) => { const p = d.phones.find(x => x.slot === i + 1); return { number: p?.number ?? '', ext: p?.ext ?? '' } }),
})
const toInput = (f: Form, updatedAt?: string): api.CustomerInput => ({
  ...f, creditLimit: f.creditLimit.trim() === '' ? null : Number(f.creditLimit),
  phones: f.phones.map((p, i) => ({ slot: i + 1, number: p.number, ext: p.ext || null })).filter(p => p.number.trim() !== ''), updatedAt,
})

/** Validation the server repeats; here it only saves a round trip and marks the field. */
function problems(f: Form): Record<string, string> {
  const e: Record<string, string> = {}
  if (!f.number.trim()) e.number = 'Code is required.'
  if (!f.lastName.trim() && !f.firstName.trim()) e.lastName = 'Enter a last name.'
  if (f.state.trim() && f.state.trim().length !== 2) e.state = 'Two letters.'
  if (f.email.trim() && !f.email.includes('@')) e.email = 'Looks invalid.'
  if (f.creditLimit.trim() && (!Number.isFinite(Number(f.creditLimit)) || Number(f.creditLimit) < 0)) e.creditLimit = 'Enter a positive amount.'
  return e
}

export function CustomerEntry({ initialId }: { initialId?: number }) {
  const shell = useShell(), winId = useWinId()
  const [id, setId] = useState<number | null>(initialId ?? null)
  const [c, setC] = useState<api.CustomerDetail | null>(null)
  const [f, setF] = useState<Form>(blank)
  const [dirty, setDirty] = useState(false)
  const [busy, setBusy] = useState(false)
  const [tab, setTab] = useState('General')
  const [lookup, setLookup] = useState(false)
  const [lk, setLk] = useState<api.CustomerLookups | null>(null)
  const [error, setError] = useState('')
  const [bad, setBad] = useState<Record<string, string>>({})

  useEffect(() => { api.getCustomerLookups().then(setLk).catch(() => {}) }, [])

  const load = useCallback((cid: number) => api.getCustomer(cid).then(d => {
    setC(d); setF(fromDetail(d)); setDirty(false); setError(''); setBad({}); shell.setTitle(winId, `Customer: ${d.lastName}`)
  }).catch(e => setError(e.message)), [shell, winId])

  useEffect(() => {
    if (id == null) { setC(null); setF(blank()); setDirty(false); setBad({}); setError(''); shell.setTitle(winId, 'Customer Entry (new)'); return }
    load(id)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])
  useEffect(() => { // mark unsaved changes in the window title
    if (id == null) return
    shell.setTitle(winId, `Customer: ${f.lastName || c?.lastName || ''}${dirty ? ' *' : ''}`)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [dirty])

  const set = <K extends keyof Form>(k: K, v: Form[K]) => { setF(p => ({ ...p, [k]: v })); setDirty(true); setBad(b => { const { [k]: _, ...rest } = b; return rest }) }
  const setPhone = (i: number, k: 'number' | 'ext', v: string) => { setF(p => ({ ...p, phones: p.phones.map((x, j) => j === i ? { ...x, [k]: v } : x) })); setDirty(true) }
  const confirmDiscard = () => !dirty || window.confirm('Discard your unsaved changes?')

  async function save() {
    const e = problems(f); setBad(e)
    if (Object.keys(e).length) { setError('Fix the highlighted fields.'); setTab('General'); return }
    setBusy(true); setError('')
    try {
      if (id == null) { const r = await api.createCustomer(toInput(f)); shell.toast('Customer created.'); setDirty(false); setId(r.id) }
      else { await api.updateCustomer(id, toInput(f, c?.updatedAt)); shell.toast('Customer saved.'); await load(id) }
    } catch (err) { setError(err instanceof Error ? err.message : 'Save failed.'); shell.toast('Not saved.', 'error') }
    finally { setBusy(false) }
  }
  async function remove() {
    if (id == null || !window.confirm(`Delete customer ${f.number} (${f.lastName})? This cannot be undone.`)) return
    setBusy(true)
    try { await api.deleteCustomer(id); shell.toast('Customer deleted.'); setDirty(false); setId(null) }
    catch (err) { setError(err instanceof Error ? err.message : 'Delete failed.') }
    finally { setBusy(false) }
  }
  useEffect(() => {
    const k = (e: KeyboardEvent) => { if ((e.ctrlKey || e.metaKey) && e.key === 's') { e.preventDefault(); if (dirty && !busy) void save() } }
    document.addEventListener('keydown', k); return () => document.removeEventListener('keydown', k)
  })

  const text = (k: 'number' | 'lastName' | 'firstName' | 'address1' | 'address2' | 'city' | 'zip' | 'email' | 'contactName' | 'taxId' | 'creditLimit', width?: string, extra?: object) =>
    <input value={f[k]} onChange={e => set(k, e.target.value)} className={bad[k] ? 'bad' : ''} title={bad[k]} style={width ? { width } : undefined} {...extra} />
  const check = (k: 'taxExempt' | 'printStatement' | 'noPrices') => <input type="checkbox" checked={f[k]} onChange={e => set(k, e.target.checked)} />
  const pick = (k: 'type' | 'taxGroup' | 'priceCategory' | 'category', options: api.CodeName[] | string[] | undefined, allowBlank: boolean, width: string) => {
    const opts = (options ?? []).map(o => typeof o === 'string' ? { code: o, description: null } : o)
    if (f[k] && !opts.some(o => o.code === f[k])) opts.push({ code: f[k], description: null })   // keep a code the lists no longer carry
    return <select value={f[k]} onChange={e => set(k, e.target.value)} style={{ width }}>
      {allowBlank && <option value="">(none)</option>}
      {opts.map(o => <option key={o.code} value={o.code}>{o.description ? `${o.code} - ${o.description}` : o.code}</option>)}
    </select>
  }
  const tz = shell.me.userTimeZone ?? shell.me.companyTimeZone

  return (
    <>
      <Toolbar tools={[
        { label: 'Save', icon: 'save', onClick: save, disabled: busy || !dirty, title: dirty ? 'Save (Ctrl+S)' : 'No changes to save' },
        { label: 'Delete', icon: 'delete', onClick: remove, disabled: busy || id == null || !shell.me.isAdmin, title: shell.me.isAdmin ? 'Delete this customer' : 'Only administrators can delete customers' },
        { label: 'New', icon: 'clear', onClick: () => { if (confirmDiscard()) { setId(null); setTab('General') } }, title: 'Start a new customer' },
        { label: 'Notes', icon: 'notes', onClick: () => setTab('Notes'), title: 'Printed and internal notes' }]} />
      <div className="form">
        <Field label="Code">{text('number', '9rem', { autoFocus: id == null })}<button className="dots" title="Look up a customer" onClick={() => { if (confirmDiscard()) setLookup(true) }}>…</button>
          {dirty && <span className="dirty">unsaved changes</span>}</Field>
        <Field label="Balance"><RO value={c ? money(c.balance) : ''} width="9rem" />{c && <button className="dots" title="Open this customer's account: open invoices, payments, statement" onClick={() => shell.open('receivables', c.id)}>↗</button>}</Field>
        {error && <p className="formerr">{error}</p>}
        <Tabs tabs={['General', 'Finance/Tax', 'Notes', 'Documents']} active={tab} onChange={setTab} />
        <div className="panel">
          {tab === 'General' && <>
            <Field label="Customer Type">{pick('type', lk?.types, false, '5rem')}</Field>
            <Field label="First, Last">{text('firstName', '12rem')}{text('lastName', '20rem')}</Field>
            <Field label="Email Address" wide>{text('email', undefined, { type: 'email' })}</Field>
            <Field label="Contact">{text('contactName', '20rem')}</Field>
            <div className="cols">
              <fieldset><legend>Address</legend>
                <Field label="Address" wide>{text('address1')}</Field><Field label="" wide>{text('address2')}</Field>
                <Field label="City">{text('city')}</Field>
                <Field label="State"><input value={f.state} maxLength={2} onChange={e => set('state', e.target.value.toUpperCase())} className={bad.state ? 'bad' : ''} style={{ width: '3rem' }} /><span className="lbl">ZIP</span>{text('zip', '7rem')}</Field>
              </fieldset>
              <fieldset><legend>Phone</legend>
                {PHONE_LABELS.map((l, i) => <Field key={l} label={l}>
                  <input value={f.phones[i].number} onChange={e => setPhone(i, 'number', e.target.value)} style={{ width: '10rem' }} autoComplete="off" />
                  <span className="lbl">Ext</span><input value={f.phones[i].ext} onChange={e => setPhone(i, 'ext', e.target.value)} style={{ width: '4rem' }} /></Field>)}
              </fieldset>
            </div>
          </>}
          {tab === 'Finance/Tax' && <>
            <Field label="Tax Group">{pick('taxGroup', lk?.taxGroups, true, '16rem')}</Field>
            <Field label="Tax Exempt">{check('taxExempt')}</Field>
            <Field label="Tax ID">{text('taxId', '12rem')}</Field>
            <Field label="Price Category">{pick('priceCategory', lk?.priceCategories, true, '16rem')}</Field>
            <Field label="Category">{pick('category', lk?.customerCategories, true, '16rem')}</Field>
            <Field label="Credit Limit">{text('creditLimit', '10rem', { inputMode: 'decimal', placeholder: 'none' })}</Field>
            <Field label="Print Statement">{check('printStatement')}</Field>
            <Field label="No Prices">{check('noPrices')}</Field>
            <Field label="Last Transaction"><RO value={c?.lastTransactionOn ? plainDate(c.lastTransactionOn) : ''} width="10rem" /></Field>
          </>}
          {tab === 'Notes' && <>
            <Field label="Printed note" wide><textarea className="edit" value={f.notePrinted} onChange={e => set('notePrinted', e.target.value)} /></Field>
            <Field label="Internal note" wide><textarea className="edit" value={f.noteInternal} onChange={e => set('noteInternal', e.target.value)} /></Field>
          </>}
          {tab === 'Documents' && (c ? <DocumentBrowser customerId={c.id} compact /> : <p className="muted">Save the customer first; then their documents appear here.</p>)}
        </div>
      </div>
      <Audit createdAt={c ? instant(c.createdAt, tz) : undefined} updatedAt={c ? instant(c.updatedAt, tz) : undefined} />
      {lookup && <CustomerLookup onClose={() => setLookup(false)} onPick={i => { setId(i); setLookup(false) }} />}
    </>
  )
}
