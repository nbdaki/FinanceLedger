using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using FinanceLedger.Core.Data;

namespace FinanceLedger.Core.Security;

public sealed class LedgerCreateResult
{
    public required EncryptedLedger Ledger { get; init; }
    public required string RecoveryCode { get; init; }
}

public static class RecoveryCodeGenerator
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder();
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0 && i % 4 == 0)
                sb.Append('-');
            sb.Append(Alphabet[bytes[i] % Alphabet.Length]);
        }
        return sb.ToString();
    }

    public static string Normalize(string code) =>
        new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}

/// <summary>
/// 账簿容器：明文 SQLite 仅在解锁期间以用户专属 ACL 的临时文件存在，
/// 持久化文件始终为 AES-GCM 密文。数据密钥被密码与恢复码分别包装。
/// </summary>
public sealed class EncryptedLedger : IDisposable
{
    private static readonly byte[] Magic = "FLDG0001"u8.ToArray();
    private const int Version = 1;
    private const int SaltSize = 32;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Iterations = 210_000;

    private byte[]? _dataKey;
    private string? _tempDbPath;

    public string FilePath { get; }
    public string Name { get; private set; } = string.Empty;
    public LedgerDatabase? Database { get; private set; }
    public bool IsUnlocked => Database is not null;

    private EncryptedLedger(string filePath)
    {
        FilePath = filePath;
    }

    public static bool Exists(string path) => File.Exists(path);

    /// <summary>永久删除账簿容器文件。调用前应确保账簿已锁定。</summary>
    public static void Delete(string path)
    {
        var temp = path + ".tmp";
        if (File.Exists(temp))
            File.Delete(temp);
        if (File.Exists(path))
            File.Delete(path);
    }

    public static string DefaultLedgerPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FinanceLedger");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "personal.ledger");
    }

    public static LedgerCreateResult Create(string path, string name, string password)
    {
        PurgeTempFiles();
        if (File.Exists(path))
            throw new InvalidOperationException("账簿文件已存在。");

        var recoveryCode = RecoveryCodeGenerator.Generate();
        var dataKey = new byte[KeySize];
        RandomNumberGenerator.Fill(dataKey);

        var passwordSalt = new byte[SaltSize];
        RandomNumberGenerator.Fill(passwordSalt);
        var recoverySalt = new byte[SaltSize];
        RandomNumberGenerator.Fill(recoverySalt);

        var passwordWrap = Wrap(dataKey, password, passwordSalt);
        var recoveryWrap = Wrap(dataKey, RecoveryCodeGenerator.Normalize(recoveryCode), recoverySalt);

        var tempDb = NewTempDbPath();
        try
        {
            using (var db = new LedgerDatabase(tempDb))
            {
                db.Initialize();
                SetMeta(db, "ledger_name", name);
                SetMeta(db, "created_at", DateTimeOffset.Now.ToString("O"));
            }

            var payloadNonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(payloadNonce);
            var plain = File.ReadAllBytes(tempDb);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(dataKey, TagSize))
                aes.Encrypt(payloadNonce, plain, cipher, tag);

            WriteContainer(path, name, passwordSalt, passwordWrap, recoverySalt, recoveryWrap, payloadNonce, cipher, tag);
        }
        finally
        {
            TryDelete(tempDb);
            CryptographicOperations.ZeroMemory(dataKey);
        }

        var ledger = new EncryptedLedger(path) { Name = name };
        return new LedgerCreateResult { Ledger = ledger, RecoveryCode = recoveryCode };
    }

    public static EncryptedLedger Open(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到账簿文件。", path);
        PurgeTempFiles();
        return new EncryptedLedger(path) { Name = ReadName(path) };
    }

    public bool TryUnlock(string secret, bool useRecoveryCode)
    {
        if (IsUnlocked)
            return true;

        try
        {
            var header = ReadHeader(FilePath);
            var normalized = useRecoveryCode ? RecoveryCodeGenerator.Normalize(secret) : secret;
            var salt = useRecoveryCode ? header.RecoverySalt : header.PasswordSalt;
            var wrapped = useRecoveryCode ? header.RecoveryWrapped : header.PasswordWrapped;
            var key = DeriveKey(normalized, salt);
            byte[]? dek = null;
            try
            {
                dek = Unwrap(wrapped.AsSpan(), key);
                var plain = DecryptPayload(FilePath, header, dek);
                _tempDbPath = NewTempDbPath();
                File.WriteAllBytes(_tempDbPath, plain);
                RestrictToCurrentUser(_tempDbPath);
                CryptographicOperations.ZeroMemory(plain);

                _dataKey = dek;
                dek = null;
                Database = new LedgerDatabase(_tempDbPath);
                Database.Initialize();
                Name = GetMeta("ledger_name") ?? header.Name;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                if (dek is not null)
                    CryptographicOperations.ZeroMemory(dek);
            }
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public bool VerifyPassword(string password)
    {
        try
        {
            var header = ReadHeader(FilePath);
            var key = DeriveKey(password, header.PasswordSalt);
            try
            {
                var dek = Unwrap(header.PasswordWrapped.AsSpan(), key);
                CryptographicOperations.ZeroMemory(dek);
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch
        {
            return false;
        }
    }

    public void ChangePassword(string newPassword)
    {
        EnsureUnlocked();
        var header = ReadHeader(FilePath);
        var newSalt = new byte[SaltSize];
        RandomNumberGenerator.Fill(newSalt);
        var newWrap = Wrap(_dataKey!, newPassword, newSalt);
        header.PasswordSalt = newSalt;
        header.PasswordWrapped = newWrap;
        Save(header);
    }

    public void Save()
    {
        EnsureUnlocked();
        Save(ReadHeader(FilePath));
    }

    private void Save(Header header)
    {
        var plain = ReadDatabaseBytesSafely();
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(_dataKey!, TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);
        CryptographicOperations.ZeroMemory(plain);

        header.PayloadNonce = nonce;
        WriteContainer(FilePath, header.Name, header.PasswordSalt, header.PasswordWrapped,
            header.RecoverySalt, header.RecoveryWrapped, nonce, cipher, tag);
    }

    public string? GetMeta(string key)
    {
        EnsureUnlocked();
        using var cmd = Database!.Connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        EnsureUnlocked();
        SetMeta(Database!, key, value);
    }

    private static void SetMeta(LedgerDatabase db, string key, string value)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public void Lock()
    {
        Database?.Dispose();
        Database = null;
        _tempDbPath = null;
        if (_dataKey is not null)
            CryptographicOperations.ZeroMemory(_dataKey);
        _dataKey = null;
        PurgeTempFiles();
    }

    private void EnsureUnlocked()
    {
        if (!IsUnlocked)
            throw new InvalidOperationException("账簿尚未解锁。");
    }

    private static byte[] DeriveKey(string secret, byte[] salt)
    {
        var password = Encoding.UTF8.GetBytes(secret);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    private static byte[] Wrap(byte[] dataKey, string secret, byte[] salt)
    {
        var key = DeriveKey(secret, salt);
        try
        {
            var nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);
            var cipher = new byte[dataKey.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, dataKey, cipher, tag);
            return nonce.Concat(cipher).Concat(tag).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Unwrap(ReadOnlySpan<byte> wrapped, byte[] key)
    {
        var nonce = wrapped[..NonceSize];
        var cipher = wrapped[NonceSize..^TagSize];
        var tag = wrapped[^TagSize..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    private static byte[] DecryptPayload(string path, Header header, byte[] dataKey)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);
        fs.Position = header.PayloadOffset;
        var cipher = br.ReadBytes((int)header.PayloadLength);
        var tag = br.ReadBytes(TagSize);
        if (tag.Length != TagSize)
            throw new CryptographicException("账簿文件已损坏。");
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(dataKey, TagSize);
        aes.Decrypt(header.PayloadNonce, cipher, tag, plain);
        return plain;
    }

    private static string ReadName(string path)
    {
        try
        {
            return ReadHeader(path).Name;
        }
        catch
        {
            return Path.GetFileNameWithoutExtension(path);
        }
    }

    private static Header ReadHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);
        var magic = br.ReadBytes(8);
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException("不是有效的账簿文件。");
        var version = br.ReadInt32();
        if (version != Version)
            throw new InvalidDataException("账簿版本不受支持。");
        var iterations = br.ReadInt32();
        var nameLength = br.ReadInt32();
        var name = Encoding.UTF8.GetString(br.ReadBytes(nameLength));
        var passwordSalt = br.ReadBytes(SaltSize);
        var passwordWrapped = br.ReadBytes(NonceSize + KeySize + TagSize);
        var recoverySalt = br.ReadBytes(SaltSize);
        var recoveryWrapped = br.ReadBytes(NonceSize + KeySize + TagSize);
        var payloadNonce = br.ReadBytes(NonceSize);
        var payloadLength = br.ReadInt64();
        return new Header
        {
            Name = name,
            PasswordSalt = passwordSalt,
            PasswordWrapped = passwordWrapped,
            RecoverySalt = recoverySalt,
            RecoveryWrapped = recoveryWrapped,
            PayloadNonce = payloadNonce,
            PayloadLength = payloadLength,
            PayloadOffset = fs.Position,
            Iterations = iterations
        };
    }

    private static void WriteContainer(string path, string name, byte[] passwordSalt,
        byte[] passwordWrapped, byte[] recoverySalt, byte[] recoveryWrapped,
        byte[] payloadNonce, byte[] cipher, byte[] tag)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(Magic);
            bw.Write(Version);
            bw.Write(Iterations);
            bw.Write(nameBytes.Length);
            bw.Write(nameBytes);
            bw.Write(passwordSalt);
            bw.Write(passwordWrapped);
            bw.Write(recoverySalt);
            bw.Write(recoveryWrapped);
            bw.Write(payloadNonce);
            bw.Write((long)cipher.Length);
            bw.Write(cipher);
            bw.Write(tag);
        }

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, ms.ToArray());
        RestrictToCurrentUser(temp);
        if (File.Exists(path))
            File.Delete(path);
        File.Move(temp, path);
    }

    private byte[] ReadDatabaseBytesSafely()
    {
        var connection = Database?.Connection ?? throw new InvalidOperationException("账簿尚未解锁。");
        connection.Close();
        try
        {
            return File.ReadAllBytes(_tempDbPath!);
        }
        finally
        {
            connection.Open();
        }
    }

    private sealed class Header
    {
        public string Name { get; set; } = string.Empty;
        public byte[] PasswordSalt { get; set; } = [];
        public byte[] PasswordWrapped { get; set; } = [];
        public byte[] RecoverySalt { get; set; } = [];
        public byte[] RecoveryWrapped { get; set; } = [];
        public byte[] PayloadNonce { get; set; } = [];
        public long PayloadLength { get; set; }
        public long PayloadOffset { get; set; }
        public int Iterations { get; set; }
    }

    private static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FinanceLedger", "working");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string NewTempDbPath() => Path.Combine(TempDirectory(), Guid.NewGuid().ToString("N") + ".db");

    private static void PurgeTempFiles()
    {
        try
        {
            foreach (var file in Directory.GetFiles(TempDirectory(), "*.db"))
                TryDelete(file);
        }
        catch
        {
            // 临时目录不可访问时忽略，解锁会退化为直接使用新路径。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 忽略清理失败，下一次启动会重试。
        }
    }

    private static void RestrictToCurrentUser(string path)
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent().User;
            if (identity is null)
                return;
            var file = new FileInfo(path);
            var security = file.GetAccessControl();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(
                identity, FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(security);
        }
        catch
        {
            // 某些环境不允许修改 ACL，密文文件本身仍受密码保护。
        }
    }

    public void Dispose() => Lock();
}
