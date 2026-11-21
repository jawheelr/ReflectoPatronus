import { useCallback, useEffect, useRef, useState } from 'react'
import * as api from '../api'
import { CustomerEntry } from '../windows/CustomerEntry'
import { DocumentBrowser } from '../windows/DocumentBrowser'
import { QuoteWindow } from '../windows/QuoteWindow'
import { DatabaseTransfer } from '../windows/DatabaseTransfer'
import { SupplierPricing } from '../windows/SupplierPricing'
import { SettingsWindow } from '../windows/SettingsWindow'
import { TaxCodeEntry, TaxGroupEntry } from '../windows/TaxWindows'
import { CommandPalette } from './CommandPalette'
import { Modal } from './ui'
import { ShellContext, WinIdContext, type Kind } from './WindowContext'

interface Win { id: string; kind: Kind; arg?: number; title: string }
interface Toast { id: number; message: string; kind: 'ok' | 'error' }
const TITLES: Record<Kind, string> = { customer: 'Customer Entry', quote: 'Quote', documents: 'Documents', settings: 'Settings', taxcodes: 'Tax Code Entry', taxgroups: 'Tax Group Entry', pricing: 'Supplier Pricing', transfer: 'Import / Export Database' }
interface MenuItem { label: string; kind?: Kind; arg?: number; action?: () => void; disabled?: boolean; note?: string; children?: MenuItem[]; sep?: boolean }
const soon = (...labels: string[]): MenuItem[] => labels.map(label => ({ label, disabled: true }))
const sep = (): MenuItem => ({ label: '', sep: true })
const SAVED = 'rp.windows'
const isMac = /Mac|iPhone|iPad/.test(navigator.platform)

/** Open windows survive a page reload (kept per browser tab; cleared on sign-out). */
function restore(): { wins: Win[]; active: string | null } {
  try { const s = JSON.parse(sessionStorage.getItem(SAVED) ?? 'null'); if (s && Array.isArray(s.wins)) return s } catch { /* ignore */ }
  return { wins: [], active: null }
}

function MenuList({ items, run }: { items: MenuItem[]; run: (i: MenuItem) => void }) {
  return <ul>{items.map((i, n) => i.sep ? <li key={n} className="sep" role="separator" /> : (
    <li key={i.label} className={i.children ? 'sub' : ''}>
      <button disabled={i.disabled} onClick={() => run(i)}>{i.label}{i.children ? <span className="arrow">▸</span> : i.note ? <small> {i.note}</small> : null}</button>
      {i.children && <MenuList items={i.children} run={run} />}
    </li>))}</ul>
}

export function Shell({ me, onSignOut, refreshMe }: { me: api.Me; onSignOut: () => void; refreshMe: () => void }) {
  const saved = useRef(restore()).current
  const [wins, setWins] = useState<Win[]>(saved.wins)
  const [active, setActive] = useState<string | null>(saved.active)
  const [menu, setMenu] = useState<string | null>(null)
  const [about, setAbout] = useState(false)
  type Theme = 'system' | 'light' | 'dark'
  const [theme, setThemeState] = useState<Theme>(() => (localStorage.getItem('rp.theme') as Theme) || 'system')
  const setTheme = (t: Theme) => {
    setThemeState(t)
    if (t === 'system') { localStorage.removeItem('rp.theme'); delete document.documentElement.dataset.theme }
    else { localStorage.setItem('rp.theme', t); document.documentElement.dataset.theme = t }
  }
  const sysDark = window.matchMedia('(prefers-color-scheme: dark)').matches
  const isDark = theme === 'dark' || (theme === 'system' && sysDark)
  const [palette, setPalette] = useState(false)
  const [toasts, setToasts] = useState<Toast[]>([])
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({})
  const seq = useRef(1000)

  useEffect(() => { sessionStorage.setItem(SAVED, JSON.stringify({ wins, active })) }, [wins, active])

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
  const toast = useCallback((message: string, kind: 'ok' | 'error' = 'ok') => {
    const id = Date.now() + Math.random()
    setToasts(t => [...t, { id, message, kind }])
    setTimeout(() => setToasts(t => t.filter(x => x.id !== id)), kind === 'error' ? 6000 : 3000)
  }, [])
  const signOut = () => { sessionStorage.removeItem(SAVED); onSignOut() }

  useEffect(() => {
    const k = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setMenu(null)
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); setPalette(p => !p) }
    }
    document.addEventListener('keydown', k); return () => document.removeEventListener('keydown', k)
  }, [])

  const themeItems: MenuItem[] = [
    { label: (theme === 'light' ? '✓ ' : '') + 'Light', action: () => setTheme('light') },
    { label: (theme === 'dark' ? '✓ ' : '') + 'Dark', action: () => setTheme('dark') },
    { label: (theme === 'system' ? '✓ ' : '') + 'Match system', action: () => setTheme('system') },
  ]
  const admin = (items: MenuItem[]) => (me.isAdmin ? items : [])
  const menus: Record<string, MenuItem[]> = {
    File: [{ label: 'Log Out', action: signOut }, { label: 'Close Window', action: () => active && close(active), disabled: !active },
      ...soon('Change Password', 'User Profiles'), { label: 'Appearance', children: themeItems }],
    Daily: [{ label: 'Flat Glass Quote', kind: 'quote' }, ...soon('Payment'), { label: 'Documents', kind: 'documents' }],
    Maintenance: [
      { label: 'Accounts', children: [{ label: 'Customers', kind: 'customer' }, ...soon('Customer Categories', 'Customer Special Pricing', 'Vendor Entry')] },
      { label: 'Miscellaneous', children: [...soon('Price Categories', 'Lossage Table', 'Markup Categories', 'Special Cuts Markup'), sep(), ...soon('Salesman/Installer', 'Advertising Codes', 'Payment Categories', 'Status Table')] },
      { label: 'Parts', children: [...soon('Flat Glass Types', 'Fabrications', 'Flat Glass Parts'), sep(), ...soon('Part Categories', 'Lookup Categories', 'Add-on Parts', 'GL Accts')] },
      { label: 'Taxes', children: [{ label: 'Tax Codes Entry', kind: 'taxcodes' }, { label: 'Tax Groups Entry', kind: 'taxgroups' }] },
    ],
    Utilities: [
      ...soon('Company Information', 'Enterprise Information', 'Reprint Document'),
      { label: 'Delete Options', children: soon('Delete Payments', 'Delete Deposits', 'Delete Inactive Customers', 'Delete Quotes', 'Void Invoice', 'Void Work Order', 'Void Credit Invoice', 'Remove Finance Charges') },
      { label: 'Invoice File', children: soon('Apply Credits', 'Edit Flat Glass Invoice') },
      ...admin([{ label: 'Perform Cost Increase / Supplier Pricing', kind: 'pricing' as Kind }, { label: 'Import / Export Database', kind: 'transfer' as Kind }]),
      sep(), { label: 'Settings (time zone, devices)', kind: 'settings' },
    ],
    Reports: [
      { label: 'Sales', children: soon('Sales Breakdown', 'Sales by Customer Category', 'Sales Detail by Agent', 'Sales by Agent', 'Tax Breakdown', 'Sales by Advertising Code', 'Sales By Ad Code By ZIP', 'Flat Glass Sales by Part Category', 'Invoice Profitability', 'Cost of Goods Sold', 'Gross Sales & Royalties') },
      { label: 'Receivables', children: soon('Aged Open Invoices', 'Statements', 'List Finance Charges', 'List Payments', 'Payment Breakdown', 'Payments by User') },
      { label: 'Accounts', children: soon('Print Customer List', 'List Customer Categories', 'Customer Special Pricing List', 'Customer Address Labels', 'Customer History Report', 'Bill-To Sales Detail', 'Vendors') },
      { label: 'Salesmen/Installers', children: soon('List Salesman', 'List Installers', 'Salesman Totals', 'Installer Totals', 'Labor Productivity', 'Work Order Status') },
      { label: 'File Listing', children: [...soon('List Part Categories', 'List Add-On Parts'), sep(), ...soon('List Quotes, WO\'s, Invoices', 'Print Work Order Labels', 'Invoice Edit Notes'), sep(),
        ...soon('List Price Categories', 'List Markup Categories', 'List Advertising Codes'), sep(), ...soon('List Flat Glass Types', 'List Fabrications', 'List Flat Glass Parts'), sep(), ...soon('List Users', 'List Companies')] },
      ...soon('Ordering'),
    ],
    Help: [...soon('Online Help'), { label: 'About ReflectoPatronus', action: () => setAbout(true) }],
  }
  const run = (i: MenuItem) => { if (i.disabled || i.children) return; setMenu(null); if (i.kind) open(i.kind, i.arg); i.action?.() }
  const commands = [{ label: 'Flat Glass Quote', kind: 'quote' as Kind }, { label: 'Customer Entry', kind: 'customer' as Kind }, { label: 'Documents', kind: 'documents' as Kind },
    { label: 'Tax Code Entry', kind: 'taxcodes' as Kind }, { label: 'Tax Group Entry', kind: 'taxgroups' as Kind }, { label: 'Settings', kind: 'settings' as Kind }, ...(me.isAdmin ? [{ label: 'Supplier Pricing', kind: 'pricing' as Kind }, { label: 'Import / Export Database', kind: 'transfer' as Kind }] : [])]

  return (
    <ShellContext.Provider value={{ me, open, close, setTitle, refreshMe, toast }}>
      <div className="app" onMouseDown={e => { if (!(e.target as HTMLElement).closest('.menubar')) setMenu(null) }}>
        <nav className="menubar" aria-label="Main menu">
          {Object.entries(menus).map(([name, items]) => (
            <div key={name} className="menu">
              <button className={menu === name ? 'on' : ''} onClick={() => setMenu(menu === name ? null : name)} onMouseEnter={() => menu && setMenu(name)}>{name}</button>
              {menu === name && <MenuList items={items} run={run} />}
            </div>
          ))}
          <span className="spacer" />
          <button className="themebtn" onClick={() => setTheme(isDark ? 'light' : 'dark')} aria-label={isDark ? 'Switch to light theme' : 'Switch to dark theme'} title={isDark ? 'Light theme' : 'Dark theme'}>{isDark ? '☀' : '☾'}</button>
          <button className="searchbtn" onClick={() => setPalette(true)} aria-label="Search"><span>Search…</span><kbd>{isMac ? '⌘' : 'Ctrl'} K</kbd></button>
        </nav>
        <div className="body">
          <aside className="side">
            <section>
              <h3 onClick={() => setCollapsed(c => ({ ...c, q: !c.q }))}>Quick Launch <span>{collapsed.q ? '⌄' : '⌃'}</span></h3>
              {!collapsed.q && <ul>
                <li><button onClick={() => open('quote')} title="Flat Glass Quote"><i className="ico q" /><span>Flat Glass Quote</span></button></li>
                <li><button disabled title="Payment (not built yet)"><i className="ico p" /><span>Payment</span></button></li>
                <li><button onClick={() => open('customer')} title="Customers"><i className="ico c" /><span>Customers</span></button></li>
                <li><button onClick={signOut} title="Log Out"><i className="ico l" /><span>Log Out</span></button></li>
              </ul>}
            </section>
            <section className="openwins">
              <h3 onClick={() => setCollapsed(c => ({ ...c, o: !c.o }))}>Open Windows <span>{collapsed.o ? '⌄' : '⌃'}</span></h3>
              {!collapsed.o && <ul>{wins.map(w => (
                <li key={w.id}><button className={w.id === active ? 'on' : ''} onClick={() => setActive(w.id)} title={w.title}>{w.title}</button></li>
              ))}</ul>}
            </section>
          </aside>
          <main className="workspace">
            {wins.length === 0 && <div className="empty-state">
              <img className="watermark" src="/logo-lockup.svg" alt="" aria-hidden="true" />
              <p>Press <kbd>{isMac ? '⌘' : 'Ctrl'} K</kbd> to find a customer or document, or start a <button className="link" onClick={() => open('quote')}>new quote</button>.</p>
            </div>}
            {wins.map(w => (
              <section key={w.id} className="window" hidden={w.id !== active} aria-label={w.title}>
                <header><span>{w.title}</span><button className="x" onClick={() => close(w.id)} aria-label={`Close ${w.title}`}>×</button></header>
                <div className="window-body">
                  <WinIdContext.Provider value={w.id}>
                    {w.kind === 'customer' && <CustomerEntry initialId={w.arg} />}
                    {w.kind === 'quote' && <QuoteWindow initialId={w.arg} />}
                    {w.kind === 'documents' && <div className="form"><DocumentBrowser /></div>}
                    {w.kind === 'settings' && <SettingsWindow />}
                    {w.kind === 'pricing' && <SupplierPricing />}
                    {w.kind === 'transfer' && <DatabaseTransfer />}
                    {w.kind === 'taxcodes' && <TaxCodeEntry />}
                    {w.kind === 'taxgroups' && <TaxGroupEntry />}
                  </WinIdContext.Provider>
                </div>
              </section>
            ))}
          </main>
        </div>
        <footer className="statusbar"><span>{me.code}</span><span>{me.timeZone}</span><span className="spacer" /><span>Ver. 0.1.0</span></footer>
        {palette && <CommandPalette commands={commands} open={open} onClose={() => setPalette(false)} />}
        {about && <Modal title="About" onClose={() => setAbout(false)} width={420}><div className="lookup-body"><p><strong>ReflectoPatronus</strong> 0.1.0</p><p>A modern glass shop software, replacing the legacy system and running on PostgreSQL.</p><p>Developer: Jarred Wheeler</p><p className="muted">Customers, documents and tax setup are read-only for now.</p></div></Modal>}
        <div className="toasts" role="status" aria-live="polite">{toasts.map(t => <div key={t.id} className={'toast ' + t.kind}>{t.message}</div>)}</div>
      </div>
    </ShellContext.Provider>
  )
}
