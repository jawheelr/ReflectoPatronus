import { useEffect, useState } from 'react'
import * as api from '../api'
import { useShell } from '../shell/WindowContext'
import { Field, Toolbar } from '../shell/ui'
import { CustomerLookup } from './CustomerLookup'
import { PartPicker } from './QuoteEditor'

const int = (s: string) => (s.trim() === '' || !Number.isInteger(Number(s)) ? NaN : Number(s))

/** The waste allowance added to each side of a piece of glass before it is priced, per thickness (whole inches + a fraction). */
export function LossTable() {
  const { me, toast } = useShell(), admin = api.can(me, 'catalog.write')
  type R = { tn: string; td: string; li: string; ln: string; ld: string }
  const [rows, setRows] = useState<R[]>([]); const [dirty, setDirty] = useState(false); const [error, setError] = useState('')
  useEffect(() => { api.getLoss().then(r => setRows(r.map(x => ({ tn: String(x.thickNum), td: String(x.thickDen), li: String(x.lossInt), ln: String(x.lossNum), ld: String(x.lossDen) })))).catch(e => setError(e.message)) }, [])
  const set = (i: number, c: Partial<R>) => { setRows(rs => rs.map((r, k) => k === i ? { ...r, ...c } : r)); setDirty(true) }
  async function save() {
    const out = rows.map(r => ({ thickNum: int(r.tn), thickDen: int(r.td), lossInt: int(r.li), lossNum: int(r.ln), lossDen: int(r.ld) }))
    if (out.some(r => Object.values(r).some(Number.isNaN))) return setError('Every box needs a whole number.')
    try { await api.putLoss(out); setDirty(false); setError(''); toast('Saved.') } catch (e) { setError((e as Error).message) }
  }
  const box = (v: string, on: (s: string) => void) => <input value={v} inputMode="numeric" onChange={e => on(e.target.value)} style={{ width: '3.5rem' }} disabled={!admin} />
  return (
    <div className="form">
      <Toolbar tools={[{ label: 'Save', icon: 'save', onClick: save, disabled: !admin || !dirty }]} />
      {error && <p className="formerr">{error}</p>}
      <p className="muted">Glass is cut larger than its finished size. For each thickness, this much is added to <b>both</b> the width and the height before the square footage is worked out. Example: 1/4 inch glass with 1 and 1/2 adds 1½ inches to each side.</p>
      <table className="grid-table" style={{ maxWidth: '40rem' }}><thead><tr><th>Thickness</th><th>Allowance (inches + fraction)</th><th /></tr></thead>
        <tbody>{rows.map((r, i) => <tr key={i}>
          <td>{box(r.tn, v => set(i, { tn: v }))} / {box(r.td, v => set(i, { td: v }))}</td>
          <td>{box(r.li, v => set(i, { li: v }))} and {box(r.ln, v => set(i, { ln: v }))} / {box(r.ld, v => set(i, { ld: v }))}</td>
          <td>{admin && <button className="x" onClick={() => { setRows(rs => rs.filter((_, k) => k !== i)); setDirty(true) }}>×</button>}</td></tr>)}</tbody></table>
      {admin && <p><button onClick={() => { setRows(rs => [...rs, { tn: '1', td: '4', li: '0', ln: '0', ld: '1' }]); setDirty(true) }}>+ Add thickness</button></p>}
    </div>
  )
}

interface SP { key: number; partId: number | null; partCode: string; thicks: { num: number; den: number; text: string }[]; thick: string; cat: string; price: string }
let spKey = 1

/** Prices agreed with one customer. A special price beats the catalog price on that customer's quotes. */
export function SpecialPricing({ initialCustomer }: { initialCustomer?: number }) {
  const { me, toast } = useShell(), admin = api.can(me, 'customers.write')
  const [cid, setCid] = useState<number | null>(initialCustomer ?? null); const [name, setName] = useState('')
  const [rows, setRows] = useState<SP[]>([]); const [dirty, setDirty] = useState(false); const [pick, setPick] = useState(false); const [error, setError] = useState('')
  const [cats, setCats] = useState<api.MaintRow[]>([])
  useEffect(() => { api.getMaintRows('price-categories').then(setCats).catch(() => {}) }, [])
  useEffect(() => {
    if (cid == null) return
    api.getCustomer(cid).then(c => setName(`${c.lastName}${c.firstName ? ', ' + c.firstName : ''}`)).catch(() => {})
    api.getSpecialPrices(cid).then(r => { setRows(r.map(x => ({ key: spKey++, partId: x.partId, partCode: x.partCode, thicks: [{ num: x.thickNum, den: x.thickDen, text: x.thickDen ? `${x.thickNum}/${x.thickDen}` : 'none' }], thick: `${x.thickNum}/${x.thickDen}`, cat: x.priceCategory ?? '', price: String(x.price) }))); setDirty(false); setError('') }).catch(e => setError(e.message))
  }, [cid])
  const set = (key: number, c: Partial<SP>) => { setRows(rs => rs.map(r => r.key === key ? { ...r, ...c } : r)); setDirty(true) }
  async function save() {
    const out = rows.map(r => { const [tn, td] = (r.thick || '0/0').split('/').map(Number); return { partId: r.partId ?? 0, thickNum: tn, thickDen: td, priceCategory: r.cat || null, price: Number(r.price) } })
    if (rows.some(r => r.partId == null || !Number.isFinite(Number(r.price)) || r.price.trim() === '' || !r.thick)) return setError('Each line needs a part, a thickness and a price.')
    try { await api.putSpecialPrices(cid!, out); setDirty(false); setError(''); toast('Saved.') } catch (e) { setError((e as Error).message) }
  }
  return (
    <div className="form">
      <Toolbar tools={[{ label: 'Save', icon: 'save', onClick: save, disabled: !admin || !dirty || cid == null }, { label: 'Customer', icon: 'open', onClick: () => { if (!dirty || window.confirm('Discard your unsaved changes?')) setPick(true) } }]} />
      {error && <p className="formerr">{error}</p>}
      <Field label="Customer"><input readOnly className="ro" value={name} placeholder="Choose a customer" style={{ width: '22rem' }} onClick={() => setPick(true)} /><button className="dots" onClick={() => setPick(true)}>…</button></Field>
      {cid != null && <>
        <table className="grid-table"><thead><tr><th>Part</th><th>Thickness</th><th>Price category</th><th className="num">Price</th><th /></tr></thead>
          <tbody>{rows.map(r => <tr key={r.key}>
            <td><PartPicker source="G" label={r.partCode} onPick={p => set(r.key, { partId: p.id, partCode: p.code, thicks: p.thicknesses, thick: p.thicknesses.length === 1 ? `${p.thicknesses[0].num}/${p.thicknesses[0].den}` : '' })} /></td>
            <td><select value={r.thick} onChange={e => set(r.key, { thick: e.target.value })} disabled={r.partId == null}><option value="">Choose</option>{r.thicks.map(t => <option key={t.text} value={`${t.num}/${t.den}`}>{t.text || 'none'}</option>)}</select></td>
            <td><select value={r.cat} onChange={e => set(r.key, { cat: e.target.value })}><option value="">(any category)</option>{cats.map(c => <option key={String(c.code)} value={String(c.code)}>{String(c.code)}</option>)}</select></td>
            <td className="num"><input value={r.price} inputMode="decimal" onChange={e => set(r.key, { price: e.target.value })} style={{ width: '6rem' }} disabled={!admin} /></td>
            <td>{admin && <button className="x" onClick={() => { setRows(rs => rs.filter(x => x.key !== r.key)); setDirty(true) }}>×</button>}</td></tr>)}
            {rows.length === 0 && <tr><td colSpan={5} className="muted">No special prices for this customer.</td></tr>}</tbody></table>
        {admin && <p><button onClick={() => { setRows(rs => [...rs, { key: spKey++, partId: null, partCode: '', thicks: [], thick: '', cat: '', price: '' }]); setDirty(true) }}>+ Add price</button></p>}
        <p className="muted small">The price is per square foot for glass. This screen adds prices for glass types; a price you type on a quote line still wins.</p></>}
      {pick && <CustomerLookup onClose={() => setPick(false)} onPick={id => { setPick(false); setCid(id) }} />}
    </div>
  )
}
