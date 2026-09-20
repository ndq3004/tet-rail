-- Cognito subjects and passenger data are owned by Catalog. National ID values are protected by the application
-- before persistence; audit rows intentionally store no passenger field values.
CREATE TABLE IF NOT EXISTS catalog.identity_users (
    id uuid PRIMARY KEY,
    cognito_subject varchar(255) NOT NULL UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS catalog.user_roles (
    user_id uuid NOT NULL REFERENCES catalog.identity_users(id) ON DELETE CASCADE,
    role varchar(20) NOT NULL CHECK (role IN ('CUSTOMER', 'ADMIN')),
    PRIMARY KEY (user_id, role)
);

CREATE TABLE IF NOT EXISTS catalog.passengers (
    id uuid PRIMARY KEY,
    owner_user_id uuid NOT NULL REFERENCES catalog.identity_users(id) ON DELETE RESTRICT,
    full_name varchar(200) NOT NULL,
    date_of_birth date NOT NULL,
    national_id_protected text NULL,
    national_id_last_four varchar(4) NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK ((national_id_protected IS NULL) = (national_id_last_four IS NULL))
);
CREATE INDEX IF NOT EXISTS ix_passengers_owner_user_id ON catalog.passengers(owner_user_id, id);

CREATE TABLE IF NOT EXISTS catalog.passenger_audit (
    id uuid PRIMARY KEY,
    passenger_id uuid NOT NULL,
    actor_user_id uuid NOT NULL REFERENCES catalog.identity_users(id) ON DELETE RESTRICT,
    action varchar(20) NOT NULL CHECK (action IN ('CREATED', 'UPDATED', 'DELETED')),
    correlation_id uuid NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_passenger_audit_passenger_id ON catalog.passenger_audit(passenger_id, occurred_at);

-- Rollback is allowed only before a shared environment stores passenger data. Thereafter, repair with a forward migration.
