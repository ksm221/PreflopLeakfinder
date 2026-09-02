using System.ComponentModel.DataAnnotations.Schema;
namespace PokerStudy.Core;

public enum GameFormat { HU, ThreeW }
public enum Position { BTN, SB, BB }
public enum ActionType { Fold, Check, Call, Limp, Bet, Raise, AllIn, Unknown, Walk }
public sealed class HandEntity
{
    public int Id { get; set; }
    public string HandId { get; set; } = ""; public string TournamentId { get; set; } = "";
    public string TournamentName { get; set; } = ""; public DateTime PlayedAtUtc { get; set; }
    public GameFormat Format { get; set; }
    public Position HeroPosition { get; set; }
    public decimal HeroStack { get; set; }
    public decimal BigBlind { get; set; }
    public decimal StackBb { get; set; }
    public string Card1 { get; set; } = ""; public string Card2 { get; set; } = ""; public string StartingHand { get; set; } = "";
    public ActionType HeroAction { get; set; }
    public string Spot { get; set; } = ""; public string SourceFile { get; set; } = "";
    public string RawText { get; set; } = "";
    public Position? PriorActorPosition { get; set; }
    public ActionType? PriorActionType { get; set; }
    public decimal? PriorActionBb { get; set; }
    [NotMapped] public string GtoVerdict { get; set; } = "";
    [NotMapped] public string GtoDetail { get; set; } = "";
    [NotMapped] public string PositionLabel => $"{(Format == GameFormat.HU ? "HU" : "3W")} {HeroPosition}";
}
public sealed class ActionEntity
{
    public int Id { get; set; }
    public string HandId { get; set; } = ""; public int Sequence { get; set; }
    public string Player { get; set; } = ""; public Position? Position { get; set; }
    public ActionType ActionType { get; set; }
    public decimal Amount { get; set; }
    public bool IsAllIn { get; set; }
}
public sealed class TournamentEntity
{
    public int Id { get; set; }
    public string TournamentId { get; set; } = ""; public string Name { get; set; } = "";
    public string HeroName { get; set; } = ""; public DateTime? StartedAtUtc { get; set; }
    public int RegisteredPlayers { get; set; }
    public int FinishPosition { get; set; }
}
public sealed class ImportedFileEntity
{
    public int Id { get; set; }
    public string Path { get; set; } = ""; public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public DateTime ImportedAtUtc { get; set; }
    public string Status { get; set; } = ""; public int HandsImported { get; set; }
    public string Error { get; set; } = "";
}

public sealed record ParsedHand(HandEntity Hand, List<ActionEntity> Actions);