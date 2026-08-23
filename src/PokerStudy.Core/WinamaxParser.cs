using System.Globalization;
using System.Text.RegularExpressions;
namespace PokerStudy.Core;

public sealed class WinamaxParser
{
    static readonly Regex Header = new(@"Winamax Poker - Tournament ""(?<name>[^""]+)"" buyIn: .*? level: \d+ - HandId: #(?<id>[^ ]+) - Holdem no limit \((?<sb>\d+)\/(?<bb>\d+)\) - (?<date>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}) UTC", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Table = new(@"Table: '.*?\((?<tid>\d+)\)#\d+' (?<players>\d+)-max .*? Seat #(?<button>\d+) is the button", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Seat = new(@"Seat (?<seat>\d+): (?<name>.+?) \((?<stack>\d+(?:\.\d+)?)\)", RegexOptions.Compiled);
    static readonly Regex Blind = new(@"^(?<name>.+?) posts (?<blind>small|big) blind (?<amount>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
    static readonly Regex Dealt = new(@"Dealt to (?<hero>.+?) \[(?<c1>[2-9TJQKA][cdhs]) (?<c2>[2-9TJQKA][cdhs])\]", RegexOptions.Compiled);
    public IEnumerable<ParsedHand> ParseFile(string path, string hero, string tid, string tname)
    {
        var text = File.ReadAllText(path); var ms = Header.Matches(text);
        for (int i = 0; i < ms.Count; i++) { var block = text[ms[i].Index..(i + 1 < ms.Count ? ms[i + 1].Index : text.Length)].Trim(); ParsedHand? p = null; try { p = ParseBlock(block, path, hero, tid, tname, ms[i]); } catch { } if (p != null) yield return p; }
    }
    ParsedHand? ParseBlock(string b, string path, string hero, string tid, string tn, Match h)
    {
        var table = Table.Match(b); var dealt = Dealt.Match(b); if (!table.Success || !dealt.Success) return null;
        var seats = Seat.Matches(b).Select(m => new SeatInfo(int.Parse(m.Groups["seat"].Value), m.Groups["name"].Value, decimal.Parse(m.Groups["stack"].Value, CultureInfo.InvariantCulture))).ToList();
        var hs = seats.FirstOrDefault(x => x.Name.Equals(hero, StringComparison.OrdinalIgnoreCase)); if (hs == null) return null;
        var blinds = Blind.Matches(b); var sb = blinds.Cast<Match>().FirstOrDefault(x => x.Groups["blind"].Value.Equals("small", StringComparison.OrdinalIgnoreCase))?.Groups["name"].Value;
        var bb = decimal.Parse(h.Groups["bb"].Value, CultureInfo.InvariantCulture); var format = seats.Count <= 2 ? GameFormat.HU : GameFormat.ThreeW;
        var button = int.Parse(table.Groups["button"].Value); var pos = hs.Seat == button ? (format == GameFormat.HU ? Position.SB : Position.BTN) : (hs.Name.Equals(sb, StringComparison.OrdinalIgnoreCase) ? Position.SB : Position.BB);
        // Effective stack: capped by the largest single OTHER stack in the hand. If hero has the overall
        // largest stack, they're only ever effectively as deep as the next-biggest opponent; if hero isn't
        // the largest stack, their own (shorter) stack already is the effective stack.
        var otherStacks = seats.Where(x => x.Seat != hs.Seat).Select(x => x.Stack).ToList();
        var effStack = otherStacks.Count > 0 ? Math.Min(hs.Stack, otherStacks.Max()) : hs.Stack;
        var c1 = dealt.Groups["c1"].Value; var c2 = dealt.Groups["c2"].Value; var acts = ParseActions(Pre(b), seats, button, format, sb);
        var heroAction = acts.FirstOrDefault(x => x.Player.Equals(hero, StringComparison.OrdinalIgnoreCase));
        var (spot, priorPos, priorType, priorAmt) = BuildSpot(format, pos, effStack / bb, acts, hero);
        var hand = new HandEntity { HandId = h.Groups["id"].Value, TournamentId = tid, TournamentName = tn, PlayedAtUtc = DateTime.ParseExact(h.Groups["date"].Value, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), Format = format, HeroPosition = pos, HeroStack = hs.Stack, BigBlind = bb, StackBb = effStack / bb, Card1 = c1, Card2 = c2, StartingHand = Starting(c1, c2), HeroAction = heroAction?.ActionType ?? ActionType.Walk, Spot = spot, SourceFile = path, RawText = b, PriorActorPosition = priorPos, PriorActionType = priorType, PriorActionBb = priorAmt.HasValue ? priorAmt.Value / bb : (decimal?)null };
        return new ParsedHand(hand, acts);
    }
    static string Pre(string b) { var s = b.IndexOf("*** PRE-FLOP ***", StringComparison.Ordinal); if (s < 0) return ""; s += "*** PRE-FLOP ***".Length; var e = b.IndexOf("*** FLOP ***", s, StringComparison.Ordinal); if (e < 0) e = b.IndexOf("*** SUMMARY ***", s, StringComparison.Ordinal); return b[s..(e < 0 ? b.Length : e)]; }
    List<ActionEntity> ParseActions(string pre, List<SeatInfo> seats, int button, GameFormat format, string? sb)
    {
        var r = new List<ActionEntity>(); int seq = 0;
        // Player names can contain spaces (e.g. "uden lo"), so match against the known seat names
        // (longest first, in case one name is a prefix of another) instead of splitting on the first space.
        var names = seats.Select(x => x.Name).OrderByDescending(n => n.Length).ToList();
        foreach (var raw in pre.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var player = names.FirstOrDefault(n => line.StartsWith(n + " ", StringComparison.OrdinalIgnoreCase));
            if (player == null) continue;
            var rest = line[player.Length..].TrimStart();
            ActionType? t = null; decimal amt = 0; bool ai = rest.Contains("all-in", StringComparison.OrdinalIgnoreCase);
            if (rest.StartsWith("folds", StringComparison.OrdinalIgnoreCase)) t = ActionType.Fold;
            else if (rest.StartsWith("calls ", StringComparison.OrdinalIgnoreCase)) { t = ActionType.Call; amt = Amt(rest); }
            else if (rest.StartsWith("raises ", StringComparison.OrdinalIgnoreCase)) { t = ai ? ActionType.AllIn : ActionType.Raise; amt = To(rest); }
            if (t == null) continue; var p = seats.FirstOrDefault(x => x.Name.Equals(player, StringComparison.OrdinalIgnoreCase)); var pos = p == null ? (Position?)null : Pos(p.Seat, button, format, seats, sb);
            if (t == ActionType.Call && (r.Count == 0 || r.All(x => x.ActionType is ActionType.Fold or ActionType.Limp))) t = ActionType.Limp;
            r.Add(new ActionEntity { Sequence = seq++, Player = player, Position = pos, ActionType = t.Value, Amount = amt, IsAllIn = ai });
        }
        return r;
    }
    static Position Pos(int seat, int button, GameFormat f, List<SeatInfo> s, string? sb) => seat == button ? (f == GameFormat.HU ? Position.SB : Position.BTN) : (s.First(x => x.Seat == seat).Name.Equals(sb, StringComparison.OrdinalIgnoreCase) ? Position.SB : Position.BB);
    static decimal Amt(string x) { var m = Regex.Match(x, @"\b(?:calls|to)\s+(\d+(?:\.\d+)?)"); return m.Success ? decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0; }
    static decimal To(string x) { var m = Regex.Match(x, @"\bto\s+(\d+(?:\.\d+)?)"); return m.Success ? decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : Amt(x); }
    static (string spot, Position? priorPos, ActionType? priorType, decimal? priorAmt) BuildSpot(GameFormat f, Position p, decimal bb, List<ActionEntity> a, string hero)
    {
        var i = a.FindIndex(x => x.Player.Equals(hero, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return ($"{f} {p} {bb:0.##} BB | Folded to hero (walk)", null, null, null);
        var b = a.Take(i).Where(x => x.ActionType != ActionType.Fold).ToList();
        if (b.Count == 0) return ($"{f} {p} {bb:0.##} BB | First action", null, null, null);
        var x = b[^1];
        return ($"{f} {p} {bb:0.##} BB | vs {x.Position} {x.ActionType}", x.Position, x.ActionType, x.Amount);
    }
    static string Starting(string a, string b) { const string r = "23456789TJQKA"; var x = r.IndexOf(a[0]); var y = r.IndexOf(b[0]); var hi = x >= y ? a[0] : b[0]; var lo = x >= y ? b[0] : a[0]; return hi == lo ? $"{hi}{lo}" : $"{hi}{lo}{(a[1] == b[1] ? 's' : 'o')}"; }
    sealed record SeatInfo(int Seat, string Name, decimal Stack);
}