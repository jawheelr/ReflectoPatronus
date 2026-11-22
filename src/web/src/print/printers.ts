// Printer profiles live on THIS device (localStorage), because printers belong to the device, not to the shop's server.
// A web page cannot list a computer's printers, so a profile records the page setup for a printer you add here and the
// operating system's own print dialog (which lists every printer installed on the device) does the actual choosing.
// A native desktop/iOS client can later fill the same profiles from the OS printer list without changing anything else.

export type DocKind = 'quote' | 'workorder' | 'invoice' | 'credit' | 'statement'
export const DOC_KINDS: { kind: DocKind; label: string }[] = [
  { kind: 'quote', label: 'Quotes' }, { kind: 'workorder', label: 'Work orders' }, { kind: 'invoice', label: 'Invoices' },
  { kind: 'credit', label: 'Credit memos' }, { kind: 'statement', label: 'Statements' }]

export type Paper = 'letter' | 'legal' | 'a4' | 'half' | 'receipt' | 'label' | 'custom'
export const PAPERS: Record<Paper, { label: string; w: number; h: number }> = {
  letter: { label: 'Letter 8.5 × 11 in', w: 8.5, h: 11 }, legal: { label: 'Legal 8.5 × 14 in', w: 8.5, h: 14 }, a4: { label: 'A4 210 × 297 mm', w: 8.27, h: 11.69 },
  half: { label: 'Half letter 5.5 × 8.5 in', w: 5.5, h: 8.5 }, receipt: { label: 'Receipt roll 80 mm', w: 3.15, h: 11 },
  label: { label: 'Label 4 × 6 in', w: 4, h: 6 }, custom: { label: 'Custom size', w: 8.5, h: 11 },
}

export interface PrinterProfile {
  id: string
  name: string            // what staff call it, e.g. "Front desk laser"
  systemName: string      // the printer's name in the system dialog (a reminder; the browser cannot preselect it)
  paper: Paper
  width: number; height: number      // inches; used when paper is custom
  landscape: boolean
  margin: number          // inches
  scale: number           // percent
  copies: number
  color: boolean
}
export interface PrintPrefs { profiles: PrinterProfile[]; defaults: Partial<Record<DocKind, string>>; showPricesOnWorkOrder: boolean }

const KEY = 'rp.printers'
export const newProfile = (name = 'New printer'): PrinterProfile => ({
  id: Math.random().toString(36).slice(2, 10), name, systemName: '', paper: 'letter', width: 8.5, height: 11, landscape: false, margin: 0.5, scale: 100, copies: 1, color: false })

export function loadPrefs(): PrintPrefs {
  try {
    const p = JSON.parse(localStorage.getItem(KEY) ?? 'null') as PrintPrefs | null
    if (p && Array.isArray(p.profiles) && p.profiles.length) return { ...p, defaults: p.defaults ?? {}, showPricesOnWorkOrder: p.showPricesOnWorkOrder ?? false }
  } catch { /* fall through */ }
  return { profiles: [{ ...newProfile('Default printer'), id: 'default' }], defaults: {}, showPricesOnWorkOrder: false }
}
export const savePrefs = (p: PrintPrefs) => localStorage.setItem(KEY, JSON.stringify(p))

/** The profile to use for a kind of document: the one chosen for it, else the first. */
export function profileFor(p: PrintPrefs, kind: DocKind): PrinterProfile {
  return p.profiles.find(x => x.id === p.defaults[kind]) ?? p.profiles[0]
}
export const pageSize = (p: PrinterProfile) => {
  const base = p.paper === 'custom' ? { w: p.width, h: p.height } : PAPERS[p.paper]
  return p.landscape ? { w: base.h, h: base.w } : { w: base.w, h: base.h }
}
