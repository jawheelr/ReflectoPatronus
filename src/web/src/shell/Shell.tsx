import { useCallback, useEffect, useRef, useState } from 'react'
import * as api from '../api'
import { CustomerEntry } from '../windows/CustomerEntry'
import { DocumentBrowser } from '../windows/DocumentBrowser'
import { QuoteWindow } from '../windows/QuoteWindow'
import { DatabaseTransfer } from '../windows/DatabaseTransfer'
import { SupplierPricing } from '../windows/SupplierPricing'
import { SettingsWindow } from '../windows/SettingsWindow'
import { TaxCodeEntry, TaxGroupEntry } from '../windows/TaxWindows'
import { MAINT_TABLES, MaintenanceEditor } from '../windows/MaintenanceEditor'
import { HelpWindow } from '../windows/HelpWindow'
import { Receivables } from '../windows/Receivables'
import { PrinterSetup } from '../windows/PrinterSetup'
import { CatalogEditor } from '../windows/CatalogEditor'
import { LossTable, SpecialPricing } from '../windows/PricingWindows'
import { ChangePassword, Cleanup, CompanyInfo, Groups, Users } from '../windows/AdminWindows'
import { REPORT_MENU, ReportWindow } from '../windows/ReportWindow'
import { CommandPalette } from './CommandPalette'
import { UpdateBanner, UpdateDialog, useUpdates } from './UpdateBanner'
import { Modal } from './ui'
import { ShellContext, WinIdContext, type Kind } from './WindowContext'

interface Win { id: string; kind: Kind; arg?: number; title: string; min?: boolean; max?: boolean }
interface Toast { id: number; message: string; kind: 'ok' | 'error' }
const TITLES: Record<Kind, string> = { customer: 'Customer Entry', quote: 'Quote', documents: 'Documents', settings: 'Settings', taxcodes: 'Tax Code Entry', taxgroups: 'Tax Group Entry', pricing: 'Supplier Pricing', transfer: 'Import / Export Database', maint: 'Maintenance', help: 'Knowledge Base', receivables: 'Receivables', printers: 'Printer Setup', catalog: 'Catalog', loss: 'Lossage Table', special: 'Customer Special Pricing', company: 'Company Information', password: 'Change Password', users: 'User Profiles', groups: 'Groups', cleanup: 'Delete Options', report: 'Report' }
interface MenuItem { label: string; kind?: Kind; arg?: number; action?: () => void; disabled?: boolean; note?: string; children?: MenuItem[]; sep?: boolean }
const soon = (...labels: string[]): MenuItem[] => labels.map(label => ({ label, disabled: true }))
const sep = (): MenuItem => ({ label: '', sep: true })
const rep = (...titles: string[]): MenuItem[] => titles.map(t => ({ label: t, kind: 'report' as Kind, arg: REPORT_MENU.findIndex(r => r.title === t) }))
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
  const upd = useUpdates(me); const [updOpen, setUpdOpen] = useState(false)
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
    setWins(w => w.some(x => x.id === id) ? w.map(x => x.id === id && x.min ? { ...x, min: false } : x) : [...w, { id, kind, arg, title: TITLES[kind] }])
    setActive(id)
  }, [])
  const minimize = useCallback((id: string) => {
    setWins(w => { const i = w.findIndex(x => x.id === id); const next = w.map(x => x.id === id ? { ...x, min: true } : x)
      setActive(a => a !== id ? a : ([...next.slice(0, i).reverse(), ...next.slice(i + 1)].find(x => !x.min)?.id ?? null)); return next })
  }, [])
  const toggleMax = useCallback((id: string) => setWins(w => w.map(x => x.id === id ? { ...x, max: !x.max } : x)), [])
  /** Taskbar behaviour: a minimized window comes back, the active one minimizes, another one is brought to the front. */
  const pick = (id: string) => {
    const w = wins.find(x => x.id === id); if (!w) return
    if (w.min) { setWins(ws => ws.map(x => x.id === id ? { ...x, min: false } : x)); setActive(id) }
    else if (id === active) minimize(id)
    else setActive(id)
  }
  const close = useCallback((id: string) => {
    setWins(w => { const i = w.findIndex(x => x.id === id); const next = w.filter(x => x.id !== id); setActive(a => a === id ? ([...next.slice(0, i).reverse(), ...next.slice(i)].find(x => !x.min)?.id ?? null) : a); return next })
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
  const can = (...p: string[]) => api.can(me, ...p)
  const when = (ok: boolean, items: MenuItem[]) => (ok ? items : [])
  const menus: Record<string, MenuItem[]> = {
    File: [{ label: 'Log Out', action: signOut }, { label: 'Close Window', action: () => active && close(active), disabled: !active },
      { label: 'Printer Setup…', kind: 'printers' }, { label: 'Change Password', kind: 'password' }, { label: 'User Profiles', kind: 'users', disabled: !can('users.manage') }, { label: 'Groups (roles)', kind: 'groups', disabled: !can('users.manage') }, { label: 'Appearance', children: themeItems }],
    Daily: [{ label: 'Flat Glass Quote', kind: 'quote', disabled: !can('quotes.write', 'documents.read') }, { label: 'Payment / Receivables', kind: 'receivables', disabled: !can('receivables.read') }, { label: 'Documents', kind: 'documents', disabled: !can('documents.read') }],
    Maintenance: [
      { label: 'Accounts', children: [{ label: 'Customers', kind: 'customer', disabled: !can('customers.read') }, { label: 'Customer Categories', kind: 'maint', arg: MAINT_TABLES.indexOf('customer-categories') }, { label: 'Customer Special Pricing', kind: 'special', disabled: !can('customers.read') }, ...soon('Vendor Entry')] },
      { label: 'Miscellaneous', children: [{ label: 'Price Categories', kind: 'maint', arg: MAINT_TABLES.indexOf('price-categories') }, { label: 'Lossage Table', kind: 'loss' }, ...soon('Markup Categories', 'Special Cuts Markup'), sep(), { label: 'Salesman/Installer', kind: 'maint', arg: MAINT_TABLES.indexOf('staff') }, ...soon('Advertising Codes'), { label: 'Payment Categories', kind: 'maint', arg: MAINT_TABLES.indexOf('pay-categories') }, { label: 'Status Table', kind: 'maint', arg: MAINT_TABLES.indexOf('statuses') }] },
      { label: 'Parts', children: [{ label: 'Flat Glass Types', kind: 'catalog', arg: 0 }, ...soon('Fabrications'), { label: 'Flat Glass Parts', kind: 'catalog', arg: 1 }, sep(), ...soon('Part Categories', 'Lookup Categories', 'Add-on Parts', 'GL Accts')] },
      { label: 'Taxes', children: [{ label: 'Tax Codes Entry', kind: 'maint', arg: MAINT_TABLES.indexOf('tax-codes') }, { label: 'Tax Groups Entry', kind: 'taxgroups' }] },
    ],
    Utilities: [
      { label: 'Company Information', kind: 'company', disabled: !can('company.write') }, ...soon('Enterprise Information'), { label: 'Reprint Document', kind: 'documents', disabled: !can('documents.read') },
      { label: 'Delete Options', children: [{ label: 'Delete (void) Payments', kind: 'receivables', disabled: !can('payments.void') }, { label: 'Delete (void) Deposits', kind: 'receivables', disabled: !can('payments.void') }, { label: 'Delete Inactive Customers', kind: 'cleanup', disabled: !can('utilities.cleanup') }, { label: 'Delete Quotes', kind: 'cleanup', disabled: !can('utilities.cleanup') }, { label: 'Void Invoice', kind: 'documents', disabled: !can('documents.void') }, { label: 'Void Work Order', kind: 'documents', disabled: !can('documents.void') }, { label: 'Void Credit Invoice', kind: 'documents', disabled: !can('documents.void') }, ...soon('Remove Finance Charges')] },
      { label: 'Invoice File', children: [{ label: 'Apply Credits', kind: 'receivables', disabled: !can('payments.write') }, { label: 'Edit Flat Glass Invoice', kind: 'documents', disabled: !can('invoices.write') }] },
      ...when(can('pricing.manage'), [{ label: 'Perform Cost Increase / Supplier Pricing', kind: 'pricing' as Kind }]), ...when(can('transfer.manage'), [{ label: 'Import / Export Database', kind: 'transfer' as Kind }]),
      sep(), { label: 'Settings (time zone, devices)', kind: 'settings' },
    ],
    Reports: [
      { label: 'Sales', children: [...rep('Sales Breakdown', 'Sales by Customer Category', 'Sales Detail by Agent', 'Sales by Agent', 'Tax Breakdown'), ...soon('Sales by Advertising Code', 'Sales By Ad Code By ZIP', 'Flat Glass Sales by Part Category', 'Invoice Profitability', 'Cost of Goods Sold', 'Gross Sales & Royalties')] },
      { label: 'Receivables', children: [...rep('Aged Open Invoices', 'Statements (open items)', 'List Payments', 'Payment Breakdown', 'Payments by User'), ...soon('List Finance Charges')] },
      { label: 'Accounts', children: [...rep('Print Customer List', 'List Customer Categories', 'Customer Special Pricing List', 'Customer History Report'), ...soon('Customer Address Labels', 'Bill-To Sales Detail', 'Vendors')] },
      { label: 'Salesmen/Installers', children: [...rep('List Salesman', 'List Installers', 'Salesman Totals', 'Installer Totals', 'Work Order Status'), ...soon('Labor Productivity')] },
      { label: 'File Listing', children: [...soon('List Part Categories', 'List Add-On Parts'), sep(), ...rep("List Quotes, WO's, Invoices"), ...soon('Print Work Order Labels', 'Invoice Edit Notes'), sep(),
        ...rep('List Price Categories'), ...soon('List Markup Categories', 'List Advertising Codes'), sep(), ...rep('List Flat Glass Types'), ...soon('List Fabrications'), ...rep('List Flat Glass Parts'), sep(), ...rep('List Users', 'List Companies')] },
      ...soon('Ordering'),
    ],
    Help: [{ label: 'Knowledge Base', kind: 'help' }, { label: 'Check for Updates…', action: () => setUpdOpen(true), disabled: !can('users.manage') }, { label: 'About ReflectoPatronus', action: () => setAbout(true) }],
  }
  const lock = (items: MenuItem[]): MenuItem[] => items.map(i => ({ ...i, disabled: true, children: i.children ? lock(i.children) : undefined }))
  if (!can('reports.view')) menus.Reports = lock(menus.Reports)
  const run = (i: MenuItem) => { if (i.disabled || i.children) return; setMenu(null); if (i.kind) open(i.kind, i.arg); i.action?.() }
  const commands = [{ label: 'Flat Glass Quote', kind: 'quote' as Kind }, { label: 'Customer Entry', kind: 'customer' as Kind }, { label: 'Payments / Receivables', kind: 'receivables' as Kind }, { label: 'Printer Setup', kind: 'printers' as Kind }, { label: 'Documents', kind: 'documents' as Kind },
    { label: 'Tax Code Entry', kind: 'maint' as Kind, arg: MAINT_TABLES.indexOf('tax-codes') }, { label: 'Knowledge Base (how to use)', kind: 'help' as Kind }, { label: 'Tax Group Entry', kind: 'taxgroups' as Kind }, { label: 'Settings', kind: 'settings' as Kind }, ...(me.isAdmin ? [{ label: 'Supplier Pricing', kind: 'pricing' as Kind }, { label: 'Import / Export Database', kind: 'transfer' as Kind }] : [])]

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
        <UpdateBanner s={upd.s} onDetails={() => setUpdOpen(true)} onDismiss={() => api.dismissUpdate().then(upd.reload)} />
        <div className="body">
          <aside className="side">
            <section>
              <h3 onClick={() => setCollapsed(c => ({ ...c, q: !c.q }))}>Quick Launch <span>{collapsed.q ? '⌄' : '⌃'}</span></h3>
              {!collapsed.q && <ul>
                <li><button onClick={() => open('quote')} disabled={!can('quotes.write', 'documents.read')} title="Flat Glass Quote"><i className="ico q" /><span>Flat Glass Quote</span></button></li>
                <li><button onClick={() => open('receivables')} disabled={!can('receivables.read')} title="Payments and receivables"><i className="ico p" /><span>Payment</span></button></li>
                <li><button onClick={() => open('customer')} disabled={!can('customers.read')} title="Customers"><i className="ico c" /><span>Customers</span></button></li>
                <li><button onClick={signOut} title="Log Out"><i className="ico l" /><span>Log Out</span></button></li>
              </ul>}
            </section>
            <section className="openwins">
              <h3 onClick={() => setCollapsed(c => ({ ...c, o: !c.o }))}>Open Windows <span>{collapsed.o ? '⌄' : '⌃'}</span></h3>
              {!collapsed.o && <ul>{wins.map(w => (
                <li key={w.id}><button className={(w.id === active && !w.min ? 'on ' : '') + (w.min ? 'minimized' : '')} onClick={() => pick(w.id)} title={w.min ? `${w.title} (minimized, click to restore)` : w.title}>{w.title}</button></li>
              ))}</ul>}
            </section>
          </aside>
          <main className="workspace">
            {wins.length === 0 && <div className="empty-state">
              <img className="watermark" src="/logo-lockup.svg" alt="" aria-hidden="true" />
              <p>Press <kbd>{isMac ? '⌘' : 'Ctrl'} K</kbd> to find a customer or document, or start a <button className="link" onClick={() => open('quote')}>new quote</button>.</p>
            </div>}
            {wins.map(w => (
              <section key={w.id} className={'window' + (w.max ? ' max' : '')} hidden={w.id !== active || !!w.min} aria-label={w.title}>
                <header onDoubleClick={e => { if (!(e.target as HTMLElement).closest('button')) toggleMax(w.id) }} title="Double-click to maximize or restore">
                  <span>{w.title}</span>
                  <span className="wbtns">
                    <button className="x" onClick={() => minimize(w.id)} aria-label={`Minimize ${w.title}`} title="Minimize">–</button>
                    <button className="x" onClick={() => toggleMax(w.id)} aria-label={`${w.max ? 'Restore' : 'Maximize'} ${w.title}`} title={w.max ? 'Restore' : 'Maximize'}>{w.max ? '❐' : '☐'}</button>
                    <button className="x close" onClick={() => close(w.id)} aria-label={`Close ${w.title}`} title="Close">×</button>
                  </span>
                </header>
                <div className="window-body">
                  <WinIdContext.Provider value={w.id}>
                    {w.kind === 'customer' && <CustomerEntry initialId={w.arg} />}
                    {w.kind === 'quote' && <QuoteWindow initialId={w.arg} />}
                    {w.kind === 'documents' && <div className="form"><DocumentBrowser /></div>}
                    {w.kind === 'settings' && <SettingsWindow />}
                    {w.kind === 'pricing' && <SupplierPricing />}
                    {w.kind === 'transfer' && <DatabaseTransfer />}
                    {w.kind === 'taxcodes' && <TaxCodeEntry />}
                    {w.kind === 'maint' && <MaintenanceEditor table={MAINT_TABLES[w.arg ?? 0]} />}
                    {w.kind === 'help' && <HelpWindow />}
                    {w.kind === 'receivables' && <Receivables initialCustomer={w.arg} />}
                    {w.kind === 'printers' && <PrinterSetup />}
                    {w.kind === 'catalog' && <CatalogEditor source={w.arg === 1 ? 'P' : 'G'} />}
                    {w.kind === 'loss' && <LossTable />}
                    {w.kind === 'special' && <SpecialPricing initialCustomer={w.arg} />}
                    {w.kind === 'company' && <CompanyInfo />}
                    {w.kind === 'password' && <ChangePassword />}
                    {w.kind === 'users' && <Users />}
                    {w.kind === 'groups' && <Groups />}
                    {w.kind === 'cleanup' && <Cleanup />}
                    {w.kind === 'report' && <ReportWindow index={w.arg ?? 0} />}
                    {w.kind === 'taxgroups' && <TaxGroupEntry />}
                  </WinIdContext.Provider>
                </div>
              </section>
            ))}
          </main>
        </div>
        <footer className="statusbar"><span>{me.code}</span><span>{me.timeZone}</span><span className="spacer" /><span>Ver. 0.1.0</span></footer>
        {updOpen && <UpdateDialog me={me} s={upd.s} setS={upd.setS} onClose={() => setUpdOpen(false)} />}
        {palette && <CommandPalette commands={commands} open={open} onClose={() => setPalette(false)} />}
        {about && <Modal title="About" onClose={() => setAbout(false)} width={420}><div className="lookup-body"><p><strong>ReflectoPatronus</strong> 0.1.0</p><p>A modern glass shop software, replacing the legacy system and running on PostgreSQL.</p><p>Developer: Jarred Wheeler</p><p className="muted">Documents, quotes and payments are read-only for now; customers and the maintenance tables are editable.</p></div></Modal>}
        <div className="toasts" role="status" aria-live="polite">{toasts.map(t => <div key={t.id} className={'toast ' + t.kind}>{t.message}</div>)}</div>
      </div>
    </ShellContext.Provider>
  )
}
