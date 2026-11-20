import { useEffect, useState } from 'react'
import * as api from '../api'
import { Audit, Field, Modal, RO, Toolbar } from '../shell/ui'

const pct = (n: number) => `${n.toFixed(4)}%`

function Picker<T extends { code: string; description: string | null }>({ title, items, onPick, onClose }:
  { title: string; items: T[]; onPick: (i: T) => void; onClose: () => void }) {
  const [q, setQ] = useState('')
  const rows = items.filter(i => (i.code + ' ' + (i.description ?? '')).toLowerCase().includes(q.toLowerCase()))
  return (
    <Modal title={title} onClose={onClose} width={520}>
      <div className="lookup-body">
        <input autoFocus className="search" placeholder="Find" value={q} onChange={e => setQ(e.target.value)} onKeyDown={e => e.key === 'Enter' && rows[0] && onPick(rows[0])} />
        <table className="grid-table selectable"><thead><tr><th>Code</th><th>Description</th></tr></thead>
          <tbody>{rows.map(i => <tr key={i.code} onClick={() => onPick(i)}><td>{i.code}</td><td>{i.description}</td></tr>)}</tbody></table>
      </div>
    </Modal>
  )
}

export function TaxCodeEntry() {
  const [codes, setCodes] = useState<api.TaxCode[]>([]); const [sel, setSel] = useState<api.TaxCode | null>(null); const [pick, setPick] = useState(false)
  useEffect(() => { api.getTaxCodes().then(setCodes) }, [])
  return (
    <>
      <Toolbar tools={[{ label: 'Save', icon: 'save' }, { label: 'Delete', icon: 'delete' }, { label: 'Clear', icon: 'clear', onClick: () => setSel(null) }]} />
      <div className="form">
        <Field label="Tax Code"><RO value={sel?.code} width="12rem" /><button className="dots" onClick={() => setPick(true)} title="Look up a tax code">…</button></Field>
        <Field label="Description"><RO value={sel?.description} width="22rem" /></Field>
        <Field label="Percent"><RO value={sel ? pct(sel.percent) : ''} width="8rem" /></Field>
        <Field label=""><label className="radio"><input type="checkbox" disabled checked={sel?.taxesLabor ?? false} /> Labor Taxable</label>
          <label className="radio"><input type="checkbox" disabled checked={sel?.nonCalc ?? false} /> Non-Calculative</label></Field>
      </div>
      <Audit />
      {pick && <Picker title="Lookup Tax Codes" items={codes} onClose={() => setPick(false)} onPick={c => { setSel(c); setPick(false) }} />}
    </>
  )
}

export function TaxGroupEntry() {
  const [groups, setGroups] = useState<api.TaxGroup[]>([]); const [sel, setSel] = useState<api.TaxGroup | null>(null); const [pick, setPick] = useState(false)
  useEffect(() => { api.getTaxGroups().then(setGroups) }, [])
  return (
    <>
      <Toolbar tools={[{ label: 'Save', icon: 'save' }, { label: 'Delete', icon: 'delete' }, { label: 'Clear', icon: 'clear', onClick: () => setSel(null) }]} />
      <div className="form">
        <Field label="Tax Group"><RO value={sel?.code} width="12rem" /><button className="dots" onClick={() => setPick(true)} title="Look up a tax group">…</button></Field>
        <Field label="Description"><RO value={sel?.description} width="22rem" /></Field>
        {[1, 2, 3, 4, 5].map(n => {
          const c = sel?.codes.find(x => x.position === n)
          return <Field key={n} label={`Tax Code ${n}`}><RO value={c?.code} width="10rem" /><RO value={c?.description} width="16rem" /><RO value={c ? pct(c.percent) : ''} width="8rem" /></Field>
        })}
        {sel && <p className="muted">Combined rate: {pct(sel.codes.reduce((a, c) => a + c.percent, 0))}</p>}
      </div>
      <Audit />
      {pick && <Picker title="Lookup Tax Groups" items={groups} onClose={() => setPick(false)} onPick={g => { setSel(g); setPick(false) }} />}
    </>
  )
}
