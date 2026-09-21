using System;
using System.Threading.Tasks;

namespace PepperDash.Essentials.Plugins.Samsung.TizenWebsocket.Protocol
{
    /// <summary>
    /// Handles Samsung WebSocket authentication flow.
    /// Samsung QN990 displays may require token-based authentication on first connection.
    /// </summary>
    public class SamsungTizenWebsocketAuthentication
    {
        private readonly SamsungTizenWebsocketProtocolBridge bridge;
        private readonly string key;

        public string SessionToken { get; private set; }
        public bool IsAuthenticated { get; private set; }
        public DateTime TokenExpiry { get; private set; }

        public SamsungTizenWebsocketAuthentication(string key, SamsungTizenWebsocketProtocolBridge bridge)
        {
            this.key = key ?? throw new ArgumentNullException(nameof(key));
            this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        /// <summary>
        /// Marks the session authenticated for samsung.remote.control transport.
        /// Consumer Samsung displays accept remote-key commands without an explicit auth command.
        /// </summary>
        public Task<bool> AuthenticateAsync()
        {
            if (IsAuthenticated && TokenExpiry > DateTime.UtcNow)
            {
                return Task.FromResult(true);
            }

            IsAuthenticated = true;
            TokenExpiry = DateTime.MaxValue;
            return Task.FromResult(true);
        }

        /// <summary>
        /// Refreshes the session token if it's expiring soon.
        /// </summary>
        public Task<bool> RefreshTokenAsync()
        {
            return AuthenticateAsync();
        }

        /// <summary>
        /// Clears authentication state.
        /// </summary>
        public void Clear()
        {
            SessionToken = null;
            IsAuthenticated = false;
            TokenExpiry = DateTime.MinValue;
        }
    }
}
