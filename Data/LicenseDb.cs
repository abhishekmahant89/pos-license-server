using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PosLicenseServer.Models;

namespace PosLicenseServer.Data;

public class LicenseDb
{
    private readonly string _connectionString;
    private const string SecretSalt = "MAHANT_POS_SECURE_SALT_2026_X99";

    public LicenseDb(string connectionString = "Data Source=licenses.db")
    {
        _connectionString = connectionString;
    }

    public async Task InitializeAsync()
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            CREATE TABLE IF NOT EXISTS ClientLicenses (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MachineId TEXT NOT NULL UNIQUE,
                RestaurantName TEXT,
                ClientName TEXT,
                PhoneNumber TEXT,
                Status TEXT NOT NULL DEFAULT 'Trial',
                TrialExpiresAt TEXT,
                DeveloperMessage TEXT,
                RegisteredAt TEXT NOT NULL,
                LastHeartbeatAt TEXT NOT NULL,
                ClientIp TEXT,
                ClientVersion TEXT,
                OfflineActivationKey TEXT,
                InstallState TEXT DEFAULT 'Installed',
                InstalledAt TEXT,
                UninstalledAt TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_machine_id ON ClientLicenses(MachineId);

            CREATE TABLE IF NOT EXISTS PosSessions (
                SessionId TEXT PRIMARY KEY,
                MachineId TEXT NOT NULL,
                ProcessId INTEGER,
                AppName TEXT,
                AppPath TEXT,
                StartedAt TEXT NOT NULL,
                LastHeartbeatAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_possessions_mid ON PosSessions(MachineId);

            CREATE TABLE IF NOT EXISTS PosSessionHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL,
                MachineId TEXT NOT NULL,
                ProcessId INTEGER,
                AppName TEXT,
                AppPath TEXT,
                StartedAt TEXT NOT NULL,
                EndedAt TEXT,
                DurationSeconds INTEGER DEFAULT 0,
                ExitReason TEXT DEFAULT 'Running',
                IsActive INTEGER DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS idx_poshistory_mid ON PosSessionHistory(MachineId);
            CREATE INDEX IF NOT EXISTS idx_poshistory_sid ON PosSessionHistory(SessionId);

            CREATE TABLE IF NOT EXISTS PosSyncLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MachineId TEXT NOT NULL,
                SyncType TEXT NOT NULL,
                RecordsCount INTEGER DEFAULT 0,
                Status TEXT DEFAULT 'Success',
                Details TEXT,
                Timestamp TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_possync_mid ON PosSyncLogs(MachineId);

            CREATE TABLE IF NOT EXISTS PosErrorLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MachineId TEXT NOT NULL,
                Severity TEXT DEFAULT 'Error',
                Source TEXT,
                Message TEXT NOT NULL,
                StackTrace TEXT,
                Timestamp TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_poserror_mid ON PosErrorLogs(MachineId);

            CREATE TABLE IF NOT EXISTS PosAuditTimeline (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MachineId TEXT NOT NULL,
                EventType TEXT NOT NULL,
                Title TEXT NOT NULL,
                Details TEXT,
                Timestamp TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_posaudit_mid ON PosAuditTimeline(MachineId);
        ";

        using var cmd = new SqliteCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();

        // Safe column migrations for existing databases
        await EnsureColumnAsync(conn, "ClientLicenses", "InstallState", "TEXT DEFAULT 'Installed'");
        await EnsureColumnAsync(conn, "ClientLicenses", "InstalledAt", "TEXT");
        await EnsureColumnAsync(conn, "ClientLicenses", "UninstalledAt", "TEXT");
        await EnsureColumnAsync(conn, "PosErrorLogs", "ActiveWindow", "TEXT DEFAULT ''");
    }

    private static async Task EnsureColumnAsync(SqliteConnection conn, string table, string column, string typeDefinition)
    {
        try
        {
            var checkSql = $"PRAGMA table_info({table});";
            using var cmd = new SqliteCommand(checkSql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            var exists = false;
            while (await reader.ReadAsync())
            {
                var colName = reader.GetString(1);
                if (colName.Equals(column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
            reader.Close();

            if (!exists)
            {
                var alterSql = $"ALTER TABLE {table} ADD COLUMN {column} {typeDefinition};";
                using var alterCmd = new SqliteCommand(alterSql, conn);
                await alterCmd.ExecuteNonQueryAsync();
            }
        }
        catch { }
    }

    public static string GenerateOfflineActivationKey(string machineId)
    {
        using var sha = SHA256.Create();
        var raw = machineId.Trim().ToUpperInvariant() + ":" + SecretSalt;
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        var hex = Convert.ToHexString(bytes).ToUpperInvariant();
        return $"{hex.Substring(0, 4)}-{hex.Substring(4, 4)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}";
    }

    private async Task CleanExpiredSessionsAsync(SqliteConnection conn)
    {
        try
        {
            var threshold = DateTime.UtcNow.AddMinutes(-2.5).ToString("o");
            var nowStr = DateTime.UtcNow.ToString("o");

            // 1. Mark expired sessions in PosSessionHistory as ended
            var updateHistorySql = @"
                UPDATE PosSessionHistory 
                SET EndedAt = @now,
                    IsActive = 0,
                    ExitReason = 'Closed / Shutdown / Timeout',
                    DurationSeconds = CAST((strftime('%s', @now) - strftime('%s', StartedAt)) AS INTEGER)
                WHERE IsActive = 1 
                  AND SessionId IN (
                      SELECT SessionId FROM PosSessions 
                      WHERE LastHeartbeatAt < @th 
                         OR ProcessId = 0 
                         OR ProcessId IS NULL 
                         OR SessionId LIKE '%-DEFAULT'
                  );
            ";
            using var cmdH = new SqliteCommand(updateHistorySql, conn);
            cmdH.Parameters.AddWithValue("@now", nowStr);
            cmdH.Parameters.AddWithValue("@th", threshold);
            await cmdH.ExecuteNonQueryAsync();

            // 2. Clean from PosSessions
            var cleanSql = @"
                DELETE FROM PosSessions 
                WHERE LastHeartbeatAt < @th 
                   OR ProcessId = 0 
                   OR ProcessId IS NULL 
                   OR SessionId LIKE '%-DEFAULT';";
            using var cmd = new SqliteCommand(cleanSql, conn);
            cmd.Parameters.AddWithValue("@th", threshold);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { }
    }

    public async Task<ClientLicense?> GetByMachineIdAsync(string machineId)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await CleanExpiredSessionsAsync(conn);

        var sql = "SELECT * FROM ClientLicenses WHERE MachineId = @m LIMIT 1;";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@m", machineId);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var lic = ReadLicense(reader);
            reader.Close();
            lic.ActiveInstances = await GetActiveSessionsForMachineAsync(conn, machineId);
            lic.RunningCount = lic.ActiveInstances.Count;
            return lic;
        }
        return null;
    }

    private async Task<List<ActiveInstanceDto>> GetActiveSessionsForMachineAsync(SqliteConnection conn, string machineId)
    {
        var list = new List<ActiveInstanceDto>();
        var sql = "SELECT * FROM PosSessions WHERE MachineId = @mid ORDER BY StartedAt ASC;";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@mid", machineId);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new ActiveInstanceDto
            {
                SessionId = reader.GetString(reader.GetOrdinal("SessionId")),
                ProcessId = reader.IsDBNull(reader.GetOrdinal("ProcessId")) ? 0 : reader.GetInt32(reader.GetOrdinal("ProcessId")),
                AppName = reader.IsDBNull(reader.GetOrdinal("AppName")) ? "RestaurantPOS" : reader.GetString(reader.GetOrdinal("AppName")),
                AppPath = reader.IsDBNull(reader.GetOrdinal("AppPath")) ? "" : reader.GetString(reader.GetOrdinal("AppPath")),
                StartedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("StartedAt"))),
                LastHeartbeatAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("LastHeartbeatAt")))
            });
        }
        return list;
    }

    public async Task<List<ClientLicense>> GetAllAsync()
    {
        var list = new List<ClientLicense>();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await CleanExpiredSessionsAsync(conn);

        var sql = "SELECT * FROM ClientLicenses ORDER BY datetime(LastHeartbeatAt) DESC;";
        using var cmd = new SqliteCommand(sql, conn);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(ReadLicense(reader));
        }
        reader.Close();

        // Attach active instances to each client
        foreach (var client in list)
        {
            client.ActiveInstances = await GetActiveSessionsForMachineAsync(conn, client.MachineId);
            client.RunningCount = client.ActiveInstances.Count;
        }

        return list;
    }

    public async Task<ClientLicense> RegisterOrHeartbeatAsync(LicenseCheckRequest req, string clientIp)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await CleanExpiredSessionsAsync(conn);

        var existing = await GetByMachineIdAsync(req.MachineId);
        var now = DateTime.UtcNow;
        var action = (req.Action ?? "heartbeat").ToLowerInvariant();

        // If uninstall action requested:
        if (action == "uninstall")
        {
            if (existing != null)
            {
                var uninstSql = @"
                    UPDATE ClientLicenses 
                    SET InstallState = 'Uninstalled',
                        UninstalledAt = @now
                    WHERE MachineId = @mid;
                    DELETE FROM PosSessions WHERE MachineId = @mid;
                    UPDATE PosSessionHistory 
                    SET EndedAt = @now, IsActive = 0, ExitReason = 'Uninstalled',
                        DurationSeconds = CAST((strftime('%s', @now) - strftime('%s', StartedAt)) AS INTEGER)
                    WHERE MachineId = @mid AND IsActive = 1;
                ";
                using var cmdU = new SqliteCommand(uninstSql, conn);
                cmdU.Parameters.AddWithValue("@now", now.ToString("o"));
                cmdU.Parameters.AddWithValue("@mid", req.MachineId);
                await cmdU.ExecuteNonQueryAsync();

                await AddAuditTimelineInternalAsync(conn, req.MachineId, "Uninstalled", "POS Client Uninstalled", 
                    $"Machine {req.MachineId} was uninstalled at {now:yyyy-MM-dd HH:mm:ss} UTC.");
            }
            return (await GetByMachineIdAsync(req.MachineId))!;
        }

        // If app exit action requested:
        if (action == "exit")
        {
            var sid = req.SessionId;
            var pid = req.ProcessId;

            var exitSql = @"
                UPDATE PosSessionHistory 
                SET EndedAt = @now, IsActive = 0, ExitReason = 'Clean Exit',
                    DurationSeconds = CAST((strftime('%s', @now) - strftime('%s', StartedAt)) AS INTEGER)
                WHERE MachineId = @mid AND (SessionId = @sid OR (@pid > 0 AND ProcessId = @pid));

                DELETE FROM PosSessions WHERE MachineId = @mid AND (SessionId = @sid OR (@pid > 0 AND ProcessId = @pid));
            ";
            using var cmdE = new SqliteCommand(exitSql, conn);
            cmdE.Parameters.AddWithValue("@now", now.ToString("o"));
            cmdE.Parameters.AddWithValue("@mid", req.MachineId);
            cmdE.Parameters.AddWithValue("@sid", sid ?? "");
            cmdE.Parameters.AddWithValue("@pid", pid ?? 0);
            await cmdE.ExecuteNonQueryAsync();

            await AddAuditTimelineInternalAsync(conn, req.MachineId, "AppExited", "POS Instance Stopped (OFF)", 
                $"App instance (PID: {pid ?? 0}) closed cleanly by user.");

            return (await GetByMachineIdAsync(req.MachineId))!;
        }

        // Upsert Client Record
        if (existing == null)
        {
            var trialEnd = now.AddDays(15);
            var offlineKey = GenerateOfflineActivationKey(req.MachineId);
            var initialMessage = "Mahant POS Trial Edition (15 Days Free). Contact Abhishek Mahant: +91 96918 83083.";

            var insertSql = @"
                INSERT INTO ClientLicenses 
                (MachineId, RestaurantName, ClientName, PhoneNumber, Status, TrialExpiresAt, DeveloperMessage, RegisteredAt, LastHeartbeatAt, ClientIp, ClientVersion, OfflineActivationKey, InstallState, InstalledAt)
                VALUES 
                (@mid, @rname, @cname, @phone, 'Trial', @trialEnd, @msg, @now, @now, @ip, @ver, @key, 'Installed', @now);
            ";

            using var cmd = new SqliteCommand(insertSql, conn);
            cmd.Parameters.AddWithValue("@mid", req.MachineId);
            cmd.Parameters.AddWithValue("@rname", string.IsNullOrWhiteSpace(req.RestaurantName) ? "New POS Machine" : req.RestaurantName);
            cmd.Parameters.AddWithValue("@cname", string.IsNullOrWhiteSpace(req.ClientName) ? "Store Owner" : req.ClientName);
            cmd.Parameters.AddWithValue("@phone", req.PhoneNumber ?? "");
            cmd.Parameters.AddWithValue("@trialEnd", trialEnd.ToString("o"));
            cmd.Parameters.AddWithValue("@msg", initialMessage);
            cmd.Parameters.AddWithValue("@now", now.ToString("o"));
            cmd.Parameters.AddWithValue("@ip", clientIp);
            cmd.Parameters.AddWithValue("@ver", req.ClientVersion ?? "1.0.0");
            cmd.Parameters.AddWithValue("@key", offlineKey);

            await cmd.ExecuteNonQueryAsync();

            await AddAuditTimelineInternalAsync(conn, req.MachineId, "Installed", "Initial POS Installation", 
                $"Installed on machine {req.MachineId} by {req.ClientName ?? "Owner"} (v{req.ClientVersion ?? "2.1.0"}).");
        }
        else
        {
            var updateSql = @"
                UPDATE ClientLicenses 
                SET LastHeartbeatAt = @now,
                    ClientIp = @ip,
                    InstallState = 'Installed',
                    ClientVersion = @ver" +
                    (!string.IsNullOrWhiteSpace(req.RestaurantName) ? ", RestaurantName = @rname" : "") +
                    (!string.IsNullOrWhiteSpace(req.PhoneNumber) ? ", PhoneNumber = @phone" : "") +
                " WHERE MachineId = @mid;";

            using var cmd = new SqliteCommand(updateSql, conn);
            cmd.Parameters.AddWithValue("@now", now.ToString("o"));
            cmd.Parameters.AddWithValue("@ip", clientIp);
            cmd.Parameters.AddWithValue("@ver", req.ClientVersion ?? existing.ClientVersion);
            cmd.Parameters.AddWithValue("@mid", req.MachineId);
            if (!string.IsNullOrWhiteSpace(req.RestaurantName))
                cmd.Parameters.AddWithValue("@rname", req.RestaurantName);
            if (!string.IsNullOrWhiteSpace(req.PhoneNumber))
                cmd.Parameters.AddWithValue("@phone", req.PhoneNumber);

            await cmd.ExecuteNonQueryAsync();
        }

        // Manage Multi-Instance Session ONLY for actual running applications (never during setup installation)
        if (action != "install" && (!string.IsNullOrWhiteSpace(req.SessionId) || (req.ProcessId.HasValue && req.ProcessId.Value > 0)))
        {
            var sessionId = !string.IsNullOrWhiteSpace(req.SessionId)
                ? req.SessionId
                : $"{req.MachineId}-PID-{req.ProcessId}";

            var appName = !string.IsNullOrWhiteSpace(req.AppName)
                ? req.AppName
                : $"POS (PID: {req.ProcessId})";

            var upsertSessionSql = @"
                INSERT INTO PosSessions (SessionId, MachineId, ProcessId, AppName, AppPath, StartedAt, LastHeartbeatAt)
                VALUES (@sid, @mid, @pid, @app, @path, @now, @now)
                ON CONFLICT(SessionId) DO UPDATE SET
                    LastHeartbeatAt = @now,
                    AppName = @app,
                    AppPath = COALESCE(NULLIF(@path, ''), AppPath);
            ";

            using var sessionCmd = new SqliteCommand(upsertSessionSql, conn);
            sessionCmd.Parameters.AddWithValue("@sid", sessionId);
            sessionCmd.Parameters.AddWithValue("@mid", req.MachineId);
            sessionCmd.Parameters.AddWithValue("@pid", (object?)req.ProcessId ?? DBNull.Value);
            sessionCmd.Parameters.AddWithValue("@app", appName);
            sessionCmd.Parameters.AddWithValue("@path", req.AppPath ?? "");
            sessionCmd.Parameters.AddWithValue("@now", now.ToString("o"));
            await sessionCmd.ExecuteNonQueryAsync();

            // Track into PosSessionHistory if not already present
            var insertHistorySql = @"
                INSERT INTO PosSessionHistory (SessionId, MachineId, ProcessId, AppName, AppPath, StartedAt, IsActive, ExitReason)
                SELECT @sid, @mid, @pid, @app, @path, @now, 1, 'Running'
                WHERE NOT EXISTS (SELECT 1 FROM PosSessionHistory WHERE SessionId = @sid);
            ";
            using var histCmd = new SqliteCommand(insertHistorySql, conn);
            histCmd.Parameters.AddWithValue("@sid", sessionId);
            histCmd.Parameters.AddWithValue("@mid", req.MachineId);
            histCmd.Parameters.AddWithValue("@pid", (object?)req.ProcessId ?? DBNull.Value);
            histCmd.Parameters.AddWithValue("@app", appName);
            histCmd.Parameters.AddWithValue("@path", req.AppPath ?? "");
            histCmd.Parameters.AddWithValue("@now", now.ToString("o"));
            var inserted = await histCmd.ExecuteNonQueryAsync();

            if (inserted > 0)
            {
                await AddAuditTimelineInternalAsync(conn, req.MachineId, "AppStarted", "POS Instance Started (ON)", 
                    $"Launched {appName} (PID: {req.ProcessId}) at {now:HH:mm:ss} UTC.");
            }

            // Inline Sync Logging
            if (!string.IsNullOrWhiteSpace(req.LastSyncStatus))
            {
                await AddSyncLogInternalAsync(conn, req.MachineId, "Orders Sync", req.LastSyncRecords ?? 0, req.LastSyncStatus, req.LastSyncDetails ?? "");
            }

            // Inline Error Logging
            if (!string.IsNullOrWhiteSpace(req.LastError))
            {
                await AddErrorLogInternalAsync(conn, req.MachineId, "Error", appName, req.LastError, "");
            }
        }
        else if (action == "install")
        {
            // Remove any accidental dummy session created previously for this machine
            var cleanDummy = "DELETE FROM PosSessions WHERE MachineId = @mid AND (ProcessId = 0 OR ProcessId IS NULL OR SessionId LIKE '%-DEFAULT');";
            using var cleanCmd = new SqliteCommand(cleanDummy, conn);
            cleanCmd.Parameters.AddWithValue("@mid", req.MachineId);
            await cleanCmd.ExecuteNonQueryAsync();

            await AddAuditTimelineInternalAsync(conn, req.MachineId, "Installed", "POS Setup Completed", 
                $"Installation registered by {req.ClientName ?? "User"} on {req.RestaurantName ?? "Machine"} (v{req.ClientVersion ?? "2.1.0"}).");
        }

        return (await GetByMachineIdAsync(req.MachineId))!;
    }

    public async Task<bool> ApplyRemoteControlAsync(RemoteControlRequest req)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var existing = await GetByMachineIdAsync(req.MachineId);
        if (existing == null) return false;

        string newStatus = existing.Status;
        DateTime? newTrialExpires = existing.TrialExpiresAt;
        string newMsg = req.DeveloperMessage ?? existing.DeveloperMessage;

        var auditTitle = "Remote Control Action";
        var auditDetails = "";

        switch (req.Action.ToLowerInvariant())
        {
            case "activate":
                newStatus = "Active";
                newMsg = "Licensed to " + (req.RestaurantName ?? existing.RestaurantName) + ". Verified by Abhishek Mahant.";
                auditTitle = "License Activated (Permanent)";
                auditDetails = $"Permanently activated for {req.RestaurantName ?? existing.RestaurantName}.";
                break;
            case "suspend":
                newStatus = "Suspended";
                newMsg = "POS software suspended by developer Abhishek Mahant. Contact +91 96918 83083.";
                auditTitle = "Software Suspended / Remotely Killed";
                auditDetails = "Access blocked remotely by developer.";
                break;
            case "expire":
                newStatus = "Expired";
                newMsg = "Trial license has expired. Contact Abhishek Mahant: +91 96918 83083.";
                auditTitle = "License Expired";
                auditDetails = "Trial expired remotely.";
                break;
            case "settrial":
                newStatus = "Trial";
                var days = req.TrialDays.HasValue && req.TrialDays > 0 ? req.TrialDays.Value : 15;
                newTrialExpires = DateTime.UtcNow.AddDays(days);
                newMsg = $"Trial mode extended by {days} days. Contact Abhishek Mahant: +91 96918 83083.";
                auditTitle = $"Trial Extended (+{days} Days)";
                auditDetails = $"Valid until {newTrialExpires:yyyy-MM-dd HH:mm:ss} UTC.";
                break;
            case "setmessage":
                if (!string.IsNullOrWhiteSpace(req.DeveloperMessage))
                    newMsg = req.DeveloperMessage;
                auditTitle = "Broadcast Message Sent";
                auditDetails = $"Message: {newMsg}";
                break;
        }

        var updateSql = @"
            UPDATE ClientLicenses 
            SET Status = @status,
                TrialExpiresAt = @trialEnd,
                DeveloperMessage = @msg,
                RestaurantName = COALESCE(@rname, RestaurantName),
                ClientName = COALESCE(@cname, ClientName),
                PhoneNumber = COALESCE(@phone, PhoneNumber)
            WHERE MachineId = @mid;
        ";

        using var cmd = new SqliteCommand(updateSql, conn);
        cmd.Parameters.AddWithValue("@status", newStatus);
        cmd.Parameters.AddWithValue("@trialEnd", newTrialExpires.HasValue ? newTrialExpires.Value.ToString("o") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@msg", newMsg);
        cmd.Parameters.AddWithValue("@mid", req.MachineId);
        cmd.Parameters.AddWithValue("@rname", (object?)req.RestaurantName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cname", (object?)req.ClientName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@phone", (object?)req.PhoneNumber ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync();

        await AddAuditTimelineInternalAsync(conn, req.MachineId, req.Action, auditTitle, auditDetails);

        return true;
    }

    public async Task<bool> DeleteLicenseAsync(string machineId)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            DELETE FROM ClientLicenses WHERE MachineId = @mid;
            DELETE FROM PosSessions WHERE MachineId = @mid;
            DELETE FROM PosSessionHistory WHERE MachineId = @mid;
            DELETE FROM PosSyncLogs WHERE MachineId = @mid;
            DELETE FROM PosErrorLogs WHERE MachineId = @mid;
            DELETE FROM PosAuditTimeline WHERE MachineId = @mid;
        ";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@mid", machineId);
        return (await cmd.ExecuteNonQueryAsync()) > 0;
    }

    // -------------------------------------------------------------
    // LOGGING AND AUDIT METHODS
    // -------------------------------------------------------------

    public async Task AddSyncLogAsync(SyncReportRequest req)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await AddSyncLogInternalAsync(conn, req.MachineId, req.SyncType, req.RecordsCount, req.Status, req.Details);
    }

    private static async Task AddSyncLogInternalAsync(SqliteConnection conn, string machineId, string syncType, int count, string status, string details)
    {
        try
        {
            var nowStr = DateTime.UtcNow.ToString("o");
            var sql = @"
                INSERT INTO PosSyncLogs (MachineId, SyncType, RecordsCount, Status, Details, Timestamp)
                VALUES (@mid, @stype, @rcnt, @status, @det, @now);
            ";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@mid", machineId);
            cmd.Parameters.AddWithValue("@stype", string.IsNullOrWhiteSpace(syncType) ? "Orders" : syncType);
            cmd.Parameters.AddWithValue("@rcnt", count);
            cmd.Parameters.AddWithValue("@status", string.IsNullOrWhiteSpace(status) ? "Success" : status);
            cmd.Parameters.AddWithValue("@det", details ?? "");
            cmd.Parameters.AddWithValue("@now", nowStr);
            await cmd.ExecuteNonQueryAsync();

            var auditTitle = $"Data Sync: {syncType} ({status})";
            var auditDetails = $"{count} records synced. {details}".Trim();
            await AddAuditTimelineInternalAsync(conn, machineId, "Sync", auditTitle, auditDetails);
        }
        catch { }
    }

    public async Task AddErrorLogAsync(ErrorReportRequest req)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await AddErrorLogInternalAsync(conn, req.MachineId, req.Severity, req.Source, req.Message, req.StackTrace, req.ActiveWindow);
    }

    private static async Task AddErrorLogInternalAsync(SqliteConnection conn, string machineId, string severity, string source, string message, string stackTrace, string? activeWindow = null)
    {
        try
        {
            var nowStr = DateTime.UtcNow.ToString("o");
            var win = string.IsNullOrWhiteSpace(activeWindow) ? "Startup / Background" : activeWindow;
            var sql = @"
                INSERT INTO PosErrorLogs (MachineId, Severity, Source, ActiveWindow, Message, StackTrace, Timestamp)
                VALUES (@mid, @sev, @src, @win, @msg, @stack, @now);
            ";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@mid", machineId);
            cmd.Parameters.AddWithValue("@sev", string.IsNullOrWhiteSpace(severity) ? "Error" : severity);
            cmd.Parameters.AddWithValue("@src", source ?? "POS");
            cmd.Parameters.AddWithValue("@win", win);
            cmd.Parameters.AddWithValue("@msg", message ?? "Unknown error");
            cmd.Parameters.AddWithValue("@stack", stackTrace ?? "");
            cmd.Parameters.AddWithValue("@now", nowStr);
            await cmd.ExecuteNonQueryAsync();

            var auditTitle = $"[{severity.ToUpper()}] [{win}] {source}: {Truncate(message, 50)}";
            await AddAuditTimelineInternalAsync(conn, machineId, "Error", auditTitle, message);
        }
        catch { }
    }

    private static async Task AddAuditTimelineInternalAsync(SqliteConnection conn, string machineId, string eventType, string title, string details)
    {
        try
        {
            var nowStr = DateTime.UtcNow.ToString("o");
            var sql = @"
                INSERT INTO PosAuditTimeline (MachineId, EventType, Title, Details, Timestamp)
                VALUES (@mid, @etype, @title, @det, @now);
            ";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@mid", machineId);
            cmd.Parameters.AddWithValue("@etype", eventType);
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@det", details ?? "");
            cmd.Parameters.AddWithValue("@now", nowStr);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { }
    }

    // -------------------------------------------------------------
    // FULL DETAILS QUERY (SESSIONS, SYNC, ERRORS, TIMELINE)
    // -------------------------------------------------------------

    public async Task<ClientDetailResponse?> GetClientFullDetailsAsync(string machineId)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await CleanExpiredSessionsAsync(conn);

        var license = await GetByMachineIdAsync(machineId);
        if (license == null) return null;

        var response = new ClientDetailResponse
        {
            License = license,
            ActiveSessions = license.ActiveInstances
        };

        // 1. Session On/Off History
        var sessionSql = "SELECT * FROM PosSessionHistory WHERE MachineId = @mid ORDER BY StartedAt DESC LIMIT 50;";
        using (var cmd = new SqliteCommand(sessionSql, conn))
        {
            cmd.Parameters.AddWithValue("@mid", machineId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var s = new SessionHistoryDto
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    SessionId = reader.GetString(reader.GetOrdinal("SessionId")),
                    MachineId = reader.GetString(reader.GetOrdinal("MachineId")),
                    ProcessId = reader.IsDBNull(reader.GetOrdinal("ProcessId")) ? 0 : reader.GetInt32(reader.GetOrdinal("ProcessId")),
                    AppName = reader.IsDBNull(reader.GetOrdinal("AppName")) ? "POS" : reader.GetString(reader.GetOrdinal("AppName")),
                    AppPath = reader.IsDBNull(reader.GetOrdinal("AppPath")) ? "" : reader.GetString(reader.GetOrdinal("AppPath")),
                    StartedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("StartedAt"))),
                    ExitReason = reader.IsDBNull(reader.GetOrdinal("ExitReason")) ? "Running" : reader.GetString(reader.GetOrdinal("ExitReason")),
                    IsActive = reader.GetInt32(reader.GetOrdinal("IsActive")) == 1,
                    DurationSeconds = reader.IsDBNull(reader.GetOrdinal("DurationSeconds")) ? 0 : reader.GetInt32(reader.GetOrdinal("DurationSeconds"))
                };
                if (!reader.IsDBNull(reader.GetOrdinal("EndedAt")))
                {
                    if (DateTime.TryParse(reader.GetString(reader.GetOrdinal("EndedAt")), out var dtEnd))
                        s.EndedAt = dtEnd;
                }
                response.SessionHistory.Add(s);
            }
        }

        // 2. Data Sync History
        var syncSql = "SELECT * FROM PosSyncLogs WHERE MachineId = @mid ORDER BY Timestamp DESC LIMIT 50;";
        using (var cmd = new SqliteCommand(syncSql, conn))
        {
            cmd.Parameters.AddWithValue("@mid", machineId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                response.SyncLogs.Add(new SyncLogDto
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    MachineId = reader.GetString(reader.GetOrdinal("MachineId")),
                    SyncType = reader.GetString(reader.GetOrdinal("SyncType")),
                    RecordsCount = reader.GetInt32(reader.GetOrdinal("RecordsCount")),
                    Status = reader.GetString(reader.GetOrdinal("Status")),
                    Details = reader.IsDBNull(reader.GetOrdinal("Details")) ? "" : reader.GetString(reader.GetOrdinal("Details")),
                    Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("Timestamp")))
                });
            }
        }

        // 3. Error Logs
        var errSql = "SELECT * FROM PosErrorLogs WHERE MachineId = @mid ORDER BY Timestamp DESC LIMIT 50;";
        using (var cmd = new SqliteCommand(errSql, conn))
        {
            cmd.Parameters.AddWithValue("@mid", machineId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var winIdx = -1;
                try { winIdx = reader.GetOrdinal("ActiveWindow"); } catch {}
                var activeWin = (winIdx >= 0 && !reader.IsDBNull(winIdx)) ? reader.GetString(winIdx) : "Startup / System";

                response.ErrorLogs.Add(new ErrorLogDto
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    MachineId = reader.GetString(reader.GetOrdinal("MachineId")),
                    Severity = reader.GetString(reader.GetOrdinal("Severity")),
                    Source = reader.IsDBNull(reader.GetOrdinal("Source")) ? "POS" : reader.GetString(reader.GetOrdinal("Source")),
                    ActiveWindow = activeWin,
                    Message = reader.GetString(reader.GetOrdinal("Message")),
                    StackTrace = reader.IsDBNull(reader.GetOrdinal("StackTrace")) ? "" : reader.GetString(reader.GetOrdinal("StackTrace")),
                    Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("Timestamp")))
                });
            }
        }

        // 4. Combined Audit Timeline
        var auditSql = "SELECT * FROM PosAuditTimeline WHERE MachineId = @mid ORDER BY Timestamp DESC LIMIT 100;";
        using (var cmd = new SqliteCommand(auditSql, conn))
        {
            cmd.Parameters.AddWithValue("@mid", machineId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                response.Timeline.Add(new AuditTimelineDto
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    MachineId = reader.GetString(reader.GetOrdinal("MachineId")),
                    EventType = reader.GetString(reader.GetOrdinal("EventType")),
                    Title = reader.GetString(reader.GetOrdinal("Title")),
                    Details = reader.IsDBNull(reader.GetOrdinal("Details")) ? "" : reader.GetString(reader.GetOrdinal("Details")),
                    Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("Timestamp")))
                });
            }
        }

        return response;
    }

    public async Task<List<SessionHistoryDto>> GetAllSessionHistoryAsync(int limit = 200)
    {
        var list = new List<SessionHistoryDto>();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await CleanExpiredSessionsAsync(conn);

        var sql = "SELECT * FROM PosSessionHistory ORDER BY StartedAt DESC LIMIT @lim;";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@lim", limit);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var s = new SessionHistoryDto
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                SessionId = reader.GetString(reader.GetOrdinal("SessionId")),
                MachineId = reader.GetString(reader.GetOrdinal("MachineId")),
                ProcessId = reader.IsDBNull(reader.GetOrdinal("ProcessId")) ? 0 : reader.GetInt32(reader.GetOrdinal("ProcessId")),
                AppName = reader.IsDBNull(reader.GetOrdinal("AppName")) ? "POS" : reader.GetString(reader.GetOrdinal("AppName")),
                AppPath = reader.IsDBNull(reader.GetOrdinal("AppPath")) ? "" : reader.GetString(reader.GetOrdinal("AppPath")),
                StartedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("StartedAt"))),
                ExitReason = reader.IsDBNull(reader.GetOrdinal("ExitReason")) ? "Running" : reader.GetString(reader.GetOrdinal("ExitReason")),
                IsActive = reader.GetInt32(reader.GetOrdinal("IsActive")) == 1,
                DurationSeconds = reader.IsDBNull(reader.GetOrdinal("DurationSeconds")) ? 0 : reader.GetInt32(reader.GetOrdinal("DurationSeconds"))
            };
            if (!reader.IsDBNull(reader.GetOrdinal("EndedAt")))
            {
                if (DateTime.TryParse(reader.GetString(reader.GetOrdinal("EndedAt")), out var dtEnd))
                    s.EndedAt = dtEnd;
            }
            list.Add(s);
        }
        return list;
    }

    public async Task<List<SyncLogDto>> GetAllSyncLogsAsync(int limit = 200)
    {
        var list = new List<SyncLogDto>();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = "SELECT * FROM PosSyncLogs ORDER BY Timestamp DESC LIMIT @lim;";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@lim", limit);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new SyncLogDto
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                MachineId = reader.GetString(reader.GetOrdinal("MachineId")),
                SyncType = reader.GetString(reader.GetOrdinal("SyncType")),
                RecordsCount = reader.GetInt32(reader.GetOrdinal("RecordsCount")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                Details = reader.IsDBNull(reader.GetOrdinal("Details")) ? "" : reader.GetString(reader.GetOrdinal("Details")),
                Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("Timestamp")))
            });
        }
        return list;
    }

    public async Task<List<ErrorLogDto>> GetAllErrorLogsAsync(int limit = 200)
    {
        var list = new List<ErrorLogDto>();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = "SELECT * FROM PosErrorLogs ORDER BY Timestamp DESC LIMIT @lim;";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@lim", limit);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var winIdx = -1;
            try { winIdx = reader.GetOrdinal("ActiveWindow"); } catch {}
            var activeWin = (winIdx >= 0 && !reader.IsDBNull(winIdx)) ? reader.GetString(winIdx) : "Startup / System";

            list.Add(new ErrorLogDto
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                MachineId = reader.GetString(reader.GetOrdinal("MachineId")),
                Severity = reader.GetString(reader.GetOrdinal("Severity")),
                Source = reader.IsDBNull(reader.GetOrdinal("Source")) ? "POS" : reader.GetString(reader.GetOrdinal("Source")),
                ActiveWindow = activeWin,
                Message = reader.GetString(reader.GetOrdinal("Message")),
                StackTrace = reader.IsDBNull(reader.GetOrdinal("StackTrace")) ? "" : reader.GetString(reader.GetOrdinal("StackTrace")),
                Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("Timestamp")))
            });
        }
        return list;
    }

    public async Task<List<AuditTimelineDto>> GetAllAuditTimelineAsync(int limit = 200)
    {
        var list = new List<AuditTimelineDto>();
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = "SELECT * FROM PosAuditTimeline ORDER BY Timestamp DESC LIMIT @lim;";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@lim", limit);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new AuditTimelineDto
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                MachineId = reader.GetString(reader.GetOrdinal("MachineId")),
                EventType = reader.GetString(reader.GetOrdinal("EventType")),
                Title = reader.GetString(reader.GetOrdinal("Title")),
                Details = reader.IsDBNull(reader.GetOrdinal("Details")) ? "" : reader.GetString(reader.GetOrdinal("Details")),
                Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("Timestamp")))
            });
        }
        return list;
    }

    private static string Truncate(string val, int max)
    {
        if (string.IsNullOrEmpty(val)) return "";
        return val.Length <= max ? val : val.Substring(0, max) + "...";
    }

    private static ClientLicense ReadLicense(SqliteDataReader r)
    {
        var lic = new ClientLicense
        {
            Id = r.GetInt32(r.GetOrdinal("Id")),
            MachineId = r.GetString(r.GetOrdinal("MachineId")),
            RestaurantName = r.IsDBNull(r.GetOrdinal("RestaurantName")) ? "" : r.GetString(r.GetOrdinal("RestaurantName")),
            ClientName = r.IsDBNull(r.GetOrdinal("ClientName")) ? "" : r.GetString(r.GetOrdinal("ClientName")),
            PhoneNumber = r.IsDBNull(r.GetOrdinal("PhoneNumber")) ? "" : r.GetString(r.GetOrdinal("PhoneNumber")),
            Status = r.GetString(r.GetOrdinal("Status")),
            DeveloperMessage = r.IsDBNull(r.GetOrdinal("DeveloperMessage")) ? "" : r.GetString(r.GetOrdinal("DeveloperMessage")),
            RegisteredAt = DateTime.Parse(r.GetString(r.GetOrdinal("RegisteredAt"))),
            LastHeartbeatAt = DateTime.Parse(r.GetString(r.GetOrdinal("LastHeartbeatAt"))),
            ClientIp = r.IsDBNull(r.GetOrdinal("ClientIp")) ? "" : r.GetString(r.GetOrdinal("ClientIp")),
            ClientVersion = r.IsDBNull(r.GetOrdinal("ClientVersion")) ? "" : r.GetString(r.GetOrdinal("ClientVersion")),
            OfflineActivationKey = r.IsDBNull(r.GetOrdinal("OfflineActivationKey")) ? "" : r.GetString(r.GetOrdinal("OfflineActivationKey")),
            InstallState = HasColumn(r, "InstallState") && !r.IsDBNull(r.GetOrdinal("InstallState")) ? r.GetString(r.GetOrdinal("InstallState")) : "Installed"
        };

        if (HasColumn(r, "InstalledAt") && !r.IsDBNull(r.GetOrdinal("InstalledAt")))
        {
            if (DateTime.TryParse(r.GetString(r.GetOrdinal("InstalledAt")), out var dti))
                lic.InstalledAt = dti;
        }

        if (HasColumn(r, "UninstalledAt") && !r.IsDBNull(r.GetOrdinal("UninstalledAt")))
        {
            if (DateTime.TryParse(r.GetString(r.GetOrdinal("UninstalledAt")), out var dtu))
                lic.UninstalledAt = dtu;
        }

        if (!r.IsDBNull(r.GetOrdinal("TrialExpiresAt")))
        {
            var str = r.GetString(r.GetOrdinal("TrialExpiresAt"));
            if (DateTime.TryParse(str, out var dt))
            {
                lic.TrialExpiresAt = dt;
            }
        }

        return lic;
    }

    private static bool HasColumn(SqliteDataReader reader, string columnName)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
