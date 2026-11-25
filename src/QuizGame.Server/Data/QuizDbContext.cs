using Microsoft.EntityFrameworkCore;

namespace QuizGame.Server.Data;

public sealed class QuizDbContext(DbContextOptions<QuizDbContext> options) : DbContext(options)
{
    public DbSet<GameMasterEntity> GameMasters => Set<GameMasterEntity>();
    public DbSet<GameSessionEntity> GameSessions => Set<GameSessionEntity>();
    public DbSet<PlayerEntity> Players => Set<PlayerEntity>();

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
    }
}
