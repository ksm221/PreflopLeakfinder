using Microsoft.EntityFrameworkCore;
namespace PokerStudy.Core;
public sealed class PokerStudyDbContext : DbContext {
 public DbSet<HandEntity> Hands=>Set<HandEntity>(); public DbSet<ActionEntity> Actions=>Set<ActionEntity>();
 public DbSet<TournamentEntity> Tournaments=>Set<TournamentEntity>(); public DbSet<ImportedFileEntity> ImportedFiles=>Set<ImportedFileEntity>();
 private readonly string _dbPath; public PokerStudyDbContext(string dbPath)=>_dbPath=dbPath;
 protected override void OnConfiguring(DbContextOptionsBuilder o)=>o.UseSqlite($"Data Source={_dbPath}");
 protected override void OnModelCreating(ModelBuilder m) {
  m.Entity<HandEntity>().HasIndex(x=>x.HandId).IsUnique();
  m.Entity<ImportedFileEntity>().HasIndex(x=>new{x.Path,x.Size,x.LastWriteUtc}).IsUnique();
  m.Entity<TournamentEntity>().HasIndex(x=>x.TournamentId).IsUnique();
 }
}