import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import * as api from './api'
import { DocumentTable } from './Documents'
import { money, plainDate } from './format'

export function CustomersPage() {
  const [q, setQ] = useState('')
  const [page, setPage] = useState<api.Page<api.CustomerSummary> | null>(null)
  const [offset, setOffset] = useState(0)
  const [error, setError] = useState('')

  useEffect(() => setOffset(0), [q])
  useEffect(() => {
    let live = true
    const t = setTimeout(() => {
      api.searchCustomers(q.trim(), offset).then(p => { if (live) { setPage(p); setError('') } })
        .catch(e => live && setError(e.message))
    }, 250) // debounce typing
    return () => { live = false; clearTimeout(t) }
  }, [q, offset])

  return (
    <>
      <h2>Customers</h2>
      <input className="search" placeholder="Name, account number or phone" value={q} onChange={e => setQ(e.target.value)} autoFocus />
      {error && <p className="error">{error}</p>}
      <table>
        <thead><tr><th>Account</th><th>Name</th><th>City</th><th>Phone</th><th className="num">Balance</th></tr></thead>
        <tbody>
          {page?.items.map(c => (
            <tr key={c.id}>
              <td><Link to={`/customers/${c.id}`}>{c.number}</Link></td>
              <td>{[c.lastName, c.firstName].filter(Boolean).join(', ')}</td>
              <td>{[c.city, c.state].filter(Boolean).join(', ')}</td>
              <td>{c.phone}</td>
              <td className="num">{money(c.balance)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {page && page.items.length === 0 && <p>No customers match.</p>}
      <div className="pager">
        <button disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - 25))}>Previous</button>
        <button disabled={!page?.hasMore} onClick={() => setOffset(offset + 25)}>Next</button>
      </div>
    </>
  )
}

export function CustomerPage({ tz: _tz }: { tz: string }) {
  const { id } = useParams()
  const [c, setC] = useState<api.CustomerDetail | null>(null)
  const [error, setError] = useState('')
  useEffect(() => { api.getCustomer(Number(id)).then(setC).catch(e => setError(e.message)) }, [id])
  if (error) return <p className="error">{error}</p>
  if (!c) return <p>Loading…</p>
  return (
    <>
      <p><Link to="/customers">← Customers</Link></p>
      <h2>{[c.lastName, c.firstName].filter(Boolean).join(', ')} <small>#{c.number}</small></h2>
      <dl className="grid">
        <dt>Address</dt><dd>{[c.address1, c.address2, [c.city, c.state, c.zip].filter(Boolean).join(' ')].filter(Boolean).join(', ')}</dd>
        <dt>Contact</dt><dd>{c.contactName}</dd>
        <dt>Email</dt><dd>{c.email}</dd>
        <dt>Phones</dt><dd>{c.phones.map(p => p.number + (p.ext ? ` x${p.ext}` : '')).join(' · ')}</dd>
        <dt>Tax</dt><dd>{c.taxExempt ? 'Exempt' : c.taxGroup}{c.taxId ? ` (${c.taxId})` : ''}</dd>
        <dt>Price category</dt><dd>{c.priceCategory}</dd>
        <dt>Credit limit</dt><dd>{c.creditLimit != null && money(c.creditLimit)}</dd>
        <dt>Last transaction</dt><dd>{c.lastTransactionOn && plainDate(c.lastTransactionOn)}</dd>
        <dt>Balance</dt><dd><strong>{money(c.balance)}</strong></dd>
        {c.noteInternal && <><dt>Internal note</dt><dd className="pre">{c.noteInternal}</dd></>}
        {c.notePrinted && <><dt>Printed note</dt><dd className="pre">{c.notePrinted}</dd></>}
      </dl>
      <h3>Documents</h3>
      <DocumentTable customerId={c.id} compact />
    </>
  )
}
