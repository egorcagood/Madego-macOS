using EgorVpn.Presentation;
void Check(bool yes,string name){if(!yes)throw new Exception(name);Console.WriteLine("PASS "+name);}
var nodes=new[]{new Row("unmeasured",null,true,true),new Row("slow",280,false,false),new Row("fast",12,false,false),new Row("equal",12,false,false)};
var sorted=NodeOrdering.Apply(nodes,"Ping",r=>r.Ping,r=>r.Active,r=>r.Favorite).ToArray();
Check(sorted.Select(r=>r.Name).SequenceEqual(new[]{"fast","equal","slow","unmeasured"}),"Ping sorting places measured low latency first and unknown last, retaining ties");
Check(nodes[0].Name=="unmeasured"&&nodes[0].Active,"Sorting does not mutate nodes, active state or provider order");
Check(NodeOrdering.Apply(nodes,"Default",r=>r.Ping,r=>r.Active,r=>r.Favorite).First()==nodes[0],"Default retains existing active/favourite ordering");
Check(NodeOrdering.Apply(nodes.Where(r=>r.Name!="slow"),"Ping",r=>r.Ping,r=>r.Active,r=>r.Favorite).Count()==3,"Sort preserves the prefiltered subscription/country/search scope");
Check(NodeOrdering.Apply(Array.Empty<Row>(),"Ping",r=>r.Ping,r=>r.Active,r=>r.Favorite).Count()==0,"Empty server scope remains empty");
Check(NodeOrdering.Apply(nodes,"unknown",r=>r.Ping,r=>r.Active,r=>r.Favorite).First()==nodes[0],"Unknown sort ID retains safe existing order");
record Row(string Name,long? Ping,bool Active,bool Favorite);
