using System;
using System.Globalization;
using System.Text;
using Karambolo.Extensions.Logging.File;

namespace BramkiUsers.Infrastructure
{
    // Custom formatter for file log lines
    internal sealed class SimpleLogEntryTextBuilder : FileLogEntryTextBuilder
    {
        public static readonly SimpleLogEntryTextBuilder Default = new();

        /// <summary>
        /// Append timestamp in format: " @ 2025-12-04 03:15:51"
        /// (no milliseconds, no UTC suffix)
        /// </summary>
        protected override void AppendTimestamp(StringBuilder sb, DateTimeOffset timestamp)
        {
            sb.Append(" @ ")
              .Append(timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }

        protected override void AppendLogScopeInfo(StringBuilder sb, IExternalScopeProvider scopeProvider)
        {
            scopeProvider.ForEachScope((scope, builder) =>
            {
                builder.Append(' ');

                AppendLogScope(builder, scope);
            }, sb);
        }

        protected override void AppendMessage(StringBuilder sb, string message)
        {
            sb.Append(" => \n");
            sb.AppendLine(message);
        }
    }
}