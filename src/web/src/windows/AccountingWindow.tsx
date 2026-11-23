import { useCallback, useEffect, useState } from 'react'
import * as api from '../api'
import { instant, money } from '../format'
import { Tabs } from '../shell/ui'
import { useShell } from '../shell/WindowContext'

const TABS = ['QuickBooks Desktop', 'Waiting & sent', 'Files (IIF / Sage 50)', 'Names & accounts']
const today = () => new Date().toISOString().slice(0, 10)
const monthStart = () => today().slice(0, 8) + '01'

export function AccountingWindow() {
  const { me, toast } = useShell()
  const [tab, setTab] = useState(TABS[0])
  const [o, setO] = useState<api.AccountingOverview | null>(null)
  const load = useCallback(() => api.getAccounting().then(setO).catch(e => toast((e as Error).message, 'error')), [toast])
  useEffect(() => { load(); const t = setInterval(load, 15000); return () => clearInterval(t) }, [load])
  if (!api.can(me, 'accounting.manage')) return <div className="form"><p>You do not have permission to use the accounting tools.</p></div>
  if (!o) return <div className="form"><p>Loading…</p></div>
  return (
    <div className="form acct">
      <Tabs tabs={TABS} active={tab} onChange={setTab} />
      {tab === TABS[0] && <Connect o={o} reload={load} />}
      {tab === TABS[1] && <Queue o={o} reload={load} />}
      {tab === TABS[2] && <Files o={o} />}
      {tab === TABS[3] && <Names o={o} reload={load} />}
    </div>
  )
}

function Connect({ o, reload }: { o: api.AccountingOverview; reload: () => void }) {
  const { toast } = useShell()
  const c = o.config; const on = c.target === 'qbd'
  const [url, setUrl] = useState(c.publicUrl ?? window.location.origin)
  const [every, setEvery] = useState(String(o.effective.every))
  const [since, setSince] = useState(c.since ?? '')
  const [shown, setShown] = useState(false)
  const isHttp = /^http:\/\//i.test(url) && !/^http:\/\/(localhost|127\.0\.0\.1)/i.test(url)
  const save = async (patch: Partial<api.AccountingConfig>) => {
    try { await api.saveAccounting({ ...c, publicUrl: url, runEveryMinutes: Number(every) || null, since: since || null, ...patch }); toast('Saved.'); reload() } catch (e) { toast((e as Error).message, 'error') }
  }
  return (
    <>
      <h3>Send to QuickBooks Desktop automatically</h3>
      <p>Turn this on and every new invoice, credit memo and payment — plus the customer they belong to — is sent to QuickBooks without anyone doing anything. QuickBooks Desktop cannot be reached from outside, so a small Intuit program that comes with it, the <strong>QuickBooks Web Connector</strong>, asks this server every few minutes whether anything is waiting.</p>
      <label className="check"><input type="checkbox" checked={on} onChange={e => save({ target: e.target.checked ? 'qbd' : 'off' })} /> Send paperwork and payments to QuickBooks Desktop</label>
      <div className="row">
        <label>Only send things dated on or after <input type="date" value={since} onChange={e => setSince(e.target.value)} onBlur={() => on && save({})} /></label>
        <small className="muted">Left empty, it is the day you turn this on, so old paperwork is never sent by accident.</small>
      </div>
      <p className="muted">{on ? <>Waiting: <strong>{o.pending.documents}</strong> invoices and credit memos, <strong>{o.pending.payments}</strong> payments{o.pending.voids ? <>, <strong>{o.pending.voids}</strong> voids</> : null}. Sent so far: <strong>{o.sent}</strong>. Problems: <strong>{o.errors}</strong>.</> : 'Sending is off.'}</p>
      <p>{o.last ? <>Last contact from QuickBooks: <strong>{instant(o.last.at, 'UTC')}</strong>{o.last.message ? ` — ${o.last.message}` : ''}{o.last.errors ? ` (${o.last.errors} problem${o.last.errors > 1 ? 's' : ''})` : ''}.</> : <span className="muted">QuickBooks has not connected yet.</span>}</p>

      <h3>Connect the Web Connector (once)</h3>
      <ol className="steps">
        <li>On the computer that runs QuickBooks, open <em>Start → QuickBooks Web Connector</em> (it is installed with QuickBooks; Intuit offers a free download otherwise).</li>
        <li>Enter the address people use to reach ReflectoPatronus:
          <div className="row"><input style={{ width: '22rem' }} value={url} onChange={e => setUrl(e.target.value)} onBlur={() => save({})} placeholder="https://glass.example.com" /></div>
          {isHttp && <p className="warn">The Web Connector only accepts <strong>https</strong> addresses (or this computer, <code>localhost</code>). Put ReflectoPatronus behind your HTTPS gateway, or use the file export instead.</p>}
        </li>
        <li><button onClick={() => api.downloadAccounting(`/accounting/qbwc.qwc?url=${encodeURIComponent(url)}`, 'ReflectoPatronus.qwc').catch(e => toast((e as Error).message, 'error'))}>Download the .qwc file</button> and load it in the Web Connector (<em>Add an application</em>). In QuickBooks, say <em>Yes, always</em> to the access request.</li>
        <li>When the Web Connector asks for a password, type this one (user <code>{o.effective.user}</code>):
          <div className="row"><code className="secret">{shown ? c.qbwcPassword : '••••••••••••••••••••••••'}</code>
            <button onClick={() => setShown(!shown)}>{shown ? 'Hide' : 'Show'}</button>
            <button onClick={async () => { if (confirm('Make a new password? The Web Connector will need it typed again.')) { await api.newAccountingPassword(); reload(); setShown(true) } }}>New password</button></div></li>
        <li>Tick the application in the Web Connector and set <em>Auto-Run</em> to <input style={{ width: '4rem' }} value={every} onChange={e => setEvery(e.target.value)} onBlur={() => on && save({})} /> minutes. QuickBooks must be open with your company file for it to run.</li>
      </ol>
      <p className="muted">QuickBooks must have <em>Sales Tax</em> turned on (Edit → Preferences → Sales Tax) if you charge tax. ReflectoPatronus creates the income account, the Glass / Parts / Labor / Other items and the sales tax items for itself the first time; names can be changed under “Names &amp; accounts”.</p>
    </>
  )
}

function Queue({ o, reload }: { o: api.AccountingOverview; reload: () => void }) {
  const { toast } = useShell()
  const [status, setStatus] = useState<'pending' | 'error' | 'sent' | 'skipped'>('pending')
  const [rows, setRows] = useState<api.AccountingRow[] | null>(null)
  const load = useCallback(() => { setRows(null); api.getAccountingQueue(status).then(setRows).catch(e => toast((e as Error).message, 'error')) }, [status, toast])
  useEffect(load, [load])
  const act = async (f: () => Promise<unknown>, msg: string) => { try { await f(); toast(msg); load(); reload() } catch (e) { toast((e as Error).message, 'error') } }
  const label: Record<string, string> = { pending: `Waiting (${o.pending.documents + o.pending.payments})`, error: `Problems (${o.errors})`, sent: `Sent (${o.sent})`, skipped: `Skipped (${o.skipped})` }
  return (
    <>
      <div className="row">{(['pending', 'error', 'sent', 'skipped'] as const).map(s => <button key={s} className={status === s ? 'on' : ''} onClick={() => setStatus(s)}>{label[s]}</button>)}
        {(status === 'error' || status === 'skipped') && rows && rows.length > 0 && <button onClick={() => act(() => api.retryAccounting(), 'They will be sent again.')}>Send all again</button>}</div>
      <table className="grid-table">
        <thead><tr><th>What</th><th>Customer</th><th>Date</th><th className="num">Amount</th><th>Note</th><th /></tr></thead>
        <tbody>
          {rows === null && <tr><td colSpan={6}>Loading…</td></tr>}
          {rows?.length === 0 && <tr><td colSpan={6} className="muted">Nothing here.</td></tr>}
          {rows?.map(r => (
            <tr key={r.kind + r.refId}>
              <td>{r.label}</td><td>{r.party}</td><td>{r.date ? r.date.slice(0, 10) : ''}</td><td className="num">{r.amount == null ? '' : money(r.amount)}</td>
              <td className={r.status === 'error' ? 'warn' : 'muted'}>{r.note}</td>
              <td>
                {r.status === 'pending' && <button onClick={() => act(() => api.skipAccounting(r.kind, r.refId), 'It will not be sent.')}>Don’t send</button>}
                {(r.status === 'error' || r.status === 'skipped') && <button onClick={() => act(() => api.retryAccounting(r.kind, r.refId), 'It will be sent again.')}>Send again</button>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <p className="muted">Invoices are sent once. If one is changed after it was sent, change it in QuickBooks too. Voiding an invoice or payment here voids it in QuickBooks on the next run.</p>
    </>
  )
}

function Files({ o }: { o: api.AccountingOverview }) {
  const { toast } = useShell()
  const [from, setFrom] = useState(monthStart()); const [to, setTo] = useState(today()); const [again, setAgain] = useState(false); const [mark, setMark] = useState(true)
  const get = (fmt: string, name: string) => api.downloadAccounting(`/accounting/export/${fmt}?from=${from}&to=${to}&includeSent=${again}&mark=${mark}`, name).then(() => toast('File saved.')).catch(e => toast((e as Error).message, 'error'))
  return (
    <>
      <h3>Files for any accounting program</h3>
      <p>Use these when the automatic QuickBooks connection is not possible, or for Sage 50 (Peachtree), which has no connection to send to. Each file holds everything in the date range that has not been exported to that program before.</p>
      <div className="row">
        <label>From <input type="date" value={from} onChange={e => setFrom(e.target.value)} /></label>
        <label>To <input type="date" value={to} onChange={e => setTo(e.target.value)} /></label>
        <label className="check"><input type="checkbox" checked={again} onChange={e => setAgain(e.target.checked)} /> Include what was exported before</label>
        <label className="check"><input type="checkbox" checked={mark} onChange={e => setMark(e.target.checked)} /> Remember what I export</label>
      </div>
      <h4>QuickBooks Desktop (IIF)</h4>
      <p className="muted">Invoices, credit memos, payments and customers in one file. In QuickBooks: File → Utilities → Import → IIF Files. Account names used: A/R “{o.effective.ar}”, income “{o.effective.income}”, sales tax “{o.effective.taxPayable}”, deposits “{o.effective.undeposited}” — they must exist in QuickBooks (change them under “Names &amp; accounts”).</p>
      <div className="row"><button onClick={() => get('iif', 'reflectopatronus-quickbooks.iif')}>Download IIF file</button></div>
      <h4>Sage 50 / Peachtree (CSV)</h4>
      <p className="muted">Import each file in Sage 50 with File → Select Import/Export (Sales/Invoices, Cash Receipts, Customer List). The first time, match the columns to Sage’s fields; Sage remembers the layout after that. Import the customers first.</p>
      <div className="row">
        <button onClick={() => get('sage50-customers', 'customers.csv')}>Customers</button>
        <button onClick={() => get('sage50-invoices', 'invoices.csv')}>Invoices and credit memos</button>
        <button onClick={() => get('sage50-receipts', 'receipts.csv')}>Payments received</button>
      </div>
      <p className="muted">Voids are not carried in files: if something was voided after you exported it, void it in the accounting program too.</p>
    </>
  )
}

function Names({ o, reload }: { o: api.AccountingOverview; reload: () => void }) {
  const { toast } = useShell()
  const [c, setC] = useState<api.AccountingConfig>(o.config)
  const e = o.effective
  const set = (k: keyof api.AccountingConfig, v: string) => setC(x => ({ ...x, [k]: v }))
  const item = (k: string, v: string) => setC(x => ({ ...x, items: { ...x.items, [k]: v } }))
  const f = (label: string, k: keyof api.AccountingConfig, ph: string, hint?: string) => (
    <label className="field"><span>{label}</span><input value={(c[k] as string | null | undefined) ?? ''} placeholder={ph} onChange={ev => set(k, ev.target.value)} />{hint && <small className="muted">{hint}</small>}</label>
  )
  return (
    <>
      <h3>Names and accounts</h3>
      <p>Every box here is optional. Leave one empty and the name in grey is used; QuickBooks creates what it needs by itself.</p>
      <div className="grid2">
        {f('Income account', 'incomeAccount', e.income, 'Where sales are posted.')}
        {f('Sales tax agency', 'taxAgency', e.agency, 'The vendor that sales tax is owed to.')}
        {f('Terms (invoices)', 'terms', 'none', 'For example Net 30; it must exist in QuickBooks.')}
        {f('Deposit payments to', 'depositAccount', e.undeposited)}
        <label className="field"><span>Customer names in QuickBooks</span>
          <select value={c.customerNaming ?? ''} onChange={ev => set('customerNaming', ev.target.value)}>
            <option value="">Name and account number — Smith, John (C1042)</option>
            <option value="name">Name only — Smith, John</option>
            <option value="number">Account number only — C1042</option></select>
          <small className="muted">“Name only” merges customers who share a name.</small></label>
        <label className="check"><input type="checkbox" checked={c.sendPaymentMethod} onChange={ev => setC(x => ({ ...x, sendPaymentMethod: ev.target.checked }))} /> Send the payment category as the QuickBooks payment method (it must exist there)</label>
      </div>
      <h4>Items on invoices</h4>
      <div className="grid2">
        {(['glass', 'part', 'labor', 'other'] as const).map(k => (
          <label className="field" key={k}><span>{{ glass: 'Glass', part: 'Parts', labor: 'Labor', other: 'Other / notes' }[k]}</span><input value={c.items[k] ?? ''} placeholder={{ glass: 'Glass', part: 'Parts', labor: 'Labor', other: 'Other' }[k]} onChange={ev => item(k, ev.target.value)} /></label>
        ))}
      </div>
      <h4>For the IIF file only</h4>
      <div className="grid2">
        {f('Accounts receivable', 'arAccount', e.ar)}
        {f('Sales tax payable', 'taxPayableAccount', e.taxPayable)}
        {f('Undeposited funds', 'undepositedAccount', e.undeposited)}
      </div>
      <div className="row"><button className="primary" onClick={async () => { try { await api.saveAccounting(c); toast('Saved.'); reload() } catch (er) { toast((er as Error).message, 'error') } }}>Save</button></div>
    </>
  )
}
