-- Backups and maintenance of the NewrestPos database (SQL Server Agent jobs, or Azure SQL automated backups).
-- Targets: RPO 15 min (log backups), RTO 2 h. Retention aligned with docs/compliance.md (fiscal data: 10 years via the
-- signed monthly archives; database backups: 35 days).
-- Replace <backup-path> with a path on separate storage (another server / immutable blob via URL credential).

-- 1. Recovery model (required for point-in-time restore).
ALTER DATABASE NewrestPos SET RECOVERY FULL;
GO

-- 2. Weekly full backup (Sunday 01:00), checksum + compression + verification.
BACKUP DATABASE NewrestPos TO DISK = N'<backup-path>\NewrestPos_FULL.bak'
    WITH COMPRESSION, CHECKSUM, INIT, STATS = 10;
RESTORE VERIFYONLY FROM DISK = N'<backup-path>\NewrestPos_FULL.bak' WITH CHECKSUM;
GO

-- 3. Daily differential backup (01:00, other days).
BACKUP DATABASE NewrestPos TO DISK = N'<backup-path>\NewrestPos_DIFF.bak' WITH DIFFERENTIAL, COMPRESSION, CHECKSUM, INIT;
GO

-- 4. Transaction log backup every 15 minutes (file name with timestamp in the Agent job step).
BACKUP LOG NewrestPos TO DISK = N'<backup-path>\NewrestPos_LOG.trn' WITH COMPRESSION, CHECKSUM;
GO

-- 5. Weekly integrity and index maintenance (Saturday 03:00).
DBCC CHECKDB (NewrestPos) WITH NO_INFOMSGS, ALL_ERRORMSGS;
-- Index maintenance: Ola Hallengren's IndexOptimize recommended; minimum:
EXEC sp_MSforeachtable N'IF OBJECT_SCHEMA_NAME(OBJECT_ID(''?'')) = ''pos'' ALTER INDEX ALL ON ? REORGANIZE;';
EXEC sp_updatestats;
GO

-- Restore (point in time), see docs/runbook.md "Restaurer la base":
-- RESTORE DATABASE NewrestPos FROM DISK = N'...FULL.bak' WITH NORECOVERY, REPLACE;
-- RESTORE DATABASE NewrestPos FROM DISK = N'...DIFF.bak' WITH NORECOVERY;
-- RESTORE LOG NewrestPos FROM DISK = N'...LOG.trn' WITH STOPAT = '2026-10-04T11:45:00', RECOVERY;
