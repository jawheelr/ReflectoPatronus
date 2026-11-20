import { useEffect, useState, type FormEvent } from 'react'
import * as api from './api'
import { Shell } from './shell/Shell'

export default function App() {
  const [me, setMe] = useState<api.Me | null>(null)
  const [booting, setBooting] = useState(api.hasStoredSession())
  const loadMe = () => api.getMe().then(setMe)

  useEffect(() => {
    api.setSignedOutHandler(() => setMe(null))
    if (!api.hasStoredSession()) return
    api.refresh().then(ok => ok ? loadMe() : undefined).catch(() => {}).finally(() => setBooting(false))
  }, [])

  if (booting) return <p className="center">Loading…</p>
  if (!me) return <Login onSignedIn={loadMe} />
  return <Shell me={me} refreshMe={loadMe} onSignOut={async () => { await api.logout(); setMe(null) }} />
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
      <h1><b>reflecto</b>patronus</h1>
      <label>User<input value={userCode} onChange={e => setUserCode(e.target.value)} autoFocus autoComplete="username" /></label>
      <label>Password<input type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="current-password" /></label>
      {error && <p className="error">{error}</p>}
      <button className="primary" disabled={busy || !userCode || !password}>Sign in</button>
    </form>
  )
}
