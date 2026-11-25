using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PosLicenseServer.Data;
using PosLicenseServer.Models;

namespace PosLicenseServer.Services;

public class TelegramBotService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly LicenseDb _licenseDb;
    private readonly ILogger<TelegramBotService> _logger;

    // In-memory throttling caches to avoid notification flooding
    private readonly ConcurrentDictionary<string, DateTime> _lastAppStartAlerts = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastSyncErrorAlerts = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastAppErrorAlerts = new();

    public const string HardcodedDefaultBotToken = "9008744051:AAEhTj6Wx0Q-ELk0ig-lgBOg6V7XoccodjA";
    public const string HardcodedDefaultChatId = "8168115314";

    public TelegramBotService(
        IConfiguration configuration,
        LicenseDb licenseDb,
        ILogger<TelegramBotService> logger)
    {
        _configuration = configuration;
        _licenseDb = licenseDb;
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task<TelegramConfig> GetEffectiveConfigAsync()
    {
        // 1. Try DB
        var dbConfig = await _licenseDb.GetTelegramConfigAsync();
        var token = dbConfig.BotToken;
        var chatId = dbConfig.ChatId;

        // 2. Fallback to Environment Variables
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "";
        }
        if (string.IsNullOrWhiteSpace(chatId))
        {
            chatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID") ?? "";
        }

        // 3. Fallback to IConfiguration or Hardcoded Default
        if (string.IsNullOrWhiteSpace(token))
        {
            token = _configuration["Telegram:BotToken"];
            if (string.IsNullOrWhiteSpace(token))
            {
                token = HardcodedDefaultBotToken;
            }
        }
        if (string.IsNullOrWhiteSpace(chatId))
        {
            chatId = _configuration["Telegram:ChatId"];
            if (string.IsNullOrWhiteSpace(chatId))
            {
                chatId = HardcodedDefaultChatId;
            }
        }

        return new TelegramConfig
        {
            BotToken = token.Trim(),
            ChatId = chatId.Trim(),
            EnableAppStartAlert = dbConfig.EnableAppStartAlert,
            EnableSyncErrorAlert = dbConfig.EnableSyncErrorAlert,
            EnableAppErrorAlert = dbConfig.EnableAppErrorAlert
        };
    }

    public string GetDefaultBotToken()
    {
        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            token = _configuration["Telegram:BotToken"];
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            token = HardcodedDefaultBotToken;
        }
        return (token ?? "").Trim();
    }

    public string GetDefaultChatId()
    {
        var chatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID");
        if (string.IsNullOrWhiteSpace(chatId))
        {
            chatId = _configuration["Telegram:ChatId"];
        }
        if (string.IsNullOrWhiteSpace(chatId))
        {
            chatId = HardcodedDefaultChatId;
        }
        return (chatId ?? "").Trim();
    }

    public async Task<(bool HasOverride, string OverrideToken, string OverrideChatId)> GetOverrideStatusAsync()
    {
        var dbConfig = await _licenseDb.GetTelegramConfigAsync();
        var hasOverride = !string.IsNullOrWhiteSpace(dbConfig.BotToken) || !string.IsNullOrWhiteSpace(dbConfig.ChatId);
        return (hasOverride, dbConfig.BotToken, dbConfig.ChatId);
    }

    public async Task SaveConfigAsync(TelegramConfig config)
    {
        await _licenseDb.SaveTelegramConfigAsync(config);
    }

    public async Task ResetToDefaultAsync()
    {
        await _licenseDb.SetSettingAsync("Telegram_BotToken", "");
        await _licenseDb.SetSettingAsync("Telegram_ChatId", "");
    }

    public async Task<(bool Success, string Message)> SendRawMessageAsync(string botToken, string chatId, string htmlMessage)
    {
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
        {
            return (false, "BotToken and ChatId are required.");
        }

        try
        {
            var url = $"https://api.telegram.org/bot{botToken.Trim()}/sendMessage";
            var payload = new
            {
                chat_id = chatId.Trim(),
                text = htmlMessage,
                parse_mode = "HTML"
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var response = await _httpClient.PostAsync(url, content, cts.Token);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                return (true, "Telegram notification delivered successfully!");
            }

            _logger.LogWarning("Telegram API returned non-success: {StatusCode} {Body}", response.StatusCode, responseBody);
            return (false, $"Telegram API error: {responseBody}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram message");
            return (false, $"Exception sending Telegram message: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> SendTestNotificationAsync(string? customToken = null, string? customChatId = null)
    {
        var config = await GetEffectiveConfigAsync();
        var token = !string.IsNullOrWhiteSpace(customToken) ? customToken : config.BotToken;
        var chatId = !string.IsNullOrWhiteSpace(customChatId) ? customChatId : config.ChatId;

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
        {
            return (false, "Bot Token and Chat ID must be configured.");
        }

        var istNow = DateTime.UtcNow.AddHours(5).AddMinutes(30);
        var msg = new StringBuilder();
        msg.AppendLine("🔔 <b>MAHANT POS TELEGRAM ALERT TEST</b>");
        msg.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━");
        msg.AppendLine("✓ Congratulations! Your Telegram bot alert channel is working.");
        msg.AppendLine($"⏰ <b>Time:</b> {istNow:dd-MMM-yyyy hh:mm:ss tt} IST");
        msg.AppendLine("📡 <b>Platform:</b> PosLicenseServer Cloud Hub");
        msg.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━");
        msg.AppendLine("You will receive instant alerts for POS application runtime, errors, and system sync events.");

        return await SendRawMessageAsync(token, chatId, msg.ToString());
    }

    public async Task NotifyAppStartedAsync(string machineId, string clientName, int processId, string ip)
    {
        try
        {
            var config = await GetEffectiveConfigAsync();
            if (!config.IsConfigured || !config.EnableAppStartAlert) return;

            // Throttle to max 1 alert per machine every 2 minutes
            var now = DateTime.UtcNow;
            if (_lastAppStartAlerts.TryGetValue(machineId, out var lastTime) && (now - lastTime).TotalMinutes < 2)
            {
                return;
            }
            _lastAppStartAlerts[machineId] = now;

            var istNow = DateTime.UtcNow.AddHours(5).AddMinutes(30);
            var msg = new StringBuilder();
            msg.AppendLine("🟢 <b>POS APPLICATION STARTED</b>");
            msg.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━");
            msg.AppendLine($"🏢 <b>Client:</b> {clientName}");
            msg.AppendLine($"💻 <b>Machine ID:</b> <code>{machineId}</code>");
            msg.AppendLine($"🆔 <b>Process ID:</b> <code>{processId}</code>");
            msg.AppendLine($"🌐 <b>Client IP:</b> <code>{ip}</code>");
            msg.AppendLine($"⏰ <b>Started At:</b> {istNow:dd-MMM-yyyy hh:mm:ss tt} IST");
            msg.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━");
            msg.AppendLine("Status: Client is online and active.");

            await SendRawMessageAsync(config.BotToken, config.ChatId, msg.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed in NotifyAppStartedAsync");
        }
    }

    public async Task NotifySyncErrorAsync(string machineId, string clientName, string syncType, int count, string status, string details)
    {
        try
        {
            var config = await GetEffectiveConfigAsync();
            if (!config.IsConfigured || !config.EnableSyncErrorAlert) return;

            // Throttle to max 1 alert per machine every 10 minutes to avoid flood
            var now = DateTime.UtcNow;
            var throttleKey = $"{machineId}-sync";
            if (_lastSyncErrorAlerts.TryGetValue(throttleKey, out var lastTime) && (now - lastTime).TotalMinutes < 10)
            {
                return;
            }
            _lastSyncErrorAlerts[throttleKey] = now;

            var istNow = DateTime.UtcNow.AddHours(5).AddMinutes(30);
            var msg = new StringBuilder();
            msg.AppendLine("🚨 <b>POS DATA SYNC ERROR!</b>");
            msg.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━");
            msg.AppendLine($"🏢 <b>Client:</b> {clientName}");
            msg.AppendLine($"💻 <b>Machine ID:</b> <code>{machineId}</code>");
            msg.AppendLine($"🔄 <b>Sync Action:</b> {syncType}");
            msg.AppendLine($"📊 <b>Records:</b> {count}");
            msg.AppendLine($"⚠️ <b>Sync Status:</b> <b>{status}</b>");
            if (!string.IsNullOrWhiteSpace(details))
            {
                msg.AppendLine($"❌ <b>Details:</b> <i>{details}</i>");
            }
            msg.AppendLine($"⏰ <b>Time:</b> {istNow:dd-MMM-yyyy hh:mm:ss tt} IST");
            msg.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━");
            msg.AppendLine("⚠️ <i>Note: POS will automatically retry when connectivity restores.</i>");

            await SendRawMessageAsync(config.BotToken, config.ChatId, msg.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed in NotifySyncErrorAsync");
        }
    }

    public async Task NotifyAppErrorAsync(string machineId, string clientName, string severity, string source, string message)
    {
        try
        {
            var config = await GetEffectiveConfigAsync();
            if (!config.IsConfigured || !config.EnableAppErrorAlert) return;

            // Throttle to max 1 alert per machine per 3 minutes for general errors
            var now = DateTime.UtcNow;
            var throttleKey = $"{machineId}-err-{source}";
            if (_lastAppErrorAlerts.TryGetValue(throttleKey, out var lastTime) && (now - lastTime).TotalMinutes < 3)
            {
                return;
            }
            _lastAppErrorAlerts[throttleKey] = now;

            var istNow = DateTime.UtcNow.AddHours(5).AddMinutes(30);
            var msg = new StringBuilder();
            msg.AppendLine("⚠️ <b>POS APPLICATION EXCEPTION / ERROR</b>");
            msg.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━");
            msg.AppendLine($"🏢 <b>Client:</b> {clientName}");
            msg.AppendLine($"💻 <b>Machine ID:</b> <code>{machineId}</code>");
            msg.AppendLine($"🔥 <b>Severity:</b> {severity}");
            msg.AppendLine($"📦 <b>Module:</b> {source}");
            msg.AppendLine($"📝 <b>Message:</b> {message}");
            msg.AppendLine($"⏰ <b>Time:</b> {istNow:dd-MMM-yyyy hh:mm:ss tt} IST");
            msg.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━");
            msg.AppendLine("Full stack trace & active window are available in Developer License Portal.");

            await SendRawMessageAsync(config.BotToken, config.ChatId, msg.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed in NotifyAppErrorAsync");
        }
    }

    public async Task NotifyRemoteControlAsync(string machineId, string clientName, string action, string message, string author)
    {
        try
        {
            var config = await GetEffectiveConfigAsync();
            if (!config.IsConfigured) return;

            var istNow = DateTime.UtcNow.AddHours(5).AddMinutes(30);
            var icon = action switch
            {
                "kill" or "suspend" => "🛑",
                "activate" => "✅",
                "trial" => "⏳",
                "message" => "💬",
                _ => "⚡"
            };

            var msg = new StringBuilder();
            msg.AppendLine($"{icon} <b>POS REMOTE CONTROL ACTION: {action.ToUpperInvariant()}</b>");
            msg.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━");
            msg.AppendLine($"🏢 <b>Target:</b> {clientName}");
            msg.AppendLine($"💻 <b>Machine ID:</b> <code>{machineId}</code>");
            msg.AppendLine($"⚡ <b>Action:</b> {action}");
            if (!string.IsNullOrWhiteSpace(message))
            {
                msg.AppendLine($"ℹ️ <b>Message/Reason:</b> {message}");
            }
            msg.AppendLine($"👤 <b>Executed By:</b> {author}");
            msg.AppendLine($"⏰ <b>Time:</b> {istNow:dd-MMM-yyyy hh:mm:ss tt} IST");
            msg.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━");

            await SendRawMessageAsync(config.BotToken, config.ChatId, msg.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed in NotifyRemoteControlAsync");
        }
    }
}
