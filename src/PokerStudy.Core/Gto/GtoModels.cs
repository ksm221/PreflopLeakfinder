namespace PokerStudy.Core.Gto;
public sealed class GtoChainStep {
 public string Actor {get;set;}=""; public string Action {get;set;}=""; public string? Size {get;set;}
}
public sealed class GtoChart {
 public string Format {get;set;}="";           // "HU" or "3W"
 public string Hero {get;set;}="";              // "BU","SB","BB"
 public string? Variant {get;set;}              // e.g. "GTO" / "NoLimp" for the two SBB strategy books
 public List<GtoChainStep> Chain {get;set;}=new();
 public decimal StackBb {get;set;}
 public string SourceImage {get;set;}="";
 public Dictionary<string, Dictionary<string,int>?> Ranges {get;set;}=new();
}
public sealed record GtoResult(string Verdict, string Detail, string ChartLabel);
