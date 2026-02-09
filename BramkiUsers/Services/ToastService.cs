namespace BramkiUsers.Services
{
    public interface IToastService
    {
        /// <summary>
        /// Raised when a toast should be shown.
        /// args: (message, variant, autoHideMs)
        /// </summary>
        event Action<string, string, int>? OnShow;

        /// <summary>
        /// Show a toast.
        /// variant: "info" | "success" | "warn" | "error"
        /// autoHideMs: 0 to keep it open until closed
        /// </summary>
        void Show(string message, string variant = "info", int autoHideMs = 0);
    }
    public sealed class ToastService : IToastService
    {
        public event Action<string, string, int>? OnShow;

        public void Show(string message, string variant = "info", int autoHideMs = 0)
            => OnShow?.Invoke(message, variant, autoHideMs);
    }
}
