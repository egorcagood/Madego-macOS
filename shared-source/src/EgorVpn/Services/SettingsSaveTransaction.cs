using EgorVpn.Models;
namespace EgorVpn.Services;
// Called under the view model mutation gate. Scheduler writes occur only for an explicit setting change.
public static class SettingsSaveTransaction {
    public static async Task SaveAsync(AppSettings previous,AppSettings snapshot,bool canConfigureStartup,Action<bool> applyStartup,Func<AppSettings,Task> persist,Action<bool> resetDraftStartup) {
        var changed=canConfigureStartup&&previous.StartWithWindows!=snapshot.StartWithWindows;
        if(!changed){await persist(snapshot);return;}
        try{applyStartup(snapshot.StartWithWindows);await persist(snapshot);}
        catch(Exception originalError) {
            Exception? rollbackError=null;
            // An apply error can arrive after a scheduler mutation/readback. Reconcile the previous state too.
            try{applyStartup(previous.StartWithWindows);}catch(Exception error){rollbackError=error;}
            finally{resetDraftStartup(previous.StartWithWindows);}
            if(rollbackError is not null)throw new SettingsStartupRollbackException(originalError,rollbackError);
            throw;
        }
    }
}
public sealed class SettingsStartupRollbackException : AggregateException {
    public SettingsStartupRollbackException(Exception originalError,Exception rollbackError):base("Не удалось сохранить настройки и восстановить автозапуск.",originalError,rollbackError){}
}
