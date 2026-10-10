using System.Security.Cryptography;
namespace Madyar.PeerChat;
public sealed partial class PeerWorkspace : IAsyncDisposable {
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly FileStream _ownership;
    readonly IIdentityProtector _protector;
    readonly string _directory;
    IdentityDocument _identity = null!;
    string _nickname = "";
    bool _disposed;
    PeerWorkspace(string directory, IIdentityProtector protector, FileStream ownership) { _directory = directory; _protector = protector; _ownership = ownership; }
    public static Task<PeerWorkspace> OpenAsync(string directory, IIdentityProtector protector, CancellationToken ct = default) => OpenCoreAsync(directory,protector,new RestoreFileSystem(),ct);
    static async Task<PeerWorkspace> OpenCoreAsync(string directory, IIdentityProtector protector,IRestoreFileSystem files, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        PeerFiles.NoLinks(directory); var parent = Path.GetDirectoryName(directory)!; Directory.CreateDirectory(parent);
        var lockPath = Path.Combine(parent, "." + Path.GetFileName(directory) + ".lock"); PeerFiles.NoLinks(lockPath);
        var ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var w = new PeerWorkspace(directory, protector, ownership) { _restoreFiles=files };
        try {
            PeerProfileRestore.Recover(directory,files);
            if (!Directory.Exists(directory)) {
                var staging = directory + ".create-" + Guid.NewGuid().ToString("N");
                var seed = RandomNumberGenerator.GetBytes(32);
                try {
                    Directory.CreateDirectory(staging);
                    PeerFiles.Save(Path.Combine(staging, "identity.json"), PeerIdentityStore.Create(seed, protector), false);
                    PeerFiles.Save(Path.Combine(staging, "profile.json"), new ProfileDocument(1, ""), false);
                    ct.ThrowIfCancellationRequested(); Directory.Move(staging, directory);
                } finally { CryptographicOperations.ZeroMemory(seed); if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            }
            (w._identity, w._nickname) = PeerIdentityStore.Load(directory, protector);
            w.Contacts.Initialize(); _ = w.Contacts.List(); _ = w.Contacts.Blocked(); w.InitializeMessaging();
            await Task.CompletedTask; return w;
        } catch { ownership.Dispose(); throw; }
    }
    async Task<T> Locked<T>(Func<T> action, CancellationToken ct) {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); if(_unavailable)throw new IOException("Восстановление прервано. Перезапустите приложение для восстановления профиля."); ct.ThrowIfCancellationRequested(); return action(); }
        finally { _gate.Release(); }
    }
    PeerProfile Profile() => new(_identity.PeerId, _identity.PublicKey.ToArray(), _nickname);
    public Task<PeerProfile> ReadProfileAsync(CancellationToken ct = default) => Locked(Profile, ct);
    public Task SetNicknameAsync(string name, CancellationToken ct = default) => Locked(() => {
        name = PeerFiles.Name(name);
        PeerFiles.Save(Path.Combine(_directory, "profile.json"), new ProfileDocument(1, name)); _nickname = name; return true;
    }, ct);
    byte[] ReadSeed() => _protector.Unprotect(_identity.ProtectedSeed);
    public async ValueTask DisposeAsync() {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; _ownership.Dispose(); }
        finally { _gate.Release(); }
    }
}
