import { useEffect, useRef, useState } from 'react'
import * as api from '../api'
import { money, plainDate } from '../format'
import { TYPE_NAMES } from '../windows/DocumentBrowser'
import type { Kind } from './WindowContext'

export interface Command { label: string; kind: Kind; arg?: number }
type Hit = { key: string; group: string; title: string; detail?: string; run: () => void }

/** Ctrl/Cmd+K: jump to a customer, a document or a window from anywhere. */
export function CommandPalette({ commands, open, onClose }: { commands: Command[]; open: (k: Kind, arg?: number) => void; onClose: () => void }) {
  const [q, setQ] = useState('')
  const [cust, setCust] = useState<api.CustomerSummary[]>([])
  const [docs, setDocs] = useState<api.DocumentSummary[]>([])
  const [idx, setIdx] = useState(0)
  const list = useRef<HTMLUListElement>(null)

  useEffect(() => {
    const term = q.trim(); let live = true
    if (term.length < 2) { setCust([]); setDocs([]); return }
    const t = setTimeout(() => {
      api.searchCustomers(term, 0, 6).then(p => live && setCust(p.items)).catch(() => {})
      api.searchDocuments({ q: term, includeVoided: true, limit: 6 }).then(p => live && setDocs(p.items)).catch(() => {})
    }, 150)
    return () => { live = false; clearTimeout(t) }
  }, [q])

  const go = (k: Kind, arg?: number) => () => { onClose(); open(k, arg) }
  const needle = q.trim().toLowerCase()
  const hits: Hit[] = [
    ...commands.filter(c => !needle || c.label.toLowerCase().includes(needle)).map(c => ({ key: 'c' + c.label, group: 'Go to', title: c.label, run: go(c.kind, c.arg) })),
    ...cust.map(c => ({ key: 'u' + c.id, group: 'Customers', title: [c.lastName, c.firstName].filter(Boolean).join(', ') || '(no name)', detail: `${c.number}${c.city ? ' · ' + c.city : ''} · ${money(c.balance)}`, run: go('customer', c.id) })),
    ...docs.map(d => ({ key: 'd' + d.id, group: 'Documents', title: `${TYPE_NAMES[d.type] ?? d.type} ${d.number}`, detail: `${plainDate(d.date)} · ${d.customerName ?? ''} · ${money(d.total)}`, run: go('quote', d.id) })),
  ]
  useEffect(() => setIdx(0), [q, cust, docs])
  useEffect(() => { list.current?.querySelector('li.on')?.scrollIntoView({ block: 'nearest' }) }, [idx])

  function key(e: React.KeyboardEvent) {
    if (e.key === 'Escape') onClose()
    else if (e.key === 'ArrowDown') { e.preventDefault(); setIdx(i => Math.min(hits.length - 1, i + 1)) }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setIdx(i => Math.max(0, i - 1)) }
    else if (e.key === 'Enter') { e.preventDefault(); hits[idx]?.run() }
  }
  let last = ''
  return (
    <div className="backdrop palette-backdrop" onMouseDown={e => e.target === e.currentTarget && onClose()}>
      <div className="palette" role="dialog" aria-label="Search">
        <input autoFocus placeholder="Search customers, documents, or jump to a window…" value={q} onChange={e => setQ(e.target.value)} onKeyDown={key} aria-label="Search" />
        <ul ref={list}>
          {hits.map((h, i) => {
            const head = h.group !== last ? <li className="group" key={'g' + h.group}>{h.group}</li> : null
            last = h.group
            return [head, <li key={h.key} className={i === idx ? 'on' : ''} onMouseMove={() => setIdx(i)} onClick={h.run}><span>{h.title}</span>{h.detail && <small>{h.detail}</small>}</li>]
          })}
          {hits.length === 0 && <li className="none">No matches.</li>}
        </ul>
        <footer><span>↑↓ to move</span><span>Enter to open</span><span>Esc to close</span></footer>
      </div>
    </div>
  )
}
