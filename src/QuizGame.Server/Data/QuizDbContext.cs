using Microsoft.EntityFrameworkCore;

namespace QuizGame.Server.Data;

public sealed class QuizDbContext(DbContextOptions<QuizDbContext> options) : DbContext(options)
{
    public DbSet<GameMasterEntity> GameMasters => Set<GameMasterEntity>();
    public DbSet<GameSessionEntity> GameSessions => Set<GameSessionEntity>();
    public DbSet<PlayerEntity> Players => Set<PlayerEntity>();
    public DbSet<RoundEntity> Rounds => Set<RoundEntity>();
    public DbSet<AnswerEntity> Answers => Set<AnswerEntity>();
    public DbSet<CategoryEntity> Categories => Set<CategoryEntity>();
    public DbSet<QuestionEntity> Questions => Set<QuestionEntity>();
    public DbSet<QuestionOptionEntity> QuestionOptions => Set<QuestionOptionEntity>();

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

        b.Entity<CategoryEntity>(e =>
        {
            e.ToTable("Categories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(64);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<QuestionEntity>(e =>
        {
            e.ToTable("Questions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.Text).IsRequired().HasMaxLength(1000);
            e.Property(x => x.CorrectNumber).HasColumnType("numeric");
            e.Property(x => x.Unit).HasMaxLength(32);
            e.HasOne(x => x.Category).WithMany(x => x.Questions)
                .HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GameMasterEntity>().WithMany()
                .HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.CategoryId);
            e.HasIndex(x => x.IsActive);
            // Estimation questions carry a number; multiple-choice questions do not (plan §5.2).
            e.ToTable(t => t.HasCheckConstraint(
                "CK_Questions_CorrectNumber_ByType",
                """("Type" = 'Estimation' AND "CorrectNumber" IS NOT NULL) OR ("Type" = 'MultipleChoice' AND "CorrectNumber" IS NULL)"""));
        });

        b.Entity<QuestionOptionEntity>(e =>
        {
            e.ToTable("QuestionOptions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Label).IsRequired().HasConversion<string>().HasMaxLength(1);
            e.Property(x => x.Text).IsRequired().HasMaxLength(500);
            e.HasOne(x => x.Question).WithMany(x => x.Options)
                .HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.QuestionId);
        });
    }
}
