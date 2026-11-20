import { useCallback, useEffect, useRef, useState } from 'react'
import * as api from '../api'
import { CustomerEntry } from '../windows/CustomerEntry'
import { DocumentBrowser } from '../windows/DocumentBrowser'
import { QuoteWindow } from '../windows/QuoteWindow'
import { SettingsWindow } from '../windows/SettingsWindow'
import { TaxCodeEntry, TaxGroupEntry } from '../windows/TaxWindows'
import { Modal } from './ui'
import { ShellContext, WinIdContext, type Kind } from './WindowContext'

interface Win { id: string; kind: Kind; arg?: number; title: string }
const TITLES: Record<Kind, string> = { customer: 'Customer Entry', quote: 'Quote', documents: 'Documents', settings: 'Settings', taxcodes: 'Tax Code Entry', taxgroups: 'Tax Group Entry' }
interface MenuItem { label: string; kind?: Kind; arg?: number; action?: () => void; disabled?: boolean; note?: string }

export function Shell({ me, onSignOut, refreshMe }: { me: api.Me; onSignOut: () => void; refreshMe: () => void }) {
  const [wins, setWins] = useState<Win[]>([])
  const [active, setActive] = useState<string | null>(null)
  const [menu, setMenu] = useState<string | null>(null)
  const [about, setAbout] = useState(false)
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({})
  const seq = useRef(0)

  const open = useCallback((kind: Kind, arg?: number) => {
    // A blank quote/customer entry window can be opened repeatedly; a specific record or a tool window is reused.
    const id = arg != null ? `${kind}:${arg}` : kind === 'quote' || kind === 'customer' ? `${kind}:new:${++seq.current}` : kind
    setWins(w => w.some(x => x.id === id) ? w : [...w, { id, kind, arg, title: TITLES[kind] }])
    setActive(id)
  }, [])
  const close = useCallback((id: string) => {
    setWins(w => { const i = w.findIndex(x => x.id === id); const next = w.filter(x => x.id !== id); setActive(a => a === id ? (next[Math.max(0, i - 1)]?.id ?? null) : a); return next })
  }, [])
  const setTitle = useCallback((id: string, title: string) => setWins(w => w.map(x => x.id === id && x.title !== title ? { ...x, title } : x)), [])

  useEffect(() => {
    const k = (e: KeyboardEvent) => e.key === 'Escape' && setMenu(null)
    document.addEventListener('keydown', k); return () => document.removeEventListener('keydown', k)
  }, [])

  const menus: Record<string, MenuItem[]> = {
    File: [{ label: 'Close Window', action: () => active && close(active), disabled: !active }, { label: 'Log Out', action: onSignOut }],
    Daily: [{ label: 'Flat Glass Quote', kind: 'quote' }, { label: 'Payment', disabled: true, note: 'not built yet' }, { label: 'Documents', kind: 'documents' }],
    Maintenance: [{ label: 'Customers', kind: 'customer' }, { label: 'Tax Code Entry', kind: 'taxcodes' }, { label: 'Tax Group Entry', kind: 'taxgroups' }],
    Utilities: [{ label: 'Settings (time zone, devices)', kind: 'settings' }],
    Reports: [{ label: 'No reports yet', disabled: true }],
    Help: [{ label: 'About ReflectoPatronus', action: () => setAbout(true) }],
  }
  const run = (i: MenuItem) => { setMenu(null); if (i.disabled) return; if (i.kind) open(i.kind, i.arg); i.action?.() }

  return (
    <ShellContext.Provider value={{ me, open, close, setTitle, refreshMe }}>
      <div className="app" onMouseDown={e => { if (!(e.target as HTMLElement).closest('.menubar')) setMenu(null) }}>
        <nav className="menubar" aria-label="Main menu">
          {Object.entries(menus).map(([name, items]) => (
            <div key={name} className="menu">
              <button className={menu === name ? 'on' : ''} onClick={() => setMenu(menu === name ? null : name)} onMouseEnter={() => menu && setMenu(name)}>{name}</button>
              {menu === name && <ul>{items.map(i => <li key={i.label}><button disabled={i.disabled} onClick={() => run(i)}>{i.label}{i.note && <small> ({i.note})</small>}</button></li>)}</ul>}
            </div>
          ))}
        </nav>
        <div className="body">
          <aside className="side">
            <section>
              <h3 onClick={() => setCollapsed(c => ({ ...c, q: !c.q }))}>Quick Launch <span>{collapsed.q ? '⌄' : '⌃'}</span></h3>
              {!collapsed.q && <ul>
                <li><button onClick={() => open('quote')}><i className="ico q" />Flat Glass Quote</button></li>
                <li><button disabled title="Not built yet"><i className="ico p" />Payment</button></li>
                <li><button onClick={() => open('customer')}><i className="ico c" />Customers</button></li>
                <li><button onClick={onSignOut}><i className="ico l" />Log Out</button></li>
              </ul>}
            </section>
            <section>
              <h3 onClick={() => setCollapsed(c => ({ ...c, o: !c.o }))}>Open Windows <span>{collapsed.o ? '⌄' : '⌃'}</span></h3>
              {!collapsed.o && <ul>{wins.map(w => (
                <li key={w.id}><button className={w.id === active ? 'on' : ''} onClick={() => setActive(w.id)}>{w.title}</button></li>
              ))}</ul>}
            </section>
          </aside>
          <main className="workspace">
            {wins.length === 0 && <div className="watermark" aria-hidden="true"><b>reflecto</b>patronus</div>}
            {wins.map(w => (
              <section key={w.id} className="window" hidden={w.id !== active} aria-label={w.title}>
                <header><span>{w.title}</span><button className="x" onClick={() => close(w.id)} aria-label={`Close ${w.title}`}>×</button></header>
                <div className="window-body">
                  <WinIdContext.Provider value={w.id}>
                    {w.kind === 'customer' && <CustomerEntry initialId={w.arg} />}
                    {w.kind === 'quote' && <QuoteWindow initialId={w.arg} />}
                    {w.kind === 'documents' && <div className="form"><DocumentBrowser /></div>}
                    {w.kind === 'settings' && <SettingsWindow />}
                    {w.kind === 'taxcodes' && <TaxCodeEntry />}
                    {w.kind === 'taxgroups' && <TaxGroupEntry />}
                  </WinIdContext.Provider>
                </div>
              </section>
            ))}
          </main>
        </div>
        <footer className="statusbar"><span>{me.code}</span><span>{me.timeZone}</span><span className="spacer" /><span>Ver. 0.1.0</span></footer>
        {about && <Modal title="About" onClose={() => setAbout(false)} width={420}><div className="lookup-body"><p><strong>ReflectoPatronus</strong> 0.1.0</p><p>A modern replacement for GlassQuote 7, running on PostgreSQL.</p><p className="muted">Customers, documents and tax setup are read-only for now.</p></div></Modal>}
      </div>
    </ShellContext.Provider>
  )
}
