import { useCallback, useEffect, useState } from 'react'
import * as api from '../api'
import { useShell } from '../shell/WindowContext'
import { Field, Toolbar } from '../shell/ui'
import { MaintenanceEditor } from './MaintenanceEditor'

const pct = (n: number) => `${n.toFixed(4)}%`

export const TaxCodeEntry = () => <MaintenanceEditor table="tax-codes" />

/** A tax group is a code, a description and up to five tax codes in fixed positions; its combined rate is shown live. */
export function TaxGroupEntry() {
  const shell = useShell(), admin = shell.me.isAdmin
  const [groups, setGroups] = useState<api.TaxGroup[]>([]); const [codes, setCodes] = useState<api.TaxCode[]>([])
  const [sel, setSel] = useState<string | null>(null); const [isNew, setIsNew] = useState(false)
  const [code, setCode] = useState(''); const [desc, setDesc] = useState(''); const [slots, setSlots] = useState<string[]>(['', '', '', '', ''])
  const [dirty, setDirty] = useState(false); const [error, setError] = useState(''); const [busy, setBusy] = useState(false); const [filter, setFilter] = useState('')

  const reload = useCallback(async () => { setGroups(await api.getTaxGroups()) }, [])
  useEffect(() => { reload().catch(e => setError(e.message)); api.getTaxCodes().then(setCodes) }, [reload])

  const confirmDiscard = () => !dirty || window.confirm('Discard your unsaved changes?')
  function choose(g: api.TaxGroup) {
    if (!confirmDiscard()) return
    setSel(g.code); setIsNew(false); setCode(g.code); setDesc(g.description ?? ''); setDirty(false); setError('')
    setSlots([1, 2, 3, 4, 5].map(n => g.codes.find(c => c.position === n)?.code ?? ''))
  }
  function startNew() { if (!confirmDiscard()) return; setSel(null); setIsNew(true); setCode(''); setDesc(''); setSlots(['', '', '', '', '']); setDirty(false); setError('') }
  const input = (): api.TaxGroupInput => ({ code: code.trim(), description: desc.trim(), codes: slots.map((c, i) => ({ position: i + 1, code: c })).filter(c => c.code) })
  async function save() {
    if (!code.trim()) { setError('Code is required.'); return }
    const dup = slots.filter(Boolean).find((c, i, a) => a.indexOf(c) !== i)
    if (dup) { setError(`Tax code ${dup} is used twice.`); return }
    setBusy(true); setError('')
    try {
      if (isNew) { await api.createTaxGroup(input()); setSel(code.trim()); setIsNew(false) } else if (sel) await api.updateTaxGroup(sel, input())
      await reload(); setDirty(false); shell.toast('Saved.')
    } catch (e) { setError(e instanceof Error ? e.message : 'Save failed.'); shell.toast('Not saved.', 'error') } finally { setBusy(false) }
  }
  async function remove() {
    if (!sel || !window.confirm(`Delete tax group ${sel}? This cannot be undone.`)) return
    setBusy(true); setError('')
    try { await api.deleteTaxGroup(sel); await reload(); setSel(null); setCode(''); setDesc(''); setSlots(['', '', '', '', '']); setDirty(false); shell.toast('Deleted.') }
    catch (e) { setError(e instanceof Error ? e.message : 'Delete failed.') } finally { setBusy(false) }
  }
  const rate = slots.reduce((a, c) => a + (codes.find(x => x.code === c)?.percent ?? 0), 0)
  const shown = groups.filter(g => !filter || (g.code + ' ' + (g.description ?? '')).toLowerCase().includes(filter.toLowerCase()))
  const active = sel != null || isNew
  return (
    <>
      <Toolbar tools={[
        { label: 'Save', icon: 'save', onClick: save, disabled: busy || !dirty || !admin, title: admin ? 'Save' : 'Only administrators can change these' },
        { label: 'Delete', icon: 'delete', onClick: remove, disabled: busy || !sel || !admin },
        { label: 'New', icon: 'clear', onClick: startNew, disabled: !admin }]} />
      {!admin && <p className="muted">You can look at tax groups; only an administrator can change them.</p>}
      {error && <p className="formerr">{error}</p>}
      <div className="maint">
        <div>
          <input className="search" placeholder="Find" value={filter} onChange={e => setFilter(e.target.value)} aria-label="Find" />
          <div className="maint-list"><table className="grid-table selectable"><thead><tr><th>Code</th><th>Description</th></tr></thead>
            <tbody>{shown.map(g => <tr key={g.code} className={g.code === sel ? 'sel' : ''} onClick={() => choose(g)}><td>{g.code}</td><td>{g.description}</td></tr>)}</tbody></table></div>
        </div>
        <fieldset disabled={!admin || !active}>
          <legend>{isNew ? 'New tax group' : sel ? `Tax group ${sel}` : 'Select a tax group'}{dirty && <span className="dirty">unsaved changes</span>}</legend>
          <Field label="Tax Group"><input value={code} disabled={!isNew} maxLength={20} onChange={e => { setCode(e.target.value); setDirty(true) }} style={{ width: '12rem' }} /></Field>
          <Field label="Description"><input value={desc} maxLength={80} onChange={e => { setDesc(e.target.value); setDirty(true) }} style={{ width: '18rem' }} /></Field>
          {slots.map((s, i) => <Field key={i} label={`Tax Code ${i + 1}`}>
            <select value={s} onChange={e => { setSlots(p => p.map((x, j) => j === i ? e.target.value : x)); setDirty(true) }} style={{ width: '18rem' }}>
              <option value="">(none)</option>
              {codes.map(c => <option key={c.code} value={c.code}>{c.code}{c.description ? ` - ${c.description}` : ''} ({pct(c.percent)})</option>)}
              {s && !codes.some(c => c.code === s) && <option value={s}>{s}</option>}
            </select></Field>)}
          <p className="muted">Combined rate: {pct(rate)}</p>
        </fieldset>
      </div>
    </>
  )
}
