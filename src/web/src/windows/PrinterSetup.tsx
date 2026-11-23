import { useEffect, useState } from 'react'
import * as api from '../api'
import { pageHtml, printHtml } from '../print/engine'
import { DOC_KINDS, PAPERS, loadPrefs, newProfile, savePrefs, type Paper, type PrintPrefs, type PrinterProfile, pageSize } from '../print/printers'
import { renderTestPage } from '../print/render'
import { useShell } from '../shell/WindowContext'
import { Field } from '../shell/ui'

/**
 * Printer setup for this device. Add a profile for each printer connected to this computer or tablet (name, paper, margins,
 * copies) and say which documents go to which. Printing then opens the system print dialog, which lists the device's printers.
 */
export function PrinterSetup() {
  const shell = useShell()
  const [prefs, setPrefs] = useState<PrintPrefs>(loadPrefs)
  const [sel, setSel] = useState(prefs.profiles[0].id)
  const [company, setCompany] = useState<api.CompanyInfo | null>(null)
  useEffect(() => { api.getCompany().then(setCompany).catch(() => {}) }, [])
  const p = prefs.profiles.find(x => x.id === sel) ?? prefs.profiles[0]

  const commit = (next: PrintPrefs) => { setPrefs(next); savePrefs(next) }
  const patch = (c: Partial<PrinterProfile>) => commit({ ...prefs, profiles: prefs.profiles.map(x => x.id === p.id ? { ...x, ...c } : x) })
  const add = () => { const n = newProfile(`Printer ${prefs.profiles.length + 1}`); commit({ ...prefs, profiles: [...prefs.profiles, n] }); setSel(n.id) }
  function remove() {
    if (prefs.profiles.length < 2) return shell.toast('Keep at least one printer profile.', 'error')
    if (!window.confirm(`Remove "${p.name}"?`)) return
    const profiles = prefs.profiles.filter(x => x.id !== p.id)
    const defaults = Object.fromEntries(Object.entries(prefs.defaults).filter(([, v]) => v !== p.id))
    commit({ ...prefs, profiles, defaults }); setSel(profiles[0].id)
  }
  const usedFor = (k: string) => (prefs.defaults as Record<string, string>)[k] === p.id
  const setUse = (k: string, on: boolean) => {
    const defaults = { ...prefs.defaults } as Record<string, string>
    if (on) defaults[k] = p.id; else if (defaults[k] === p.id) delete defaults[k]
    commit({ ...prefs, defaults })
  }
  const test = async () => {
    const { w, h } = pageSize(p)
    const logo = prefs.watermark ? await api.getLogoDataUrl().catch(() => null) : null
    void printHtml(pageHtml(renderTestPage(p.name, `${p.paper === 'custom' ? 'Custom' : PAPERS[p.paper].label} · ${w} × ${h} in${p.landscape ? ' (landscape)' : ''} · margins ${p.margin} in · scale ${p.scale}% · ${p.copies} cop${p.copies === 1 ? 'y' : 'ies'}`, company), p, 'Test page', prefs.watermark ? (logo ? { logo } : company?.name) : null))
  }
  const num = (v: string, min: number, max: number, d: number) => Math.min(max, Math.max(min, Number.isFinite(Number(v)) && v !== '' ? Number(v) : d))

  return (
    <div className="maint">
      <aside className="maint-list">
        <ul>{prefs.profiles.map(x => <li key={x.id}><button className={x.id === p.id ? 'on' : ''} onClick={() => setSel(x.id)}>{x.name}<small>{x.systemName || PAPERS[x.paper].label}</small></button></li>)}</ul>
        <button onClick={add}>+ Add printer</button>
      </aside>
      <div className="maint-form">
        <p className="muted small">Printers belong to the device you are on, so this list is saved on <b>this</b> computer or tablet only. Each time you print, the system dialog lists every printer installed here; a profile just remembers the paper, margins and copies for it.</p>
        <Field label="Name"><input value={p.name} maxLength={40} onChange={e => patch({ name: e.target.value })} style={{ width: '16rem' }} /></Field>
        <Field label="Printer (as the system names it)"><input value={p.systemName} maxLength={80} placeholder="e.g. HP LaserJet Pro, Front desk" onChange={e => patch({ systemName: e.target.value })} style={{ width: '16rem' }} /></Field>
        <Field label="Paper"><select value={p.paper} onChange={e => patch({ paper: e.target.value as Paper })}>{Object.entries(PAPERS).map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}</select></Field>
        {p.paper === 'custom' && <Field label="Size (inches)"><input type="number" step="0.01" min={2} max={20} value={p.width} onChange={e => patch({ width: num(e.target.value, 2, 20, 8.5) })} style={{ width: '5rem' }} /> × <input type="number" step="0.01" min={2} max={30} value={p.height} onChange={e => patch({ height: num(e.target.value, 2, 30, 11) })} style={{ width: '5rem' }} /></Field>}
        <Field label="Orientation"><label className="radio"><input type="radio" checked={!p.landscape} onChange={() => patch({ landscape: false })} /> Portrait</label><label className="radio"><input type="radio" checked={p.landscape} onChange={() => patch({ landscape: true })} /> Landscape</label></Field>
        <Field label="Margins (inches)"><input type="number" step="0.05" min={0} max={2} value={p.margin} onChange={e => patch({ margin: num(e.target.value, 0, 2, 0.5) })} style={{ width: '5rem' }} /></Field>
        <Field label="Scale (%)"><input type="number" step="5" min={25} max={200} value={p.scale} onChange={e => patch({ scale: num(e.target.value, 25, 200, 100) })} style={{ width: '5rem' }} /></Field>
        <Field label="Copies"><input type="number" min={1} max={20} value={p.copies} onChange={e => patch({ copies: num(e.target.value, 1, 20, 1) })} style={{ width: '5rem' }} /></Field>
        <Field label=""><label className="chk"><input type="checkbox" checked={p.color} onChange={e => patch({ color: e.target.checked })} /> Print in color</label></Field>
        <fieldset><legend>Use this printer for</legend>
          {DOC_KINDS.map(k => <label key={k.kind} className="chk"><input type="checkbox" checked={usedFor(k.kind)} onChange={e => setUse(k.kind, e.target.checked)} /> {k.label}</label>)}
          <p className="muted small">Anything not assigned goes to the first printer in the list.</p>
        </fieldset>
        <label className="chk"><input type="checkbox" checked={prefs.watermark} onChange={e => commit({ ...prefs, watermark: e.target.checked })} /> Print the company logo (or the company name when no logo is set) as a faint watermark on every page (can also be switched per print)</label>
        <label className="chk"><input type="checkbox" checked={prefs.showPricesOnWorkOrder} onChange={e => commit({ ...prefs, showPricesOnWorkOrder: e.target.checked })} /> Show prices on printed work orders (off = shop copy without prices)</label>
        <div className="actions"><button onClick={test}>Print test page…</button><button onClick={remove}>Remove this printer</button></div>
      </div>
    </div>
  )
}
