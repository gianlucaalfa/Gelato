using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text.Json;
using Gelato.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Gelato.Controllers;

/// <summary>
/// Palco API - Simple key-value cache migrated to Gelato.
/// Maintains the /Palco route for compatibility.
/// </summary>
[ApiController]
[Route("Palco")]
[Authorize(Policy = Policies.RequiresElevation)]
public class PalcoCacheController(ILogger<PalcoCacheController> logger, RegistrationRequestLimiter limiter) : ControllerBase
{
    private const string RegistrationNs = "anfiteatro-registration";

    // Access the service via GelatoPlugin instance or injection
    private PalcoCacheService? Cache => GelatoPlugin.Instance?.Configuration.PalcoEnabled == true
        ? GelatoPlugin.Instance.PalcoCache : null;

    // ========== PUBLIC ENDPOINTS (No Auth) ==========

    /// <summary>
    /// Check if registration is enabled.
    /// </summary>
    [HttpGet("Registration/Enabled")]
    [AllowAnonymous]
    public ActionResult GetRegistrationEnabled()
    {
        var value = Cache?.Get("registration-enabled", RegistrationNs);
        var enabled = Cache is not null && RegistrationRequestLimiter.IsRegistrationEnabled(value);
        return Ok(new { enabled });
    }

    /// <summary>
    /// Submit a registration request.
    /// </summary>
    [HttpPost("Registration/Request")]
    [AllowAnonymous]
    [Consumes(MediaTypeNames.Application.Json)]
    [RequestSizeLimit(32768)]
    public async Task<ActionResult> SubmitRegistrationRequest(
        [FromBody] RegistrationRequest request
    )
    {
        if (Cache == null)
            return StatusCode(503, new { error = "Cache unavailable" });
        var enabledJson = Cache.Get("registration-enabled", RegistrationNs);
        if (!RegistrationRequestLimiter.IsRegistrationEnabled(enabledJson))
            return StatusCode(403, new { error = "Registration is disabled" });
        if (!limiter.TryAcquire(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"))
            return StatusCode(429, new { error = "Too many registration requests" });
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Id, @"^request-[A-Za-z0-9_-]{1,120}$"))
            return BadRequest(new { error = "Invalid request ID" });
        try
        {
            using var data = JsonDocument.Parse(request.Data);
            if (data.RootElement.ValueKind != JsonValueKind.Object)
                return BadRequest(new { error = "Registration data must be an object" });
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Invalid registration data" });
        }
        var requestId = request.Id["request-".Length..];
        if (!Cache.TryAddRegistrationRequest(request.Id, request.Data, request.TtlSeconds))
            return Conflict(new { error = "Request already exists or the request queue is full" });

        // Notify admin via email if SMTP is configured
        try
        {
            var smtpJson = Cache.Get("smtp-config", RegistrationNs);
            if (!string.IsNullOrEmpty(smtpJson))
            {
                var smtp = JsonSerializer.Deserialize<SmtpConfig>(smtpJson);
                if (smtp != null && !string.IsNullOrEmpty(smtp.Host))
                {
                    // Extract user details
                    var userData = JsonSerializer.Deserialize<JsonElement>(request.Data);
                    var userName = userData.TryGetProperty("name", out var n)
                        ? n.GetString()
                        : "Unknown";
                    var userEmail = userData.TryGetProperty("email", out var e)
                        ? e.GetString()
                        : "Unknown";
                    var userMessage = userData.TryGetProperty("userMessage", out var m)
                        ? m.GetString()
                        : "";

                    var adminEmail = !string.IsNullOrEmpty(smtp.AdminEmail)
                        ? smtp.AdminEmail
                        : smtp.Username;

                    if (!string.IsNullOrEmpty(adminEmail))
                    {
                        using var client = new SmtpClient(smtp.Host, smtp.Port);
                        client.EnableSsl = true;
                        client.Credentials = new NetworkCredential(smtp.Username, smtp.Password);

                        var body =
                            $"A new user has requested access to your server.\n\nUsername: {userName}\nEmail: {userEmail}";

                        if (!string.IsNullOrEmpty(userMessage))
                        {
                            body += $"\n\nMessage from User:\n{userMessage}";
                        }

                        body += "\n\nPlease review this request in the Anfiteatro admin panel.";

                        using var mail = new MailMessage
                        {
                            From = new MailAddress(
                                smtp.FromAddress ?? smtp.Username,
                                smtp.FromName ?? "Anfiteatro"
                            ),
                            Subject = $"New Registration Request: {userName}",
                            Body = body,
                        };
                        mail.To.Add(adminEmail);

                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
                        timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        await client.SendMailAsync(mail, timeout.Token);
                        logger.LogInformation(
                            "[Gelato] Palco Admin notification sent to {AdminEmail} for registration: {Id}",
                            adminEmail,
                            requestId
                        );
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Gelato] Palco Failed to send admin notification email");
        }

        return Ok(new { success = true, requestId });
    }

    // ========== AUTHENTICATED ENDPOINTS ==========

    /// <summary>
    /// Get a cached value.
    /// </summary>
    [HttpGet("Cache/{key}")]
    public ActionResult Get([FromRoute, StringLength(256)] string key, [FromQuery, StringLength(128)] string ns = "")
    {
        if (Cache == null)
            return StatusCode(503);
        var value = Cache.Get(key, ns);
        if (value == null)
            return NotFound();
        return Ok(
            new
            {
                Key = key,
                Value = value,
                Namespace = ns,
            }
        );
    }

    /// <summary>
    /// Set a cached value.
    /// </summary>
    [HttpPost("Cache/{key}")]
    [Consumes(MediaTypeNames.Application.Json)]
    public ActionResult Set(
        [FromRoute, StringLength(256)] string key,
        [FromBody] SetRequest request,
        [FromQuery, StringLength(128)] string ns = ""
    )
    {
        if (Cache == null)
            return StatusCode(503);
        Cache.Set(key, request.Value, request.TtlSeconds, ns);
        logger.LogInformation("[Gelato] Palco Saved: {Key} in {Ns}", key, ns);
        return Ok(new { success = true });
    }

    /// <summary>
    /// Delete a cached value.
    /// </summary>
    [HttpDelete("Cache/{key}")]
    public ActionResult Delete([FromRoute, StringLength(256)] string key, [FromQuery, StringLength(128)] string ns = "")
    {
        if (Cache == null)
            return StatusCode(503);
        var deleted = Cache.Delete(key, ns);

        logger.LogInformation(
            "[Gelato] Palco Deleted: {Key} from {Ns}, success={Deleted}",
            key,
            ns,
            deleted
        );
        return Ok(new { success = true, deleted });
    }

    /// <summary>
    /// Get multiple cached values.
    /// </summary>
    [HttpPost("Cache/Bulk")]
    [Consumes(MediaTypeNames.Application.Json)]
    public ActionResult<Dictionary<string, string>> GetBulk(
        [FromBody] BulkRequest request,
        [FromQuery, StringLength(128)] string ns = ""
    )
    {
        if (Cache == null)
            return StatusCode(503);
        var keys = request.Keys.Take(201).ToArray();
        if (keys.Length > 200 || keys.Any(key => string.IsNullOrEmpty(key) || key.Length > 256))
            return BadRequest("At most 200 keys are allowed");
        return Ok(Cache.GetBulk(keys, ns));
    }

    /// <summary>
    /// Get cache stats.
    /// </summary>
    [HttpGet("Cache/Stats")]
    public ActionResult GetStats()
    {
        if (Cache == null)
            return StatusCode(503);
        var (total, expired, size) = Cache.GetStats();
        return Ok(
            new
            {
                TotalEntries = total,
                ExpiredEntries = expired,
                DatabaseSizeBytes = size,
            }
        );
    }

    /// <summary>
    /// Send an email (uses SMTP config from cache).
    /// </summary>
    [HttpPost("Email/Send")]
    [Consumes(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> SendEmail([FromBody] EmailRequest request)
    {
        if (Cache is null) return StatusCode(503);
        try
        {
            using var client = new SmtpClient(request.SmtpHost, request.SmtpPort);
            client.EnableSsl = true;
            client.Credentials = new NetworkCredential(request.SmtpUsername, request.SmtpPassword);

            using var mail = new MailMessage
            {
                From = new MailAddress(request.FromAddress, request.FromName),
                Subject = request.Subject,
                Body = request.Body,
                IsBodyHtml = request.Body.Contains('<'),
            };
            mail.To.Add(request.To);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await client.SendMailAsync(mail, timeout.Token);
            logger.LogInformation("[Gelato] Palco Email sent to {To}", request.To);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Gelato] Palco Email failed to {To}", request.To);
            return Ok(new { success = false, error = ex.Message });
        }
    }
}

#region Request Models

public class RegistrationRequest
{
    [Required, StringLength(128)]
    public required string Id { get; set; }

    [Required, StringLength(16384)]
    public required string Data { get; set; }
    public int TtlSeconds { get; set; } = 2592000; // 30 days
}

public class SetRequest
{
    [Required, StringLength(262144)]
    public required string Value { get; set; }
    [Range(0, 31536000)]
    public int TtlSeconds { get; set; } = 0;
}

public class BulkRequest
{
    [Required]
    public required IEnumerable<string> Keys { get; set; }
}

public class EmailRequest
{
    [Required]
    public required string To { get; set; }

    [Required]
    public required string Subject { get; set; }

    [Required]
    public required string Body { get; set; }

    [Required]
    public required string SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;

    [Required]
    public required string SmtpUsername { get; set; }

    [Required]
    public required string SmtpPassword { get; set; }

    [Required]
    public required string FromAddress { get; set; }
    public string FromName { get; set; } = "Anfiteatro";
}

public class SmtpConfig
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }
    public string? AdminEmail { get; set; }
}

#endregion
