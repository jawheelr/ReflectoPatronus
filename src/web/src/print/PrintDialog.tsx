import { useEffect, useMemo, useState } from 'react'
import * as api from '../api'
import { useShell } from '../shell/WindowContext'
import { Modal } from '../shell/ui'
import { pageHtml, printHtml } from './engine'
import { loadPrefs, savePrefs, profileFor, type DocKind } from './printers'

/** Preview + print. Shows the document as it will come out on the chosen printer profile; Print opens the system dialog. */
export function PrintDialog({ title, kind, build, canHidePrices, hidePricesDefault, onPrinted, onClose }: {
  title: string; kind: DocKind; build: (hidePrices: boolean, company: api.CompanyInfo | null) => string
  canHidePrices?: boolean; hidePricesDefault?: boolean; onPrinted?: () => void; onClose: () => void
}) {
  const shell = useShell()
  const prefs = useMemo(loadPrefs, [])
  const [profileId, setProfileId] = useState(profileFor(prefs, kind).id)
  const [copies, setCopies] = useState<number | null>(null)
  const [wm, setWm] = useState(prefs.watermark)
  const [hide, setHide] = useState(hidePricesDefault ?? false)
  const [company, setCompany] = useState<api.CompanyInfo | null>(null)
  const [ready, setReady] = useState(false)
  useEffect(() => { api.getCompany().then(setCompany).catch(() => {}).finally(() => setReady(true)) }, [])
  const profile = { ...prefs.profiles.find(p => p.id === profileId)!, ...(copies != null ? { copies } : {}) }
  const html = useMemo(() => ready ? pageHtml(build(hide, company), profile, title, wm ? company?.name : null) : '', [ready, hide, wm, company, profileId, copies]) // eslint-disable-line react-hooks/exhaustive-deps

  async function go() {
    await printHtml(html); onPrinted?.(); onClose()
  }
  return (
    <Modal title={`Print ${title}`} onClose={onClose} width={900}>
      <div className="printdlg">
        <div className="opts">
          <label>Printer profile<select value={profileId} onChange={e => setProfileId(e.target.value)}>{prefs.profiles.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label>
          <label>Copies<input type="number" min={1} max={20} value={copies ?? profile.copies} onChange={e => setCopies(Math.max(1, Number(e.target.value) || 1))} style={{ width: '4rem' }} /></label>
          {canHidePrices && <label className="chk"><input type="checkbox" checked={!hide} onChange={e => setHide(!e.target.checked)} /> Show prices</label>}
          <label className="chk" title="Prints the company name faintly across each page"><input type="checkbox" checked={wm} onChange={e => { setWm(e.target.checked); savePrefs({ ...loadPrefs(), watermark: e.target.checked }) }} /> Watermark</label>
          <span className="spacer" />
          <button className="link" onClick={() => shell.open('printers')}>Printer setup…</button>
          <button className="primary" onClick={go} disabled={!ready}>Print…</button>
        </div>
        <p className="muted small">{profile.systemName ? <>Choose <b>{profile.systemName}</b> in the next dialog. </> : null}The dialog that opens lists every printer installed on this device, and can also save a PDF.</p>
        <iframe title="Print preview" className="preview" srcDoc={html} />
      </div>
    </Modal>
  )
}
