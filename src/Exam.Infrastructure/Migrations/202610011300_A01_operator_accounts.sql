-- 考务账号与登录会话。口令只保存 PBKDF2 加盐哈希；会话只保存令牌的 SHA-256 摘要。
CREATE TABLE IF NOT EXISTS exam.operator_accounts
(
    username_key text PRIMARY KEY,
    username text NOT NULL,
    display_name text NOT NULL CHECK (length(btrim(display_name)) BETWEEN 1 AND 64),
    password_hash text NOT NULL,
    status text NOT NULL CHECK (status IN ('pending', 'active', 'disabled')),
    is_administrator boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL,
    approved_by text NULL,
    approved_at timestamptz NULL,
    updated_at timestamptz NOT NULL,
    updated_by text NULL
);

CREATE TABLE IF NOT EXISTS exam.operator_sessions
(
    token_digest text PRIMARY KEY,
    username_key text NOT NULL REFERENCES exam.operator_accounts (username_key) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL,
    revoked_at timestamptz NULL
);

CREATE INDEX IF NOT EXISTS ix_operator_sessions_user ON exam.operator_sessions (username_key) WHERE revoked_at IS NULL;
