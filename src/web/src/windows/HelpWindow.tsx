import { Fragment, useMemo, useState, type ReactNode } from 'react'
import { ARTICLES, type Article } from '../help/articles'

const inline = (s: string): ReactNode[] => s.split(/(\*\*[^*]+\*\*)/g).map((p, i) => p.startsWith('**') ? <strong key={i}>{p.slice(2, -2)}</strong> : <Fragment key={i}>{p}</Fragment>)

function Body({ lines }: { lines: string[] }) {
  const out: ReactNode[] = []
  for (let i = 0; i < lines.length; i++) {
    const l = lines[i]
    if (l.startsWith('## ')) out.push(<h4 key={i}>{l.slice(3)}</h4>)
    else if (l.startsWith('- ') || /^\d+\. /.test(l)) {
      const ordered = /^\d+\. /.test(l); const items: string[] = []
      while (i < lines.length && (ordered ? /^\d+\. /.test(lines[i]) : lines[i].startsWith('- '))) { items.push(lines[i].replace(ordered ? /^\d+\. / : /^- /, '')); i++ }
      i--
      const li = items.map((t, j) => <li key={j}>{inline(t)}</li>)
      out.push(ordered ? <ol key={i}>{li}</ol> : <ul key={i}>{li}</ul>)
    } else out.push(<p key={i}>{inline(l)}</p>)
  }
  return <>{out}</>
}

/** Help > Knowledge Base: searchable how-to articles, grouped by topic. */
export function HelpWindow() {
  const [q, setQ] = useState('')
  const [id, setId] = useState(ARTICLES[0].id)
  const shown = useMemo(() => {
    const t = q.trim().toLowerCase()
    return t ? ARTICLES.filter(a => (a.title + ' ' + a.body.join(' ')).toLowerCase().includes(t)) : ARTICLES
  }, [q])
  const groups = useMemo(() => [...new Set(shown.map(a => a.group))].map(g => ({ g, items: shown.filter(a => a.group === g) })), [shown])
  const cur: Article | undefined = ARTICLES.find(a => a.id === id)
  return (
    <div className="kb">
      <nav aria-label="Articles">
        <input className="search" placeholder="Search the knowledge base" value={q} onChange={e => setQ(e.target.value)} aria-label="Search the knowledge base" />
        {groups.map(({ g, items }) => <div key={g}><h5>{g}</h5><ul>{items.map(a => <li key={a.id}><button className={a.id === id ? 'on' : ''} onClick={() => setId(a.id)}>{a.title}</button></li>)}</ul></div>)}
        {shown.length === 0 && <p className="muted">No article matches "{q}".</p>}
      </nav>
      <article>{cur && <><h3>{cur.title}</h3><Body lines={cur.body} /></>}</article>
    </div>
  )
}
