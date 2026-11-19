#!/usr/bin/env python3
"""GlassQuote 7 (SQL Server) -> ReflectoPatronus (PostgreSQL) one-off importer.

Design rules (see docs/DB-DESIGN.md):
  * The legacy database is only ever READ.
  * Legacy ids are preserved as primary keys (INSERT ... OVERRIDING SYSTEM VALUE, then setval).
  * Rows whose parent no longer exists (GQ7 had no foreign keys) go to import_quarantine, never silently dropped.
  * Columns the model drops are scanned: constant/empty ones are discarded and reported; a real value in a
    non-constant dropped column is preserved in that row's legacy_extra JSON. Secrets are never imported.
  * Everything runs in ONE transaction (except attachments, which commit in batches and are resumable), so
    --dry-run executes the whole import and rolls back. A reconciliation report decides the exit code.
"""
import argparse
import collections
import datetime as dt
import decimal
import hashlib
import json
import mimetypes
import os
import re
import sys
import time

import psycopg
import pymssql
from psycopg.types.json import Jsonb

SENTINEL_BEFORE = dt.datetime(1901, 1, 1)           # Delphi/Access "null date" 1899-12-30 / 1900-01-01
AUDIT_COL = re.compile(r"^(create|update)_(userid|timestamp)$")
# Never imported and never copied into legacy_extra (credentials / card data / tokens).
SECRET_COLS = {"user_password", "cmpy_smtppassword", "cmpy_qixpassword", "ent_kusttoken", "cust_creditcard"}
# Dropped on purpose: GQ7 data-conversion batch markers (values 1899-12-30, 2012-09-10, 2018-xx) and print-queue dates.
IGNORE_COLS = {"inv_transdate", "inv_reqenddate", "inv_referraldate", "inv_reqdate", "pay_transdate",
               "inv_agrss_install_time", "inv_agrss_driveaway_time"}
LINE_SOURCE = {"G": "glass", "P": "part", "LAA": "labor", "USR": "custom"}
BATCH = 5000


# ----------------------------------------------------------------------------- helpers
def log(msg):
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)


def txt(v):
    if v is None:
        return None
    v = str(v).rstrip()
    return v if v.strip() else None


def dte(v):
    if isinstance(v, dt.datetime):
        return None if v < SENTINEL_BEFORE else v.date()
    return v


def ts(v):
    if isinstance(v, dt.datetime):
        return None if v < SENTINEL_BEFORE else v
    return v


def nz(v):
    """Zero -> NULL for optional numeric columns (GQ7 used 0 for 'unset')."""
    return None if v is None or v == 0 else v


def jdump(o):
    return json.dumps(o, default=lambda x: x.isoformat() if hasattr(x, "isoformat") else str(x))


class Importer:
    def __init__(self, args):
        self.a = args
        self.stats = collections.defaultdict(lambda: collections.Counter())
        self.notes = []                      # human-readable findings for the report
        self.placeholders = collections.defaultdict(set)
        self.known = collections.defaultdict(set)   # lookup-table code sets
        self.users = {}
        self.src = pymssql.connect(server=args.mssql_host, port=args.mssql_port, user=args.mssql_user,
                                   password=os.environ["MSSQL_PASSWORD"], database=args.mssql_db,
                                   charset="CP1252", login_timeout=30, timeout=0)
        self.pg = psycopg.connect("")        # libpq env: PGHOST/PGUSER/PGPASSWORD/PGSSL*
        self.cur = self.pg.cursor()
        self.cur.execute("SELECT set_config('TimeZone', %s, false)", (args.tz,))
        self.quarantine_rows = []

    # --- source access
    def q(self, sql, params=None):
        c = self.src.cursor(as_dict=True)
        c.execute(sql, params or ())
        while True:
            rows = c.fetchmany(BATCH)
            if not rows:
                break
            for r in rows:
                yield {k.lower(): v for k, v in r.items()}
        c.close()

    def scalar(self, sql):
        c = self.src.cursor()
        c.execute(sql)
        v = c.fetchone()[0]
        c.close()
        return v

    def src_columns(self, table):
        return [(r["column_name"].lower(), r["data_type"]) for r in
                self.q("SELECT COLUMN_NAME column_name, DATA_TYPE data_type FROM INFORMATION_SCHEMA.COLUMNS "
                       "WHERE TABLE_NAME=%s ORDER BY ORDINAL_POSITION", (table,))]

    # --- target access
    def insert(self, table, cols, rows, override=True):
        if not rows:
            return
        ov = "OVERRIDING SYSTEM VALUE " if override else ""
        sql = f"INSERT INTO {table} ({', '.join(cols)}) {ov}VALUES ({', '.join(['%s'] * len(cols))})"
        for i in range(0, len(rows), BATCH):
            self.cur.executemany(sql, rows[i:i + BATCH])

    def quarantine(self, table, sid, reason, row):
        self.stats[table]["quarantined"] += 1
        self.stats[table]["q:" + reason] += 1
        self.quarantine_rows.append((sid, table, reason, Jsonb(row, dumps=jdump)))
        if len(self.quarantine_rows) >= BATCH:
            self.flush_quarantine()

    def flush_quarantine(self):
        self.insert("import_quarantine", ["source_id", "source_table", "reason", "row_data"],
                    self.quarantine_rows, override=False)
        self.quarantine_rows = []

    def ensure(self, table, code, **extra):
        """FK safety for lookup tables: a code used by data but missing from its reference table gets a placeholder row."""
        code = txt(code)
        if code is None:
            return None
        if code not in self.known[table]:
            cols = ["code"] + list(extra)
            vals = [code] + list(extra.values())
            if table in ("price_category", "customer_category", "pay_category"):
                cols.append("description"); vals.append("imported placeholder: code used by data but not defined in GQ7")
            if table == "tax_group":
                cols.append("description"); vals.append("imported placeholder: code used by data but not defined in GQ7")
            self.cur.execute(f"INSERT INTO {table} ({', '.join(cols)}) VALUES ({', '.join(['%s'] * len(cols))})", vals)
            self.known[table].add(code)
            self.placeholders[table].add(code)
        return code

    def user_id(self, code):
        code = txt(code)
        if code is None:
            return None
        if code not in self.users:
            self.cur.execute("INSERT INTO app_user (code, active) VALUES (%s, false) RETURNING id", (code,))
            self.users[code] = self.cur.fetchone()[0]
            self.stats["app_user"]["audit_only_codes"] += 1
        return self.users[code]

    # --- dropped-column handling
    def prepare_stragglers(self, table, consumed):
        """Find dropped columns and classify them. Returns a function row->legacy_extra dict or None."""
        consumed = {c.lower() for c in consumed}
        dropped = []
        for name, typ in self.src_columns(table):
            if name in consumed or AUDIT_COL.match(name) or name in IGNORE_COLS or typ in ("image", "varbinary"):
                continue
            dropped.append((name, typ))
        live = []
        nonconst = {}
        if dropped:
            parts = []
            for name, typ in dropped:
                parts.append(f"COUNT(DISTINCT CAST([{name}] AS varchar(450))) AS [{name}]")
            c = self.src.cursor(as_dict=True)
            c.execute(f"SELECT {', '.join(parts)} FROM {table}")
            counts = {k.lower(): v for k, v in c.fetchone().items()}
            c.close()
            for name, typ in dropped:
                if name in SECRET_COLS:
                    continue
                if counts[name] > 1:
                    nonconst[name] = counts[name]
                    live.append(name)
        self.stats[table]["dropped_columns"] = len(dropped)
        self.stats[table]["dropped_nonconstant"] = len(live)
        if live:
            self.notes.append(f"{table}: dropped columns with varying values, preserved per-row in legacy_extra: "
                              f"{', '.join(f'{n}({d} distinct)' for n, d in nonconst.items())}")

        def extra(row):
            out = {}
            for n in live:
                v = row.get(n)
                if v is None or v is False or v == 0 or (isinstance(v, str) and not v.strip()):
                    continue
                if isinstance(v, dt.datetime) and v < SENTINEL_BEFORE:
                    continue
                out[n] = v.rstrip() if isinstance(v, str) else v
            return out or None
        return extra

    def secret_report(self):
        found = {}
        for table, col in (("GQUSER", "User_Password"), ("GQCUST", "Cust_CreditCard")):
            n = self.scalar(f"SELECT COUNT(*) FROM {table} WHERE LTRIM(RTRIM(ISNULL(CAST({col} AS varchar(max)),'')))<>''")
            found[f"{table}.{col}"] = n
        self.notes.append(f"secrets NOT imported (non-empty source rows): {found}")
        if found.get("GQCUST.Cust_CreditCard"):
            self.notes.append("WARNING: GQCUST.Cust_CreditCard contains data in the source; it was NOT copied. Handle separately.")

    # ------------------------------------------------------------------------- phases
    def reset_target(self):
        self.cur.execute("SELECT (SELECT count(*) FROM customer)+(SELECT count(*) FROM document)+(SELECT count(*) FROM app_user)")
        if self.cur.fetchone()[0]:
            if not self.a.reset:
                sys.exit("Target already has data. Re-run with --reset to wipe imported data first.")
            log("--reset: truncating target data tables")
            self.cur.execute("SELECT tablename FROM pg_tables WHERE schemaname='public' "
                             "AND tablename NOT IN ('doc_type','schema_migrations')")
            tables = [r[0] for r in self.cur.fetchall()]
            self.cur.execute("TRUNCATE " + ", ".join(tables) + " RESTART IDENTITY CASCADE")

    def phase_users(self):
        log("users")
        rows = []
        for r in self.q("SELECT * FROM GQUSER"):
            rows.append((txt(r["user_id"]), txt(r["user_name"]), txt(r["user_type"]), txt(r["user_group"]),
                         nz(r["user_cmpyid"]), bool(r["user_nodispatch"]), True))
            self.stats["GQUSER"]["source"] += 1
        self.insert("app_user", ["code", "name", "user_type", "user_group", "company_id", "no_dispatch", "active"],
                    rows, override=False)
        self.cur.execute("SELECT code, id FROM app_user")
        self.users = dict(self.cur.fetchall())
        self.stats["GQUSER"]["loaded"] = len(rows)
        # every user code that only appears in audit columns becomes an inactive user, so audit FKs hold
        for tbl, col in [("GQCUST", "Create_UserID"), ("GQCUST", "Update_UserID"), ("GQINV", "Create_UserID"),
                         ("GQINV", "Update_UserID"), ("GQPAY", "Create_UserID"), ("GQPAY", "Update_UserID"),
                         ("GQAR", "Create_UserID"), ("GQREVISE", "Create_UserID"), ("FGPart", "Create_UserID"),
                         ("FGPart", "Update_UserID"), ("GQUserLog", "Log_User"), ("GQInvStatus", "Create_UserID")]:
            for r in self.q(f"SELECT DISTINCT RTRIM({col}) c FROM {tbl} WHERE {col} IS NOT NULL"):
                self.user_id(r["c"])
        # permissions
        rows = []
        for r in self.q("SELECT * FROM GQSEC"):
            self.stats["GQSEC"]["source"] += 1
            uid = self.user_id(r["sec_userid"])
            rows.append((uid, txt(r["sec_functionid"]), bool(r["sec_add"]), bool(r["sec_change"]),
                         bool(r["sec_delete"]), bool(r["sec_read"])))
        self.insert("user_permission", ["user_id", "function_id", "can_add", "can_change", "can_delete", "can_read"],
                    rows, override=False)
        self.stats["GQSEC"]["loaded"] = len(rows)

    def phase_reference(self):
        log("reference data")
        # tax codes / groups
        rows = []
        for r in self.q("SELECT * FROM GQTXCD"):
            self.stats["GQTXCD"]["source"] += 1
            rows.append((txt(r["txcd_code"]), txt(r["txcd_desc"]), r["txcd_percent"], bool(r["txcd_taxlabor"]), bool(r["txcd_noncalc"])))
            self.known["tax_code"].add(txt(r["txcd_code"]))
        self.insert("tax_code", ["code", "description", "percent", "taxes_labor", "non_calc"], rows, override=False)
        self.stats["GQTXCD"]["loaded"] = len(rows)
        groups, gcodes = [], []
        for r in self.q("SELECT * FROM GQTXGP"):
            self.stats["GQTXGP"]["source"] += 1
            g = txt(r["txgp_group"])
            groups.append((g, txt(r["txgp_desc"])))
            self.known["tax_group"].add(g)
            for i in range(1, 6):
                c = txt(r[f"txgp_tcode{i}"])
                if c:
                    self.ensure_tax_code(c)
                    gcodes.append((g, i, c))
        self.insert("tax_group", ["code", "description"], groups, override=False)
        self.insert("tax_group_code", ["tax_group", "position", "tax_code"], gcodes, override=False)
        self.stats["GQTXGP"]["loaded"] = len(groups)
        # simple code tables
        for src, tbl, cols in [("FGPCCAT", "price_category", ("pccat_code", "pccat_desc")),
                               ("GQCCAT", "customer_category", ("ccat_code", "ccat_desc"))]:
            rows = []
            for r in self.q(f"SELECT * FROM {src}"):
                self.stats[src]["source"] += 1
                rows.append((txt(r[cols[0]]), txt(r[cols[1]])))
                self.known[tbl].add(txt(r[cols[0]]))
            self.insert(tbl, ["code", "description"], rows, override=False)
            self.stats[src]["loaded"] = len(rows)
        rows = []
        for r in self.q("SELECT * FROM GQPayCat"):
            self.stats["GQPayCat"]["source"] += 1
            rows.append((txt(r["pac_code"]), txt(r["pac_desc"]), bool(r["pac_undeposited"])))
            self.known["pay_category"].add(txt(r["pac_code"]))
        self.insert("pay_category", ["code", "description", "undeposited"], rows, override=False)
        self.stats["GQPayCat"]["loaded"] = len(rows)
        rows = []
        for r in self.q("SELECT * FROM FGUofM"):
            self.stats["FGUofM"]["source"] += 1
            rows.append((r["id"], txt(r["umunitdesc"]), txt(r["umshortdesc"]), txt(r["umpluraldesc"])))
        self.insert("unit_of_measure", ["id", "name", "short_name", "plural_name"], rows, override=False)
        self.uoms = {r[0] for r in rows}
        self.stats["FGUofM"]["loaded"] = len(rows)
        # staff, carriers, statuses, sequences
        rows = []
        self.staff = {}
        for r in self.q("SELECT * FROM GQSLIN"):
            self.stats["GQSLIN"]["source"] += 1
            code = txt(r["slin_code"])
            self.staff[code] = r["id"]
            rows.append((r["id"], nz(r["slin_cmpyid"]), r["slln_rate"], bool(r["slin_salesman"]), bool(r["slin_installer"]),
                         bool(r["slin_region"]), bool(r["slin_deleted"]), code, txt(r["slin_name"]), txt(r["slin_phone"]),
                         txt(r["slin_hours"]), txt(r["slin_lunch"])))
        self.insert("staff", ["id", "company_id", "rate", "is_salesman", "is_installer", "is_region", "deleted", "code",
                              "name", "phone", "hours", "lunch"], rows)
        self.stats["GQSLIN"]["loaded"] = len(rows)
        rows = []
        for r in self.q("SELECT * FROM GQCARR"):
            self.stats["GQCARR"]["source"] += 1
            rows.append((r["id"], txt(r["carr_number"]) or "", txt(r["carr_name"]) or "", txt(r["carr_safelitenum"])))
        self.insert("carrier", ["id", "number", "name", "safelite_number"], rows)
        self.stats["GQCARR"]["loaded"] = len(rows)
        rows = []
        for r in self.q("SELECT * FROM GQStatus"):
            self.stats["GQStatus"]["source"] += 1
            rows.append((r["id"], txt(r["stat_name"]) or "", bool(r["stat_system"]), bool(r["stat_obsolete"])))
        self.insert("document_status", ["id", "name", "is_system", "obsolete"], rows)
        self.status_ids = {r[0] for r in rows}
        self.stats["GQStatus"]["loaded"] = len(rows)
        rows = []
        for r in self.q("SELECT * FROM GQSeq"):
            self.stats["GQSeq"]["source"] += 1
            rows.append((r["seq_cmpyid"], r["seq_type"], r["seq_value"]))
        self.insert("number_sequence", ["company_id", "seq_type", "next_value"], rows, override=False)
        self.stats["GQSeq"]["loaded"] = len(rows)
        # glass loss, zip
        rows = []
        for r in self.q("SELECT * FROM FGLoss"):
            self.stats["FGLoss"]["source"] += 1
            rows.append((r["id"], r["los_thicknum"], r["los_thickden"], r["los_lossint"], r["los_lossnum"], r["los_lossden"]))
        self.insert("glass_loss", ["id", "thick_num", "thick_den", "loss_int", "loss_num", "loss_den"], rows)
        self.stats["FGLoss"]["loaded"] = len(rows)
        log("zip codes")
        rows, seen = [], set()
        for r in self.q("SELECT * FROM ZipData"):
            self.stats["ZipData"]["source"] += 1
            key = (r["zipcode"].strip(), r["zipstate"].strip(), r["zipcity"].strip())
            if key in seen:
                self.stats["ZipData"]["skipped"] += 1
                continue
            seen.add(key)
            rows.append((key[0], key[1], key[2], r["ziplatitude"], r["ziplongitude"], txt(r["zipcounty"]),
                         txt(r["zipareacode"]), txt(r["ziptimezone"]), txt(r["ziptyp"])))
        self.insert("zip_code", ["zip", "state", "city", "latitude", "longitude", "county", "area_code", "time_zone", "zip_type"],
                    rows, override=False)
        self.stats["ZipData"]["loaded"] = len(rows)

    def ensure_tax_code(self, code):
        if code not in self.known["tax_code"]:
            self.cur.execute("INSERT INTO tax_code (code, description, percent) VALUES (%s, %s, 0)",
                             (code, "imported placeholder: code used by data but not defined in GQ7"))
            self.known["tax_code"].add(code)
            self.placeholders["tax_code"].add(code)

    def phase_company(self):
        log("company / config")
        mapped = {"id", "cmpy_code", "cmpy_name", "cmpy_addr1", "cmpy_addr2", "cmpy_city", "cmpy_state", "cmpy_zip",
                  "cmpy_phone", "cmpy_fax", "cmpy_emailaddr", "cmpy_fedid", "cmpy_taxgroup", "cmpy_terms",
                  "cmpy_fincharge", "cmpy_graceperiod", "cmpy_f_pricecat", "cmpy_invoice_note", "cmpy_disclaimer",
                  "cmpy_usedisclaimer", "cmpy_logo"}
        rows = []
        for r in self.q("SELECT * FROM GQCMPY"):
            self.stats["GQCMPY"]["source"] += 1
            flat = {k: v for k, v in r.items() if k.startswith("cmpy_f_") and k not in mapped}
            rest = {k: v for k, v in r.items() if k not in mapped and not k.startswith("cmpy_f_")
                    and k not in SECRET_COLS and not isinstance(v, (bytes, bytearray)) and not AUDIT_COL.match(k)}
            tg = self.ensure("tax_group", r["cmpy_taxgroup"])
            rows.append((r["id"], txt(r["cmpy_code"]) or "", txt(r["cmpy_name"]) or "", txt(r["cmpy_addr1"]),
                         txt(r["cmpy_addr2"]), txt(r["cmpy_city"]), txt(r["cmpy_state"]), txt(r["cmpy_zip"]),
                         txt(r["cmpy_phone"]), txt(r["cmpy_fax"]), txt(r["cmpy_emailaddr"]), txt(r["cmpy_fedid"]), tg,
                         r["cmpy_terms"], r["cmpy_fincharge"], r["cmpy_graceperiod"], txt(r["cmpy_f_pricecat"]),
                         txt(r["cmpy_invoice_note"]), txt(r["cmpy_disclaimer"]), bool(r["cmpy_usedisclaimer"]),
                         bytes(r["cmpy_logo"]) if r["cmpy_logo"] else None,
                         Jsonb(flat, dumps=jdump), Jsonb(rest, dumps=jdump)))
        self.insert("company", ["id", "code", "name", "address1", "address2", "city", "state", "zip", "phone", "fax",
                                "email", "fed_id", "tax_group", "default_terms_days", "finance_charge_pct", "grace_days",
                                "default_price_category", "invoice_note", "disclaimer", "use_disclaimer", "logo",
                                "flat_options", "legacy_settings"], rows, override=False)
        self.stats["GQCMPY"]["loaded"] = len(rows)
        # verbatim single-row config (minus secrets)
        for tbl in ("QBSetup", "QBAcct", "GQEnterprise"):
            for r in self.q(f"SELECT * FROM {tbl}"):
                self.stats[tbl]["source"] += 1
                clean = {k: v for k, v in r.items() if k not in SECRET_COLS and not isinstance(v, (bytes, bytearray))}
                self.cur.execute("INSERT INTO legacy_config (key, value) VALUES (%s, %s)", (tbl, Jsonb(clean, dumps=jdump)))
                self.stats[tbl]["loaded"] += 1

    def load_notes(self):
        self.notes_by_id = {}
        for r in self.q("SELECT Id, Note_Printed, Note_NonPrinted FROM GQNote"):
            self.stats["GQNote"]["source"] += 1
            self.notes_by_id[r["id"]] = (txt(r["note_printed"]), txt(r["note_nonprinted"]))
        self.notes_used = set()

    def note(self, nid):
        if not nid:
            return (None, None)
        if nid in self.notes_by_id:
            self.notes_used.add(nid)
            return self.notes_by_id[nid]
        return (None, None)

    def phase_customers(self):
        log("customers")
        consumed = {"id", "cust_number", "cust_cmpyid", "cust_type", "cust_permanent", "cust_fname", "cust_lname",
                    "cust_bill_addr1", "cust_bill_addr2", "cust_bill_city", "cust_bill_state", "cust_bill_zipcode",
                    "cust_emailaddr", "cust_creditlimit", "cust_taxgroup", "cust_taxexempt", "cust_taxid",
                    "cust_f_pricecat", "cust_contactname", "cust_category", "cust_print_statement", "cust_transdate",
                    "cust_notid", "cust_noprices"}
        consumed |= {f"cust_bill_phone{i}" for i in range(1, 6)} | {f"cust_bill_ext{i}" for i in range(1, 6)}
        extra = self.prepare_stragglers("GQCUST", consumed)
        self.cust_by_number, self.cust_ids = {}, set()
        rows, phones = [], []
        for r in self.q("SELECT * FROM GQCUST ORDER BY Id"):
            self.stats["GQCUST"]["source"] += 1
            num = txt(r["cust_number"])
            if num is None or num in self.cust_by_number:
                self.quarantine("GQCUST", r["id"], "blank_or_duplicate_number", r)
                continue
            pn, ni = self.note(r["cust_notid"])
            rows.append((r["id"], nz(r["cust_cmpyid"]), nz(r["cust_creditlimit"]), dte(r["cust_transdate"]),
                         self.user_id(r["create_userid"]), self.user_id(r["update_userid"]),
                         r["create_timestamp"] if ts(r["create_timestamp"]) else dt.datetime.now(),
                         ts(r["update_timestamp"]) or ts(r["create_timestamp"]) or dt.datetime.now(),
                         txt(r["cust_type"]) or "?", bool(r["cust_taxexempt"]), bool(r["cust_print_statement"]),
                         bool(r["cust_permanent"]), bool(r["cust_noprices"]), num, txt(r["cust_lname"]) or "",
                         txt(r["cust_fname"]), txt(r["cust_bill_addr1"]), txt(r["cust_bill_addr2"]), txt(r["cust_bill_city"]),
                         txt(r["cust_bill_state"]), txt(r["cust_bill_zipcode"]), txt(r["cust_emailaddr"]),
                         txt(r["cust_contactname"]), txt(r["cust_taxid"]),
                         self.ensure("tax_group", r["cust_taxgroup"]), self.ensure("price_category", r["cust_f_pricecat"]),
                         self.ensure("customer_category", r["cust_category"]), pn, ni,
                         Jsonb(extra(r), dumps=jdump) if extra(r) else None))
            self.cust_by_number[num] = r["id"]
            self.cust_ids.add(r["id"])
            for i in range(1, 6):
                p = txt(r[f"cust_bill_phone{i}"])
                if p:
                    phones.append((r["id"], i, p, txt(r[f"cust_bill_ext{i}"])))
        self.insert("customer", ["id", "company_id", "credit_limit", "last_transaction_on", "created_by", "updated_by",
                                 "created_at", "updated_at", "type", "tax_exempt", "print_statement", "permanent",
                                 "no_prices", "number", "last_name", "first_name", "address1", "address2", "city", "state",
                                 "zip", "email", "contact_name", "tax_id", "tax_group", "price_category", "category",
                                 "note_printed", "note_internal", "legacy_extra"], rows)
        self.insert("customer_phone", ["customer_id", "slot", "number", "ext"], phones, override=False)
        self.stats["GQCUST"]["loaded"] = len(rows)

    def phase_parts(self):
        log("parts / pricing")
        extra = self.prepare_stragglers("FGPart", {"id", "fgp_code", "fgp_desc", "fgp_source", "fgp_uofm",
                                                   "fgp_laboruofm", "fgp_notid"})
        rows, self.part_by_code, self.part_ids = [], {}, set()
        for r in self.q("SELECT * FROM FGPart ORDER BY Id"):
            self.stats["FGPart"]["source"] += 1
            code = txt(r["fgp_code"])
            pn, ni = self.note(r["fgp_notid"])
            uom = r["fgp_uofm"] if r["fgp_uofm"] in self.uoms else None
            luom = r["fgp_laboruofm"] if r["fgp_laboruofm"] in self.uoms else None
            if (r["fgp_uofm"] and uom is None) or (r["fgp_laboruofm"] and luom is None):
                self.stats["FGPart"]["unknown_uom_nulled"] += 1
            rows.append((r["id"], self.user_id(r["create_userid"]), self.user_id(r["update_userid"]),
                         ts(r["create_timestamp"]) or dt.datetime.now(), ts(r["update_timestamp"]) or dt.datetime.now(),
                         uom, luom, txt(r["fgp_source"]) or "?", code, txt(r["fgp_desc"]), pn, ni,
                         Jsonb(extra(r), dumps=jdump) if extra(r) else None))
            self.part_by_code[code.upper()] = r["id"]
            self.part_ids.add(r["id"])
        self.insert("part", ["id", "created_by", "updated_by", "created_at", "updated_at", "uom_id", "labor_uom_id",
                             "source", "code", "description", "note_printed", "note_internal", "legacy_extra"], rows)
        self.stats["FGPart"]["loaded"] = len(rows)
        rows, self.thick_ids = [], set()
        for r in self.q("SELECT * FROM FGPartThick ORDER BY Id"):
            self.stats["FGPartThick"]["source"] += 1
            if r["fpt_fgpid"] not in self.part_ids:
                self.quarantine("FGPartThick", r["id"], "missing_part", r); continue
            rows.append((r["id"], r["fpt_fgpid"], r["fpt_thicknum"], r["fpt_thickden"], txt(r["fpt_markupcat"])))
            self.thick_ids.add(r["id"])
        self.insert("part_thickness", ["id", "part_id", "thick_num", "thick_den", "markup_category"], rows)
        self.stats["FGPartThick"]["loaded"] = len(rows)
        rows, seen = [], set()
        for r in self.q("SELECT * FROM FGPricing ORDER BY Id"):
            self.stats["FGPricing"]["source"] += 1
            if r["prc_fptid"] not in self.thick_ids:
                self.quarantine("FGPricing", r["id"], "missing_thickness", r); continue
            cat = self.ensure("price_category", r["prc_pricecat"])
            if (r["prc_fptid"], cat) in seen:
                self.quarantine("FGPricing", r["id"], "duplicate_key", r); continue
            seen.add((r["prc_fptid"], cat))
            rows.append((r["id"], r["prc_fptid"], cat, r["prc_price1"], r["prc_price2"], r["prc_price3"], r["prc_price4"],
                         r["prc_minprice"], r["prc_laborprice"], r["prc_minlabor"], bool(r["prc_taxable"])))
        self.insert("part_price", ["id", "part_thickness_id", "price_category", "price1", "price2", "price3", "price4",
                                   "min_price", "labor_price", "min_labor", "taxable"], rows)
        self.stats["FGPricing"]["loaded"] = len(rows)
        rows = []
        for r in self.q("SELECT * FROM FGCUSPRC ORDER BY Id"):
            self.stats["FGCUSPRC"]["source"] += 1
            cid = self.cust_by_number.get(txt(r["cprc_custnum"]))
            pid = self.part_by_code.get((txt(r["cprc_partnum"]) or "").upper())
            if cid is None or pid is None:
                self.quarantine("FGCUSPRC", r["id"], "missing_customer_or_part", r); continue
            rows.append((r["id"], cid, pid, r["cprc_thicknum"], r["cprc_thickden"],
                         self.ensure("price_category", r["cprc_pricecat"]), r["cprc_price"]))
        self.insert("customer_part_price", ["id", "customer_id", "part_id", "thick_num", "thick_den", "price_category", "price"], rows)
        self.stats["FGCUSPRC"]["loaded"] = len(rows)

    def phase_documents(self):
        log("documents")
        consumed = {"id", "inv_cmpyid", "inv_doc_type", "inv_doc_prefix", "inv_doc_num", "inv_doc_suffix", "inv_aorf",
                    "inv_invtype", "inv_original_inv_num", "inv_date", "inv_voided", "inv_f_pricecat", "inv_spec_instr",
                    "inv_wo_instr", "inv_ponum", "inv_policynum", "inv_claimnum", "inv_lossdate", "inv_terms",
                    "inv_shop_or_mobile", "inv_salesnum", "inv_f_discount", "inv_billto_num", "inv_billto_billorship",
                    "inv_agent_num", "inv_agent_billorship", "inv_cust_num", "inv_cust_lname", "inv_cust_fname",
                    "inv_cust_billorship", "inv_cust_addr1", "inv_cust_addr2", "inv_cust_city", "inv_cust_state",
                    "inv_cust_zip", "inv_taxgroup", "inv_noteid", "inv_custcat", "inv_locaddr1", "inv_locaddr2",
                    "inv_loccity", "inv_locstate", "inv_loczip", "inv_initials", "inv_printedtime",
                    "inv_billto_lname", "inv_billto_fname"}
        consumed |= {f"inv_cust_phone{i}" for i in range(1, 4)} | {f"inv_cust_ext{i}" for i in range(1, 4)}
        extra = self.prepare_stragglers("GQINV", consumed)
        self.doc_ids = set()
        rows, shiptypes = [], collections.Counter()
        for r in self.q("SELECT * FROM GQINV ORDER BY Id"):
            self.stats["GQINV"]["source"] += 1
            x = extra(r) or {}
            cnum, bnum = txt(r["inv_cust_num"]), txt(r["inv_billto_num"])
            cid = self.cust_by_number.get(cnum)
            if cnum and cid is None:
                x["customer_number_not_found"] = cnum
                self.stats["GQINV"]["customer_missing"] += 1
            bid = None
            if bnum and bnum != cnum:
                bid = self.cust_by_number.get(bnum)
                if bid is None:
                    x["bill_to_number_not_found"] = bnum
            sales = txt(r["inv_salesnum"])
            sid = self.staff.get(sales) if sales else None
            if sales and sid is None:
                x["sales_code_not_found"] = sales
            if r["inv_aorf"] and txt(r["inv_aorf"]) != "F":
                x["aorf"] = txt(r["inv_aorf"])
            if txt(r["inv_invtype"]) not in (None, "P"):
                x["invtype"] = txt(r["inv_invtype"])
            ship = txt(r["inv_cust_billorship"])
            shiptypes[ship] += 1
            phones = []
            for i in range(1, 4):
                p = txt(r[f"inv_cust_phone{i}"])
                if p:
                    e = txt(r[f"inv_cust_ext{i}"])
                    phones.append(p + (f"x{e}" if e else ""))
            pn, ni = self.note(r["inv_noteid"])
            created = ts(r["create_timestamp"]) or dt.datetime.now()
            rows.append((r["id"], ts(r["inv_printedtime"]), created, ts(r["update_timestamp"]) or created,
                         r["inv_cmpyid"] or 0, r["inv_doc_num"] or 0, cid, bid, sid,
                         dte(r["inv_date"]) or created.date(), dte(r["inv_lossdate"]), nz(r["inv_f_discount"]),
                         self.user_id(r["create_userid"]), self.user_id(r["update_userid"]),
                         txt(r["inv_doc_type"]) or "R", bool(r["inv_voided"]), txt(r["inv_shop_or_mobile"]),
                         (ship or "").upper() == "S", txt(r["inv_doc_prefix"]) or "", txt(r["inv_doc_suffix"]) or "",
                         txt(r["inv_original_inv_num"]), txt(r["inv_terms"]), txt(r["inv_initials"]), txt(r["inv_agent_num"]),
                         txt(r["inv_ponum"]), txt(r["inv_policynum"]), txt(r["inv_claimnum"]),
                         self.ensure("price_category", r["inv_f_pricecat"]), self.ensure("customer_category", r["inv_custcat"]),
                         self.ensure("tax_group", r["inv_taxgroup"]),
                         txt(r["inv_cust_lname"]), txt(r["inv_cust_fname"]), txt(r["inv_cust_addr1"]), txt(r["inv_cust_addr2"]),
                         txt(r["inv_cust_city"]), txt(r["inv_cust_state"]), txt(r["inv_cust_zip"]), phones or None,
                         txt(r["inv_locaddr1"]), txt(r["inv_locaddr2"]), txt(r["inv_loccity"]), txt(r["inv_locstate"]),
                         txt(r["inv_loczip"]), txt(r["inv_spec_instr"]), txt(r["inv_wo_instr"]), pn, ni,
                         Jsonb(x, dumps=jdump) if x else None))
            self.doc_ids.add(r["id"])
        self.notes.append(f"GQINV.Inv_Cust_BillorShip value counts (addr_is_ship = value 'S'): {dict(shiptypes)}")
        self.insert("document", ["id", "printed_at", "created_at", "updated_at", "company_id", "doc_number", "customer_id",
                                 "bill_to_customer_id", "staff_sales_id", "doc_date", "loss_date", "discount_pct",
                                 "created_by", "updated_by", "doc_type", "voided", "shop_or_mobile", "addr_is_ship",
                                 "doc_prefix", "doc_suffix", "source_doc_number", "terms", "initials", "agent_code",
                                 "po_number", "policy_number", "claim_number", "price_category", "customer_category",
                                 "tax_group", "cust_last_name", "cust_first_name", "cust_address1", "cust_address2",
                                 "cust_city", "cust_state", "cust_zip", "cust_phones", "loc_address1", "loc_address2",
                                 "loc_city", "loc_state", "loc_zip", "spec_instructions", "wo_instructions",
                                 "note_printed", "note_internal", "legacy_extra"], rows)
        self.stats["GQINV"]["loaded"] = len(rows)
        del rows

        log("document lines")
        consumed = {"id", "lin_invid", "lin_seq_num", "lin_parent", "lin_partnumber", "lin_desc", "lin_f_thicknum",
                    "lin_f_thickden", "lin_qty", "lin_list", "lin_unitprice", "lin_mult", "lin_amount", "lin_taxable",
                    "lin_source", "lin_a_kind", "lin_amount_ovr", "lin_list_ovr", "lin_f_dims", "lin_f_shape",
                    "lin_f_uofmprice", "lin_f_uofmunits", "lin_f_specprice", "lin_f_pricecat"}
        extra = self.prepare_stragglers("GQLIN", consumed)
        rows = []
        for r in self.q("SELECT * FROM GQLIN ORDER BY id"):
            self.stats["GQLIN"]["source"] += 1
            if r["lin_invid"] not in self.doc_ids:
                self.quarantine("GQLIN", r["id"], "missing_document", r); continue
            src = LINE_SOURCE.get(txt(r["lin_source"]))
            if src is None:
                self.quarantine("GQLIN", r["id"], "unknown_source", r); continue
            x = extra(r) or {}
            if (txt(r["lin_a_kind"]) == "LAA") != (src == "labor"):
                x["a_kind"] = txt(r["lin_a_kind"])          # kind was redundant with source except here
                self.stats["GQLIN"]["kind_source_disagree"] += 1
            tn, td = r["lin_f_thicknum"], r["lin_f_thickden"]
            rows.append((r["id"], r["lin_invid"], nz(r["lin_qty"]), nz(r["lin_list"]), nz(r["lin_unitprice"]),
                         r["lin_mult"], r["lin_amount"] or 0, nz(r["lin_f_uofmprice"]), nz(r["lin_f_uofmunits"]),
                         r["lin_seq_num"], r["lin_parent"] if r["lin_parent"] and r["lin_parent"] > 0 else None,
                         None if not tn and not td else tn, None if not tn and not td else td, src,
                         bool(r["lin_taxable"]), bool(r["lin_amount_ovr"]), bool(r["lin_list_ovr"]),
                         bool(r["lin_f_specprice"]), txt(r["lin_partnumber"]), txt(r["lin_desc"]), txt(r["lin_f_dims"]),
                         txt(r["lin_f_shape"]), self.ensure("price_category", r["lin_f_pricecat"]),
                         Jsonb(x, dumps=jdump) if x else None))
            if len(rows) >= 20000:
                self.insert_lines(rows); rows = []
        self.insert_lines(rows)
        self.stats["GQLIN"]["loaded"] = self.stats["GQLIN"]["source"] - self.stats["GQLIN"]["quarantined"]

        log("tax / installers / revisions / status")
        rows, seen = [], set()
        for r in self.q("SELECT * FROM GQInvTax ORDER BY Id"):
            self.stats["GQInvTax"]["source"] += 1
            if r["inv_invid"] not in self.doc_ids:
                self.quarantine("GQInvTax", r["id"], "missing_document", r); continue
            code = txt(r["inv_taxcode"])
            if not code:
                self.quarantine("GQInvTax", r["id"], "blank_tax_code", r); continue
            self.ensure_tax_code(code)
            if (r["inv_invid"], code) in seen:
                self.quarantine("GQInvTax", r["id"], "duplicate_key", r); continue
            seen.add((r["inv_invid"], code))
            rows.append((r["id"], r["inv_invid"], code, r["inv_taxpercent"], r["inv_taxamount"]))
        self.insert("document_tax", ["id", "document_id", "tax_code", "percent", "amount"], rows)
        self.stats["GQInvTax"]["loaded"] = len(rows)
        rows = []
        for r in self.q("SELECT * FROM GQInvInst ORDER BY Id"):
            self.stats["GQInvInst"]["source"] += 1
            if r["inv_invid"] not in self.doc_ids:
                self.quarantine("GQInvInst", r["id"], "missing_document", r); continue
            code = txt(r["inv_inscode"]) or ""
            rows.append((r["id"], r["inv_invid"], self.staff.get(code), code, r["inv_inshours"], r["inv_insamount"]))
        self.insert("document_installer", ["id", "document_id", "staff_id", "installer_code", "hours", "amount"], rows)
        self.stats["GQInvInst"]["loaded"] = len(rows)
        rows = []
        for r in self.q("SELECT * FROM GQREVISE ORDER BY Id"):
            self.stats["GQREVISE"]["source"] += 1
            if r["rev_invid"] not in self.doc_ids:
                self.quarantine("GQREVISE", r["id"], "missing_document", r); continue
            at = ts(r["rev_timestamp"]) or ts(r["create_timestamp"])
            if at is None:
                self.quarantine("GQREVISE", r["id"], "no_timestamp", r); continue
            rows.append((r["id"], at, r["rev_invid"], r["rev_invtotal"], self.user_id(r["create_userid"]), txt(r["rev_comment"])))
        self.insert("document_revision", ["id", "at", "document_id", "total", "created_by", "comment"], rows)
        self.stats["GQREVISE"]["loaded"] = len(rows)
        rows = []
        for r in self.q("SELECT * FROM GQInvStatus ORDER BY Id"):
            self.stats["GQInvStatus"]["source"] += 1
            if r["ists_invid"] not in self.doc_ids or r["ists_statid"] not in self.status_ids:
                self.quarantine("GQInvStatus", r["id"], "missing_document_or_status", r); continue
            rows.append((r["id"], ts(r["ists_timestamp"]), r["ists_invid"], r["ists_statid"],
                         self.user_id(r["create_userid"]), txt(r["ists_note"])))
        self.insert("document_status_event", ["id", "at", "document_id", "status_id", "created_by", "note"], rows)
        self.stats["GQInvStatus"]["loaded"] = len(rows)

    def insert_lines(self, rows):
        self.insert("document_line", ["id", "document_id", "qty", "list_price", "unit_price", "multiplier", "amount",
                                      "uom_price", "uom_units", "seq", "parent_seq", "thick_num", "thick_den", "source",
                                      "taxable", "amount_override", "list_override", "special_price", "part_code",
                                      "description", "dims", "shape", "price_category", "legacy_extra"], rows)

    def phase_money(self):
        log("payments / AR")
        extra = self.prepare_stragglers("GQPAY", {"id", "pay_timestamp", "pay_type", "pay_custid", "pay_paymentamt",
                                                  "pay_paycat", "pay_desc"})
        rows, self.pay_ids = [], set()
        for r in self.q("SELECT * FROM GQPAY ORDER BY Id"):
            self.stats["GQPAY"]["source"] += 1
            if r["pay_custid"] not in self.cust_ids:
                self.quarantine("GQPAY", r["id"], "missing_customer", r); continue
            at = ts(r["pay_timestamp"]) or ts(r["create_timestamp"])
            if at is None:
                self.quarantine("GQPAY", r["id"], "no_timestamp", r); continue
            x = extra(r)
            rows.append((r["id"], at, r["pay_custid"], r["pay_paymentamt"], self.user_id(r["create_userid"]),
                         txt(r["pay_type"]) or "?", self.ensure("pay_category", r["pay_paycat"]), txt(r["pay_desc"]),
                         Jsonb(x, dumps=jdump) if x else None))
            self.pay_ids.add(r["id"])
        self.insert("payment", ["id", "paid_at", "customer_id", "amount", "created_by", "type", "category", "description",
                                "legacy_extra"], rows)
        self.stats["GQPAY"]["loaded"] = len(rows)
        rows = []
        for r in self.q("SELECT * FROM GQAR ORDER BY id"):
            self.stats["GQAR"]["source"] += 1
            if not r["ar_debit"] and not r["ar_credit"]:
                self.stats["GQAR"]["skipped"] += 1; self.stats["GQAR"]["skip:zero_amount"] += 1; continue
            if r["ar_debit"] and r["ar_credit"]:
                self.quarantine("GQAR", r["id"], "debit_and_credit_both_set", r); continue
            if r["ar_cust_id"] not in self.cust_ids:
                self.quarantine("GQAR", r["id"], "missing_customer", r); continue
            if r["ar_purpose"] not in (1, 3, 4, 5):
                self.quarantine("GQAR", r["id"], "unknown_purpose", r); continue
            x = {}
            did = r["ar_inv_id"] if r["ar_inv_id"] in self.doc_ids else None
            if r["ar_inv_id"] and did is None:
                x["missing_document_id"] = r["ar_inv_id"]; self.stats["GQAR"]["kept_missing_document"] += 1
            pid = rid = None
            if r["ar_parent_src"] == 2:
                pid = r["ar_parent_id"] if r["ar_parent_id"] in self.pay_ids else None
                if pid is None:
                    x["missing_payment_id"] = r["ar_parent_id"]; self.stats["GQAR"]["kept_missing_payment"] += 1
            elif r["ar_parent_src"] == 1 and r["ar_parent_id"] != r["ar_inv_id"]:
                rid = r["ar_parent_id"] if r["ar_parent_id"] in self.doc_ids else None
                if rid is None:
                    x["missing_related_document_id"] = r["ar_parent_id"]
            rows.append((r["id"], ts(r["create_timestamp"]) or dt.datetime.now(), r["ar_cust_id"], did, pid, rid,
                         (r["ar_debit"] or 0) - (r["ar_credit"] or 0), self.user_id(r["create_userid"]), r["ar_purpose"],
                         Jsonb(x, dumps=jdump) if x else None))
        self.insert("ar_entry", ["id", "created_at", "customer_id", "document_id", "payment_id", "related_document_id",
                                 "amount", "created_by", "purpose", "legacy_extra"], rows)
        self.stats["GQAR"]["loaded"] = len(rows)

    def phase_audit_log(self):
        log("audit log")
        rows = []
        for r in self.q("SELECT * FROM GQUserLog ORDER BY Id"):
            self.stats["GQUserLog"]["source"] += 1
            at = ts(r["log_time"])
            if at is None:
                self.quarantine("GQUserLog", r["id"], "no_timestamp", r); continue
            rows.append((at, r["log_id"], self.user_id(r["log_user"]), bool(r["log_delete"]), txt(r["log_tablename"]) or ""))
            if len(rows) >= 20000:
                self.insert("legacy_audit_log", ["at", "row_id", "user_id", "is_delete", "table_name"], rows, override=False); rows = []
        self.insert("legacy_audit_log", ["at", "row_id", "user_id", "is_delete", "table_name"], rows, override=False)
        self.stats["GQUserLog"]["loaded"] = self.stats["GQUserLog"]["source"] - self.stats["GQUserLog"]["quarantined"]

    def phase_attachments(self):
        """Resumable and committed in batches; bytes are content-addressed (sha256) so duplicates are stored once."""
        self.pg.commit()
        total = self.scalar("SELECT COUNT(*) FROM GQAttach")
        self.stats["GQAttach"]["source"] = total
        log(f"attachments: {total} rows")
        c = self.src.cursor(as_dict=True)
        c.execute("SELECT Id, Att_InvId, RTRIM(Att_FileName) fn, ISNULL(DATALENGTH(Att_Contents),0) n FROM GQAttach ORDER BY Id")
        metas = [{k.lower(): v for k, v in r.items()} for r in c.fetchall()]
        c.close()
        self.cur.execute("SELECT id FROM attachment")
        done = {r[0] for r in self.cur.fetchall()}
        self.cur.execute("SELECT sha256 FROM attachment_blob")
        blobs = {bytes(r[0]) for r in self.cur.fetchall()}
        todo = []
        for m in metas:
            if m["id"] in done:
                self.stats["GQAttach"]["resumed_already_loaded"] += 1; continue
            if m["att_invid"] not in self.doc_ids:
                self.quarantine("GQAttach", m["id"], "missing_document",
                                {"id": m["id"], "att_invid": m["att_invid"], "file_name": m["fn"], "bytes": m["n"]}); continue
            if m["n"] == 0:
                self.quarantine("GQAttach", m["id"], "empty_contents", {"id": m["id"], "file_name": m["fn"]}); continue
            todo.append(m)
        self.flush_quarantine(); self.pg.commit()
        loaded = dup = 0
        t0 = time.time()
        for i in range(0, len(todo), 50):
            chunk = todo[i:i + 50]
            ids = ",".join(str(m["id"]) for m in chunk)
            c = self.src.cursor(as_dict=True)
            c.execute(f"SELECT Id, Att_Contents FROM GQAttach WHERE Id IN ({ids})")
            data = {r["Id"]: bytes(r["Att_Contents"]) for r in c.fetchall()}
            c.close()
            for m in chunk:
                b = data[m["id"]]
                h = hashlib.sha256(b).digest()
                if h in blobs:
                    dup += 1
                else:
                    self.cur.execute("INSERT INTO attachment_blob (sha256, size_bytes, data) VALUES (%s,%s,%s) ON CONFLICT DO NOTHING",
                                     (h, len(b), b))
                    blobs.add(h)
                ctype = mimetypes.guess_type(m["fn"] or "")[0] or "application/octet-stream"
                self.cur.execute("INSERT INTO attachment (id, document_id, sha256, file_name, content_type) "
                                 "OVERRIDING SYSTEM VALUE VALUES (%s,%s,%s,%s,%s) ON CONFLICT (id) DO NOTHING",
                                 (m["id"], m["att_invid"], h, m["fn"] or "unnamed", ctype))
                loaded += 1
            self.pg.commit()
            if (i // 50) % 20 == 0:
                log(f"  attachments {i + len(chunk)}/{len(todo)}  ({(time.time() - t0):.0f}s)")
        self.stats["GQAttach"]["loaded"] = loaded + self.stats["GQAttach"]["resumed_already_loaded"]
        self.stats["GQAttach"]["duplicate_bytes_deduped"] = dup

    def finish_sequences(self):
        self.cur.execute("SELECT table_name FROM information_schema.columns WHERE table_schema='public' "
                         "AND column_name='id' AND is_identity='YES'")
        for (t,) in self.cur.fetchall():
            self.cur.execute(f"SELECT setval(pg_get_serial_sequence('{t}','id'), COALESCE(max(id),1), max(id) IS NOT NULL) FROM {t}")

    # ------------------------------------------------------------------------- reconciliation
    def reconcile(self):
        log("reconciliation")
        rep = {"tables": {}, "money": {}, "findings": [], "notes": self.notes,
               "placeholders": {k: sorted(v) for k, v in self.placeholders.items()}}
        bad = []
        for t, s in sorted(self.stats.items()):
            if "source" not in s:
                continue
            accounted = s["loaded"] + s["quarantined"] + s["skipped"]
            ok = accounted == s["source"]
            rep["tables"][t] = {k: v for k, v in s.items()}
            rep["tables"][t]["accounted"] = accounted
            if not ok:
                bad.append(f"{t}: source {s['source']} != loaded+quarantined+skipped {accounted}")
        cur = self.cur

        def one(sql):
            cur.execute(sql); return cur.fetchone()[0]
        D = decimal.Decimal
        # document counts by type
        src_types = {r["t"]: r["n"] for r in self.q("SELECT Inv_Doc_Type t, COUNT(*) n FROM GQINV GROUP BY Inv_Doc_Type")}
        cur.execute("SELECT doc_type, count(*) FROM document GROUP BY 1")
        tgt_types = {r[0].strip(): r[1] for r in cur.fetchall()}
        rep["money"]["document_counts_match"] = {k.strip(): v for k, v in src_types.items()} == tgt_types
        # line totals
        src_lines = self.scalar("SELECT ISNULL(SUM(Lin_Amount),0) FROM GQLIN")
        tgt_lines = one("SELECT COALESCE(sum(amount),0) FROM document_line")
        q_lines = one("SELECT COALESCE(sum((row_data->>'lin_amount')::numeric),0) FROM import_quarantine WHERE source_table='GQLIN'")
        rep["money"]["lines"] = {"source": str(src_lines), "target": str(tgt_lines), "quarantined": str(q_lines),
                                 "match": D(str(src_lines)) == tgt_lines + q_lines}
        # AR
        src_ar = self.scalar("SELECT ISNULL(SUM(ar_debit),0)-ISNULL(SUM(ar_credit),0) FROM GQAR")
        tgt_ar = one("SELECT COALESCE(sum(amount),0) FROM ar_entry")
        q_ar = one("SELECT COALESCE(sum((row_data->>'ar_debit')::numeric - (row_data->>'ar_credit')::numeric),0) "
                   "FROM import_quarantine WHERE source_table='GQAR'")
        rep["money"]["ar_net"] = {"source": str(src_ar), "target": str(tgt_ar), "quarantined_net": str(q_ar),
                                  "match": D(str(src_ar)) == tgt_ar + q_ar}
        src_pay = self.scalar("SELECT ISNULL(SUM(Pay_PaymentAmt),0) FROM GQPAY")
        tgt_pay = one("SELECT COALESCE(sum(amount),0) FROM payment")
        q_pay = one("SELECT COALESCE(sum((row_data->>'pay_paymentamt')::numeric),0) FROM import_quarantine WHERE source_table='GQPAY'")
        rep["money"]["payments"] = {"source": str(src_pay), "target": str(tgt_pay), "quarantined": str(q_pay),
                                    "match": D(str(src_pay)) == tgt_pay + q_pay}
        # business rule: document total == AR invoice debit
        cur.execute("""SELECT count(*), count(*) FILTER (WHERE abs(t.total - a.debit) >= 0.005)
                       FROM document d JOIN document_totals t ON t.document_id = d.id
                       JOIN (SELECT document_id, sum(amount) debit FROM ar_entry WHERE purpose = 1 GROUP BY 1) a ON a.document_id = d.id""")
        n, mism = cur.fetchone()
        cur.execute("""SELECT d.id, doc_number_text(d.doc_prefix, d.doc_number, d.doc_suffix), d.doc_type, t.total, a.debit
                       FROM document d JOIN document_totals t ON t.document_id = d.id
                       JOIN (SELECT document_id, sum(amount) debit FROM ar_entry WHERE purpose = 1 GROUP BY 1) a ON a.document_id = d.id
                       WHERE abs(t.total - a.debit) >= 0.005 ORDER BY d.id LIMIT 50""")
        rep["money"]["invoice_total_vs_ar_debit"] = {
            "compared": n, "mismatched": mism,
            "mismatches": [{"id": r[0], "doc": r[1], "type": r[2], "computed_total": str(r[3]), "ar_debit": str(r[4])} for r in cur.fetchall()]}
        # integrity
        for name, sql in [("documents_without_customer", "SELECT count(*) FROM document WHERE customer_id IS NULL"),
                          ("lines_parent_seq_dangling", "SELECT count(*) FROM document_line l WHERE parent_seq IS NOT NULL AND NOT EXISTS "
                           "(SELECT 1 FROM document_line p WHERE p.document_id=l.document_id AND p.seq=l.parent_seq)"),
                          ("quarantined_total", "SELECT count(*) FROM import_quarantine")]:
            rep["findings"].append({name: one(sql)})
        for k in ("lines", "ar_net", "payments"):
            if not rep["money"][k]["match"]:
                bad.append(f"money mismatch: {k}")
        if not rep["money"]["document_counts_match"]:
            bad.append("document counts by type differ")
        rep["unexplained"] = bad
        return rep

    # ------------------------------------------------------------------------- driver
    def run(self):
        t0 = time.time()
        self.reset_target()
        self.secret_report()
        self.load_notes()
        self.phase_users()
        self.phase_reference()
        self.phase_company()
        self.phase_customers()
        self.phase_parts()
        self.phase_documents()
        self.phase_money()
        if not self.a.skip_audit_log:
            self.phase_audit_log()
        self.stats["GQNote"]["loaded"] = len(self.notes_used)      # notes are inlined on their owner row
        for nid, (printed, nonprinted) in self.notes_by_id.items():
            if nid not in self.notes_used:                           # owner deleted: keep the text in quarantine
                self.quarantine("GQNote", nid, "note_not_referenced", {"id": nid, "printed": printed, "non_printed": nonprinted})
        self.flush_quarantine()
        self.finish_sequences()
        rep = self.reconcile()
        if self.a.dry_run:
            self.pg.rollback()
            log("DRY RUN: everything rolled back")
        else:
            self.pg.commit()
            if not self.a.skip_attachments:
                self.phase_attachments()
                self.flush_quarantine()
                self.finish_sequences()
                self.pg.commit()
                rep = self.reconcile_attachments(rep)
        rep["elapsed_seconds"] = round(time.time() - t0)
        rep["dry_run"] = self.a.dry_run
        os.makedirs(self.a.out, exist_ok=True)
        path = os.path.join(self.a.out, "import-report.json")
        with open(path, "w") as f:
            json.dump(rep, f, indent=2, default=str)
        self.print_summary(rep, path)
        return 1 if rep["unexplained"] else 0

    def reconcile_attachments(self, rep):
        s = self.stats["GQAttach"]
        rep["tables"]["GQAttach"] = dict(s)
        accounted = s["loaded"] + s["quarantined"] + s["skipped"]
        rep["tables"]["GQAttach"]["accounted"] = accounted
        if accounted != s["source"]:
            rep["unexplained"].append(f"GQAttach: source {s['source']} != accounted {accounted}")
        return rep

    def print_summary(self, rep, path):
        print("\n=== RECONCILIATION SUMMARY ===")
        print(f"{'source table':<14}{'source':>9}{'loaded':>9}{'quarant.':>9}{'skipped':>9}")
        for t, s in rep["tables"].items():
            print(f"{t:<14}{s.get('source', 0):>9}{s.get('loaded', 0):>9}{s.get('quarantined', 0):>9}{s.get('skipped', 0):>9}")
        m = rep["money"]
        print(f"\nlines sum match:   {m['lines']['match']}   AR net match: {m['ar_net']['match']}   payments match: {m['payments']['match']}")
        v = m["invoice_total_vs_ar_debit"]
        print(f"document total == AR debit: {v['compared'] - v['mismatched']}/{v['compared']} match, {v['mismatched']} mismatched")
        for n in rep["notes"]:
            print("note:", n)
        if rep["placeholders"]:
            print("placeholder lookup rows created:", rep["placeholders"])
        print("UNEXPLAINED:", rep["unexplained"] or "none")
        print("report:", path)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--mssql-host", default=os.environ.get("MSSQL_HOST", "gq7-mssql"))
    p.add_argument("--mssql-port", type=int, default=int(os.environ.get("MSSQL_PORT", "1433")))
    p.add_argument("--mssql-user", default=os.environ.get("MSSQL_USER", "sa"))
    p.add_argument("--mssql-db", default=os.environ.get("MSSQL_DB", "gqdata"))
    p.add_argument("--tz", default=os.environ.get("IMPORT_TZ", "America/Chicago"),
                   help="time zone the GQ7 (naive) timestamps were recorded in")
    p.add_argument("--dry-run", action="store_true", help="run everything, report, roll back (attachments skipped)")
    p.add_argument("--reset", action="store_true", help="wipe previously imported data in the target first")
    p.add_argument("--skip-attachments", action="store_true")
    p.add_argument("--skip-audit-log", action="store_true")
    p.add_argument("--out", default="/out")
    args = p.parse_args()
    sys.exit(Importer(args).run())


if __name__ == "__main__":
    main()
