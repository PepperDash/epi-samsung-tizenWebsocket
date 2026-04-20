using System.Collections.Generic;
using Newtonsoft.Json;
using PepperDash.Core;
using PepperDash.Essentials.Core;

namespace PepperDash.Essentials.Plugin
{
	/// <summary>
	/// Configuration properties for the Samsung Tizen WebSocket display plugin.
	/// </summary>
	[ConfigSnippet("\"properties\":{\"control\":{\"method\":\"https\",\"tcpSshProperties\":{\"address\":\"192.168.1.100\",\"port\":8002}},\"pollIntervalMs\":5000}")]
	public class SamsungTizenWebsocketConfig
	{
		/// <summary>Preferred Essentials control configuration object.</summary>
		[JsonProperty("control")]
		public EssentialsControlPropertiesConfig Control { get; set; }

		/// <summary>IP address or hostname of the Samsung display.</summary>
		[JsonProperty("address", NullValueHandling = NullValueHandling.Ignore)]
		public string Address { get; set; }

		/// <summary>Legacy Samsung Tizen WebSocket port fallback (default: 8002).</summary>
		[JsonProperty("port", NullValueHandling = NullValueHandling.Ignore)]
		public int? Port { get; set; }

		/// <summary>How often to poll device status in milliseconds (default: 5000).</summary>
		[JsonProperty("pollIntervalMs")]
		public long PollIntervalMs { get; set; } = 5000;

		/// <summary>Warn if no poll response within this many milliseconds (default: 60000).</summary>
		[JsonProperty("warningTimeoutMs")]
		public long WarningTimeoutMs { get; set; } = 60000;

		/// <summary>Error if no poll response within this many milliseconds (default: 120000).</summary>
		[JsonProperty("errorTimeoutMs")]
		public long ErrorTimeoutMs { get; set; } = 120000;

		/// <summary>Display cooldown timer in milliseconds (default: 8000).</summary>
		[JsonProperty("coolingTimeMs")]
		public uint CoolingTimeMs { get; set; } = 8000;

		/// <summary>Display warmup timer in milliseconds (default: 10000).</summary>
		[JsonProperty("warmingTimeMs")]
		public uint WarmingTimeMs { get; set; } = 10000;

		/// <summary>UPnP RenderingControl service port for direct volume/mute control (default: 9197).</summary>
		[JsonProperty("upnpPort")]
		public int UpnpPort { get; set; } = 9197;

		/// <summary>Optional friendly names and visibility per source key.</summary>
		[JsonProperty("friendlyNames")]
		public List<SamsungInputFriendlyName> FriendlyNames { get; set; }

		public SamsungTizenWebsocketConfig()
		{
			FriendlyNames = new List<SamsungInputFriendlyName>();
		}

		public string GetAddress()
		{
			return !string.IsNullOrEmpty(Control?.TcpSshProperties?.Address)
				? Control.TcpSshProperties.Address
				: Address;
		}

		public int GetPort()
		{
			var controlPort = Control?.TcpSshProperties?.Port ?? 0;
			if (controlPort > 0)
				return controlPort;

			if (Port.HasValue && Port.Value > 0)
				return Port.Value;

			return 8002;
		}

		public eControlMethod GetControlMethod()
		{
			return Control?.Method ?? eControlMethod.None;
		}

		public bool UseSecureWebSocket()
		{
			switch (GetControlMethod())
			{
				case eControlMethod.Http:
				case eControlMethod.Ws:
					return false;
				case eControlMethod.Https:
				case eControlMethod.Wss:
				case eControlMethod.None:
				default:
					return true;
			}
		}
	}

	/// <summary>
	/// Optional friendly-name mapping for input sources.
	/// </summary>
	public class SamsungInputFriendlyName
	{
		[JsonProperty("inputKey")]
		public string InputKey { get; set; }

		[JsonProperty("name")]
		public string Name { get; set; }

		[JsonProperty("hideInput")]
		public bool HideInput { get; set; }
	}
}
