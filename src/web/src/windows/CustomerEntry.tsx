import { useEffect, useState } from 'react'
import * as api from '../api'
import { money, plainDate } from '../format'
import { useShell, useWinId } from '../shell/WindowContext'
import { Audit, Field, RO, Tabs, Toolbar } from '../shell/ui'
import { CustomerLookup } from './CustomerLookup'
import { DocumentBrowser } from './DocumentBrowser'

// GQ7 labelled its five phone slots like this (Customer Entry screen).
const PHONE_LABELS = ['Cell', 'Work', 'Home', 'Pager', 'Other']

export function CustomerEntry({ initialId }: { initialId?: number }) {
  const shell = useShell(), winId = useWinId()
  const [id, setId] = useState<number | null>(initialId ?? null)
  const [c, setC] = useState<api.CustomerDetail | null>(null)
  const [tab, setTab] = useState('General')
  const [lookup, setLookup] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    if (id == null) { setC(null); shell.setTitle(winId, 'Customer Entry'); return }
    api.getCustomer(id).then(d => { setC(d); setError(''); shell.setTitle(winId, `Customer: ${d.lastName}`) }).catch(e => setError(e.message))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  const phone = (slot: number) => c?.phones.find(p => p.slot === slot)
  return (
    <>
      <Toolbar tools={[
        { label: 'Save', icon: 'save' }, { label: 'Delete', icon: 'delete' },
        { label: 'Clear', icon: 'clear', onClick: () => { setId(null); setTab('General') }, title: 'Clear the form' },
        { label: 'Notes', icon: 'notes', disabled: !c }]} />
      <div className="form">
        <Field label="Code"><RO value={c?.number} width="9rem" /><button className="dots" title="Look up a customer" onClick={() => setLookup(true)}>…</button></Field>
        <Field label="Balance"><RO value={c ? money(c.balance) : ''} width="9rem" /></Field>
        {error && <p className="error">{error}</p>}
        <Tabs tabs={['General', 'Finance/Tax', 'Documents']} active={tab} onChange={setTab} />
        <div className="panel">
          {tab === 'General' && <>
            <Field label="Customer Type"><RO value={c?.type} width="5rem" /></Field>
            <Field label="First, Last"><RO value={c?.firstName} width="12rem" /><RO value={c?.lastName} width="20rem" /></Field>
            <Field label="Email Address" wide><RO value={c?.email} /></Field>
            <Field label="Contact"><RO value={c?.contactName} width="20rem" /></Field>
            <div className="cols">
              <fieldset><legend>Address</legend>
                <Field label="Address" wide><RO value={c?.address1} /></Field><Field label="" wide><RO value={c?.address2} /></Field>
                <Field label="City"><RO value={c?.city} /></Field>
                <Field label="State"><RO value={c?.state} width="3rem" /><span className="lbl">ZIP</span><RO value={c?.zip} width="7rem" /></Field>
              </fieldset>
              <fieldset><legend>Phone</legend>
                {PHONE_LABELS.map((l, i) => <Field key={l} label={l}><RO value={phone(i + 1)?.number} width="10rem" /><span className="lbl">Ext</span><RO value={phone(i + 1)?.ext} width="4rem" /></Field>)}
              </fieldset>
            </div>
          </>}
          {tab === 'Finance/Tax' && <>
            <Field label="Tax Group"><RO value={c?.taxGroup} width="10rem" /></Field>
            <Field label="Tax Exempt"><input type="checkbox" disabled checked={c?.taxExempt ?? false} /></Field>
            <Field label="Tax ID"><RO value={c?.taxId} width="12rem" /></Field>
            <Field label="Price Category"><RO value={c?.priceCategory} width="10rem" /></Field>
            <Field label="Category"><RO value={c?.category} width="10rem" /></Field>
            <Field label="Credit Limit"><RO value={c?.creditLimit != null ? money(c.creditLimit) : ''} width="10rem" /></Field>
            <Field label="Print Statement"><input type="checkbox" disabled checked={c?.printStatement ?? false} /></Field>
            <Field label="No Prices"><input type="checkbox" disabled checked={c?.noPrices ?? false} /></Field>
            <Field label="Last Transaction"><RO value={c?.lastTransactionOn ? plainDate(c.lastTransactionOn) : ''} width="10rem" /></Field>
            {c?.noteInternal && <Field label="Internal note" wide><textarea className="ro" readOnly value={c.noteInternal} /></Field>}
            {c?.notePrinted && <Field label="Printed note" wide><textarea className="ro" readOnly value={c.notePrinted} /></Field>}
          </>}
          {tab === 'Documents' && (c ? <DocumentBrowser customerId={c.id} compact /> : <p className="muted">Look up a customer to see their documents.</p>)}
        </div>
      </div>
      <Audit />
      {lookup && <CustomerLookup onClose={() => setLookup(false)} onPick={i => { setId(i); setLookup(false) }} />}
    </>
  )
}
