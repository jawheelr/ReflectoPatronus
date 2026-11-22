import { useCallback, useEffect, useMemo, useState } from 'react'
import * as api from '../api'
import { useShell, useWinId } from '../shell/WindowContext'
import { Toolbar } from '../shell/ui'

/** Tables offered in the Maintenance menu; the window argument is the index into this list. */
export const MAINT_TABLES = ['tax-codes', 'customer-categories', 'price-categories', 'pay-categories', 'staff', 'statuses'] as const
export type MaintKey = typeof MAINT_TABLES[number]

const kindOf = (c: api.MaintCol): 'text' | 'bool' | 'decimal' => {
  const t = c.type
  return t === 'Bool' || t === 1 ? 'bool' : t === 'Decimal' || t === 2 ? 'decimal' : 'text'
}
type Form = Record<string, string | boolean>

/** One editor for all the small code tables: the list on the left, the selected row's form on the right. */
export function MaintenanceEditor({ table }: { table: string }) {
  const shell = useShell(), winId = useWinId()
  const [def, setDef] = useState<api.MaintTable | null>(null)
  const [rows, setRows] = useState<api.MaintRow[]>([])
  const [sel, setSel] = useState<string | null>(null)     // key of the selected row; null with isNew = a new row
  const [isNew, setIsNew] = useState(false)
  const [f, setF] = useState<Form>({})
  const [dirty, setDirty] = useState(false)
  const [filter, setFilter] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const admin = api.can(shell.me, 'maintenance.write')

  const editable = useMemo(() => def ? (def.keyIdentity ? def.cols : [def.keyColumn, ...def.cols]) : [], [def])
  const blank = useCallback((d: api.MaintTable): Form => Object.fromEntries([d.keyColumn, ...d.cols].map(c => [c.api, kindOf(c) === 'bool' ? false : ''])), [])
  const keyOf = (r: api.MaintRow) => String(r[def!.keyColumn.api])

  const loadRows = useCallback(async (t: string) => setRows(await api.getMaintRows(t)), [])
  useEffect(() => {
    api.getMaintTables().then(all => {
      const d = all.find(x => x.key === table); if (!d) { setError('Unknown table.'); return }
      setDef(d); setF(blank(d)); shell.setTitle(winId, d.title); return loadRows(table)
    }).catch(e => setError(e.message))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [table])

  const confirmDiscard = () => !dirty || window.confirm('Discard your unsaved changes?')
  function choose(r: api.MaintRow) {
    if (!def || !confirmDiscard()) return
    setSel(keyOf(r)); setIsNew(false); setDirty(false); setError('')
    setF(Object.fromEntries([def.keyColumn, ...def.cols].map(c => [c.api, kindOf(c) === 'bool' ? !!r[c.api] : r[c.api] == null ? '' : String(r[c.api])])))
  }
  function startNew() { if (!def || !confirmDiscard()) return; setSel(null); setIsNew(true); setDirty(false); setError(''); setF(blank(def)) }
  const set = (k: string, v: string | boolean) => { setF(p => ({ ...p, [k]: v })); setDirty(true) }

  const payload = (): api.MaintRow => Object.fromEntries(editable.map(c => [c.api, kindOf(c) === 'bool' ? f[c.api] === true : (f[c.api] as string).trim() === '' ? null : (f[c.api] as string).trim()]))
  async function save() {
    if (!def) return
    const missing = editable.find(c => !c.readOnly && c.required && kindOf(c) !== 'bool' && !(f[c.api] as string).trim())
    if (missing) { setError(`${missing.label} is required.`); return }
    setBusy(true); setError('')
    try {
      if (isNew) { const r = await api.createMaintRow(table, payload()); await loadRows(table); setSel(r.key); setIsNew(false) }
      else if (sel != null) { await api.updateMaintRow(table, sel, payload()); await loadRows(table) }
      setDirty(false); shell.toast('Saved.')
    } catch (e) { setError(e instanceof Error ? e.message : 'Save failed.'); shell.toast('Not saved.', 'error') }
    finally { setBusy(false) }
  }
  async function remove() {
    if (sel == null || !window.confirm(`Delete ${def!.title.replace(/ Entry$/, '')} entry ${sel}? This cannot be undone.`)) return
    setBusy(true); setError('')
    try { await api.deleteMaintRow(table, sel); await loadRows(table); setSel(null); setIsNew(false); setDirty(false); setF(blank(def!)); shell.toast('Deleted.') }
    catch (e) { setError(e instanceof Error ? e.message : 'Delete failed.') }
    finally { setBusy(false) }
  }
  useEffect(() => {
    const k = (e: KeyboardEvent) => { if ((e.ctrlKey || e.metaKey) && e.key === 's') { e.preventDefault(); if (dirty && !busy && admin) void save() } }
    document.addEventListener('keydown', k); return () => document.removeEventListener('keydown', k)
  })

  if (!def) return <p className="muted">{error || 'Loading…'}</p>
  const shownCols = [def.keyColumn, ...def.cols].slice(0, 8)
  const shown = rows.filter(r => !filter || Object.values(r).some(v => String(v ?? '').toLowerCase().includes(filter.toLowerCase())))
  const protectedRow = def.protected && sel != null && rows.find(r => keyOf(r) === sel)?.isSystem === true
  return (
    <>
      <Toolbar tools={[
        { label: 'Save', icon: 'save', onClick: save, disabled: busy || !dirty || !admin, title: !admin ? 'You do not have permission to change these' : dirty ? 'Save (Ctrl+S)' : 'No changes to save' },
        { label: 'Delete', icon: 'delete', onClick: remove, disabled: busy || sel == null || !admin || protectedRow, title: protectedRow ? 'System entries cannot be deleted' : 'Delete this entry' },
        { label: 'New', icon: 'clear', onClick: startNew, disabled: !admin, title: admin ? 'Add an entry' : 'You do not have permission to change these' }]} />
      {!admin && <p className="muted">You can look at these entries; you need permission to change them.</p>}
      {def.note && <p className="muted">{def.note}</p>}
      {error && <p className="formerr">{error}</p>}
      <div className="maint">
        <div>
          <input className="search" placeholder="Find" value={filter} onChange={e => setFilter(e.target.value)} aria-label="Find" />
          <div className="maint-list">
            <table className="grid-table selectable"><thead><tr>{shownCols.map(c => <th key={c.api}>{c.label}</th>)}</tr></thead>
              <tbody>{shown.map(r => <tr key={keyOf(r)} className={keyOf(r) === sel ? 'sel' : ''} onClick={() => choose(r)}>{shownCols.map(c => <td key={c.api} className={kindOf(c) === 'decimal' ? 'num' : ''}>{kindOf(c) === 'bool' ? (r[c.api] ? '✓' : '') : String(r[c.api] ?? '')}</td>)}</tr>)}
                {shown.length === 0 && <tr><td colSpan={shownCols.length} className="muted">Nothing found.</td></tr>}</tbody></table>
          </div>
        </div>
        <fieldset disabled={!admin || (sel == null && !isNew)}>
          <legend>{isNew ? 'New entry' : sel != null ? `Entry ${sel}` : 'Select an entry'}{dirty && <span className="dirty">unsaved changes</span>}</legend>
          {editable.map(c => {
            const k = kindOf(c), isKey = !def.keyIdentity && c === def.keyColumn
            return <label className="field" key={c.api}><span>{c.label}</span>
              <div className="val">{k === 'bool'
                ? <input type="checkbox" checked={f[c.api] === true} disabled={c.readOnly} onChange={e => set(c.api, e.target.checked)} />
                : <input value={f[c.api] as string} maxLength={c.max || undefined} disabled={c.readOnly || (isKey && !isNew)} inputMode={k === 'decimal' ? 'decimal' : undefined}
                    onChange={e => set(c.api, e.target.value)} style={{ width: k === 'decimal' ? '8rem' : isKey ? '12rem' : '18rem' }} autoFocus={isKey && isNew} />}
                {isKey && !isNew && <span className="muted">fixed once created</span>}</div></label>
          })}
        </fieldset>
      </div>
    </>
  )
}
