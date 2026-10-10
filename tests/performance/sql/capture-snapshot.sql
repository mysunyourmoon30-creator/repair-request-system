-- Point-in-time snapshot used before and after every k6 run (docs/15 §13). READ-ONLY: it only selects from catalog / DMV
-- views and from the perf database tables; it creates, alters and deletes nothing. Run it through
-- ../scripts/capture-snapshot.ps1 against the dedicated perf database (never a shared or production database).
SET NOCOUNT ON;

-- 1. data fingerprint: proves a run neither wrote nor changed anything
-- (A login, which every k6 setup() performs, legitimately appends one USER / AUTH_LOGIN_SUCCEEDED row; the timeline read path
-- must append nothing, so the fingerprint of every other audit row and the login rows are reported separately.)
SELECT 'audit_history (excluding login events)' AS table_name, COUNT_BIG(*) AS row_count, CHECKSUM_AGG(CHECKSUM(audit_history_id)) AS id_checksum FROM audit_history WHERE entity_type <> 'USER'
UNION ALL SELECT 'audit_history (login events: USER)', COUNT_BIG(*), NULL FROM audit_history WHERE entity_type = 'USER'
UNION ALL SELECT 'repair_request',   COUNT_BIG(*), NULL FROM repair_request
UNION ALL SELECT 'work_order',       COUNT_BIG(*), NULL FROM work_order
UNION ALL SELECT 'service_visit',    COUNT_BIG(*), NULL FROM service_visit
UNION ALL SELECT 'work_session',     COUNT_BIG(*), NULL FROM work_session
UNION ALL SELECT 'corrective_action', COUNT_BIG(*), NULL FROM corrective_action;

SELECT CONVERT(bigint, CONVERT(binary(8), @@DBTS)) AS dbts, SYSUTCDATETIME() AS captured_at_utc, DB_NAME() AS db_name;

-- 2. the timeline statements in the plan cache (cumulative counters; diff two snapshots for a run)
SELECT
    CONVERT(varchar(66), qs.sql_handle, 1) AS sql_handle,
    qs.statement_start_offset,
    CONVERT(varchar(66), qs.plan_handle, 1) AS plan_handle,
    qs.execution_count,
    qs.total_logical_reads, qs.total_physical_reads, qs.total_worker_time, qs.total_elapsed_time,
    qs.max_elapsed_time, qs.max_logical_reads, qs.total_spills, qs.max_spills, qs.total_rows,
    LEFT(REPLACE(REPLACE(SUBSTRING(st.text, qs.statement_start_offset / 2 + 1,
        (CASE WHEN qs.statement_end_offset = -1 THEN LEN(CONVERT(nvarchar(max), st.text)) * 2 ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2 + 1),
        CHAR(13), ' '), CHAR(10), ' '), 160) AS statement_head
FROM sys.dm_exec_query_stats AS qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
CROSS APPLY sys.dm_exec_plan_attributes(qs.plan_handle) AS pa
WHERE pa.attribute = 'dbid' AND CONVERT(int, pa.value) = DB_ID()
  AND st.text LIKE '%audit_history%'
  AND st.text NOT LIKE '%dm_exec_query_stats%'
  AND (st.text LIKE '%OPENJSON%' OR st.text LIKE '%UNION ALL%' OR st.text LIKE '%audit_history_id%');

-- 3. plan shape of those statements: any scan of audit_history (clustered or timeline index) is flagged
;WITH XMLNAMESPACES (DEFAULT 'http://schemas.microsoft.com/sqlserver/2004/07/showplan')
SELECT
    CONVERT(varchar(66), qs.plan_handle, 1) AS plan_handle,
    qs.statement_start_offset,
    p.query_plan.exist('//RelOp[@PhysicalOp="Clustered Index Scan"]/IndexScan/Object[@Table="[audit_history]"]') AS clustered_scan_on_audit_history,
    p.query_plan.exist('//RelOp[@PhysicalOp="Index Scan"]/IndexScan/Object[@Table="[audit_history]"]') AS index_scan_on_audit_history,
    p.query_plan.exist('//RelOp[@PhysicalOp="Index Seek"]/IndexScan/Object[@Table="[audit_history]"][@Index="[IX_audit_history_timeline]"]') AS timeline_index_seek,
    p.query_plan.exist('//RelOp[@PhysicalOp="Clustered Index Seek"]/IndexScan/Object[@Table="[audit_history]"]') AS clustered_seek,
    p.query_plan.exist('//SpillToTempDb') AS spill_warning_in_plan,
    p.query_plan.exist('//RelOp[@PhysicalOp="Sort"]') AS has_sort
FROM sys.dm_exec_query_stats AS qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) AS p
CROSS APPLY sys.dm_exec_plan_attributes(qs.plan_handle) AS pa
WHERE pa.attribute = 'dbid' AND CONVERT(int, pa.value) = DB_ID()
  AND st.text LIKE '%audit_history%'
  AND st.text NOT LIKE '%dm_exec_query_stats%'
  AND (st.text LIKE '%OPENJSON%' OR st.text LIKE '%UNION ALL%');

-- 4. instance wait statistics (cumulative; diff two snapshots), benign waits excluded
SELECT TOP (25) wait_type, waiting_tasks_count, wait_time_ms, signal_wait_time_ms
FROM sys.dm_os_wait_stats
WHERE wait_type NOT IN (
    'SLEEP_TASK','BROKER_TASK_STOP','BROKER_TO_FLUSH','BROKER_EVENTHANDLER','SQLTRACE_BUFFER_FLUSH','CLR_AUTO_EVENT','CLR_MANUAL_EVENT',
    'LAZYWRITER_SLEEP','CHECKPOINT_QUEUE','XE_TIMER_EVENT','XE_DISPATCHER_WAIT','FT_IFTS_SCHEDULER_IDLE_WAIT','LOGMGR_QUEUE','DIRTY_PAGE_POLL',
    'HADR_FILESTREAM_IOMGR_IOCOMPLETION','REQUEST_FOR_DEADLOCK_SEARCH','SQLTRACE_INCREMENTAL_FLUSH_SLEEP','WAITFOR','ONDEMAND_TASK_QUEUE',
    'KSOURCE_WAKEUP','SP_SERVER_DIAGNOSTICS_SLEEP','QDS_PERSIST_TASK_MAIN_LOOP_SLEEP','QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP',
    'QDS_ASYNC_QUEUE','PWAIT_ALL_COMPONENTS_INITIALIZED','XE_LIVE_TARGET_TVF','PVS_PREALLOCATE','SOS_WORK_DISPATCHER','PARALLEL_REDO_WORKER_WAIT_WORK')
  AND waiting_tasks_count > 0
ORDER BY wait_time_ms DESC;

-- 5. tempdb pressure (page counts, 8 KB pages) and the connection pool as the server sees it
SELECT
    SUM(user_object_reserved_page_count) AS tempdb_user_object_pages,
    SUM(internal_object_reserved_page_count) AS tempdb_internal_object_pages,
    SUM(version_store_reserved_page_count) AS tempdb_version_store_pages
FROM tempdb.sys.dm_db_file_space_usage;

SELECT
    COUNT(*) AS sessions_on_db,
    SUM(CASE WHEN r.session_id IS NOT NULL THEN 1 ELSE 0 END) AS running_requests,
    SUM(CASE WHEN r.blocking_session_id <> 0 THEN 1 ELSE 0 END) AS blocked_requests
FROM sys.dm_exec_sessions AS s
LEFT JOIN sys.dm_exec_requests AS r ON r.session_id = s.session_id
WHERE s.database_id = DB_ID() AND s.is_user_process = 1 AND s.session_id <> @@SPID;
