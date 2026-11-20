import { useEffect, useRef, type ReactNode } from 'react'

/** The toolbar icons are drawn inline so there is no icon-font dependency. */
const PATHS: Record<string, string> = {
  save: 'M5 3h11l3 3v15H5zM8 3v5h7V3M8 21v-7h8v7',
  print: 'M7 8V3h10v5M5 8h14a2 2 0 0 1 2 2v6h-4v4H7v-4H3v-6a2 2 0 0 1 2-2zM7 14h10v6H7z',
  email: 'M3 6h18v12H3zM3 7l9 7 9-7',
  open: 'M3 7h6l2 2h10v10H3zM3 19l3-8h17l-3 8',
  delete: 'M5 5l14 14M19 5L5 19',
  clear: 'M4 20l9-9 4 4-5 5zM13 11l4-7 3 3-7 4',
  notes: 'M6 3h9l4 4v14H6zM9 12h7M9 16h7',
}
export function Icon({ name }: { name: keyof typeof PATHS | string }) {
  return <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round"><path d={PATHS[name]} /></svg>
}

export interface Tool { label: string; icon: string; onClick?: () => void; disabled?: boolean; title?: string }
export const NOT_BUILT = 'Not built yet: this window is read-only for now.'

export function Toolbar({ tools }: { tools: Tool[] }) {
  return (
    <div className="toolbar">
      {tools.map(t => (
        <button key={t.label} className="tool" disabled={t.disabled || !t.onClick} title={t.disabled || !t.onClick ? (t.title ?? NOT_BUILT) : t.title} onClick={t.onClick}>
          <Icon name={t.icon} /><span>{t.label}</span>
        </button>
      ))}
    </div>
  )
}

export function Tabs({ tabs, active, onChange }: { tabs: string[]; active: string; onChange: (t: string) => void }) {
  return <div className="tabs" role="tablist">{tabs.map(t => <button key={t} role="tab" aria-selected={t === active} className={t === active ? 'on' : ''} onClick={() => onChange(t)}>{t}</button>)}</div>
}

/** Label + read-only value, laid out like GQ7's form fields. */
export function Field({ label, children, wide }: { label: string; children?: ReactNode; wide?: boolean }) {
  return <label className={'field' + (wide ? ' wide' : '')}><span>{label}</span><div className="val">{children}</div></label>
}
export const RO = ({ value, width }: { value?: string | number | null; width?: string }) =>
  <input className="ro" readOnly value={value ?? ''} style={width ? { width } : undefined} tabIndex={-1} />

export function Modal({ title, onClose, children, width = 720 }: { title: string; onClose: () => void; children: ReactNode; width?: number }) {
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const k = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    document.addEventListener('keydown', k); ref.current?.focus()
    return () => document.removeEventListener('keydown', k)
  }, [onClose])
  return (
    <div className="backdrop" onMouseDown={e => e.target === e.currentTarget && onClose()}>
      <div className="modal" style={{ width }} ref={ref} tabIndex={-1} role="dialog" aria-label={title}>
        <header><span>{title}</span><button className="x" onClick={onClose} aria-label="Close">×</button></header>
        {children}
      </div>
    </div>
  )
}

export function Audit({ createdAt, updatedAt }: { createdAt?: string; updatedAt?: string }) {
  return <div className="audit"><span>Created: {createdAt ?? ''}</span><span>Updated: {updatedAt ?? ''}</span></div>
}
