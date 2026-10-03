-- Least-privilege SQL accounts (run by a DBA after the first migration).
-- Replace the passwords via your secret manager; prefer Windows/Entra authentication when available.
--   pos_migrator : DDL rights on schema [pos], used ONLY by Newrest.Pos.Migrator during deployments.
--   pos_api      : DML on [pos], no DDL; cannot UPDATE/DELETE fiscal tables.

CREATE LOGIN [pos_api] WITH PASSWORD = N'$(PosApiPassword)', CHECK_POLICY = ON;
CREATE USER [pos_api] FOR LOGIN [pos_api] WITH DEFAULT_SCHEMA = [pos];

GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[pos] TO [pos_api];

-- Fiscal and ledger records are append-only, also at database level (defence in depth on top of
-- the EF Core interceptor). Corrections go through credit notes / reversal movements.
DENY UPDATE, DELETE ON [pos].[Tickets] TO [pos_api];
DENY UPDATE, DELETE ON [pos].[TicketLines] TO [pos_api];
DENY UPDATE, DELETE ON [pos].[Payments] TO [pos_api];
DENY UPDATE, DELETE ON [pos].[AccountMovements] TO [pos_api];
DENY UPDATE, DELETE ON [pos].[ZReports] TO [pos_api];
DENY UPDATE, DELETE ON [pos].[ZReportLines] TO [pos_api];
DENY UPDATE, DELETE ON [pos].[AuditLog] TO [pos_api];
DENY UPDATE, DELETE ON [pos].[__EFMigrationsHistory] TO [pos_api];

CREATE LOGIN [pos_migrator] WITH PASSWORD = N'$(PosMigratorPassword)', CHECK_POLICY = ON;
CREATE USER [pos_migrator] FOR LOGIN [pos_migrator] WITH DEFAULT_SCHEMA = [pos];
ALTER ROLE [db_ddladmin] ADD MEMBER [pos_migrator];
GRANT SELECT, INSERT, UPDATE, DELETE, REFERENCES ON SCHEMA::[pos] TO [pos_migrator];
