using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
namespace PokerStudy.Core;
public sealed record ImportStats(long Files,long Hands,long Errors,TimeSpan Duration);
public sealed class FolderImporter {
 readonly string _db; readonly int _parallel;
 public FolderImporter(string db,int? parallel=null){_db=db;_parallel=Math.Clamp(parallel??Math.Max(2,Environment.ProcessorCount/2),1,8);}
 public async Task<ImportStats> ImportAsync(string folder,IProgress<(long files,long hands,long errors)>? progress,CancellationToken token){
  Directory.CreateDirectory(Path.GetDirectoryName(_db)!); await using(var d=new PokerStudyDbContext(_db)){await d.Database.EnsureCreatedAsync(token);}
  var sw=System.Diagnostics.Stopwatch.StartNew();long files=0,hands=0,errors=0;
  var paths=Directory.EnumerateFiles(folder,"*.txt",SearchOption.AllDirectories).ToList();

  // Phase 1: summary files carry the hero's name (hand-history files don't contain it at all).
  // Build a tournamentId -> hero map first so hand-history files can be resolved to a hero
  // regardless of the order files are processed in during the parallel phase below.
  var heroByTid=new System.Collections.Concurrent.ConcurrentDictionary<string,string>();
  await Parallel.ForEachAsync(paths,new ParallelOptions{MaxDegreeOfParallelism=_parallel,CancellationToken=token},async(path,ct)=>{
   string text; try{text=await File.ReadAllTextAsync(path,ct);}catch{return;}
   if(IsSummary(text)){var tid=Tid(text);var hero=Hero(text);if(tid!=null&&hero!=null)heroByTid[tid]=hero;}
  });

  await Parallel.ForEachAsync(paths,new ParallelOptions{MaxDegreeOfParallelism=_parallel,CancellationToken=token},async(path,ct)=>{
   try{
    var info=new FileInfo(path); await using var d=new PokerStudyDbContext(_db);
    if(await d.ImportedFiles.AsNoTracking().AnyAsync(x=>x.Path==path&&x.Size==info.Length&&x.LastWriteUtc==info.LastWriteTimeUtc,ct)){Interlocked.Increment(ref files);progress?.Report((files,hands,errors));return;}
    var text=await File.ReadAllTextAsync(path,ct);
    if(IsSummary(text)){
      var tid=Tid(text);var tn=TName(text);var hero=Hero(text);
      if(tid!=null&&!await d.Tournaments.AnyAsync(x=>x.TournamentId==tid,ct))d.Tournaments.Add(new TournamentEntity{TournamentId=tid,Name=tn??"",HeroName=hero??""});
      await d.SaveChangesAsync(ct);
    } else if(text.Contains("HandId:")){
      var tid=HandTid(text);var tn=HandTname(text);
      if(tid!=null&&heroByTid.TryGetValue(tid,out var hero)){
        var parsed=new WinamaxParser().ParseFile(path,hero,tid,tn??"").ToList();var ids=parsed.Select(x=>x.Hand.HandId).ToList();
        var existing=(await d.Hands.AsNoTracking().Where(x=>ids.Contains(x.HandId)).Select(x=>x.HandId).ToListAsync(ct)).ToHashSet();
        foreach(var p in parsed)if(!existing.Contains(p.Hand.HandId)){foreach(var a in p.Actions)a.HandId=p.Hand.HandId;d.Hands.Add(p.Hand);d.Actions.AddRange(p.Actions);Interlocked.Increment(ref hands);}
        await d.SaveChangesAsync(ct);
      }
      // if tid is null or no matching summary file provided a hero, the file is skipped (nothing to
      // attribute the hands to) but is still marked imported below so it isn't retried forever.
    }
    d.ImportedFiles.Add(new ImportedFileEntity{Path=path,Size=info.Length,LastWriteUtc=info.LastWriteTimeUtc,ImportedAtUtc=DateTime.UtcNow,Status="OK"});
    await d.SaveChangesAsync(ct);
   }catch{Interlocked.Increment(ref errors);}
   Interlocked.Increment(ref files);progress?.Report((files,hands,errors));
  });
  sw.Stop();return new ImportStats(files,hands,errors,sw.Elapsed);
 }
 static readonly Regex sum=new(@"Winamax Poker - Tournament summary\s*:\s*(?<n>.+?)\((?<id>\d+)\)",RegexOptions.Compiled);
 static readonly Regex heroR=new(@"^Player\s*:\s*(?<hero>.+)$",RegexOptions.Compiled|RegexOptions.Multiline);
 static readonly Regex handTable=new(@"Table: '.*?\((?<tid>\d+)\)#",RegexOptions.Compiled);
 static readonly Regex handHeader=new(@"Tournament ""(?<n>[^""]+)""",RegexOptions.Compiled);
 static string? Hero(string x)=>heroR.Match(x) is var m&&m.Success?m.Groups["hero"].Value.Trim():null;
 static string? Tid(string x)=>sum.Match(x) is var m&&m.Success?m.Groups["id"].Value:null;
 static string? TName(string x)=>sum.Match(x) is var m&&m.Success?m.Groups["n"].Value.Trim():null;
 static string? HandTid(string x)=>handTable.Match(x) is var m&&m.Success?m.Groups["tid"].Value:null;
 static string? HandTname(string x)=>handHeader.Match(x) is var m&&m.Success?m.Groups["n"].Value.Trim():null;
 static bool IsSummary(string x)=>x.StartsWith("Winamax Poker - Tournament summary",StringComparison.OrdinalIgnoreCase);
}
