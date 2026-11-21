import { useEffect, useState } from 'react'
import * as api from '../api'
import { money } from '../format'
import { Modal, SkeletonRows, arrowMove } from '../shell/ui'

/** GQ7 "Lookup Customers": search box, name/code list, address + phone preview of the highlighted row. */
export function CustomerLookup({ onPick, onClose }: { onPick: (id: number) => void; onClose: () => void }) {
  const [q, setQ] = useState('')
  const [sort, setSort] = useState<'name' | 'code'>('name')
  const [offset, setOffset] = useState(0)
  const [page, setPage] = useState<api.Page<api.CustomerSummary> | null>(null)
  const [sel, setSel] = useState<number | null>(null)
  const [detail, setDetail] = useState<api.CustomerDetail | null>(null)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(true)

  useEffect(() => setOffset(0), [q, sort])
  useEffect(() => {
    let live = true
    setLoading(true)
    const t = setTimeout(() => api.searchCustomers(q.trim(), offset, 12, sort)
      .then(p => { if (live) { setPage(p); setSel(p.items[0]?.id ?? null); setError('') } }).catch(e => live && setError(e.message))
      .finally(() => live && setLoading(false)), 200)
    return () => { live = false; clearTimeout(t) }
  }, [q, sort, offset])
  useEffect(() => { document.querySelector('.lookup tr.sel')?.scrollIntoView({ block: 'nearest' }) }, [sel])
  useEffect(() => { setDetail(null); if (sel != null) api.getCustomer(sel).then(setDetail).catch(() => {}) }, [sel])

  return (
    <Modal title="Lookup Customers" onClose={onClose} width={820}>
      <div className="lookup">
        <div>
          <input autoFocus className="search" placeholder="Name, account number or phone" value={q} onChange={e => setQ(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter' && sel != null) onPick(sel); const m = arrowMove(e, page?.items.map(i => i.id) ?? [], sel); if (m != null) setSel(m) }} />
          <label className="inline">Order by <select value={sort} onChange={e => setSort(e.target.value as 'name' | 'code')}><option value="name">Name</option><option value="code">Code</option></select></label>
          {error && <p className="error">{error}</p>}
          <table className={'grid-table selectable' + (loading && page ? ' loading' : '')}>
            <thead><tr><th>Name (Last, First)</th><th>Code</th><th className="num">Balance</th></tr></thead>
            <tbody>
              {!page && <SkeletonRows cols={3} rows={8} />}
              {page?.items.map(c => (
                <tr key={c.id} className={c.id === sel ? 'sel' : ''} onClick={() => setSel(c.id)} onDoubleClick={() => onPick(c.id)}>
                  <td>{[c.lastName, c.firstName].filter(Boolean).join(', ')}</td><td>{c.number}</td><td className="num">{money(c.balance)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {page && page.items.length === 0 && <p className="muted">No customers match.</p>}
          <div className="pager">
            <button disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - 12))}>Previous</button>
            <button disabled={!page?.hasMore} onClick={() => setOffset(offset + 12)}>Next</button>
          </div>
        </div>
        <aside>
          <fieldset><legend>Customer Address</legend>
            {detail && <>{[detail.address1, detail.address2].filter(Boolean).map((l, i) => <div key={i}>{l}</div>)}<div>{[detail.city, detail.state, detail.zip].filter(Boolean).join(' ')}</div></>}
          </fieldset>
          <fieldset><legend>Phone</legend>{detail?.phones.map(p => <div key={p.slot}>{p.number}{p.ext ? ` x${p.ext}` : ''}</div>)}</fieldset>
        </aside>
      </div>
      <footer className="dialog-buttons">
        <button className="primary" disabled={sel == null} onClick={() => sel != null && onPick(sel)}>OK</button>
        <button onClick={onClose}>Cancel</button>
      </footer>
    </Modal>
  )
}
