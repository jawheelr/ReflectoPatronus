import { pageSize, type PrinterProfile } from './printers'

/**
 * Wraps document HTML in a full page whose @page rule carries the printer profile's paper size, orientation and margins.
 * Copies are repeated page-break-separated, so a profile asking for 2 copies prints each document twice, collated.
 */
export function pageHtml(body: string, p: PrinterProfile, title: string, watermark?: string | null): string {
  const { w, h } = pageSize(p)
  const copies = Math.min(Math.max(Math.round(p.copies) || 1, 1), 20)
  const mark = watermark?.trim() ? `<div class="wm" aria-hidden="true"><span>${esc(watermark.trim())}</span></div>` : ''
  const sheets = Array.from({ length: copies }, (_, i) => `<section class="copy"${i ? ' style="break-before:page"' : ''}>${mark}${body}</section>`).join('')
  const narrow = w < 4.5
  return `<!doctype html><html><head><meta charset="utf-8"><title>${esc(title)}</title><style>
@page { size: ${w}in ${h}in; margin: ${p.margin}in }
* { box-sizing: border-box }
html { zoom: ${p.scale / 100} }
body { margin: 0; font: ${narrow ? 10 : 11}pt/1.35 -apple-system, "Segoe UI", Helvetica, Arial, sans-serif; color: #111; ${p.color ? '' : 'filter: grayscale(1);'} }
h1 { font-size: ${narrow ? 14 : 20}pt; margin: 0 }
.hdr { display: flex; justify-content: space-between; gap: 1rem; border-bottom: 2px solid #111; padding-bottom: .4rem; margin-bottom: .6rem }
.hdr .co { font-weight: 600 } .hdr .meta { text-align: right }
.hdr small, .muted { color: #555 } small { font-size: 9pt }
.parties { display: flex; gap: 1.5rem; margin-bottom: .6rem } .parties > div { flex: 1 } .parties h4 { margin: 0 0 .15rem; font-size: 9pt; text-transform: uppercase; color: #555; letter-spacing: .04em }
table { width: 100%; border-collapse: collapse } th, td { padding: .2rem .35rem; text-align: left; vertical-align: top; border-bottom: 1px solid #ccc }
th { font-size: 9pt; text-transform: uppercase; border-bottom: 1.5px solid #111; color: #333 } .num { text-align: right; white-space: nowrap }
tr { break-inside: avoid } thead { display: table-header-group } .child td:nth-child(3) { padding-left: 1.2rem }
.totals { margin: .5rem 0 0 auto; width: ${narrow ? '100%' : '45%'} } .totals td { border: 0; padding: .1rem .35rem } .totals .grand td { border-top: 2px solid #111; font-weight: 700; font-size: 12pt }
.note { margin-top: .7rem; white-space: pre-wrap } .box { border: 1px solid #999; padding: .4rem .6rem; margin-top: .6rem; white-space: pre-wrap }
.foot { margin-top: 1rem; font-size: 8.5pt; color: #444; white-space: pre-wrap } .void { color: #b00; font-size: 26pt; font-weight: 800; letter-spacing: .2em; text-align: center; border: 4px solid #b00; margin: .4rem 0 }
.wm { position: fixed; top: 0; left: 0; width: 100%; height: 100vh; display: flex; align-items: center; justify-content: center; z-index: -1; pointer-events: none; overflow: hidden }
.wm span { transform: rotate(-35deg); font: 800 ${narrow ? 28 : 64}pt/1.1 -apple-system, Helvetica, Arial, sans-serif; color: rgba(0,0,0,.07); text-align: center; text-transform: uppercase; letter-spacing: .05em; max-width: 90%; -webkit-print-color-adjust: exact; print-color-adjust: exact }
.sig { margin-top: 1.6rem; display: flex; gap: 2rem } .sig div { flex: 1; border-top: 1px solid #111; padding-top: .15rem; font-size: 9pt }
</style></head><body>${sheets}</body></html>`
}

export const esc = (s: string | number | null | undefined) =>
  String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!)

/**
 * Print through a hidden frame so the page's own styles never leak into the printout; the OS dialog then lists the device's printers.
 * The frame is page-sized but parked off-screen: browsers silently skip print() on 0x0 or display:none frames.
 * If the frame cannot print, falls back to a popup window.
 */
export function printHtml(html: string): Promise<void> {
  return new Promise(resolve => {
    const f = document.createElement('iframe')
    f.setAttribute('aria-hidden', 'true'); f.tabIndex = -1
    f.style.cssText = 'position:fixed;left:-10000px;top:0;width:8.5in;height:11in;border:0;opacity:0;pointer-events:none'
    let finished = false
    const done = () => { if (finished) return; finished = true; setTimeout(() => f.remove(), 1000); resolve() }
    const popup = () => {
      const w = window.open('', '_blank', 'width=900,height=700')
      if (!w) { alert('The print window was blocked by the browser. Allow pop-ups for this site and try again.'); return done() }
      w.document.open(); w.document.write(html); w.document.close()
      w.addEventListener('afterprint', () => { w.close() }, { once: true })
      setTimeout(() => { w.focus(); w.print() }, 300)
      done()
    }
    f.onload = () => {
      const w = f.contentWindow
      if (!w || w.location.href === 'about:blank' && !w.document.body?.children.length) return
      w.addEventListener('afterprint', done, { once: true })
      setTimeout(() => {
        try { w.focus(); w.print() } catch { popup() }
      }, 100)
      setTimeout(done, 120000)
    }
    f.srcdoc = html
    document.body.appendChild(f)
  })
}
