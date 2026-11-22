import type { Account, CompanyInfo, DocumentDetail, DocumentLine, DocumentParty } from '../api'
import { money, plainDate } from '../format'
import { esc } from './engine'
import type { DocKind } from './printers'

export const kindOf = (type: string): DocKind => ({ Q: 'quote', W: 'workorder', I: 'invoice', C: 'credit' } as Record<string, DocKind>)[type] ?? 'invoice'
const TITLE: Record<DocKind, string> = { quote: 'QUOTE', workorder: 'WORK ORDER', invoice: 'INVOICE', credit: 'CREDIT MEMO', statement: 'STATEMENT' }

const cityLine = (p: { city: string | null; state: string | null; zip: string | null }) => [p.city, [p.state, p.zip].filter(Boolean).join(' ')].filter(Boolean).join(', ')
const addr = (p: DocumentParty) => [p.name, p.address1, p.address2, cityLine(p), p.phones.length ? p.phones.join(' · ') : ''].filter(Boolean).map(esc).join('<br>')

function companyBlock(c: CompanyInfo | null) {
  if (!c) return ''
  const lines = [c.address1, c.address2, cityLine(c), [c.phone && `Phone ${c.phone}`, c.fax && `Fax ${c.fax}`].filter(Boolean).join('  '), c.email].filter(Boolean)
  return `<div class="co">${esc(c.name)}<br><small>${lines.map(esc).join('<br>')}</small></div>`
}

const sizeText = (l: DocumentLine) => l.dims ? esc(l.dims) : ''
function lineRow(l: DocumentLine, prices: boolean) {
  const desc = [l.description, l.shape].filter(Boolean).map(esc).join(' ')
  return `<tr class="${l.parentSeq != null ? 'child' : ''}"><td class="num">${l.qty ?? ''}</td><td>${esc(l.partCode)}</td><td>${l.thickness ? esc(l.thickness) + ' ' : ''}${desc}</td><td>${sizeText(l)}</td>`
    + (prices ? `<td class="num">${l.unitPrice != null && l.source !== 'custom' ? money(l.unitPrice) : ''}</td><td class="num">${money(l.amount)}${!l.taxable && l.amount !== 0 ? ' *' : ''}</td>` : '') + '</tr>'
}

export interface RenderOpts { company: CompanyInfo | null; hidePrices?: boolean }

/** The printed form of a quote, work order, invoice or credit memo. Work orders can leave prices off (the shop copy). */
export function renderDocument(d: DocumentDetail, o: RenderOpts): string {
  const kind = kindOf(d.type), prices = !o.hidePrices
  const t = d.totals
  const rows = d.lines.map(l => lineRow(l, prices)).join('')
  const totals = !prices ? '' : `<table class="totals">
    <tr><td>Subtotal</td><td class="num">${money(t.lines)}</td></tr>
    ${d.taxes.filter(x => x.amount !== 0 || d.taxes.length === 1).map(x => `<tr><td>Tax ${esc(x.code)} (${x.percent}%)</td><td class="num">${money(x.amount)}</td></tr>`).join('')}
    <tr class="grand"><td>${kind === 'credit' ? 'Credit total' : 'Total'}</td><td class="num">${money(t.total)}</td></tr>
    ${kind === 'invoice' && d.paid ? `<tr><td>Paid</td><td class="num">${money(-d.paid)}</td></tr>` : ''}
    ${kind === 'invoice' && (d.paid || d.balance !== t.total) ? `<tr class="grand"><td>Balance due</td><td class="num">${money(d.balance)}</td></tr>` : ''}
  </table>`
  const co = o.company
  const foot = [kind === 'invoice' ? co?.invoiceNote : null, co?.useDisclaimer && kind !== 'workorder' ? co.disclaimer : null].filter(Boolean).map(esc).join('\n\n')
  const site = d.jobSite && (d.jobSite.address1 || d.jobSite.city) ? `<div><h4>Job site</h4>${addr(d.jobSite)}</div>` : ''
  return `
  <div class="hdr">${companyBlock(co)}<div class="meta"><h1>${TITLE[kind]}</h1><div># ${esc(d.number)}</div><small>${plainDate(d.date)}</small></div></div>
  ${d.voided ? '<div class="void">VOID</div>' : ''}
  <div class="parties"><div><h4>${kind === 'workorder' ? 'Customer' : kind === 'quote' ? 'Quoted to' : 'Bill to'}</h4>${addr(d.customer)}</div>${site}
    <div><h4>Details</h4><small>${[d.poNumber && `PO ${esc(d.poNumber)}`, d.terms && `Terms ${esc(d.terms)}`, d.salesPerson && `Sold by ${esc(d.salesPerson)}`,
      d.sourceDocumentNumber && d.sourceDocumentNumber !== '0000000' && `From ${esc(d.sourceDocumentNumber)}`, d.claimNumber && `Claim ${esc(d.claimNumber)}`, d.policyNumber && `Policy ${esc(d.policyNumber)}`].filter(Boolean).join('<br>')}</small></div></div>
  <table><thead><tr><th class="num">Qty</th><th>Part</th><th>Description</th><th>Size</th>${prices ? '<th class="num">Price</th><th class="num">Amount</th>' : ''}</tr></thead><tbody>${rows}</tbody></table>
  ${totals}
  ${prices && d.lines.some(l => !l.taxable && l.amount !== 0) ? '<div class="foot">* not taxable</div>' : ''}
  ${d.notePrinted ? `<div class="note">${esc(d.notePrinted)}</div>` : ''}
  ${kind === 'workorder' && d.specialInstructions ? `<div class="box"><b>Special instructions</b>\n${esc(d.specialInstructions)}</div>` : ''}
  ${kind === 'workorder' && d.workOrderInstructions ? `<div class="box"><b>Work order instructions</b>\n${esc(d.workOrderInstructions)}</div>` : ''}
  ${kind === 'workorder' ? '<div class="sig"><div>Customer signature</div><div>Date</div><div>Installer</div></div>' : ''}
  ${foot ? `<div class="foot">${foot}</div>` : ''}`
}

/** A customer statement: the open items and what is owed. */
export function renderStatement(a: Account, o: RenderOpts, today: string): string {
  const rows = a.documents.map(x => `<tr><td>${esc(x.number)}</td><td>${esc({ I: 'Invoice', W: 'Work order', C: 'Credit memo', Q: 'Quote' }[x.type] ?? x.type)}</td><td>${plainDate(x.date)}</td><td>${esc(x.poNumber)}</td><td class="num">${money(x.total)}</td><td class="num">${money(x.balance)}</td></tr>`).join('')
  return `
  <div class="hdr">${companyBlock(o.company)}<div class="meta"><h1>STATEMENT</h1><small>${plainDate(today)}</small></div></div>
  <div class="parties"><div><h4>Account</h4>${esc(a.name)}<br><small>${esc(a.number)}</small></div></div>
  <table><thead><tr><th>Number</th><th>Type</th><th>Date</th><th>PO</th><th class="num">Amount</th><th class="num">Balance</th></tr></thead><tbody>${rows || '<tr><td colspan="6">Nothing is owed.</td></tr>'}</tbody></table>
  <table class="totals">${a.onAccount ? `<tr><td>Payments on account</td><td class="num">${money(-a.onAccount)}</td></tr>` : ''}<tr class="grand"><td>Balance due</td><td class="num">${money(a.balance)}</td></tr></table>
  ${o.company?.invoiceNote ? `<div class="foot">${esc(o.company.invoiceNote)}</div>` : ''}`
}

export function renderTestPage(name: string, details: string, company: CompanyInfo | null): string {
  return `<div class="hdr">${companyBlock(company)}<div class="meta"><h1>Test page</h1><small>${esc(name)}</small></div></div>
  <p>If you can read this and the box below sits inside the edge of the paper on all four sides, this printer profile is set up correctly.</p>
  <div class="box"><b>${esc(name)}</b>\n${esc(details)}</div>
  <table><thead><tr><th>Qty</th><th>Part</th><th>Description</th><th class="num">Amount</th></tr></thead><tbody>
  <tr><td>1</td><td>SAMPLE</td><td>Sample line to check column widths</td><td class="num">${money(1234.5)}</td></tr></tbody></table>`
}
