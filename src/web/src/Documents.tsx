import { useEffect, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import * as api from './api'
import { instant, money, plainDate } from './format'

export const TYPE_NAMES: Record<string, string> = { Q: 'Quote', W: 'Work order', I: 'Invoice', C: 'Credit memo', R: 'Other' }
const TABS: [string, string][] = [['', 'All'], ['Q', 'Quotes'], ['W', 'Work orders'], ['I', 'Invoices'], ['C', 'Credits']]

/** Document table with paging; used by the Documents page and, filtered, by the customer page. */
export function DocumentTable({ customerId, type, q, from, to, includeVoided, compact }: api.DocumentQuery & { compact?: boolean }) {
  const [offset, setOffset] = useState(0)
  const [page, setPage] = useState<api.Page<api.DocumentSummary> | null>(null)
  const [error, setError] = useState('')
  useEffect(() => setOffset(0), [customerId, type, q, from, to, includeVoided])
  useEffect(() => {
    let live = true
    const t = setTimeout(() => {
      api.searchDocuments({ customerId, type, q, from, to, includeVoided, offset })
        .then(p => { if (live) { setPage(p); setError('') } }).catch(e => live && setError(e.message))
    }, 250)
    return () => { live = false; clearTimeout(t) }
  }, [customerId, type, q, from, to, includeVoided, offset])

  return (
    <>
      {error && <p className="error">{error}</p>}
      <table>
        <thead><tr><th>Number</th><th>Type</th><th>Date</th>{!compact && <th>Customer</th>}<th>PO</th><th className="num">Total</th></tr></thead>
        <tbody>
          {page?.items.map(d => (
            <tr key={d.id} className={d.voided ? 'voided' : ''}>
              <td><Link to={`/documents/${d.id}`}>{d.number}</Link>{d.voided && ' (void)'}</td>
              <td>{TYPE_NAMES[d.type] ?? d.type}</td>
              <td>{plainDate(d.date)}</td>
              {!compact && <td>{d.customerId ? <Link to={`/customers/${d.customerId}`}>{d.customerName}</Link> : d.customerName}</td>}
              <td>{d.poNumber}</td>
              <td className="num">{money(d.total)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {page && page.items.length === 0 && <p>No documents match.</p>}
      <div className="pager">
        <button disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - 25))}>Previous</button>
        <button disabled={!page?.hasMore} onClick={() => setOffset(offset + 25)}>Next</button>
      </div>
    </>
  )
}

export function DocumentsPage() {
  // Filters live in the URL so a list can be bookmarked and the Back button restores it.
  const [sp, setSp] = useSearchParams()
  const type = sp.get('type') ?? '', q = sp.get('q') ?? '', from = sp.get('from') ?? '', to = sp.get('to') ?? ''
  const includeVoided = sp.get('voided') === '1'
  const set = (k: string, v: string) => setSp(prev => { const n = new URLSearchParams(prev); v ? n.set(k, v) : n.delete(k); return n }, { replace: true })
  return (
    <>
      <h2>Documents</h2>
      <div className="tabs">
        {TABS.map(([code, label]) => <button key={code} className={type === code ? 'on' : ''} onClick={() => set('type', code)}>{label}</button>)}
      </div>
      <div className="filters">
        <input className="search" placeholder="Document #, PO or customer name" value={q} onChange={e => set('q', e.target.value)} autoFocus />
        <label>From <input type="date" value={from} onChange={e => set('from', e.target.value)} /></label>
        <label>To <input type="date" value={to} onChange={e => set('to', e.target.value)} /></label>
        <label><input type="checkbox" checked={includeVoided} onChange={e => set('voided', e.target.checked ? '1' : '')} /> Include voided</label>
      </div>
      <DocumentTable type={type || undefined} q={q.trim() || undefined} from={from || undefined} to={to || undefined} includeVoided={includeVoided} />
    </>
  )
}

function Party({ p }: { p: api.DocumentParty }) {
  return (
    <address>
      {p.name && <strong>{p.name}<br /></strong>}
      {[p.address1, p.address2].filter(Boolean).map((l, i) => <span key={i}>{l}<br /></span>)}
      {[p.city, p.state, p.zip].filter(Boolean).join(' ')}
      {p.phones.length > 0 && <><br />{p.phones.join(' · ')}</>}
    </address>
  )
}

export function DocumentPage({ tz }: { tz: string }) {
  const { id } = useParams()
  const [d, setD] = useState<api.DocumentDetail | null>(null)
  const [error, setError] = useState('')
  useEffect(() => { api.getDocument(Number(id)).then(setD).catch(e => setError(e.status === 404 ? 'Document not found.' : e.message)) }, [id])
  if (error) return <p className="error">{error}</p>
  if (!d) return <p>Loading…</p>
  const t = d.totals
  return (
    <>
      <p><Link to="/documents">← Documents</Link>{d.customerId && <> · <Link to={`/customers/${d.customerId}`}>Customer</Link></>}</p>
      <h2>{d.typeName} {d.number} {d.voided && <span className="badge">VOID</span>}</h2>
      <div className="two">
        <div><h4>Customer (as printed)</h4><Party p={d.customer} /></div>
        {d.jobSite && <div><h4>Job site</h4><Party p={d.jobSite} /></div>}
        <dl className="grid">
          <dt>Date</dt><dd>{plainDate(d.date)}</dd>
          {d.lossDate && <><dt>Loss date</dt><dd>{plainDate(d.lossDate)}</dd></>}
          {d.poNumber && <><dt>PO</dt><dd>{d.poNumber}</dd></>}
          {d.policyNumber && <><dt>Policy</dt><dd>{d.policyNumber}</dd></>}
          {d.claimNumber && <><dt>Claim</dt><dd>{d.claimNumber}</dd></>}
          {d.sourceDocumentNumber && <><dt>From</dt><dd>{d.sourceDocumentNumber}</dd></>}
          {d.salesPerson && <><dt>Salesperson</dt><dd>{d.salesPerson}</dd></>}
          {d.terms && <><dt>Terms</dt><dd>{d.terms}</dd></>}
          {d.taxGroup && <><dt>Tax group</dt><dd>{d.taxGroup}</dd></>}
          {d.discountPercent != null && <><dt>Discount</dt><dd>{d.discountPercent}%</dd></>}
          <dt>Created</dt><dd>{instant(d.createdAt, tz)}</dd>
        </dl>
      </div>

      <table>
        <thead><tr><th>#</th><th>Qty</th><th>Part</th><th>Description</th><th>Size</th><th className="num">Unit</th><th className="num">Amount</th></tr></thead>
        <tbody>
          {d.lines.map(l => (
            <tr key={l.seq} className={l.parentSeq != null ? 'child' : ''}>
              <td>{l.seq}</td><td>{l.qty}</td><td>{l.partCode}</td>
              <td>{[l.thickness, l.description, l.shape].filter(Boolean).join(' ')}</td>
              <td>{l.dims}</td>
              <td className="num">{l.unitPrice != null && money(l.unitPrice)}</td>
              <td className="num">{l.amount !== 0 && money(l.amount)}{l.taxable ? '' : l.amount !== 0 ? ' *' : ''}</td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr><td colSpan={6} className="num">Subtotal</td><td className="num">{money(t.lines)}</td></tr>
          {d.taxes.map(x => <tr key={x.code}><td colSpan={6} className="num">Tax {x.code} ({x.percent}%)</td><td className="num">{money(x.amount)}</td></tr>)}
          <tr className="total"><td colSpan={6} className="num">Total</td><td className="num">{money(t.total)}</td></tr>
        </tfoot>
      </table>
      <p><small>* not taxable. Glass {money(t.glass)} · Parts {money(t.part)} · Labor {money(t.labor)} · Other {money(t.custom)}</small></p>

      {d.specialInstructions && <><h4>Special instructions</h4><p className="pre">{d.specialInstructions}</p></>}
      {d.workOrderInstructions && <><h4>Work order instructions</h4><p className="pre">{d.workOrderInstructions}</p></>}
      {d.notePrinted && <><h4>Printed note</h4><p className="pre">{d.notePrinted}</p></>}
      {d.noteInternal && <><h4>Internal note</h4><p className="pre">{d.noteInternal}</p></>}
      {d.installers.length > 0 && <><h4>Installers</h4><ul>{d.installers.map((i, n) => <li key={n}>{i.name ?? i.code}{i.hours != null && ` — ${i.hours} h`}{i.amount != null && ` — ${money(i.amount)}`}</li>)}</ul></>}
      {d.statuses.length > 0 && <><h4>Status history</h4><ul>{d.statuses.map((s, n) => <li key={n}>{s.at ? instant(s.at, tz) : 'unknown time'} — {s.status}{s.note && `: ${s.note}`}</li>)}</ul></>}
      {d.revisions.length > 0 && <><h4>Revisions</h4><ul>{d.revisions.map((r, n) => <li key={n}>{instant(r.at, tz)}{r.total != null && ` — ${money(r.total)}`}{r.comment && ` — ${r.comment}`}</li>)}</ul></>}
    </>
  )
}
