using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DOSI.CORE.UserManagement;

namespace DOSI.CORE.Security;

/// <summary>
/// Per-user file encryption using AES-GCM (authenticated encryption with a
/// 256-bit data key). Provides transparent <c>ReadAllBytes</c>/<c>WriteAllBytes</c>
/// helpers that automatically encrypt on write and decrypt on read.
/// <para>
/// Key model:
/// <list type="bullet">
///   <item>The user's password derives a <em>password key</em> via PBKDF2-SHA256 (separate salt
///   from the auth-password salt, stored in <see cref="DOSIUser.VaultPasswordSalt"/>).</item>
///   <item>A long-lived random <em>data key</em> is generated on first sign-in after the vault
///   is enabled and is wrapped (AES-GCM-encrypted) with the password key. The wrapped blob
///   plus its nonce are stored in the user's account JSON (<see cref="DOSIUser.VaultWrappedDataKey"/>,
///   <see cref="DOSIUser.VaultDataKeyNonce"/>).</item>
///   <item>On password change, only the wrapped data key is re-wrapped - no file content has
///   to be re-encrypted.</item>
///   <item>The unwrapped data key lives in process memory only between sign-in and sign-out
///   and is zeroed on sign-out.</item>
/// </list>
/// </para>
/// <para>
/// On-disk file format (see <see cref="WriteAllBytes"/>):
/// <c>"DOSV1\0" magic (6 bytes) || nonce (12 bytes) || tag (16 bytes) || ciphertext</c>.
/// Files without the magic are treated as plaintext (used for backward
/// compatibility / opt-in migration).
/// </para>
/// </summary>
public static class UserVault
{
    /// <summary>Magic header (6 bytes) identifying a DOSI vault file v1.</summary>
    public static readonly byte[] FileMagic = { (byte)'D', (byte)'O', (byte)'S', (byte)'V', (byte)'1', 0 };

    private const int DataKeySizeBytes = 32;   // AES-256
    private const int NonceSizeBytes = 12;     // AES-GCM nonce
    private const int TagSizeBytes = 16;       // AES-GCM tag
    private const int VaultSaltSizeBytes = 16;
    private const int VaultIterations = 120_000;

    private static readonly object SyncRoot = new();
    private static byte[]? _currentDataKey;        // unwrapped 32-byte AES-256 key (or null if locked)
    private static string? _currentDataKeyOwner;   // username currently unlocked

    static UserVault()
    {
        // Zero the in-memory data key on sign-out so a stolen process dump
        // after sign-out doesn't leak the file-encryption key.
        UserManager.UserSignedOut += (_, _) => Lock();
    }

    /// <summary>
    /// True when a data key for <see cref="UserManager.CurrentUser"/> is loaded
    /// in memory and the vault helpers can encrypt/decrypt.
    /// </summary>
    public static bool IsUnlocked
    {
        get
        {
            lock (SyncRoot)
            {
                var user = UserManager.CurrentUser;
                return _currentDataKey != null && user != null &&
                       string.Equals(_currentDataKeyOwner, user.Username, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// True when the user's account already has a wrapped data key on file
    /// (i.e. the vault has been enabled at some point in the past).
    /// </summary>
    public static bool IsEnabledForUser(DOSIUser user) =>
        user != null &&
        !string.IsNullOrEmpty(user.VaultWrappedDataKey) &&
        !string.IsNullOrEmpty(user.VaultDataKeyNonce) &&
        !string.IsNullOrEmpty(user.VaultPasswordSalt);

    /// <summary>
    /// Enables the vault for <paramref name="user"/> by generating a fresh
    /// random data key, wrapping it with the user's password, and persisting
    /// the wrapped blob into the account JSON. No-op if already enabled.
    /// Returns <c>true</c> on success.
    /// </summary>
    public static bool EnableForUser(DOSIUser user, string password)
    {
        if (user == null || string.IsNullOrEmpty(password)) return false;
        if (IsEnabledForUser(user)) return true;

        try
        {
            var salt = RandomNumberGenerator.GetBytes(VaultSaltSizeBytes);
            var passwordKey = DerivePasswordKey(password, salt);

            var dataKey = RandomNumberGenerator.GetBytes(DataKeySizeBytes);
            var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
            var ciphertext = new byte[dataKey.Length];
            var tag = new byte[TagSizeBytes];

            using (var aes = new AesGcm(passwordKey, TagSizeBytes))
            {
                aes.Encrypt(nonce, dataKey, ciphertext, tag);
            }

            // Pack: ciphertext || tag (we store nonce separately).
            var wrapped = new byte[ciphertext.Length + tag.Length];
            Buffer.BlockCopy(ciphertext, 0, wrapped, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, wrapped, ciphertext.Length, tag.Length);

            user.VaultPasswordSalt = Convert.ToBase64String(salt);
            user.VaultPasswordIterations = VaultIterations;
            user.VaultWrappedDataKey = Convert.ToBase64String(wrapped);
            user.VaultDataKeyNonce = Convert.ToBase64String(nonce);
            UserManager.SaveUser(user);

            // Stash unwrapped key for current process if this is the active user.
            if (UserManager.CurrentUser != null &&
                string.Equals(UserManager.CurrentUser.Username, user.Username, StringComparison.OrdinalIgnoreCase))
            {
                lock (SyncRoot)
                {
                    _currentDataKey = dataKey;
                    _currentDataKeyOwner = user.Username;
                }
            }
            else
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }

            CryptographicOperations.ZeroMemory(passwordKey);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Unwraps the data key for <paramref name="user"/> using the supplied
    /// password and stashes it in process memory. Called by
    /// <see cref="UserManager.Authenticate"/>. Returns <c>true</c> on success.
    /// </summary>
    public static bool Unlock(DOSIUser user, string password)
    {
        if (user == null || string.IsNullOrEmpty(password)) return false;
        if (!IsEnabledForUser(user)) return false;

        byte[]? passwordKey = null;
        byte[]? dataKey = null;
        try
        {
            var salt = Convert.FromBase64String(user.VaultPasswordSalt!);
            var nonce = Convert.FromBase64String(user.VaultDataKeyNonce!);
            var wrapped = Convert.FromBase64String(user.VaultWrappedDataKey!);
            if (wrapped.Length < TagSizeBytes) return false;

            var iterations = user.VaultPasswordIterations <= 0 ? VaultIterations : user.VaultPasswordIterations;
            passwordKey = DerivePasswordKeyWithIterations(password, salt, iterations);

            var ciphertext = new byte[wrapped.Length - TagSizeBytes];
            var tag = new byte[TagSizeBytes];
            Buffer.BlockCopy(wrapped, 0, ciphertext, 0, ciphertext.Length);
            Buffer.BlockCopy(wrapped, ciphertext.Length, tag, 0, TagSizeBytes);

            dataKey = new byte[DataKeySizeBytes];
            using (var aes = new AesGcm(passwordKey, TagSizeBytes))
            {
                aes.Decrypt(nonce, ciphertext, tag, dataKey);
            }

            lock (SyncRoot)
            {
                if (_currentDataKey != null) CryptographicOperations.ZeroMemory(_currentDataKey);
                _currentDataKey = dataKey;
                _currentDataKeyOwner = user.Username;
            }
            dataKey = null; // ownership transferred
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (passwordKey != null) CryptographicOperations.ZeroMemory(passwordKey);
            if (dataKey != null) CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>
    /// Re-wraps the data key with a new password. Called by
    /// <see cref="UserManager.UpdatePassword"/>. Requires the vault to be
    /// currently unlocked (so we have the unwrapped data key in memory).
    /// </summary>
    public static bool RewrapForPasswordChange(DOSIUser user, string newPassword)
    {
        if (user == null || string.IsNullOrEmpty(newPassword)) return false;
        if (!IsEnabledForUser(user)) return true; // nothing to do
        if (!IsUnlocked) return false;

        byte[]? passwordKey = null;
        try
        {
            var salt = RandomNumberGenerator.GetBytes(VaultSaltSizeBytes);
            passwordKey = DerivePasswordKey(newPassword, salt);

            byte[] dataKey;
            lock (SyncRoot) { dataKey = (byte[])_currentDataKey!.Clone(); }

            var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
            var ciphertext = new byte[dataKey.Length];
            var tag = new byte[TagSizeBytes];

            try
            {
                using var aes = new AesGcm(passwordKey, TagSizeBytes);
                aes.Encrypt(nonce, dataKey, ciphertext, tag);
            }
            finally { CryptographicOperations.ZeroMemory(dataKey); }

            var wrapped = new byte[ciphertext.Length + tag.Length];
            Buffer.BlockCopy(ciphertext, 0, wrapped, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, wrapped, ciphertext.Length, tag.Length);

            user.VaultPasswordSalt = Convert.ToBase64String(salt);
            user.VaultPasswordIterations = VaultIterations;
            user.VaultWrappedDataKey = Convert.ToBase64String(wrapped);
            user.VaultDataKeyNonce = Convert.ToBase64String(nonce);
            UserManager.SaveUser(user);
            return true;
        }
        catch { return false; }
        finally { if (passwordKey != null) CryptographicOperations.ZeroMemory(passwordKey); }
    }

    /// <summary>Zeroes the in-memory data key. Called automatically on sign-out.</summary>
    public static void Lock()
    {
        lock (SyncRoot)
        {
            if (_currentDataKey != null) CryptographicOperations.ZeroMemory(_currentDataKey);
            _currentDataKey = null;
            _currentDataKeyOwner = null;
        }
    }

    // ===== Public IO helpers ===================================================

    /// <summary>
    /// Returns <c>true</c> if the on-disk file at <paramref name="path"/> begins
    /// with the DOSI vault magic header. Cheap; does not decrypt.
    /// </summary>
    public static bool IsEncryptedFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = File.OpenRead(path);
            Span<byte> buf = stackalloc byte[6];
            var read = fs.Read(buf);
            if (read < FileMagic.Length) return false;
            for (int i = 0; i < FileMagic.Length; i++)
                if (buf[i] != FileMagic[i]) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Reads <paramref name="path"/>. If the file is encrypted, the vault must
    /// be unlocked. If it is plaintext, the raw bytes are returned (legacy /
    /// pre-vault files). Throws on decryption failure or path-access denial.
    /// </summary>
    public static byte[] ReadAllBytes(string path)
    {
        UserSandbox.AssertReadAccess(path);

        if (!IsEncryptedFile(path)) return File.ReadAllBytes(path);

        if (!IsUnlocked)
            throw new InvalidOperationException(
                "User vault is locked - cannot decrypt file. Sign in or call UserVault.Unlock first.");

        var raw = File.ReadAllBytes(path);
        var headerLen = FileMagic.Length + NonceSizeBytes + TagSizeBytes;
        if (raw.Length < headerLen)
            throw new CryptographicException("Encrypted file is truncated.");

        var nonce = new byte[NonceSizeBytes];
        var tag = new byte[TagSizeBytes];
        Buffer.BlockCopy(raw, FileMagic.Length, nonce, 0, NonceSizeBytes);
        Buffer.BlockCopy(raw, FileMagic.Length + NonceSizeBytes, tag, 0, TagSizeBytes);

        var ctLen = raw.Length - headerLen;
        var ciphertext = new byte[ctLen];
        Buffer.BlockCopy(raw, headerLen, ciphertext, 0, ctLen);

        var plaintext = new byte[ctLen];
        byte[] keyCopy;
        lock (SyncRoot) { keyCopy = (byte[])_currentDataKey!.Clone(); }
        try
        {
            using var aes = new AesGcm(keyCopy, TagSizeBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        finally { CryptographicOperations.ZeroMemory(keyCopy); }

        return plaintext;
    }

    /// <summary>UTF-8 convenience over <see cref="ReadAllBytes"/>.</summary>
    public static string ReadAllText(string path) => Encoding.UTF8.GetString(ReadAllBytes(path));

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/>, encrypting
    /// it with the unlocked data key when the vault is available. When the
    /// vault is locked the bytes are written as plaintext (no fail-closed
    /// here so guest / pre-setup flows still work).
    /// </summary>
    public static void WriteAllBytes(string path, byte[] content)
    {
        UserSandbox.AssertWriteAccess(path);
        if (content == null) throw new ArgumentNullException(nameof(content));

        if (!IsUnlocked) { File.WriteAllBytes(path, content); return; }

        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[content.Length];
        var tag = new byte[TagSizeBytes];

        byte[] keyCopy;
        lock (SyncRoot) { keyCopy = (byte[])_currentDataKey!.Clone(); }
        try
        {
            using var aes = new AesGcm(keyCopy, TagSizeBytes);
            aes.Encrypt(nonce, content, ciphertext, tag);
        }
        finally { CryptographicOperations.ZeroMemory(keyCopy); }

        // Layout: magic || nonce || tag || ciphertext
        var output = new byte[FileMagic.Length + nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(FileMagic, 0, output, 0, FileMagic.Length);
        Buffer.BlockCopy(nonce, 0, output, FileMagic.Length, nonce.Length);
        Buffer.BlockCopy(tag, 0, output, FileMagic.Length + nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, output, FileMagic.Length + nonce.Length + tag.Length, ciphertext.Length);

        File.WriteAllBytes(path, output);
    }

    /// <summary>UTF-8 convenience over <see cref="WriteAllBytes"/>.</summary>
    public static void WriteAllText(string path, string content) =>
        WriteAllBytes(path, Encoding.UTF8.GetBytes(content ?? string.Empty));

    /// <summary>
    /// Walks the current user's home folder and re-writes every plaintext file
    /// through the vault. Use as an opt-in migration step (e.g. from a
    /// "Migrate my files" Settings button) - <em>not</em> automatic.
    /// Returns (encryptedCount, skippedCount, failedCount).
    /// </summary>
    public static (int Encrypted, int Skipped, int Failed) MigrateCurrentUserFiles()
    {
        var user = UserManager.CurrentUser;
        if (user == null || !IsUnlocked) return (0, 0, 0);

        int enc = 0, skip = 0, fail = 0;
        var root = UserManager.GetUserFolder(user.Username);
        if (!Directory.Exists(root)) return (0, 0, 0);

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            // Skip the account JSON (it must remain readable for login) and audit log.
            var name = Path.GetFileName(path);
            if (string.Equals(name, user.Username + ".json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, ".audit.log", StringComparison.OrdinalIgnoreCase))
            {
                skip++; continue;
            }

            try
            {
                if (IsEncryptedFile(path)) { skip++; continue; }
                var bytes = File.ReadAllBytes(path);
                WriteAllBytes(path, bytes);
                enc++;
            }
            catch { fail++; }
        }
        return (enc, skip, fail);
    }

    private static byte[] DerivePasswordKey(string password, byte[] salt) =>
        DerivePasswordKeyWithIterations(password, salt, VaultIterations);

    private static byte[] DerivePasswordKeyWithIterations(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, DataKeySizeBytes);
}
