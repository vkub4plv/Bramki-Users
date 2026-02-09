namespace BramkiUsers.Wcf
{
    public sealed class VisoOptions
    {
        public string BaseUrl { get; set; } = "";
        public int SendTimeoutSeconds { get; set; } = 15;
        public int ReceiveTimeoutSeconds { get; set; } = 15;
        public int OperationTimeoutSeconds { get; set; } = 20;
        public int KeepAliveMinutes { get; set; } = 5;
    }

    public sealed class VisoServiceAccountOptions
    {
        public string Login { get; set; } = "";
        public string Password { get; set; } = "";
    }
}