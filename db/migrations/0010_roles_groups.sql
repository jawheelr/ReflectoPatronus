-- 0010: role-based access. A GROUP (role) is a named set of permissions; a user belongs to any number of groups and may do
-- what any of their groups allows. The special permission '*' (the built-in Administrators group) allows everything.
-- Permission keys are defined in the API (Auth/Permissions.cs); this table only stores which group has which.
-- app_user.is_admin is kept as a derived flag (member of Administrators) for older tooling and the importer.

CREATE TABLE app_group (
    id smallint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code text NOT NULL UNIQUE,
    name text NOT NULL,
    description text,
    is_system boolean NOT NULL DEFAULT false,      -- built-in: cannot be deleted; Administrators is also locked to '*'
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE group_permission (
    group_id smallint NOT NULL REFERENCES app_group(id) ON DELETE CASCADE,
    permission text NOT NULL,
    PRIMARY KEY (group_id, permission)
);

CREATE TABLE user_group (
    user_id smallint NOT NULL REFERENCES app_user(id) ON DELETE CASCADE,
    group_id smallint NOT NULL REFERENCES app_group(id) ON DELETE RESTRICT,
    PRIMARY KEY (user_id, group_id)
);
CREATE INDEX user_group_group ON user_group (group_id);

INSERT INTO app_group (code, name, description, is_system) VALUES
    ('administrators', 'Administrators', 'Everything, including users, groups, the database and clean-up tools.', true),
    ('managers',       'Managers',       'Runs the business: all paperwork, payments, prices, tables, reports and company details. No user or database administration.', true),
    ('counter',        'Counter / Accounts', 'Customers, quotes, work orders, invoices, credit memos and payments.', true),
    ('sales',          'Sales',          'Customers, quotes and work orders. No invoices or payments.', true),
    ('shop',           'Shop / Installers', 'Look up and print customers and documents. Cannot change anything.', true),
    ('readonly',       'Read only',      'Look at customers, documents, receivables and reports. Cannot change anything.', true);

INSERT INTO group_permission (group_id, permission)
SELECT g.id, p.permission FROM app_group g JOIN (VALUES
    ('administrators', '*'),
    ('managers', 'customers.read'), ('managers', 'customers.write'), ('managers', 'customers.delete'), ('managers', 'documents.read'),
    ('managers', 'quotes.write'), ('managers', 'workorders.write'), ('managers', 'invoices.write'), ('managers', 'credits.write'), ('managers', 'documents.void'),
    ('managers', 'receivables.read'), ('managers', 'payments.write'), ('managers', 'payments.void'), ('managers', 'catalog.write'), ('managers', 'maintenance.write'),
    ('managers', 'pricing.manage'), ('managers', 'reports.view'), ('managers', 'company.write'),
    ('counter', 'customers.read'), ('counter', 'customers.write'), ('counter', 'documents.read'), ('counter', 'quotes.write'), ('counter', 'workorders.write'),
    ('counter', 'invoices.write'), ('counter', 'credits.write'), ('counter', 'receivables.read'), ('counter', 'payments.write'),
    ('sales', 'customers.read'), ('sales', 'customers.write'), ('sales', 'documents.read'), ('sales', 'quotes.write'), ('sales', 'workorders.write'),
    ('shop', 'customers.read'), ('shop', 'documents.read'),
    ('readonly', 'customers.read'), ('readonly', 'documents.read'), ('readonly', 'receivables.read'), ('readonly', 'reports.view')
) AS p(code, permission) ON p.code = g.code;

-- Keep everyone who could sign in before able to do what they could: administrators stay administrators, other people with a
-- password become Managers (they could previously do everything except the administrator-only tools).
INSERT INTO user_group (user_id, group_id)
SELECT u.id, g.id FROM app_user u JOIN app_group g ON g.code = 'administrators' WHERE u.is_admin;
INSERT INTO user_group (user_id, group_id)
SELECT u.id, g.id FROM app_user u JOIN app_group g ON g.code = 'managers' WHERE NOT u.is_admin AND u.password_hash IS NOT NULL AND u.active;
