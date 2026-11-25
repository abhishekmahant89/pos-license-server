using System;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PosLicenseServer.Data;
using PosLicenseServer.Models;

var builder = WebApplication.CreateBuilder(args);

// Cloud Port Binding (Render, Railway, Docker support)
var envPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(envPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{envPort}");
}

// Add services
var dbPath = Environment.GetEnvironmentVariable("DATABASE_PATH") ?? "licenses.db";
builder.Services.AddSingleton<LicenseDb>(sp => new LicenseDb($"Data Source={dbPath}"));
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();

// Initialize database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LicenseDb>();
    await db.InitializeAsync();
}

// -------------------------------------------------------------
// REST API ENDPOINTS FOR CLIENT POS & DEVELOPER PORTAL
// -------------------------------------------------------------

// 1. POS Client Heartbeat & Verification (Tracks Multi-Instance Sessions)
app.MapPost("/api/license/verify", async (HttpContext ctx, LicenseCheckRequest req, LicenseDb db) =>
{
    if (string.IsNullOrWhiteSpace(req.MachineId))
    {
        return Results.BadRequest(new { success = false, message = "MachineId is required." });
    }

    var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
    var license = await db.RegisterOrHeartbeatAsync(req, clientIp);

    var response = new LicenseResponse
    {
        Success = true,
        MachineId = license.MachineId,
        Status = license.Status,
        IsAllowed = license.IsAllowed,
        TrialDaysRemaining = license.TrialDaysRemaining,
        TrialExpiresAt = license.TrialExpiresAt,
        DeveloperMessage = license.DeveloperMessage,
        RunningInstances = license.RunningCount,
        InstallState = license.InstallState,
        ServerUtcTime = DateTime.UtcNow,
        DeveloperContact = new DeveloperContactInfo()
    };

    return Results.Ok(response);
});

// 2. Client Install Notification (Called from Setup Installer)
app.MapPost("/api/license/install", async (HttpContext ctx, LicenseCheckRequest req, LicenseDb db) =>
{
    if (string.IsNullOrWhiteSpace(req.MachineId))
    {
        return Results.BadRequest(new { success = false, message = "MachineId is required." });
    }

    req.Action = "install";
    var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
    var license = await db.RegisterOrHeartbeatAsync(req, clientIp);
    return Results.Ok(new { success = true, installState = "Installed", machineId = license.MachineId });
});

// 3. Client Uninstall Notification (Called from Uninstaller)
app.MapPost("/api/license/uninstall", async (LicenseCheckRequest req, LicenseDb db) =>
{
    if (string.IsNullOrWhiteSpace(req.MachineId))
    {
        return Results.BadRequest(new { success = false, message = "MachineId is required." });
    }

    req.Action = "uninstall";
    var license = await db.RegisterOrHeartbeatAsync(req, "Uninstaller");
    return Results.Ok(new { success = true, installState = "Uninstalled", machineId = req.MachineId });
});

// 4. Client Exit Notification (Instant Session Cleanup)
app.MapPost("/api/license/exit", async (LicenseCheckRequest req, LicenseDb db) =>
{
    if (string.IsNullOrWhiteSpace(req.MachineId))
    {
        return Results.BadRequest(new { success = false, message = "MachineId is required." });
    }

    req.Action = "exit";
    await db.RegisterOrHeartbeatAsync(req, "ExitHandler");
    return Results.Ok(new { success = true });
});

// 5. Developer: Get All Registered Client Machines
app.MapGet("/api/license/clients", async (LicenseDb db) =>
{
    var clients = await db.GetAllAsync();
    return Results.Ok(clients);
});

// 6. Developer: Get Full Diagnostics for Specific Client
app.MapGet("/api/license/clients/{machineId}/details", async (string machineId, LicenseDb db) =>
{
    var details = await db.GetClientFullDetailsAsync(machineId);
    if (details == null)
    {
        return Results.NotFound(new { success = false, message = "Machine not found." });
    }
    return Results.Ok(details);
});

// 7. Client: Report Data Sync Activity
app.MapPost("/api/license/sync-report", async (SyncReportRequest req, LicenseDb db) =>
{
    if (string.IsNullOrWhiteSpace(req.MachineId))
    {
        return Results.BadRequest(new { success = false, message = "MachineId is required." });
    }
    await db.AddSyncLogAsync(req);
    return Results.Ok(new { success = true });
});

// 8. Client: Report Error or Warning Log
app.MapPost("/api/license/report-error", async (ErrorReportRequest req, LicenseDb db) =>
{
    if (string.IsNullOrWhiteSpace(req.MachineId))
    {
        return Results.BadRequest(new { success = false, message = "MachineId is required." });
    }
    await db.AddErrorLogAsync(req);
    return Results.Ok(new { success = true });
});

// 9. Developer: Remote Control (Activate, Kill/Suspend, Set Trial, Broadcast Message)
app.MapPost("/api/license/control", async (RemoteControlRequest req, LicenseDb db) =>
{
    if (string.IsNullOrWhiteSpace(req.MachineId) || string.IsNullOrWhiteSpace(req.Action))
    {
        return Results.BadRequest(new { success = false, message = "MachineId and Action are required." });
    }

    var success = await db.ApplyRemoteControlAsync(req);
    if (!success)
    {
        return Results.NotFound(new { success = false, message = "Machine not found." });
    }

    var updated = await db.GetByMachineIdAsync(req.MachineId);
    return Results.Ok(new { success = true, license = updated });
});

// 10. Developer: Generate Offline Activation Key
app.MapGet("/api/license/generate-key/{machineId}", (string machineId) =>
{
    if (string.IsNullOrWhiteSpace(machineId))
    {
        return Results.BadRequest(new { success = false, message = "MachineId required" });
    }
    var key = LicenseDb.GenerateOfflineActivationKey(machineId);
    return Results.Ok(new { machineId, activationKey = key });
});

// 11. Developer: Delete Client Record
app.MapDelete("/api/license/clients/{machineId}", async (string machineId, LicenseDb db) =>
{
    var deleted = await db.DeleteLicenseAsync(machineId);
    return Results.Ok(new { success = deleted });
});

// -------------------------------------------------------------
// DEDICATED GLOBAL REPORTS APIS
// -------------------------------------------------------------

// 12. ⏱️ Master App Running History (ON / OFF Events)
app.MapGet("/api/license/reports/running-history", async (LicenseDb db) =>
{
    var history = await db.GetAllSessionHistoryAsync();
    return Results.Ok(history);
});

// 13. 🔄 Cloud & POS Data Sync History
app.MapGet("/api/license/reports/sync-history", async (LicenseDb db) =>
{
    var logs = await db.GetAllSyncLogsAsync();
    return Results.Ok(logs);
});

// 14. ⚠️ Error & Diagnostics Reports
app.MapGet("/api/license/reports/error-logs", async (LicenseDb db) =>
{
    var errors = await db.GetAllErrorLogsAsync();
    return Results.Ok(errors);
});

// 15. 📜 Full Audit Timeline
app.MapGet("/api/license/reports/audit-timeline", async (LicenseDb db) =>
{
    var timeline = await db.GetAllAuditTimelineAsync();
    return Results.Ok(timeline);
});

// -------------------------------------------------------------
// DEVELOPER PORTAL (WEB UI)
// -------------------------------------------------------------
string GetPortalHtml()
{
    string[] candidatePaths = {
        Path.Combine(AppContext.BaseDirectory, "DeveloperPortal.html"),
        Path.Combine(Directory.GetCurrentDirectory(), "DeveloperPortal.html"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "DeveloperPortal.html")
    };

    foreach (var p in candidatePaths)
    {
        if (File.Exists(p)) return File.ReadAllText(p);
    }
    return "<h1>DeveloperPortal.html not found</h1>";
}

app.MapGet("/", () => Results.Content(GetPortalHtml(), "text/html", System.Text.Encoding.UTF8));
app.MapGet("/admin", () => Results.Content(GetPortalHtml(), "text/html", System.Text.Encoding.UTF8));

app.Run();
