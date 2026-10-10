using System;
using Madyar.Localization;
namespace EgorVpn.Presentation;
// Presentation-only formatting. The service formatter and serialized/protocol data stay unchanged.
public static class InterfaceFormat
{
    public static string Bytes(long? bytes)
    {
        if(bytes is null)return "—";
        double value=Math.Max(0,bytes.Value);string[] units=["Б","КБ","МБ","ГБ","ТБ"];var i=0;
        while(value>=1024&&i<units.Length-1){value/=1024;i++;}
        return value.ToString("0.#",UiText.Culture)+" "+UiText.T(units[i]);
    }
    public static string Rate(double? bytes)=>bytes is null?"—":Bytes((long)Math.Max(0,bytes.Value))+(UiText.Language=="ru"?"/с":"/s");
}
