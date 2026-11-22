import { useEffect, useMemo, useRef, useState } from 'react'
import * as api from '../api'
import { money } from '../format'
import { useShell } from '../shell/WindowContext'
import { Field, Tabs, Toolbar, type Tool } from '../shell/ui'
import { CustomerLookup } from './CustomerLookup'

type Source = 'glass' | 'part' | 'labor' | 'custom'
interface Row {
  key: number; source: Source; qty: string; partId: number | null; partLabel: string; thicks: { num: number; den: number; text: string }[]
  thickNum: number | null; thickDen: number | null; w: string; h: string; units: string; price: string; description: string; taxable: boolean | null
}
let rowKey = 1
const blankRow = (source: Source = 'glass'): Row => ({ key: rowKey++, source, qty: '1', partId: null, partLabel: '', thicks: [], thickNum: null, thickDen: null, w: '', h: '', units: '', price: '', description: '', taxable: null })
const num = (s: string) => (s.trim() === '' || !Number.isFinite(Number(s)) ? null : Number(s))

/** A saved line becomes an editable row. Saved prices are kept ("locked") so opening an old quote never silently reprices it. */
function fromLine(l: api.DocumentLine): Row {
  const r = blankRow(); r.qty = String(l.qty ?? 1)
  if ((l.source === 'glass' || l.source === 'part') && l.partId != null) {
    Object.assign(r, { source: l.source, partId: l.partId, partLabel: l.partCode ?? '', description: l.description ?? '', thickNum: l.thickNum, thickDen: l.thickDen,
      thicks: l.thickNum != null && l.thickDen ? [{ num: l.thickNum, den: l.thickDen, text: l.thickness ?? '' }] : [], w: l.widthIn != null ? String(l.widthIn) : '', h: l.heightIn != null ? String(l.heightIn) : '',
      price: l.unitPrice != null ? String(l.unitPrice) : '', taxable: l.taxable })
  } else {   // labor, text lines, and parts that are no longer in the catalog stay as typed lines with their saved price
    r.source = l.source === 'labor' ? 'labor' : 'custom'
    r.description = [l.partCode && l.source !== 'labor' ? l.partCode : '', l.description ?? ''].filter(Boolean).join(' ')
    r.price = l.unitPrice != null ? String(l.unitPrice) : l.qty ? String(l.amount / l.qty) : ''; r.taxable = l.taxable
  }
  return r
}

const toDraft = (r: Row): api.DraftLine => ({
  source: r.source, qty: num(r.qty), partId: r.partId, thickNum: r.thickNum, thickDen: r.thickDen, widthIn: num(r.w), heightIn: num(r.h),
  units: num(r.units), priceLevel: null, rate: null, unitPrice: num(r.price), amount: null, description: r.description.trim() || null, taxable: r.taxable,
})

export function PartPicker({ source, label, onPick }: { source: 'G' | 'P'; label: string; onPick: (p: api.CatalogPart) => void }) {
  const [q, setQ] = useState(label); const [hits, setHits] = useState<api.CatalogPart[]>([]); const [open, setOpen] = useState(false)
  const seq = useRef(0)
  useEffect(() => { setQ(label) }, [label])
  useEffect(() => {
    if (!open) return
    const my = ++seq.current
    const t = setTimeout(() => api.getCatalogParts(q, source).then(h => my === seq.current && setHits(h)).catch(() => {}), 150)
    return () => clearTimeout(t)
  }, [q, open, source])
  return (
    <div className="picker">
      <input value={q} placeholder={source === 'G' ? 'Glass type' : 'Part'} onFocus={() => setOpen(true)} onChange={e => { setQ(e.target.value); setOpen(true) }} onBlur={() => setTimeout(() => setOpen(false), 150)} />
      {open && hits.length > 0 && <ul>{hits.map(h => <li key={h.id}><button type="button" onMouseDown={() => { onPick(h); setQ(h.code); setOpen(false) }}>{h.code}<small>{h.description}</small></button></li>)}</ul>}
    </div>
  )
}

const NOUN: Record<string, string> = { Q: 'quote', W: 'work order', I: 'invoice' }

/** Entry form for quotes, work orders and invoices (same lines and pricing; invoices also post to the ledger on the server). `extraTools` get a guard that asks before discarding unsaved edits. */
export function QuoteEditor({ initial, onSaved, onOpen, onNew, extraTools }: {
  initial: api.DocumentDetail | null; onSaved: (id: number) => void; onOpen: () => void; onNew: () => void
  extraTools?: (guard: (f: () => void) => () => void) => Tool[]
}) {
  const shell = useShell(), { me } = shell
  const noun = NOUN[initial?.type ?? 'Q'] ?? 'document'
  const today = useMemo(() => new Date().toLocaleDateString('en-CA', { timeZone: me.companyTimeZone }), [me.companyTimeZone])
  const [customerId, setCustomerId] = useState<number | null>(initial?.customerId ?? null)
  const [customerName, setCustomerName] = useState(initial?.customer.name ?? '')
  const [date, setDate] = useState(initial?.date ?? today)
  const [po, setPo] = useState(initial?.poNumber ?? ''); const [terms, setTerms] = useState(initial?.terms ?? '')
  const [salesId, setSalesId] = useState<number | null>(initial?.salesPersonId ?? null)
  const [taxGroup, setTaxGroup] = useState(initial?.taxGroup ?? ''); const [priceCat, setPriceCat] = useState(initial?.priceCategory ?? '')
  const [discount, setDiscount] = useState(initial?.discountPercent ? String(initial.discountPercent) : '')
  const [mobile, setMobile] = useState(initial?.shopOrMobile === 'M')
  const [notes, setNotes] = useState({ spec: initial?.specialInstructions ?? '', wo: initial?.workOrderInstructions ?? '', printed: initial?.notePrinted ?? '', internal: initial?.noteInternal ?? '' })
  const [site, setSite] = useState({ address1: initial?.jobSite?.address1 ?? '', address2: initial?.jobSite?.address2 ?? '', city: initial?.jobSite?.city ?? '', state: initial?.jobSite?.state ?? '', zip: initial?.jobSite?.zip ?? '' })
  const [ins, setIns] = useState({ policy: initial?.policyNumber ?? '', claim: initial?.claimNumber ?? '', loss: initial?.lossDate ?? '' })
  const [rows, setRows] = useState<Row[]>(() => initial && initial.lines.length ? initial.lines.map(fromLine) : [blankRow()])
  const [tab, setTab] = useState('General'); const [lookup, setLookup] = useState(false)
  const [calc, setCalc] = useState<api.CalcResult | null>(null); const [calcError, setCalcError] = useState('')
  const [dirty, setDirty] = useState(false); const [busy, setBusy] = useState(false); const [error, setError] = useState('')
  const [lk, setLk] = useState<api.CustomerLookups | null>(null); const [sales, setSales] = useState<api.MaintRow[]>([])
  useEffect(() => { api.getCustomerLookups().then(setLk).catch(() => {}); api.getMaintRows('staff').then(r => setSales(r.filter(x => x.isSalesman && !x.deleted))).catch(() => {}) }, [])

  const touch = () => setDirty(true)
  const setRow = (key: number, patch: Partial<Row>) => { setRows(rs => rs.map(r => r.key === key ? { ...r, ...patch } : r)); touch() }
  const draft = (): api.DraftDocument => ({ customerId, priceCategory: priceCat || null, taxGroup: taxGroup || null, discountPercent: num(discount), lines: rows.map(toDraft) })

  // live pricing: the server prices every change (debounced), so what you see is what is saved
  useEffect(() => {
    if (customerId == null) { setCalc(null); setCalcError(''); return }
    let live = true
    const t = setTimeout(() => api.calculateDocument(draft()).then(c => { if (live) { setCalc(c); setCalcError('') } }).catch(e => { if (live) { setCalc(null); setCalcError(e.message) } }), 350)
    return () => { live = false; clearTimeout(t) }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rows, customerId, priceCat, taxGroup, discount])

  function pickPart(key: number, p: api.CatalogPart) {
    const one = p.thicknesses.length === 1 ? p.thicknesses[0] : null
    setRow(key, { partId: p.id, partLabel: p.code, description: p.description ?? '', thicks: p.thicknesses, thickNum: one?.num ?? null, thickDen: one?.den ?? null, price: '', taxable: null })
  }
  const input = (): api.DocumentInput => ({
    ...draft(), date: date || null, poNumber: po || null, salesPersonId: salesId, terms: terms || null, shopOrMobile: mobile ? 'M' : 'S',
    specialInstructions: notes.spec || null, workOrderInstructions: notes.wo || null, notePrinted: notes.printed || null, noteInternal: notes.internal || null,
    jobSite: site.address1 || site.city || site.zip ? { name: null, address1: site.address1 || null, address2: site.address2 || null, city: site.city || null, state: site.state || null, zip: site.zip || null, phones: [] } : null,
    lossDate: ins.loss || null, policyNumber: ins.policy || null, claimNumber: ins.claim || null, updatedAt: initial?.updatedAt ?? null,
  })
  async function save() {
    if (customerId == null) { setError('Choose a customer first.'); setTab('General'); return }
    setBusy(true); setError('')
    try {
      const id = initial ? (await api.updateDocument(initial.id, input()), initial.id) : (await api.createDocument(input())).id
      shell.toast(initial ? `${noun[0].toUpperCase() + noun.slice(1)} saved.` : 'Quote created.'); setDirty(false); onSaved(id)
    } catch (e) { setError(e instanceof Error ? e.message : 'Save failed.'); shell.toast('Not saved.', 'error') } finally { setBusy(false) }
  }
  async function voidIt() {
    if (!initial || !window.confirm(`Void ${noun} ${initial.number}? It stays on file, marked VOID, and cannot be edited.${initial.type === 'I' ? ' Its amount comes off the customer balance.' : ''}`)) return
    setBusy(true)
    try { await api.voidDocument(initial.id); shell.toast(`${noun[0].toUpperCase() + noun.slice(1)} voided.`); onSaved(initial.id) } catch (e) { setError(e instanceof Error ? e.message : 'Void failed.') } finally { setBusy(false) }
  }
  useEffect(() => {
    const k = (e: KeyboardEvent) => { if ((e.ctrlKey || e.metaKey) && e.key === 's') { e.preventDefault(); if (dirty && !busy) void save() } }
    document.addEventListener('keydown', k); return () => document.removeEventListener('keydown', k)
  })
  const guard = (f: () => void) => () => { if (!dirty || window.confirm('Discard your unsaved changes?')) f() }

  const priced = calc && calc.lines.length === rows.length ? calc.lines : null
  const ta = (k: keyof typeof notes, label: string) => <Field label={label} wide><textarea className="edit" value={notes[k]} onChange={e => { setNotes(n => ({ ...n, [k]: e.target.value })); touch() }} /></Field>
  return (
    <>
      <Toolbar tools={[
        { label: 'Save', icon: 'save', onClick: save, disabled: busy || !dirty, title: dirty ? 'Save (Ctrl+S)' : 'No changes to save' },
        { label: 'Open', icon: 'open', onClick: guard(onOpen), title: 'Open a document' },
        { label: 'Void', icon: 'delete', onClick: voidIt, disabled: busy || !initial, title: initial ? `Void this ${noun}` : 'Save the quote first' },
        { label: 'New', icon: 'clear', onClick: guard(onNew), title: 'Start a new quote' }, ...(extraTools?.(guard) ?? [])]} />
      {error && <p className="formerr">{error}</p>}
      <Tabs tabs={['General', 'Insurance', 'Installation', 'Notes']} active={tab} onChange={setTab} />
      <div className="panel">
        {tab === 'General' && <div className="general">
          <div>
            <Field label="Customer"><input readOnly className="ro" value={customerName} placeholder="Choose a customer" style={{ width: '22rem' }} onClick={() => setLookup(true)} /><button className="dots" title="Look up a customer" onClick={() => setLookup(true)}>…</button></Field>
            <Field label="Tax Group"><select value={taxGroup} onChange={e => { setTaxGroup(e.target.value); touch() }} style={{ width: '19rem' }}>
              <option value="">(customer default{calc?.taxGroup ? `: ${calc.taxGroup}` : ''})</option>{lk?.taxGroups.map(g => <option key={g.code} value={g.code}>{g.code}{g.description ? ` - ${g.description}` : ''}</option>)}</select></Field>
            <Field label="Price Category"><select value={priceCat} onChange={e => { setPriceCat(e.target.value); touch() }} style={{ width: '19rem' }}>
              <option value="">(customer default{calc?.priceCategory ? `: ${calc.priceCategory}` : ''})</option>{lk?.priceCategories.map(g => <option key={g.code} value={g.code}>{g.code}{g.description ? ` - ${g.description}` : ''}</option>)}</select></Field>
            <Field label="Sold by"><select value={salesId ?? ''} onChange={e => { setSalesId(e.target.value ? Number(e.target.value) : null); touch() }} style={{ width: '19rem' }}>
              <option value="">(none)</option>{sales.map(s => <option key={String(s.id)} value={Number(s.id)}>{String(s.name || s.code)}</option>)}</select></Field>
            <Field label=""><label className="radio"><input type="radio" checked={!mobile} onChange={() => { setMobile(false); touch() }} /> In Shop</label>
              <label className="radio"><input type="radio" checked={mobile} onChange={() => { setMobile(true); touch() }} /> Mobile</label></Field>
          </div>
          <div className="right">
            <Field label="Doc Date"><input type="date" value={date} onChange={e => { setDate(e.target.value); touch() }} /></Field>
            <Field label="PO #"><input value={po} maxLength={40} onChange={e => { setPo(e.target.value); touch() }} style={{ width: '12rem' }} /></Field>
            <Field label="Terms"><input value={terms} maxLength={40} onChange={e => { setTerms(e.target.value); touch() }} style={{ width: '12rem' }} /></Field>
            <Field label="Discount %"><input value={discount} inputMode="decimal" onChange={e => { setDiscount(e.target.value); touch() }} style={{ width: '6rem' }} /></Field>
          </div>
        </div>}
        {tab === 'Insurance' && <>
          <Field label="Policy #"><input value={ins.policy} maxLength={40} onChange={e => { setIns(x => ({ ...x, policy: e.target.value })); touch() }} style={{ width: '14rem' }} /></Field>
          <Field label="Claim #"><input value={ins.claim} maxLength={40} onChange={e => { setIns(x => ({ ...x, claim: e.target.value })); touch() }} style={{ width: '14rem' }} /></Field>
          <Field label="Loss Date"><input type="date" value={ins.loss} onChange={e => { setIns(x => ({ ...x, loss: e.target.value })); touch() }} /></Field>
        </>}
        {tab === 'Installation' && <fieldset><legend>Job site</legend>
          <Field label="Address" wide><input value={site.address1} onChange={e => { setSite(s => ({ ...s, address1: e.target.value })); touch() }} /></Field>
          <Field label="" wide><input value={site.address2} onChange={e => { setSite(s => ({ ...s, address2: e.target.value })); touch() }} /></Field>
          <Field label="City"><input value={site.city} onChange={e => { setSite(s => ({ ...s, city: e.target.value })); touch() }} /></Field>
          <Field label="State"><input value={site.state} maxLength={2} onChange={e => { setSite(s => ({ ...s, state: e.target.value.toUpperCase() })); touch() }} style={{ width: '3rem' }} /><span className="lbl">ZIP</span><input value={site.zip} onChange={e => { setSite(s => ({ ...s, zip: e.target.value })); touch() }} style={{ width: '7rem' }} /></Field>
        </fieldset>}
        {tab === 'Notes' && <>{ta('printed', 'Printed note')}{ta('internal', 'Internal note')}{ta('spec', 'Special instructions')}{ta('wo', 'Work order instructions')}</>}
      </div>

      <table className="grid-table lines qe">
        <thead><tr><th>Type</th><th className="num">Qty</th><th>Part / description</th><th>Thick</th><th className="num">Width</th><th className="num">Height</th><th className="num">Price</th><th className="num">Amount</th><th /></tr></thead>
        <tbody>
          {rows.map((r, i) => {
            const p = priced?.[i]; const isItem = r.source === 'glass' || r.source === 'part'
            return (
              <tr key={r.key}>
                <td><select value={r.source} onChange={e => setRow(r.key, { ...blankRow(e.target.value as Source), key: r.key, qty: r.qty })}>
                  <option value="glass">Glass</option><option value="part">Part</option><option value="labor">Labor</option><option value="custom">Text / other</option></select></td>
                <td className="num"><input value={r.qty} inputMode="numeric" onChange={e => setRow(r.key, { qty: e.target.value })} style={{ width: '3.5rem' }} /></td>
                <td>{isItem
                  ? <><PartPicker source={r.source === 'glass' ? 'G' : 'P'} label={r.partLabel} onPick={pt => pickPart(r.key, pt)} /><small className="muted">{r.description}</small></>
                  : <input value={r.description} placeholder={r.source === 'labor' ? 'Labor Charge' : 'Description'} onChange={e => setRow(r.key, { description: e.target.value })} />}</td>
                <td>{isItem && r.partId != null && (r.thicks.length > 1
                  ? <select value={r.thickNum != null ? `${r.thickNum}/${r.thickDen}` : ''} onChange={e => { const [n, d] = e.target.value.split('/').map(Number); setRow(r.key, { thickNum: n, thickDen: d, price: '' }) }}>
                      <option value="">Choose</option>{r.thicks.map(t => <option key={t.text} value={`${t.num}/${t.den}`}>{t.text}</option>)}</select>
                  : <span>{r.thicks[0]?.text}</span>)}</td>
                <td className="num">{r.source === 'glass' && <input value={r.w} inputMode="decimal" placeholder="in" onChange={e => setRow(r.key, { w: e.target.value, price: '' })} style={{ width: '4.5rem' }} />}</td>
                <td className="num">{r.source === 'glass' && <input value={r.h} inputMode="decimal" placeholder="in" onChange={e => setRow(r.key, { h: e.target.value, price: '' })} style={{ width: '4.5rem' }} />}</td>
                <td className="num"><input value={r.price} inputMode="decimal" placeholder={p?.unitPrice != null && isItem ? p.unitPrice.toFixed(2) : 'auto'} title={r.price ? 'Typed price. Clear it to use the catalog price.' : 'Catalog price (type to override)'}
                  onChange={e => setRow(r.key, { price: e.target.value })} style={{ width: '5.5rem' }} className={r.price ? 'locked' : ''} /></td>
                <td className="num">{p ? money(p.amount) : ''}{p && !p.taxable && p.amount !== 0 ? ' *' : ''}{p?.warnings.map(w => <div key={w} className="warn">{w}</div>)}</td>
                <td><button className="x" title="Remove line" onClick={() => { setRows(rs => rs.length > 1 ? rs.filter(x => x.key !== r.key) : [blankRow()]); touch() }}>×</button></td>
              </tr>
            )
          })}
        </tbody>
        <tfoot>
          <tr><td colSpan={9}><button onClick={() => { setRows(rs => [...rs, blankRow()]); touch() }}>+ Add line</button>
            {initial && rows.some(r => r.price && (r.source === 'glass' || r.source === 'part')) && <button style={{ marginLeft: '.5rem' }} title="Clear the saved prices so every line is priced from the catalog again"
              onClick={() => { setRows(rs => rs.map(r => r.source === 'glass' || r.source === 'part' ? { ...r, price: '' } : r)); touch() }}>Reprice from catalog</button>}</td></tr>
          {calc && <>
            <tr><td colSpan={7} className="num">Subtotal</td><td className="num">{money(calc.subtotal)}</td><td /></tr>
            {calc.taxes.map(t => <tr key={t.code}><td colSpan={7} className="num">Tax {t.code} ({t.percent}%)</td><td className="num">{money(t.amount)}</td><td /></tr>)}
            <tr className="total"><td colSpan={7} className="num">Total</td><td className="num">{money(calc.total)}</td><td /></tr></>}
        </tfoot>
      </table>
      {customerId == null && <p className="muted">Choose a customer to see prices.</p>}
      {calcError && <p className="formerr">{calcError}</p>}
      <p className="muted small">Prices come from the catalog for the price category above: glass by the square foot (with the waste allowance for its thickness, never below the minimum), parts by the unit. Type a price to override a line; * = not taxable.</p>
      {lookup && <CustomerLookup onClose={() => setLookup(false)} onPick={async id => { setLookup(false); const c = await api.getCustomer(id); setCustomerId(id); setCustomerName(`${c.lastName}${c.firstName ? ', ' + c.firstName : ''}`); touch() }} />}
    </>
  )
}
