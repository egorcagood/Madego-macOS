namespace EgorVpn.Presentation;
public static class NodeOrdering
{
    public static IEnumerable<T> Apply<T>(IEnumerable<T> nodes,string mode,Func<T,long?> ping,Func<T,bool> active,Func<T,bool> favorite)
    {
        if(mode=="Ping")return nodes.OrderBy(n=>ping(n) is null).ThenBy(n=>ping(n)??long.MaxValue).ThenByDescending(active).ThenByDescending(favorite);
        return nodes.OrderByDescending(active).ThenByDescending(favorite);
    }
}
