import { useEffect, useState, type FormEvent } from 'react'
import { NavLink, Navigate, Route, Routes } from 'react-router-dom'
import * as api from './api'
import { CustomerPage, CustomersPage } from './Customers'
import { DocumentPage, DocumentsPage } from './Documents'
import { SettingsPage } from './Settings'

export default function App() {
  const [me, setMe] = useState<api.Me | null>(null)
  const [booting, setBooting] = useState(api.hasStoredSession())

  useEffect(() => {
    api.setSignedOutHandler(() => setMe(null))
    if (!api.hasStoredSession()) return
    api.refresh().then(ok => ok ? api.getMe().then(setMe) : undefined).catch(() => {}).finally(() => setBooting(false))
  }, [])

  if (booting) return <p className="center">Loading…</p>
  if (!me) return <Login onSignedIn={() => api.getMe().then(setMe)} />

  return (
    <div className="shell">
      <nav>
        <strong>ReflectoPatronus</strong>
        <NavLink to="/customers">Customers</NavLink>
        <NavLink to="/documents">Documents</NavLink>
        <NavLink to="/settings">Settings</NavLink>
        <span className="spacer" />
        <span>{me.name ?? me.code}</span>
        <button onClick={async () => { await api.logout(); setMe(null) }}>Sign out</button>
      </nav>
      <main>
        <Routes>
          <Route path="/customers" element={<CustomersPage />} />
          <Route path="/customers/:id" element={<CustomerPage tz={me.timeZone} />} />
          <Route path="/documents" element={<DocumentsPage />} />
          <Route path="/documents/:id" element={<DocumentPage tz={me.timeZone} />} />
          <Route path="/settings" element={<SettingsPage me={me} onChange={() => api.getMe().then(setMe)} />} />
          <Route path="*" element={<Navigate to="/customers" replace />} />
        </Routes>
      </main>
    </div>
  )
}

function Login({ onSignedIn }: { onSignedIn: () => void }) {
  const [userCode, setUserCode] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent) {
    e.preventDefault(); setBusy(true); setError('')
    try { await api.login(userCode.trim(), password); onSignedIn() }
    catch (err) { setError(err instanceof api.ApiError && err.status === 401 ? 'Invalid credentials.' : 'Sign-in failed.') }
    finally { setBusy(false) }
  }
  return (
    <form className="login" onSubmit={submit}>
      <h1>ReflectoPatronus</h1>
      <label>User<input value={userCode} onChange={e => setUserCode(e.target.value)} autoFocus autoComplete="username" /></label>
      <label>Password<input type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="current-password" /></label>
      {error && <p className="error">{error}</p>}
      <button disabled={busy || !userCode || !password}>Sign in</button>
    </form>
  )
}
