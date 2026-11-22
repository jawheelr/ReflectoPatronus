import { useCallback, useEffect, useRef, useState } from 'react'
import * as api from '../api'
import { instant } from '../format'
import { useShell } from '../shell/WindowContext'

const n = (x: number) => x.toLocaleString('en-US')
const size = (b: number) => b >= 1 << 30 ? `${(b / (1 << 30)).toFixed(2)} GB` : b >= 1 << 20 ? `${(b / (1 << 20)).toFixed(1)} MB` : `${Math.max(1, Math.round(b / 1024))} KB`

/**
 * Export / import for databases of any size. The server does the heavy work as background jobs on files in its transfer folder, so the
 * browser never has to hold the data: downloads go straight to disk through the browser's download manager (and resume if interrupted),
 * uploads stream from disk with a progress bar, and files can also be copied in and out of the folder directly (scp, SMB, docker cp).
 */
export function DatabaseTransfer() {
  const { me, toast } = useShell()
  const [withFiles, setWithFiles] = useState(false)
  const [files, setFiles] = useState<api.TransferFiles | null>(null)
  const [job, setJob] = useState<api.TransferJob | null>(null)
  const [pick, setPick] = useState<string | null>(null)
  const [mode, setMode] = useState<'merge' | 'replace'>('merge'); const [overwrite, setOverwrite] = useState(false); const [confirm, setConfirm] = useState('')
  const [up, setUp] = useState<{ sent: number; total: number; name: string } | null>(null)
  const [error, setError] = useState('')
  const timer = useRef<number>(0)

  const refresh = useCallback(() => api.getTransferFiles().then(setFiles).catch(e => setError(e.message)), [])
  useEffect(() => { if (api.can(me, 'transfer.manage')) void refresh() }, [me, refresh])
  useEffect(() => () => window.clearInterval(timer.current), [])
  if (!api.can(me, 'transfer.manage')) return <div className="form"><p>You do not have permission to export or import the database.</p></div>

  function watch(id: string) {
    window.clearInterval(timer.current)
    let misses = 0   // a dropped request must not end the watch: the job keeps running on the server
    const tick = async () => {
      try {
        const j = await api.getJob(id); setJob(j); misses = 0
        if (j.state !== 'running') { window.clearInterval(timer.current); void refresh(); toast(j.state === 'done' ? (j.message ?? 'Done.') : 'The job failed.', j.state === 'done' ? 'ok' : 'error') }
      } catch (e) { if (++misses >= 8) { window.clearInterval(timer.current); setError(`Lost contact with the server: ${(e as Error).message}. The job may still be running; reopen this window to check.`) } }
    }
    void tick(); timer.current = window.setInterval(tick, 1500)
  }
  const run = async (f: () => Promise<void>) => { setError(''); try { await f() } catch (e) { setError((e as Error).message) } }
  const saveOnServer = () => run(async () => { setJob(null); watch((await api.startExportJob(withFiles)).id) })
  const downloadNow = () => run(() => api.downloadViaBrowser({ attachments: withFiles }))
  const upload = (f: File | undefined) => f && run(async () => {
    setUp({ sent: 0, total: f.size, name: f.name })
    try { await api.uploadTransferFile(f, (sent, total) => setUp({ sent, total, name: f.name })); toast('Uploaded.'); setPick(f.name); await refresh() } finally { setUp(null) }
  })
  const importIt = (dryRun: boolean) => pick && run(async () => { setJob(null); watch((await api.startImportJob(pick, mode, overwrite, dryRun)).id) })
  const remove = (name: string) => window.confirm(`Delete ${name} from the server?`) && run(async () => { await api.deleteTransferFile(name); if (pick === name) setPick(null); await refresh() })

  const running = job?.state === 'running'
  const ready = !!pick && !running && (mode === 'merge' || confirm === 'REPLACE')
  const pct = job && job.total > 0 ? Math.min(100, Math.round(job.bytes / job.total * 100)) : null
  return (
    <div className="form">
      <h3>Export</h3>
      <p className="muted">The whole database as one file (.rpb): customers, documents, parts, pricing, tax and settings. Sign-in passwords, devices and supplier feed passwords are never included.</p>
      <label><input type="checkbox" checked={withFiles} onChange={e => setWithFiles(e.target.checked)} /> Include photos and attachments <span className="muted">(most of the database; can be tens of gigabytes)</span></label>
      <p className="row-actions">
        <button onClick={saveOnServer} disabled={running} title="Recommended for large databases: the server writes the file to its transfer folder, then you download it">Save a copy on the server</button>
        <button onClick={downloadNow} title="Streams straight to your Downloads folder; if it is interrupted you must start again">Download now</button>
      </p>
      <p className="muted small">For a large database use <b>Save a copy on the server</b>, then download that file below: the download can resume if the connection drops. The file stays in the server's transfer folder (<code>{files?.directory ?? '…'}</code>) where you can also copy it with scp, a network share or <code>docker cp</code>.</p>

      <h3>Files on the server</h3>
      <table className="grid-table"><thead><tr><th>File</th><th className="num">Size</th><th>Saved</th><th /></tr></thead>
        <tbody>{files?.files.map(f => <tr key={f.name} className={pick === f.name ? 'sel' : ''}>
          <td>{f.name}</td><td className="num">{size(f.size)}</td><td>{instant(f.modified, me.timeZone)}</td>
          <td className="actions-cell"><button onClick={() => setPick(f.name)}>Use for import</button> <button onClick={() => run(() => api.downloadViaBrowser({ file: f.name }))}>Download</button> <button className="x" title="Delete" onClick={() => remove(f.name)}>×</button></td></tr>)}
          {files?.partial.map(f => <tr key={f.name} className="muted"><td>{f.name} (in progress or interrupted)</td><td className="num">{size(f.size)}</td><td colSpan={2} /></tr>)}
          {files && files.files.length === 0 && <tr><td colSpan={4} className="muted">No files yet.</td></tr>}</tbody></table>
      {files && files.freeBytes > 0 && <p className="muted small">{size(files.freeBytes)} free in the transfer folder.</p>}
      <p><label className="upload">Upload a file from this computer: <input type="file" accept=".rpb" disabled={!!up} onChange={e => { upload(e.target.files?.[0]); e.target.value = '' }} /></label>
        <span className="muted small"> Very large files are more reliable copied into the folder directly.</span></p>
      {up && <p><progress value={up.sent} max={up.total} style={{ width: '20rem' }} /> Uploading {up.name}: {size(up.sent)} of {size(up.total)}</p>}

      <h3>Import</h3>
      <p>{pick ? <>Importing from <b>{pick}</b></> : <span className="muted">Choose a file above (Use for import), or upload one.</span>}</p>
      <fieldset className="radios">
        <label><input type="radio" checked={mode === 'merge'} onChange={() => setMode('merge')} /> <strong>Merge</strong>: add records that are new. Records are matched by their ID.</label>
        {mode === 'merge' && <label className="indent"><input type="checkbox" checked={overwrite} onChange={e => setOverwrite(e.target.checked)} /> Overwrite records that already exist with the file's version <span className="muted">(otherwise existing records are kept as they are)</span></label>}
        <label><input type="radio" checked={mode === 'replace'} onChange={() => setMode('replace')} /> <strong>Replace everything</strong>: delete the current data first, then load the file.</label>
        {mode === 'replace' && <label className="indent">Type <code>REPLACE</code> to confirm: <input value={confirm} onChange={e => setConfirm(e.target.value)} style={{ width: '8rem' }} /></label>}
      </fieldset>
      <p className="muted">Users are never overwritten or deleted by an import; users missing here are added without a password. Everything is applied in one step: if anything is wrong, nothing changes. Export a backup first.</p>
      <p className="row-actions">
        <button onClick={() => importIt(true)} disabled={!ready}>Preview (no changes)</button>
        <button onClick={() => importIt(false)} disabled={!ready}>{mode === 'replace' ? 'Replace and import' : 'Import'}</button>
      </p>

      {job && <div className="panel-block">
        <h3>{job.kind === 'export' ? 'Export' : 'Import'} {job.state === 'running' ? 'running…' : job.state === 'done' ? 'finished' : 'failed'}{job.file ? `: ${job.file}` : ''}</h3>
        {job.state === 'running' && <p><progress value={pct ?? undefined} max={pct == null ? undefined : 100} style={{ width: '20rem' }} /> {pct != null ? `${pct}% of the file` : `${size(job.bytes)} written so far`}. You can leave this window open or come back; it keeps running on the server.</p>}
        {job.message && <p role={job.state === 'error' ? 'alert' : undefined} className={job.state === 'error' ? 'error' : ''}>{job.message}</p>}
        {job.result && <>
          <h4>{job.result.applied ? 'Imported' : 'Preview'}: {n(job.result.inserted)} added, {n(job.result.updated)} overwritten, {n(job.result.skipped)} left as they were{job.result.deleted ? `, ${n(job.result.deleted)} deleted first` : ''}</h4>
          <table className="grid-table"><thead><tr><th>Table</th><th className="num">Added</th><th className="num">Overwritten</th><th className="num">Unchanged</th><th className="num">Deleted first</th></tr></thead>
            <tbody>{job.result.tables.map(t => <tr key={t.table}><td>{t.table}</td><td className="num">{n(t.inserted)}</td><td className="num">{n(t.updated)}</td><td className="num">{n(t.skipped)}</td><td className="num">{n(t.deleted)}</td></tr>)}</tbody></table></>}
      </div>}
      {error && <p role="alert" className="error">{error}</p>}
    </div>
  )
}
