# Web console (`src/web`)

React + TypeScript (Vite). It talks only to `/api/v1`; the API serves the built files from the same origin, so there is no CORS.

- **Build:** `./scripts/build-web.sh` (Node image, no local Node needed) writes to `src/ReflectoPatronus.Api/wwwroot` (git-ignored), then `docker compose --env-file .env up -d --build api`. The LXC artifact (`scripts/build-api-artifact.sh`) picks up `wwwroot` too, so run `build-web.sh` first.
- **Dev:** `cd src/web && npm install && npm run dev` (proxies `/api` to `localhost:8080`).
- **Screens today:** sign-in, customer search and detail (with that customer's documents), documents list (type tabs, number/PO/name search, date range, voided toggle; filters live in the URL) and document detail with totals, settings (time zone picker, signed-in devices with revoke).
- **Sessions:** access token in memory, refresh token in `sessionStorage` (gone when the tab closes); one refresh-and-retry on 401. A strict CSP (`default-src 'self'`) is sent with the console.
- **Planned (admin only):** Backup & Restore page. It needs a server-side design first, since the API's database role is deliberately not allowed to run DDL.
