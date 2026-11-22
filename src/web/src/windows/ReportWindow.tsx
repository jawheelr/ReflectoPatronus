import { useEffect, useMemo, useState } from 'react'
import * as api from '../api'
import { money, plainDate } from '../format'
import { esc } from '../print/engine'
import { PrintDialog } from '../print/PrintDialog'
import { useShell, useWinId } from '../shell/WindowContext'
import { Field, Toolbar } from '../shell/ui'
import { CustomerLookup } from './CustomerLookup'

/** The reports offered in the Reports menu (the window argument is the index here). Ids match the server's report registry. */
export const REPORT_MENU: { id: string; title: string; group: string }[] = [
  { id: 'sales-breakdown', title: 'Sales Breakdown', group: 'Sales' }, { id: 'sales-by-category', title: 'Sales by Customer Category', group: 'Sales' },
  { id: 'sales-by-agent', title: 'Sales by Agent', group: 'Sales' }, { id: 'sales-by-agent', title: 'Salesman Totals', group: 'Salesmen/Installers' }, { id: 'sales-detail-by-agent', title: 'Sales Detail by Agent', group: 'Sales' }, { id: 'tax-breakdown', title: 'Tax Breakdown', group: 'Sales' },
  { id: 'aged-open-invoices', title: 'Aged Open Invoices', group: 'Receivables' }, { id: 'open-items', title: 'Statements (open items)', group: 'Receivables' },
  { id: 'list-payments', title: 'List Payments', group: 'Receivables' }, { id: 'payment-breakdown', title: 'Payment Breakdown', group: 'Receivables' }, { id: 'payments-by-user', title: 'Payments by User', group: 'Receivables' },
  { id: 'customer-list', title: 'Print Customer List', group: 'Accounts' }, { id: 'customer-categories', title: 'List Customer Categories', group: 'Accounts' },
  { id: 'special-pricing-list', title: 'Customer Special Pricing List', group: 'Accounts' }, { id: 'customer-history', title: 'Customer History Report', group: 'Accounts' },
  { id: 'list-salesmen', title: 'List Salesman', group: 'Salesmen/Installers' }, { id: 'list-installers', title: 'List Installers', group: 'Salesmen/Installers' },
  { id: 'installer-totals', title: 'Installer Totals', group: 'Salesmen/Installers' }, { id: 'work-order-status', title: 'Work Order Status', group: 'Salesmen/Installers' },
  { id: 'list-documents', title: "List Quotes, WO's, Invoices", group: 'File Listing' }, { id: 'list-price-categories', title: 'List Price Categories', group: 'File Listing' },
  { id: 'list-glass-types', title: 'List Flat Glass Types', group: 'File Listing' }, { id: 'list-parts', title: 'List Flat Glass Parts', group: 'File Listing' },
  { id: 'list-users', title: 'List Users', group: 'File Listing' }, { id: 'list-companies', title: 'List Companies', group: 'File Listing' },
]

type Cell = string | number | null
const fmt = (c: api.ReportColumn, v: Cell): string => {
  if (v == null || v === '') return ''
  switch (c.type) {
    case 'money': return money(Number(v))
    case 'date': return plainDate(String(v).slice(0, 10))
    case 'int': return Number(v).toLocaleString('en-US')
    case 'number': return Number(v).toLocaleString('en-US', { maximumFractionDigits: 2 })
    default: return String(v)
  }
}
const csvCell = (s: string) => /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s

export function ReportWindow({ index }: { index: number }) {
  const { me, setTitle } = useShell(), winId = useWinId()
  const entry = REPORT_MENU[index] ?? REPORT_MENU[0]
  const [def, setDef] = useState<api.ReportDef | null>(null)
  const today = useMemo(() => new Date().toLocaleDateString('en-CA', { timeZone: me.companyTimeZone }), [me.companyTimeZone])
  const [from, setFrom] = useState(today.slice(0, 4) + '-01-01'); const [to, setTo] = useState(today); const [asOf, setAsOf] = useState(today)
  const [cust, setCust] = useState<{ id: number; name: string } | null>(null); const [type, setType] = useState(''); const [pick, setPick] = useState(false)
  const [res, setRes] = useState<api.ReportResult | null>(null); const [busy, setBusy] = useState(false); const [error, setError] = useState(''); const [printing, setPrinting] = useState(false)

  useEffect(() => { setTitle(winId, entry.title); api.getReportDefs().then(d => setDef(d.find(x => x.id === entry.id) ?? null)).catch(e => setError(e.message)) }, [entry.id]) // eslint-disable-line react-hooks/exhaustive-deps
  const has = (p: string) => def?.params.includes(p)
  async function run() {
    if (has('customer') && !cust) return setError('Choose a customer first.')
    setBusy(true); setError('')
    try { setRes(await api.runReport(entry.id, { from: has('range') ? from : undefined, to: has('range') ? to : undefined, asOf: has('asof') ? asOf : undefined, customerId: cust?.id, type: type || undefined })) }
    catch (e) { setError((e as Error).message); setRes(null) } finally { setBusy(false) }
  }
  useEffect(() => { if (def && def.params.filter(p => p !== 'range' && p !== 'asof' && p !== 'doctype').length === 0) void run() }, [def]) // eslint-disable-line react-hooks/exhaustive-deps

  const totals = res?.columns.map((c, i) => c.total ? res.rows.reduce((s, r) => s + (Number(r[i]) || 0), 0) : null)
  const subtitle = [has('range') && `${plainDate(from)} to ${plainDate(to)}`, has('asof') && `as of ${plainDate(asOf)}`, cust && cust.name].filter(Boolean).join(' · ')
  function csv() {
    if (!res) return
    const lines = [res.columns.map(c => csvCell(c.label)).join(','), ...res.rows.map(r => res.columns.map((c, i) => csvCell(c.type === 'money' || c.type === 'number' || c.type === 'int' ? (r[i] == null ? '' : String(r[i])) : String(r[i] ?? ''))).join(','))]
    const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob([lines.join('\r\n')], { type: 'text/csv' })); a.download = `${entry.id}-${today}.csv`; a.click(); setTimeout(() => URL.revokeObjectURL(a.href), 10_000)
  }
  const html = (company: api.CompanyInfo | null) => res ? `<div class="hdr"><div class="co">${esc(company?.name ?? '')}</div><div class="meta"><h1>${esc(res.title)}</h1><small>${esc(subtitle)}</small></div></div>
    <table><thead><tr>${res.columns.map(c => `<th class="${c.type === 'text' || c.type === 'date' ? '' : 'num'}">${esc(c.label)}</th>`).join('')}</tr></thead><tbody>
    ${res.rows.map(r => `<tr>${res.columns.map((c, i) => `<td class="${c.type === 'text' || c.type === 'date' ? '' : 'num'}">${esc(fmt(c, r[i]))}</td>`).join('')}</tr>`).join('')}
    ${totals?.some(t => t != null) ? `<tr>${res.columns.map((c, i) => `<th class="num">${i === 0 ? 'Total' : totals![i] != null ? esc(fmt(c, totals![i] as number)) : ''}</th>`).join('')}</tr>` : ''}</tbody></table>` : ''

  return (
    <>
      <Toolbar tools={[{ label: 'Run', icon: 'open', onClick: run, disabled: busy || !def }, { label: 'Print', icon: 'print', onClick: () => setPrinting(true), disabled: !res || res.rows.length === 0 }, { label: 'CSV', icon: 'save', onClick: csv, disabled: !res || res.rows.length === 0, title: 'Download as a spreadsheet file (opens in Excel)' }]} />
      <div className="form">
        <div className="repparams">
          {has('range') && <><Field label="From"><input type="date" value={from} onChange={e => setFrom(e.target.value)} /></Field><Field label="To"><input type="date" value={to} onChange={e => setTo(e.target.value)} /></Field></>}
          {has('asof') && <Field label="As of"><input type="date" value={asOf} onChange={e => setAsOf(e.target.value)} /></Field>}
          {has('customer') && <Field label="Customer"><input readOnly className="ro" value={cust?.name ?? ''} placeholder="Choose a customer" style={{ width: '18rem' }} onClick={() => setPick(true)} /><button className="dots" onClick={() => setPick(true)}>…</button></Field>}
          {has('doctype') && <Field label="Type"><select value={type} onChange={e => setType(e.target.value)}><option value="">All</option><option value="Q">Quotes</option><option value="W">Work orders</option><option value="I">Invoices</option><option value="C">Credit memos</option></select></Field>}
        </div>
        {def?.note && <p className="muted small">{def.note}</p>}
        {error && <p className="formerr">{error}</p>}
        {busy && <p className="muted">Working…</p>}
        {res && <>
          <table className="grid-table report"><thead><tr>{res.columns.map(c => <th key={c.key} className={c.type === 'text' || c.type === 'date' ? '' : 'num'}>{c.label}</th>)}</tr></thead>
            <tbody>{res.rows.map((r, k) => <tr key={k}>{res.columns.map((c, i) => <td key={c.key} className={c.type === 'text' || c.type === 'date' ? '' : 'num'}>{fmt(c, r[i])}</td>)}</tr>)}
              {res.rows.length === 0 && <tr><td colSpan={res.columns.length} className="muted">Nothing to show for these dates.</td></tr>}</tbody>
            {totals?.some(t => t != null) && res.rows.length > 0 && <tfoot><tr className="total">{res.columns.map((c, i) => <td key={c.key} className="num">{i === 0 ? 'Total' : totals![i] != null ? fmt(c, totals![i] as number) : ''}</td>)}</tr></tfoot>}</table>
          <p className="muted small">{res.rows.length.toLocaleString()} rows{res.truncated ? ' (stopped at 20,000; narrow the dates)' : ''}.</p></>}
      </div>
      {pick && <CustomerLookup onClose={() => setPick(false)} onPick={async id => { setPick(false); const c = await api.getCustomer(id); setCust({ id, name: `${c.lastName}${c.firstName ? ', ' + c.firstName : ''}` }) }} />}
      {printing && res && <PrintDialog title={res.title} kind="statement" build={(_, company) => html(company)} onClose={() => setPrinting(false)} />}
    </>
  )
}
