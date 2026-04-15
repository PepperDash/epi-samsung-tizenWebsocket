using System.Collections.Generic;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Config;

namespace PepperDash.Essentials.Plugin
{
	/// <summary>
	/// Plugin device factory for Samsung Tizen WebSocket display devices.
	/// </summary>
	public class SamsungTizenWebsocketDeviceFactory : EssentialsPluginDeviceFactory<SamsungTizenWebsocketController>
	{
		/// <summary>
		/// Plugin device factory constructor
		/// </summary>
		public SamsungTizenWebsocketDeviceFactory()
		{
			MinimumEssentialsFrameworkVersion = "2.24.0";

			TypeNames = new List<string>() { "samsungTizenWebsocket" };
		}

		/// <summary>
		/// Builds and returns an instance of SamsungTizenWebsocketController.
		/// </summary>
		/// <param name="dc">device configuration</param>
		public override EssentialsDevice BuildDevice(DeviceConfig dc)
		{
			Debug.LogVerbose("[{key}] Factory attempting to create new device from type: {type}", dc.Key, dc.Type);

			var config = dc.Properties.ToObject<SamsungTizenWebsocketConfig>();
			if (config == null)
			{
				Debug.LogError("[{key}] Factory: failed to read properties config for {name}", dc.Key, dc.Name);
				return null;
			}

			var controlMethod = config.GetControlMethod();
			switch (controlMethod)
			{
				case eControlMethod.None:
				case eControlMethod.Http:
				case eControlMethod.Https:
				case eControlMethod.Ws:
				case eControlMethod.Wss:
					break;
				default:
					Debug.LogError("[{key}] Factory: control method '{method}' is not supported. Use http, https, ws, or wss", dc.Key, controlMethod);
					return null;
			}

			if (string.IsNullOrEmpty(config.GetAddress()))
			{
				Debug.LogError("[{key}] Factory: 'properties.control.tcpSshProperties.address' is required, or use legacy 'properties.address'", dc.Key);
				return null;
			}

			return new SamsungTizenWebsocketController(dc.Key, dc.Name, config);
		}
	}
}
