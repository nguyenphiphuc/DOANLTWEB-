using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DoAnLtWeb.Models
{
    public class PaymentTransaction
    {
        public int Id { get; set; }

        public int UserId { get; set; }
        public User? User { get; set; }

        [MaxLength(100)]
        public string PaymentCode { get; set; } = string.Empty;

        [MaxLength(50)]
        public string PlanName { get; set; } = "VIP Monthly";

        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }

        [MaxLength(30)]
        public string Status { get; set; } = "Pending";

        [MaxLength(50)]
        public string BankName { get; set; } = "MBBank";

        [MaxLength(50)]
        public string AccountNumber { get; set; } = "1111122005";

        [MaxLength(100)]
        public string AccountName { get; set; } = "Tran Minh Khang";

        [MaxLength(255)]
        public string TransferContent { get; set; } = string.Empty;

        public string VietQrUrl { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ConfirmedAt { get; set; }
        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(12);

        [MaxLength(500)]
        public string Note { get; set; } = string.Empty;
    }
}
