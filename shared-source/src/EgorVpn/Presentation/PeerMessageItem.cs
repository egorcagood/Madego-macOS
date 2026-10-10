using Madyar.Localization;
using EgorVpn.Services;
using Madyar.PeerChat;
namespace EgorVpn.Presentation;
public sealed class PeerMessageItem : ObservableObject {
    internal PeerMessage Message {get;}
    public bool IsOutgoing=>Message.Outgoing;
    public bool IsServer=>Message.Kind=="server";
    public bool CanRetry=>Message.Outgoing&&Message.Status is "Failed" or "Pending";
    public bool CanAddServer {get;}
    readonly string _contactLabel;
    public string Title=>Message.Outgoing?UiText.T("Вы"):_contactLabel;
    readonly string _body;
    bool _invalidServer;
    public string Body=>_invalidServer?UiText.T("Конфигурация сервера не распознана."):_body;
    string _serverTitle="";
    public string ServerTitle=>_invalidServer?UiText.T("Сервер"):_serverTitle;
    public string StatusText=>Message.Status switch {"Pending"=>UiText.T("Отправляется…"),"Delivered"=>UiText.T("Доставлено"),"Failed"=>UiText.T("Не доставлено"),_=>UiText.T("Получено")};
    public string TimeText=>Message.CreatedUtc.ToLocalTime().ToString("HH:mm",UiText.Culture);
    public bool HasDeliveryStatus=>Message.Outgoing;
    public bool DeliveryFailed=>Message.Outgoing&&Message.Status=="Failed";
    public bool DeliveryPending=>Message.Outgoing&&Message.Status=="Pending";
    public string DeliveryMark=>!Message.Outgoing?"":Message.Status switch {"Delivered"=>"✓","Failed"=>"!","Pending"=>"◷",_=>"…"};
    public string MetadataTooltip=>Title+" · "+Message.CreatedUtc.ToLocalTime().ToString("g",UiText.Culture)+"\n"+StatusText;
    public string Details=>TimeText+" · "+StatusText;
    public PeerMessageItem(PeerMessage message,string contactLabel) {
        Message=message;_contactLabel=contactLabel;_body=message.Text;
        if(!IsServer)return;
        _invalidServer=true;
        try {var node=SingleServerShare.Import(message.Text).Nodes.Single();_serverTitle=node.Name;_body=DisplayFormat.Endpoint(node.Host,node.Port);_invalidServer=false;CanAddServer=!message.Outgoing;}
        catch(ArgumentException) { }
    }
}
