import { useCallback, useEffect, useState } from 'react'
import * as api from '../api'
import { instant, money } from '../format'
import { Modal, Tabs } from '../shell/ui'
import { useShell } from '../shell/WindowContext'

const TABS = ['Stock', 'Needs & ordering', 'Purchase orders', 'Suppliers & items']
const n = (x: number) => (Math.round(x * 1000) / 1000).toLocaleString('en-US', { maximumFractionDigits: 3 })
const esc = (s: string) => s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]!))
const ago = (iso: string) => { const m = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000)); return m < 60 ? `${m} min ago` : m < 2880 ? `${Math.round(m / 60)} h ago` : `${Math.round(m / 1440)} days ago` }
/** What the supplier last said it had, with when we asked. */
function SupplierSays({ s }: { s: api.SupplierStockInfo | null }) {
  if (!s) return <span className="muted">—</span>
  const text = s.status === 'out' ? 'Out' : s.qty != null ? n(s.qty) : s.status === 'in_stock' ? 'In stock' : '?'
  return <span className={s.status === 'out' ? 'neg' : s.status === 'unknown' ? 'muted' : ''} title={`${s.note ? s.note + '. ' : ''}Checked ${ago(s.checkedAt)}`}>{text} <small className="muted">{ago(s.checkedAt)}</small></span>
}
const poNo = (id: number) => `PO-${String(id).padStart(5, '0')}`

/** Inventory and purchasing. Off until an administrator switches it on; while off nothing in the program touches stock. */
export function InventoryWindow() {
  const { me, toast } = useShell()
  const [on, setOn] = useState<boolean | null>(null)
  const [tab, setTab] = useState(TABS[0])
  const [tick, setTick] = useState(0)
  const bump = useCallback(() => setTick(t => t + 1), [])
  useEffect(() => { api.getInventoryOn().then(setOn).catch(e => toast((e as Error).message, 'error')) }, [toast])
  if (!api.can(me, 'inventory.manage')) return <div className="form"><p>You do not have permission to use inventory.</p></div>
  if (on === null) return <div className="form"><p>Loading…</p></div>
  const flip = async (v: boolean) => { try { setOn((await api.setInventoryOn(v)).on); toast(v ? 'Inventory is on.' : 'Inventory is off. Your counts and orders are kept.') } catch (e) { toast((e as Error).message, 'error') } }
  const switchBox = (
    <label className="check"><input type="checkbox" checked={on} onChange={e => void flip(e.target.checked)} /> Use inventory (stock counts, work order reservations, ordering from suppliers)</label>
  )
  if (!on) return (
    <div className="form inv">
      <h3>Inventory is turned off</h3>
      <p>While it is off, the program ignores stock completely: invoices do not use up anything, and no inventory screens or checks appear on paperwork. Turn it on when you are ready to count stock.</p>
      {switchBox}
      <p className="muted small">Stock is counted per supplier item (the items you import under Supplier Pricing), in the supplier's own unit such as sheets. To have glass and parts used up automatically, link each part and thickness to its supplier item under Supplier Pricing. Turning inventory off later keeps everything you entered.</p>
    </div>)
  return (
    <div className="form inv">
      <Tabs tabs={TABS} active={tab} onChange={setTab} />
      {tab === TABS[0] && <StockTab tick={tick} bump={bump} />}
      {tab === TABS[1] && <NeedsTab bump={bump} go={() => setTab(TABS[2])} />}
      {tab === TABS[2] && <OrdersTab tick={tick} bump={bump} />}
      {tab === TABS[3] && <SuppliersTab bump={bump} />}
      <hr />{switchBox}
    </div>)
}

function StockTab({ tick, bump }: { tick: number; bump: () => void }) {
  const { toast } = useShell()
  const [rows, setRows] = useState<api.InvItem[]>([]); const [q, setQ] = useState('')
  const [sel, setSel] = useState<api.InvItem | null>(null); const [adding, setAdding] = useState(false)
  useEffect(() => { api.getInvItems().then(r => { setRows(r); setSel(s => s && r.find(x => x.id === s.id) || null) }).catch(e => toast((e as Error).message, 'error')) }, [tick, toast])
  const shown = rows.filter(r => !q || `${r.sku} ${r.description ?? ''} ${r.supplier} ${r.location ?? ''}`.toLowerCase().includes(q.toLowerCase()))
  return (
    <>
      <p className="muted">Open work orders set stock aside (<strong>committed</strong>); it leaves stock when the work order becomes an invoice. Open quotes count as expected demand in the needs estimate.</p>
      <div className="row"><input className="search" placeholder="Find an item…" value={q} onChange={e => setQ(e.target.value)} /><button className="primary" onClick={() => setAdding(true)}>Track a supplier item</button></div>
      <div className="maint-list">
        <table className="grid-table"><thead><tr><th>Supplier</th><th>SKU</th><th>Description</th><th>Where</th><th className="num">On hand</th><th className="num">Committed</th><th className="num">Available</th><th className="num">On order</th><th className="num">Supplier says</th><th>Unit</th></tr></thead>
          <tbody>{shown.map(r => <tr key={r.id} className={(sel?.id === r.id ? 'sel ' : '') + (!r.active ? 'muted' : '')} onClick={() => setSel(r)}>
            <td>{r.supplier}</td><td>{r.sku}</td><td>{r.description}</td><td>{r.location}</td><td className="num">{n(r.onHand)}</td><td className="num">{n(r.committed)}</td>
            <td className={'num' + (r.available < 0 ? ' neg' : '')}>{n(r.available)}</td><td className="num">{n(r.onOrder)}</td><td className="num"><SupplierSays s={r.supplierStock} /></td><td>{r.unit}</td></tr>)}
            {shown.length === 0 && <tr><td colSpan={10} className="muted">{rows.length ? 'No match.' : 'Nothing is tracked yet. Choose “Track a supplier item”.'}</td></tr>}</tbody></table>
      </div>
      {sel && <ItemPanel key={sel.id} item={sel} done={bump} />}
      {adding && <AddItem onClose={() => setAdding(false)} done={() => { setAdding(false); bump() }} />}
    </>)
}

function ItemPanel({ item, done }: { item: api.InvItem; done: () => void }) {
  const { toast } = useShell()
  const [f, setF] = useState({ unit: item.unit, reorderPoint: String(item.reorderPoint), orderMultiple: String(item.orderMultiple), leadDays: String(item.leadDays), location: item.location ?? '' })
  const [count, setCount] = useState(''); const [delta, setDelta] = useState(''); const [note, setNote] = useState('')
  const [moves, setMoves] = useState<api.InvMove[]>([])
  useEffect(() => { api.getInvMoves(item.id).then(setMoves).catch(() => undefined) }, [item.id, item.onHand])
  const run = async (fn: () => Promise<unknown>, ok: string) => { try { await fn(); toast(ok); done() } catch (e) { toast((e as Error).message, 'error') } }
  return (
    <fieldset><legend>{item.sku} — {item.supplier}</legend>
      <div className="row"><span>Supplier says: <SupplierSays s={item.supplierStock} /></span>
        <button onClick={() => run(async () => { const r = await api.pollItemStock(item.id); if (r.unknown) throw new Error(r.message) }, 'Checked with the supplier.')}>Check with supplier now</button></div>
      <div className="row">
        <label>Counted now <input size={7} value={count} onChange={e => setCount(e.target.value)} /></label>
        <button disabled={count === '' || isNaN(Number(count))} onClick={() => run(() => api.adjustInvItem(item.id, { setTo: Number(count), note }), 'Count saved.').then(() => setCount(''))}>Set count</button>
        <label>or add / remove <input size={7} placeholder="-2" value={delta} onChange={e => setDelta(e.target.value)} /></label>
        <button disabled={delta === '' || !Number(delta)} onClick={() => run(() => api.adjustInvItem(item.id, { qty: Number(delta), note }), 'Adjusted.').then(() => setDelta(''))}>Apply</button>
        <label>Note <input value={note} onChange={e => setNote(e.target.value)} placeholder="recount, breakage…" /></label>
      </div>
      <div className="row">
        <label>Reorder at <input size={6} value={f.reorderPoint} onChange={e => setF({ ...f, reorderPoint: e.target.value })} /></label>
        <label>Order in multiples of <input size={5} value={f.orderMultiple} onChange={e => setF({ ...f, orderMultiple: e.target.value })} /></label>
        <label>Lead time (days) <input size={4} value={f.leadDays} onChange={e => setF({ ...f, leadDays: e.target.value })} /></label>
        <label>Unit <input size={7} value={f.unit} onChange={e => setF({ ...f, unit: e.target.value })} /></label>
        <label>Where <input size={12} value={f.location} onChange={e => setF({ ...f, location: e.target.value })} /></label>
        <button onClick={() => run(() => api.editInvItem(item.id, { unit: f.unit, reorderPoint: Number(f.reorderPoint), orderMultiple: Number(f.orderMultiple), leadDays: Number(f.leadDays), location: f.location }), 'Saved.')}>Save settings</button>
        <button onClick={() => run(() => api.editInvItem(item.id, { active: !item.active }), item.active ? 'Item hidden from estimates.' : 'Item active again.')}>{item.active ? 'Stop tracking' : 'Track again'}</button>
      </div>
      <p className="muted small">Using about {n(item.perDay)} {item.unit}/day over the last 90 days.</p>
      <details><summary>History ({moves.length})</summary>
        <table className="grid-table"><thead><tr><th>When</th><th>What</th><th className="num">Qty</th><th>Note</th><th>By</th></tr></thead>
          <tbody>{moves.map((m, i) => <tr key={i}><td>{m.at.slice(0, 16).replace('T', ' ')}</td><td>{({ receive: 'Received', use: 'Used', adjust: 'Adjusted', count: 'Counted' } as Record<string, string>)[m.kind]}</td><td className="num">{m.qty > 0 ? '+' : ''}{n(m.qty)}</td><td>{m.note}</td><td>{m.user}</td></tr>)}</tbody></table></details>
    </fieldset>)
}

function AddItem({ onClose, done }: { onClose: () => void; done: () => void }) {
  const { toast } = useShell()
  const [q, setQ] = useState(''); const [rows, setRows] = useState<api.InvCatalogRow[]>([]); const [pick, setPick] = useState<api.InvCatalogRow | null>(null)
  const [f, setF] = useState({ unit: 'sheet', onHand: '', reorderPoint: '', orderMultiple: '1', leadDays: '7' })
  useEffect(() => { const t = setTimeout(() => api.getInvCatalog(q).then(setRows).catch(() => undefined), 200); return () => clearTimeout(t) }, [q])
  const save = async () => {
    try { await api.addInvItem({ supplierItemId: pick!.supplierItemId, unit: f.unit, onHand: f.onHand ? Number(f.onHand) : null, reorderPoint: f.reorderPoint ? Number(f.reorderPoint) : null, orderMultiple: Number(f.orderMultiple) || 1, leadDays: Number(f.leadDays) || 0 }); toast('Now tracked.'); done() }
    catch (e) { toast((e as Error).message, 'error') }
  }
  return (
    <Modal title="Track a supplier item" onClose={onClose} width={760}>
      <div className="lookup-body">
        <p>Pick from the supplier items you imported under Supplier Pricing. {rows.length === 0 && q === '' && 'None found yet: import a supplier price list first.'}</p>
        <input className="search" autoFocus placeholder="Search supplier, SKU or description…" value={q} onChange={e => setQ(e.target.value)} />
        <div className="maint-list" style={{ maxHeight: '14rem' }}><table className="grid-table"><thead><tr><th>Supplier</th><th>SKU</th><th>Description</th><th className="num">Cost</th><th>Linked parts</th></tr></thead>
          <tbody>{rows.map(r => <tr key={r.supplierItemId} className={(pick?.supplierItemId === r.supplierItemId ? 'sel ' : '') + (r.tracked ? 'muted' : '')} onClick={() => !r.tracked && setPick(r)}>
            <td>{r.supplier}</td><td>{r.sku}</td><td>{r.description}</td><td className="num">{money(r.unitCost)}</td><td>{r.tracked ? 'already tracked' : r.links ? `${r.links} linked` : 'not linked: counts by hand only'}</td></tr>)}</tbody></table></div>
        {pick && <div className="row">
          <label>Unit <input size={7} value={f.unit} onChange={e => setF({ ...f, unit: e.target.value })} /></label>
          <label>On hand now <input size={6} value={f.onHand} onChange={e => setF({ ...f, onHand: e.target.value })} /></label>
          <label>Reorder at <input size={6} value={f.reorderPoint} onChange={e => setF({ ...f, reorderPoint: e.target.value })} /></label>
          <label>Order in multiples of <input size={5} value={f.orderMultiple} onChange={e => setF({ ...f, orderMultiple: e.target.value })} /></label>
          <label>Lead days <input size={4} value={f.leadDays} onChange={e => setF({ ...f, leadDays: e.target.value })} /></label></div>}
        <div className="actions"><button className="primary" disabled={!pick} onClick={() => void save()}>Track it</button><button onClick={onClose}>Cancel</button></div>
      </div>
    </Modal>)
}

function NeedsTab({ bump, go }: { bump: () => void; go: () => void }) {
  const { toast } = useShell()
  const [cover, setCover] = useState('14'); const [d, setD] = useState<api.InvNeed[]>([])
  useEffect(() => { api.getInvNeeds(Number(cover) || 0).then(r => setD(r.items)).catch(e => toast((e as Error).message, 'error')) }, [cover, toast])
  const bySupplier = [...new Set(d.map(x => x.supplierId))].map(id => ({ id, name: d.find(x => x.supplierId === id)!.supplier, rows: d.filter(x => x.supplierId === id) }))
  const make = async (id: number | null) => {
    try { const r = await api.poFromNeeds(id, Number(cover) || 0); toast(r.created.length ? `Made ${r.created.length} draft order${r.created.length > 1 ? 's' : ''}.` : 'Nothing to order.'); bump(); go() } catch (e) { toast((e as Error).message, 'error') }
  }
  return (
    <>
      <p>What to order so you do not run short. For each item we hold the larger of its reorder point, or: stock set aside by open work orders, plus the open quotes from the last 60 days that usually turn into jobs, plus recent usage over the supplier's lead time and the days of cover below. Then subtract what is on hand or already on order.</p>
      <div className="row"><label>Days of cover beyond lead time <input size={4} value={cover} onChange={e => setCover(e.target.value)} /></label>
        <button className="primary" disabled={d.length === 0} onClick={() => void make(null)}>Make draft orders for everything</button></div>
      {bySupplier.length === 0 && <p className="muted">Nothing needs ordering right now.</p>}
      {bySupplier.map(s => <div key={s.id}>
        <h4>{s.name} <button onClick={() => void make(s.id)}>Make draft order</button></h4>
        <table className="grid-table"><thead><tr><th>SKU</th><th>Description</th><th className="num">On hand</th><th className="num">Work orders</th><th className="num">Quotes</th><th className="num">Per day</th><th className="num">On order</th><th className="num">Order</th><th className="num">Supplier says</th><th className="num">Cost</th></tr></thead>
          <tbody>{s.rows.map(r => <tr key={r.id}><td>{r.sku}</td><td>{r.description}</td><td className="num">{n(r.onHand)}</td><td className="num">{n(r.committed)}</td><td className="num">{n(r.quoted)}</td><td className="num">{n(r.perDay)}</td><td className="num">{n(r.onOrder)}</td><td className="num"><strong>{n(r.suggest)} {r.unit}</strong></td><td className="num"><SupplierSays s={r.supplierStock} /></td><td className="num">{money(r.cost)}</td></tr>)}</tbody></table></div>)}
    </>)
}

function OrdersTab({ tick, bump }: { tick: number; bump: () => void }) {
  const { toast } = useShell()
  const [status, setStatus] = useState('open'); const [rows, setRows] = useState<api.PoRow[]>([]); const [sel, setSel] = useState<number | null>(null); const [making, setMaking] = useState(false)
  useEffect(() => { api.getPos(status === 'open' ? undefined : 'all').then(setRows).catch(e => toast((e as Error).message, 'error')) }, [status, tick, toast])
  const label: Record<string, string> = { draft: 'Draft', sent: 'Sent', partial: 'Part received', received: 'Received', cancelled: 'Cancelled' }
  return (
    <>
      <div className="row"><button className={status === 'open' ? 'on' : ''} onClick={() => setStatus('open')}>Open</button><button className={status === 'all' ? 'on' : ''} onClick={() => setStatus('all')}>All</button>
        <button className="primary" onClick={() => setMaking(true)}>New order</button></div>
      <table className="grid-table"><thead><tr><th>Order</th><th>Supplier</th><th>Status</th><th>Made</th><th>Expected</th><th className="num">Lines</th><th className="num">Total</th></tr></thead>
        <tbody>{rows.map(r => <tr key={r.id} className={sel === r.id ? 'sel' : ''} onClick={() => setSel(r.id)}><td>{poNo(r.id)}</td><td>{r.supplier}</td><td>{label[r.status]}</td><td>{r.createdAt.slice(0, 10)}</td><td>{r.expectedDate?.slice(0, 10)}</td><td className="num">{r.lines}</td><td className="num">{money(r.total)}</td></tr>)}
          {rows.length === 0 && <tr><td colSpan={7} className="muted">No orders. Use “Needs & ordering” to draft them from your shortfalls.</td></tr>}</tbody></table>
      {sel !== null && <Order key={sel} id={sel} changed={() => { bump() }} gone={() => { setSel(null); bump() }} />}
      {making && <NewOrder onClose={() => setMaking(false)} done={id => { setMaking(false); setSel(id); bump() }} />}
    </>)
}

function Order({ id, changed, gone }: { id: number; changed: () => void; gone: () => void }) {
  const { me, toast } = useShell()
  const [d, setD] = useState<api.PoDetail | null>(null); const [qty, setQty] = useState<Record<number, string>>({}); const [recv, setRecv] = useState<Record<number, string>>({})
  const [note, setNote] = useState(''); const [exp, setExp] = useState('')
  const load = useCallback(() => api.getPo(id).then(x => { setD(x); setQty(Object.fromEntries(x.lines.map(l => [l.id, String(l.qty)]))); setNote(x.order.note ?? ''); setExp(x.order.expectedDate?.slice(0, 10) ?? ''); setRecv({}) }).catch(e => toast((e as Error).message, 'error')), [id, toast])
  useEffect(() => { void load() }, [load])
  if (!d) return null
  const o = d.order, draft = o.status === 'draft', open = o.status === 'draft' || o.status === 'sent' || o.status === 'partial'
  const total = d.lines.reduce((s, l) => s + l.qty * l.unitCost, 0)
  const run = async (fn: () => Promise<unknown>, ok: string, after?: () => void) => { try { await fn(); toast(ok); changed(); after ? after() : await load() } catch (e) { toast((e as Error).message, 'error') } }
  const save = () => api.editPo(id, { note, expectedDate: exp || null, lines: d.lines.filter(l => Number(qty[l.id]) > 0).map(l => ({ itemId: l.itemId, qty: Number(qty[l.id]) })) })
  const text = () => [`Purchase order ${poNo(o.id)}${o.company ? ` from ${o.company}` : ''}`, o.expectedDate ? `Needed by: ${o.expectedDate.slice(0, 10)}` : '', '',
    ...d.lines.map(l => `${n(l.qty)} ${l.unit}   ${l.sku}${l.description ? '   ' + l.description : ''}`), '', o.note ?? '', o.company ? `Thank you,\n${o.company}` : ''].filter((x, i, a) => x !== '' || a[i - 1] !== '').join('\n')
  const email = () => { window.location.href = `mailto:${o.supplierEmail ?? ''}?subject=${encodeURIComponent(`Purchase order ${poNo(o.id)}${o.company ? ' - ' + o.company : ''}`)}&body=${encodeURIComponent(text())}` }
  const print = () => {
    const w = window.open('', '_blank'); if (!w) { toast('Allow pop-ups to print.', 'error'); return }
    w.document.write(`<!doctype html><title>${poNo(o.id)}</title><style>body{font:14px sans-serif;margin:2rem}table{border-collapse:collapse;width:100%;margin-top:1rem}td,th{border-bottom:1px solid #999;padding:.3rem .5rem;text-align:left}.r{text-align:right}</style>
      <h2>Purchase order ${poNo(o.id)}</h2><p><strong>${esc(o.company ?? '')}</strong>${o.companyPhone ? ' · ' + esc(o.companyPhone) : ''}<br>To: ${esc(o.supplier)}${o.supplierEmail ? ' · ' + esc(o.supplierEmail) : ''}<br>Date: ${o.createdAt.slice(0, 10)}${o.expectedDate ? ' · Needed by: ' + o.expectedDate.slice(0, 10) : ''}</p>
      <table><tr><th class="r">Qty</th><th>Unit</th><th>SKU</th><th>Description</th><th class="r">Unit cost</th><th class="r">Total</th></tr>${d.lines.map(l => `<tr><td class="r">${n(l.qty)}</td><td>${esc(l.unit)}</td><td>${esc(l.sku)}</td><td>${esc(l.description ?? '')}</td><td class="r">${money(l.unitCost)}</td><td class="r">${money(l.qty * l.unitCost)}</td></tr>`).join('')}
      <tr><td colspan="5" class="r"><strong>Total</strong></td><td class="r"><strong>${money(total)}</strong></td></tr></table>${o.note ? `<p>${esc(o.note)}</p>` : ''}`)
    w.document.close(); w.focus(); w.print()
  }
  const csv = () => {
    const q = (s: string) => `"${s.replace(/"/g, '""')}"`
    const body = ['SKU,Description,Qty,Unit,UnitCost', ...d.lines.map(l => [q(l.sku), q(l.description ?? ''), l.qty, q(l.unit), l.unitCost].join(','))].join('\r\n')
    const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob([body], { type: 'text/csv' })); a.download = `${poNo(o.id)}.csv`; a.click(); setTimeout(() => URL.revokeObjectURL(a.href), 5000)
  }
  return (
    <fieldset><legend>{poNo(o.id)} — {o.supplier} ({o.status})</legend>
      {!o.supplierEmail && <p className="muted small">No email address for this supplier yet. Add one on the Suppliers tab, or print the order.</p>}
      <table className="grid-table"><thead><tr><th>SKU</th><th>Description</th><th className="num">Qty</th><th>Unit</th><th className="num">Cost</th><th className="num">Total</th>{o.status !== 'draft' && <><th className="num">Received</th>{open && <th>Receive now</th>}</>}</tr></thead>
        <tbody>{d.lines.map(l => <tr key={l.id}><td>{l.sku}</td><td>{l.description}</td>
          <td className="num">{draft ? <input size={6} value={qty[l.id] ?? ''} onChange={e => setQty({ ...qty, [l.id]: e.target.value })} title="Set to 0 to remove" /> : n(l.qty)}</td><td>{l.unit}</td><td className="num">{money(l.unitCost)}</td><td className="num">{money(l.qty * l.unitCost)}</td>
          {o.status !== 'draft' && <><td className="num">{n(l.received)}</td>{open && <td><input size={6} placeholder={n(Math.max(0, l.qty - l.received))} value={recv[l.id] ?? ''} onChange={e => setRecv({ ...recv, [l.id]: e.target.value })} /></td>}</>}</tr>)}
          <tr className="total"><td colSpan={5} className="num">Total</td><td className="num">{money(total)}</td></tr></tbody></table>
      {draft && <div className="row"><label>Needed by <input type="date" value={exp} onChange={e => setExp(e.target.value)} /></label><label>Note to supplier <input size={40} value={note} onChange={e => setNote(e.target.value)} /></label><button onClick={() => void run(save, 'Saved.')}>Save changes</button></div>}
      {o.supplierNotes && <p className="muted small">Supplier notes: {o.supplierNotes}</p>}
      <div className="actions">
        <button onClick={email} disabled={!o.supplierEmail} title={o.supplierEmail ? 'Opens your email program with the order filled in' : 'Add an email address on the Suppliers tab'}>Email order</button>
        <button onClick={print}>Print</button><button onClick={csv}>Download CSV</button>
        {draft && <button className="primary" onClick={() => void run(async () => { await save(); await api.poSent(id) }, 'Marked as sent. Use Email order first if you have not sent it yet.')}>Mark as sent</button>}
        {open && o.status !== 'draft' && <button className="primary" onClick={() => void run(() => api.poReceive(id, d.lines.map(l => ({ lineId: l.id, qty: recv[l.id] === undefined || recv[l.id] === '' ? 0 : Number(recv[l.id]) })).filter(l => l.qty > 0)), 'Stock updated.')}>Receive what I typed</button>}
        {open && o.status !== 'draft' && <button onClick={() => void run(() => api.poReceive(id, d.lines.filter(l => l.qty > l.received).map(l => ({ lineId: l.id, qty: l.qty - l.received }))), 'Everything received.')}>Receive all</button>}
        {open && <button onClick={() => { if (confirm(o.status === 'partial' ? 'Close this order? What is still missing will no longer count as on order.' : draft ? 'Cancel this order?' : 'Cancel this order? It will no longer count as on order.')) void run(() => api.poCancel(id), 'Order closed.') }}>{o.status === 'partial' ? 'Close short' : 'Cancel order'}</button>}
        {draft && <button onClick={() => { if (confirm('Delete this draft?')) void run(() => api.deletePo(id), 'Deleted.', gone) }}>Delete draft</button>}
        <span className="muted small">{o.sentAt ? `Sent ${instant(o.sentAt, me.timeZone)}` : ''}</span>
      </div>
    </fieldset>)
}

function NewOrder({ onClose, done }: { onClose: () => void; done: (id: number) => void }) {
  const { toast } = useShell()
  const [sup, setSup] = useState<api.InvSupplier[]>([]); const [items, setItems] = useState<api.InvItem[]>([]); const [sid, setSid] = useState(0); const [qty, setQty] = useState<Record<number, string>>({})
  useEffect(() => { void api.getInvSuppliers().then(setSup); void api.getInvItems().then(setItems) }, [])
  const mine = items.filter(i => i.supplierId === sid && i.active)
  const lines = mine.filter(i => Number(qty[i.id]) > 0).map(i => ({ itemId: i.id, qty: Number(qty[i.id]) }))
  const go = async () => { try { done((await api.createPo({ supplierId: sid, lines })).id) } catch (e) { toast((e as Error).message, 'error') } }
  return (
    <Modal title="New purchase order" onClose={onClose} width={720}>
      <div className="lookup-body">
        <label>Supplier <select value={sid} onChange={e => { setSid(Number(e.target.value)); setQty({}) }}><option value={0}>Choose…</option>{[...new Set(items.map(i => i.supplierId))].map(id => <option key={id} value={id}>{sup.find(s => s.id === id)?.name ?? items.find(i => i.supplierId === id)!.supplier}</option>)}</select></label>
        {sid > 0 && <table className="grid-table"><thead><tr><th>SKU</th><th>Description</th><th className="num">On hand</th><th className="num">Order</th></tr></thead>
          <tbody>{mine.map(i => <tr key={i.id}><td>{i.sku}</td><td>{i.description}</td><td className="num">{n(i.onHand)}</td><td className="num"><input size={6} value={qty[i.id] ?? ''} onChange={e => setQty({ ...qty, [i.id]: e.target.value })} /> {i.unit}</td></tr>)}</tbody></table>}
        <div className="actions"><button className="primary" disabled={!lines.length} onClick={() => void go()}>Make draft order</button><button onClick={onClose}>Cancel</button></div>
      </div>
    </Modal>)
}

/** Small CSV reader (quotes, commas and line breaks inside quotes; tab or semicolon files are detected from the first line). */
function parseCsv(text: string): string[][] {
  text = text.replace(/^﻿/, '')
  const first = text.split(/\r?\n/, 1)[0] ?? ''
  const sep = [',', '\t', ';'].map(c => [c, first.split(c).length] as const).sort((a, b) => b[1] - a[1])[0]![0]
  const rows: string[][] = []; let row: string[] = []; let f = ''; let q = false
  for (let i = 0; i < text.length; i++) {
    const ch = text[i]!
    if (q) { if (ch === '"') { if (text[i + 1] === '"') { f += '"'; i++ } else q = false } else f += ch }
    else if (ch === '"') q = true
    else if (ch === sep) { row.push(f); f = '' }
    else if (ch === '\n' || ch === '\r') { if (ch === '\r' && text[i + 1] === '\n') i++; row.push(f); f = ''; if (row.some(x => x.trim())) rows.push(row); row = [] }
    else f += ch
  }
  row.push(f); if (row.some(x => x.trim())) rows.push(row)
  return rows
}
const FIELDS: { key: keyof api.InvImportRow; label: string; guess: RegExp }[] = [
  { key: 'sku', label: 'SKU / item number', guess: /^(sku|item\s*(no|num|number|#|code)?|part\s*(no|num|number|#)?|product\s*(code|no)?|code)$/i },
  { key: 'description', label: 'Description', guess: /desc|name|title/i },
  { key: 'cost', label: 'Cost', guess: /cost|price|each|amount/i },
  { key: 'unit', label: 'Unit (sheet, box…)', guess: /^(unit|uom|u\/m|per)$/i },
  { key: 'onHand', label: 'On hand now', guess: /on\s*hand|qty|quantity|stock|count/i },
  { key: 'reorderPoint', label: 'Reorder at', guess: /reorder|min(imum)?/i },
  { key: 'orderMultiple', label: 'Order multiple', guess: /multiple|pack|bundle|case/i },
]
const num = (v: string | undefined) => { const t = (v ?? '').replace(/[$,\s]/g, ''); if (t === '') return null; const x = Number(t); return isNaN(x) ? NaN : x }

function ImportItems({ supplier, onClose, done }: { supplier: api.InvSupplier; onClose: () => void; done: () => void }) {
  const { toast } = useShell()
  const [table, setTable] = useState<string[][]>([]); const [paste, setPaste] = useState(''); const [map, setMap] = useState<Record<string, number>>({}); const [track, setTrack] = useState(true)
  const [busy, setBusy] = useState(false); const [result, setResult] = useState<api.InvImportResult | null>(null)
  const load = (text: string) => {
    const t = parseCsv(text); setTable(t)
    const head = t[0] ?? []; const m: Record<string, number> = {}
    for (const f of FIELDS) { const i = head.findIndex((h, ix) => f.guess.test(h.trim()) && !Object.values(m).includes(ix)); if (i >= 0) m[f.key] = i }
    setMap(m)
  }
  const body = table.slice(1)
  const rows: api.InvImportRow[] = body.map(r => {
    const g = (k: string) => map[k] === undefined ? undefined : r[map[k]!]?.trim()
    return { sku: g('sku') ?? '', description: g('description') || null, cost: num(g('cost')), unit: g('unit') || null, onHand: num(g('onHand')), reorderPoint: num(g('reorderPoint')), orderMultiple: num(g('orderMultiple')) }
  })
  const bad = rows.filter(r => !r.sku || [r.cost, r.onHand, r.reorderPoint, r.orderMultiple].some(x => x !== null && x !== undefined && isNaN(x as number)))
  const good = rows.filter(r => !bad.includes(r))
  const go = async () => {
    setBusy(true)
    try { const r = await api.saveInvSupplierItems(supplier.id, good, track); setResult(r); toast('Imported.'); done() } catch (e) { toast((e as Error).message, 'error') } finally { setBusy(false) }
  }
  return (
    <Modal title={`Import items from a file — ${supplier.name}`} onClose={onClose} width={860}>
      <div className="lookup-body">
        {result ? <>
          <p><strong>{result.added}</strong> new items, <strong>{result.changed}</strong> changed, {result.unchanged} unchanged{track ? <>, <strong>{result.tracked}</strong> now counted in inventory</> : null}.{result.skipped ? ` ${result.skipped} rows skipped.` : ''}</p>
          {result.priceChanges > 0 && <p className="muted">{result.priceChanges} selling price changes were proposed from the new costs. Review them under Supplier Pricing → Pending changes; nothing changes until you approve.</p>}
          <div className="actions"><button className="primary" onClick={onClose}>Done</button></div></> : <>
          <p>A spreadsheet saved as CSV with a header row. Only the SKU column is required; items you already have are updated by SKU, and an empty cost leaves the old cost alone.</p>
          <div className="row"><input type="file" accept=".csv,.tsv,.txt,text/csv" onChange={async e => { const f = e.target.files?.[0]; if (f) load(await f.text()) }} />
            <span className="muted">or paste rows below</span></div>
          <textarea rows={4} style={{ width: '100%' }} placeholder={'SKU,Description,Cost,Unit\nCL-1/4,Clear 1/4 sheet 60x96,100.00,sheet'} value={paste} onChange={e => { setPaste(e.target.value); load(e.target.value) }} />
          {table.length > 1 && <>
            <h4>Which column is which?</h4>
            <div className="row">{FIELDS.map(f => <label key={f.key}>{f.label} <select value={map[f.key] ?? ''} onChange={e => setMap({ ...map, [f.key]: e.target.value === '' ? undefined! : Number(e.target.value) })}><option value="">(none)</option>{table[0]!.map((h, i) => <option key={i} value={i}>{h || `Column ${i + 1}`}</option>)}</select></label>)}</div>
            <table className="grid-table"><thead><tr><th>SKU</th><th>Description</th><th className="num">Cost</th><th>Unit</th><th className="num">On hand</th><th className="num">Reorder</th><th className="num">Multiple</th></tr></thead>
              <tbody>{rows.slice(0, 6).map((r, i) => <tr key={i} className={bad.includes(r) ? 'neg' : ''}><td>{r.sku}</td><td>{r.description}</td><td className="num">{r.cost ?? ''}</td><td>{r.unit}</td><td className="num">{r.onHand ?? ''}</td><td className="num">{r.reorderPoint ?? ''}</td><td className="num">{r.orderMultiple ?? ''}</td></tr>)}</tbody></table>
            <p className="muted small">{good.length} usable rows{bad.length ? `, ${bad.length} will be skipped (no SKU or a bad number; shown in red above if among the first rows)` : ''}.</p>
            <label className="check"><input type="checkbox" checked={track} onChange={e => setTrack(e.target.checked)} /> Also start counting these in inventory (uses the Unit, On hand, Reorder and Multiple columns when present)</label></>}
          <div className="actions"><button className="primary" disabled={busy || good.length === 0 || map.sku === undefined} onClick={() => void go()}>{busy ? 'Importing…' : `Import ${good.length} items`}</button><button onClick={onClose}>Cancel</button></div></>}
      </div>
    </Modal>)
}

function SupplierForm({ s, onClose, done }: { s: api.InvSupplier | null; onClose: () => void; done: (id?: number) => void }) {
  const { toast } = useShell()
  const [f, setF] = useState({ name: s?.name ?? '', email: s?.email ?? '', phone: s?.phone ?? '', orderNotes: s?.orderNotes ?? '' })
  const save = async () => {
    try {
      if (s) { await api.saveInvSupplier(s.id, f); toast('Saved.'); done() } else { const r = await api.addInvSupplier(f); toast('Supplier added.'); done(r.id) }
    } catch (e) { toast((e as Error).message, 'error') }
  }
  return (
    <Modal title={s ? `Supplier: ${s.name}` : 'Add a supplier'} onClose={onClose} width={560}>
      <div className="lookup-body">
        <div className="row"><label>Name <input autoFocus size={34} value={f.name} onChange={e => setF({ ...f, name: e.target.value })} /></label></div>
        <div className="row"><label>Order email <input size={30} value={f.email} onChange={e => setF({ ...f, email: e.target.value })} /></label><label>Phone <input size={14} value={f.phone} onChange={e => setF({ ...f, phone: e.target.value })} /></label></div>
        <div className="row"><label>Notes <input size={46} placeholder="account number, delivery instructions…" value={f.orderNotes} onChange={e => setF({ ...f, orderNotes: e.target.value })} /></label></div>
        <div className="actions"><button className="primary" disabled={!f.name.trim()} onClick={() => void save()}>Save</button><button onClick={onClose}>Cancel</button>
          {s && <button onClick={async () => { try { await api.saveInvSupplier(s.id, { ...f, active: !s.active }); toast(s.active ? 'Supplier hidden.' : 'Supplier active again.'); done() } catch (e) { toast((e as Error).message, 'error') } }}>{s.active ? 'Hide supplier' : 'Show supplier again'}</button>}</div>
        <p className="muted small">Feeds and automatic price updates for a supplier are set up under Supplier Pricing.</p>
      </div>
    </Modal>)
}

function SuppliersTab({ bump }: { bump: () => void }) {
  const { toast } = useShell()
  const [rows, setRows] = useState<api.InvSupplier[]>([]); const [sel, setSel] = useState<number | null>(null)
  const [form, setForm] = useState<api.InvSupplier | 'new' | null>(null); const [importing, setImporting] = useState(false)
  const [items, setItems] = useState<api.InvCatalogRow[]>([]); const [q, setQ] = useState(''); const [tick, setTick] = useState(0)
  const [it, setIt] = useState({ sku: '', description: '', cost: '', unit: '', track: true })
  useEffect(() => { api.getInvSuppliers().then(r => { setRows(r); setSel(s => s ?? r.find(x => x.active)?.id ?? null) }).catch(e => toast((e as Error).message, 'error')) }, [tick, toast])
  const cur = rows.find(r => r.id === sel) ?? null
  useEffect(() => { if (!sel) { setItems([]); return } const t = setTimeout(() => api.getInvCatalog(q, sel).then(setItems).catch(() => undefined), 200); return () => clearTimeout(t) }, [sel, q, tick])
  const refresh = () => { setTick(t => t + 1); bump() }
  const addItem = async () => {
    const cost = it.cost.trim() === '' ? null : Number(it.cost.replace(/[$,]/g, ''))
    if (cost !== null && (isNaN(cost) || cost < 0)) { toast('The cost must be a number.', 'error'); return }
    try { await api.saveInvSupplierItems(cur!.id, [{ sku: it.sku, description: it.description, cost, unit: it.unit }], it.track); toast('Saved.'); setIt({ ...it, sku: '', description: '', cost: '' }); refresh() } catch (e) { toast((e as Error).message, 'error') }
  }
  return (
    <>
      <p>Add the companies you buy from, then load their items (SKU, description, cost) by typing them in or importing a spreadsheet. Items you load can be counted in inventory and ordered from here.</p>
      <div className="row"><button className="primary" onClick={() => setForm('new')}>Add a supplier</button></div>
      <table className="grid-table"><thead><tr><th>Supplier</th><th>Order email</th><th>Phone</th><th className="num">Items</th><th>Stock source</th><th /></tr></thead>
        <tbody>{rows.map(s => <tr key={s.id} className={(sel === s.id ? 'sel ' : '') + (!s.active ? 'muted' : '')} onClick={() => setSel(s.id)}><td>{s.name}{!s.active && ' (hidden)'}</td><td>{s.email}</td><td>{s.phone}</td><td className="num">{s.items}</td><td>{s.availMode === "none" ? <span className="muted">none</span> : (s.availMode === "api" ? "API" : "Web page") + (s.availStatus === "error" ? " (failing)" : "")}</td><td><button onClick={e => { e.stopPropagation(); setForm(s) }}>Edit</button></td></tr>)}
          {rows.length === 0 && <tr><td colSpan={6} className="muted">No suppliers yet. Choose “Add a supplier”.</td></tr>}</tbody></table>
      {cur && <fieldset><legend>{cur.name}: items</legend>
        <div className="row"><label>SKU <input size={14} value={it.sku} onChange={e => setIt({ ...it, sku: e.target.value })} /></label><label>Description <input size={26} value={it.description} onChange={e => setIt({ ...it, description: e.target.value })} /></label>
          <label>Cost <input size={8} value={it.cost} onChange={e => setIt({ ...it, cost: e.target.value })} /></label><label>Unit <input size={7} placeholder="sheet" value={it.unit} onChange={e => setIt({ ...it, unit: e.target.value })} /></label>
          <label className="check"><input type="checkbox" checked={it.track} onChange={e => setIt({ ...it, track: e.target.checked })} /> count in inventory</label>
          <button disabled={!it.sku.trim()} onClick={() => void addItem()}>Add or update item</button><button className="primary" onClick={() => setImporting(true)}>Import items from a file…</button></div>
        <div className="row"><input className="search" placeholder="Find an item…" value={q} onChange={e => setQ(e.target.value)} /></div>
        <div className="maint-list" style={{ maxHeight: '16rem' }}><table className="grid-table"><thead><tr><th>SKU</th><th>Description</th><th className="num">Cost</th><th>Counted in inventory</th><th>Linked parts</th></tr></thead>
          <tbody>{items.map(r => <tr key={r.supplierItemId}><td>{r.sku}</td><td>{r.description}</td><td className="num">{money(r.unitCost)}</td><td>{r.tracked ? `yes (${r.unit ?? 'each'})` : 'no'}</td><td>{r.links ? `${r.links} linked` : 'none'}</td></tr>)}
            {items.length === 0 && <tr><td colSpan={5} className="muted">No items yet. Add one above or import a file.</td></tr>}</tbody></table></div>
        <p className="muted small">To have glass and parts used up automatically, link each part and thickness to its item under Supplier Pricing → Cost links.</p>
      </fieldset>}
      {cur && <AvailSetup key={cur.id} supplier={cur} done={refresh} />}
      {form && <SupplierForm s={form === 'new' ? null : form} onClose={() => setForm(null)} done={id => { setForm(null); if (id) setSel(id); refresh() }} />}
      {importing && cur && <ImportItems supplier={cur} onClose={() => setImporting(false)} done={refresh} />}
    </>)
}

const FIELDS_BY: Record<string, { key: string; label: string; hint?: string; wide?: boolean }[]> = {
  'json-list': [{ key: 'sku', label: 'SKU field', hint: 'sku' }, { key: 'qty', label: 'Quantity field', hint: 'qty' }, { key: 'status', label: 'Stock text field (optional)' }, { key: 'rows', label: 'List is at (optional)', hint: 'items or data.products' }],
  'csv-list': [{ key: 'sku', label: 'SKU column', hint: 'sku' }, { key: 'qty', label: 'Quantity column', hint: 'qty' }, { key: 'status', label: 'Stock text column (optional)' }],
  'json-one': [{ key: 'qty', label: 'Quantity at', hint: 'data.available' }, { key: 'status', label: 'or stock text at', hint: 'data.status' }],
  'html-one': [{ key: 'qtyPattern', label: 'Quantity pattern', hint: 'Available:\\s*([\\d,]+)', wide: true }, { key: 'inPattern', label: 'In-stock pattern (optional)', hint: 'In Stock', wide: true }, { key: 'outPattern', label: 'Out-of-stock pattern (optional)', hint: 'Out of Stock|Sold Out', wide: true }],
  'html-list': [{ key: 'rowPattern', label: 'Row pattern', hint: '<td>(?<sku>[^<]+)</td>\\s*<td>(?<qty>[\\d,]+)</td>', wide: true }, { key: 'inPattern', label: 'In-stock pattern (optional)', wide: true }, { key: 'outPattern', label: 'Out-of-stock pattern (optional)', wide: true }],
}

/** Where a supplier's stock comes from: its API (JSON or CSV) or a web page. Only records what the supplier says; never changes our counts or prices. */
function AvailSetup({ supplier, done }: { supplier: api.InvSupplier; done: () => void }) {
  const { toast, me } = useShell()
  const [cfg, setCfg] = useState<api.AvailConfig | null>(null)
  const [mode, setMode] = useState<string>('none'); const [url, setUrl] = useState(''); const [auth, setAuth] = useState(''); const [clearAuth, setClearAuth] = useState(false)
  const [format, setFormat] = useState('json'); const [map, setMap] = useState<Record<string, string>>({}); const [hours, setHours] = useState(''); const [sku, setSku] = useState('')
  const [test, setTest] = useState<api.AvailTest | null>(null); const [msg, setMsg] = useState(''); const [busy, setBusy] = useState(false)
  const load = useCallback(() => api.getAvail(supplier.id).then(c => { setCfg(c); setMode(c.mode); setUrl(c.url ?? ''); setFormat(c.format); setMap(c.map as Record<string, string>); setHours(c.refreshHours ? String(c.refreshHours) : ''); setAuth(''); setClearAuth(false) }).catch(e => toast((e as Error).message, 'error')), [supplier.id, toast])
  useEffect(() => { void load() }, [load])
  if (!cfg) return null
  const perSku = url.toLowerCase().includes('{sku}')
  const fmt = mode === 'scrape' ? 'html' : format === 'html' ? 'json' : format
  const fields = FIELDS_BY[`${fmt}-${perSku ? 'one' : 'list'}`] ?? []
  const input = (): api.AvailInput => ({ mode, url, authHeader: auth || undefined, clearAuth, format: fmt, map: Object.fromEntries(Object.entries(map).filter(([k, v]) => v.trim() && (fields.some(f => f.key === k)))), refreshHours: hours ? Number(hours) : null, sku: sku || undefined })
  const run = async (fn: () => Promise<void>) => { setBusy(true); try { await fn() } catch (e) { toast((e as Error).message, 'error') } finally { setBusy(false) } }
  const save = () => run(async () => { await api.saveAvail(supplier.id, input()); toast('Saved.'); await load(); done() })
  const tryIt = () => run(async () => { setTest(await api.testAvail(supplier.id, input())) })
  const check = () => run(async () => { const r = await api.pollSupplierStock(supplier.id); setMsg(r.message); toast('Checked.'); await load(); done() })
  const st = cfg.status
  return (
    <fieldset><legend>{supplier.name}: stock from the supplier</legend>
      <p className="muted small">Ask the supplier what it has in stock, from its API or by reading a web page. It shows next to your own stock and in the needs list; it never changes your counts or prices. Only https addresses on the public internet are allowed. If you read web pages, make sure the supplier's terms allow it.</p>
      <div className="row"><label>Source <select value={mode} onChange={e => { setMode(e.target.value); setTest(null) }}><option value="none">Not used</option><option value="api">Its API (JSON or CSV)</option><option value="scrape">Read a web page</option></select></label>
        {mode === 'api' && <label>Answer is <select value={fmt} onChange={e => setFormat(e.target.value)}><option value="json">JSON</option><option value="csv">CSV</option></select></label>}
        {mode !== 'none' && <label>Check every <input size={4} value={hours} placeholder="manual" onChange={e => setHours(e.target.value)} /> hours</label>}</div>
      {mode !== 'none' && <>
        <div className="row"><label style={{ flex: 1 }}>Address <input style={{ width: '34rem' }} value={url} placeholder="https://supplier.example/api/stock?sku={sku}" onChange={e => setUrl(e.target.value)} /></label></div>
        <p className="muted small">{perSku ? 'One request per tracked item: {sku} is replaced with the item’s SKU.' : 'One request returns the whole list; rows are matched to your items by SKU. Add {sku} to the address to ask item by item instead.'}</p>
        <div className="row"><label>Authorization header <input type="password" size={28} autoComplete="off" placeholder={cfg.hasAuthHeader ? 'set (leave blank to keep)' : 'Bearer …'} value={auth} onChange={e => setAuth(e.target.value)} /></label>
          {cfg.hasAuthHeader && <label className="check"><input type="checkbox" checked={clearAuth} onChange={e => setClearAuth(e.target.checked)} /> remove it</label>}</div>
        <div className="row" style={{ flexWrap: 'wrap' }}>{fields.map(f => <label key={f.key} style={f.wide ? { flexBasis: '100%' } : undefined}>{f.label} <input style={{ width: f.wide ? '34rem' : undefined }} size={f.wide ? 40 : 16} placeholder={f.hint} value={map[f.key] ?? ''} onChange={e => setMap({ ...map, [f.key]: e.target.value })} /></label>)}</div>
        {fmt === 'html' && <p className="muted small">Patterns are regular expressions, matched without regard to case. In a quantity pattern the first group is the number; in a row pattern name the groups <code>(?&lt;sku&gt;…)</code> and <code>(?&lt;qty&gt;…)</code>.</p>}
      </>}
      <div className="row"><button className="primary" disabled={busy} onClick={() => void save()}>Save</button>
        {mode !== 'none' && <><label>Try with SKU <input size={12} value={sku} placeholder={perSku ? 'first item' : ''} onChange={e => setSku(e.target.value)} disabled={!perSku} /></label>
          <button disabled={busy || !url} onClick={() => void tryIt()}>Try it (saves nothing)</button></>}
        {cfg.mode !== 'none' && <button disabled={busy} onClick={() => void check()}>Check tracked items now</button>}</div>
      {test && <div className="muted small"><div>Read {test.length.toLocaleString()} characters{test.total != null ? `, ${test.total} rows` : ''}. Result:</div>
        <table className="grid-table"><thead><tr><th>SKU</th><th className="num">Quantity</th><th>Status</th><th>Note</th></tr></thead><tbody>{test.found.map((f, i) => <tr key={i}><td>{f.sku}</td><td className="num">{f.qty ?? ''}</td><td>{f.status}</td><td>{f.note}</td></tr>)}
          {test.found.length === 0 && <tr><td colSpan={4}>Nothing matched. Start of the answer:</td></tr>}</tbody></table>
        <pre style={{ whiteSpace: 'pre-wrap', maxHeight: '8rem', overflow: 'auto' }}>{test.snippet}</pre></div>}
      {(msg || st.lastAt) && <p className="small"><strong>{st.lastStatus === 'error' ? 'Last check failed' : 'Last check'}</strong> {st.lastAt ? instant(st.lastAt, me.timeZone) : ''}: {msg || st.lastMessage}</p>}
    </fieldset>)
}
