using Microsoft.EntityFrameworkCore;

namespace QuizGame.Server.Data;

public sealed class QuizDbContext(DbContextOptions<QuizDbContext> options) : DbContext(options)
{
    public DbSet<GameMasterEntity> GameMasters => Set<GameMasterEntity>();
    public DbSet<GameSessionEntity> GameSessions => Set<GameSessionEntity>();
    public DbSet<PlayerEntity> Players => Set<PlayerEntity>();
    public DbSet<RoundEntity> Rounds => Set<RoundEntity>();
    public DbSet<AnswerEntity> Answers => Set<AnswerEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<GameMasterEntity>(e =>
        {
            e.ToTable("GameMasters");
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).IsRequired().HasMaxLength(64);
            e.Property(x => x.UsernameKey).IsRequired().HasMaxLength(64);
            e.Property(x => x.PasswordHash).IsRequired().HasMaxLength(256);
            e.HasIndex(x => x.UsernameKey).IsUnique();
        });

        b.Entity<GameSessionEntity>(e =>
        {
            e.ToTable("GameSessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.RoomCode).IsRequired().HasMaxLength(6);
            e.HasOne(x => x.GameMaster).WithMany().HasForeignKey(x => x.GameMasterId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.EndedAt);
        });

        b.Entity<PlayerEntity>(e =>
        {
            e.ToTable("Players");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(20);
            e.Property(x => x.ColorHex).IsRequired().HasMaxLength(9);
            e.HasOne(x => x.Session).WithMany(x => x.Players).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.SessionId);
        });

        b.Entity<RoundEntity>(e =>
        {
            e.ToTable("Rounds");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).IsRequired().HasMaxLength(16);
            e.HasOne(x => x.Session).WithMany(x => x.Rounds).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.SessionId, x.Seq }).IsUnique();
        });

        b.Entity<AnswerEntity>(e =>
        {
            e.ToTable("Answers");
            e.HasKey(x => x.Id);
            e.Property(x => x.ChosenOption).HasMaxLength(1);
            e.HasOne(x => x.Round).WithMany(x => x.Answers).HasForeignKey(x => x.RoundId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RoundId, x.PlayerId }).IsUnique();
        });
    }
}
