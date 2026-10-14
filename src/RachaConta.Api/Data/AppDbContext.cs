using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RachaConta.Api.Domain;

namespace RachaConta.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Split> Splits => Set<Split>();
    public DbSet<SplitParticipant> SplitParticipants => Set<SplitParticipant>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<ExpenseShare> ExpenseShares => Set<ExpenseShare>();
    public DbSet<Settlement> Settlements => Set<Settlement>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Provider-agnostic conventions: money as decimal(18,2), enums as readable strings,
        // DateTime always read back as UTC.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Split>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(16).IsRequired();
            e.Property(x => x.AdminToken).HasMaxLength(64).IsRequired();
            e.Property(x => x.Title).HasMaxLength(80).IsRequired();
            e.Property(x => x.PixKey).HasMaxLength(77).IsRequired();
            e.Property(x => x.ReceiverName).HasMaxLength(100).IsRequired();
            e.Property(x => x.ReceiverCity).HasMaxLength(15).IsRequired();
            e.HasMany(x => x.Participants).WithOne().HasForeignKey(p => p.SplitId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SplitParticipant>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(60).IsRequired();
        });

        b.Entity<Group>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(80).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.HasMany(x => x.Members).WithOne().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Expenses).WithOne().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Settlements).WithOne().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Member>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(60).IsRequired();
            e.Property(x => x.PixKey).HasMaxLength(77);
        });

        b.Entity<Expense>(e =>
        {
            e.Property(x => x.Description).HasMaxLength(120).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(500);
            e.HasOne(x => x.PaidBy).WithMany().HasForeignKey(x => x.PaidByMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Shares).WithOne().HasForeignKey(s => s.ExpenseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.GroupId, x.Date });
        });

        b.Entity<ExpenseShare>(e =>
        {
            e.Property(x => x.Percentage).HasPrecision(9, 4);
            e.HasOne(x => x.Member).WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Settlement>(e =>
        {
            e.Property(x => x.Notes).HasMaxLength(500);
            e.HasOne(x => x.FromMember).WithMany().HasForeignKey(x => x.FromMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ToMember).WithMany().HasForeignKey(x => x.ToMemberId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v == null ? v : (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()),
        v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
}
