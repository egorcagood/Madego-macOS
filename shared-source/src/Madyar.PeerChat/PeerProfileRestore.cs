using System.Text.Json;
using System.Text.RegularExpressions;
namespace Madyar.PeerChat;
internal interface IRestoreFileSystem {
    void Write(string path,byte[] bytes);
    void Move(string from,string to);
    void Delete(string path);
}
internal sealed class RestoreFileSystem:IRestoreFileSystem {
    public void Write(string path,byte[] bytes)=>PeerFiles.Write(path,bytes);
    public void Move(string from,string to)=>Directory.Move(from,to);
    public void Delete(string path)=>File.Delete(path);
}
internal sealed record RestoreJournal(int SchemaVersion,string Stage,string Backup,string Phase);
internal static class PeerProfileRestore {
    internal static string JournalPath(string current)=>Path.Combine(Path.GetDirectoryName(current)!,"."+Path.GetFileName(current)+".restore.json");
    static string Owned(string current,string name,string kind) {
        var prefix="."+Path.GetFileName(current)+"."+kind+"-";
        if(!name.StartsWith(prefix,StringComparison.Ordinal)||!Regex.IsMatch(name[prefix.Length..],"^[a-f0-9]{32}$"))throw new InvalidDataException("Повреждённый путь восстановления.");
        var path=Path.Combine(Path.GetDirectoryName(current)!,name);PeerFiles.NoLinks(path);return path;
    }
    internal static void Recover(string current,IRestoreFileSystem fs) {
        var journal=JournalPath(current);PeerFiles.NoLinks(journal);if(!File.Exists(journal))return;
        var j=PeerFiles.Load<RestoreJournal>(journal);
        if(j.SchemaVersion!=1||j.Phase is not ("Prepared" or "OldMoved" or "NewMoved" or "RollingBack" or "Committed"))throw new InvalidDataException("Повреждённый журнал восстановления.");
        var stage=Owned(current,j.Stage,"restore");var backup=Owned(current,j.Backup,"backup");PeerFiles.NoLinks(current);
        if(j.Phase=="Committed") {
            if(!Directory.Exists(current))throw new InvalidDataException("Отсутствует восстановленный профиль.");
            fs.Delete(journal);return;
        }
        // Persist intent before rollback mutations. If backup has already been moved back,
        // RollingBack + current present is the completed rollback, safe to resume after a crash.
        if(j.Phase!="RollingBack") {
            if(!Directory.Exists(backup)&&(!Directory.Exists(current)||j.Phase!="Prepared"))throw new InvalidDataException("Не найден исходный профиль для отката.");
            fs.Write(journal,JsonSerializer.SerializeToUtf8Bytes(j with {Phase="RollingBack"},PeerFiles.Json));
        }
        if(Directory.Exists(backup)) {
            if(Directory.Exists(current))fs.Move(current,current+".failed-"+Guid.NewGuid().ToString("N"));
            fs.Move(backup,current);
        } else if(!Directory.Exists(current))throw new InvalidDataException("Не найден исходный профиль для отката.");
        fs.Delete(journal);
        // Incomplete protected staging is preserved; no plaintext key is ever written.
    }
    internal static void Apply(string current,BackupSnapshot snapshot,IIdentityProtector protector,IRestoreFileSystem fs,CancellationToken ct) {
        PeerBackupCodec.Validate(snapshot);PeerFiles.NoLinks(current);
        var parent=Path.GetDirectoryName(current)!;var name=Path.GetFileName(current);
        var stageName="."+name+".restore-"+Guid.NewGuid().ToString("N");var backupName="."+name+".backup-"+Guid.NewGuid().ToString("N");
        var stage=Path.Combine(parent,stageName);var backup=Path.Combine(parent,backupName);var journal=JournalPath(current);
        Directory.CreateDirectory(stage);
        PeerFiles.Save(Path.Combine(stage,"identity.json"),PeerIdentityStore.Create(snapshot.Seed,protector),false);
        PeerFiles.Save(Path.Combine(stage,"profile.json"),new ProfileDocument(1,snapshot.Profile.Nickname),false);
        var store=new PeerContactStore(stage);store.Initialize();foreach(var c in snapshot.Contacts) {ct.ThrowIfCancellationRequested();store.Add(c);}
        foreach(var c in snapshot.BlockedContacts??[]) {ct.ThrowIfCancellationRequested();store.Block(c);}
        _=PeerIdentityStore.Load(stage,protector);_=store.List();_=store.Blocked();ct.ThrowIfCancellationRequested();
        void Phase(string phase)=>fs.Write(journal,JsonSerializer.SerializeToUtf8Bytes(new RestoreJournal(1,stageName,backupName,phase),PeerFiles.Json));
        Phase("Prepared");fs.Move(current,backup);Phase("OldMoved");fs.Move(stage,current);Phase("NewMoved");
        _=PeerIdentityStore.Load(current,protector);_=new PeerContactStore(current).List();Phase("Committed");fs.Delete(journal);
    }
}
public sealed partial class PeerWorkspace {
    IRestoreFileSystem _restoreFiles = new RestoreFileSystem();
    bool _unavailable;
    internal static Task<PeerWorkspace> OpenAsync(string directory,IIdentityProtector protector,IRestoreFileSystem files,CancellationToken ct=default)=>OpenCoreAsync(directory,protector,files,ct);
    public static Task<PeerWorkspace> RestoreOfflineAsync(string directory,IIdentityProtector protector,PeerBackupPreview preview,bool confirmed,CancellationToken ct=default) => Task.Run(()=> {
        ct.ThrowIfCancellationRequested();if(!confirmed)throw new InvalidOperationException("Подтвердите замену повреждённого профиля.");
        directory=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);PeerFiles.NoLinks(directory);
        var parent=Path.GetDirectoryName(directory)!;var lockPath=Path.Combine(parent,"."+Path.GetFileName(directory)+".lock");PeerFiles.NoLinks(lockPath);
        var ownership=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var w=new PeerWorkspace(directory,protector,ownership);
        try {
            PeerProfileRestore.Recover(directory,w._restoreFiles);
            if(!Directory.Exists(directory))throw new InvalidDataException("Исходная папка профиля не найдена.");
            preview.Use(snapshot=> {PeerProfileRestore.Apply(directory,snapshot,protector,w._restoreFiles,ct);return true;});
            (w._identity,w._nickname)=PeerIdentityStore.Load(directory,protector);_=w.Contacts.List();w.InitializeMessaging();return w;
        } catch {
            try {PeerProfileRestore.Recover(directory,w._restoreFiles);}catch(Exception){/* journal remains for next startup */}
            ownership.Dispose();throw;
        }
    },ct);
    public Task<PeerProfile> RestoreBackupAsync(PeerBackupPreview preview,bool replaceConfirmed,CancellationToken ct=default) => Task.Run(()=>Locked(()=> {
        if(!replaceConfirmed)throw new InvalidOperationException("Подтвердите замену локального профиля и контактов.");
        return preview.Use(snapshot=> {
            try {InvalidateMessaging();PeerProfileRestore.Apply(_directory,snapshot,_protector,_restoreFiles,ct);(_identity,_nickname)=PeerIdentityStore.Load(_directory,_protector);InitializeMessaging();return Profile();}
            catch {
                try {PeerProfileRestore.Recover(_directory,_restoreFiles);(_identity,_nickname)=PeerIdentityStore.Load(_directory,_protector);InitializeMessaging();}
                catch { _unavailable=true; }
                throw;
            }
        });
    },ct),ct);
}
