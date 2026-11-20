import { useEffect, useState } from 'react'
import * as api from '../api'
import { money, plainDate } from '../format'
import { useShell } from '../shell/WindowContext'
import { Tabs } from '../shell/ui'

export const TYPE_NAMES: Record<string, string> = { Q: 'Quote', W: 'Work order', I: 'Invoice', C: 'Credit memo', R: 'Other' }
const TABS: [string, string][] = [['', 'All'], ['Q', 'Quotes'], ['W', 'Work orders'], ['I', 'Invoices'], ['C', 'Credits']]

/** Filterable document list. In a modal (`onPick`) it picks one; in a window (or on a customer) a click opens it. */
export function DocumentBrowser({ customerId, compact, onPick, fixedType, initialQuery = '' }:
  { customerId?: number; compact?: boolean; onPick?: (d: api.DocumentSummary) => void; fixedType?: string; initialQuery?: string }) {
  const shell = useShell()
  const [type, setType] = useState(fixedType ?? '')
  const [q, setQ] = useState(initialQuery)
  const [from, setFrom] = useState(''); const [to, setTo] = useState('')
  const [voided, setVoided] = useState(false)
  const [offset, setOffset] = useState(0)
  const [page, setPage] = useState<api.Page<api.DocumentSummary> | null>(null)
  const [error, setError] = useState('')
  const pick = (d: api.DocumentSummary) => onPick ? onPick(d) : shell.open('quote', d.id)

  useEffect(() => setOffset(0), [type, q, from, to, voided])
  useEffect(() => {
    let live = true
    const t = setTimeout(() => api.searchDocuments({ customerId, type: type || undefined, q: q.trim() || undefined, from: from || undefined,
      to: to || undefined, includeVoided: voided, offset, limit: compact ? 10 : 25 })
      .then(p => { if (live) { setPage(p); setError('') } }).catch(e => live && setError(e.message)), 250)
    return () => { live = false; clearTimeout(t) }
  }, [customerId, type, q, from, to, voided, offset, compact])

  return (
    <>
      {!fixedType && !compact && <Tabs tabs={TABS.map(t => t[1])} active={TABS.find(t => t[0] === type)![1]} onChange={l => setType(TABS.find(t => t[1] === l)![0])} />}
      {!compact && <div className="filters">
        <input className="search" autoFocus placeholder="Document #, PO or customer name" value={q} onChange={e => setQ(e.target.value)} />
        <label>From <input type="date" value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label>To <input type="date" value={to} onChange={e => setTo(e.target.value)} /></label>
        <label><input type="checkbox" checked={voided} onChange={e => setVoided(e.target.checked)} /> Include voided</label>
      </div>}
      {error && <p className="error">{error}</p>}
      <table className="grid-table selectable">
        <thead><tr><th>Number</th><th>Type</th><th>Date</th>{!compact && <th>Customer</th>}<th>PO</th><th className="num">Total</th></tr></thead>
        <tbody>
          {page?.items.map(d => (
            <tr key={d.id} className={d.voided ? 'voided' : ''} onClick={() => pick(d)}>
              <td>{d.number}{d.voided && ' (void)'}</td><td>{TYPE_NAMES[d.type] ?? d.type}</td><td>{plainDate(d.date)}</td>
              {!compact && <td>{d.customerName}</td>}<td>{d.poNumber}</td><td className="num">{money(d.total)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {page && page.items.length === 0 && <p className="muted">No documents match.</p>}
      <div className="pager">
        <button disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - (compact ? 10 : 25)))}>Previous</button>
        <button disabled={!page?.hasMore} onClick={() => setOffset(offset + (compact ? 10 : 25))}>Next</button>
      </div>
    </>
  )
}
