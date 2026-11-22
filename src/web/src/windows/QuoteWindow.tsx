import { useEffect, useState } from 'react'
import * as api from '../api'
import { instant, money, plainDate } from '../format'
import { useShell, useWinId } from '../shell/WindowContext'
import { Audit, Field, Modal, RO, Tabs, Toolbar, type Tool } from '../shell/ui'
import { PrintDialog } from '../print/PrintDialog'
import { loadPrefs } from '../print/printers'
import { kindOf, renderDocument } from '../print/render'
import { PaymentDialog } from './PaymentDialog'
import { DocumentBrowser, TYPE_NAMES } from './DocumentBrowser'
import { QuoteEditor } from './QuoteEditor'

const TYPES = ['Q', 'W', 'I', 'C']

/** The "Quote" window: one window for quotes, work orders, invoices and credit memos (Document Type). Quotes, work orders and invoices are editable; credit memos are fixed once created. */
export function QuoteWindow({ initialId }: { initialId?: number }) {
  const { me, open, setTitle, toast } = useShell(), winId = useWinId()
  const [docType, setDocType] = useState('Q')
  const [number, setNumber] = useState('')
  const [d, setD] = useState<api.DocumentDetail | null>(null)
  const [balance, setBalance] = useState<api.CustomerDetail | null>(null)
  const [tab, setTab] = useState('General')
  const [lookup, setLookup] = useState<string | null>(null)
  const [error, setError] = useState('')
  const [printing, setPrinting] = useState(false); const [paying, setPaying] = useState(false); const [crediting, setCrediting] = useState(false)
  const [creditSeqs, setCreditSeqs] = useState<number[]>([]); const [busy, setBusy] = useState(false)

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

  async function convert(to: 'W' | 'I' | 'C', lineSeqs?: number[]) {
    if (!d) return
    const what = { W: 'work order', I: 'invoice', C: 'credit memo' }[to]
    const msg = to === 'I' ? `Turn ${d.typeName.toLowerCase()} ${d.number} into an invoice? It is billed to ${d.customer.name ?? 'the customer'} and the amount is added to their balance.`
      : to === 'W' ? `Create a work order from quote ${d.number}?` : ''
    if (msg && !window.confirm(msg)) return
    setBusy(true); setError('')
    try {
      let r
      try { r = await api.convertDocument(d.id, to, { lineSeqs }) }
      catch (e) {
        if (e instanceof api.ApiError && e.status === 409 && /already turned into/.test(e.message) && window.confirm(`${e.message}\n\nCreate another anyway?`)) r = await api.convertDocument(d.id, to, { lineSeqs, force: true })
        else throw e
      }
      toast(`${what[0].toUpperCase() + what.slice(1)} ${r.number} created.`); setCrediting(false)
      if (r.id === d.id) await load(r.id); else open('quote', r.id)
    } catch (e) { setError(e instanceof Error ? e.message : 'Could not convert.') } finally { setBusy(false) }
  }
  const tools = (guard: (f: () => void) => () => void): Tool[] => {
    if (!d) return []
    const t: Tool[] = [{ label: 'Print', icon: 'print', onClick: guard(() => setPrinting(true)), title: 'Preview and print this document' }]
    if (d.voided) return t
    if (d.type === 'Q' && api.can(me, 'workorders.write')) t.push({ label: 'Work Order', icon: 'notes', onClick: guard(() => void convert('W')), disabled: busy, title: 'Create a work order from this quote' })
    if ((d.type === 'Q' || d.type === 'W') && api.can(me, 'invoices.write')) t.push({ label: 'Invoice', icon: 'notes', onClick: guard(() => void convert('I')), disabled: busy, title: d.type === 'W' ? 'Turn this work order into an invoice' : 'Create an invoice from this quote' })
    if (d.type === 'I' && api.can(me, 'credits.write')) t.push({ label: 'Credit', icon: 'notes', onClick: guard(() => { setCreditSeqs(d.lines.filter(l => l.amount !== 0).map(l => l.seq)); setCrediting(true) }), disabled: busy, title: 'Credit all or some lines of this invoice' })
    if ((d.type === 'I' || d.type === 'W') && d.customerId && api.can(me, 'payments.write')) t.push({ label: 'Payment', icon: 'save', onClick: guard(() => setPaying(true)), title: 'Receive a payment or deposit for this customer' })
    return t
  }
  const prefs = loadPrefs()
  const editing = d == null ? docType === 'Q' && api.can(me, 'quotes.write') : d.editable && api.can(me, ({ Q: 'quotes.write', W: 'workorders.write', I: 'invoices.write' } as Record<string, string>)[d.type] ?? '*')
  const status = d?.statuses.at(-1)?.status
  const t = d?.totals
  return (
    <>
      {!editing && <Toolbar tools={[
        ...tools(f => f),
        { label: 'Open', icon: 'open', onClick: () => setLookup(''), title: 'Open a document' },
        { label: 'Clear', icon: 'clear', onClick: clear, title: 'Clear the form' }]} />}
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
          {d && (d.type === 'I' || d.type === 'W' || d.type === 'C') && !d.voided && <span className="muted"> Total {money(d.totals.total)}{d.paid ? ` · ${d.type === 'W' ? 'deposits' : 'paid'} ${money(d.paid)}` : ''}{d.type === 'C' ? ' · applied to the invoice' : d.type === 'I' ? <> · balance due <b>{money(d.balance)}</b></> : ''}</span>}
        </div>
        {error && <p className="error">{error}</p>}
        {editing ? <QuoteEditor key={`${d?.id ?? 'new'}:${d?.type}:${d?.updatedAt}`} initial={d} onSaved={load} onOpen={() => setLookup('')} onNew={clear} extraTools={tools} /> : <>
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
              {d && d.type !== 'Q' && <Field label="Paid"><RO value={money(d.paid)} width="9rem" /></Field>}
              {d && d.type !== 'Q' && <Field label="Doc Balance"><RO value={money(d.balance)} width="9rem" /></Field>}
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
      {printing && d && <PrintDialog title={`${d.typeName.toLowerCase()} ${d.number}`} kind={kindOf(d.type)} canHidePrices
        hidePricesDefault={!!balance?.noPrices || (d.type === 'W' && !prefs.showPricesOnWorkOrder)}
        build={(hide, company) => renderDocument(d, { company, hidePrices: hide })}
        onPrinted={() => { void api.markPrinted(d.id).then(() => load(d.id)) }} onClose={() => setPrinting(false)} />}
      {paying && d?.customerId && <PaymentDialog customerId={d.customerId} customerName={d.customer.name ?? ''} preselect={d.id} onDone={() => load(d.id)} onClose={() => setPaying(false)} />}
      {crediting && d && (
        <Modal title={`Credit memo for invoice ${d.number}`} onClose={() => setCrediting(false)} width={720}>
          <div className="lookup-body">
            <p>Choose the lines to credit. The credit memo is applied to this invoice, lowering what the customer owes.</p>
            <table className="grid-table"><thead><tr><th /><th className="num">Qty</th><th>Description</th><th className="num">Amount</th></tr></thead>
              <tbody>{d.lines.map(l => <tr key={l.seq}><td><input type="checkbox" disabled={l.amount === 0} checked={creditSeqs.includes(l.seq)} onChange={e => setCreditSeqs(c => e.target.checked ? [...c, l.seq] : c.filter(x => x !== l.seq))} /></td>
                <td className="num">{l.qty}</td><td>{[l.partCode, l.description].filter(Boolean).join(' – ')}</td><td className="num">{money(l.amount)}</td></tr>)}</tbody></table>
            <p className="muted small">Tax is credited in proportion to the taxable lines you choose.</p>
            <div className="actions"><button className="primary" disabled={busy || creditSeqs.length === 0} onClick={() => void convert('C', creditSeqs)}>Create credit memo</button><button onClick={() => setCrediting(false)}>Cancel</button></div>
          </div>
        </Modal>)}
      {lookup !== null && (
        <Modal title={`Lookup ${TYPE_NAMES[docType]}s`} onClose={() => setLookup(null)} width={820}>
          <div className="lookup-body"><DocumentBrowser fixedType={docType} initialQuery={lookup} onPick={s => { setLookup(null); load(s.id) }} /></div>
        </Modal>
      )}
    </>
  )
}
