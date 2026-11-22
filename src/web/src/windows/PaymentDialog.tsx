import { useEffect, useState } from 'react'
import * as api from '../api'
import { money, plainDate } from '../format'
import { useShell } from '../shell/WindowContext'
import { Field, Modal } from '../shell/ui'

const cents = (n: number) => Math.round(n * 100) / 100
const parse = (s: string) => (s.trim() === '' || !Number.isFinite(Number(s)) ? 0 : cents(Number(s)))

/** Receive a payment or deposit and apply it to what the customer owes (oldest first unless you say otherwise). */
export function PaymentDialog({ customerId, customerName, preselect, onDone, onClose }: {
  customerId: number; customerName: string; preselect?: number; onDone: () => void; onClose: () => void
}) {
  const shell = useShell()
  const [acct, setAcct] = useState<api.Account | null>(null)
  const [cats, setCats] = useState<api.MaintRow[]>([])
  const [amount, setAmount] = useState(''); const [type, setType] = useState<'P' | 'D'>('P'); const [cat, setCat] = useState(''); const [desc, setDesc] = useState('')
  const [applied, setApplied] = useState<Record<number, string>>({}); const [manual, setManual] = useState(false)
  const [busy, setBusy] = useState(false); const [error, setError] = useState('')

  useEffect(() => {
    api.getAccount(customerId).then(a => {
      setAcct(a)
      const d = a.documents.find(x => x.id === preselect && x.balance > 0)
      if (d) { setAmount(d.balance.toFixed(2)); setApplied({ [d.id]: d.balance.toFixed(2) }) }
    }).catch(e => setError(e.message))
    api.getMaintRows('pay-categories').then(setCats).catch(() => {})
  }, [customerId, preselect])

  const open = (acct?.documents ?? []).filter(d => d.balance > 0 && !d.voided)
  const total = parse(amount)
  const sumApplied = cents(open.reduce((s, d) => s + parse(applied[d.id] ?? ''), 0))
  function allocate(value: number) {   // oldest first, the preselected document before the rest
    let left = value; const next: Record<number, string> = {}
    for (const d of [...open].sort((a, b) => (a.id === preselect ? -1 : b.id === preselect ? 1 : 0))) {
      const take = Math.min(left, d.balance); if (take > 0) { next[d.id] = take.toFixed(2); left = cents(left - take) }
    }
    setApplied(next)
  }
  const changeAmount = (v: string) => { setAmount(v); if (!manual) allocate(parse(v)) }
  async function save() {
    setError('')
    if (total <= 0) return setError('Enter the amount received.')
    if (sumApplied > total) return setError('More is applied than the amount received.')
    setBusy(true)
    try {
      const r = await api.createPayment({ customerId, amount: total, type, category: cat || null, description: desc.trim() || null,
        applications: open.map(d => ({ documentId: d.id, amount: parse(applied[d.id] ?? '') })).filter(a => a.amount > 0) })
      shell.toast(r.onAccount > 0 ? `Payment saved; ${money(r.onAccount)} is held on account.` : 'Payment saved.'); onDone(); onClose()
    } catch (e) { setError(e instanceof Error ? e.message : 'Could not save the payment.') } finally { setBusy(false) }
  }

  return (
    <Modal title={`Receive payment – ${customerName}`} onClose={onClose} width={760}>
      <div className="lookup-body">
        {acct && <p>Balance owed: <b>{money(acct.balance)}</b>{acct.onAccount > 0 && <> · on account: {money(acct.onAccount)}</>}</p>}
        <Field label="Amount received"><input autoFocus inputMode="decimal" value={amount} onChange={e => changeAmount(e.target.value)} style={{ width: '9rem' }} /></Field>
        <Field label="Type"><label className="radio"><input type="radio" checked={type === 'P'} onChange={() => setType('P')} /> Payment</label>
          <label className="radio"><input type="radio" checked={type === 'D'} onChange={() => setType('D')} /> Deposit (on a work order)</label></Field>
        <Field label="Paid by"><select value={cat} onChange={e => setCat(e.target.value)}><option value="">(not recorded)</option>{cats.map(c => <option key={String(c.code)} value={String(c.code)}>{String(c.description || c.code)}</option>)}</select></Field>
        <Field label="Reference"><input value={desc} maxLength={80} placeholder="Check number, last 4 of card…" onChange={e => setDesc(e.target.value)} style={{ width: '18rem' }} /></Field>
        <table className="grid-table"><thead><tr><th>Document</th><th>Date</th><th>PO</th><th className="num">Owed</th><th className="num">Apply</th></tr></thead>
          <tbody>{open.map(d => <tr key={d.id}><td>{{ I: 'Invoice', W: 'Work order' }[d.type] ?? d.type} {d.number}</td><td>{plainDate(d.date.slice(0, 10))}</td><td>{d.poNumber}</td><td className="num">{money(d.balance)}</td>
            <td className="num"><input inputMode="decimal" value={applied[d.id] ?? ''} onChange={e => { setManual(true); setApplied(a => ({ ...a, [d.id]: e.target.value })) }} style={{ width: '6.5rem' }} /></td></tr>)}
            {acct && open.length === 0 && <tr><td colSpan={5} className="muted">Nothing is owed; the whole amount will be held on account.</td></tr>}</tbody></table>
        <p className="muted small">Applied {money(sumApplied)} · {total > sumApplied ? <>{money(cents(total - sumApplied))} will be held on account</> : 'nothing left over'}.
          {manual && <> <button className="link" onClick={() => { setManual(false); allocate(total) }}>Apply automatically</button></>}</p>
        {error && <p className="formerr">{error}</p>}
        <div className="actions"><button className="primary" onClick={save} disabled={busy || total <= 0}>Save payment</button><button onClick={onClose}>Cancel</button></div>
      </div>
    </Modal>
  )
}
