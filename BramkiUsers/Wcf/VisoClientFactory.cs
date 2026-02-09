using System;
using System.ServiceModel;
using System.Xml;
using Microsoft.Extensions.Options;
using BramkiUsers.SessionManagement;
using BramkiUsers.ConfigurationQuery;
using BramkiUsers.SystemSynchronization;
using BramkiUsers.Communication;

namespace BramkiUsers.Wcf
{
    public sealed class VisoClientFactory
    {
        private readonly VisoOptions _opt;
        private readonly BasicHttpBinding _short;

        public VisoClientFactory(IOptions<VisoOptions> opt)
        {
            _opt = opt.Value;
            _short = new BasicHttpBinding
            {
                MaxBufferSize = int.MaxValue,
                MaxReceivedMessageSize = int.MaxValue,
                ReaderQuotas = XmlDictionaryReaderQuotas.Max,
                AllowCookies = true,
                SendTimeout = TimeSpan.FromSeconds(_opt.SendTimeoutSeconds),
                ReceiveTimeout = TimeSpan.FromSeconds(_opt.ReceiveTimeoutSeconds),
                OpenTimeout = TimeSpan.FromSeconds(30),
                CloseTimeout = TimeSpan.FromSeconds(30)
            };
        }

        private string U(string tail) => _opt.BaseUrl.TrimEnd('/') + "/" + tail.TrimStart('/');

        public SessionManagementServiceClient CreateSession()
            => new(_short, new EndpointAddress(U("SessionManagement")));
        public ConfigurationQueryServiceClient CreateConfig()
            => new(_short, new EndpointAddress(U("ConfigurationQuery")));

        public SystemSynchronizationServiceClient CreateSync()
        {
            var c = new SystemSynchronizationServiceClient(_short, new EndpointAddress(U("SystemSynchronization")));
            c.InnerChannel.OperationTimeout = TimeSpan.FromSeconds(_opt.OperationTimeoutSeconds);
            return c;
        }

        public CommunicationServiceClient CreateComm()
        {
            var c = new CommunicationServiceClient(_short, new EndpointAddress(U("Communication")));
            c.InnerChannel.OperationTimeout = TimeSpan.FromSeconds(_opt.OperationTimeoutSeconds);
            return c;
        }
    }
}