using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var allowedOrigin = Environment.GetEnvironmentVariable("TERMIDM_LICENSE_ALLOWED_ORIGIN")
                    ?? "https://zstudio-lab.github.io";
var privateKeyPem = builder.Configuration["TERMIDM_LICENSE_PRIVATE_KEY_PEM"];
var signingKeyReady = IsValidSigningKey(privateKeyPem);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(allowedOrigin).WithMethods("POST").WithHeaders("Content-Type")));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("license-issue", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(10),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();
app.UseHttpsRedirection();
app.UseCors();
app.UseRateLimiter();

app.MapPost("/api/licenses", (LicenseRequest request, IConfiguration configuration) =>
{
    if (request.Name is null || request.Name.Trim().Length is < 1 or > 100 ||
        request.Email is null || request.Email.Trim().Length is < 3 or > 254 || !request.Email.Contains('@') ||
        request.Country is null || request.Country.Trim().Length is < 2 or > 80 ||
        request.Phone is null || request.Phone.Trim().Length is < 3 or > 40 ||
        request.MachineId is null || !Regex.IsMatch(request.MachineId, "\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant))
        return Results.BadRequest(new { error = "Enter valid name, email, phone, country, and device ID values." });

    var configuredPrivateKey = configuration["TERMIDM_LICENSE_PRIVATE_KEY_PEM"];
    if (string.IsNullOrWhiteSpace(configuredPrivateKey))
        return Results.Problem("License issuance is not configured on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);

    try
    {
        var payload = string.Join('|', "Pass-User_" + Uri.EscapeDataString(request.Name.Trim()),
            Uri.EscapeDataString(request.Email.Trim()), Uri.EscapeDataString(request.Country.Trim()),
            request.MachineId.ToLowerInvariant());
        using var signer = ECDsa.Create();
        signer.ImportFromPem(configuredPrivateKey);
        var signature = signer.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var encodedSignature = Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Results.Ok(new { key = payload + "." + encodedSignature });
    }
    catch (CryptographicException)
    {
        return Results.Problem("License issuance is temporarily unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.RequireRateLimiting("license-issue")
.Accepts<LicenseRequest>("application/json")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status429TooManyRequests);

app.MapGet("/health", () => signingKeyReady
    ? Results.Ok(new { status = "ok" })
    : Results.Problem("License issuer is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable));
app.Run();

static bool IsValidSigningKey(string? privateKeyPem)
{
    if (string.IsNullOrWhiteSpace(privateKeyPem)) return false;
    try
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return key.KeySize == 256 && key.ExportParameters(true).D is not null;
    }
    catch (CryptographicException) { return false; }
    catch (ArgumentException) { return false; }
}

internal sealed record LicenseRequest(string? Name, string? Email, string? Phone, string? Country, string? MachineId);
