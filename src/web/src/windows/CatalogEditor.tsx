import { useCallback, useEffect, useState } from 'react'
import * as api from '../api'
import { useShell, useWinId } from '../shell/WindowContext'
import { Field, Toolbar } from '../shell/ui'

type Num = string
interface PriceF { category: string; p1: Num; p2: Num; p3: Num; p4: Num; min: Num; labor: Num; minLabor: Num; taxable: boolean }
interface ThickF { num: string; den: string; markup: string; prices: PriceF[] }
interface Form { code: string; description: string; uomId: string; laborUomId: string; notePrinted: string; noteInternal: string; thicks: ThickF[] }

const t = (v: number | null) => (v == null ? '' : String(v))
const n = (s: string) => (s.trim() === '' || !Number.isFinite(Number(s)) ? null : Number(s))
const blankPrice = (category = ''): PriceF => ({ category, p1: '', p2: '', p3: '', p4: '', min: '', labor: '', minLabor: '', taxable: true })
const blank = (): Form => ({ code: '', description: '', uomId: '', laborUomId: '', notePrinted: '', noteInternal: '', thicks: [] })
const fromItem = (i: api.ItemDetail): Form => ({
  code: i.code, description: i.description ?? '', uomId: t(i.uomId), laborUomId: t(i.laborUomId), notePrinted: i.notePrinted ?? '', noteInternal: i.noteInternal ?? '',
  thicks: i.thicknesses.map(x => ({ num: String(x.num), den: String(x.den), markup: x.markupCategory ?? '', prices: x.prices.map(p => ({ category: p.category ?? '', p1: t(p.price1), p2: t(p.price2), p3: t(p.price3), p4: t(p.price4), min: t(p.minPrice), labor: t(p.laborPrice), minLabor: t(p.minLabor), taxable: p.taxable })) })),
})

/** Glass types (source G) and parts (source P): code, unit, thicknesses and the price of each thickness per price category. */
export function CatalogEditor({ source }: { source: 'G' | 'P' }) {
  const shell = useShell(), winId = useWinId(), admin = shell.me.isAdmin
  const word = source === 'G' ? 'glass type' : 'part'
  const [list, setList] = useState<api.ItemSummary[]>([]); const [q, setQ] = useState('')
  const [sel, setSel] = useState<number | null>(null); const [isNew, setIsNew] = useState(false)
  const [orig, setOrig] = useState<api.ItemDetail | null>(null)
  const [f, setF] = useState<Form>(blank()); const [dirty, setDirty] = useState(false)
  const [uoms, setUoms] = useState<{ id: number; name: string; shortName: string | null }[]>([]); const [cats, setCats] = useState<api.MaintRow[]>([])
  const [busy, setBusy] = useState(false); const [error, setError] = useState('')

  const loadList = useCallback(() => api.getItems(q, source).then(setList).catch(e => setError(e.message)), [q, source])
  useEffect(() => { const h = setTimeout(loadList, 200); return () => clearTimeout(h) }, [loadList])
  useEffect(() => { api.getUoms().then(setUoms).catch(() => {}); api.getMaintRows('price-categories').then(setCats).catch(() => {}) }, [])
  useEffect(() => { shell.setTitle(winId, source === 'G' ? 'Flat Glass Types' : 'Flat Glass Parts') }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const guard = (go: () => void) => { if (!dirty || window.confirm('Discard your unsaved changes?')) go() }
  const choose = (id: number) => guard(async () => { try { const i = await api.getItem(id); setOrig(i); setF(fromItem(i)); setSel(id); setIsNew(false); setDirty(false); setError('') } catch (e) { setError((e as Error).message) } })
  const startNew = () => guard(() => { setOrig(null); setF(blank()); setSel(null); setIsNew(true); setDirty(false); setError('') })
  const edit = (c: Partial<Form>) => { setF(x => ({ ...x, ...c })); setDirty(true) }
  const editThick = (i: number, c: Partial<ThickF>) => edit({ thicks: f.thicks.map((x, k) => k === i ? { ...x, ...c } : x) })
  const editPrice = (i: number, j: number, c: Partial<PriceF>) => editThick(i, { prices: f.thicks[i].prices.map((x, k) => k === j ? { ...x, ...c } : x) })

  const toInput = (): api.ItemInput => ({
    code: f.code.trim(), source, description: f.description.trim() || null, uomId: n(f.uomId), laborUomId: n(f.laborUomId), notePrinted: f.notePrinted.trim() || null, noteInternal: f.noteInternal.trim() || null,
    thicknesses: f.thicks.map(x => ({ num: n(x.num) ?? 0, den: n(x.den) ?? 0, markupCategory: x.markup.trim() || null,
      prices: x.prices.map(p => ({ category: p.category || null, price1: n(p.p1), price2: n(p.p2), price3: n(p.p3), price4: n(p.p4), minPrice: n(p.min), laborPrice: n(p.labor), minLabor: n(p.minLabor), taxable: p.taxable })) })),
    updatedAt: orig?.updatedAt ?? null,
  })
  async function save() {
    setBusy(true); setError('')
    try {
      let id = sel
      if (isNew) id = (await api.createItem(toInput())).id; else await api.updateItem(sel!, toInput())
      shell.toast('Saved.'); setDirty(false); setIsNew(false); await loadList()
      const i = await api.getItem(id!); setOrig(i); setF(fromItem(i)); setSel(id)
    } catch (e) { setError((e as Error).message); shell.toast('Not saved.', 'error') } finally { setBusy(false) }
  }
  async function remove() {
    if (sel == null || !window.confirm(`Delete ${f.code}? This also deletes its prices.`)) return
    try { await api.deleteItem(sel); shell.toast('Deleted.'); setSel(null); setOrig(null); setF(blank()); setDirty(false); await loadList() } catch (e) { setError((e as Error).message) }
  }
  useEffect(() => {
    const k = (e: KeyboardEvent) => { if ((e.ctrlKey || e.metaKey) && e.key === 's') { e.preventDefault(); if (dirty && !busy && admin) void save() } }
    document.addEventListener('keydown', k); return () => document.removeEventListener('keydown', k)
  })

  const show = isNew || sel != null
  const numIn = (v: string, on: (s: string) => void, w = '4.4rem', ph = '') => <input value={v} inputMode="decimal" placeholder={ph} onChange={e => on(e.target.value)} style={{ width: w }} />
  return (
    <>
      <Toolbar tools={[
        { label: 'Save', icon: 'save', onClick: save, disabled: !admin || busy || !dirty, title: !admin ? 'Only administrators can change the catalog' : 'Save (Ctrl+S)' },
        { label: 'New', icon: 'clear', onClick: startNew, disabled: !admin, title: `Add a ${word}` },
        { label: 'Delete', icon: 'delete', onClick: remove, disabled: !admin || sel == null || isNew, title: `Delete this ${word}` }]} />
      {error && <p className="formerr">{error}</p>}
      <div className="maint wide">
        <div>
          <input className="search" placeholder={`Search ${word}s…`} value={q} onChange={e => setQ(e.target.value)} />
          <div className="maint-list"><table className="grid-table"><thead><tr><th>Code</th><th>Description</th></tr></thead>
            <tbody>{list.map(i => <tr key={i.id} className={i.id === sel ? 'sel' : ''} onClick={() => choose(i.id)} style={{ cursor: 'pointer' }}><td>{i.code}</td><td>{i.description}</td></tr>)}
              {list.length === 0 && <tr><td colSpan={2} className="muted">Nothing found.</td></tr>}</tbody></table></div>
        </div>
        <div className="maint-form">
          {!show && <p className="muted">Choose a {word}, or click New.</p>}
          {show && <fieldset disabled={!admin} style={{ border: 0, padding: 0 }}>
            <Field label="Code"><input value={f.code} maxLength={30} onChange={e => edit({ code: e.target.value })} style={{ width: '14rem' }} /></Field>
            <Field label="Description"><input value={f.description} maxLength={120} onChange={e => edit({ description: e.target.value })} style={{ width: '26rem' }} /></Field>
            <Field label="Sold by the"><select value={f.uomId} onChange={e => edit({ uomId: e.target.value })}><option value="">(not set)</option>{uoms.map(u => <option key={u.id} value={u.id}>{u.name}</option>)}</select>
              <span className="lbl">Labor by the</span><select value={f.laborUomId} onChange={e => edit({ laborUomId: e.target.value })}><option value="">(not set)</option>{uoms.map(u => <option key={u.id} value={u.id}>{u.name}</option>)}</select></Field>
            <Field label="Printed note" wide><textarea className="edit" value={f.notePrinted} onChange={e => edit({ notePrinted: e.target.value })} /></Field>
            <Field label="Internal note" wide><textarea className="edit" value={f.noteInternal} onChange={e => edit({ noteInternal: e.target.value })} /></Field>
            <h4>Thicknesses and prices</h4>
            <p className="muted small">{source === 'G' ? 'Glass is priced per square foot. ' : ''}A part without a thickness uses <b>0 / 0</b>. Price levels 1-4 are alternate prices: a quote uses the first one that is filled in. <b>(default)</b> is used when a price category has no price of its own.</p>
            {f.thicks.map((th, i) => (
              <fieldset key={i} className="thick">
                <legend>Thickness <input value={th.num} inputMode="numeric" onChange={e => editThick(i, { num: e.target.value })} style={{ width: '3rem' }} /> / <input value={th.den} inputMode="numeric" onChange={e => editThick(i, { den: e.target.value })} style={{ width: '3rem' }} /> in
                  <span className="lbl">Markup category</span><input value={th.markup} maxLength={20} onChange={e => editThick(i, { markup: e.target.value })} style={{ width: '6rem' }} />
                  <button className="x" title="Remove this thickness and its prices" onClick={() => edit({ thicks: f.thicks.filter((_, k) => k !== i) })}>×</button></legend>
                <table className="grid-table"><thead><tr><th>Price category</th><th className="num">Level 1</th><th className="num">Level 2</th><th className="num">Level 3</th><th className="num">Level 4</th><th className="num">Minimum</th><th className="num">Labor</th><th className="num">Min labor</th><th>Tax</th><th /></tr></thead>
                  <tbody>{th.prices.map((p, j) => <tr key={j}>
                    <td><select value={p.category} onChange={e => editPrice(i, j, { category: e.target.value })}><option value="">(default)</option>{cats.map(c => <option key={String(c.code)} value={String(c.code)}>{String(c.code)}{c.description ? ` – ${c.description}` : ''}</option>)}</select></td>
                    <td className="num">{numIn(p.p1, v => editPrice(i, j, { p1: v }))}</td><td className="num">{numIn(p.p2, v => editPrice(i, j, { p2: v }))}</td><td className="num">{numIn(p.p3, v => editPrice(i, j, { p3: v }))}</td><td className="num">{numIn(p.p4, v => editPrice(i, j, { p4: v }))}</td>
                    <td className="num">{numIn(p.min, v => editPrice(i, j, { min: v }))}</td><td className="num">{numIn(p.labor, v => editPrice(i, j, { labor: v }))}</td><td className="num">{numIn(p.minLabor, v => editPrice(i, j, { minLabor: v }))}</td>
                    <td><input type="checkbox" checked={p.taxable} title="Taxable" onChange={e => editPrice(i, j, { taxable: e.target.checked })} /></td>
                    <td><button className="x" title="Remove this price" onClick={() => editThick(i, { prices: th.prices.filter((_, k) => k !== j) })}>×</button></td></tr>)}</tbody></table>
                <button onClick={() => editThick(i, { prices: [...th.prices, blankPrice()] })}>+ Add price category</button>
              </fieldset>))}
            <button onClick={() => edit({ thicks: [...f.thicks, { num: source === 'G' ? '1' : '0', den: source === 'G' ? '4' : '0', markup: '', prices: [blankPrice()] }] })}>+ Add thickness</button>
          </fieldset>}
        </div>
      </div>
    </>
  )
}
