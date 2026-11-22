import { useEffect, useState } from 'react'
import * as api from '../api'
import { instant, money, plainDate } from '../format'
import { useShell, useWinId } from '../shell/WindowContext'
import { Audit, Field, Modal, RO, Tabs, Toolbar } from '../shell/ui'
import { DocumentBrowser, TYPE_NAMES } from './DocumentBrowser'
import { QuoteEditor } from './QuoteEditor'

const TYPES = ['Q', 'W', 'I', 'C']

/** The "Quote" window (the same window serves quotes, work orders, invoices and credits via Document Type). Quotes are editable; the rest are read-only for now. */
export function QuoteWindow({ initialId }: { initialId?: number }) {
  const { me, open, setTitle } = useShell(), winId = useWinId()
  const [docType, setDocType] = useState('Q')
  const [number, setNumber] = useState('')
  const [d, setD] = useState<api.DocumentDetail | null>(null)
  const [balance, setBalance] = useState<api.CustomerDetail | null>(null)
  const [tab, setTab] = useState('General')
  const [lookup, setLookup] = useState<string | null>(null)
  const [error, setError] = useState('')

  const load = (id: number) => api.getDocument(id).then(x => {
    setD(x); setDocType(x.type); setNumber(x.number); setError(''); setTab('General'); setTitle(winId, `${x.typeName} ${x.number}`)
    setBalance(null); if (x.customerId) api.getCustomer(x.customerId).then(setBalance).catch(() => {})
  }).catch(e => setError(e.status === 404 ? 'Document not found.' : e.message))
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { if (initialId != null) load(initialId) }, [initialId])

  const clear = () => { setD(null); setNumber(''); setBalance(null); setError(''); setTitle(winId, 'Quote') }
  async function enter() {
    const n = number.trim(); if (!n) return setLookup('')
    const p = await api.searchDocuments({ type: docType, q: n, includeVoided: true, limit: 5 })
    const exact = p.items.filter(i => i.number.replace(/^\D*0*/, '').replace(/\D*$/, '') === n.replace(/^0+/, '') || i.number === n)
    if (exact.length === 1) load(exact[0].id); else setLookup(n)
  }

  const editing = docType === 'Q' && (d == null || d.editable)
  const status = d?.statuses.at(-1)?.status
  const t = d?.totals
  return (
    <>
      {!editing && <Toolbar tools={[
        { label: 'Save', icon: 'save' }, { label: 'Print', icon: 'print' }, { label: 'E-mail', icon: 'email' },
        { label: 'Open', icon: 'open', onClick: () => setLookup(''), title: 'Open a document' },
        { label: 'Delete', icon: 'delete' }, { label: 'Clear', icon: 'clear', onClick: clear, title: 'Clear the form' }]} />}
      <div className="form">
        <div className="doc-id">
          <Field label="Document Type">
            <select value={docType} onChange={e => { setDocType(e.target.value); if (d && d.type !== e.target.value) clear() }}>
              {TYPES.map(c => <option key={c} value={c}>{TYPE_NAMES[c]}</option>)}
              {d?.type === 'R' && <option value="R">Other</option>}
            </select>
          </Field>
          <Field label="Document #">
            <input value={number} onChange={e => setNumber(e.target.value)} onKeyDown={e => e.key === 'Enter' && enter()} placeholder="Type a number, Enter" autoFocus={initialId == null} />
            <button className="dots" title="Look up a document" onClick={() => setLookup(number.trim())}>…</button>
          </Field>
          {d?.voided && <span className="badge">VOID</span>}
        </div>
        {error && <p className="error">{error}</p>}
        {editing ? <QuoteEditor key={d?.id ?? 'new'} initial={d} onSaved={load} onOpen={() => setLookup('')} onNew={clear} /> : <>
        <Tabs tabs={['General', 'Insurance', 'Installation', 'Other', 'Notes']} active={tab} onChange={setTab} />
        <div className="panel">
          {tab === 'General' && <div className="general">
            <div>
              <Field label="Customer"><RO value={d?.customer.name} width="22rem" />{d?.customerId && <button className="dots" title="Open customer" onClick={() => open('customer', d.customerId!)}>↗</button>}</Field>
              <Field label="Bill To"><RO value={d?.billToCustomerId ? `Customer #${d.billToCustomerId}` : d ? 'Same as customer' : ''} width="22rem" /></Field>
              <Field label="Tax Group"><RO value={d?.taxGroup} width="10rem" /></Field>
              <Field label="Price Category"><RO value={d?.priceCategory} width="10rem" /></Field>
              <Field label="Sold by"><RO value={d?.salesPerson} width="10rem" /></Field>
              <Field label=""><label className="radio"><input type="radio" disabled checked={d?.shopOrMobile !== 'M'} readOnly /> In Shop</label>
                <label className="radio"><input type="radio" disabled checked={d?.shopOrMobile === 'M'} readOnly /> Mobile</label></Field>
              {d && <address>{[d.customer.address1, d.customer.address2, [d.customer.city, d.customer.state, d.customer.zip].filter(Boolean).join(' ')].filter(Boolean).join(', ')}{d.customer.phones.length > 0 && ' · ' + d.customer.phones.join(' · ')}</address>}
            </div>
            <div className="right">
              <Field label="Doc Date"><RO value={d ? plainDate(d.date) : ''} width="9rem" /></Field>
              <Field label="Printed"><RO value={d ? (d.printedAt ? instant(d.printedAt, me.timeZone) : "N/A") : ""} width="12rem" /></Field>
              <Field label="Cust Balance"><RO value={balance ? money(balance.balance) : ''} width="9rem" /></Field>
              <Field label="Credit Limit"><RO value={balance?.creditLimit != null ? money(balance.creditLimit) : balance ? '$0.00' : ''} width="9rem" /></Field>
              <Field label="Discount"><RO value={d ? `${(d.discountPercent ?? 0).toFixed(2)}%` : ''} width="9rem" /></Field>
              <Field label="Status"><RO value={status} width="9rem" /></Field>
            </div>
          </div>}
          {tab === 'Insurance' && <>
            <Field label="Policy #"><RO value={d?.policyNumber} width="14rem" /></Field>
            <Field label="Claim #"><RO value={d?.claimNumber} width="14rem" /></Field>
            <Field label="Loss Date"><RO value={d?.lossDate ? plainDate(d.lossDate) : ''} width="10rem" /></Field>
          </>}
          {tab === 'Installation' && <>
            {d?.jobSite && <fieldset><legend>Job site</legend><address>{[d.jobSite.address1, d.jobSite.address2, [d.jobSite.city, d.jobSite.state, d.jobSite.zip].filter(Boolean).join(' ')].filter(Boolean).join(', ')}</address></fieldset>}
            <table className="grid-table"><thead><tr><th>Installer</th><th className="num">Hours</th><th className="num">Amount</th></tr></thead>
              <tbody>{d?.installers.map((i, n) => <tr key={n}><td>{i.name ?? i.code}</td><td className="num">{i.hours}</td><td className="num">{i.amount != null && money(i.amount)}</td></tr>)}</tbody></table>
            {d && !d.jobSite && d.installers.length === 0 && <p className="muted">No installation details on this document.</p>}
          </>}
          {tab === 'Other' && <>
            <Field label="PO #"><RO value={d?.poNumber} width="14rem" /></Field>
            <Field label="Terms"><RO value={d?.terms} width="14rem" /></Field>
            <Field label="Created From"><RO value={d?.sourceDocumentNumber} width="14rem" /></Field>
            <Field label="Created"><RO value={d ? instant(d.createdAt, me.timeZone) : ''} width="14rem" /></Field>
            {d && d.statuses.length > 0 && <><h4>Status history</h4><ul>{d.statuses.map((s, n) => <li key={n}>{s.at ? instant(s.at, me.timeZone) : 'unknown time'} — {s.status}{s.note && `: ${s.note}`}</li>)}</ul></>}
            {d && d.revisions.length > 0 && <><h4>Revisions</h4><ul>{d.revisions.map((r, n) => <li key={n}>{instant(r.at, me.timeZone)}{r.total != null && ` — ${money(r.total)}`}{r.comment && ` — ${r.comment}`}</li>)}</ul></>}
          </>}
          {tab === 'Notes' && <>
            {([['Special instructions', d?.specialInstructions], ['Work order instructions', d?.workOrderInstructions], ['Printed note', d?.notePrinted], ['Internal note', d?.noteInternal]] as const)
              .map(([l, v]) => <Field key={l} label={l} wide><textarea className="ro" readOnly value={v ?? ''} /></Field>)}
          </>}
        </div>

        <table className="grid-table lines">
          <thead><tr><th className="num">Qty</th><th>Part Code</th><th>Thick</th><th>Description</th><th>Size</th><th className="num">List</th><th className="num">Amount</th></tr></thead>
          <tbody>
            {d?.lines.map(l => (
              <tr key={l.seq} className={l.parentSeq != null ? 'child' : ''}>
                <td className="num">{l.qty}</td><td>{l.partCode}</td><td>{l.thickness}</td><td>{[l.description, l.shape].filter(Boolean).join(' ')}</td><td>{l.dims}</td>
                <td className="num">{l.listPrice != null ? money(l.listPrice) : ''}</td><td className="num">{money(l.amount)}{!l.taxable && l.amount !== 0 ? ' *' : ''}</td>
              </tr>
            ))}
            {!d && Array.from({ length: 4 }, (_, i) => <tr key={i} className="blank"><td className="num">0</td><td /><td /><td /><td /><td className="num">0.00</td><td className="num">0.00</td></tr>)}
          </tbody>
          {d && t && <tfoot>
            <tr><td colSpan={6} className="num">Subtotal</td><td className="num">{money(t.lines)}</td></tr>
            {d.taxes.map(x => <tr key={x.code}><td colSpan={6} className="num">Tax {x.code} ({x.percent}%)</td><td className="num">{money(x.amount)}</td></tr>)}
            <tr className="total"><td colSpan={6} className="num">Total</td><td className="num">{money(t.total)}</td></tr>
          </tfoot>}
        </table>
        {d && <p className="muted small">* not taxable · Glass {money(t!.glass)} · Parts {money(t!.part)} · Labor {money(t!.labor)} · Other {money(t!.custom)}</p>}
        </>}
      </div>
      <Audit createdAt={d ? instant(d.createdAt, me.timeZone) : undefined} />
      {lookup !== null && (
        <Modal title={`Lookup ${TYPE_NAMES[docType]}s`} onClose={() => setLookup(null)} width={820}>
          <div className="lookup-body"><DocumentBrowser fixedType={docType} initialQuery={lookup} onPick={s => { setLookup(null); load(s.id) }} /></div>
        </Modal>
      )}
    </>
  )
}
