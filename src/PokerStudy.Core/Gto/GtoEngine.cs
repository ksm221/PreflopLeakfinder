using System.Text.Json;
namespace PokerStudy.Core.Gto;

public sealed class GtoStore
{
    public List<GtoChart> Charts { get; }
    public GtoStore(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        Charts = JsonSerializer.Deserialize<List<GtoChart>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    }
}

// Phase 2: matches hands at any chain depth (open, facing one raise, vs-3bet, vs-squeeze, vs-4bet-shove,
// limp->iso->reraise lines, etc.) by comparing the hand's actual pre-hero, non-fold action sequence
// against each chart's recorded chain step-by-step, using the *shape* of each action (Limp / Call /
// Raise / Shove) rather than the taxonomy's free-text action labels directly - those labels
// ("Open","3Bet","Squeeze","Iso","Raise"...) were written for human readability and aren't a fully
// consistent vocabulary, but every one of them reduces to one of those four shapes.
//
// As before, this only evaluates hero's FIRST preflop decision in the hand (matching how Spot/HeroAction
// are recorded) - if hero acts again later after facing a raise-over-their-own-raise, that second
// decision isn't separately modeled.
public static class GtoMatcher
{

    static string FormatKey(GameFormat f) => f == GameFormat.HU ? "HU" : "3W";
    static string PosKey(Position p) => p == Position.BTN ? "BU" : p.ToString();

    static decimal? SizeToBb(string? size, decimal? effStackBb)
    {
        if (size == null) return null;
        var s = size.ToLowerInvariant();
        if (s.Contains("minraise")) return 2.0m;
        if (s.Contains("2.5")) return 2.5m;
        if (s.Contains("2bb") || s == "2x") return 2.0m;
        if (s.Contains("3bb") || s == "3x") return 3.0m;
        if (s.Contains("default")) return 3.0m;
        if (s.Contains("mid")) return 4.75m;
        if (s.Contains("33%") && effStackBb.HasValue) return effStackBb.Value * 0.33m;
        return null;
    }
    static bool IsShoveSize(string? size) => size != null && size.Contains("Shove", StringComparison.OrdinalIgnoreCase);

    static string ShapeOfReal(ActionType a) => a switch
    {
        ActionType.Limp => "Limp",
        ActionType.Call => "Call",
        ActionType.Raise => "Raise",
        ActionType.AllIn => "AllIn",
        _ => "Other"
    };
    static string ShapeOfTax(GtoChainStep s)
    {
        if (s.Action.Equals("Limp", StringComparison.OrdinalIgnoreCase)) return "Limp";
        if (s.Action.Equals("Call", StringComparison.OrdinalIgnoreCase)) return "Call";
        if (IsShoveSize(s.Size) || s.Action.Equals("Shove", StringComparison.OrdinalIgnoreCase)) return "AllIn";
        return "Raise"; // Open / 3Bet / 4Bet / Squeeze / Iso / Raise (non-shove) all count as a raise shape
    }

    /// <param name="handActions">ALL of this hand's preflop ActionEntity rows, in their original Sequence order (not pre-filtered).</param>
    public static GtoResult? Evaluate(GtoStore store, HandEntity hand, IReadOnlyList<ActionEntity> handActions)
    {
        if (hand.HeroAction == ActionType.Walk) return new GtoResult("N/A", "Everyone folded before hero had to act - no decision to evaluate", "");

        var ordered = handActions.OrderBy(x => x.Sequence).ToList();
        var heroFirstIdx = ordered.FindIndex(x => x.Position == hand.HeroPosition);
        if (heroFirstIdx < 0) return null; // shouldn't happen given HeroAction != Walk, but guard anyway

        var priorActions = ordered.Take(heroFirstIdx).Where(x => x.ActionType != ActionType.Fold).ToList();
        if (priorActions.Any(a => a.Position == null)) return null; // can't reliably match if a position wasn't resolved

        var fmt = FormatKey(hand.Format);
        var heroKey = PosKey(hand.HeroPosition);
        var actualChain = priorActions.Select(a => (Actor: PosKey(a.Position!.Value), Shape: ShapeOfReal(a.ActionType), Bb: a.Amount / hand.BigBlind)).ToList();

        var candList = store.Charts.Where(c =>
          c.Format == fmt && c.Hero == heroKey && c.Chain.Count == actualChain.Count &&
          c.Chain.Zip(actualChain, (cs, ac) => cs.Actor == ac.Actor && ShapeOfTax(cs) == ac.Shape).All(ok => ok)
         ).ToList();
        if (candList.Count == 0) return null;

        var withVariant = candList.Where(c => c.Variant == null || c.Variant == "GTO").ToList();
        if (withVariant.Count > 0) candList = withVariant;

        // disambiguate by size using the LAST (most decision-relevant) chain step, only when it's a
        // non-shove raise - shoves are already uniquely matched by shape, and limp/call have no size.
        if (actualChain.Count > 0 && actualChain[^1].Shape == "Raise")
        {
            var lastBb = actualChain[^1].Bb;
            var distinctSizes = candList.Select(c => c.Chain[^1].Size).Distinct().Count();
            if (distinctSizes > 1)
            {
                var sized = candList.Select(c => (chart: c, bb: SizeToBb(c.Chain[^1].Size, hand.StackBb))).Where(x => x.bb.HasValue).ToList();
                if (sized.Count > 0)
                {
                    var best = sized.OrderBy(x => Math.Abs(x.bb!.Value - lastBb)).First();
                    candList = candList.Where(c => SizeToBb(c.Chain[^1].Size, hand.StackBb) == best.bb).ToList();
                }
            }
        }

        var chart = candList.OrderBy(c => Math.Abs(c.StackBb - hand.StackBb)).First();

        if (!chart.Ranges.TryGetValue(hand.StartingHand, out var dist) || dist == null || dist.Count == 0)
            return new GtoResult("No data", "This hand isn't in the digitized GTO range for this spot", Label(chart));

        var primary = dist.OrderByDescending(kv => kv.Value).First();
        bool IsRaiseCat(string k) => k == "RaiseSmall" || k == "RaiseBig";

        // Shoving over an opponent who is already effectively all-in isn't a "raise" in GTO terms -
        // with no one left to act, it's the same decision as calling.
        var priorWasAllIn = actualChain.Count > 0 && actualChain[^1].Shape == "AllIn";
        var heroActionForMatch = (hand.HeroAction == ActionType.AllIn && priorWasAllIn) ? ActionType.Call : hand.HeroAction;

        int heroPct; bool heroMatchesPrimary;
        if (heroActionForMatch == ActionType.Raise)
        {
            heroPct = dist.Where(kv => IsRaiseCat(kv.Key)).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
            heroMatchesPrimary = IsRaiseCat(primary.Key);
        }
        else
        {
            var cat = MapHeroAction(heroActionForMatch);
            heroPct = cat != null && dist.TryGetValue(cat, out var p) ? p : 0;
            heroMatchesPrimary = cat != null && cat == primary.Key;
        }

        const int MixThreshold = 20;

        string verdict, detail;
        if (heroPct >= MixThreshold && heroMatchesPrimary)
        {
            verdict = "Matches GTO"; detail = $"{primary.Key} {primary.Value}% (your action)";
        }
        else if (heroPct >= MixThreshold)
        {
            verdict = "Partial match"; detail = $"You took a {heroPct}% mixed-strategy option; GTO favors {primary.Key} {primary.Value}%";
        }
        else if (heroPct > 0)
        {
            verdict = "Deviation"; detail = $"Only a {heroPct}% minor option in GTO (below {MixThreshold}% mix threshold); favors {primary.Key} {primary.Value}%";
        }
        else
        {
            verdict = "Deviation"; detail = $"GTO recommends {primary.Key} {primary.Value}% (you {hand.HeroAction})";
        }
        return new GtoResult(verdict, detail, Label(chart));
    }

    static string? MapHeroAction(ActionType a) => a switch
    {
        ActionType.Fold => "Fold",
        ActionType.Call => "Call",
        ActionType.Limp => "Call",
        ActionType.AllIn => "AllIn",
        _ => null
    };

    static string Label(GtoChart c) => $"{c.Format} {c.Hero}{(c.Variant != null ? $" ({c.Variant})" : "")} @ {c.StackBb:0.#}bb";
}