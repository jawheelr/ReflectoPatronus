import { useCallback, useEffect, useState } from 'react'
import * as api from '../api'
import { instant, money } from '../format'
import { useShell } from '../shell/WindowContext'
import { Field, Modal, Tabs } from '../shell/ui'

const TABS = ['Suppliers', 'Cost links', 'Markups', 'Pending changes']
const thick = (n: number, d: number) => `${n}/${d}`
const summary = (r: api.IngestResult) =>
  `${r.added} new, ${r.changed} changed costs. ` + (r.priceChanges ? `${r.priceChanges} price changes waiting for review.` : 'No price changes needed.')

export function SupplierPricing() {
  const { me } = useShell()
  const [tab, setTab] = useState(TABS[0])
  const [pending, setPending] = useState(0)
  const refreshPending = useCallback(() => { api.getBatches('pending').then(b => setPending(b.length)).catch(() => {}) }, [])
  useEffect(refreshPending, [refreshPending, tab])
  if (!me.isAdmin) return <div className="form"><p>Administrator access is required.</p></div>
  return (
    <div className="form">
      <p className="muted">Supplier costs flow into selling prices as <strong>cost × factor × (1 + category markup)</strong>. Changes are proposed for your review; nothing goes live until you approve it, and existing documents never change.</p>
      <Tabs tabs={TABS.map(t => t === 'Pending changes' && pending ? `Pending changes (${pending})` : t)} active={tab === 'Pending changes' && pending ? `Pending changes (${pending})` : tab} onChange={t => setTab(t.replace(/ \(\d+\)$/, ''))} />
      {tab === 'Suppliers' && <Suppliers onChanged={refreshPending} />}
      {tab === 'Cost links' && <Links />}
      {tab === 'Markups' && <Markups onChanged={refreshPending} />}
      {tab === 'Pending changes' && <Pending onChanged={refreshPending} />}
    </div>
  )
}

// ---------------------------------------------------------------------------------------------------------------------
function Suppliers({ onChanged }: { onChanged: () => void }) {
  const { toast, me } = useShell()
  const [list, setList] = useState<api.Supplier[] | null>(null)
  const [edit, setEdit] = useState<api.Supplier | 'new' | null>(null)
  const [sel, setSel] = useState<api.Supplier | null>(null)
  const load = useCallback(() => api.getSuppliers().then(l => { setList(l); setSel(s => s && l.find(x => x.id === s.id) || null) }), [])
  useEffect(() => { load() }, [load])
  const done = (r: api.IngestResult) => { toast(summary(r)); load(); onChanged() }
  const fail = (e: unknown) => toast((e as Error).message, 'error')

  return (
    <>
      <p><button onClick={() => setEdit('new')}>Add supplier</button></p>
      <table className="grid-table">
        <thead><tr><th>Supplier</th><th>Source</th><th>Refresh</th><th>Last run</th><th>Items</th><th>Linked</th><th /></tr></thead>
        <tbody>
          {(list ?? []).map(s => (
            <tr key={s.id} className={sel?.id === s.id ? 'sel' : ''} onClick={() => setSel(s)}>
              <td>{s.name}{!s.active && <small> (inactive)</small>}</td><td>{s.sourceType}</td>
              <td>{s.sourceType === 'feed' && s.refreshHours ? `every ${s.refreshHours} h` : '—'}</td>
              <td>{s.lastRunAt ? <>{instant(s.lastRunAt, me.timeZone)} <small title={s.lastMessage ?? ''}>{s.lastStatus === 'error' ? '⚠ ' : ''}{s.lastMessage}</small></> : '—'}</td>
              <td>{s.itemCount}</td><td>{s.linkedCount}</td>
              <td><button onClick={e => { e.stopPropagation(); setEdit(s) }}>Edit</button></td>
            </tr>
          ))}
          {list?.length === 0 && <tr><td colSpan={7} className="muted">No suppliers yet. Add one, then load its costs by feed, file upload or by hand.</td></tr>}
        </tbody>
      </table>
      {sel && <SupplierDetail s={sel} onDone={done} onFail={fail} />}
      {edit && <SupplierForm s={edit === 'new' ? null : edit} onClose={() => setEdit(null)} onSaved={() => { setEdit(null); load() }} onDeleted={() => { setEdit(null); setSel(null); load() }} />}
    </>
  )
}

function SupplierDetail({ s, onDone, onFail }: { s: api.Supplier; onDone: (r: api.IngestResult) => void; onFail: (e: unknown) => void }) {
  const [items, setItems] = useState<api.SupplierItem[]>([])
  const [q, setQ] = useState('')
  const [pct, setPct] = useState('')
  const [row, setRow] = useState({ sku: '', description: '', cost: '' })
  const [busy, setBusy] = useState(false)
  const load = useCallback(() => api.getSupplierItems(s.id, q).then(setItems), [s.id, q])
  useEffect(() => { const t = setTimeout(load, 200); return () => clearTimeout(t) }, [load, s.itemCount])
  const run = async (f: () => Promise<api.IngestResult>) => { setBusy(true); try { onDone(await f()); load() } catch (e) { onFail(e) } finally { setBusy(false) } }
  return (
    <div className="panel-block">
      <h3>{s.name}: costs</h3>
      <div className="row-actions">
        {s.sourceType === 'feed' && <button disabled={busy} onClick={() => run(() => api.runFeed(s.id))}>Fetch feed now</button>}
        <label className="filebtn">Upload price list ({s.feedFormat.toUpperCase()})
          <input type="file" accept={s.feedFormat === 'json' ? '.json' : '.csv,text/csv'} hidden disabled={busy}
            onChange={async e => { const f = e.target.files?.[0]; e.target.value = ''; if (f) run(async () => api.importCosts(s.id, await f.text())) }} />
        </label>
        <span>Adjust all costs by <input type="number" step="0.1" value={pct} onChange={e => setPct(e.target.value)} style={{ width: '5rem' }} /> %
          <button disabled={busy || !pct} onClick={() => confirm(`Change all ${s.itemCount} costs by ${pct}%?`) && run(() => api.adjustCosts(s.id, Number(pct)))}>Apply</button></span>
      </div>
      <p className="muted">Upload columns: <code>{s.columnMap.sku ?? 'sku'}</code>, <code>{s.columnMap.cost ?? 'cost'}</code>, optional <code>{s.columnMap.description ?? 'description'}</code> (change under Edit).</p>
      <div className="row-actions">
        <input placeholder="SKU" value={row.sku} onChange={e => setRow({ ...row, sku: e.target.value })} />
        <input placeholder="Description" value={row.description} onChange={e => setRow({ ...row, description: e.target.value })} />
        <input placeholder="Unit cost" type="number" step="0.0001" min="0" value={row.cost} onChange={e => setRow({ ...row, cost: e.target.value })} style={{ width: '7rem' }} />
        <button disabled={busy || !row.sku || row.cost === ''} onClick={() => run(async () => { const r = await api.postCosts(s.id, [{ sku: row.sku, description: row.description || null, cost: Number(row.cost) }]); setRow({ sku: '', description: '', cost: '' }); return r })}>Add / update cost</button>
      </div>
      <input type="search" placeholder="Filter items…" value={q} onChange={e => setQ(e.target.value)} />
      <table className="grid-table">
        <thead><tr><th>SKU</th><th>Description</th><th style={{ textAlign: 'right' }}>Unit cost</th><th>Updated</th><th>Linked to</th></tr></thead>
        <tbody>
          {items.map(i => <tr key={i.id}><td>{i.sku}</td><td>{i.description}</td><td style={{ textAlign: 'right' }}>{i.unitCost.toFixed(4)}</td><td>{i.costUpdatedAt.slice(0, 10)}</td><td>{i.links || '—'}</td></tr>)}
          {items.length === 0 && <tr><td colSpan={5} className="muted">No items.</td></tr>}
        </tbody>
      </table>
    </div>
  )
}

function SupplierForm({ s, onClose, onSaved, onDeleted }: { s: api.Supplier | null; onClose: () => void; onSaved: () => void; onDeleted: () => void }) {
  const { toast } = useShell()
  const [f, setF] = useState({
    name: s?.name ?? '', active: s?.active ?? true, sourceType: s?.sourceType ?? 'manual', feedUrl: s?.feedUrl ?? '', feedAuthHeader: '',
    feedFormat: s?.feedFormat ?? 'csv', sku: s?.columnMap.sku ?? 'sku', cost: s?.columnMap.cost ?? 'cost', description: s?.columnMap.description ?? 'description',
    refreshHours: s?.refreshHours?.toString() ?? '24',
  })
  const set = <K extends keyof typeof f>(k: K, v: (typeof f)[K]) => setF(p => ({ ...p, [k]: v }))
  async function save() {
    try {
      await api.saveSupplier(s?.id ?? null, {
        name: f.name, active: f.active, sourceType: f.sourceType, feedUrl: f.sourceType === 'feed' ? f.feedUrl : null,
        feedAuthHeader: f.feedAuthHeader || null, feedFormat: f.feedFormat, columnMap: { sku: f.sku, cost: f.cost, description: f.description },
        refreshHours: f.sourceType === 'feed' && f.refreshHours ? Number(f.refreshHours) : null,
      })
      toast('Supplier saved.'); onSaved()
    } catch (e) { toast((e as Error).message, 'error') }
  }
  return (
    <Modal title={s ? `Edit ${s.name}` : 'Add supplier'} onClose={onClose} width={560}>
      <div className="lookup-body">
        <Field label="Name"><input value={f.name} onChange={e => set('name', e.target.value)} autoFocus /></Field>
        <Field label="Active"><input type="checkbox" checked={f.active} onChange={e => set('active', e.target.checked)} /></Field>
        <Field label="How costs arrive">
          <select value={f.sourceType} onChange={e => set('sourceType', e.target.value as api.Supplier['sourceType'])}>
            <option value="manual">Manual entry / % adjustment</option><option value="upload">File upload (CSV or JSON)</option><option value="feed">Automatic feed (scheduled URL)</option>
          </select>
        </Field>
        {f.sourceType !== 'manual' && <Field label="File format"><select value={f.feedFormat} onChange={e => set('feedFormat', e.target.value as 'csv' | 'json')}><option value="csv">CSV (header row)</option><option value="json">JSON (array of objects)</option></select></Field>}
        {f.sourceType === 'feed' && <>
          <Field label="Feed URL (https)" wide><input value={f.feedUrl} onChange={e => set('feedUrl', e.target.value)} placeholder="https://supplier.example.com/prices.csv" /></Field>
          <Field label="Authorization header" wide><input type="password" autoComplete="off" value={f.feedAuthHeader} onChange={e => set('feedAuthHeader', e.target.value)} placeholder={s?.hasAuthHeader ? '(saved: leave blank to keep)' : 'e.g. Bearer abc123 (optional)'} /></Field>
          <Field label="Refresh every (hours)"><input type="number" min={1} value={f.refreshHours} onChange={e => set('refreshHours', e.target.value)} style={{ width: '6rem' }} /></Field>
        </>}
        {f.sourceType !== 'manual' && <>
          <Field label="Column: SKU"><input value={f.sku} onChange={e => set('sku', e.target.value)} /></Field>
          <Field label="Column: cost"><input value={f.cost} onChange={e => set('cost', e.target.value)} /></Field>
          <Field label="Column: description"><input value={f.description} onChange={e => set('description', e.target.value)} /></Field>
        </>}
        <p className="row-actions">
          <button onClick={save} disabled={!f.name.trim()}>Save</button><button onClick={onClose}>Cancel</button>
          {s && <button onClick={async () => { if (confirm(`Delete ${s.name} and its ${s.itemCount} items and cost links?`)) { await api.deleteSupplier(s.id); toast('Supplier deleted.'); onDeleted() } }}>Delete</button>}
        </p>
      </div>
    </Modal>
  )
}

// ---------------------------------------------------------------------------------------------------------------------
function Links() {
  const { toast } = useShell()
  const [links, setLinks] = useState<api.CostLink[]>([])
  const [adding, setAdding] = useState(false)
  const load = useCallback(() => api.getLinks().then(setLinks), [])
  useEffect(() => { load() }, [load])
  return (
    <>
      <p><button onClick={() => setAdding(true)}>Link a glass / part</button> <span className="muted">Each linked thickness is repriced from its supplier item.</span></p>
      <table className="grid-table">
        <thead><tr><th>Part</th><th>Thickness</th><th>Supplier item</th><th style={{ textAlign: 'right' }}>Cost</th><th style={{ textAlign: 'right' }}>Factor</th><th /></tr></thead>
        <tbody>
          {links.map(l => (
            <tr key={l.thicknessId}><td>{l.partCode} <small>{l.partDescription}</small></td><td>{thick(l.thickNum, l.thickDen)}</td><td>{l.sku}</td>
              <td style={{ textAlign: 'right' }}>{l.unitCost.toFixed(4)}</td><td style={{ textAlign: 'right' }}>{l.factor}</td>
              <td><button onClick={async () => { await api.deleteLink(l.thicknessId); toast('Link removed.'); load() }}>Remove</button></td></tr>
          ))}
          {links.length === 0 && <tr><td colSpan={6} className="muted">Nothing linked yet.</td></tr>}
        </tbody>
      </table>
      {adding && <LinkForm onClose={() => setAdding(false)} onSaved={() => { setAdding(false); load() }} />}
    </>
  )
}

function LinkForm({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const { toast } = useShell()
  const [q, setQ] = useState('')
  const [hits, setHits] = useState<api.ThicknessHit[]>([])
  const [th, setTh] = useState<api.ThicknessHit | null>(null)
  const [suppliers, setSuppliers] = useState<api.Supplier[]>([])
  const [sid, setSid] = useState<number | ''>('')
  const [items, setItems] = useState<api.SupplierItem[]>([])
  const [iid, setIid] = useState<number | ''>('')
  const [factor, setFactor] = useState('1')
  useEffect(() => { api.getSuppliers().then(setSuppliers) }, [])
  useEffect(() => { const t = setTimeout(() => api.searchThicknesses(q).then(setHits), 200); return () => clearTimeout(t) }, [q])
  useEffect(() => { setIid(''); if (sid) api.getSupplierItems(sid).then(setItems) }, [sid])
  async function save() {
    try { await api.putLink(th!.thicknessId, Number(iid), Number(factor)); toast('Linked.'); onSaved() } catch (e) { toast((e as Error).message, 'error') }
  }
  return (
    <Modal title="Link a glass / part to a supplier item" onClose={onClose} width={640}>
      <div className="lookup-body">
        <input type="search" placeholder="Find a glass or part…" value={q} onChange={e => setQ(e.target.value)} autoFocus />
        <div style={{ maxHeight: '10rem', overflow: 'auto' }}>
          <table className="grid-table"><tbody>
            {hits.map(h => <tr key={h.thicknessId} className={th?.thicknessId === h.thicknessId ? 'sel' : ''} onClick={() => setTh(h)}>
              <td>{h.partCode} <small>{h.partDescription}</small></td><td>{thick(h.thickNum, h.thickDen)}</td><td>{h.linked && <small>already linked</small>}</td></tr>)}
          </tbody></table>
        </div>
        <Field label="Supplier"><select value={sid} onChange={e => setSid(e.target.value ? Number(e.target.value) : '')}><option value="">Choose…</option>{suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select></Field>
        <Field label="Item"><select value={iid} onChange={e => setIid(e.target.value ? Number(e.target.value) : '')} disabled={!sid}><option value="">Choose…</option>{items.map(i => <option key={i.id} value={i.id}>{i.sku} ({i.unitCost.toFixed(4)}){i.description ? ` ${i.description}` : ''}</option>)}</select></Field>
        <Field label="Factor"><input type="number" step="any" min="0" value={factor} onChange={e => setFactor(e.target.value)} style={{ width: '8rem' }} /></Field>
        <p className="muted">Factor converts the supplier's unit to ours, e.g. 0.025 for a 60×96 sheet priced per sheet but sold per sq ft (1 ÷ 40).</p>
        <p className="row-actions"><button onClick={save} disabled={!th || !iid || !(Number(factor) > 0)}>Link</button><button onClick={onClose}>Cancel</button></p>
      </div>
    </Modal>
  )
}

// ---------------------------------------------------------------------------------------------------------------------
function Markups({ onChanged }: { onChanged: () => void }) {
  const { toast } = useShell()
  const [rows, setRows] = useState<api.Markup[]>([])
  const [vals, setVals] = useState<Record<string, string>>({})
  const load = useCallback(() => api.getMarkups().then(r => { setRows(r); setVals(Object.fromEntries(r.map(m => [m.code, m.markupPct?.toString() ?? '']))) }), [])
  useEffect(() => { load() }, [load])
  async function save(m: api.Markup) {
    const v = vals[m.code].trim()
    try {
      const r = await api.putMarkup(m.code, v === '' ? null : Number(v))
      toast(v === '' ? `${m.code} is no longer managed automatically.` : `${m.code} markup saved. ${r.priceChanges ? `${r.priceChanges} price changes waiting for review.` : 'No price changes needed.'}`)
      load(); onChanged()
    } catch (e) { toast((e as Error).message, 'error') }
  }
  return (
    <>
      <p className="muted">Markup % on cost for each price category. Leave blank to keep a category's prices manual: automation never touches it. Prices apply to the first price column (Price 1).</p>
      <table className="grid-table">
        <thead><tr><th>Category</th><th>Description</th><th>Markup %</th><th>Linked prices</th><th /></tr></thead>
        <tbody>
          {rows.map(m => (
            <tr key={m.code}><td>{m.code}</td><td>{m.description}</td>
              <td><input type="number" step="0.01" value={vals[m.code] ?? ''} onChange={e => setVals({ ...vals, [m.code]: e.target.value })} style={{ width: '6rem' }} placeholder="manual" /></td>
              <td>{m.linkedPrices}</td>
              <td><button disabled={(vals[m.code] ?? '') === (m.markupPct?.toString() ?? '')} onClick={() => save(m)}>Save</button></td></tr>
          ))}
        </tbody>
      </table>
      <p><button onClick={async () => { try { const r = await api.recalculate(); toast(r.priceChanges ? `${r.priceChanges} price changes waiting for review.` : 'Prices already match costs.'); onChanged() } catch (e) { toast((e as Error).message, 'error') } }}>Recalculate all now</button></p>
    </>
  )
}

// ---------------------------------------------------------------------------------------------------------------------
function Pending({ onChanged }: { onChanged: () => void }) {
  const { toast, me } = useShell()
  const [batches, setBatches] = useState<api.Batch[] | null>(null)
  const [open, setOpen] = useState<api.Batch | null>(null)
  const [changes, setChanges] = useState<api.BatchChange[] | null>(null)
  const load = useCallback(() => api.getBatches().then(setBatches), [])
  useEffect(() => { load() }, [load])
  useEffect(() => { setChanges(null); if (open) api.getBatch(open.id).then(setChanges) }, [open])
  async function decide(b: api.Batch, approve: boolean) {
    if (approve && !confirm(`Apply ${b.changeCount} price changes to live pricing?`)) return
    try {
      const r = await api.decideBatch(b.id, approve)
      toast(approve ? `${r.pricesUpdated} prices updated.` : 'Changes rejected.'); setOpen(null); load(); onChanged()
    } catch (e) { toast((e as Error).message, 'error'); load() }
  }
  return (
    <>
      <table className="grid-table">
        <thead><tr><th>Proposed</th><th>From</th><th>Changes</th><th>Status</th><th /></tr></thead>
        <tbody>
          {(batches ?? []).map(b => (
            <tr key={b.id} className={open?.id === b.id ? 'sel' : ''} onClick={() => setOpen(b)}>
              <td>{instant(b.createdAt, me.timeZone)}</td><td>{b.supplier ?? 'All suppliers'} <small>({b.source})</small></td><td>{b.changeCount}</td>
              <td>{b.status}{b.decidedBy && <small> by {b.decidedBy}</small>}</td>
              <td>{b.status === 'pending' && <><button onClick={e => { e.stopPropagation(); decide(b, true) }}>Approve</button> <button onClick={e => { e.stopPropagation(); decide(b, false) }}>Reject</button></>}</td>
            </tr>
          ))}
          {batches?.length === 0 && <tr><td colSpan={5} className="muted">No price changes have been proposed yet.</td></tr>}
        </tbody>
      </table>
      {open && (
        <div className="panel-block">
          <h3>Batch #{open.id}: old vs new price</h3>
          <table className="grid-table">
            <thead><tr><th>Part</th><th>Thickness</th><th>Category</th><th style={{ textAlign: 'right' }}>Cost</th><th style={{ textAlign: 'right' }}>Markup</th><th style={{ textAlign: 'right' }}>Old</th><th style={{ textAlign: 'right' }}>New</th><th style={{ textAlign: 'right' }}>Change</th></tr></thead>
            <tbody>
              {(changes ?? []).map((c, i) => {
                const d = c.oldPrice ? (c.newPrice / c.oldPrice - 1) * 100 : null
                return <tr key={i}><td>{c.partCode} <small>{c.partDescription}</small></td><td>{thick(c.thickNum, c.thickDen)}</td><td>{c.category}</td>
                  <td style={{ textAlign: 'right' }}>{c.cost.toFixed(4)}</td><td style={{ textAlign: 'right' }}>{c.markupPct}%</td>
                  <td style={{ textAlign: 'right' }}>{c.oldPrice === null ? '—' : money(c.oldPrice)}</td><td style={{ textAlign: 'right' }}><strong>{money(c.newPrice)}</strong></td>
                  <td style={{ textAlign: 'right' }}>{d === null ? 'new' : `${d > 0 ? '+' : ''}${d.toFixed(1)}%`}</td></tr>
              })}
              {changes === null && <tr><td colSpan={8} className="muted">Loading…</td></tr>}
            </tbody>
          </table>
        </div>
      )}
    </>
  )
}
