namespace Subscription_Backend.Models
{
    public class Subscription
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Plan { get; set; } = null!;
        public bool IsActive { get; set; }
    }
}
