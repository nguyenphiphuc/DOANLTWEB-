using Microsoft.EntityFrameworkCore;
using DoAnLtWeb.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace DoAnLtWeb.Data
{
    public class AppDbContext : IdentityDbContext<User, IdentityRole<int>, int>
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Presentation> Presentations { get; set; }
        public DbSet<Slide> Slides { get; set; }
        public DbSet<PaymentTransaction> PaymentTransactions { get; set; }
        public DbSet<TemplateSubmission> TemplateSubmissions { get; set; }
        public DbSet<PresentationShare> PresentationShares { get; set; }
        public DbSet<PresentationShareMember> PresentationShareMembers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>().ToTable("Users");

            modelBuilder.Entity<Presentation>()
                .HasMany(p => p.Slides)
                .WithOne(s => s.Presentation)
                .HasForeignKey(s => s.PresentationId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<PaymentTransaction>()
                .HasOne(t => t.User)
                .WithMany(u => u.PaymentTransactions)
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<PaymentTransaction>()
                .HasIndex(t => t.PaymentCode)
                .IsUnique();

            modelBuilder.Entity<TemplateSubmission>()
                .HasOne(s => s.User)
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<TemplateSubmission>()
                .HasOne(s => s.Presentation)
                .WithMany()
                .HasForeignKey(s => s.PresentationId)
                .OnDelete(DeleteBehavior.NoAction);

            modelBuilder.Entity<TemplateSubmission>()
                .HasIndex(s => s.Status);

            modelBuilder.Entity<PresentationShare>()
                .HasIndex(s => s.ShareToken).IsUnique();
            modelBuilder.Entity<PresentationShare>()
                .HasOne(s => s.Presentation)
                .WithMany()
                .HasForeignKey(s => s.PresentationId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<PresentationShare>()
                .HasOne(s => s.OwnerUser)
                .WithMany()
                .HasForeignKey(s => s.OwnerUserId)
                .OnDelete(DeleteBehavior.NoAction);

            modelBuilder.Entity<PresentationShareMember>()
                .HasIndex(m => new { m.ShareId, m.UserId }).IsUnique();
            modelBuilder.Entity<PresentationShareMember>()
                .HasOne(m => m.Share)
                .WithMany()
                .HasForeignKey(m => m.ShareId)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<PresentationShareMember>()
                .HasOne(m => m.User)
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.NoAction);
        }
    }
}
