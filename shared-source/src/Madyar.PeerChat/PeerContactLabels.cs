namespace Madyar.PeerChat;
/// <summary>Linear suffix counting keeps contact labels distinct without dispatcher-wide nested scans.</summary>
public static class PeerContactLabels {
 public static IReadOnlyList<string> Create(IReadOnlyList<PeerContact> rows) {
  var shortCounts=new Dictionary<string,int>(StringComparer.Ordinal);
  var longCounts=new Dictionary<string,int>(StringComparer.Ordinal);
  foreach(var contact in rows) {
   var shortId=contact.PeerId[^8..];var longId=contact.PeerId[^12..];
   shortCounts[shortId]=shortCounts.GetValueOrDefault(shortId)+1;
   longCounts[longId]=longCounts.GetValueOrDefault(longId)+1;
  }
  var labels=new string[rows.Count];
  for(int i=0;i<rows.Count;i++) {
   var contact=rows[i];var shortId=contact.PeerId[^8..];var longId=contact.PeerId[^12..];
   var id=shortCounts[shortId]==1?shortId:longCounts[longId]==1?longId:contact.PeerId;
   labels[i]=$"{contact.Alias??contact.Nickname} · {id}";
  }
  return labels;
 }
}
