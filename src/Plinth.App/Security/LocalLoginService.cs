using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Plinth.App.Security;

/// <summary>受信任部署的本地登录开关；默认关闭，开启后启动时必须先登录。</summary>
public sealed class LocalLoginOptions
{
    public bool Enabled { get; set; }
    /// <summary>首次设置的默认用户名；为空时由用户在首次启动时填写。</summary>
    public string Username { get; set; } = "";
}

public sealed record LocalLoginResult(bool Success, string? ErrorKey = null);

public sealed class LocalLoginException(string errorKey) : Exception(errorKey)
{
    public string ErrorKey { get; } = errorKey;
}

/// <summary>
/// 本机登录门禁：只保存 PBKDF2 哈希，不保存明文。它是桌面端的使用门槛，
/// 不是数据加密或抗本地文件访问的安全边界；远程身份与权限仍由 OIDC 决定。
/// </summary>
public sealed class LocalLoginService
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;
    public const int MaxUsernameLength = 64;
    public const int PasswordHashIterations = 210_000;
    private const string HashAlgorithm = "pbkdf2-sha256";
    private const string FileName = "login.json";

    private readonly LocalLoginOptions _options;
    private readonly string _path;

    public LocalLoginService(LocalLoginOptions options, string dataDirectory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _path = Path.Combine(dataDirectory, FileName);
    }

    public bool IsEnabled => _options.Enabled;
    public string CredentialPath => _path;
    public string PrefillUsername => Normalize(_options.Username);

    /// <summary>没有可解析的凭据文件时为 true；损坏文件也按未设置处理，便于重新设置。</summary>
    public bool NeedsSetup => IsEnabled && TryRead() is null;

    public string ReadUsername() => TryRead()?.Username ?? "";

    public static string? ValidateUsername(string? value)
    {
        var name = Normalize(value);
        if (name.Length is < 1 or > MaxUsernameLength || name.Any(char.IsControl))
            return "login.error.username";
        return null;
    }

    public static string? ValidatePassword(string? value)
    {
        if (value is null || value.Length is < MinPasswordLength or > MaxPasswordLength)
            return "login.error.passwordLength";
        if (value.Any(char.IsControl))
            return "login.error.passwordControl";
        return null;
    }

    public async Task<LocalLoginResult> AuthenticateAsync(string? username, string? password,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled) throw new InvalidOperationException("Local login is disabled.");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return new(false, "login.error.required");

        Credential? credential;
        try
        {
            credential = TryRead();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, "login.error.storage");
        }

        if (credential is null) return new(false, "login.error.setupRequired");
        var name = Normalize(username);
        var verified = await Task.Run(() => Verify(name, password, credential), cancellationToken);
        return verified ? new(true) : new(false, "login.error.invalid");
    }

    public async Task CreateCredentialAsync(string? username, string? password,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled) throw new InvalidOperationException("Local login is disabled.");
        var name = Normalize(username);
        var usernameError = ValidateUsername(name);
        if (usernameError is not null) throw new LocalLoginException(usernameError);
        var passwordError = ValidatePassword(password);
        if (passwordError is not null) throw new LocalLoginException(passwordError);

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = await Task.Run(() => Derive(password!, salt, PasswordHashIterations), cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(new CredentialFile
            {
                Version = 1,
                Username = name,
                PasswordHash = Format(salt, PasswordHashIterations, hash),
            });
            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temp, _path, overwrite: true);
            }
            finally
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new LocalLoginException("login.error.storage");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private Credential? TryRead()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var file = JsonSerializer.Deserialize<CredentialFile>(File.ReadAllText(_path, Encoding.UTF8));
            if (file is null || file.Version != 1) return null;
            if (ValidateUsername(file.Username) is not null) return null;
            if (!TryParse(file.PasswordHash, out var salt, out var iterations, out var hash)) return null;
            return new Credential(file.Username.Trim(), salt, iterations, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or FormatException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool Verify(string username, string password, Credential credential)
    {
        if (!string.Equals(username, credential.Username, StringComparison.Ordinal)) return false;
        var derived = Derive(password, credential.Salt, credential.Iterations);
        try { return CryptographicOperations.FixedTimeEquals(derived, credential.Hash); }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations,
            HashAlgorithmName.SHA256, 32);

    private static string Format(byte[] salt, int iterations, byte[] hash) =>
        $"{HashAlgorithm}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";

    private static bool TryParse(string? value, out byte[] salt, out int iterations, out byte[] hash)
    {
        salt = [];
        iterations = 0;
        hash = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], HashAlgorithm, StringComparison.Ordinal)
            || !int.TryParse(parts[1], out iterations)
            || iterations is < 10_000 or > 1_000_000)
            return false;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        if (salt.Length != 16 || hash.Length != 32)
        {
            salt = [];
            hash = [];
            return false;
        }
        return true;
    }

    private static string Normalize(string? value) => (value ?? "").Trim();

    private sealed record Credential(string Username, byte[] Salt, int Iterations, byte[] Hash);

    private sealed class CredentialFile
    {
        public int Version { get; set; }
        public string Username { get; set; } = "";
        public string PasswordHash { get; set; } = "";
    }
}
