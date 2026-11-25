import { useCallback, useEffect, useState } from 'react'
import * as api from '../api'
import { instant, money } from '../format'
import { Modal, Tabs } from '../shell/ui'
import { useShell } from '../shell/WindowContext'

const TABS = ['Stock', 'Needs & ordering', 'Purchase orders', 'Suppliers']
const n = (x: number) => (Math.round(x * 1000) / 1000).toLocaleString('en-US', { maximumFractionDigits: 3 })
const esc = (s: string) => s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]!))
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
      {tab === TABS[3] && <SuppliersTab />}
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
        <table className="grid-table"><thead><tr><th>Supplier</th><th>SKU</th><th>Description</th><th>Where</th><th className="num">On hand</th><th className="num">Committed</th><th className="num">Available</th><th className="num">On order</th><th>Unit</th></tr></thead>
          <tbody>{shown.map(r => <tr key={r.id} className={(sel?.id === r.id ? 'sel ' : '') + (!r.active ? 'muted' : '')} onClick={() => setSel(r)}>
            <td>{r.supplier}</td><td>{r.sku}</td><td>{r.description}</td><td>{r.location}</td><td className="num">{n(r.onHand)}</td><td className="num">{n(r.committed)}</td>
            <td className={'num' + (r.available < 0 ? ' neg' : '')}>{n(r.available)}</td><td className="num">{n(r.onOrder)}</td><td>{r.unit}</td></tr>)}
            {shown.length === 0 && <tr><td colSpan={9} className="muted">{rows.length ? 'No match.' : 'Nothing is tracked yet. Choose “Track a supplier item”.'}</td></tr>}</tbody></table>
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
        <table className="grid-table"><thead><tr><th>SKU</th><th>Description</th><th className="num">On hand</th><th className="num">Work orders</th><th className="num">Quotes</th><th className="num">Per day</th><th className="num">On order</th><th className="num">Order</th><th className="num">Cost</th></tr></thead>
          <tbody>{s.rows.map(r => <tr key={r.id}><td>{r.sku}</td><td>{r.description}</td><td className="num">{n(r.onHand)}</td><td className="num">{n(r.committed)}</td><td className="num">{n(r.quoted)}</td><td className="num">{n(r.perDay)}</td><td className="num">{n(r.onOrder)}</td><td className="num"><strong>{n(r.suggest)} {r.unit}</strong></td><td className="num">{money(r.cost)}</td></tr>)}</tbody></table></div>)}
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

function SuppliersTab() {
  const { toast } = useShell()
  const [rows, setRows] = useState<api.InvSupplier[]>([])
  useEffect(() => { api.getInvSuppliers().then(setRows).catch(e => toast((e as Error).message, 'error')) }, [toast])
  const set = (id: number, patch: Partial<api.InvSupplier>) => setRows(r => r.map(x => x.id === id ? { ...x, ...patch } : x))
  const save = async (s: api.InvSupplier) => { try { await api.saveInvSupplier(s.id, { email: s.email ?? '', phone: s.phone ?? '', orderNotes: s.orderNotes ?? '' }); toast('Saved.') } catch (e) { toast((e as Error).message, 'error') } }
  return (
    <>
      <p>Where purchase orders go. The email address fills in the “Email order” button; the notes (account number, delivery instructions) show on the order screen.</p>
      <table className="grid-table"><thead><tr><th>Supplier</th><th>Order email</th><th>Phone</th><th>Notes</th><th /></tr></thead>
        <tbody>{rows.map(s => <tr key={s.id}><td>{s.name}</td>
          <td><input size={26} value={s.email ?? ''} onChange={e => set(s.id, { email: e.target.value })} /></td><td><input size={13} value={s.phone ?? ''} onChange={e => set(s.id, { phone: e.target.value })} /></td>
          <td><input size={30} value={s.orderNotes ?? ''} onChange={e => set(s.id, { orderNotes: e.target.value })} /></td><td><button onClick={() => void save(s)}>Save</button></td></tr>)}
          {rows.length === 0 && <tr><td colSpan={5} className="muted">No suppliers yet. Add them under Supplier Pricing.</td></tr>}</tbody></table>
    </>)
}
