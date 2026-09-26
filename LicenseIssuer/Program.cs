using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var allowedOrigin = Environment.GetEnvironmentVariable("TERMIDM_LICENSE_ALLOWED_ORIGIN")
                    ?? "https://zstudio-lab.github.io";
var privateKeyPem = builder.Configuration["TERMIDM_LICENSE_PRIVATE_KEY_PEM"];
var encryptionPrivateKeyPem = builder.Configuration["TERMIDM_LICENSE_ENCRYPTION_PRIVATE_KEY_PEM"];
var issuerReady = IsValidSigningKey(privateKeyPem) && IsValidEncryptionKey(encryptionPrivateKeyPem);
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

app.MapPost("/api/licenses", (EncryptedLicenseRequest request, IConfiguration configuration) =>
{
    var configuredPrivateKey = configuration["TERMIDM_LICENSE_PRIVATE_KEY_PEM"];
    var configuredEncryptionKey = configuration["TERMIDM_LICENSE_ENCRYPTION_PRIVATE_KEY_PEM"];
    if (string.IsNullOrWhiteSpace(configuredPrivateKey) || string.IsNullOrWhiteSpace(configuredEncryptionKey))
        return Results.Problem("License issuance is not configured on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);

    LicenseRequest? details;
    try
    {
        details = DecryptRequest(request, configuredEncryptionKey);
    }
    catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentException)
    {
        return Results.BadRequest(new { error = "The encrypted registration payload is invalid." });
    }

    if (details is null || details.Name is null || details.Name.Trim().Length is < 1 or > 100 ||
        details.Email is null || details.Email.Trim().Length is < 3 or > 254 || !details.Email.Contains('@') ||
        details.Country is null || details.Country.Trim().Length is < 2 or > 80 ||
        details.Phone is null || details.Phone.Trim().Length is < 3 or > 40 ||
        details.MachineId is null || !Regex.IsMatch(details.MachineId, "\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant))
        return Results.BadRequest(new { error = "Enter valid name, email, phone, country, and device ID values." });

    try
    {
        var payload = string.Join('|', "Pass-User_" + Uri.EscapeDataString(details.Name.Trim()),
            Uri.EscapeDataString(details.Email.Trim()), Uri.EscapeDataString(details.Country.Trim()),
            details.MachineId.ToLowerInvariant());
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
.Accepts<EncryptedLicenseRequest>("application/json")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status429TooManyRequests);

app.MapGet("/health", () => issuerReady
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

static bool IsValidEncryptionKey(string? privateKeyPem)
{
    if (string.IsNullOrWhiteSpace(privateKeyPem)) return false;
    try
    {
        using var key = RSA.Create();
        key.ImportFromPem(privateKeyPem);
        return key.KeySize == 3072 && key.ExportParameters(true).D is not null;
    }
    catch (CryptographicException) { return false; }
    catch (ArgumentException) { return false; }
}

static LicenseRequest? DecryptRequest(EncryptedLicenseRequest request, string privateKeyPem)
{
    if (request.WrappedKey is null || request.Nonce is null || request.Ciphertext is null ||
        request.WrappedKey.Length > 2048 || request.Nonce.Length > 64 || request.Ciphertext.Length > 16_384)
        throw new FormatException("Encrypted request fields are invalid.");

    var wrappedKey = Convert.FromBase64String(request.WrappedKey);
    var nonce = Convert.FromBase64String(request.Nonce);
    var ciphertextAndTag = Convert.FromBase64String(request.Ciphertext);
    if (wrappedKey.Length != 384 || nonce.Length != 12 || ciphertextAndTag.Length is < 17 or > 8192)
        throw new FormatException("Encrypted request fields are invalid.");

    using var rsa = RSA.Create();
    rsa.ImportFromPem(privateKeyPem);
    var aesKey = rsa.Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256);
    try
    {
        if (aesKey.Length != 32) throw new CryptographicException("Invalid content-encryption key.");
        var plaintext = new byte[ciphertextAndTag.Length - 16];
        try
        {
            using var aes = new AesGcm(aesKey, 16);
            aes.Decrypt(nonce,
                ciphertextAndTag.AsSpan(0, ciphertextAndTag.Length - 16),
                ciphertextAndTag.AsSpan(ciphertextAndTag.Length - 16), plaintext);
            return JsonSerializer.Deserialize<LicenseRequest>(plaintext);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(aesKey);
        CryptographicOperations.ZeroMemory(ciphertextAndTag);
    }
}

internal sealed record EncryptedLicenseRequest(string? WrappedKey, string? Nonce, string? Ciphertext);
internal sealed record LicenseRequest(string? Name, string? Email, string? Phone, string? Country, string? MachineId);
