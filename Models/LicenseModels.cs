using System;
using System.Collections.Generic;

namespace PosLicenseServer.Models;

public class ActiveInstanceDto
{
    public string SessionId { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public string AppName { get; set; } = string.Empty;
    public string AppPath { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime LastHeartbeatAt { get; set; }
}

public class ClientLicense
{
    public int Id { get; set; }
    public string MachineId { get; set; } = string.Empty;
    public string RestaurantName { get; set; } = "Unknown Restaurant";
    public string ClientName { get; set; } = "Client";
    public string PhoneNumber { get; set; } = string.Empty;
    
    // Status can be: "Active", "Trial", "Suspended", "Expired"
    public string Status { get; set; } = "Trial"; 
    
    public DateTime? TrialExpiresAt { get; set; }
    public string DeveloperMessage { get; set; } = "Welcome to Mahant POS!";
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    public DateTime LastHeartbeatAt { get; set; } = DateTime.UtcNow;
    public string ClientIp { get; set; } = string.Empty;
    public string ClientVersion { get; set; } = "1.0.0";
    public string OfflineActivationKey { get; set; } = string.Empty;

    // Multi-Instance and Installation Tracking:
    public int RunningCount { get; set; } = 0;
    public bool IsRunning => RunningCount > 0 || (DateTime.UtcNow - LastHeartbeatAt).TotalMinutes <= 3.5;
    public string InstallState { get; set; } = "Installed"; // "Installed" or "Uninstalled"
    public DateTime? InstalledAt { get; set; }
    public DateTime? UninstalledAt { get; set; }
    public List<ActiveInstanceDto> ActiveInstances { get; set; } = new();

    public bool IsAllowed
    {
        get
        {
            if (Status == "Suspended") return false;
            if (Status == "Active") return true;
            if (Status == "Trial")
            {
                if (TrialExpiresAt.HasValue && DateTime.UtcNow > TrialExpiresAt.Value)
                    return false;
                return true;
            }
            if (Status == "Expired") return false;
            return false;
        }
    }

    public int TrialDaysRemaining
    {
        get
        {
            if (Status == "Active") return 9999;
            if (Status != "Trial" || !TrialExpiresAt.HasValue) return 0;
            var diff = TrialExpiresAt.Value - DateTime.UtcNow;
            return diff.TotalDays > 0 ? (int)Math.Ceiling(diff.TotalDays) : 0;
        }
    }
}

public class LicenseCheckRequest
{
    public string MachineId { get; set; } = string.Empty;
    public string RestaurantName { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string ClientVersion { get; set; } = "1.0.0";
    public string? SessionId { get; set; }
    public int? ProcessId { get; set; }
    public string? AppName { get; set; }
    public string? AppPath { get; set; }
    public string? Action { get; set; } // "heartbeat", "install", "uninstall", "exit"
    public string? LastSyncStatus { get; set; }
    public string? LastSyncDetails { get; set; }
    public int? LastSyncRecords { get; set; }
    public string? LastError { get; set; }
}

public class SessionHistoryDto
{
    public int Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public string AppName { get; set; } = string.Empty;
    public string AppPath { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int DurationSeconds { get; set; }
    public string ExitReason { get; set; } = "Running";
    public bool IsActive { get; set; } = true;
}

public class SyncLogDto
{
    public int Id { get; set; }
    public string MachineId { get; set; } = string.Empty;
    public string SyncType { get; set; } = "Orders";
    public int RecordsCount { get; set; }
    public string Status { get; set; } = "Success"; // Success, Failed, Partial
    public string Details { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class ErrorLogDto
{
    public int Id { get; set; }
    public string MachineId { get; set; } = string.Empty;
    public string Severity { get; set; } = "Error"; // Error, Warning, Critical
    public string Source { get; set; } = "POS Client";
    public string ActiveWindow { get; set; } = "Startup / Background";
    public string Message { get; set; } = string.Empty;
    public string StackTrace { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class AuditTimelineDto
{
    public int Id { get; set; }
    public string MachineId { get; set; } = string.Empty;
    public string EventType { get; set; } = "Info"; // Installed, AppStarted, AppExited, Sync, Error, Uninstalled, Activated, Suspended
    public string Title { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class ClientDetailResponse
{
    public ClientLicense License { get; set; } = new();
    public List<ActiveInstanceDto> ActiveSessions { get; set; } = new();
    public List<SessionHistoryDto> SessionHistory { get; set; } = new();
    public List<SyncLogDto> SyncLogs { get; set; } = new();
    public List<ErrorLogDto> ErrorLogs { get; set; } = new();
    public List<AuditTimelineDto> Timeline { get; set; } = new();
}

public class SyncReportRequest
{
    public string MachineId { get; set; } = string.Empty;
    public string SyncType { get; set; } = "Orders Sync";
    public int RecordsCount { get; set; }
    public string Status { get; set; } = "Success";
    public string Details { get; set; } = string.Empty;
}

public class ErrorReportRequest
{
    public string MachineId { get; set; } = string.Empty;
    public string Severity { get; set; } = "Error";
    public string Source { get; set; } = "POS Client";
    public string? ActiveWindow { get; set; }
    public string Message { get; set; } = string.Empty;
    public string StackTrace { get; set; } = string.Empty;
}

public class RemoteControlRequest
{
    public string MachineId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty; // "Activate", "Suspend", "Expire", "SetTrial", "SetMessage"
    public int? TrialDays { get; set; }
    public string? DeveloperMessage { get; set; }
    public string? ClientName { get; set; }
    public string? PhoneNumber { get; set; }
    public string? RestaurantName { get; set; }
}

public class LicenseResponse
{
    public bool Success { get; set; } = true;
    public string MachineId { get; set; } = string.Empty;
    public string Status { get; set; } = "Trial"; // Active, Trial, Suspended, Expired
    public bool IsAllowed { get; set; }
    public int TrialDaysRemaining { get; set; }
    public DateTime? TrialExpiresAt { get; set; }
    public string DeveloperMessage { get; set; } = string.Empty;
    public int RunningInstances { get; set; } = 1;
    public string InstallState { get; set; } = "Installed";
    public DateTime ServerUtcTime { get; set; } = DateTime.UtcNow;
    public DeveloperContactInfo DeveloperContact { get; set; } = new();
}

public class DeveloperContactInfo
{
    public string DeveloperName { get; set; } = "Abhishek Mahant";
    public string Phone { get; set; } = "+91 96918 83083";
    public string Email { get; set; } = "info@abhishekmhant.in";
    public string Website { get; set; } = "www.abhishekmhant.in";
}

public class TelegramConfig
{
    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public bool EnableAppStartAlert { get; set; } = true;
    public bool EnableSyncErrorAlert { get; set; } = true;
    public bool EnableAppErrorAlert { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);
}

public class TelegramTestRequest
{
    public string? BotToken { get; set; }
    public string? ChatId { get; set; }
}
