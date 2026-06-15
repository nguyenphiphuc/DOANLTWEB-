using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace DoAnLtWeb.Models
{
    public class User : IdentityUser<int>
    {
        [JsonIgnore]
        [NotMapped]
        public string Username
        {
            get => UserName ?? string.Empty;
            set => UserName = value;
        }

        public bool IsVip { get; set; } = false;
        public DateTime? VipExpiresAt { get; set; }
        public string VipPlanName { get; set; } = "Free";

        public ICollection<PaymentTransaction> PaymentTransactions { get; set; } = new List<PaymentTransaction>();
    }
}
