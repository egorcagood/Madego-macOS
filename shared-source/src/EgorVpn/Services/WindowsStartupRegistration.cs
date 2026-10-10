using Madyar.Localization;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using EgorVpn.Branding;
namespace EgorVpn.Services;

[SupportedOSPlatform("windows")]
public static class WindowsStartupRegistration {
    static readonly object Gate=new();
    public static bool IsInstalledInstance {
        get {
            if(!OperatingSystem.IsWindows()||!WindowsStartupPolicy.IsInstalledExecutable(Environment.ProcessPath,AppPaths.LocalAppData,Brand.Current))return false;
            try{AssertPhysicalInstalledFile(Environment.ProcessPath!);return true;}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException){return false;}
        }
    }
    public static void Apply(bool enabled) {
        var stage="preflight";
        try{ApplyCore(enabled,ref stage);}
        catch(WindowsStartupConfigurationException){throw;}
        catch(Exception error){throw new WindowsStartupConfigurationException(error.Message,error,stage);}
    }
    static void ApplyCore(bool enabled,ref string stage) {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Автозапуск настраивается только в Windows.");
        if(!WindowsStartupPolicy.IsInstalledExecutable(Environment.ProcessPath,AppPaths.LocalAppData,Brand.Current))throw new InvalidOperationException("Автозапуск доступен для установленной копии клиента.");
        var executable=WindowsStartupPolicy.InstalledExecutable(AppPaths.LocalAppData,Brand.Current);AssertPhysicalInstalledFile(executable);
        stage="current-user-rights";
        using var user=WindowsIdentity.GetCurrent();
        if(!new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator))throw new InvalidOperationException("Настройте автозапуск из клиента, открытого с разрешением Windows.");
        var sid=user.User?.Value??throw new InvalidOperationException("Не удалось определить пользователя Windows.");
        stage="current-user-identity";
        // Translate the trusted current SID once. Never resolve an account name obtained from untrusted task XML.
        string? account=null;
        try{account=((NTAccount)user.User!.Translate(typeof(NTAccount))).Value;}
        catch(IdentityNotMappedException){/* A literal matching SID remains verifiable; an unverified account spelling stays rejected. */}
        var name=WindowsStartupPolicy.TaskName(sid,Brand.Current);
        lock(Gate) {
            object? service=null,folder=null,existing=null,registered=null;
            try {
                stage="scheduler-connect";
                var type=Type.GetTypeFromProgID("Schedule.Service")??throw new InvalidOperationException("Планировщик задач Windows недоступен.");
                service=Activator.CreateInstance(type)??throw new InvalidOperationException("Не удалось открыть планировщик задач Windows.");
                ((dynamic)service).Connect(); // Local service/current token; no server, username, password or domain arguments.
                stage="root-folder";
                folder=((dynamic)service).GetFolder("\\");
                stage="task-lookup";
                existing=WindowsStartupPolicy.ReadTaskOrMissing(()=>((dynamic)folder).GetTask(name));
                if(existing is not null) {
                    stage="existing-task-validation";
                    string xml=((dynamic)existing).Xml;
                    if(!WindowsStartupPolicy.IsOwnedTaskXml(xml,executable,AppPaths.LocalAppData,sid,Brand.Current,account))throw new InvalidOperationException("Задача с таким именем принадлежит другой настройке. Автозапуск не изменён.");
                    if(enabled&&(bool)((dynamic)existing).Enabled&&WindowsStartupPolicy.IsDesiredTaskXml(xml,executable,AppPaths.LocalAppData,sid,Brand.Current,account))return;
                    Release(existing);existing=null;
                }else if(!enabled)return;
                if(enabled) {
                    // TASK_CREATE_OR_UPDATE | TASK_IGNORE_REGISTRATION_TRIGGERS=38; interactive token=3, no stored password.
                    stage="task-registration";
                    registered=((dynamic)folder).RegisterTask(name,WindowsStartupPolicy.BuildTaskXml(executable,AppPaths.LocalAppData,sid,Brand.Current),38,sid,null,3,null);
                    stage="registered-task-readback";
                    var problem=registered is null?"missing-readback":WindowsStartupPolicy.ReadbackProblem((bool)((dynamic)registered).Enabled,(string)((dynamic)registered).Xml,executable,AppPaths.LocalAppData,sid,Brand.Current,account);
                    if(problem is not null)throw new InvalidOperationException(UiText.F("Не удалось подтвердить сохранённые параметры автозапуска Windows. Проверка: {0}.",problem));
                } else {
                    stage="task-delete";
                    WindowsStartupPolicy.DeleteTaskOrMissing(()=>((dynamic)folder).DeleteTask(name,0));
                }
            }catch(Exception e)when(e is COMException or IOException or UnauthorizedAccessException) {
                throw new InvalidOperationException(UiText.F("Не удалось настроить автозапуск Windows. Код 0x{0}.",e.HResult.ToString("X8")),e);
            }finally {Release(registered);Release(existing);Release(folder);Release(service);}
        }
    }
    static void AssertPhysicalInstalledFile(string executable) {
        if(new DriveInfo(Path.GetPathRoot(executable)!).DriveType==DriveType.Network)throw new IOException("Автозапуск не настраивается с сетевого диска.");
        var paths=new System.Collections.Generic.Stack<string>();
        for(var path=Path.GetFullPath(executable);!string.IsNullOrEmpty(path);path=Path.GetDirectoryName(path))paths.Push(path);
        // Inspect parents before descendants, so a junction cannot redirect a later filesystem check.
        while(paths.Count>0)if((File.GetAttributes(paths.Pop())&FileAttributes.ReparsePoint)!=0)throw new IOException("Автозапуск не настраивается через ссылки или junction.");
        if((File.GetAttributes(executable)&(FileAttributes.Directory|FileAttributes.Device))!=0)throw new IOException("Установленная копия клиента не является обычным файлом.");
    }
    static void Release(object? value) {
        if(value is null||!Marshal.IsComObject(value))return;
        try{Marshal.FinalReleaseComObject(value);}
        catch(InvalidComObjectException){/* Already released RCW; do not prevent release of the other owned COM objects. */}
    }
}

public sealed class WindowsStartupConfigurationException : InvalidOperationException {
    public string Stage { get; }
    public WindowsStartupConfigurationException(string message,Exception innerException,string stage):base(message,innerException){Stage=stage;}
    public override string ToString()=>base.ToString()+Environment.NewLine+"StartupStage: "+Stage;
}
