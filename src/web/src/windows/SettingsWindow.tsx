import { useEffect, useState } from 'react'
import * as api from '../api'
import { instant } from '../format'
import { useShell } from '../shell/WindowContext'

export function SettingsWindow() {
  const { me, refreshMe } = useShell()
  const [zones, setZones] = useState<string[]>([])
  const [sessions, setSessions] = useState<api.SessionInfo[]>([])
  const [msg, setMsg] = useState('')
  const load = () => api.getSessions().then(setSessions)
  useEffect(() => { api.getTimeZones().then(setZones); load() }, [])
  async function save(tz: string) {
    try { await api.setTimeZone(tz || null); refreshMe(); setMsg('Saved.') } catch (e) { setMsg((e as Error).message) }
  }
  return (
    <div className="form">
      <h3>Time zone</h3>
      <p>Times are shown in <strong>{me.timeZone}</strong>{me.userTimeZone ? '' : ' (company default)'}.</p>
      <select value={me.userTimeZone ?? ''} onChange={e => save(e.target.value)}>
        <option value="">Company default ({me.companyTimeZone})</option>
        {zones.map(z => <option key={z}>{z}</option>)}
      </select> <span>{msg}</span>
      <h3>Signed-in devices</h3>
      <table className="grid-table">
        <thead><tr><th>Device</th><th>Signed in</th><th>Last used</th><th /></tr></thead>
        <tbody>
          {sessions.map(s => (
            <tr key={s.id}>
              <td>{s.deviceName} <small>({s.platform})</small>{s.current && ' — this device'}</td>
              <td>{instant(s.createdAt, me.timeZone)}</td><td>{instant(s.lastSeenAt, me.timeZone)}</td>
              <td>{!s.current && <button onClick={() => api.revokeSession(s.id).then(load)}>Sign out</button>}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
