using EgorVpn.Models;
using EgorVpn.Services;
namespace Madego.Core;

// Export only the selected connection parameters. Never forward raw URI/JSON or subscription/display metadata.
public static class CanonicalServerShare
{
    static bool ContainsNestedLink(string value)
    {
        static bool Linked(string text)=>text.Contains("://",StringComparison.Ordinal)||text.Contains("madyar:",StringComparison.OrdinalIgnoreCase);
        for(int round=0;round<32;round++) {
            if(Linked(value))return true;
            // Known parameters still cannot conceal a source URL through a reversible text encoding.
            try {var compact=value.Replace('-','+').Replace('_','/');compact+=new string('=',(4-compact.Length%4)%4);
                var decoded=new System.Text.UTF8Encoding(false,true).GetString(Convert.FromBase64String(compact));if(Linked(decoded))return true;
            }catch(FormatException){}catch(System.Text.DecoderFallbackException){}
            var next=Uri.UnescapeDataString(value);if(next==value)return false;value=next;
        }
        return true; // Deep opaque encodings are not a safely shareable transport parameter.
    }
    public static string Export(VpnNode node)
    {
        if (node.Protocol != "vless" || !Guid.TryParse(node.UserId,out var uuid) || node.Port is < 1 or > 65535)
            throw new ArgumentException("Invalid VLESS server.");
        if (node.Transport is not ("raw" or "websocket" or "httpupgrade" or "grpc" or "xhttp") || node.Security is not ("none" or "tls" or "reality"))
            throw new ArgumentException("Unsupported server transport/security.");
        // XHTTP extra can contain arbitrary nested server/URL objects. Preserve it for local VPN configs,
        // but do not share it until a recursive capability whitelist is established.
        if (!string.IsNullOrWhiteSpace(node.ExtraJson))
            throw new ArgumentException("Opaque transport options cannot be shared safely.");
        var host=node.Host.Trim('[',']');
        if (Uri.CheckHostName(host)==UriHostNameType.Unknown) throw new ArgumentException("Invalid server host.");
        var fields=new List<KeyValuePair<string,string>>();
        void Add(string key,string value) {
            if (value.Length > 8192 || value.Any(char.IsControl) || ContainsNestedLink(value))
                throw new ArgumentException("Nested links or control characters cannot be shared.");
            if (!string.IsNullOrEmpty(value)) fields.Add(new(key,value));
        }
        Add("type",node.Transport switch {"raw"=>"tcp","websocket"=>"ws",_=>node.Transport});
        Add("security",node.Security);Add("encryption",node.Encryption);Add("flow",node.Flow);
        Add("sni",node.Sni);Add("fp",node.Fingerprint);Add("pbk",node.PublicKey);Add("sid",node.ShortId);
        Add("spx",node.SpiderX);Add("pqv",node.Mldsa65Verify);Add("alpn",node.Alpn);
        Add("path",node.Path);Add("host",node.HostHeader);Add("mode",node.Mode);Add("serviceName",node.ServiceName);
        if(node.AllowInsecure)Add("allowInsecure","1");
        if(host.Contains(':'))host="["+host+"]";
        var result="vless://"+uuid.ToString()+"@"+host+":"+node.Port+"?"+string.Join("&",fields.Select(p=>Uri.EscapeDataString(p.Key)+"="+Uri.EscapeDataString(p.Value)))+"#Shared%20server";
        _=SingleServerShare.Import(result);
        return result;
    }
}
