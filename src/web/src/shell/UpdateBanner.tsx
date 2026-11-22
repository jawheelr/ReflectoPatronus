import { useCallback, useEffect, useState } from 'react'
import * as api from '../api'
import { instant } from '../format'
import { Modal } from './ui'

/**
 * Tells administrators when a newer commit exists on the branch this server tracks. The server checks GitHub once a week and remembers
 * the answer; this only reads that. Updating is a deliberate step on the server itself (the commands are shown), never automatic.
 */
export function useUpdates(me: api.Me) {
  const [s, setS] = useState<api.UpdateStatus | null>(null)
  const allowed = api.can(me, 'users.manage')
  const load = useCallback(() => { if (allowed) api.getUpdateStatus().then(setS).catch(() => {}) }, [allowed])
  useEffect(() => { load(); const t = window.setInterval(load, 6 * 3600 * 1000); return () => window.clearInterval(t) }, [load])
  return { s, setS, reload: load, allowed }
}

export function UpdateBanner({ s, onDetails, onDismiss }: { s: api.UpdateStatus | null; onDetails: () => void; onDismiss: () => void }) {
  if (!s || !s.available || s.dismissed) return null
  return (
    <div className="updatebar" role="status">
      <span><b>A new version is available.</b> {s.commitsBehind ? `${s.commitsBehind} newer change${s.commitsBehind === 1 ? '' : 's'}` : 'Newer changes'} on {s.branch}{s.latestMessage ? `: “${s.latestMessage}”` : ''}.</span>
      <span className="spacer" />
      <button onClick={onDetails}>How to update</button><button onClick={onDismiss} title="Hide this until there is something newer">Not now</button>
    </div>
  )
}

export function UpdateDialog({ me, s, setS, onClose }: { me: api.Me; s: api.UpdateStatus | null; setS: (s: api.UpdateStatus) => void; onClose: () => void }) {
  const [busy, setBusy] = useState(false); const [err, setErr] = useState('')
  const check = async () => { setBusy(true); setErr(''); try { setS(await api.checkForUpdates()) } catch (e) { setErr((e as Error).message) } finally { setBusy(false) } }
  return (
    <Modal title="Updates" onClose={onClose} width={640}>
      <div className="lookup-body">
        {!s ? <p className="muted">Loading…</p> : <>
          <p>This server is running commit <code>{s.currentShort ?? 'unknown'}</code> ({s.mode === 'docker' ? 'Docker' : s.mode === 'lxc' ? 'LXC container' : 'other install'}).
            {s.checkedAt ? <> Last checked {instant(s.checkedAt, me.timeZone)}.</> : ' It has not been checked yet.'}</p>
          {!s.enabled && <p className="muted">The weekly check is turned off on this server (<code>Updates__Enabled=false</code>).</p>}
          {s.error && <p className="formerr">{s.error}</p>}
          {s.canCompare ? (s.available
            ? <p><b>A newer version is available:</b> <code>{s.latestShort}</code>{s.latestMessage ? ` “${s.latestMessage}”` : ''}{s.latestDate ? `, ${instant(s.latestDate, me.timeZone)}` : ''}{s.commitsBehind ? ` (${s.commitsBehind} change${s.commitsBehind === 1 ? '' : 's'} ahead)` : ''}. {s.latestUrl && <a href={s.latestUrl} target="_blank" rel="noreferrer">See it on GitHub</a>}</p>
            : s.latest ? <p>This server is up to date with <code>{s.branch}</code> (<code>{s.latestShort}</code>).</p> : null)
            : <p className="muted">This build does not know which commit it came from (a locally built image without <code>GIT_SHA</code>), so it cannot tell whether it is behind. Updating with the command below fixes that.</p>}
          <h4>To update</h4>
          <ol><li>Export a backup first (Utilities &gt; Import / Export Database &gt; Save a copy on the server).</li>
            <li>Run on the server:{s.commands.map(c => <pre key={c} className="cmd">{c}</pre>)}</li>
            <li>The update scripts ask before changing anything, apply database changes, restart the program and check it came back.</li></ol>
          <p className="muted small">The server asks GitHub for the newest commit of <code>{s.repository}</code> once a week and sends nothing about your shop. Nothing updates by itself.</p>
        </>}
        {err && <p className="formerr">{err}</p>}
        <div className="actions"><button onClick={check} disabled={busy || !s?.enabled}>{busy ? 'Checking…' : 'Check now'}</button><button onClick={onClose}>Close</button></div>
      </div>
    </Modal>
  )
}
