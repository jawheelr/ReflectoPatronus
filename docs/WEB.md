# Web console (`src/web`)

React + TypeScript (Vite). It talks only to `/api/v1`; the API serves the built files from the same origin, so there is no CORS.

- **Build:** `./scripts/build-web.sh` (Node image, no local Node needed) writes to `src/ReflectoPatronus.Api/wwwroot` (git-ignored), then `docker compose --env-file .env up -d --build api`. The LXC artifact (`scripts/build-api-artifact.sh`) picks up `wwwroot` too, so run `build-web.sh` first.
- **Dev:** `cd src/web && npm install && npm run dev` (proxies `/api` to `localhost:8080`).
- **Look and feel:** the structure follows the legacy system (menu bar File/Daily/Maintenance/Utilities/Reports/Help, Quick Launch panel, Open Windows list, several windows open at once, status bar, Save/Delete/Clear toolbar, "..." lookup buttons, Created/Updated footer) in a modern style. Source screenshots live outside the repo.
- **Windows today (all read-only):** Flat Glass Quote (one window for quotes, work orders, invoices and credits via Document Type; General/Insurance/Installation/Other/Notes tabs, line grid, totals), Customer Entry (General, Finance/Tax, Documents; phone slots labelled Cell/Work/Home/Pager/Other), customer and document lookups, Documents browser, Tax Code Entry, Tax Group Entry, Settings (time zone, signed-in devices).
- **Not built yet:** saving anything (needs the edit APIs and, for quotes, server-side pricing), Payment, Print/E-mail, Part Availability and Attachments tabs, reports, Company Information. Customer Type codes (A/B/D) are shown raw until their the legacy system labels are confirmed.
- **Sessions:** access token in memory, refresh token in `sessionStorage` (gone when the tab closes); one refresh-and-retry on 401. A strict CSP (`default-src 'self'`) is sent with the console.
- **Planned (admin only):** Backup & Restore page. It needs a server-side design first, since the API's database role is deliberately not allowed to run DDL.
