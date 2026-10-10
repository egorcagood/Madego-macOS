using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using EgorVpn.Branding;
namespace EgorVpn.Services;

public static class WindowsStartupPolicy {
    static readonly XNamespace Schema="http://schemas.microsoft.com/windows/2004/02/mit/task";
    public static string InstalledExecutable(string localAppData,BrandProfile brand) {
        brand.Validate();
        if(string.IsNullOrWhiteSpace(localAppData)||!Path.IsPathFullyQualified(localAppData)||localAppData.StartsWith("//",StringComparison.Ordinal)||localAppData.StartsWith("\\\\",StringComparison.Ordinal)||localAppData.Any(char.IsControl))throw new ArgumentException("Не удалось определить папку установленного клиента.");
        return Path.GetFullPath(Path.Combine(localAppData,"Programs",brand.InstallFolder,brand.ExecutableName+".exe"));
    }
    public static bool IsInstalledExecutable(string? executable,string localAppData,BrandProfile brand) {
        if(string.IsNullOrWhiteSpace(executable)||executable.Any(c=>char.IsControl(c)||c=='"')||!Path.IsPathFullyQualified(executable))return false;
        try{return Path.GetFullPath(executable).Equals(InstalledExecutable(localAppData,brand),StringComparison.OrdinalIgnoreCase);}
        catch(Exception e)when(e is ArgumentException or NotSupportedException or PathTooLongException){return false;}
    }
    static void ValidateSid(string sid) {
        if(sid is null||sid.Length>184||!Regex.IsMatch(sid,@"\AS-1-[0-9]+(?:-[0-9]+)+\z",RegexOptions.CultureInvariant)||sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20")throw new ArgumentException("Автозапуск доступен только для текущего пользователя Windows.");
    }
    public static string TaskName(string userSid,BrandProfile brand) {
        brand.Validate();ValidateSid(userSid);
        var suffix=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userSid)))[..20];
        return brand.ApplicationId+"-Startup-"+suffix;
    }
    static string Source(string userSid,BrandProfile brand)=>"Madyar startup v1:"+TaskName(userSid,brand);
    static XElement Element(string name,object value)=>new(Schema+name,value);
    public static string BuildTaskXml(string executable,string localAppData,string userSid,BrandProfile brand) {
        if(!IsInstalledExecutable(executable,localAppData,brand))throw new ArgumentException("Автозапуск доступен для установленной копии клиента.");
        var name=TaskName(userSid,brand);executable=InstalledExecutable(localAppData,brand);
        return new XElement(Schema+"Task",new XAttribute("version","1.2"),
            new XElement(Schema+"RegistrationInfo",Element("Description",brand.ProductName+": запуск в трее при входе текущего пользователя."),Element("Source",Source(userSid,brand)),Element("URI","\\"+name)),
            new XElement(Schema+"Triggers",new XElement(Schema+"LogonTrigger",Element("Enabled",true),Element("UserId",userSid))),
            new XElement(Schema+"Principals",new XElement(Schema+"Principal",new XAttribute("id","CurrentUser"),Element("UserId",userSid),Element("LogonType","InteractiveToken"),Element("RunLevel","HighestAvailable"))),
            new XElement(Schema+"Settings",Element("MultipleInstancesPolicy","IgnoreNew"),Element("DisallowStartIfOnBatteries",false),Element("StopIfGoingOnBatteries",false),Element("AllowHardTerminate",false),Element("StartWhenAvailable",true),Element("RunOnlyIfNetworkAvailable",false),Element("AllowStartOnDemand",true),Element("Enabled",true),Element("Hidden",false),Element("RunOnlyIfIdle",false),Element("WakeToRun",false),Element("ExecutionTimeLimit","PT0S"),Element("Priority",7)),
            new XElement(Schema+"Actions",new XAttribute("Context","CurrentUser"),new XElement(Schema+"Exec",Element("Command",executable),Element("Arguments","--start-in-tray"),Element("WorkingDirectory",Path.GetDirectoryName(executable)!))))
            .ToString(SaveOptions.DisableFormatting);
    }
    public static bool IsOwnedTaskXml(string xml,string executable,string localAppData,string userSid,BrandProfile brand,string? verifiedCurrentAccount=null) {
        if(string.IsNullOrEmpty(xml)||xml.Length>65536||!IsInstalledExecutable(executable,localAppData,brand))return false;
        try {
            using var input=new StringReader(xml);
            using var reader=XmlReader.Create(input,new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536});
            var task=XElement.Load(reader);if(task.Name!=Schema+"Task")return false;
            var registration=task.Element(Schema+"RegistrationInfo");
            if(registration?.Element(Schema+"Source")?.Value!=Source(userSid,brand)||registration.Element(Schema+"URI")?.Value!="\\"+TaskName(userSid,brand))return false;
            var principals=task.Element(Schema+"Principals")?.Elements().ToArray();
            if(principals?.Length!=1||principals[0].Name!=Schema+"Principal"||!IsCurrentUser(principals[0].Element(Schema+"UserId")?.Value,userSid,verifiedCurrentAccount)||principals[0].Element(Schema+"GroupId") is not null)return false;
            var triggers=task.Element(Schema+"Triggers")?.Elements().ToArray();
            if(triggers?.Length!=1||triggers[0].Name!=Schema+"LogonTrigger"||!IsCurrentUser(triggers[0].Element(Schema+"UserId")?.Value,userSid,verifiedCurrentAccount))return false;
            var actions=task.Element(Schema+"Actions")?.Elements().ToArray();
            if(actions?.Length!=1||actions[0].Name!=Schema+"Exec"||actions[0].Element(Schema+"Arguments")?.Value!="--start-in-tray")return false;
            var expected=InstalledExecutable(localAppData,brand);
            return string.Equals(actions[0].Element(Schema+"Command")?.Value,expected,StringComparison.OrdinalIgnoreCase)&&SameDirectory(actions[0].Element(Schema+"WorkingDirectory")?.Value,Path.GetDirectoryName(expected)!);
        }catch(Exception e)when(e is XmlException or ArgumentException or InvalidOperationException){return false;}
    }
    public static bool IsDesiredTaskXml(string xml,string executable,string localAppData,string userSid,BrandProfile brand,string? verifiedCurrentAccount=null) {
        if(!IsOwnedTaskXml(xml,executable,localAppData,userSid,brand,verifiedCurrentAccount))return false;
        try {
            using var input=new StringReader(xml);
            using var reader=XmlReader.Create(input,new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536});
            var task=XElement.Load(reader);var principal=task.Element(Schema+"Principals")!.Elements().Single();
            var principalId=(string?)principal.Attribute("id");
            var context=(string?)task.Element(Schema+"Actions")!.Attribute("Context");
            if((principalId is not null&&principalId.Length==0)||(context is not null&&(context.Length==0||context!=principalId))||principal.Element(Schema+"GroupId") is not null||principal.Element(Schema+"LogonType")?.Value!="InteractiveToken"||principal.Element(Schema+"RunLevel")?.Value!="HighestAvailable")return false;
            var trigger=task.Element(Schema+"Triggers")!.Elements().Single();
            if(!Boolean(trigger,"Enabled",true))return false;
            var settings=task.Element(Schema+"Settings");if(settings is null)return false;
            return Boolean(settings,"Enabled",true)&&settings.Element(Schema+"MultipleInstancesPolicy")?.Value=="IgnoreNew"&&
                !Boolean(settings,"DisallowStartIfOnBatteries",true)&&!Boolean(settings,"StopIfGoingOnBatteries",true)&&
                !Boolean(settings,"RunOnlyIfNetworkAvailable",false)&&!Boolean(settings,"RunOnlyIfIdle",false)&&
                !Boolean(settings,"WakeToRun",false)&&!Boolean(settings,"AllowHardTerminate",true)&&
                XmlConvert.ToTimeSpan(settings.Element(Schema+"ExecutionTimeLimit")?.Value??"PT72H")==TimeSpan.Zero;
        }catch(Exception e)when(e is XmlException or ArgumentException or InvalidOperationException or FormatException){return false;}
    }
    // verifiedCurrentAccount is supplied only from the current token SID translated by Windows, never from task XML.
    static bool IsCurrentUser(string? value,string sid,string? verifiedCurrentAccount) {
        if(value==sid)return true;
        return value is not null&&!value.StartsWith("S-",StringComparison.OrdinalIgnoreCase)&&
            !string.IsNullOrEmpty(verifiedCurrentAccount)&&verifiedCurrentAccount.Length<=256&&!verifiedCurrentAccount.Any(char.IsControl)&&
            value.Equals(verifiedCurrentAccount,StringComparison.OrdinalIgnoreCase);
    }
    static bool SameDirectory(string? value,string expected) {
        if(string.IsNullOrWhiteSpace(value)||value.Any(c=>char.IsControl(c)||c=='"')||!Path.IsPathFullyQualified(value))return false;
        try{return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).Equals(Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar),StringComparison.OrdinalIgnoreCase);}
        catch(Exception e)when(e is ArgumentException or NotSupportedException or PathTooLongException){return false;}
    }
    public static string? ReadbackProblem(bool enabled,string xml,string executable,string localAppData,string userSid,BrandProfile brand,string? verifiedCurrentAccount=null) {
        if(!enabled)return "task-disabled";
        if(!IsOwnedTaskXml(xml,executable,localAppData,userSid,brand,verifiedCurrentAccount))return "owner-user-or-executable";
        return IsDesiredTaskXml(xml,executable,localAppData,userSid,brand,verifiedCurrentAccount)?null:"principal-or-settings";
    }
    static bool Boolean(XElement owner,string name,bool schemaDefault)=>owner.Element(Schema+name) is { } element?XmlConvert.ToBoolean(element.Value):schemaDefault;
    public static object? ReadTaskOrMissing(Func<object?> read) {
        try{return read();}catch(Exception e)when(IsMissingTaskError(e)){return null;}
    }
    public static void DeleteTaskOrMissing(Action delete) {
        try{delete();}catch(Exception e)when(IsMissingTaskError(e)){}
    }
    // Do not confuse SCHED_E_ACCOUNT_INFORMATION_NOT_SET (0x8004130F) with a missing task.
    public static bool IsMissingTaskError(Exception error)=>(error is System.Runtime.InteropServices.COMException or FileNotFoundException)&&IsMissingTaskError(error.HResult);
    public static bool IsMissingTaskError(int hresult)=>hresult==unchecked((int)0x80070002);
}
