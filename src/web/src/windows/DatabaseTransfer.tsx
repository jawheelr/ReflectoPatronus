import { useState } from 'react'
import * as api from '../api'
import { useShell } from '../shell/WindowContext'

const n = (x: number) => x.toLocaleString('en-US')

export function DatabaseTransfer() {
  const { me, toast } = useShell()
  const [withFiles, setWithFiles] = useState(false)
  const [busy, setBusy] = useState(false)
  const [file, setFile] = useState<File | null>(null)
  const [mode, setMode] = useState<'merge' | 'replace'>('merge')
  const [overwrite, setOverwrite] = useState(false)
  const [confirm, setConfirm] = useState('')
  const [result, setResult] = useState<api.TransferResult | null>(null)
  const [error, setError] = useState('')
  if (!me.isAdmin) return <div className="form"><p>Administrator access is required.</p></div>

  async function doExport() {
    setBusy(true)
    try {
      const blob = await api.exportDb(withFiles)
      const a = document.createElement('a')
      a.href = URL.createObjectURL(blob)
      a.download = `reflectopatronus-${new Date().toISOString().slice(0, 16).replace(/[-:T]/g, '')}.rpb`
      a.click(); setTimeout(() => URL.revokeObjectURL(a.href), 10_000)
      toast('Export downloaded.')
    } catch (e) { toast((e as Error).message, 'error') } finally { setBusy(false) }
  }

  async function doImport(dryRun: boolean) {
    if (!file) return
    setBusy(true); setError(''); setResult(null)
    try {
      const r = await api.importDb(file, mode, overwrite, dryRun)
      setResult(r); toast(dryRun ? 'Preview finished. Nothing was changed.' : 'Import finished.')
    } catch (e) { setError((e as Error).message) } finally { setBusy(false) }
  }

  const ready = !!file && !busy && (mode === 'merge' || confirm === 'REPLACE')
  return (
    <div className="form">
      <h3>Export</h3>
      <p className="muted">Downloads the whole database as one file (.rpb): customers, documents, parts, pricing, tax and settings. Sign-in passwords, devices and supplier feed passwords are never included.</p>
      <label><input type="checkbox" checked={withFiles} onChange={e => setWithFiles(e.target.checked)} /> Include photos and attachments <span className="muted">(can be many gigabytes; a browser may not be able to hold a file that large)</span></label>
      <p><button onClick={doExport} disabled={busy}>Export database</button></p>

      <h3>Import</h3>
      <p><input type="file" accept=".rpb" onChange={e => { setFile(e.target.files?.[0] ?? null); setResult(null); setError('') }} /></p>
      <fieldset className="radios">
        <label><input type="radio" checked={mode === 'merge'} onChange={() => setMode('merge')} /> <strong>Merge</strong>: add records that are new. Records are matched by their ID.</label>
        {mode === 'merge' && <label className="indent"><input type="checkbox" checked={overwrite} onChange={e => setOverwrite(e.target.checked)} /> Overwrite records that already exist with the file's version <span className="muted">(otherwise existing records are kept as they are)</span></label>}
        <label><input type="radio" checked={mode === 'replace'} onChange={() => setMode('replace')} /> <strong>Replace everything</strong>: delete the current data first, then load the file.</label>
        {mode === 'replace' && <label className="indent">Type <code>REPLACE</code> to confirm: <input value={confirm} onChange={e => setConfirm(e.target.value)} style={{ width: '8rem' }} /></label>}
      </fieldset>
      <p className="muted">Users are never overwritten or deleted by an import; users missing here are added without a password. Everything is applied in one step: if anything is wrong, nothing changes. Export a backup first.</p>
      <p className="row-actions">
        <button onClick={() => doImport(true)} disabled={!ready}>Preview (no changes)</button>
        <button onClick={() => doImport(false)} disabled={!ready}>{mode === 'replace' ? 'Replace and import' : 'Import'}</button>
        {busy && <span className="muted">Working… large files take a few minutes.</span>}
      </p>
      {error && <p role="alert" className="error">{error}</p>}
      {result && (
        <div className="panel-block">
          <h3>{result.applied ? 'Imported' : 'Preview'}: {n(result.inserted)} added, {n(result.updated)} overwritten, {n(result.skipped)} left as they were{result.deleted ? `, ${n(result.deleted)} deleted first` : ''}</h3>
          <table className="grid-table">
            <thead><tr><th>Table</th><th style={{ textAlign: 'right' }}>Added</th><th style={{ textAlign: 'right' }}>Overwritten</th><th style={{ textAlign: 'right' }}>Unchanged</th><th style={{ textAlign: 'right' }}>Deleted first</th></tr></thead>
            <tbody>{result.tables.map(t => <tr key={t.table}><td>{t.table}</td><td style={{ textAlign: 'right' }}>{n(t.inserted)}</td><td style={{ textAlign: 'right' }}>{n(t.updated)}</td><td style={{ textAlign: 'right' }}>{n(t.skipped)}</td><td style={{ textAlign: 'right' }}>{n(t.deleted)}</td></tr>)}</tbody>
          </table>
        </div>
      )}
    </div>
  )
}
