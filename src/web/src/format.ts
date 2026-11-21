// Instants are UTC from the API and shown in the user's zone; plain dates (YYYY-MM-DD) are never shifted.
export const money = (n: number) => n.toLocaleString('en-US', { style: 'currency', currency: 'USD' })
export const instant = (iso: string, tz: string) =>
  new Date(iso.endsWith('Z') ? iso : iso + 'Z').toLocaleString('en-US', { timeZone: tz, dateStyle: 'medium', timeStyle: 'short' })
export const plainDate = (d: string) => {
  const [y, m, day] = d.split('-').map(Number)
  return new Date(Date.UTC(y, m - 1, day)).toLocaleDateString('en-US', { timeZone: 'UTC', dateStyle: 'medium' })
}
