import { useCallback, useEffect, useState } from 'react'
import * as api from '../api'
import { instant, money, plainDate } from '../format'
import { renderStatement } from '../print/render'
import { PrintDialog } from '../print/PrintDialog'
import { useShell, useWinId } from '../shell/WindowContext'
import { Toolbar } from '../shell/ui'
import { CustomerLookup } from './CustomerLookup'
import { PaymentDialog } from './PaymentDialog'

const TYPE: Record<string, string> = { I: 'Invoice', W: 'Work order', C: 'Credit memo' }

/** Who owes what: customers with a balance on the left, the chosen customer's open items and payments on the right. */
export function Receivables({ initialCustomer }: { initialCustomer?: number }) {
  const { me, open, toast, setTitle } = useShell(), winId = useWinId()
  const [q, setQ] = useState(''); const [credit, setCredit] = useState(false)
  const [list, setList] = useState<api.ReceivableCustomer[] | null>(null)
  const [sel, setSel] = useState<number | null>(initialCustomer ?? null)
  const [acct, setAcct] = useState<api.Account | null>(null)
  const [pay, setPay] = useState(false); const [print, setPrint] = useState(false); const [pick, setPick] = useState(false)
  const [error, setError] = useState('')

  const loadList = useCallback(() => { api.getReceivables(q, credit).then(l => { setList(l); setError('') }).catch(e => setError(e.message)) }, [q, credit])
  useEffect(() => { const t = setTimeout(loadList, 200); return () => clearTimeout(t) }, [loadList])
  const loadAcct = useCallback(() => { if (sel != null) api.getAccount(sel).then(a => { setAcct(a); setTitle(winId, `Receivables – ${a.name}`) }).catch(e => setError(e.message)) }, [sel, setTitle, winId])
  useEffect(() => { setAcct(null); loadAcct() }, [loadAcct])
  const refresh = () => { loadAcct(); loadList() }

  async function voidPay(p: api.PaymentRow) {
    if (!window.confirm(`Void the ${money(p.amount)} payment? The amount goes back onto the documents it was applied to.`)) return
    try { await api.voidPayment(p.id); toast('Payment voided.'); refresh() } catch (e) { toast(e instanceof Error ? e.message : 'Could not void.', 'error') }
  }
  const today = new Date().toLocaleDateString('en-CA', { timeZone: me.companyTimeZone })
  return (
    <>
      <Toolbar tools={[
        { label: 'Payment', icon: 'save', onClick: () => setPay(true), disabled: !acct, title: acct ? 'Receive a payment from this customer' : 'Choose a customer first' },
        { label: 'Statement', icon: 'print', onClick: () => setPrint(true), disabled: !acct, title: 'Print a statement' },
        { label: 'Customer', icon: 'open', onClick: () => setPick(true), title: 'Find any customer' },
        { label: 'Refresh', icon: 'clear', onClick: refresh }]} />
      {error && <p className="formerr">{error}</p>}
      <div className="maint">
        <div>
          <input className="search" placeholder="Search customers…" value={q} onChange={e => setQ(e.target.value)} />
          <label className="chk"><input type="checkbox" checked={credit} onChange={e => setCredit(e.target.checked)} /> Show customers with credit instead of balances due</label>
          <div className="maint-list"><table className="grid-table"><thead><tr><th>Customer</th><th className="num">Balance</th></tr></thead>
            <tbody>{list?.map(c => <tr key={c.id} className={c.id === sel ? 'sel' : ''} onClick={() => setSel(c.id)} style={{ cursor: 'pointer' }}><td>{c.name}<small className="muted"> {c.number}</small></td><td className="num">{money(c.balance)}</td></tr>)}
              {list && list.length === 0 && <tr><td colSpan={2} className="muted">{credit ? 'No customer has credit.' : 'Nobody owes anything.'}</td></tr>}</tbody></table></div>
          {list && !credit && list.length > 0 && <p className="muted small">{list.length} customers owe {money(list.reduce((s, c) => s + c.balance, 0))}{list.length >= 100 ? ' (top 100 shown)' : ''}.</p>}
        </div>
        <div className="maint-form">
          {!acct && <p className="muted">{sel == null ? 'Choose a customer to see what they owe.' : 'Loading…'}</p>}
          {acct && <>
            <h3 style={{ margin: 0 }}>{acct.name} <small className="muted">{acct.number}</small> <button className="link" onClick={() => open('customer', acct.customerId)}>open customer ↗</button></h3>
            <p>Balance <b>{money(acct.balance)}</b>{acct.onAccount > 0 && <> · {money(acct.onAccount)} paid on account</>}</p>
            <table className="grid-table"><thead><tr><th>Document</th><th>Date</th><th>PO</th><th className="num">Amount</th><th className="num">Owed</th></tr></thead>
              <tbody>{acct.documents.map(d => <tr key={d.id} onClick={() => open('quote', d.id)} style={{ cursor: 'pointer' }} title="Open this document"><td>{TYPE[d.type] ?? d.type} {d.number}{d.voided && <span className="badge">VOID</span>}</td><td>{plainDate(d.date.slice(0, 10))}</td><td>{d.poNumber}</td><td className="num">{money(d.total)}</td><td className="num">{money(d.balance)}</td></tr>)}
                {acct.documents.length === 0 && <tr><td colSpan={5} className="muted">No open documents.</td></tr>}</tbody></table>
            <h4>Recent payments</h4>
            <table className="grid-table"><thead><tr><th>Date</th><th>Type</th><th>Reference</th><th className="num">Amount</th><th /></tr></thead>
              <tbody>{acct.payments.map(p => <tr key={p.id} className={p.voided ? 'void' : ''}><td>{instant(p.paidAt, me.timeZone)}</td><td>{p.type === 'D' ? 'Deposit' : 'Payment'}{p.category ? ` (${p.category})` : ''}</td><td>{p.description}{p.voided && <span className="badge">VOID</span>}</td><td className="num">{money(p.amount)}</td>
                <td>{me.isAdmin && !p.voided && <button className="x" title="Void this payment" onClick={() => voidPay(p)}>×</button>}</td></tr>)}
                {acct.payments.length === 0 && <tr><td colSpan={5} className="muted">No payments yet.</td></tr>}</tbody></table>
          </>}
        </div>
      </div>
      {pay && acct && <PaymentDialog customerId={acct.customerId} customerName={acct.name} onDone={refresh} onClose={() => setPay(false)} />}
      {print && acct && <PrintDialog title={`statement – ${acct.name}`} kind="statement" build={(_, company) => renderStatement(acct, { company }, today)} onClose={() => setPrint(false)} />}
      {pick && <CustomerLookup onClose={() => setPick(false)} onPick={id => { setPick(false); setSel(id) }} />}
    </>
  )
}
