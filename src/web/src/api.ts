// Thin API client. Access token lives in memory only; the refresh token in sessionStorage (cleared when the tab
// closes). Both are opaque, revocable per device. A 401 triggers one refresh-and-retry.
export interface UserInfo { id: number; code: string; name: string | null; timeZone: string }
interface TokenResponse { accessToken: string; refreshToken: string; expiresInSeconds: number; user: UserInfo }
export interface Me { id: number; code: string; name: string | null; timeZone: string; userTimeZone: string | null; companyTimeZone: string; isAdmin: boolean; permissions: string[]; groups: string[] }
/** True when the signed-in user may do ANY of the permissions ('*' = Administrators, may do everything). */
export const can = (me: Me, ...perms: string[]) => me.permissions.includes('*') || perms.some(p => me.permissions.includes(p))
export interface SessionInfo { id: string; deviceName: string; platform: string; createdAt: string; lastSeenAt: string; current: boolean }
export interface CustomerSummary { id: number; number: string; lastName: string; firstName: string | null; city: string | null; state: string | null; phone: string | null; balance: number }
export interface CustomerPhone { slot: number; number: string; ext: string | null }
export interface CustomerDetail extends CustomerSummary {
  type: string; address1: string | null; address2: string | null; zip: string | null; email: string | null
  contactName: string | null; taxGroup: string | null; taxExempt: boolean; taxId: string | null
  priceCategory: string | null; category: string | null; printStatement: boolean; noPrices: boolean
  creditLimit: number | null; lastTransactionOn: string | null; notePrinted: string | null; noteInternal: string | null
  phones: CustomerPhone[]; createdAt: string; updatedAt: string
}
export interface CustomerInput {
  number: string; type: string; lastName: string; firstName: string; address1: string; address2: string; city: string; state: string; zip: string
  email: string; contactName: string; taxGroup: string; taxExempt: boolean; taxId: string; priceCategory: string; category: string
  printStatement: boolean; noPrices: boolean; creditLimit: number | null; notePrinted: string; noteInternal: string
  phones: CustomerPhone[]; updatedAt?: string
}
export interface CodeName { code: string; description: string | null }
export interface CustomerLookups { priceCategories: CodeName[]; customerCategories: CodeName[]; taxGroups: CodeName[]; types: string[] }
export interface Page<T> { items: T[]; limit: number; offset: number; hasMore: boolean }

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message) }
}

const RT = 'rp.refresh'
let accessToken: string | null = null
let onSignedOut: () => void = () => {}
export const setSignedOutHandler = (f: () => void) => { onSignedOut = f }
export const hasStoredSession = () => sessionStorage.getItem(RT) !== null

async function raw(path: string, init: RequestInit = {}, auth = true): Promise<Response> {
  const headers = new Headers(init.headers)
  if (init.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  if (auth && accessToken) headers.set('Authorization', `Bearer ${accessToken}`)
  return fetch(`/api/v1${path}`, { ...init, headers })
}

async function errorOf(r: Response): Promise<ApiError> {
  let msg = r.statusText
  try { msg = (await r.json()).error ?? msg } catch { /* not JSON */ }
  return new ApiError(r.status, msg)
}

function store(t: TokenResponse) { accessToken = t.accessToken; sessionStorage.setItem(RT, t.refreshToken) }

let refreshing: Promise<boolean> | null = null
export function refresh(): Promise<boolean> {
  refreshing ??= (async () => {
    const rt = sessionStorage.getItem(RT)
    if (!rt) return false
    const r = await raw('/auth/refresh', { method: 'POST', body: JSON.stringify({ refreshToken: rt }) }, false)
    if (!r.ok) { accessToken = null; sessionStorage.removeItem(RT); return false }
    store(await r.json()); return true
  })().finally(() => { refreshing = null })
  return refreshing
}

async function call<T>(path: string, init: RequestInit = {}): Promise<T> {
  let r = await raw(path, init)
  if (r.status === 401 && await refresh()) r = await raw(path, init)
  if (r.status === 401) { onSignedOut(); throw await errorOf(r) }
  if (!r.ok) throw await errorOf(r)
  return r.status === 204 ? (undefined as T) : r.json()
}

export async function login(userCode: string, password: string): Promise<UserInfo> {
  const deviceName = `Web browser (${navigator.platform || 'unknown'})`
  const r = await raw('/auth/login', { method: 'POST', body: JSON.stringify({ userCode, password, deviceName, platform: 'web' }) }, false)
  if (!r.ok) throw await errorOf(r)
  const t: TokenResponse = await r.json(); store(t); return t.user
}
export async function logout() {
  try { await raw('/auth/logout', { method: 'POST' }) } finally { accessToken = null; sessionStorage.removeItem(RT) }
}

export const getMe = () => call<Me>('/me')
export const setTimeZone = (timeZone: string | null) => call<void>('/me/settings', { method: 'PUT', body: JSON.stringify({ timeZone }) })
export const getTimeZones = () => call<string[]>('/timezones')
export const getSessions = () => call<SessionInfo[]>('/me/sessions')
export const revokeSession = (id: string) => call<void>(`/me/sessions/${id}`, { method: 'DELETE' })
export const searchCustomers = (q: string, offset = 0, limit = 25, sort: 'name' | 'code' = 'name') =>
  call<Page<CustomerSummary>>(`/customers?q=${encodeURIComponent(q)}&sort=${sort}&limit=${limit}&offset=${offset}`)
export const getCustomer = (id: number) => call<CustomerDetail>(`/customers/${id}`)
export const createCustomer = (c: CustomerInput) => call<{ id: number }>('/customers', { method: 'POST', body: JSON.stringify(c) })
export const updateCustomer = (id: number, c: CustomerInput) => call<void>(`/customers/${id}`, { method: 'PUT', body: JSON.stringify(c) })
export const deleteCustomer = (id: number) => call<void>(`/customers/${id}`, { method: 'DELETE' })
export const getCustomerLookups = () => call<CustomerLookups>('/lookups/customer')

export interface DocumentSummary { id: number; type: string; number: string; date: string; customerId: number | null; customerName: string | null; poNumber: string | null; voided: boolean; total: number }
export interface DocumentLine { seq: number; parentSeq: number | null; source: string; qty: number | null; partCode: string | null; description: string | null; dims: string | null; shape: string | null; thickness: string | null; unitPrice: number | null; listPrice: number | null; multiplier: number; amount: number; taxable: boolean
  thickNum: number | null; thickDen: number | null; widthIn: number | null; heightIn: number | null; partId: number | null; units: number | null; specialPrice: boolean; amountOverride: boolean }
export interface DocumentParty { name: string | null; address1: string | null; address2: string | null; city: string | null; state: string | null; zip: string | null; phones: string[] }
export interface DocumentDetail {
  id: number; type: string; typeName: string; number: string; date: string; lossDate: string | null; voided: boolean
  customerId: number | null; billToCustomerId: number | null; customer: DocumentParty; jobSite: DocumentParty | null
  poNumber: string | null; policyNumber: string | null; claimNumber: string | null; sourceDocumentNumber: string | null
  terms: string | null; salesPerson: string | null; taxGroup: string | null; priceCategory: string | null; discountPercent: number | null
  specialInstructions: string | null; workOrderInstructions: string | null; notePrinted: string | null; noteInternal: string | null
  createdAt: string; printedAt: string | null
  totals: { lines: number; taxable: number; tax: number; total: number; glass: number; part: number; labor: number; custom: number }
  lines: DocumentLine[]; taxes: { code: string; percent: number; amount: number }[]
  installers: { code: string; name: string | null; hours: number | null; amount: number | null }[]
  statuses: { at: string | null; status: string; note: string | null }[]; revisions: { at: string; total: number | null; comment: string | null }[]
  shopOrMobile: string | null; updatedAt: string; salesPersonId: number | null; editable: boolean; balance: number; paid: number
}
export interface DocumentQuery { type?: string; customerId?: number; q?: string; from?: string; to?: string; includeVoided?: boolean; offset?: number; limit?: number }
export const searchDocuments = (f: DocumentQuery) => {
  const p = new URLSearchParams()
  if (f.type) p.set('type', f.type)
  if (f.customerId) p.set('customerId', String(f.customerId))
  if (f.q) p.set('q', f.q)
  if (f.from) p.set('from', f.from)
  if (f.to) p.set('to', f.to)
  if (f.includeVoided) p.set('includeVoided', 'true')
  p.set('limit', String(f.limit ?? 25)); p.set('offset', String(f.offset ?? 0))
  return call<Page<DocumentSummary>>(`/documents?${p}`)
}
export const getDocument = (id: number) => call<DocumentDetail>(`/documents/${id}`)

// Quote entry: the server prices everything; the client only sends what the user typed.
export interface DraftLine { source: string; qty: number | null; partId: number | null; thickNum: number | null; thickDen: number | null; widthIn: number | null; heightIn: number | null
  units: number | null; priceLevel: number | null; rate: number | null; unitPrice: number | null; amount: number | null; description: string | null; taxable: boolean | null }
export interface DraftDocument { customerId: number | null; priceCategory: string | null; taxGroup: string | null; discountPercent: number | null; lines: DraftLine[] }
export interface PricedLine { seq: number; source: string; qty: number; partCode: string | null; description: string | null; thickness: string | null; dims: string | null
  sqFt: number | null; rate: number | null; unitPrice: number | null; amount: number; taxable: boolean; specialPrice: boolean; warnings: string[] }
export interface CalcResult { lines: PricedLine[]; taxes: { code: string; percent: number; amount: number }[]; subtotal: number; tax: number; total: number; priceCategory: string | null; taxGroup: string | null; warnings: string[] }
export interface DocumentInput extends DraftDocument {
  date: string | null; poNumber: string | null; salesPersonId: number | null; terms: string | null; shopOrMobile: string | null
  specialInstructions: string | null; workOrderInstructions: string | null; notePrinted: string | null; noteInternal: string | null
  jobSite: DocumentParty | null; lossDate: string | null; policyNumber: string | null; claimNumber: string | null; updatedAt: string | null
}
export interface CatalogPart { id: number; code: string; description: string | null; source: string; uomId: number | null; thicknesses: { num: number; den: number; text: string }[] }
export const calculateDocument = (d: DraftDocument) => call<CalcResult>('/documents/calculate', { method: 'POST', body: JSON.stringify(d) })
export const createDocument = (d: DocumentInput) => call<{ id: number; number: string; total: number }>('/documents', { method: 'POST', body: JSON.stringify(d) })
export const updateDocument = (id: number, d: DocumentInput) => call<{ total: number }>(`/documents/${id}`, { method: 'PUT', body: JSON.stringify(d) })
export const voidDocument = (id: number) => call<void>(`/documents/${id}/void`, { method: 'POST' })
export const getCatalogParts = (q: string, source: 'G' | 'P') => call<CatalogPart[]>(`/catalog/parts?q=${encodeURIComponent(q)}&source=${source}`)

export interface TaxCode { code: string; description: string | null; percent: number; taxesLabor: boolean; nonCalc: boolean }
export interface TaxGroup { code: string; description: string | null; codes: { position: number; code: string; description: string | null; percent: number }[] }
export const getTaxCodes = () => call<TaxCode[]>('/tax-codes')
export const getTaxGroups = () => call<TaxGroup[]>('/tax-groups')
export interface TaxGroupInput { code: string; description: string; codes: { position: number; code: string }[] }
export const createTaxGroup = (g: TaxGroupInput) => call<{ code: string }>('/tax-groups', { method: 'POST', body: JSON.stringify(g) })
export const updateTaxGroup = (code: string, g: TaxGroupInput) => call<void>(`/tax-groups/${encodeURIComponent(code)}`, { method: 'PUT', body: JSON.stringify(g) })
export const deleteTaxGroup = (code: string) => call<void>(`/tax-groups/${encodeURIComponent(code)}`, { method: 'DELETE' })

// Small reference tables (tax codes, categories, salesmen/installers, statuses): one generic editor.
export interface MaintCol { api: string; db: string; label: string; type: 'Text' | 'Bool' | 'Decimal' | 0 | 1 | 2; required: boolean; max: number; readOnly: boolean }
export interface MaintTable { key: string; title: string; note: string | null; keyColumn: MaintCol; keyIdentity: boolean; cols: MaintCol[]; protected: boolean }
export type MaintRow = Record<string, string | number | boolean | null>
export const getMaintTables = () => call<MaintTable[]>('/maintenance')
export const getMaintRows = (t: string) => call<MaintRow[]>(`/maintenance/${t}`)
export const createMaintRow = (t: string, row: MaintRow) => call<{ key: string }>(`/maintenance/${t}`, { method: 'POST', body: JSON.stringify(row) })
export const updateMaintRow = (t: string, key: string, row: MaintRow) => call<void>(`/maintenance/${t}/${encodeURIComponent(key)}`, { method: 'PUT', body: JSON.stringify(row) })
export const deleteMaintRow = (t: string, key: string) => call<void>(`/maintenance/${t}/${encodeURIComponent(key)}`, { method: 'DELETE' })

// ---- supplier pricing (admin only) ----
export interface Supplier {
  id: number; name: string; active: boolean; sourceType: 'manual' | 'upload' | 'feed'; feedUrl: string | null; hasAuthHeader: boolean
  feedFormat: 'csv' | 'json'; columnMap: { sku?: string; cost?: string; description?: string }; refreshHours: number | null
  lastRunAt: string | null; lastStatus: string | null; lastMessage: string | null; itemCount: number; linkedCount: number
}
export interface SupplierInput { name: string; active: boolean; sourceType: string; feedUrl: string | null; feedAuthHeader: string | null; feedFormat: string; columnMap: object; refreshHours: number | null }
export interface SupplierItem { id: number; sku: string; description: string | null; unitCost: number; costUpdatedAt: string; links: number }
export interface IngestResult { added: number; changed: number; unchanged: number; batchId: number | null; priceChanges: number }
export interface CostLink { thicknessId: number; partCode: string; partDescription: string | null; thickNum: number; thickDen: number; supplierItemId: number; sku: string; unitCost: number; factor: number; supplierId: number }
export interface ThicknessHit { thicknessId: number; partCode: string; partDescription: string | null; thickNum: number; thickDen: number; linked: boolean }
export interface Markup { code: string; description: string | null; markupPct: number | null; linkedPrices: number }
export interface Batch { id: number; createdAt: string; source: string; status: string; decidedAt: string | null; supplier: string | null; decidedBy: string | null; changeCount: number }
export interface BatchChange { partCode: string; partDescription: string | null; thickNum: number; thickDen: number; category: string | null; oldPrice: number | null; newPrice: number; cost: number; markupPct: number }
const J = (b: unknown): RequestInit => ({ body: JSON.stringify(b) })
export const getSuppliers = () => call<Supplier[]>('/pricing/suppliers')
export const saveSupplier = (id: number | null, i: SupplierInput) => call<{ id: number }>(id ? `/pricing/suppliers/${id}` : '/pricing/suppliers', { method: id ? 'PUT' : 'POST', ...J(i) })
export const deleteSupplier = (id: number) => call<void>(`/pricing/suppliers/${id}`, { method: 'DELETE' })
export const getSupplierItems = (id: number, q = '') => call<SupplierItem[]>(`/pricing/suppliers/${id}/items?q=${encodeURIComponent(q)}`)
export const postCosts = (id: number, rows: { sku: string; description: string | null; cost: number }[]) => call<IngestResult>(`/pricing/suppliers/${id}/costs`, { method: 'POST', ...J({ rows }) })
export const adjustCosts = (id: number, percent: number) => call<IngestResult>(`/pricing/suppliers/${id}/adjust`, { method: 'POST', ...J({ percent }) })
export const importCosts = (id: number, content: string) => call<IngestResult>(`/pricing/suppliers/${id}/import`, { method: 'POST', ...J({ content }) })
export const runFeed = (id: number) => call<IngestResult>(`/pricing/suppliers/${id}/run`, { method: 'POST' })
export const getLinks = () => call<CostLink[]>('/pricing/links')
export const searchThicknesses = (q: string) => call<ThicknessHit[]>(`/pricing/thicknesses?q=${encodeURIComponent(q)}`)
export const putLink = (thicknessId: number, supplierItemId: number, factor: number) => call<void>(`/pricing/links/${thicknessId}`, { method: 'PUT', ...J({ supplierItemId, factor }) })
export const deleteLink = (thicknessId: number) => call<void>(`/pricing/links/${thicknessId}`, { method: 'DELETE' })
export const getMarkups = () => call<Markup[]>('/pricing/markups')
export const putMarkup = (code: string, markupPct: number | null) => call<IngestResult>(`/pricing/markups/${encodeURIComponent(code)}`, { method: 'PUT', ...J({ markupPct }) })
export const recalculate = () => call<IngestResult>('/pricing/recalculate', { method: 'POST' })
export const getBatches = (status = '') => call<Batch[]>(`/pricing/batches?status=${status}`)
export const getBatch = (id: number) => call<BatchChange[]>(`/pricing/batches/${id}`)
export const decideBatch = (id: number, approve: boolean) => call<{ status: string; pricesUpdated: number }>(`/pricing/batches/${id}/${approve ? 'approve' : 'reject'}`, { method: 'POST' })

// ---- database export / import (admin only) ----
export interface TransferTable { table: string; inserted: number; updated: number; skipped: number; deleted: number }
export interface TransferResult { mode: string; overwrite: boolean; dryRun: boolean; applied: boolean; inserted: number; updated: number; skipped: number; deleted: number; tables: TransferTable[] }
// Database transfer. Big files never pass through the page's memory: the server writes and reads files in its transfer folder as background jobs,
// the browser downloads finished files with its own download manager (resumable) and uploads from disk.
export interface TransferFile { name: string; size: number; modified: string }
export interface TransferFiles { directory: string; freeBytes: number; files: TransferFile[]; partial: { name: string; size: number }[] }
export interface TransferJob { id: string; kind: 'export' | 'import'; state: 'running' | 'done' | 'error'; message: string | null; file: string | null; bytes: number; total: number; result: TransferResult | null }
export const getTransferFiles = () => call<TransferFiles>('/admin/transfer/files')
export const deleteTransferFile = (name: string) => call<void>(`/admin/transfer/files/${encodeURIComponent(name)}`, { method: 'DELETE' })
export const startExportJob = (attachments: boolean) => call<{ id: string }>('/admin/transfer/export', { method: 'POST', body: JSON.stringify({ attachments }) })
export const startImportJob = (file: string, mode: 'merge' | 'replace', overwrite: boolean, dryRun: boolean) =>
  call<{ id: string }>('/admin/transfer/import', { method: 'POST', body: JSON.stringify({ file, mode, overwrite, dryRun }) })
export const getJob = (id: string) => call<TransferJob>(`/admin/transfer/jobs/${id}`)
/** Starts a native browser download (straight to disk, resumable for saved files). `file` omitted = a live export. */
export async function downloadViaBrowser(opts: { file?: string; attachments?: boolean }) {
  const { url } = await call<{ url: string }>('/admin/transfer/ticket', { method: 'POST', body: JSON.stringify({ file: opts.file ?? null, attachments: opts.attachments ?? false }) })
  const a = document.createElement('a'); a.href = url; a.rel = 'noopener'; document.body.appendChild(a); a.click(); a.remove()
}
/** Streams a file from disk to the server's transfer folder with progress (XMLHttpRequest, because fetch cannot report upload progress). */
export async function uploadTransferFile(file: File, onProgress: (sent: number, total: number) => void): Promise<void> {
  await call('/me')   // makes sure the access token is fresh before a long upload
  return new Promise((resolve, reject) => {
    const x = new XMLHttpRequest()
    x.open('PUT', `/api/v1/admin/transfer/files/${encodeURIComponent(file.name)}`)
    if (accessToken) x.setRequestHeader('Authorization', `Bearer ${accessToken}`)
    x.upload.onprogress = e => e.lengthComputable && onProgress(e.loaded, e.total)
    x.onload = () => { if (x.status >= 200 && x.status < 300) resolve(); else { let m = `Upload failed (${x.status}).`; try { m = JSON.parse(x.responseText).error ?? m } catch { /* keep */ } reject(new ApiError(x.status, m)) } }
    x.onerror = () => reject(new ApiError(0, 'The upload was interrupted. Check the connection and try again, or copy the file into the transfer folder on the server instead.'))
    x.send(file)
  })
}

// Work orders, invoices, credit memos
export const convertDocument = (id: number, to: 'W' | 'I' | 'C', opts: { lineSeqs?: number[]; force?: boolean } = {}) =>
  call<{ id: number; number: string; type: string; total: number }>(`/documents/${id}/convert`, { method: 'POST', body: JSON.stringify({ to, ...opts }) })
export const markPrinted = (id: number) => call<void>(`/documents/${id}/printed`, { method: 'POST' })

// Receivables and payments
export interface ReceivableCustomer { id: number; number: string; name: string; balance: number; lastActivity: string | null }
export interface OpenDocument { id: number; type: string; number: string; date: string; poNumber: string | null; voided: boolean; total: number; paid: number; balance: number }
export interface PaymentRow { id: number; paidAt: string; customerId: number; customerName: string | null; amount: number; type: string; category: string | null; description: string | null; voided: boolean; applied: number }
export interface Account { customerId: number; number: string; name: string; balance: number; onAccount: number; documents: OpenDocument[]; payments: PaymentRow[] }
export interface PaymentInput { customerId: number; amount: number; type: 'P' | 'D'; category: string | null; description: string | null; applications: { documentId: number; amount: number }[]; autoApply?: boolean }
export const getReceivables = (q: string, credit: boolean) => call<ReceivableCustomer[]>(`/receivables?${new URLSearchParams({ q, credit: String(credit) })}`)
export const getAccount = (customerId: number) => call<Account>(`/customers/${customerId}/account`)
export const createPayment = (p: PaymentInput) => call<{ id: number; applied: number; onAccount: number }>('/payments', { method: 'POST', body: JSON.stringify(p) })
export const voidPayment = (id: number) => call<void>(`/payments/${id}/void`, { method: 'POST' })

export interface CompanyInfo { id: number; name: string; address1: string | null; address2: string | null; city: string | null; state: string | null; zip: string | null; phone: string | null; fax: string | null; email: string | null; invoiceNote: string | null; disclaimer: string | null; useDisclaimer: boolean; hasLogo?: boolean }
export const getCompany = () => call<CompanyInfo>('/company')

/** The company logo as a data: URL (so printed pages and the print preview need no sign-in), or null when none is set. */
export async function getLogoDataUrl(): Promise<string | null> {
  let r = await raw('/company/logo')
  if (r.status === 401 && await refresh()) r = await raw('/company/logo')
  if (!r.ok) return null
  const blob = await r.blob()
  return new Promise(res => { const f = new FileReader(); f.onload = () => res(f.result as string); f.onerror = () => res(null); f.readAsDataURL(blob) })
}
export async function putLogo(file: File) {
  const send = () => raw('/company/logo', { method: 'PUT', body: file, headers: { 'Content-Type': file.type } })
  let r = await send(); if (r.status === 401 && await refresh()) r = await send()
  if (!r.ok) throw await errorOf(r)
}
export const deleteLogo = () => call<void>('/company/logo', { method: 'DELETE' })

// Parts and pricing catalog
export interface PriceRow { category: string | null; price1: number | null; price2: number | null; price3: number | null; price4: number | null; minPrice: number | null; laborPrice: number | null; minLabor: number | null; taxable: boolean }
export interface ThickRow { num: number; den: number; markupCategory: string | null; prices: PriceRow[] }
export interface ItemSummary { id: number; code: string; description: string | null; source: string; thicknesses: number; unit: string | null; thicknessList: string | null; price: number | null }
export interface ItemInput { code: string; source: string; description: string | null; uomId: number | null; laborUomId: number | null; notePrinted: string | null; noteInternal: string | null; thicknesses: ThickRow[]; updatedAt: string | null }
export interface ItemDetail extends ItemInput { id: number; updatedAt: string }
export const getItems = (q: string, source: 'G' | 'P') => call<ItemSummary[]>(`/catalog/items?${new URLSearchParams({ q, source })}`)
export const getItem = (id: number) => call<ItemDetail>(`/catalog/items/${id}`)
export const createItem = (i: ItemInput) => call<{ id: number }>('/catalog/items', { method: 'POST', body: JSON.stringify(i) })
export const updateItem = (id: number, i: ItemInput) => call<void>(`/catalog/items/${id}`, { method: 'PUT', body: JSON.stringify(i) })
export const deleteItem = (id: number) => call<void>(`/catalog/items/${id}`, { method: 'DELETE' })
export const getUoms = () => call<{ id: number; name: string; shortName: string | null }[]>('/catalog/uoms')
export interface LossRow { thickNum: number; thickDen: number; lossInt: number; lossNum: number; lossDen: number }
export const getLoss = () => call<LossRow[]>('/catalog/loss')
export const putLoss = (rows: LossRow[]) => call<void>('/catalog/loss', { method: 'PUT', body: JSON.stringify(rows) })
export interface SpecialPrice { partId: number; partCode: string; thickNum: number; thickDen: number; priceCategory: string | null; price: number }
export const getSpecialPrices = (customerId: number) => call<SpecialPrice[]>(`/customers/${customerId}/prices`)
export const putSpecialPrices = (customerId: number, rows: Omit<SpecialPrice, 'partCode'>[]) => call<void>(`/customers/${customerId}/prices`, { method: 'PUT', body: JSON.stringify(rows) })

// Company, users, utilities
export const putCompany = (c: CompanyInfo & { timeZone?: string | null }) => call<void>('/company', { method: 'PUT', body: JSON.stringify(c) })
export interface UserRow { id: number; code: string; name: string | null; active: boolean; isAdmin: boolean; hasPassword: boolean; lockedUntil: string | null; sessions: number; groupIds: number[] }
export const getUsers = () => call<UserRow[]>('/users')
export const createUser = (u: { code: string; name: string; password: string; groupIds: number[] }) => call<{ id: number }>('/users', { method: 'POST', body: JSON.stringify({ ...u, active: true }) })
export const updateUser = (id: number, u: { name: string | null; active: boolean; groupIds: number[] }) => call<void>(`/users/${id}`, { method: 'PUT', body: JSON.stringify(u) })
export const resetUserPassword = (id: number, password: string) => call<void>(`/users/${id}/password`, { method: 'POST', body: JSON.stringify({ password }) })
export const changeOwnPassword = (current: string, next: string) => call<void>('/me/password', { method: 'POST', body: JSON.stringify({ current, new: next }) })
export const cleanup = (what: 'delete-quotes' | 'delete-inactive-customers', before: string | null, dryRun: boolean) =>
  call<{ count: number; deleted: boolean }>(`/utilities/${what}`, { method: 'POST', body: JSON.stringify({ before, dryRun }) })
export const applyCredits = (customerId: number) => call<{ applied: number }>(`/customers/${customerId}/apply-credits`, { method: 'POST' })

// Reports
export interface ReportColumn { key: string; label: string; type: 'text' | 'money' | 'date' | 'int' | 'number'; total: boolean }
export interface ReportDef { id: string; title: string; group: string; params: string[]; columns: ReportColumn[]; note: string | null }
export interface ReportResult { id: string; title: string; columns: ReportColumn[]; rows: (string | number | null)[][]; truncated: boolean }
export const getReportDefs = () => call<ReportDef[]>('/reports')
export const runReport = (id: string, p: { from?: string; to?: string; asOf?: string; customerId?: number | null; type?: string }) => {
  const q = new URLSearchParams(); if (p.from) q.set('from', p.from); if (p.to) q.set('to', p.to); if (p.asOf) q.set('asOf', p.asOf)
  if (p.customerId) q.set('customerId', String(p.customerId)); if (p.type) q.set('type', p.type)
  return call<ReportResult>(`/reports/${id}?${q}`)
}

// Groups (roles) and permissions
export interface PermissionDef { key: string; area: string; label: string }
export interface GroupRow { id: number; code: string; name: string; description: string | null; isSystem: boolean; locked: boolean; permissions: string[]; members: number }
export const getPermissions = () => call<PermissionDef[]>('/permissions')
export const getGroups = () => call<GroupRow[]>('/groups')
export const createGroup = (g: { name: string; description: string | null; permissions: string[] }) => call<{ id: number }>('/groups', { method: 'POST', body: JSON.stringify(g) })
export const updateGroup = (id: number, g: { name: string; description: string | null; permissions: string[] }) => call<void>(`/groups/${id}`, { method: 'PUT', body: JSON.stringify(g) })
export const deleteGroup = (id: number) => call<void>(`/groups/${id}`, { method: 'DELETE' })

// API clients (OAuth client credentials for other software)
export interface ApiClient { id: string; clientId: string; name: string; note: string | null; active: boolean; groupIds: number[]; createdAt: string; secretRotatedAt: string; expiresAt: string | null; lastUsedAt: string | null; tokens: number }
export interface ApiClientInput { name: string; note: string | null; groupIds: number[]; active: boolean; expiresAt: string | null }
export const getApiClients = () => call<ApiClient[]>('/api-clients')
export const createApiClient = (c: ApiClientInput) => call<{ id: string; clientId: string; clientSecret: string }>('/api-clients', { method: 'POST', body: JSON.stringify(c) })
export const updateApiClient = (id: string, c: ApiClientInput) => call<void>(`/api-clients/${id}`, { method: 'PUT', body: JSON.stringify(c) })
export const rotateApiClientSecret = (id: string) => call<{ clientSecret: string }>(`/api-clients/${id}/secret`, { method: 'POST' })
export const deleteApiClient = (id: string) => call<void>(`/api-clients/${id}`, { method: 'DELETE' })

// Updates (administrators)
export interface UpdateStatus { mode: 'docker' | 'lxc' | 'other'; current: string | null; currentShort: string | null; latest: string | null; latestShort: string | null; latestMessage: string | null; latestDate: string | null; latestUrl: string | null
  available: boolean; canCompare: boolean; commitsBehind: number | null; checkedAt: string | null; error: string | null; dismissed: boolean; enabled: boolean; repository: string; branch: string; commands: string[]
  updaterPresent: boolean; staged: { sha: string; short: string; message: string | null; commits: string[]; at: number } | null; ready: boolean; jobState: string | null; jobVersion: string | null }
export const getUpdateStatus = () => call<UpdateStatus>('/updates')
export const checkForUpdates = () => call<UpdateStatus>('/updates/check', { method: 'POST' })
export const applyUpdate = (backup: boolean) => call<{ progressUrl: string }>('/updates/apply', { method: 'POST', body: JSON.stringify({ backup }) })
export const dismissUpdate = () => call<void>('/updates/dismiss', { method: 'POST' })

// ---- Accounting (QuickBooks Desktop push, IIF and Sage 50 / Peachtree files) ----
export interface AccountingConfig {
  target: 'off' | 'qbd'; since?: string | null; customerNaming?: string | null; incomeAccount?: string | null; taxAgency?: string | null
  arAccount?: string | null; taxPayableAccount?: string | null; undepositedAccount?: string | null; depositAccount?: string | null; terms?: string | null
  sendPaymentMethod: boolean; items: Record<string, string>; taxItems: Record<string, string>; taxGroupItems: Record<string, string>
  qbwcUser?: string | null; qbwcPassword?: string | null; publicUrl?: string | null; runEveryMinutes?: number | null
}
export interface AccountingOverview {
  config: AccountingConfig
  effective: { income: string; agency: string; ar: string; taxPayable: string; undeposited: string; user: string; every: number; naming: string; since: string }
  pending: { documents: number; payments: number; voids: number }; sent: number; errors: number; skipped: number
  last: { at: string; sent: number; errors: number; message: string | null; finished: boolean } | null
}
export interface AccountingRow { kind: string; refId: number; label: string; party: string | null; date: string | null; amount: number | null; status: string; note: string | null; sentAt: string | null }
export const getAccounting = () => call<AccountingOverview>('/accounting')
export const saveAccounting = (c: AccountingConfig) => call<AccountingConfig>('/accounting/config', { method: 'PUT', body: JSON.stringify(c) })
export const newAccountingPassword = () => call<{ password: string }>('/accounting/qbwc/password', { method: 'POST' })
export const getAccountingQueue = (status: string) => call<AccountingRow[]>(`/accounting/queue?status=${status}`)
export const retryAccounting = (kind?: string, refId?: number) => call<{ cleared: number }>('/accounting/retry', { method: 'POST', body: JSON.stringify({ kind, refId }) })
export const skipAccounting = (kind: string, refId: number) => call<void>('/accounting/skip', { method: 'POST', body: JSON.stringify({ kind, refId }) })
/** Fetches a file from the API with the sign-in token and offers it as a download. */
export async function downloadAccounting(path: string, fallbackName: string) {
  let r = await raw(path); if (r.status === 401 && await refresh()) r = await raw(path)
  if (!r.ok) throw await errorOf(r)
  const name = /filename="?([^";]+)/.exec(r.headers.get('Content-Disposition') ?? '')?.[1] ?? fallbackName
  const url = URL.createObjectURL(await r.blob()); const a = document.createElement('a'); a.href = url; a.download = name; a.click(); setTimeout(() => URL.revokeObjectURL(url), 5000)
}

// ---- Inventory and purchasing ----
export interface SupplierStockInfo { qty: number | null; status: 'in_stock' | 'out' | 'unknown'; note: string | null; checkedAt: string }
export interface InvItem { id: number; supplierId: number; supplier: string; sku: string; description: string | null; unitCost: number; unit: string; reorderPoint: number; orderMultiple: number; leadDays: number; location: string | null; active: boolean; onHand: number; committed: number; quoted: number; onOrder: number; perDay: number; available: number; suggest: number; supplierStock: SupplierStockInfo | null }
export interface InvNeed { id: number; supplierId: number; supplier: string; sku: string; description: string | null; unit: string; unitCost: number; onHand: number; committed: number; quoted: number; onOrder: number; perDay: number; reorderPoint: number; suggest: number; cost: number; supplierStock: SupplierStockInfo | null }
export interface InvCatalogRow { supplierItemId: number; supplier: string; sku: string; description: string | null; unitCost: number; links: number; tracked: boolean; unit: string | null }
export interface InvMove { at: string; qty: number; kind: string; note: string | null; user: string | null; document: string | null; poId: number | null }
export interface InvSupplier { id: number; name: string; email: string | null; phone: string | null; orderNotes: string | null; active: boolean; items: number; availMode: 'none' | 'api' | 'scrape'; availLastAt: string | null; availStatus: string | null }
export interface InvImportRow { sku: string; description?: string | null; cost?: number | null; unit?: string | null; onHand?: number | null; reorderPoint?: number | null; orderMultiple?: number | null }
export interface InvImportResult { added: number; changed: number; unchanged: number; tracked: number; skipped: number; problems: string[]; priceChanges: number }
export interface PoRow { id: number; supplier: string; status: string; createdAt: string; sentAt: string | null; expectedDate: string | null; lines: number; total: number }
export interface PoLine { id: number; itemId: number; sku: string; description: string | null; unit: string; qty: number; unitCost: number; received: number }
export interface PoDetail { order: { id: number; status: string; createdAt: string; sentAt: string | null; expectedDate: string | null; note: string | null; supplierId: number; supplier: string; supplierEmail: string | null; supplierPhone: string | null; supplierNotes: string | null; company: string | null; companyPhone: string | null }; lines: PoLine[] }
export interface StockCheck { on: boolean; type?: string; lines: { sku: string; description: string | null; unit: string; need: number; onHand: number; committed: number; onOrder: number; shortBy: number }[] }
const JB = (m: string, b?: unknown) => ({ method: m, ...(b === undefined ? {} : { body: JSON.stringify(b) }) })
export const getInventoryOn = () => call<{ on: boolean }>('/inventory/status').then(r => r.on)
export const setInventoryOn = (on: boolean) => call<{ on: boolean }>('/inventory/status', JB('PUT', { on }))
export const getInvItems = () => call<InvItem[]>('/inventory/items')
export const getInvCatalog = (q: string, supplierId?: number) => call<InvCatalogRow[]>(`/inventory/catalog?q=${encodeURIComponent(q)}${supplierId ? `&supplierId=${supplierId}` : ''}`)
export const addInvItem = (b: object) => call<{ id: number }>('/inventory/items', JB('POST', b))
export const editInvItem = (id: number, b: object) => call<void>(`/inventory/items/${id}`, JB('PUT', b))
export const adjustInvItem = (id: number, b: { qty?: number; setTo?: number; note?: string }) => call<void>(`/inventory/items/${id}/adjust`, JB('POST', b))
export const getInvMoves = (id: number) => call<InvMove[]>(`/inventory/items/${id}/moves`)
export const getInvNeeds = (coverDays: number) => call<{ coverDays: number; items: InvNeed[] }>(`/inventory/needs?coverDays=${coverDays}`)
export const getInvSuppliers = () => call<InvSupplier[]>('/inventory/suppliers')
export const saveInvSupplier = (id: number, b: object) => call<void>(`/inventory/suppliers/${id}`, JB('PUT', b))
export const getPos = (status?: string) => call<PoRow[]>(`/inventory/orders${status ? `?status=${status}` : ''}`)
export const getPo = (id: number) => call<PoDetail>(`/inventory/orders/${id}`)
export const createPo = (b: object) => call<{ id: number }>('/inventory/orders', JB('POST', b))
export const poFromNeeds = (supplierId: number | null, coverDays: number) => call<{ created: number[] }>('/inventory/orders/from-needs', JB('POST', { supplierId, coverDays }))
export const editPo = (id: number, b: object) => call<void>(`/inventory/orders/${id}`, JB('PUT', b))
export const deletePo = (id: number) => call<void>(`/inventory/orders/${id}`, JB('DELETE'))
export const poSent = (id: number) => call<void>(`/inventory/orders/${id}/sent`, JB('POST'))
export const poReceive = (id: number, lines: { lineId: number; qty: number }[]) => call<void>(`/inventory/orders/${id}/receive`, JB('POST', { lines }))
export const poCancel = (id: number) => call<void>(`/inventory/orders/${id}/cancel`, JB('POST'))
export const stockCheck = (docId: number) => call<StockCheck>(`/inventory/document/${docId}`)
export const addInvSupplier = (b: object) => call<{ id: number }>('/inventory/suppliers', JB('POST', b))
export const saveInvSupplierItems = (id: number, rows: InvImportRow[], track: boolean) => call<InvImportResult>(`/inventory/suppliers/${id}/items`, JB('POST', { rows, track }))

// Supplier stock availability (API or web page)
export interface AvailConfig { mode: 'none' | 'api' | 'scrape'; url: string | null; hasAuthHeader: boolean; format: 'json' | 'csv' | 'html'; map: Record<string, string>; refreshHours: number | null; perSku: boolean
  status: { lastAt: string | null; lastStatus: string | null; lastMessage: string | null; known: number } }
export interface AvailInput { mode: string; url: string; authHeader?: string; clearAuth?: boolean; format: string; map: Record<string, string>; refreshHours: number | null; sku?: string }
export interface AvailFound { sku: string | null; qty: number | null; status: 'in_stock' | 'out' | 'unknown'; note: string | null }
export interface AvailTest { sku: string | null; length: number; total?: number; found: AvailFound[]; snippet: string }
export interface AvailPoll { checked: number; inStock: number; out: number; unknown: number; message: string }
export const getAvail = (supplierId: number) => call<AvailConfig>(`/inventory/suppliers/${supplierId}/availability`)
export const saveAvail = (supplierId: number, b: AvailInput) => call<void>(`/inventory/suppliers/${supplierId}/availability`, JB('PUT', b))
export const testAvail = (supplierId: number, b: AvailInput) => call<AvailTest>(`/inventory/suppliers/${supplierId}/availability/test`, JB('POST', b))
export const pollSupplierStock = (supplierId: number) => call<AvailPoll>(`/inventory/suppliers/${supplierId}/availability/poll`, { method: 'POST' })
export const pollItemStock = (itemId: number) => call<AvailPoll>(`/inventory/items/${itemId}/poll`, { method: 'POST' })
