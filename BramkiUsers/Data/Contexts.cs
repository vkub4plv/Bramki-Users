using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;

namespace BramkiUsers.Data
{
    public sealed class RaportowanieContext : DbContext
    {
        public RaportowanieContext(DbContextOptions<RaportowanieContext> o) : base(o) { }

        public DbSet<DUser> DUsers => Set<DUser>();
        public DbSet<DMail> DMails => Set<DMail>();
        public DbSet<DGate> DGates => Set<DGate>();
        public DbSet<DDepartment> DDepartment => Set<DDepartment>();
        public DbSet<TUserInRoles> TUserInRoles => Set<TUserInRoles>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            // ----- DUser -----
            b.Entity<DUser>(e =>
            {
                e.ToTable("DUser");
                e.HasKey(x => x.Id);

                e.Property(x => x.Id).HasColumnName("ID");

                e.Property(x => x.Login)
                    .HasColumnName("Login")
                    .HasMaxLength(50);

                e.Property(x => x.Name)
                    .HasColumnName("Name")
                    .HasMaxLength(200);

                e.Property(x => x.IsActive)
                    .HasColumnName("IsActive");

                e.Property(x => x.IsWindowsUser)
                    .HasColumnName("IsWindowsUser");

                e.Property(x => x.CreatedOn)
                    .HasColumnName("CreatedOn");

                e.Property(x => x.CardNumber)
                    .HasColumnName("CardNumber")
                    .HasMaxLength(50);

                e.Property(x => x.ModifiedOn)
                    .HasColumnName("ModifiedOn");

                e.Property(x => x.AllowTerminalFTE)
                    .HasColumnName("AllowTerminalFTE");

                e.Property(x => x.AllowWebFTE)
                    .HasColumnName("AllowWebFTE");

                e.Property(x => x.DepartmentId)
                    .HasColumnName("DepartmentID");

                e.Property(x => x.Sex)
                    .HasColumnName("Sex");

                e.Property(x => x.Worker)
                    .HasColumnName("Worker");

                e.Property(x => x.Code)
                    .HasColumnName("Code")
                    .HasMaxLength(50);

                e.Property(x => x.AdditionalInformation)
                    .HasColumnName("AdditionalInformation")
                    .HasColumnType("nvarchar(max)");

                e.Property(x => x.ErpId)
                    .HasColumnName("ERPID")
                    .HasMaxLength(10);

                e.Property(x => x.CardNumber2)
                    .HasColumnName("CardNumber2")
                    .HasMaxLength(50);

                // Relationship: DGates(duser_id) -> DUser(ID)
                e.HasMany(x => x.Gates)
                 .WithOne(g => g.User)
                 .HasForeignKey(g => g.DUserId);
            });

            // DMails
            b.Entity<DMail>(e =>
            {
                e.ToTable("DMails");
                e.HasKey(x => x.MailsId);
                e.Property(x => x.MailsId).HasColumnName("mails_id");
                e.Property(x => x.MailsName).HasColumnName("mails_name");
                e.Property(x => x.MailsEmail).HasColumnName("mails_email");
                e.Property(x => x.MailsAdGroup).HasColumnName("mails_ad_group");
            });

            // DGates
            b.Entity<DGate>(e =>
            {
                e.ToTable("DGates");
                e.HasKey(x => x.GatesId);
                e.Property(x => x.GatesId).HasColumnName("gates_id");
                e.Property(x => x.DUserId).HasColumnName("duser_id");

                e.Property(x => x.ProvidedData).HasColumnName("gates_provided_data");
                e.Property(x => x.PpeStorageCabinets).HasColumnName("gates_PPE_storage_cabinets");
                e.Property(x => x.Lunch).HasColumnName("gates_lunch");
                e.Property(x => x.Forklifts).HasColumnName("gates_forklifts");
                e.Property(x => x.Cranes).HasColumnName("gates_cranes");
                e.Property(x => x.QualityTraining).HasColumnName("gates_quality_training");
                e.Property(x => x.HRSystem).HasColumnName("gates_hrsystem");
                e.Property(x => x.Gantries).HasColumnName("gates_gantries");
                e.Property(x => x.Phone).HasColumnName("gates_phone");
            });

            // DDepartment
            b.Entity<DDepartment>(e =>
            {
                e.ToTable("DDepartment");
                e.HasKey(x => x.DepartmentId);
                e.Property(x => x.DepartmentId).HasColumnName("ID");
                e.Property(x => x.BramkiName).HasColumnName("Bramki_Name").HasMaxLength(100);
                e.Property(x => x.BramkiCc).HasColumnName("Bramki_CC").HasMaxLength(50);
                e.Property(x => x.BramkiCode).HasColumnName("Bramki_Code").HasMaxLength(50);
            });

            // TUserInRole
            b.Entity<TUserInRoles>(e =>
            {
                e.ToTable("TUserInRoles");
                e.HasKey(x => new { x.UserId, x.RoleId });

                e.Property(x => x.UserId).HasColumnName("UserID");
                e.Property(x => x.RoleId).HasColumnName("RoleID");

                e.HasOne<DUser>()
                 .WithMany()
                 .HasForeignKey(x => x.UserId);
            });
        }
    }

    public sealed class HRContext : DbContext
    {
        public HRContext(DbContextOptions<HRContext> o) : base(o) { }

        public DbSet<Employee> Employees => Set<Employee>();
        public DbSet<EmployeeCard> EmployeeCards => Set<EmployeeCard>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            // employee
            b.Entity<Employee>(e =>
            {
                e.ToTable("employee");
                e.HasKey(x => x.EmId);

                e.Property(x => x.EmId).HasColumnName("em_id");
                e.Property(x => x.EmLogin).HasColumnName("em_login");
                e.Property(x => x.EmNumber).HasColumnName("em_number").HasMaxLength(50);

                // 1:N employee -> employee_card
                e.HasMany(x => x.Cards)
                 .WithOne(c => c.Employee)
                 .HasForeignKey(c => c.EcEmployeeId);
            });

            // employee_card
            b.Entity<EmployeeCard>(e =>
            {
                e.ToTable("employee_card");
                e.HasKey(x => x.EcId);

                e.Property(x => x.EcId).HasColumnName("ec_id");
                e.Property(x => x.EcEmployeeId).HasColumnName("ec_employee_id");
                e.Property(x => x.EcNumber).HasColumnName("ec_number").HasMaxLength(50).IsRequired();
                e.Property(x => x.EcDateFrom).HasColumnName("ec_date_from"); // stored as INT (nullable)
                e.Property(x => x.EcDateTo).HasColumnName("ec_date_to");     // stored as INT (nullable)
            });
        }
    }

    // ------------------------------ Entities ---------------------------------
    // Raportowanie
    public class DUser
    {
        public int Id { get; set; }
        public string? Login { get; set; }
        public string? Name { get; set; }
        public bool? IsActive { get; set; }
        public bool? IsWindowsUser { get; set; }
        public DateTime? CreatedOn { get; set; }
        public string? CardNumber { get; set; }
        public DateTime? ModifiedOn { get; set; }
        public bool? AllowTerminalFTE { get; set; }
        public bool? AllowWebFTE { get; set; }
        public int? DepartmentId { get; set; }
        public bool? Sex { get; set; }
        public bool? Worker { get; set; }
        public string? Code { get; set; }
        public string? AdditionalInformation { get; set; }
        public string? ErpId { get; set; }
        public string? CardNumber2 { get; set; }

        public ICollection<DGate> Gates { get; set; } = new List<DGate>();
    }

    public class DMail
    {
        public int MailsId { get; set; }
        public string? MailsName { get; set; }
        public string? MailsEmail { get; set; }
        public string? MailsAdGroup { get; set; }
    }

    public class DGate
    {
        public int GatesId { get; set; }
        public int DUserId { get; set; }

        public bool? ProvidedData { get; set; }
        public bool? PpeStorageCabinets { get; set; }
        public bool? Lunch { get; set; }
        public bool? Forklifts { get; set; }
        public bool? Cranes { get; set; }
        public bool? QualityTraining { get; set; }
        public string? HRSystem { get; set; }
        public bool? Gantries { get; set; }
        public string? Phone { get; set; }

        public DUser? User { get; set; }
    }

    public class DDepartment
    {
        public int DepartmentId { get; set; }
        public string? BramkiName { get; set; }
        public string? BramkiCc { get; set; }
        public string? BramkiCode { get; set; }
    }

    public class TUserInRoles
    {
        public int UserId { get; set; }   // TUserInRoles.UserID
        public int RoleId { get; set; }   // TUserInRoles.RoleID
    }

    // HR
    public class Employee
    {
        public int EmId { get; set; }                  // employee.em_id (PK)
        public string? EmLogin { get; set; }           // employee.em_login (nullable)
        public string? EmNumber { get; set; }          // employee.em_number (nullable)
        public ICollection<EmployeeCard> Cards { get; set; } = new List<EmployeeCard>();
    }

    public class EmployeeCard
    {
        public int EcId { get; set; }                  // employee_card.ec_id (PK)
        public int EcEmployeeId { get; set; }            // employee_card.ec_employee_id (FK -> employee.em_id)
        public string EcNumber { get; set; } = "";       // employee_card.ec_number (NOT NULL)
        public int? EcDateFrom { get; set; }             // employee_card.ec_date_from (INT, NULL)
        public int? EcDateTo { get; set; }               // employee_card.ec_date_to   (INT, NULL)

        public Employee? Employee { get; set; }
    }
}
